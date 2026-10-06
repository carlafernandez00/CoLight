using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Sweeps EnvironmentSHUpdater configurations inside a single Play session and
/// dumps everything needed for the thesis figures, plus a manifest CSV that
/// maps every run to the exact files it produced.
///
/// Because the compute shader guards _SampleCount by (_ProbeIndex == _DebugProbeIndex)
/// and _SampleDensity by (_DistributionIndex == _DebugProbeIndex), each dump covers
/// exactly ONE probe. The sweep therefore issues one UpdateSH() per (config, probe).
///
/// Figures covered:
///   Fig 1  diffuse map  IS-PerProbe vs FullScan vs RMSE, probes {0,1,8}, uniform, N=512
///   Fig 2  RMSE vs N    probe 1, uniform, IS-PerProbe, N in {32..1024}, ref = FullScan p1
///   Fig 3  sample distribution + density, probes {0,1,8}, IS-PerProbe, uniform, N=512
///   Fig 5  sample distribution, one probe, IS-None vs IS-PerProbe, uniform, N=512
///   Fig 6  the same two methods, with their diffuse maps and a FullScan
///          reference, so the distributions can be read against the estimate
///          they produce
///   Fig 4  (separate block, needs a different skybox) probe 0, N=512,
///          IS-PerProbe with Uniform / Luminance / ColorAware
/// </summary>
[DisallowMultipleComponent]
public class SHExperimentRunner : MonoBehaviour
{
    // -----------------------------------------------------------------------
    // Inspector
    // -----------------------------------------------------------------------

    [Header("References")]
    public EnvironmentSHUpdater updater;

    [Tooltip("Optional. If set, the sweep waits for IsReady before starting.")]
    public EnvironmentMapReconstructor reconstructor;

    [Header("Run control")]
    [Tooltip("Empty -> auto timestamp. Dumps land in Assets/Debug/<runName>/...")]
    public string runName = "";

    public bool runOnStart = false;

    [Tooltip("Press this to launch the sweep manually during Play. On a Mac you may need fn+F9 " +
             "(or use the component's context menu: right-click the header > 'Run sweep now').")]
    public KeyCode runKey = KeyCode.F9;

    [Tooltip("Tick this during Play to launch the sweep. Unticks itself. " +
             "Handy on macOS where the function keys are media keys by default.")]
    public bool runNow = false;

    [Tooltip("Frames to wait after the reconstructor is ready, so the panorama settles.")]
    public int settleFrames = 60;

    [Tooltip("Frames to idle between two runs. 2 is usually enough; raise it if dumps look stale.")]
    public int framesBetweenRuns = 3;

    [Tooltip("Repeat every stochastic (IS) run this many times. Files get a _rep<k> suffix. " +
             "The sampler is deterministic (Hammersley), so repeats only differ for RIS. Keep at 1.")]
    [Range(1, 10)]
    public int repeats = 1;

    [Header("Blocks")]
    [Tooltip("Figures 1, 2, 3 and 5. Run with your main skybox / reconstruction.")]
    public bool blockMain = true;

    [Tooltip("Figure 4. Switch to the other skybox FIRST, then run only this block.")]
    public bool blockFigure4 = false;

    [Header("Shared parameters")]
    public int baseNumSamples = 512;
    public EnvironmentSHUpdater.ImportanceFunction baseImportance =
        EnvironmentSHUpdater.ImportanceFunction.Uniform;

    [Header("Figure 1 / 3")]
    public int[] diffuseProbes = { 0, 1, 8 };

    [Header("Figure 2")]
    public int   convergenceProbe = 1;
    public int[] convergenceN     = { 32, 64, 128, 256, 512, 1024 };

    [Header("Figures 5 and 6")]
    [Tooltip("Probe used for the IS-None vs IS-PerProbe comparison. Figure 5 " +
             "shows its two sample distributions; figure 6 adds the two diffuse " +
             "maps and a FullScan reference, so both come out of the same runs.")]
    public int modeCompareProbe = 1;

