using UnityEngine;
using Meta.XR.EnvironmentDepth; // may not be needed but safe to keep
using System.Collections;

/// <summary>
/// Reads frames from the Quest 3 Passthrough Camera API (PCA).
/// Computes average luminance and dominant color for debug / Stage 3 lighting estimation.
///
/// SETUP:
///   1. Attach this script to any GameObject (e.g. "LightingManager")
///   2. Make sure AndroidManifest.xml has:
///        <uses-permission android:name="horizonos.permission.HEADSET_CAMERA" />
///   3. In OVRManager → "Insights Passthrough" → tick "Enable Camera Access"
/// </summary>
public class PCACameraReader : MonoBehaviour
{
    [Header("Camera Settings")]
    [Tooltip("Resolution for the WebCamTexture. 1280x960 is the safest cross-device option.")]
    public int captureWidth  = 1280;
    public int captureHeight = 960;

    [Tooltip("How many times per second to sample a frame for analysis.")]
    [Range(1, 30)]
    public int analysisFrameRate = 5;

    [Header("Debug")]
    [Tooltip("Assign a RawImage UI element to preview the live camera feed in-headset.")]
    public UnityEngine.UI.RawImage debugPreview;

    [Tooltip("Show on-screen debug label (requires a Canvas in the scene).")]
    public UnityEngine.UI.Text debugLabel;

    // ── Public read-only state ────────────────────────────────────────────
    public float  AverageLuminance  { get; private set; }
    public Color  DominantColor     { get; private set; }
    public bool   IsReady           { get; private set; }

    // ── Internals ─────────────────────────────────────────────────────────
    private WebCamTexture _camTex;
    private Texture2D     _sampleTex;   // CPU-readable copy for pixel analysis
    private Color[]       _pixels;
    private float         _sampleInterval;
    private float         _timer;

    // ─────────────────────────────────────────────────────────────────────
    void Start()
    {
        _sampleInterval = 1f / analysisFrameRate;

        // On-device: Meta's PCA cameras show up as WebCam devices.
        // The forward-facing cam is typically index 0 or named "OVRPassthroughCamera".
        // In the editor / simulator, this will open your Mac webcam instead — useful for testing.
        WebCamDevice[] devices = WebCamTexture.devices;

        if (devices.Length == 0)
        {
            Debug.LogError("[PCA] No camera devices found. Make sure HEADSET_CAMERA permission is set.");
            return;
        }

        // Try to find Meta's passthrough camera by name; fall back to index 0
        string targetCam = devices[0].name;
        foreach (var d in devices)
        {
            if (d.name.ToLower().Contains("ovr") || d.name.ToLower().Contains("passthrough"))
            {
                targetCam = d.name;
                break;
            }
        }

        Debug.Log($"[PCA] Opening camera: {targetCam}");

        _camTex = new WebCamTexture(targetCam, captureWidth, captureHeight);
        _camTex.Play();

        // Create a CPU-readable Texture2D we'll blit into for pixel sampling
        // We downsample to 64×64 for cheap per-frame analysis
        _sampleTex = new Texture2D(64, 64, TextureFormat.RGBA32, false);

        if (debugPreview != null)
            debugPreview.texture = _camTex;

        StartCoroutine(WaitForCamera());
    }

    IEnumerator WaitForCamera()
    {
        // WebCamTexture needs a frame or two before width/height are valid
        while (_camTex.width < 100)
            yield return null;

        IsReady = true;
        Debug.Log($"[PCA] Camera ready — actual resolution: {_camTex.width}×{_camTex.height}");
    }

    // ─────────────────────────────────────────────────────────────────────
    void Update()
    {
        if (!IsReady || !_camTex.didUpdateThisFrame) return;

        _timer += Time.deltaTime;
        if (_timer < _sampleInterval) return;
        _timer = 0f;

        AnalyseFrame();
    }

    // ─────────────────────────────────────────────────────────────────────
    void AnalyseFrame()
    {
        // Blit the live WebCamTexture into our small CPU texture (64×64 downsample)
        // This is the cheapest way to get pixel data without a GPU readback stall.
        RenderTexture rt = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(_camTex, rt);

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        _sampleTex.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
        _sampleTex.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);

        _pixels = _sampleTex.GetPixels();

        // ── Compute average luminance & dominant color ────────────────────
        float totalR = 0, totalG = 0, totalB = 0;
        float totalLum = 0;

        foreach (var p in _pixels)
        {
            // Perceptual luminance (BT.709)
            float lum = 0.2126f * p.r + 0.7152f * p.g + 0.0722f * p.b;
            totalLum += lum;
            totalR += p.r;
            totalG += p.g;
            totalB += p.b;
        }

        int n = _pixels.Length;
        AverageLuminance = totalLum / n;
        DominantColor    = new Color(totalR / n, totalG / n, totalB / n);

        UpdateDebugLabel();
    }

    void UpdateDebugLabel()
    {
        if (debugLabel == null) return;

        debugLabel.text =
            $"[PCA]\n" +
            $"Luminance : {AverageLuminance:F3}\n" +
            $"AvgColor  : R{DominantColor.r:F2} G{DominantColor.g:F2} B{DominantColor.b:F2}\n" +
            $"Cam size  : {_camTex.width}×{_camTex.height}";
    }

    // ─────────────────────────────────────────────────────────────────────
    void OnDestroy()
    {
        if (_camTex != null && _camTex.isPlaying)
            _camTex.Stop();
    }

    // ── Editor Gizmo: show luminance as a coloured sphere ─────────────────
    void OnDrawGizmos()
    {
        if (!Application.isPlaying || !IsReady) return;
        Gizmos.color = new Color(DominantColor.r, DominantColor.g, DominantColor.b, 0.8f);
        Gizmos.DrawSphere(transform.position + Vector3.up * 0.3f, 0.05f);
    }
}
