using System.Collections;
using System.IO;
using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// Reads frames from the Quest 3 passthrough cameras (on-device) or the computer
/// webcam (Unity Editor) and computes per-frame average luminance and dominant colour.
/// 
/// ! Currently left eye only. PassthroughCameraAccess reads one camera at a
/// time, controlled by its CameraPosition field (Left or Right). The component in your   
/// scene defaults to Left (index 0). To read both eyes simultaneously you'd need two     
/// PassthroughCameraAccess instances with different positions.
/// </summary>
public class LightEstimationManager : MonoBehaviour
{
    [Header("Camera Settings")]
    [Tooltip("Requested capture resolution. Actual resolution may differ on device.")]
    public int captureWidth = 1280;
    public int captureHeight = 960;
    [Tooltip("How many times per second to sample a frame for analysis.")]
    [Range(1, 30)]
    public int analysisFrameRate = 20;

    [Header("References")]
    [SerializeField] private PassthroughCameraAccess m_cameraAccess;
    [Tooltip("Optional RawImage to display the live camera feed.")]
    [SerializeField] private RawImage m_preview;
    [Header("Events")]
    [Tooltip("Fires every analysis frame with average luminance in [0, 1] range.")]
    [SerializeField] private UnityEvent<float> m_onBrightnessChange;

    [Header("Debug Capture")]
    [Tooltip("While enabled, saves every analysis frame to persistentDataPath/DebugFrames/ as 0000.jpg, 0001.jpg, etc.")]
    [SerializeField] private bool m_recordFrames;

    // ── Public read-only state ────────────────────────────────────────────
    /// <summary>Average perceptual luminance of the last analysed frame, in [0, 1].</summary>
    public float AverageLuminance { get; private set; }
    /// <summary>Average colour of the last analysed frame.</summary>
    public Color DominantColor { get; private set; }
    /// <summary>True once the camera is initialised and delivering frames.</summary>
    public bool IsReady { get; private set; }

    // ── Internals ─────────────────────────────────────────────────────────
    private float _sampleInterval;
    private float _timer;
    private bool _previewAssigned;
    private int _frameIndex;
    private string _debugFramesDir;

// The code inside the #if UNITY_EDITOR directive is meant to simulate the behavior of the PassthroughCameraAccess using a regular webcam when running in the Unity Editor. This allows developers to test and debug the brightness estimation functionality without needing to deploy to a device with a passthrough camera.
// TODO: separate the editor simulation into a different class to avoid cluttering the main BrightnessEstimationManager with editor-specific code, and to adhere to the Single Responsibility Principle.
// TODO: create a factory pattern to abstract away the differences between the webcam and passthrough camera, so that the main logic can operate on a common interface without needing #if UNITY_EDITOR directives scattered throughout the code.
#if UNITY_EDITOR
    private WebCamTexture _camTex;
    // WebCamTexture reports isPlaying=true immediately but stays 16x16 until
    // the first real frame arrives — guard against reading zeros.
    private bool IsWebcamReady => _camTex != null && _camTex.isPlaying && _camTex.width > 16;
#endif

    // ─────────────────────────────────────────────────────────────────────
    private void Awake()
    {
#if UNITY_EDITOR
        // Disable PassthroughCameraAccess before its Update() runs (execution order -100).
        if (m_cameraAccess != null)
            m_cameraAccess.enabled = false;
#endif
    }

    private void Start()
    {
        _sampleInterval = 1f / analysisFrameRate;
        // Editor: save next to the project root (easy to find in Finder, Unity won't import JPGs).
        // Device: use persistentDataPath (only writable location on Android/Quest).
#if UNITY_EDITOR
        _debugFramesDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "DebugFrames"));
#else
        _debugFramesDir = Path.Combine(Application.persistentDataPath, "DebugFrames");
#endif

#if UNITY_EDITOR
        WebCamDevice[] devices = WebCamTexture.devices;
        if (devices.Length == 0)
        {
            Debug.LogError("[LightEstimation] No camera found. Cannot start.");
            return;
        }
        _camTex = new WebCamTexture(devices[0].name, captureWidth, captureHeight);
        _camTex.Play();
        if (m_preview != null)
        {
            m_preview.texture = _camTex;
            _previewAssigned = true;
        }
        Debug.Log($"[LightEstimation] Editor mode — using webcam: {devices[0].name}");
        StartCoroutine(WaitForWebcam());
#else
        Debug.Log("[LightEstimation] Device mode — waiting for PassthroughCameraAccess.");
#endif
    }