    [Header("Figure 4")]
    public int figure4Probe = 0;
    public EnvironmentSHUpdater.ImportanceFunction[] figure4Functions =
    {
        EnvironmentSHUpdater.ImportanceFunction.Uniform,
        EnvironmentSHUpdater.ImportanceFunction.Luminance,
        EnvironmentSHUpdater.ImportanceFunction.ColorAware
    };
    public EnvironmentSHUpdater.ProbeAwareSampling figure4ProbeAware =
        EnvironmentSHUpdater.ProbeAwareSampling.PerProbeDistribution;

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------

    private bool   _running;
    private string _manifestPath;
    private string _resolvedRunName;

    private struct RunCfg
    {
        public string figures;          // "1,3" etc. — merged on dedup
        public int    probe;
        public EnvironmentSHUpdater.ProjectionMethod     method;
        public EnvironmentSHUpdater.ProbeAwareSampling   probeAware;
        public EnvironmentSHUpdater.ImportanceFunction   importance;
        public int    n;
        public bool   wantDiffuse;
        public bool   wantSamples;

        // Identity of the GPU work + dump. Two configs with the same key write
        // the same files, so they are merged.
        public string Key
        {
            get
            {
                bool is_ = method == EnvironmentSHUpdater.ProjectionMethod.ImportanceSampling;
                return is_
                    ? $"p{probe}|IS|{probeAware}|{importance}|N{n}"
                    : $"p{probe}|FS";
            }
        }

        public string Label
        {
            get
            {
                bool is_ = method == EnvironmentSHUpdater.ProjectionMethod.ImportanceSampling;
                return is_
                    ? $"probe{probe} {probeAware} {importance} N={n}"
                    : $"probe{probe} FullScan";
            }
        }
    }

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    void Reset()
    {
        updater = GetComponent<EnvironmentSHUpdater>();
    }

    void Start()
    {
        if (updater == null) updater = GetComponent<EnvironmentSHUpdater>();
        if (updater == null)
        {
            Debug.LogError("[SHExperimentRunner] No EnvironmentSHUpdater assigned.");
            enabled = false;
            return;
        }
        if (reconstructor == null) reconstructor = updater.reconstructor;

        if (runOnStart) StartSweep();
    }

    void Update()
    {
        if (_running) return;

        // Inspector checkbox — the reliable trigger on macOS.
        if (runNow) { runNow = false; StartSweep(); return; }

        // Keyboard shortcut. Wrapped because projects on the new Input System
        // backend throw on Input.GetKeyDown instead of just returning false.
        try { if (Input.GetKeyDown(runKey)) StartSweep(); }
        catch (System.InvalidOperationException) { /* new Input System active — use runNow */ }
    }

    /// <summary>Right-click the component header in the Inspector during Play.</summary>
    [ContextMenu("Run sweep now")]
    public void StartSweep()
    {
        if (_running) { Debug.LogWarning("[SHExperimentRunner] Already running."); return; }
        StartCoroutine(Sweep());
    }

    // -----------------------------------------------------------------------
    // Sweep
    // -----------------------------------------------------------------------

