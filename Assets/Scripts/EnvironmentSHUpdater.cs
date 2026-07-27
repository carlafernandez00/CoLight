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

    public enum ProjectionMethod
    {
        FullScan,            // Deterministic quadrature over every texel (flicker-free, cost scales with resolution)
        ImportanceSampling   // Monte Carlo sampling of a luminance distribution (cost fixed by sample count)
    }

    [Header("Projection method")]
    [Tooltip("FullScan visits every texel. ImportanceSampling draws numSamples from a luminance-weighted distribution.")]
    public ProjectionMethod method = ProjectionMethod.FullScan;

    [Range(64, 16384), Tooltip("Monte Carlo samples per probe (ImportanceSampling only).")]
    public int numSamples = 2048;

    [Header("Debug diffuse map (ImportanceSampling only)")]
    [Tooltip("Save the ambient probe's reconstructed SH to a PNG in Assets/Debug (sized to the env map).")]
    public bool debugDiffuseMap = true;

    // Internal
    private ComputeBuffer _shBuffer;
    private float[]       _shRaw;
    private int           _kernelFull;
    private int           _kernelIS;
    private int           _kernelBuildCond;
    private int           _kernelBuildMarg;
    private int           _activeKernel;
    private int           _frameCounter;
    private Texture       _currentEnvTex;
    private LightProbes   _runtimeProbes;
    private RenderTexture _diffuseRenderTexture;          // debug map written by the IS kernel

    // Luminance distribution buffers (importance sampling), sized to the mip dims
    private ComputeBuffer _condCdfBuffer;      // mipH * (mipW + 1) floats
    private ComputeBuffer _marginalCdfBuffer;  // mipH + 1 floats
    private ComputeBuffer _marginalFuncBuffer; // mipH floats
    private int           _distW, _distH;      // dims the distribution buffers were built for

    // Known property names used by Unity's Skybox/Panoramic shader
    private static readonly string[] _skyboxTexProperties = { "_MainTex", "_Tex", "_SkyTex" };

    // Profiling: Stopwatches for measuring GPU dispatch, readback, and total time
    private readonly Stopwatch _swTotal    = new Stopwatch();  // Total time for the entire UpdateSH() process
    private readonly Stopwatch _swDispatch = new Stopwatch();  // Time spent dispatching the compute shader for all probes
    private readonly Stopwatch _swReadback = new Stopwatch();  // Time spent reading back the results
    private readonly Stopwatch _swBuild    = new Stopwatch();  // Time spent building the luminance distribution (Importance Sampling only)
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
        _kernelFull      = computeShader.FindKernel("ProjectEquirectToSH");
        _kernelIS        = computeShader.FindKernel("ProjectEquirectToSH_IS");
        _kernelBuildCond = computeShader.FindKernel("BuildConditionalCDF");
        _kernelBuildMarg = computeShader.FindKernel("BuildMarginalCDF");
        EnsureBuffer(1); // at minimum one slot for the ambient probe

        // Profiling log setup
        _profileLogPath = Path.Combine(Application.dataPath, "Debug", "SHProfiler.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(_profileLogPath));
        File.WriteAllText(_profileLogPath, "frame,method,probeCount,samples,build_s,dispatch_s,readback_s,total_s\n");
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

        _condCdfBuffer?.Release();      _condCdfBuffer      = null;
        _marginalCdfBuffer?.Release();  _marginalCdfBuffer  = null;
        _marginalFuncBuffer?.Release(); _marginalFuncBuffer = null;
        _distW = _distH = 0;

        if (_diffuseRenderTexture != null) { _diffuseRenderTexture.Release(); _diffuseRenderTexture = null; }
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

        // Select the kernel for this update
        _activeKernel = (method == ProjectionMethod.ImportanceSampling) ? _kernelIS : _kernelFull;

        // 1. Global constants (SetInt/SetFloat are shared across all kernels)
        computeShader.SetInt   ("_TexWidth",        _currentEnvTex.width);
        computeShader.SetInt   ("_TexHeight",       _currentEnvTex.height);
        computeShader.SetFloat ("_EnvSphereRadius", envSphereRadius);
        computeShader.SetInt   ("_MipLevel",        mipLevel);
        computeShader.SetInt   ("_NumSamples",      numSamples);

        // 1a. Importance sampling: build the luminance distribution ONCE.
        // It depends only on the env map, so all probes reuse it.
        _swBuild.Reset();
        if (method == ProjectionMethod.ImportanceSampling)
        {
            int mipW = Mathf.Max(1, _currentEnvTex.width  >> mipLevel);
            int mipH = Mathf.Max(1, _currentEnvTex.height >> mipLevel);
            EnsureDistributionBuffers(mipW, mipH);

            _swBuild.Start();
            BuildLuminanceDistribution(mipW, mipH);
            _swBuild.Stop();

            // Bind the distribution buffers to the sampling kernel
            computeShader.SetBuffer(_kernelIS, "_CondCdf",     _condCdfBuffer);
            computeShader.SetBuffer(_kernelIS, "_MarginalCdf", _marginalCdfBuffer);

            // Bind the debug map, sized to match the env map.
            int mapW = debugDiffuseMap ? _currentEnvTex.width  : 1;
            int mapH = debugDiffuseMap ? _currentEnvTex.height : 1;
            EnsureDiffuseRenderTexture(mapW, mapH);
            computeShader.SetTexture(_kernelIS, "_DiffuseMap", _diffuseRenderTexture);
            computeShader.SetInt("_OutWidth",  debugDiffuseMap ? mapW : 0);
            computeShader.SetInt("_OutHeight", debugDiffuseMap ? mapH : 0);
        }

        // 1b. Bind common resources to the active kernel
        computeShader.SetTexture(_activeKernel, "_EquirectMap", _currentEnvTex);
        computeShader.SetBuffer (_activeKernel, "_SHCoeffs",    _shBuffer);

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

        _swTotal.Stop();

        // 3. Wait one frame for GPU work to complete
        yield return null;

        _swTotal.Start();

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
        double buildS    = _swBuild.Elapsed.TotalSeconds;
        double dispatchS = _swDispatch.Elapsed.TotalSeconds;
        double readbackS = _swReadback.Elapsed.TotalSeconds;
        double totalS    = _swTotal.Elapsed.TotalSeconds;
        int    samples   = (method == ProjectionMethod.ImportanceSampling) ? numSamples : 0;

        Debug.Log($"[EnvironmentSHUpdater] SH updated ({method}) — Band0 R={ambientSH[0,0]:F6} G={ambientSH[1,0]:F6} B={ambientSH[2,0]:F6} | {bakedCount} baked probe(s) | " +
                  $"build={buildS:F6}s dispatch={dispatchS:F6}s readback={readbackS:F6}s total={totalS:F6}s");

        File.AppendAllText(_profileLogPath,
            $"{Time.frameCount},{method},{bakedCount + 1},{samples},{buildS:F6},{dispatchS:F6},{readbackS:F6},{totalS:F6}\n");

        // 8. Dump the debug map to disk (IS only — the full-scan kernel doesn't write it)
        if (debugDiffuseMap && method == ProjectionMethod.ImportanceSampling)
            SaveDiffuseMapToDisk();
    }

    private void DispatchForProbe(int probeIndex, Vector3 position)
    {
        computeShader.SetVector("_ProbePosition", position);
        computeShader.SetInt   ("_ProbeIndex",    probeIndex);
        computeShader.Dispatch (_activeKernel, 1, 1, 1);
    }

    // Creates the debug RenderTexture only when the requested size changes.
    private void EnsureDiffuseRenderTexture(int w, int h)
    {
        if (_diffuseRenderTexture != null && _diffuseRenderTexture.width == w && _diffuseRenderTexture.height == h) return;

        // cleanup old texture if it exists
        if (_diffuseRenderTexture != null) _diffuseRenderTexture.Release();
        _diffuseRenderTexture = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf)
        {
            enableRandomWrite = true,
            name = "SHProbeDebugMap"
        };
        _diffuseRenderTexture.Create();
    }

    // Reads the debug map back from the GPU and writes it to Assets/Debug as a PNG.
    // Overwrites the same file each update so the latest map is always on disk.
    // PNG is 8-bit, so HDR values above 1 clamp — fine for a quick visual check.
    private void SaveDiffuseMapToDisk()
    {
        if (_diffuseRenderTexture == null) return;

        var prevActive = RenderTexture.active;
        RenderTexture.active = _diffuseRenderTexture;

        var tex = new Texture2D(_diffuseRenderTexture.width, _diffuseRenderTexture.height, TextureFormat.RGBAFloat, false);
        tex.ReadPixels(new Rect(0, 0, _diffuseRenderTexture.width, _diffuseRenderTexture.height), 0, 0);
        tex.Apply();

        RenderTexture.active = prevActive;

        byte[] png = tex.EncodeToPNG();
        Destroy(tex);

        string path = Path.Combine(Application.dataPath, "Debug", "SHProbeDebugMap.png");
        File.WriteAllBytes(path, png);
        Debug.Log($"[EnvironmentSHUpdater] Debug map saved: {path}");
    }

    // Builds the PBRT-style Distribution2D (luminance × sinθ) for the current
    // env map at the active mip level. Two passes: per-row conditional CDFs
    // (one thread per row), then the marginal CDF (single thread). Shared by
    // every probe in this update.
    private void BuildLuminanceDistribution(int mipW, int mipH)
    {
        // Pass 1 — conditional CDF along u, one thread per row
        computeShader.SetTexture(_kernelBuildCond, "_EquirectMap",  _currentEnvTex);
        computeShader.SetBuffer (_kernelBuildCond, "_CondCdf",      _condCdfBuffer);
        computeShader.SetBuffer (_kernelBuildCond, "_MarginalFunc", _marginalFuncBuffer);
        int groups = Mathf.CeilToInt(mipH / 64f); // kernel uses [numthreads(64,1,1)]
        computeShader.Dispatch(_kernelBuildCond, groups, 1, 1);

        // Pass 2 — marginal CDF along v, single thread
        computeShader.SetBuffer(_kernelBuildMarg, "_MarginalFunc", _marginalFuncBuffer);
        computeShader.SetBuffer(_kernelBuildMarg, "_MarginalCdf",  _marginalCdfBuffer);
        computeShader.Dispatch(_kernelBuildMarg, 1, 1, 1);
    }

    // Sizes the three CDF buffers to the mip dimensions and only rebuilds when they change.
    private void EnsureDistributionBuffers(int mipW, int mipH)
    {
        if (_condCdfBuffer != null && _distW == mipW && _distH == mipH) return;

        _condCdfBuffer?.Release();
        _marginalCdfBuffer?.Release();
        _marginalFuncBuffer?.Release();

        _condCdfBuffer      = new ComputeBuffer(mipH * (mipW + 1), sizeof(float)); // per-row CDF, W+1 entries each
        _marginalCdfBuffer  = new ComputeBuffer(mipH + 1,          sizeof(float)); // CDF over rows
        _marginalFuncBuffer = new ComputeBuffer(mipH,              sizeof(float)); // per-row integrals
        _distW = mipW;
        _distH = mipH;
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
