using System.Collections;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using System;                 
using System.Text;            
using System.Globalization;   
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

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

    [Tooltip("Where the environment comes from.")]
    public EnvironmentSource environmentSource = EnvironmentSource.Reconstructed;

    [Tooltip("If assigned, SH projection uses the reconstructed panorama instead of the skybox.")]
    public EnvironmentMapReconstructor reconstructor;

    [Tooltip("Where the ambient probe is evaluated. Avatar transform here. If null, it uses the panorama center.")]
    public Transform ambientProbeAnchor;

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

    [Tooltip("Uses the depth map for the parallax calculation. Deactivated, comes back to the sphere of radius envSphereRadius.")]
    public bool useDepthMap = true;

    [Range(0.05f, 1f), Tooltip("Minimum distance (m). Prevents geom from exploding when the probe touches a surface.")]
    public float minDepth = 0.15f;

    public enum EnvironmentSource
    {
        Reconstructed,   
        Skybox           
    }

    public enum ProjectionMethod
    {
        FullScan,            // Deterministic quadrature over every texel (flicker-free, cost scales with resolution)
        ImportanceSampling   // Monte Carlo sampling of a luminance distribution (cost fixed by sample count)
    }

    public enum ImportanceFunction
    {
        Luminance,           // Importance = max(dot(color, LUMA), 0) : Rec.709 luminance
        ColorAware,          // Importance = max(max(color.r, color.g), color.b)
        Uniform              // Importance = 1 (no weighting)
    }

    public enum ProbeAwareSampling
    {
        None,                 // shared distribution
        Resampling,           // Shared distribution + resampling
        PerProbeDistribution  // construct one distrib per probe
    }

    [Header("Projection method")]
    [Tooltip("FullScan visits every texel. ImportanceSampling draws numSamples from a luminance-weighted distribution.")]
    public ProjectionMethod method = ProjectionMethod.FullScan;

    [Tooltip("Importance function used to build the sampling distribution (ImportanceSampling only).")]
    public ImportanceFunction importanceFunction = ImportanceFunction.Luminance;

    [Tooltip("How the probe position enters the estimate. ImportanceSampling only.")]
    public ProbeAwareSampling probeAware = ProbeAwareSampling.None;

    [Range(64, 16384), Tooltip("Monte Carlo samples per probe (ImportanceSampling only).")]
    public int numSamples = 2048;

    [Range(1, 24), Tooltip("Candidates resampled per output sample, weighted by the probe's")]
    public int numCandidates = 8;

    [Header("Debug diffuse map")]
    [Tooltip("Save one probe's reconstructed SH to a PNG in Assets/Debug/SHProbe (sized to the env map). Works for both methods.")]
    public bool debugDiffuseMap = true;

    [Tooltip("Which probe to visualize/save. 0 = ambient probe (world origin); 1..N = baked probes.")]
    public int debugProbeIndex = 0;

    [Header("Debug importance sampling")]
    [Tooltip("Save information to visualize the distribution of the importance function.")]
    public bool debugImportanceSampling = false;

    [Tooltip("Write the dump only on the first update, then stop (avoids rewriting the same files every N frames).")]
    public bool dumpOnce = true; 

    [Header("Debug profiling")]
    [Tooltip("Save profiling information into a csv.")]
    public bool debugProfiling = false;

    // Internal
    private ComputeBuffer _shBuffer;
    private float[]       _shRaw;
    private int           _kernelFull;
    private int           _kernelIS;
    private int           _kernelRIS;
    private int           _kernelBuildCond;
    private int           _kernelBuildProbeCond; 
    private int           _kernelBuildMarg;
    private int           _activeKernel;
    private int           _frameCounter;
    private Texture       _currentEnvTex;
    private int           _mip;              // effective mipLevel 
    private LightProbes   _runtimeProbes;
    private RenderTexture _diffuseRenderTexture;          // debug map written by the active kernel
    private Vector3       _debugProbePos;                 // world position of the probe being debugged (for the filename)

    // Luminance distribution buffers (importance sampling), sized to the mip dims
    private ComputeBuffer _condCdfBuffer;      // mipH * (mipW + 1) floats
    private ComputeBuffer _marginalCdfBuffer;  // mipH + 1 floats
    private ComputeBuffer _marginalFuncBuffer; // mipH floats
    private int           _distW, _distH, _distributionIndex;  // dims the distribution buffers were built for

    // Debug Importance Sampling
    private ComputeBuffer _sampleCountBuffer;    // mipW * mipH uints, counts how many times each texel was sampled
    private ComputeBuffer _sampleDensityBuffer;  // mipW * mipH floats, the sampled density
    private ComputeBuffer _importanceMapBuffer;  // mipW * mipH floats, stores the importance function value for each texel
    private uint[]        _sampleCountRaw;
    private uint[]        _sampleCountZeros;
    private float[]       _densityRaw;
    private int           _dumpW, _dumpH;
    private bool          _sampleDumpWritten;   


    // Known property names used by Unity's Skybox/Panoramic shader
    private static readonly string[] _skyboxTexProperties = { "_MainTex", "_Tex", "_SkyTex" };

    // Profiling: Stopwatches for measuring GPU dispatch, readback, and total time
    private readonly Stopwatch _swTotal    = new Stopwatch();  // Total time for the entire UpdateSH() process
    private readonly Stopwatch _swDispatch = new Stopwatch();  // Time spent dispatching the compute shader for all probes
    private readonly Stopwatch _swReadback = new Stopwatch();  // Time spent reading back the results
    private readonly Stopwatch _swBuild    = new Stopwatch();  // Time spent building the luminance distribution (Importance Sampling only)
    private string _profileLogPath;
    private bool UsesSampling  => method == ProjectionMethod.ImportanceSampling;
    private bool UsesResampling=> UsesSampling && probeAware == ProbeAwareSampling.Resampling;
    private bool UsesPerProbe  => UsesSampling && probeAware == ProbeAwareSampling.PerProbeDistribution;
    private int EffectiveCandidates => UsesResampling ? Mathf.Clamp(numCandidates, 1, 24) : 1;
    
    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------
    private int SelectKernel()
    {
        if (!UsesSampling)  return _kernelFull;
        if (UsesResampling) return _kernelRIS;
        return _kernelIS;                     // None and PerProbeDistribution share this one
    }

    private string ModeTag()
    {
        if (!UsesSampling) return "FullScan";
        switch (probeAware)
        {
            case ProbeAwareSampling.Resampling:           return "IS-Resampling";
            case ProbeAwareSampling.PerProbeDistribution: return "IS-PerProbe";
            default:                                      return "IS-None";
        }
    }


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
        _kernelFull           = computeShader.FindKernel("ProjectEquirectToSH");
        _kernelIS             = computeShader.FindKernel("ProjectEquirectToSH_IS");
        _kernelRIS            = computeShader.FindKernel("ProjectEquirectToSH_RIS");
        _kernelBuildCond      = computeShader.FindKernel("BuildConditionalCDF");
        _kernelBuildProbeCond = computeShader.FindKernel("BuildProbeConditionalCDF");
        _kernelBuildMarg      = computeShader.FindKernel("BuildMarginalCDF");
        EnsureBuffer(1); // at minimum one slot for the ambient probe

        _sampleDumpWritten = false; 

        // Profiling log setup.
        // Editor: project-relative Debug folder (easy to find in Finder).
        // Device: persistentDataPath is the only writable location on Android/Quest.
        if (debugProfiling)
        {
            #if UNITY_EDITOR
                _profileLogPath = Path.Combine(Application.dataPath, "Debug", "Profiling", "SHProfiler.csv");
            #else
                    _profileLogPath = Path.Combine(Application.persistentDataPath, "Profiling", "SHProfiler.csv");
            #endif
            Directory.CreateDirectory(Path.GetDirectoryName(_profileLogPath));
            File.WriteAllText(_profileLogPath,"frame,source,mode,importance,probeCount,samples,candidates,mipLevel,build_s,dispatch_s,readback_s,total_s\n");
            Debug.Log($"[EnvironmentSHUpdater] Profiling log: {_profileLogPath}");
        }
    
        // Create a detached LightProbes clone and make it the active probe set.
        // This must happen before UpdateSH() so all writes go to the owned copy.
        InitRuntimeProbes();

        AcquireEnvTexture();
        if (_currentEnvTex != null)
            UpdateSH();
    }

    void OnDisable()
    {
        _shBuffer?.Release();
        _shBuffer = null;

        ReleaseDistributionBuffers();

        _sampleCountBuffer?.Release();  _sampleCountBuffer  = null;
        _sampleDensityBuffer?.Release();  _sampleDensityBuffer = null;
        _dumpW = _dumpH = 0;

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
            AcquireEnvTexture();

            if (_currentEnvTex != null)
                UpdateSH();
            else if (environmentSource == EnvironmentSource.Reconstructed && reconstructor == null)
                Debug.LogWarning("[EnvironmentSHUpdater] No reconstructor assigned.");
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
            AcquireEnvTexture();
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

    /// <summary>
    /// Re-arms the sample dump so the next update writes the files again
    /// (useful after switching importanceFunction or numSamples at runtime).
    /// </summary>
    public void ResetSampleDump() => _sampleDumpWritten = false;

    // -----------------------------------------------------------------------
    // reconstructor texture fetch
    // -----------------------------------------------------------------------
    private void AcquireEnvTexture()
    {
        if (environmentSource == EnvironmentSource.Skybox)
        {
            TryFetchSkyboxTexture();
            return;
        }
        _currentEnvTex = (reconstructor != null && reconstructor.IsReady)
            ? reconstructor.ColorEquirect
            : null;
    }

    // Mip requested is only valid if the texture has a mip chain
    private int EffectiveMip()
    {
        var rt = _currentEnvTex as RenderTexture;
        if (rt != null && !rt.useMipMap) return 0;
        return mipLevel;
    }

    // Change amnbient probe position to the one from the avatar. 
    // ambient probe is a fallback probe when the others are not available
    private Vector3 AmbientProbePosition()
    {
        if (ambientProbeAnchor != null) return ambientProbeAnchor.position;
        if (reconstructor != null)      return reconstructor.MapCenter;
        return Vector3.zero;
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
        int probeCount = bakedCount + 1;  // bakedCount + 1 : slot 0 reserved for ambient probe
        

        var probePos = new Vector3[probeCount];
        probePos[0] = AmbientProbePosition();                   // get ambient probe position
        for (int i = 0; i < bakedCount; i++)
            probePos[i + 1] = (positions != null && i < positions.Length) ? positions[i] : Vector3.zero;


        // Ensure we have enough space in our buffers 
        EnsureBuffer(probeCount);

        bool saveSampleDump = debugImportanceSampling && method == ProjectionMethod.ImportanceSampling 
                                                      && !(dumpOnce && _sampleDumpWritten);

        _swTotal.Restart(); // reset + start

        // Select the kernel for this update
        _activeKernel = SelectKernel();

        // 1. Global constants (SetInt/SetFloat are shared across all kernels)
        _mip = EffectiveMip();

        bool useReconstructed = environmentSource == EnvironmentSource.Reconstructed
                                && reconstructor != null;

        bool depthOK = useReconstructed
                       && useDepthMap
                       && reconstructor.DepthEquirect != null;

        computeShader.SetInt   ("_TexWidth",        _currentEnvTex.width);
        computeShader.SetInt   ("_TexHeight",       _currentEnvTex.height);
        computeShader.SetFloat ("_EnvSphereRadius", envSphereRadius);
        computeShader.SetInt   ("_MipLevel",        _mip);
        computeShader.SetInt   ("_NumSamples",      numSamples);
        computeShader.SetInt   ("_NumCandidates",   numCandidates);
        computeShader.SetInt   ("_ImportanceFunction", (int)importanceFunction);
        computeShader.SetInt   ("_DebugProbeIndex", (debugDiffuseMap || saveSampleDump) ? debugProbeIndex : -1);

        computeShader.SetInt   ("_UseDepth",   depthOK ? 1 : 0);
        computeShader.SetFloat ("_MinDepth",   minDepth);
        computeShader.SetVector("_MapCenter",  useReconstructed ? reconstructor.MapCenter : Vector3.zero);

        if (saveSampleDump) computeShader.EnableKeyword("DEBUG_IMPORTANCE_SAMPLING");
        else                computeShader.DisableKeyword("DEBUG_IMPORTANCE_SAMPLING");


        // 1a. Importance sampling: build the luminance distribution ONCE.
        // It depends only on the env map, so all probes reuse it.
        _swBuild.Reset();
        if (UsesSampling)
        {
            int mipW = Mathf.Max(1, _currentEnvTex.width  >> _mip);
            int mipH = Mathf.Max(1, _currentEnvTex.height >> _mip);

            // if per-probe distrobution, one CDF slot per probe, otherwise 1
            EnsureDistributionBuffers(mipW, mipH, UsesPerProbe ? probeCount : 1);
            
            if (saveSampleDump)
            {
                EnsureSampleDumpBuffers(mipW, mipH);
                _sampleCountBuffer.SetData(_sampleCountZeros);     // reset hits per texel
                computeShader.SetBuffer(_activeKernel, "_SampleCount", _sampleCountBuffer);
            } 

            _swBuild.Start();
            BuildDistributions(mipH, probePos, saveSampleDump, depthOK);
            _swBuild.Stop();

            // Bind the distribution buffers to the sampling kernel
            computeShader.SetBuffer(_activeKernel, "_CondCdf",     _condCdfBuffer);
            computeShader.SetBuffer(_activeKernel, "_MarginalCdf", _marginalCdfBuffer);
        }

        // 1b. Bind common resources to the active kernel
        computeShader.SetTexture(_activeKernel, "_EquirectMap", _currentEnvTex);
        computeShader.SetBuffer (_activeKernel, "_SHCoeffs",    _shBuffer);
        computeShader.SetTexture(_activeKernel, "_DepthEquirect", 
                    depthOK ? (Texture)reconstructor.DepthEquirect : Texture2D.blackTexture); // to avoid error, not used anyway

        if (debugDiffuseMap) computeShader.EnableKeyword("DEBUG_DIFFUSE_MAP");
        else                 computeShader.DisableKeyword("DEBUG_DIFFUSE_MAP");

        int mapW = debugDiffuseMap ? _currentEnvTex.width  : 1;
        int mapH = debugDiffuseMap ? _currentEnvTex.height : 1;
        EnsureDiffuseRenderTexture(mapW, mapH);
        computeShader.SetTexture(_activeKernel, "_DiffuseMap", _diffuseRenderTexture);
        computeShader.SetInt("_OutWidth",  debugDiffuseMap ? mapW : 0);
        computeShader.SetInt("_OutHeight", debugDiffuseMap ? mapH : 0);

        // 2. Dispatch probes and set their world-space positions for parallax correction
        _swDispatch.Restart();
        _debugProbePos = Vector3.zero;   // probe 0 (ambient) 
        for (int p = 0; p < probeCount; p++)
        {
            if (p == debugProbeIndex) _debugProbePos = probePos[p];   // remember for the filename
            DispatchForProbe(p, probePos[p], UsesPerProbe ? p : 0);
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
        if (debugProfiling)
        {
            LogProfilingResults(ambientSH, probeCount, bakedCount);
        }

        // 8. Dump the debug map to disk
        if (debugDiffuseMap)
            SaveDiffuseMapToDisk();
        
        // 9. Dump the importance sampling data to disk (texels + hit counts + importance map + env map)
        if (saveSampleDump)
        {
            SaveSampleDumpToDisk();
            _sampleDumpWritten = true;
        }
    }

    private void DispatchForProbe(int probeIndex, Vector3 position, int distSlot)
    {
        computeShader.SetVector("_ProbePosition", position);
        computeShader.SetInt   ("_ProbeIndex",    probeIndex);
        computeShader.SetInt   ("_DistributionIndex", distSlot);
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

    private void LogProfilingResults(SphericalHarmonicsL2 ambientSH, int probeCount, int bakedCount)
    {
        double buildS    = _swBuild.Elapsed.TotalSeconds;
        double dispatchS = _swDispatch.Elapsed.TotalSeconds;
        double readbackS = _swReadback.Elapsed.TotalSeconds;
        double totalS    = _swTotal.Elapsed.TotalSeconds;
        int    samples   = UsesSampling ? numSamples : 0;
        int    cands     = UsesSampling ? EffectiveCandidates : 0;
        string impFunc   = UsesSampling ? importanceFunction.ToString() : "N/A";
        string mode      = ModeTag();

        Debug.Log($"[EnvironmentSHUpdater] SH updated ({mode}/{impFunc}) — " +
                $"Band0 R={ambientSH[0,0]:F6} G={ambientSH[1,0]:F6} B={ambientSH[2,0]:F6} | {bakedCount} baked probe(s) | " +
                $"build={buildS:F6}s dispatch={dispatchS:F6}s readback={readbackS:F6}s total={totalS:F6}s");

        if (string.IsNullOrEmpty(_profileLogPath)) return;

        File.AppendAllText(_profileLogPath, string.Format(CultureInfo.InvariantCulture,
            "{0},{1},{2},{3},{4},{5},{6},{7},{8:F6},{9:F6},{10:F6},{11:F6}\n",
            Time.frameCount, environmentSource, mode, impFunc, probeCount, samples, cands, _mip,
            buildS, dispatchS, readbackS, totalS));
    }

    // Reads the debug map back from the GPU and writes it to Assets/Debug/SHProbe as
    // a PNG. Filename encodes the probe index, projection method, and world position
    // of the visualized probe. PNG is 8-bit, so HDR values above 1 clamp — fine for a
    // quick visual check.
    private void SaveDiffuseMapToDisk()
    {
        if (_diffuseRenderTexture == null) return;

        var prevActive = RenderTexture.active;
        RenderTexture.active = _diffuseRenderTexture;

        var tex = new Texture2D(_diffuseRenderTexture.width, _diffuseRenderTexture.height, TextureFormat.RGBAFloat, false);
        tex.ReadPixels(new Rect(0, 0, _diffuseRenderTexture.width, _diffuseRenderTexture.height), 0, 0);
        tex.Apply();

        RenderTexture.active = prevActive;

        // byte[] png = tex.EncodeToPNG();
        byte[] exr = tex.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat);
        Destroy(tex);

        var ic = System.Globalization.CultureInfo.InvariantCulture;
        Vector3 p = _debugProbePos;
        string posStr    = string.Format(ic, "pos({0:F2}_{1:F2}_{2:F2})", p.x, p.y, p.z);
        string paramsStr = UsesSampling ? $"_N={numSamples}_M={EffectiveCandidates}_{importanceFunction}" : "";
        string fileName  = $"SHProbe_idx{debugProbeIndex}_{ModeTag()}{paramsStr}_mip{_mip}_{posStr}.exr";

        #if UNITY_EDITOR
                string dir = Path.Combine(Application.dataPath, "Debug", "SHProbe");
        #else
                string dir = Path.Combine(Application.persistentDataPath, "SHProbe");
        #endif
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName);

        // File.WriteAllBytes(path, png);
        File.WriteAllBytes(path, exr);
        Debug.Log($"[EnvironmentSHUpdater] Debug map saved: {path}");
    }

    // Saves the importance sampling data to disk for visualization/debugging. Writes the following files:
    // Samples_<tag>.csv    x,y,count   (only texels that were sampled)
    // Density_<tag>.f32    W*H float32 (the function the sampler follows)
    // tag = P<probe>_<mode>_<importance>_N<n>_M<m>_mip<l>_<W>x<H>_pos(x_y_z)_R<radius>
    private void SaveSampleDumpToDisk()
    {
        if (_sampleCountBuffer == null || _dumpW <= 0 || _dumpH <= 0) return;

        int W = _dumpW, H = _dumpH;

        _sampleCountBuffer.GetData(_sampleCountRaw);
        _sampleDensityBuffer.GetData(_densityRaw);

        #if UNITY_EDITOR
                string dir = Path.Combine(Application.dataPath, "Debug", "SH_ImportanceSamples");
        #else
                string dir = Path.Combine(Application.persistentDataPath, "SH_ImportanceSamples");
        #endif
        Directory.CreateDirectory(dir);

        var     ic  = CultureInfo.InvariantCulture;
        Vector3 p   = _debugProbePos;
        string  pos = string.Format(ic, "pos({0:F2}_{1:F2}_{2:F2})", p.x, p.y, p.z);
        string  tag = string.Format(ic,
            "P{0}_{1}_{2}_N{3}_M{4}_mip{5}_{6}x{7}_{8}_R{9:F2}",
            debugProbeIndex, ModeTag(), importanceFunction, numSamples,
            EffectiveCandidates, _mip, W, H, pos, envSphereRadius);

        var sb = new StringBuilder(1 << 16);
        sb.Append("x,y,count\n");
        for (int y = 0; y < H; y++)
        {
            int row = y * W;
            for (int x = 0; x < W; x++)
            {
                uint cnt = _sampleCountRaw[row + x];
                if (cnt == 0) continue;
                sb.AppendFormat(ic, "{0},{1},{2}\n", x, y, cnt);
            }
        }
        File.WriteAllText(Path.Combine(dir, $"Samples_{tag}.csv"), sb.ToString());

        var bytes = new byte[W * H * sizeof(float)];
        Buffer.BlockCopy(_densityRaw, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(Path.Combine(dir, $"Density_{tag}.f32"), bytes);

        Debug.Log($"[EnvironmentSHUpdater] Sample dump: {tag}");
    }
 
    // Sizes the sample-dump buffers to the mip dimensions and only rebuilds when they change.
    private void EnsureSampleDumpBuffers(int mipW, int mipH)
    {
        if (_sampleCountBuffer != null && _dumpW == mipW && _dumpH == mipH) return;
 
        _sampleCountBuffer?.Release();
        _sampleDensityBuffer?.Release();
 
        int n = mipW * mipH;
        _sampleCountBuffer   = new ComputeBuffer(n, sizeof(uint)); 
        _sampleDensityBuffer = new ComputeBuffer(n, sizeof(float));
        _sampleCountRaw   = new uint[n];
        _sampleCountZeros = new uint[n];          // cached zero-fill used to clear the histogram
        _densityRaw    = new float[n];
        _dumpW = mipW;
        _dumpH = mipH;
    }


    // Shared: Builds the PBRT-style Distribution2D (luminance × sinθ) for the current
    // env map at the active mip level. Two passes: per-row conditional CDFs
    // (one thread per row), then the marginal CDF (single thread). Shared by
    // every probe in this update.
    // PerProbeDistribution: then runs one extra pass per probe
    private void BuildDistributions(int mipH, Vector3[] probePos, bool dumping, bool depthOK)
    {
        int groups = Mathf.CeilToInt(mipH / 64f);   // both build kernels are [numthreads(64,1,1)]

        computeShader.SetTexture(_kernelBuildCond, "_EquirectMap",  _currentEnvTex);
        computeShader.SetBuffer (_kernelBuildCond, "_CondCdf",      _condCdfBuffer);
        computeShader.SetBuffer (_kernelBuildCond, "_MarginalFunc", _marginalFuncBuffer);
        computeShader.SetBuffer (_kernelBuildCond, "_ImportanceMap", _importanceMapBuffer);
        computeShader.SetBuffer(_kernelBuildMarg, "_MarginalFunc", _marginalFuncBuffer);
        computeShader.SetBuffer(_kernelBuildMarg, "_MarginalCdf",  _marginalCdfBuffer);
        if (dumping) computeShader.SetBuffer(_kernelBuildCond, "_SampleDensity", _sampleDensityBuffer);


        // Shared distribution → slot 0, compute importance map
        computeShader.SetInt("_DistributionIndex", 0);
        computeShader.Dispatch(_kernelBuildCond, groups, 1, 1);  // Pass 1 — conditional CDF along u, one thread per row    

        if (!UsesPerProbe)
        {
            computeShader.Dispatch(_kernelBuildMarg, 1, 1, 1);       // Pass 2 — marginal CDF along v, single thread
            
            return;
        }

        // One distrib per probe - geom folded into importance function
        computeShader.SetBuffer(_kernelBuildProbeCond, "_CondCdf",       _condCdfBuffer);
        computeShader.SetBuffer(_kernelBuildProbeCond, "_MarginalFunc",  _marginalFuncBuffer);
        computeShader.SetBuffer(_kernelBuildProbeCond, "_ImportanceMap", _importanceMapBuffer);
        computeShader.SetTexture(_kernelBuildProbeCond, "_DepthEquirect", 
                        depthOK ? (Texture)reconstructor.DepthEquirect : Texture2D.blackTexture); // to avoid error, not used anyway
        if (dumping) computeShader.SetBuffer(_kernelBuildProbeCond, "_SampleDensity", _sampleDensityBuffer);

        for (int p = 0; p < probePos.Length; p++)
        {
            computeShader.SetVector("_ProbePosition", probePos[p]);
            computeShader.SetInt("_DistributionIndex", p);

            computeShader.Dispatch(_kernelBuildProbeCond, groups, 1, 1);
            computeShader.Dispatch(_kernelBuildMarg, 1, 1, 1);
        }

    }

    // Sizes the three CDF buffers to the mip dimensions and only rebuilds when they change.
    private void EnsureDistributionBuffers(int mipW, int mipH, int slots)
    {
        if (_condCdfBuffer != null && _distW == mipW && _distH == mipH && _distributionIndex == slots) return;

        ReleaseDistributionBuffers();

        _condCdfBuffer       = new ComputeBuffer(slots * mipH * (mipW + 1), sizeof(float)); // per-row CDF, W+1 entries each
        _marginalCdfBuffer   = new ComputeBuffer(slots * (mipH + 1),        sizeof(float)); // CDF over rows
        _marginalFuncBuffer  = new ComputeBuffer(slots * mipH,              sizeof(float)); // per-row integrals
        _importanceMapBuffer = new ComputeBuffer(mipW * mipH,               sizeof(float)); // probe-independent, single copy
        _distW = mipW; 
        _distH = mipH; 
        _distributionIndex = slots;

        float mb = (_condCdfBuffer.count + _marginalCdfBuffer.count +
                    _marginalFuncBuffer.count + _importanceMapBuffer.count) * 4f / (1024f * 1024f);
        Debug.Log($"[EnvironmentSHUpdater] Distribution buffers: {slots} slot(s) at {mipW}x{mipH} — {mb:F2} MB");
    }

    private void ReleaseDistributionBuffers()
    {
        _condCdfBuffer?.Release();       _condCdfBuffer       = null;
        _marginalCdfBuffer?.Release();   _marginalCdfBuffer   = null;
        _marginalFuncBuffer?.Release();  _marginalFuncBuffer  = null;
        _importanceMapBuffer?.Release(); _importanceMapBuffer = null;
        _distW = _distH = _distributionIndex = 0;
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
        }
        return sh;
    }
}