    private IEnumerator Sweep()
    {
        _running = true;

        // ---- Snapshot inspector state so the scene is left as we found it ----
        var s_method      = updater.method;
        var s_probeAware  = updater.probeAware;
        var s_importance  = updater.importanceFunction;
        int s_numSamples  = updater.numSamples;
        int s_probeIndex  = updater.debugProbeIndex;
        bool s_diffuse    = updater.debugDiffuseMap;
        bool s_impDebug   = updater.debugImportanceSampling;
        bool s_dumpOnce   = updater.dumpOnce;
        int  s_everyN     = updater.updateEveryNFrames;
        string s_folder   = updater.runFolder;
        string s_tag      = updater.fileTag;

        updater.updateEveryNFrames = 0;   // stop the automatic refresh during the sweep

        // ---- Wait until the environment is stable ----
        if (reconstructor != null)
        {
            int guard = 0;
            while (!reconstructor.IsReady && guard < 3000) { guard++; yield return null; }
            if (!reconstructor.IsReady)
                Debug.LogWarning("[SHExperimentRunner] Reconstructor never became ready — running anyway.");
        }
        for (int i = 0; i < settleFrames; i++) yield return null;

        // ---- Output folder + manifest ----
        _resolvedRunName = string.IsNullOrEmpty(runName)
            ? "Run_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss")
            : runName;
        updater.runFolder = _resolvedRunName;

#if UNITY_EDITOR
        string root = Path.Combine(Application.dataPath, "Debug", _resolvedRunName);
#else
        string root = Path.Combine(Application.persistentDataPath, _resolvedRunName);
#endif
        Directory.CreateDirectory(root);
        _manifestPath = Path.Combine(root, "manifest.csv");
        File.WriteAllText(_manifestPath,
            "run,figures,probe,method,probeAware,importance,N,rep,probe_x,probe_y,probe_z," +
            "diffuse_file,samples_file,density_file\n");

        WriteRunInfo(root);

        // ---- Build and execute the config list ----
        var configs = BuildConfigs();
        Debug.Log($"[SHExperimentRunner] '{_resolvedRunName}': {configs.Count} unique run(s).");

        int runIndex = 0;
        foreach (var cfg in configs)
        {
            bool stochastic = cfg.method == EnvironmentSHUpdater.ProjectionMethod.ImportanceSampling;
            int  reps       = stochastic ? Mathf.Max(1, repeats) : 1;

            for (int rep = 0; rep < reps; rep++)
            {
                runIndex++;
                Debug.Log($"[SHExperimentRunner] ({runIndex}) {cfg.Label}" +
                          (reps > 1 ? $" rep{rep}" : "") + $"  [fig {cfg.figures}]");

                updater.method              = cfg.method;
                updater.probeAware          = cfg.probeAware;
                updater.importanceFunction  = cfg.importance;
                updater.numSamples          = cfg.n;
                updater.debugProbeIndex     = cfg.probe;
                updater.debugDiffuseMap     = cfg.wantDiffuse;
                updater.debugImportanceSampling = cfg.wantSamples;
                updater.dumpOnce            = true;
                updater.fileTag             = reps > 1 ? $"_rep{rep}" : "";

                updater.ResetSampleDump();

                // Clear the "last written" markers so a silent failure is visible
                // in the manifest instead of duplicating the previous row.
                string prevDiffuse = updater.LastDiffuseMapPath;
                string prevTag     = updater.LastSampleDumpTag;

                yield return StartCoroutine(updater.UpdateSHRoutine());

                for (int i = 0; i < framesBetweenRuns; i++) yield return null;

                AppendManifestRow(root, cfg, rep, prevDiffuse, prevTag);
            }
        }

        // ---- Restore ----
        updater.method                  = s_method;
        updater.probeAware              = s_probeAware;
        updater.importanceFunction      = s_importance;
        updater.numSamples              = s_numSamples;
        updater.debugProbeIndex         = s_probeIndex;
        updater.debugDiffuseMap         = s_diffuse;
        updater.debugImportanceSampling = s_impDebug;
        updater.dumpOnce                = s_dumpOnce;
        updater.updateEveryNFrames      = s_everyN;
        updater.runFolder               = s_folder;
        updater.fileTag                 = s_tag;

