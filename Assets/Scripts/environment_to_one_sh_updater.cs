using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
/// <summary>
/// Projects the scene's Skybox/Panoramic environment map to L2 Spherical Harmonics
/// and writes the result to RenderSettings.ambientProbe + LightmapSettings.lightProbes.bakedProbes.
/// </summary>
public class EnvironmentSHUpdaterv1 : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Assign EquirectToSH.compute")]
    public ComputeShader computeShader;
    [Header("Settings")]
    [Tooltip("Update automatically every N frames. 0 = manual only.")]
    public int updateEveryNFrames = 30;
    // Internal
    private ComputeBuffer        _shBuffer;
    private float[]              _shRaw;       // 9 × float3 = 27 floats
    private int                  _kernel;
    private int                  _frameCounter;
    private Texture              _currentEnvTex;
    // Known property names used by Unity's Skybox/Panoramic shader
    private static readonly string[] _skyboxTexProperties = { "_MainTex", "_Tex", "_SkyTex" };
    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------
    void OnEnable()
    {
        _kernel   = computeShader.FindKernel("ProjectEquirectToSH");
        _shBuffer = new ComputeBuffer(9, sizeof(float) * 3);
        _shRaw    = new float[27];
        // First update immediately on enable
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
        // Try known property names
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
    // Core pipeline
    // -----------------------------------------------------------------------
    private IEnumerator ProjectAndApply()
    {
        // 1. Dispatch compute shader
        computeShader.SetTexture(_kernel, "_EquirectMap", _currentEnvTex);
        computeShader.SetBuffer (_kernel, "_SHCoeffs",    _shBuffer);
        computeShader.SetInt    ("_TexWidth",  _currentEnvTex.width);
        computeShader.SetInt    ("_TexHeight", _currentEnvTex.height);
        computeShader.Dispatch  (_kernel, 1, 1, 1);
        // 2. Wait one frame for GPU work to complete
        yield return null;
        // 3. Readback 27 floats GPU → CPU
        _shBuffer.GetData(_shRaw);
        // 4. Build SphericalHarmonicsL2
        SphericalHarmonicsL2 sh = BuildSHL2(_shRaw);
        // 5. Apply to global ambient probe (affects all dynamic objects)
        RenderSettings.ambientProbe = sh;
        // 6. Apply to baked light probes in the scene
        ApplyToLightProbes(sh);
    }
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    /// <summary>
    /// Converts the 27-float GPU readback into Unity's SphericalHarmonicsL2.
    /// Buffer layout: [c0.r, c0.g, c0.b,  c1.r, c1.g, c1.b, ... c8.r, c8.g, c8.b]
    /// Unity indexing: sh[channel, coefficient]  (0=R, 1=G, 2=B)
    /// </summary>
    private static SphericalHarmonicsL2 BuildSHL2(float[] raw)
    {
        var sh = new SphericalHarmonicsL2();
        sh.Clear();
        for (int coeff = 0; coeff < 9; coeff++)
        {
            sh[0, coeff] = raw[coeff * 3 + 0]; // R
            sh[1, coeff] = raw[coeff * 3 + 1]; // G
            sh[2, coeff] = raw[coeff * 3 + 2]; // B
        }
        return sh;
    }
    /// <summary>
    /// Writes the same SH to every probe in the scene.
    /// Since CoLight estimates a single global environment, one SH for all probes is correct.
    /// </summary>
    private static void ApplyToLightProbes(SphericalHarmonicsL2 sh)
    {
        LightProbes probes = LightmapSettings.lightProbes;
        if (probes == null || probes.count == 0)
        {
            // No baked probes in scene — ambientProbe alone is sufficient
            return;
        }
        SphericalHarmonicsL2[] bakedProbes = probes.bakedProbes;
        for (int i = 0; i < bakedProbes.Length; i++)
            bakedProbes[i] = sh;
        probes.bakedProbes = bakedProbes; // write back triggers GPU upload
    }
}