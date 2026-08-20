using Meta.XR;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Reconstructs an equirectangular (panoramic 2D) environment map from the Quest 3
/// passthrough color camera and the Environment Depth API, accumulating coverage
/// over time as the user scans the room.
///
/// Two aligned panoramas are produced (same layout, same texel = same direction):
///   - Color panorama : linear RGB, ready to feed EnvironmentSHUpdater.
///   - Depth panorama : linear metric depth (meters), 0 = not yet seen.
///
/// The heavy lifting is in EnvironmentReconstruction.compute (gather kernel). This
/// component just gathers the per-frame inputs — color texture, camera intrinsics,
/// camera pose, and the global depth uniforms — and dispatches the kernel.
///
/// The (u,v) <-> direction convention matches EnvironmentToSH.compute, so the color
/// panorama can be handed straight to EnvironmentSHUpdater.SetEnvironmentTexture().
///
/// notes / approximations:
///   - Left eye only (single PassthroughCameraAccess instance, CameraPosition = Left).
///   - Full-sphere dispatch every update (no FOV bounding box yet). TODO: FOV bounding box
///   - Color and depth sensors are treated as ~coincident at the head for the depth
///     projection (they differ by a few cm of lens offset).
///   - Depth stored as eye-space linear depth.
///   - Not available in the Unity Editor (needs passthrough + depth on device).
/// </summary>
public class EnvironmentMapReconstructor : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Assign EnvironmentReconstruction.compute")]
    [SerializeField] private ComputeShader m_computeShader;

    [Tooltip("PassthroughCameraAccess reading the LEFT color camera.")]
    [SerializeField] private PassthroughCameraAccess m_cameraAccess;

    [Header("Panorama settings")]
    [Tooltip("Equirectangular map resolution. 2:1 aspect. 2048x1024 is a good default.")]
    [SerializeField] private int m_width  = 2048;
    [SerializeField] private int m_height = 1024;

    [Tooltip("Run the reconstruction every N frames. 1 = every frame.")]
    [Range(1, 30)]
    [SerializeField] private int m_updateEveryNFrames = 1;

    [Header("Debug preview (optional)")]
    [Tooltip("RawImage that shows the reconstructed color panorama directly.")]
    [SerializeField] private RawImage m_colorPreview;
    [Tooltip("RawImage that shows the depth panorama as greyscale (near = white, unseen = blue).")]
    [SerializeField] private RawImage m_depthPreview;
    [Tooltip("Depth (m) mapped to black in the depth preview. Nearer values are brighter.")]
    [SerializeField] private float m_depthPreviewMaxMeters = 4f;

    // ── Public outputs ────────────────────────────────────────────────────────
    /// <summary>Equirectangular color panorama (linear RGB). Feed to EnvironmentSHUpdater.</summary>
    public RenderTexture ColorEquirect => _colorRT;
    /// <summary>Equirectangular depth panorama (linear meters, 0 = unseen).</summary>
    public RenderTexture DepthEquirect => _depthRT;
    /// <summary>True once the color camera is delivering frames and the maps exist.</summary>
    public bool IsReady { get; private set; }

    // ── Internals ─────────────────────────────────────────────────────────────
    private RenderTexture _colorRT;
    private RenderTexture _depthRT;
    private RenderTexture _depthDisplayRT;   // greyscale view of _depthRT for the canvas
    private Material      _depthVizMat;
    private int _kernel;
    private int _frameCounter;

    // Debug logging state — one-shot flags so we log transitions, not every frame.
    private bool _loggedWaiting, _loggedPlaying, _loggedFirstFrame, _loggedFirstDispatch, _loggedNullTex;
    private static readonly int MaxDepthID = Shader.PropertyToID("_MaxDepth");

    private static readonly int ColorEquirectID  = Shader.PropertyToID("_ColorEquirect");
    private static readonly int DepthEquirectID   = Shader.PropertyToID("_DepthEquirect");
    private static readonly int ColorTexID        = Shader.PropertyToID("_ColorTex");
    private static readonly int DepthTexGlobalName = Shader.PropertyToID("_EnvironmentDepthTexture");
    private static readonly int ZBufferParamsID   = Shader.PropertyToID("_EnvironmentDepthZBufferParams");

    // ──────────────────────────────────────────────────────────────────────────
    private void Awake()
    {
#if UNITY_EDITOR
        // Passthrough + depth are unavailable in the editor; keep the camera access
        // component from erroring and disable this reconstructor.
        if (m_cameraAccess != null) m_cameraAccess.enabled = false;
        enabled = false;
#endif
    }

    private void Start()
    {
#if UNITY_EDITOR
        return;
#else
        if (m_computeShader == null)
        {
            Debug.LogError("[EnvReconstruct] No compute shader assigned.");
            enabled = false;
            return;
        }
        _kernel = m_computeShader.FindKernel("ReconstructEquirect");

        // Material used to turn the metric depth map into a viewable greyscale image.
        if (m_depthPreview != null)
        {
            var vizShader = Shader.Find("EquirectDepthVisualize");
            if (vizShader != null)
                _depthVizMat = new Material(vizShader) { hideFlags = HideFlags.HideAndDontSave };
            else
                Debug.LogWarning("[EnvReconstruct] Shader 'EquirectDepthVisualize' not found — depth preview disabled.");
        }

        CreatePanoramas();
#endif
    }

    private void CreatePanoramas()
    {
        _colorRT = new RenderTexture(m_width, m_height, 0, RenderTextureFormat.ARGBHalf)
        {
            enableRandomWrite = true,
            useMipMap = false,
            name = "EnvColorEquirect"
        };
        _colorRT.Create();

        _depthRT = new RenderTexture(m_width, m_height, 0, RenderTextureFormat.RFloat)
        {
            enableRandomWrite = true,
            useMipMap = false,
            name = "EnvDepthEquirect"
        };
        _depthRT.Create();

        // Start empty (color = transparent black, depth = 0 = "unseen"). Persistent
        // across frames afterwards so coverage accumulates as the user scans.
        // ClearRT(_colorRT, Color.clear);
        // TODO: Clean magenta preview
        ClearRT(_colorRT, Color.magenta);
        ClearRT(_depthRT, Color.clear);

        // Color panorama can be shown directly. Depth is metric meters, so it goes
        // through a greyscale display texture updated each dispatch.
        if (m_colorPreview != null) m_colorPreview.texture = _colorRT;

        if (m_depthPreview != null && _depthVizMat != null)
        {
            _depthDisplayRT = new RenderTexture(m_width, m_height, 0, RenderTextureFormat.ARGB32)
            {
                useMipMap = false,
                name = "EnvDepthEquirectDisplay"
            };
            _depthDisplayRT.Create();
            m_depthPreview.texture = _depthDisplayRT;
        }
    }

    private static void ClearRT(RenderTexture rt, Color clear)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(true, true, clear);
        RenderTexture.active = prev;
    }

    // ──────────────────────────────────────────────────────────────────────────
    private void Update()
    {
#if UNITY_EDITOR
        return;
#else
        if (m_cameraAccess == null)
        {
            if (!_loggedNullTex) { Debug.LogError("[EnvReconstruct] m_cameraAccess is not assigned."); _loggedNullTex = true; }
            return;
        }

        // Camera not started yet (usually waiting on HEADSET_CAMERA permission).
        if (!m_cameraAccess.IsPlaying)
        {
            if (!_loggedWaiting) { Debug.Log("[EnvReconstruct] Waiting for PassthroughCameraAccess to start (IsPlaying = false) — check camera permission."); _loggedWaiting = true; }
            return;
        }
        if (!_loggedPlaying) { Debug.Log("[EnvReconstruct] PassthroughCameraAccess is PLAYING — camera started."); _loggedPlaying = true; }

        // Only integrate when a fresh camera frame is available.
        if (!m_cameraAccess.IsUpdatedThisFrame) return;

        if (!_loggedFirstFrame) { Debug.Log($"[EnvReconstruct] First camera frame received (resolution {m_cameraAccess.CurrentResolution})."); _loggedFirstFrame = true; }

        _frameCounter++;
        if (_frameCounter < m_updateEveryNFrames) return;
        _frameCounter = 0;

        Dispatch();
        if (!_loggedFirstDispatch) { Debug.Log("[EnvReconstruct] First Dispatch complete — panorama is accumulating coverage."); _loggedFirstDispatch = true; }
        IsReady = true;
#endif
    }

    private void Dispatch()
    {
        // Get the latest color texture and camera pose/intrinsics. If the color camera
        // is not delivering frames yet, skip this update.
        var colorTex = m_cameraAccess.GetTexture();
        if (colorTex == null)
        {
            if (!_loggedNullTex) { Debug.LogWarning("[EnvReconstruct] Camera is playing but GetTexture() returned null — no color frame yet."); _loggedNullTex = true; }
            return;
        }

        var intr = m_cameraAccess.Intrinsics;
        Pose pose = m_cameraAccess.GetCameraPose();

        // World -> camera-local (rigid, +Z forward), matching WorldToViewportPoint.
        Matrix4x4 invPose = Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one).inverse;

        // Sensor crop region — replicates PassthroughCameraAccess.CalcSensorCropRegion.
        // sensor's active region
        Vector2 sensorRes = intr.SensorResolution;
        Vector2 curRes    = m_cameraAccess.CurrentResolution;
        Vector2 scale = new Vector2(curRes.x / sensorRes.x, curRes.y / sensorRes.y);
        scale /= Mathf.Max(scale.x, scale.y);
        Vector4 crop = new Vector4(
            sensorRes.x * (1f - scale.x) * 0.5f,
            sensorRes.y * (1f - scale.y) * 0.5f,
            sensorRes.x * scale.x,
            sensorRes.y * scale.y);

        // ── Bind everything ──────────────────────────────────────────────────
        m_computeShader.SetInt("_OutWidth",  m_width);
        m_computeShader.SetInt("_OutHeight", m_height);

        m_computeShader.SetMatrix("_ColorInvPose", invPose);
        m_computeShader.SetVector("_ColorFocal",     intr.FocalLength);
        m_computeShader.SetVector("_ColorPrincipal", intr.PrincipalPoint);
        m_computeShader.SetVector("_ColorCropRegion", crop);
        m_computeShader.SetVector("_AnchorPos", pose.position);

        m_computeShader.SetTexture(_kernel, ColorTexID, colorTex);
        m_computeShader.SetTexture(_kernel, ColorEquirectID, _colorRT);
        m_computeShader.SetTexture(_kernel, DepthEquirectID, _depthRT);

        // Depth: bind the global depth texture explicitly; the reprojection matrices
        // and z-buffer params are read as global uniforms by the compute shader.
        // matrix array - comes through the global path
        // z-buffer params - comes through the global path but we pass it just in case
        m_computeShader.SetTextureFromGlobal(_kernel, DepthTexGlobalName, DepthTexGlobalName);
        m_computeShader.SetVector(ZBufferParamsID, Shader.GetGlobalVector(ZBufferParamsID));

        // devide into groups of 8x8 threads (matches NUM_THREADS in the compute shader)
        int groupsX = Mathf.CeilToInt(m_width  / 8f);
        int groupsY = Mathf.CeilToInt(m_height / 8f);
        m_computeShader.Dispatch(_kernel, groupsX, groupsY, 1);

        // Refresh the greyscale depth preview from the updated metric depth map.
        if (_depthDisplayRT != null && _depthVizMat != null)
        {
            _depthVizMat.SetFloat(MaxDepthID, m_depthPreviewMaxMeters);
            Graphics.Blit(_depthRT, _depthDisplayRT, _depthVizMat);
        }
    }

    /// <summary>Reset both panoramas to empty (e.g. to re-scan the room).</summary>
    public void ResetMaps()
    {
        if (_colorRT != null) ClearRT(_colorRT, Color.clear);
        if (_depthRT != null) ClearRT(_depthRT, Color.clear);
    }

    private void OnDestroy()
    {
        if (_colorRT != null) { _colorRT.Release(); Destroy(_colorRT); }
        if (_depthRT != null) { _depthRT.Release(); Destroy(_depthRT); }
        if (_depthDisplayRT != null) { _depthDisplayRT.Release(); Destroy(_depthDisplayRT); }
        if (_depthVizMat != null) Destroy(_depthVizMat);
    }
}