        Debug.Log($"[SHExperimentRunner] DONE. {runIndex} run(s) -> {root}");
        _running = false;
    }

    // -----------------------------------------------------------------------
    // Config list
    // -----------------------------------------------------------------------

    private List<RunCfg> BuildConfigs()
    {
        var byKey = new Dictionary<string, RunCfg>();
        var order = new List<string>();

        void Add(RunCfg c)
        {
            string k = c.Key;
            if (byKey.TryGetValue(k, out var existing))
            {
                existing.wantDiffuse |= c.wantDiffuse;
                existing.wantSamples |= c.wantSamples;
                if (!existing.figures.Contains(c.figures))
                    existing.figures += "+" + c.figures;
                byKey[k] = existing;
            }
            else
            {
                byKey[k] = c;
                order.Add(k);
            }
        }

        const EnvironmentSHUpdater.ProjectionMethod FS =
            EnvironmentSHUpdater.ProjectionMethod.FullScan;
        const EnvironmentSHUpdater.ProjectionMethod IS =
            EnvironmentSHUpdater.ProjectionMethod.ImportanceSampling;
        const EnvironmentSHUpdater.ProbeAwareSampling PER_PROBE =
            EnvironmentSHUpdater.ProbeAwareSampling.PerProbeDistribution;
        const EnvironmentSHUpdater.ProbeAwareSampling NONE =
            EnvironmentSHUpdater.ProbeAwareSampling.None;

        if (blockMain)
        {
            // --- Figure 1: FullScan reference + IS-PerProbe, probes {0,1,8} ---
            foreach (int p in diffuseProbes)
            {
                Add(new RunCfg {
                    figures = "1", probe = p, method = FS, probeAware = NONE,
                    importance = baseImportance, n = baseNumSamples,
                    wantDiffuse = true, wantSamples = false
                });
            }

            // --- Figures 1 + 3: IS-PerProbe at base N, probes {0,1,8} ---
            // One run serves both: diffuse map (Fig 1) and sample dump (Fig 3).
            foreach (int p in diffuseProbes)
            {
                Add(new RunCfg {
                    figures = "1,3", probe = p, method = IS, probeAware = PER_PROBE,
                    importance = baseImportance, n = baseNumSamples,
                    wantDiffuse = true, wantSamples = true
                });
            }

            // --- Figure 2: RMSE vs N at one probe (reference FullScan already added
            //     if convergenceProbe is in diffuseProbes; added here otherwise) ---
            Add(new RunCfg {
                figures = "2", probe = convergenceProbe, method = FS, probeAware = NONE,
                importance = baseImportance, n = baseNumSamples,
                wantDiffuse = true, wantSamples = false
            });
            foreach (int n in convergenceN)
            {
                Add(new RunCfg {
                    figures = "2", probe = convergenceProbe, method = IS, probeAware = PER_PROBE,
                    importance = baseImportance, n = n,
                    wantDiffuse = true, wantSamples = false
                });
            }

            // --- Figures 5 and 6: IS-None vs IS-PerProbe at one probe ---
            // Figure 5 needs only the sample dumps; figure 6 also needs each
            // method's diffuse map and a FullScan reference to measure them
            // against, so both flags are on and the reference is added here.
            // If this probe is already in diffuseProbes the FullScan run is
            // deduplicated away.
            Add(new RunCfg {
                figures = "6", probe = modeCompareProbe, method = FS, probeAware = NONE,
                importance = baseImportance, n = baseNumSamples,
                wantDiffuse = true, wantSamples = false
            });
            Add(new RunCfg {
                figures = "5,6", probe = modeCompareProbe, method = IS, probeAware = NONE,
                importance = baseImportance, n = baseNumSamples,
                wantDiffuse = true, wantSamples = true
            });
            Add(new RunCfg {
                figures = "5,6", probe = modeCompareProbe, method = IS, probeAware = PER_PROBE,
                importance = baseImportance, n = baseNumSamples,
                wantDiffuse = true, wantSamples = true
            });
        }

        if (blockFigure4)
        {
            // --- Figure 4: importance functions compared, one probe, other skybox ---
            foreach (var f in figure4Functions)
            {
                Add(new RunCfg {
                    figures = "4", probe = figure4Probe, method = IS,
                    probeAware = figure4ProbeAware, importance = f, n = baseNumSamples,
                    wantDiffuse = true, wantSamples = true
                });
            }
        }

        var list = new List<RunCfg>(order.Count);
        foreach (var k in order) list.Add(byKey[k]);
        return list;
    }

    // -----------------------------------------------------------------------
    // Manifest
    // -----------------------------------------------------------------------

    private void AppendManifestRow(string root, RunCfg cfg, int rep,
                                   string prevDiffuse, string prevTag)
    {
        var ic = CultureInfo.InvariantCulture;
        Vector3 pos = updater.LastDebugProbePos;

        string diffuseFile = "";
        if (cfg.wantDiffuse && updater.LastDiffuseMapPath != prevDiffuse)
            diffuseFile = Path.GetFileName(updater.LastDiffuseMapPath);

        string samplesFile = "", densityFile = "";
        if (cfg.wantSamples && updater.LastSampleDumpTag != prevTag)
        {
            samplesFile = $"Samples_{updater.LastSampleDumpTag}.csv";
            densityFile = $"Density_{updater.LastSampleDumpTag}.f32";
        }

        if (cfg.wantDiffuse && diffuseFile == "")
            Debug.LogWarning($"[SHExperimentRunner] No diffuse map written for {cfg.Label}");
        if (cfg.wantSamples && samplesFile == "")
            Debug.LogWarning($"[SHExperimentRunner] No sample dump written for {cfg.Label}");

        bool isIS = cfg.method == EnvironmentSHUpdater.ProjectionMethod.ImportanceSampling;

        File.AppendAllText(_manifestPath, string.Format(ic,
            "{0},{1},{2},{3},{4},{5},{6},{7},{8:F4},{9:F4},{10:F4},{11},{12},{13}\n",
            cfg.Key.Replace(',', ';'),
            cfg.figures.Replace(',', ';'),
            cfg.probe,
            isIS ? "ImportanceSampling" : "FullScan",
            isIS ? cfg.probeAware.ToString() : "N/A",
            isIS ? cfg.importance.ToString() : "N/A",
            isIS ? cfg.n : 0,
            rep,
            pos.x, pos.y, pos.z,
            diffuseFile, samplesFile, densityFile));
    }

    // Static context the plotting script needs (map dims, radius, mip, depth mode).
    private void WriteRunInfo(string root)
    {
        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("key,value\n");
        sb.AppendFormat(ic, "runName,{0}\n", _resolvedRunName);
        sb.AppendFormat(ic, "date,{0}\n", System.DateTime.Now.ToString("s"));
        sb.AppendFormat(ic, "environmentSource,{0}\n", updater.environmentSource);
        sb.AppendFormat(ic, "mipLevel,{0}\n", updater.mipLevel);
        sb.AppendFormat(ic, "envSphereRadius,{0}\n", updater.envSphereRadius);
        sb.AppendFormat(ic, "useDepthMap,{0}\n", updater.useDepthMap);
        sb.AppendFormat(ic, "minDepth,{0}\n", updater.minDepth);
        sb.AppendFormat(ic, "numCandidates,{0}\n", updater.numCandidates);
        sb.AppendFormat(ic, "intensityMultiplier,{0}\n", updater.intensityMultiplier);

        if (updater.reconstructor != null && updater.reconstructor.ColorEquirect != null)
        {
            sb.AppendFormat(ic, "envWidth,{0}\n",  updater.reconstructor.ColorEquirect.width);
            sb.AppendFormat(ic, "envHeight,{0}\n", updater.reconstructor.ColorEquirect.height);
            Vector3 c = updater.reconstructor.MapCenter;
            sb.AppendFormat(ic, "mapCenter,{0:F4} {1:F4} {2:F4}\n", c.x, c.y, c.z);
        }

        File.WriteAllText(Path.Combine(root, "run_info.csv"), sb.ToString());
    }
}