#if UNITY_EDITOR
    private IEnumerator WaitForWebcam()
    {
        while (_camTex.width < 100)
            yield return null;
        IsReady = true;
        Debug.Log($"[LightEstimation] Webcam ready — {_camTex.width}x{_camTex.height}");
    }
#endif

    // ─────────────────────────────────────────────────────────────────────
    private void Update()
    {
#if UNITY_EDITOR
        if (!IsWebcamReady)
            return;
        if (!_camTex.didUpdateThisFrame) return;
        IsReady = true;
#else
        if (!m_cameraAccess.IsPlaying)
            return;
        if (!m_previewAssigned && m_preview != null)
        {
            m_preview.texture = m_cameraAccess.GetTexture();
            _previewAssigned = true;
        }
        if (!m_cameraAccess.IsUpdatedThisFrame) return;
        IsReady = true;
#endif
        _timer += Time.deltaTime;
        if (_timer < _sampleInterval) return;
        _timer = 0f;

        AnalyseFrame();
    }

    // ─────────────────────────────────────────────────────────────────────
    private void AnalyseFrame()
    {
        // Reads every pixel at full camera resolution.
        // This is more accurate than downsampling to 64x64 but costs more CPU per sample —
        // keep analysisFrameRate low (1–5 Hz) to avoid frame budget issues on device.
#if UNITY_EDITOR
        var pixels = _camTex.GetPixels32();
        var size   = new Vector2Int(_camTex.width, _camTex.height);
#else
        // read as a NativeArray for better performance on device (avoids texture → CPU copy).
        var pixels = m_cameraAccess.GetColors();   // NativeArray<Color32>, full resolution
        var size   = m_cameraAccess.CurrentResolution;
#endif

        float totalR = 0, totalG = 0, totalB = 0, totalLum = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            // Perceptual luminance calculation using Rec. 709 coefficients. Represents the brightness as perceived by the human eye
            totalLum += 0.2126f * pixels[i].r + 0.7152f * pixels[i].g + 0.0722f * pixels[i].b;
            totalR   += pixels[i].r;
            totalG   += pixels[i].g;
            totalB   += pixels[i].b;
        }

        int n = size.x * size.y;
        // TODO: consider a rolling buffer (average over N frames) to smooth out per-frame noise 
        AverageLuminance = (totalLum / n) / 255f;   // normalise byte range (0–255) → (0–1)
        DominantColor    = new Color(totalR / n / 255f, totalG / n / 255f, totalB / n / 255f);

        m_onBrightnessChange?.Invoke(AverageLuminance);

        if (m_recordFrames)
            SaveDebugFrame();
    }

    // ─────────────────────────────────────────────────────────────────────
    private void SaveDebugFrame()
    {
        if (!Directory.Exists(_debugFramesDir))
            Directory.CreateDirectory(_debugFramesDir);

        var tex = CaptureFrame();
        if (tex == null) return;

        string path = Path.Combine(_debugFramesDir, $"{_frameIndex:D4}.jpg");
        File.WriteAllBytes(path, tex.EncodeToJPG(90));
        Destroy(tex);

        Debug.Log($"[LightEstimation] Saved debug frame {_frameIndex:D4} → {path}");
        _frameIndex++;
    }

    // ─────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Captures the current camera frame as a Texture2D.
    /// Called automatically each analysis frame when m_recordFrames is enabled.
    /// Can also be called directly from other scripts.
    /// </summary>
    public Texture2D CaptureFrame()
    {
#if UNITY_EDITOR
        if (!IsWebcamReady) return null;

        var tex = new Texture2D(_camTex.width, _camTex.height, TextureFormat.RGBA32, false);
        tex.SetPixels32(_camTex.GetPixels32());
        tex.Apply();
#else
        // read as a texture
        var src = m_cameraAccess?.GetTexture();
        if (src == null) return null;

        var rt = RenderTexture.GetTemporary(src.width, src.height, 0);
        Graphics.Blit(src, rt);
        RenderTexture.active = rt;
        var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt);
#endif
        return tex;
    }

    // ─────────────────────────────────────────────────────────────────────
    private void OnDestroy()
    {
#if UNITY_EDITOR
        if (_camTex != null && _camTex.isPlaying)
            _camTex.Stop();
#endif
    }

    private void OnDrawGizmos()
    {
        if (!Application.isPlaying || !IsReady) return;
        Gizmos.color = new Color(DominantColor.r, DominantColor.g, DominantColor.b, 0.8f);
        Gizmos.DrawSphere(transform.position + Vector3.up * 0.3f, 0.05f);
    }
}
