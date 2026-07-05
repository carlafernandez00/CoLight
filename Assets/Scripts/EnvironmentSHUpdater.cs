using System.Collections;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

/// <summary>
/// Projects the scene's Skybox/Panoramic environment map to L2 Spherical Harmonics
/// with per-probe parallax correction: each probe's world-space position shifts which
/// directions of the env map carry more solid angle weight, giving spatially-varying
/// lighting from a single environment capture.
/// </summary>
public class EnvironmentSHUpdater : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Assign EnvironmentToSH.compute")]
    public ComputeShader computeShader;

    [Header("Settings")]
    [Tooltip("Update automatically every N frames. 0 = manual only.")]
    public int updateEveryNFrames = 30;  // if 0, only update when UpdateSH() is called manually

    [Tooltip("Radius of the virtual environment sphere in world units. " +
             "Should encompass the scene but stay small enough that probe offsets " +
             "produce a visible parallax effect. Typical indoor range: 5–20.")]
    public float envSphereRadius = 10f;

    [Range(0f, 4f), Tooltip("Scale the computed SH before applying to the ambient probe.")]
    public float intensityMultiplier = 1f;

    [Range(0, 6), Tooltip("Mip level of the environment texture to sample. 0 = full res, 1 = half, 2 = quarter, etc.")]
    public int mipLevel = 0;

    // Internal
    private ComputeBuffer _shBuffer;
    private float[]       _shRaw;
    private int           _kernel;
    private int           _frameCounter;
    private Texture       _currentEnvTex;
    private LightProbes   _runtimeProbes;  

    // Known property names used by Unity's Skybox/Panoramic shader
    private static readonly string[] _skyboxTexProperties = { "_MainTex", "_Tex", "_SkyTex" };

    // Profiling: Stopwatches for measuring GPU dispatch, readback, and total time
    private readonly Stopwatch _swTotal    = new Stopwatch();  // Total time for the entire UpdateSH() process
    private readonly Stopwatch _swDispatch = new Stopwatch();  // Time spent dispatching the compute shader for all probes
    private readonly Stopwatch _swReadback = new Stopwatch();  // Time spent reading back the results
    private string _profileLogPath;

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    void Awake()
    {
        // Disable reflection probes for now
        QualitySettings.realtimeReflectionProbes = false;
        RenderSettings.reflectionIntensity = 0f;
        
        // Custom mode tells Unity to use ambientProbe as-is, without overwriting it from the skybox.
        RenderSettings.ambientMode = AmbientMode.Custom;
    }

    void OnEnable()
    {
        _kernel = computeShader.FindKernel("ProjectEquirectToSH");
        EnsureBuffer(1); // at minimum one slot for the ambient probe

        // Profiling log setup
        _profileLogPath = Path.Combine(Application.dataPath, "Debug", "SHProfiler.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(_profileLogPath));
        File.WriteAllText(_profileLogPath, "frame,probeCount,dispatch_s,readback_s,total_s\n");
        Debug.Log($"[EnvironmentSHUpdater] Profiling log: {_profileLogPath}");

        // Create a detached LightProbes clone and make it the active probe set.
        // This must happen before UpdateSH() so all writes go to the owned copy.
        InitRuntimeProbes();

        TryFetchSkyboxTexture();
        if (_currentEnvTex != null)
            UpdateSH();
    }

    void OnDisable()
    {
        _shBuffer?.Release();
        _shBuffer = null;
    }

    void Update()
    {
        if (updateEveryNFrames <= 0) return;
        _frameCounter++;
        if (_frameCounter >= updateEveryNFrames)
        {
            _frameCounter = 0;
            // Re-fetch in case the skybox material/texture changed at runtime
            TryFetchSkyboxTexture();

            if (_currentEnvTex != null)
                UpdateSH();
            else
                Debug.LogWarning("[EnvironmentSHUpdater] No equirect texture found in skybox material.");
        }
        
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Call this manually whenever the environment map changes
    /// (e.g. after your lighting estimation pipeline updates the skybox).
    /// </summary>
    public void UpdateSH()
    {
        if (_currentEnvTex == null)
        {
            TryFetchSkyboxTexture();
            if (_currentEnvTex == null)
            {
                Debug.LogWarning("[EnvironmentSHUpdater] Cannot update SH: no texture available.");
                return;
            }
        }
        StartCoroutine(ProjectAndApply());
    }

    /// <summary>
    /// Call this if you update the skybox texture externally at runtime
    /// and want to force a refresh without waiting for the next frame interval.
    /// </summary>
    public void SetEnvironmentTexture(Texture tex)
    {
        _currentEnvTex = tex;
        UpdateSH();
    }

    // -----------------------------------------------------------------------
    // Skybox texture fetch
    // -----------------------------------------------------------------------

    private void TryFetchSkyboxTexture()
    {
        Material skyMat = RenderSettings.skybox;
        if (skyMat == null)
        {
            Debug.LogWarning("[EnvironmentSHUpdater] RenderSettings.skybox is null.");
            _currentEnvTex = null;
            return;
        }

        foreach (string prop in _skyboxTexProperties)
        {
            if (skyMat.HasProperty(prop))
            {
                Texture tex = skyMat.GetTexture(prop);
                if (tex != null) 
                { 
                    _currentEnvTex = tex; 
                    return; 
                }
            }
        }
        // Fallback: log all texture properties to help debug unknown shaders
        Debug.LogWarning($"[EnvironmentSHUpdater] Could not find texture in skybox material '{skyMat.name}'. " +
                         $"Shader: '{skyMat.shader.name}'. " +
                         $"Try adding the property name to _skyboxTexProperties.");
        _currentEnvTex = null;
    }

    // -----------------------------------------------------------------------
    // Runtime probe initialisation
    // -----------------------------------------------------------------------

    // Creates a detached LightProbes clone from the scene's baked data and
    // installs it as LightmapSettings.lightProbes. All subsequent bakedProbes
    // writes target this owned object, bypassing Unity's asset-backed guard.
    private void InitRuntimeProbes()
    {
        // Try to get the detached LightProbes instance for this scene, or fallback to the default asset.
        LightProbes source = LightProbes.GetInstantiatedLightProbesForScene(gameObject.scene)
                             ?? LightmapSettings.lightProbes;

        if (source == null)
        {
            Debug.LogWarning("[EnvironmentSHUpdater] No LightProbes in scene — baked probe illumination will not update.");
            return;
        }

        _runtimeProbes = Object.Instantiate(source); // Detached copy to allow runtime writes to bakedProbes

        var baked = _runtimeProbes.bakedProbes;
        for (int i = 0; i < baked.Length; i++) baked[i] = new SphericalHarmonicsL2();
        _runtimeProbes.bakedProbes = baked;

        LightmapSettings.lightProbes = _runtimeProbes;
    }

    // -----------------------------------------------------------------------
    // Core pipeline
    // -----------------------------------------------------------------------

    private IEnumerator ProjectAndApply()
    {
        if (_runtimeProbes == null)
        {
            Debug.LogError("[EnvironmentSHUpdater] _runtimeProbes is null — InitRuntimeProbes() may have found no LightProbes in scene.");
            yield break;
        }

        // Get baked probes and positions 
        SphericalHarmonicsL2[] bakedProbes = _runtimeProbes.bakedProbes;
        Vector3[]              positions   = _runtimeProbes.positions;
        int bakedCount = bakedProbes != null ? bakedProbes.Length : 0;

        // Ensure we have enough space in our buffers 
        // bakedCount + 1 : slot 0 reserved for ambient probe
        EnsureBuffer(bakedCount + 1);

        _swTotal.Restart(); // reset + start

        // 1. Dispatch compute shader for each probe (ambient + baked)
        computeShader.SetTexture(_kernel, "_EquirectMap",    _currentEnvTex);
        computeShader.SetBuffer (_kernel, "_SHCoeffs",       _shBuffer);
        computeShader.SetInt    ("_TexWidth",                _currentEnvTex.width);
        computeShader.SetInt    ("_TexHeight",               _currentEnvTex.height);
        computeShader.SetFloat  ("_EnvSphereRadius",         envSphereRadius);
        computeShader.SetInt    ("_MipLevel",                mipLevel);

        // 2. Dispatch probes and set their world-space positions for parallax correction
        // Dispatch for ambient probe (slot 0) — always computed from world origin
        _swDispatch.Restart();
        DispatchForProbe(0, Vector3.zero);

        // Dispatch once per baked probe; fall back to origin if positions are unavailable
        for (int i = 0; i < bakedCount; i++)
        {
            Vector3 pos = (positions != null && i < positions.Length) ? positions[i] : Vector3.zero;
            DispatchForProbe(i + 1, pos);
        }
        _swDispatch.Stop();

        // _swTotal.Stop();

        // 3. Wait one frame for GPU work to complete
        // yield return null;

        // _swTotal.Start();

        // 4. Readback all SH data GPU → CPU
        _swReadback.Restart();
        _shBuffer.GetData(_shRaw);
        _swReadback.Stop();

        // 5. Build SphericalHarmonicsL2 and apply to ambient probe
        // Apply to global ambient probe (affects all dynamic objects) 
        var ambientSH = BuildSHL2(_shRaw, 0);
        RenderSettings.ambientProbe = ambientSH * intensityMultiplier;

        // 6. Build SphericalHarmonicsL2 and apply to baked probes
        if (bakedCount > 0)
        {
            for (int i = 0; i < bakedCount; i++)
                bakedProbes[i] = BuildSHL2(_shRaw, i + 1) * intensityMultiplier;
            _runtimeProbes.bakedProbes = bakedProbes;
        }

        _swTotal.Stop();

        // 7. Log profiling results
        double dispatchS = _swDispatch.Elapsed.TotalSeconds;
        double readbackS = _swReadback.Elapsed.TotalSeconds;
        double totalS    = _swTotal.Elapsed.TotalSeconds;

        Debug.Log($"[EnvironmentSHUpdater] SH updated — Band0 R={ambientSH[0,0]:F6} G={ambientSH[1,0]:F6} B={ambientSH[2,0]:F6} | {bakedCount} baked probe(s) | " +
                  $"dispatch={dispatchS:F6}s readback={readbackS:F6}s total={totalS:F6}s");

        File.AppendAllText(_profileLogPath,
            $"{Time.frameCount},{bakedCount + 1},{dispatchS:F6},{readbackS:F6},{totalS:F6}\n");
    }

    private void DispatchForProbe(int probeIndex, Vector3 position)
    {
        computeShader.SetVector("_ProbePosition", position);
        computeShader.SetInt   ("_ProbeIndex",    probeIndex);
        computeShader.Dispatch (_kernel, 1, 1, 1);
    }

    // -----------------------------------------------------------------------
    // Buffer management
    // -----------------------------------------------------------------------

    // Recreates the buffer only when the required probe count changes.
    // Each probe needs 9 float3 SH coefficients → 9 * sizeof(float3) bytes per probe.
    private void EnsureBuffer(int probeCount)
    {
        int elementCount = probeCount * 9;
        if (_shBuffer != null && _shBuffer.count == elementCount) return;

        _shBuffer?.Release();
        _shBuffer = new ComputeBuffer(elementCount, sizeof(float) * 3);
        _shRaw    = new float[elementCount * 3]; // elementCount float3s → elementCount*3 floats
    }


    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts one probe's 27-float slice from the GPU readback into a SphericalHarmonicsL2.
    /// Buffer layout per probe: [c0.r, c0.g, c0.b, c1.r, c1.g, c1.b, ..., c8.r, c8.g, c8.b]
    /// Unity indexing: sh[channel, coefficient]  (0=R, 1=G, 2=B)
    /// </summary>
    private static SphericalHarmonicsL2 BuildSHL2(float[] raw, int probeIndex)
    {
        var sh = new SphericalHarmonicsL2();
        sh.Clear();
        int offset = probeIndex * 9 * 3;
        for (int coeff = 0; coeff < 9; coeff++)
        {
            sh[0, coeff] = raw[offset + coeff * 3 + 0]; // R
            sh[1, coeff] = raw[offset + coeff * 3 + 1]; // G
            sh[2, coeff] = raw[offset + coeff * 3 + 2]; // B

            // sh[0, coeff] = 0; // R
            // sh[1, coeff] = 0; // G
            // sh[2, coeff] = 0; // B
        }
        return sh;
    }
}
