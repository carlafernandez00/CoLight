using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Runs the whole profiling matrix automatically from a single build.
///
/// Phase 1 — sweep the Monte Carlo sample count N, probe count fixed.
/// Phase 2 — sweep the probe count, N fixed.
///
/// For every configuration it runs 'warmupUpdates' unmeasured updates (shader warm-up,
/// buffer reallocation, clock ramp-up) and then 'measuredUpdates' measured ones.
/// EnvironmentSHUpdater opens a new CSV per configuration, so you end up with one
/// clean file per point on each curve and never touch the inspector mid-run.
///
/// Scene setup: empty GameObject + this component, drag the EnvironmentSHUpdater in.
/// </summary>
public class SHProfilingSweep : MonoBehaviour
{
    [Header("References")]
    public EnvironmentSHUpdater updater;

    [Header("Run control")]
    [Tooltip("Seconds to wait after the panorama is ready before the first configuration. " +
             "Gives you time to put the headset on and stop moving.")]
    public float startDelaySeconds = 10f;

    [Tooltip("Unmeasured updates per configuration (discarded).")]
    public int warmupUpdates = 10;

    [Tooltip("Measured updates per configuration (one CSV row each).")]
    public int measuredUpdates = 50;

    [Tooltip("Frames to idle between consecutive updates, so the sweep does not sit in a tight loop.")]
    public int framesBetweenUpdates = 2;

    [Header("Fixed configuration")]
    public EnvironmentSHUpdater.ProjectionMethod   method             = EnvironmentSHUpdater.ProjectionMethod.ImportanceSampling;
    public EnvironmentSHUpdater.ImportanceFunction importanceFunction = EnvironmentSHUpdater.ImportanceFunction.Luminance;
    public EnvironmentSHUpdater.ProbeAwareSampling probeAware         = EnvironmentSHUpdater.ProbeAwareSampling.None;
    public int mipLevel = 0;

    [Header("Phase 1 — sample sweep (probe count fixed)")]
    public bool  runSampleSweep = true;
    public int[] sampleSweep    = { 32, 64, 128, 256, 512, 1024 };
    public int   fixedProbes    = 9;

    [Header("Phase 2 — probe sweep (N fixed)")]
    public bool  runProbeSweep = true;
    public int[] probeSweep    = { 1, 4, 9, 16, 25, 36 };
    public int   fixedSamples  = 512;

    private bool _done;

    IEnumerator Start()
    {
        if (updater == null)
        {
            Debug.LogError("[Sweep] No EnvironmentSHUpdater assigned — aborting.");
            yield break;
        }

        // Wait until the reconstructor has produced a usable panorama.
        Debug.Log("[Sweep] Waiting for the environment map...");
        while (updater.reconstructor == null || !updater.reconstructor.IsReady)
            yield return null;

        Debug.Log($"[Sweep] Environment ready. Starting in {startDelaySeconds:F0}s — hold still.");
        yield return new WaitForSeconds(startDelaySeconds);

        // ---- Fixed state for the whole sweep -------------------------------
        updater.updateEveryNFrames      = 0;      // we drive every update ourselves
        updater.debugDiffuseMap         = false;  // critical: runs inside the dispatch
        updater.debugImportanceSampling = false;  // critical: runs inside the dispatch + disk I/O
        updater.profilingSyncReadback   = true;   // no frame yield -> total_ms is real latency
        updater.method                  = method;
        updater.importanceFunction      = importanceFunction;
        updater.probeAware              = probeAware;
        updater.mipLevel                = mipLevel;
        if (updater.reconstructor != null) updater.reconstructor.debugProfiling = false;

        int available = updater.AvailableProbeCount;
        Debug.Log($"[Sweep] Probes available in scene (incl. ambient): {available}");

        int configs = (runSampleSweep ? sampleSweep.Length : 0) + (runProbeSweep ? probeSweep.Length : 0);
        Debug.Log($"[Sweep] {configs} configurations x {warmupUpdates}+{measuredUpdates} updates.");

        // ---- Phase 1: sample sweep -----------------------------------------
        if (runSampleSweep)
        {
            foreach (int n in sampleSweep)
                yield return RunConfig(n, fixedProbes, available);
        }

        // ---- Phase 2: probe sweep ------------------------------------------
        if (runProbeSweep)
        {
            foreach (int p in probeSweep)
                yield return RunConfig(fixedSamples, p, available);
        }

        updater.debugProfiling        = false;
        updater.profilingSyncReadback = false;
        _done = true;

#if UNITY_EDITOR
        string dir = Path.Combine(Application.dataPath, "Debug", "Profiling");
#else
        string dir = Path.Combine(Application.persistentDataPath, "Profiling");
#endif
        Debug.Log($"[Sweep] ===== DONE ===== CSVs in: {dir}");
    }

    private IEnumerator RunConfig(int samples, int probes, int availableProbes)
    {
        if (probes > availableProbes)
        {
            Debug.LogWarning($"[Sweep] SKIPPING probes={probes}: only {availableProbes} exist in the scene. " +
                             "Add more probes to the Light Probe Group and re-bake.");
            yield break;
        }

        updater.numSamples = samples;
        updater.maxProbes  = probes;

        Debug.Log(string.Format(CultureInfo.InvariantCulture,
            "[Sweep] --- config  N={0}  probes={1} ---", samples, probes));

        // Warm-up: same code path, nothing written to disk.
        updater.debugProfiling = false;
        for (int i = 0; i < warmupUpdates; i++)
            yield return OneUpdate();

        // Measured.
        updater.debugProfiling = true;
        for (int i = 0; i < measuredUpdates; i++)
            yield return OneUpdate();
        updater.debugProfiling = false;
    }

    private IEnumerator OneUpdate()
    {
        var co = updater.UpdateSHRoutine();
        if (co != null) yield return co;
        for (int f = 0; f < framesBetweenUpdates; f++) yield return null;
    }

    // Minimal on-screen status. On Quest an IMGUI overlay is not visible in VR,
    // so this is only useful in the Editor / Link. Device progress goes to logcat.
    void OnGUI()
    {
        if (!Application.isEditor) return;
        GUI.Label(new Rect(10, 10, 600, 24),
            _done ? "SH sweep: DONE"
                  : $"SH sweep running — N={updater?.numSamples} probes={updater?.maxProbes}");
    }
}