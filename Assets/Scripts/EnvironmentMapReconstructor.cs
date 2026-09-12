using Meta.XR;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// Reconstructs an equirectangular (panoramic 2D) environment map from the Quest 3
/// passthrough color camera and the Environment Depth API, accumulating coverage
/// over time as the user scans the room.
///
/// Two aligned panoramas are produced (same layout, same texel = same direction):
///   - Color panorama : linear RGB, alpha channel 0 = not yet seen.
///   - Depth panorama : radial distance from C,   0 = not yet seen.
/// 
/// Notice:
///   - Color and depth sensors are treated as ~coincident at the head for the depth
///     projection (they differ by a few cm of lens offset).
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

    [Header("Scanning")]
    [Tooltip("Warn if the user drifts more than this (meters) from C while scanning.")]
    [SerializeField] private float m_scanDriftWarnMeters = 0.30f;

    [Header("Status UI")]
    [Tooltip("Label on the debug canvas. Shows scan/tracking state and drift.")]
    [SerializeField] private TMPro.TMP_Text m_statusText;

    [Header("Debug preview (optional)")]
    [Tooltip("RawImage that shows the reconstructed color panorama directly.")]
    [SerializeField] private RawImage m_colorPreview;
    [Tooltip("RawImage that shows the depth panorama as greyscale (near = white, unseen = blue).")]
    [SerializeField] private RawImage m_depthPreview;
    [Tooltip("Fallback depth (m) mapped to black in the depth preview, used until the first measured range arrives.")]
    [SerializeField] private float m_depthPreviewMaxMeters = 4f;

    // ── Public outputs ────────────────────────────────────────────────────────
    /// <summary>Equirectangular color panorama (linear RGB). Feed to EnvironmentSHUpdater.</summary>
    public RenderTexture ColorEquirect => _colorRT;
    /// <summary>Equirectangular depth panorama (linear meters, 0 = unseen).</summary>
    public RenderTexture DepthEquirect => _depthRT;
    /// <summary>True once the color camera is delivering frames and the maps exist.</summary>
    public bool IsReady { get; private set; }
    /// <summary>World-space centre the panorama is anchored at (C). Valid once scanning starts.</summary>
    public Vector3 MapCenter => _mapCenter;
    /// <summary>How far the head has drifted from C during the scan, in meters. For UI.</summary>
    public float ScanDrift { get; private set; }
    /// <summary>Current lifecycle stage, for UI.</summary>
    public bool IsScanning => _state == State.Scanning;
    public bool IsTracking => _state == State.Tracking;

    // ── Internals ─────────────────────────────────────────────────────────────
    private enum State { Idle, Scanning, Tracking }
    private State _state = State.Idle;
    private RenderTexture _colorRT;
    private RenderTexture _depthRT;
    private RenderTexture _colorDisplayRT;  
    private RenderTexture _depthDisplayRT;  
    private Material      _colorVizMat;
    private Material      _depthVizMat;
    private ComputeBuffer _depthAccum;
    private ComputeBuffer _depthRange;
    private Vector3       _mapCenter;

    private static readonly uint[] DepthRangeReset = { 0u, 0u };   // min = sentinel, max = 0
    private AsyncGPUReadbackRequest? _depthRangeRequest;
    private float _measuredMaxDepth;                                // 0 = nothing measured yet, fall back to m_depthPreviewMaxMeters

    private int _kernelScan, _kernelClear, _kernelScatter, _kernelResolve, _kernelRange;
    private int _frameCounter;
    private bool _dispatchPending;   // Update() decides, OnBeforeRenderDispatch() dispatches

    // Debug logging state — one-shot flags so we log transitions, not every frame.
    private bool _loggedWaiting, _loggedPlaying, _loggedFirstFrame, _loggedFirstDispatch, _loggedNullTex;
    private static readonly int MaxDepthID = Shader.PropertyToID("_MaxDepth");

    private static readonly int ColorEquirectID    = Shader.PropertyToID("_ColorEquirect");
    private static readonly int DepthEquirectID    = Shader.PropertyToID("_DepthEquirect");
    private static readonly int ColorTexID         = Shader.PropertyToID("_ColorTex");
    private static readonly int DepthTexGlobalName = Shader.PropertyToID("_EnvironmentDepthTexture");
    private static readonly int ZBufferParamsID    = Shader.PropertyToID("_EnvironmentDepthZBufferParams");
    private static readonly int DepthAccumID       = Shader.PropertyToID("_DepthAccumulation");
    private static readonly int DepthRangeID       = Shader.PropertyToID("_DepthRange");
     private static readonly int MapCenterID       = Shader.PropertyToID("_MapCenter");
    private static readonly int CamPosID           = Shader.PropertyToID("_CameraPos");
    private static readonly int DepthReprojGlobalID = Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices");
    private static readonly int DepthReprojID      = Shader.PropertyToID("_DepthReproj");
    private static readonly int DepthReprojInvID   = Shader.PropertyToID("_DepthReprojInverse");


    private void Awake()
    {
    #if UNITY_EDITOR
            // Passthrough + depth are unavailable in the editor; keep the camera access
            // component from erroring and disable this reconstructor.
            if (m_cameraAccess != null) m_cameraAccess.enabled = false;
            enabled = false;
    #endif
    }

    private void OnEnable()
    {
    #if !UNITY_EDITOR
        Application.onBeforeRender += OnBeforeRenderDispatch;
    #endif
    }

    private void OnDisable()
    {
    #if !UNITY_EDITOR
        Application.onBeforeRender -= OnBeforeRenderDispatch;
        _dispatchPending = false;
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
        _kernelScan = m_computeShader.FindKernel("ReconstructEquirect");
        _kernelClear = m_computeShader.FindKernel("ClearDepthAccumulation");
        _kernelScatter = m_computeShader.FindKernel("ObtainDepth");
        _kernelResolve = m_computeShader.FindKernel("ResolveDepthAndColor");
        _kernelRange = m_computeShader.FindKernel("ReduceDepthRange");

        // Material used to paint unseen texels (alpha 0) magenta in the color preview.
        if (m_colorPreview != null)
        {
            var colorVizShader = Shader.Find("EquirectColorVisualize");
            if (colorVizShader != null)
                _colorVizMat = new Material(colorVizShader) { hideFlags = HideFlags.HideAndDontSave };
            else
                Debug.LogWarning("[EnvReconstruct] Shader 'EquirectColorVisualize' not found — color preview falls back to the raw panorama.");
        }

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
        SetStatus("Ready. Press A to start scanning.", false);
    #endif
    }

    private void CreatePanoramas()
    {
        // enableRandomWrite true to allow writing at any texel
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

        // Per-frame nim accumulator. Buffer rather than texture because InterlockedMin needs a uint UAV
        _depthAccum = new ComputeBuffer(m_width * m_height, sizeof(uint));

        // Two uints: measured min and max depth over the panorama, for the preview's range.
        _depthRange = new ComputeBuffer(2, sizeof(uint));

        // Start empty (color = transparent black, depth = 0 = "unseen").
        ClearRT(_colorRT, Color.clear);
        ClearRT(_depthRT, Color.clear);

        // Display texture for the color env.: unseen texels (alpha 0) read as magenta instead of transparent
        if (m_colorPreview != null && _colorVizMat != null)
        {
            _colorDisplayRT = new RenderTexture(m_width, m_height, 0, RenderTextureFormat.ARGB32)
            {
                useMipMap = false,
                name = "EnvColorEquirectDisplay"
            };
            _colorDisplayRT.Create();

            // clear to magenta so the panel is visible on the canvas before the first dispatch
            ClearRT(_colorDisplayRT, Color.magenta);   
            m_colorPreview.texture = _colorDisplayRT;
        }
        else if (m_colorPreview != null)
        {
            m_colorPreview.texture = _colorRT;
        }

        // Depth is metric meters -> it goes through a bluescale display texture updated each dispatch
        if (m_depthPreview != null && _depthVizMat != null)
        {
            _depthDisplayRT = new RenderTexture(m_width, m_height, 0, RenderTextureFormat.ARGB32)
            {
                useMipMap = false,
                name = "EnvDepthEquirectDisplay"
            };
            _depthDisplayRT.Create();
            
            // Clear to black so the panel is visible on the canvas before the first dispatch
            ClearRT(_depthDisplayRT, Color.black);
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

    public void StartScanning()
    {
        if (m_cameraAccess == null || !m_cameraAccess.IsPlaying)
        {
            Debug.LogWarning("[EnvReconstruct] Cannot start scanning: camera not ready.");
            SetStatus("Camera not ready (check camera permission). Press A again in a moment.", false);
            return;
        }
        ResetMaps();
    
        _mapCenter = m_cameraAccess.GetCameraPose().position;
        _state = State.Scanning;
        ScanDrift = 0f;

        SetStatus("Scanning started. Stand still and look around. Press A when done.");
    }

    public void FinishScanning()
    {
        if (_state != State.Scanning) 
        {
            Debug.LogWarning("[EnvReconstruct] Cannot finish scanning: not currently scanning.");
            return;
        }

        _state = State.Tracking;
        SetStatus("Scanning finished. Tracking! you can walk around now :)");
    }
    
    private void SetStatus(string s, bool log = true)
    {
        if (log) Debug.Log($"[EnvReconstruct] {s}");
        // Guard the assignment: this is called every frame while scanning/waiting and
        // TMP rebuilds its mesh on every set, even when the string is unchanged.
        if (m_statusText != null && m_statusText.text != s) m_statusText.text = s;
    }


    private void Update()
    {
    #if UNITY_EDITOR
            return;
    #else
        // click A on the right controller to start or finish scanning
        if (OVRInput.GetDown(OVRInput.Button.One))
        {
            if (_state == State.Scanning) FinishScanning();
            else if (_state == State.Idle) StartScanning();
            else                           StartScanning();

        }
        if (_state == State.Idle) return;

        if (m_cameraAccess == null)
        {
            if (!_loggedNullTex) { Debug.LogError("[EnvReconstruct] m_cameraAccess is not assigned."); _loggedNullTex = true; }
            return;
        }

        // Camera not started yet (usually waiting on HEADSET_CAMERA permission).
        if (!m_cameraAccess.IsPlaying)
        {
            if (!_loggedWaiting) { Debug.Log("[EnvReconstruct] Waiting for PassthroughCameraAccess to start (IsPlaying = false) — check camera permission."); _loggedWaiting = true; }
            SetStatus("Waiting for the passthrough camera… (check camera permission)", false);
            return;
        }
        if (!_loggedPlaying) { Debug.Log("[EnvReconstruct] PassthroughCameraAccess is PLAYING — camera started."); _loggedPlaying = true; }

        // Only integrate when a fresh camera frame is available.
        if (!m_cameraAccess.IsUpdatedThisFrame) return;

        if (!_loggedFirstFrame) { Debug.Log($"[EnvReconstruct] First camera frame received (resolution {m_cameraAccess.CurrentResolution})."); _loggedFirstFrame = true; }

        // Update the drift from the map center C (for UI)
        if (_state == State.Scanning)
        {
            var camPos = m_cameraAccess.GetCameraPose().position;
            ScanDrift = Vector3.Distance(camPos, _mapCenter);

            if (ScanDrift > m_scanDriftWarnMeters)
            {
                SetStatus($"Move back to where you started — {ScanDrift:F2} m off. Press A when done.", false);
            }
            else
                SetStatus($"Scanning: drift {ScanDrift:F2} m. Press A when done.", false);
        }

        _frameCounter++;
        if (_frameCounter < m_updateEveryNFrames) return;
        _frameCounter = 0;

        // Defer the actual dispatch to OnBeforeRender as EnvironmentDepthManager has not yet
        // published this frame's depth texture and reprojection matrices at Update time.
        _dispatchPending = true;
    #endif
    }


    // EnvironmentDepthManager publishes from Application.onBeforeRender at the default order (0),
    // so a later order here means the globals we read are current-frame
    [BeforeRenderOrder(100)]     // Depth data instead of previous frame -> sync immediately before the camera renders the frame.
    private void OnBeforeRenderDispatch()
    {
        if (!_dispatchPending) return;
        _dispatchPending = false;

        Dispatch();
        if (!_loggedFirstDispatch) { Debug.Log("[EnvReconstruct] First Dispatch complete — panorama is accumulating coverage."); _loggedFirstDispatch = true; }
        IsReady = true;
    }

    private void Dispatch()
    {
        // Get the latest color texture and camera pose/intrinsics
        var colorTex = m_cameraAccess.GetTexture();

        // If the color camera is not delivering frames yet, skip this update.
        if (colorTex == null)
        {
            if (!_loggedNullTex) 
            { 
                Debug.LogWarning("[EnvReconstruct] Camera is playing but GetTexture() returned null — no color frame yet."); 
                _loggedNullTex = true; 
            }
            return;
        }

        var intr = m_cameraAccess.Intrinsics;
        Pose pose = m_cameraAccess.GetCameraPose();

        // sensor's active region at the current resolution (replicates PassthroughCameraAccess.CalcSensorCropRegion)
        Vector2 sensorRes = intr.SensorResolution;
        Vector2 curRes    = m_cameraAccess.CurrentResolution;
        Vector2 scale = new Vector2(curRes.x / sensorRes.x, curRes.y / sensorRes.y);
        scale /= Mathf.Max(scale.x, scale.y);      // one component exactly 1
        Vector4 crop = new Vector4(
            sensorRes.x * (1f - scale.x) * 0.5f,    // top left (coord x) of the crop region in sensor space
            sensorRes.y * (1f - scale.y) * 0.5f,    // top left (coord y) of the crop region in sensor space
            sensorRes.x * scale.x,                  // width of the crop region in sensor space
            sensorRes.y * scale.y);                 // height of the crop region in sensor space
        
        // Define color camera projection matrix: P = K * [R|t]; for K considering the crop region
        Matrix4x4 invCameraPose = Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one).inverse;
        Matrix4x4 K = Matrix4x4.identity;
        K.m00 =  intr.FocalLength.x    / crop.z;
        K.m02 = (intr.PrincipalPoint.x - crop.x) / crop.z;
        K.m11 =  intr.FocalLength.y    / crop.w;
        K.m12 = (intr.PrincipalPoint.y - crop.y) / crop.w;

        Matrix4x4 colorProjectionMatrix = K * invCameraPose;

        // Get depth reprojection matrix (world -> depth camera clip space) and inverse Depth from the EnvironmentDepth API
        // Index 0 = left eye, matching depth texture array slice 0
        Matrix4x4[] depthReprojMatrices = Shader.GetGlobalMatrixArray(DepthReprojGlobalID);
        Matrix4x4 depthReproj    = depthReprojMatrices[0];
        Matrix4x4 depthReprojInv = depthReproj.inverse;

        // BIND EVERYTHING
        m_computeShader.SetInt("_OutWidth",  m_width);
        m_computeShader.SetInt("_OutHeight", m_height);

        m_computeShader.SetMatrix("_ColorProjectionMatrix", colorProjectionMatrix);
        m_computeShader.SetVector("_ColorFocal",     intr.FocalLength);
        m_computeShader.SetVector("_ColorPrincipal", intr.PrincipalPoint);
        m_computeShader.SetVector("_ColorCropRegion", crop);

        m_computeShader.SetVector(MapCenterID, _mapCenter);
        m_computeShader.SetVector(CamPosID, pose.position);
        m_computeShader.SetMatrix(DepthReprojID, depthReproj);
        m_computeShader.SetMatrix(DepthReprojInvID, depthReprojInv);

        // depth params from Meta Global
        m_computeShader.SetVector(ZBufferParamsID, Shader.GetGlobalVector(ZBufferParamsID));

        // devide into groups of 8x8 threads (matches NUM_THREADS in the compute shader)
        int groupsX = Mathf.CeilToInt(m_width  / 8f);
        int groupsY = Mathf.CeilToInt(m_height / 8f);

        // Scanning mode
        if (_state == State.Scanning)
        {
            m_computeShader.SetTexture(_kernelScan, ColorTexID, colorTex);
            m_computeShader.SetTexture(_kernelScan, ColorEquirectID, _colorRT);
            m_computeShader.SetTexture(_kernelScan, DepthEquirectID, _depthRT);
            
            // depth texture from Meta global 
            m_computeShader.SetTextureFromGlobal(_kernelScan, DepthTexGlobalName, DepthTexGlobalName);


            m_computeShader.Dispatch(_kernelScan, groupsX, groupsY, 1);
        }
        // Tracking mode
        else
        {
            // step 0: clear per-frame accumulator
            m_computeShader.SetBuffer(_kernelClear, DepthAccumID, _depthAccum);
            m_computeShader.Dispatch(_kernelClear, groupsX, groupsY, 1);

            // step 1: scatter. reproject each stored point, InterlockedMin the result
            m_computeShader.SetTexture(_kernelScatter, DepthEquirectID, _depthRT);
            m_computeShader.SetTextureFromGlobal(_kernelScatter, DepthTexGlobalName, DepthTexGlobalName);
            m_computeShader.SetBuffer(_kernelScatter, DepthAccumID, _depthAccum);

            m_computeShader.Dispatch(_kernelScatter, groupsX, groupsY, 1);

            // step 2: resolve. update the winning depth and gather colour from it
            m_computeShader.SetTexture(_kernelResolve, ColorTexID, colorTex);
            m_computeShader.SetTexture(_kernelResolve, ColorEquirectID, _colorRT);
            m_computeShader.SetTexture(_kernelResolve, DepthEquirectID, _depthRT);
            m_computeShader.SetBuffer(_kernelResolve, DepthAccumID, _depthAccum);

            m_computeShader.Dispatch(_kernelResolve, groupsX, groupsY, 1);

        }
        

        // Measure the depth range of the updated panorama so the preview can normalise by it
        if (_depthVizMat != null)
        {
            _depthRange.SetData(DepthRangeReset);
            m_computeShader.SetTexture(_kernelRange, DepthEquirectID, _depthRT);
            m_computeShader.SetBuffer(_kernelRange, DepthRangeID, _depthRange);
            m_computeShader.Dispatch(_kernelRange, groupsX, groupsY, 1);

            CollectDepthRange();
            if (!_depthRangeRequest.HasValue)
                _depthRangeRequest = AsyncGPUReadback.Request(_depthRange);
        }

        // Refresh the color preview from the updated panorama (unseen -> magenta)
        if (_colorDisplayRT != null && _colorVizMat != null)
            Graphics.Blit(_colorRT, _colorDisplayRT, _colorVizMat);

        // Refresh the greyscale depth preview from the updated metric depth map
        if (_depthDisplayRT != null && _depthVizMat != null)
        {
            _depthVizMat.SetFloat(MaxDepthID, _measuredMaxDepth > 0f ? _measuredMaxDepth : m_depthPreviewMaxMeters);
            Graphics.Blit(_depthRT, _depthDisplayRT, _depthVizMat);
        }
    }

    // Pick up a finished range readback, if there is one -> max == 0 means no valid texel was found
    private void CollectDepthRange()
    {
        if (!_depthRangeRequest.HasValue || !_depthRangeRequest.Value.done) return;

        if (!_depthRangeRequest.Value.hasError)
        {
            float measuredMax = _depthRangeRequest.Value.GetData<float>()[1];
            if (measuredMax > 0f) _measuredMaxDepth = measuredMax;
        }
        _depthRangeRequest = null;
    }

    /// <summary>Reset both panoramas to empty.</summary>
    public void ResetMaps()
    {
        if (_colorRT != null) ClearRT(_colorRT, Color.clear);
        if (_depthRT != null) ClearRT(_depthRT, Color.clear);

        // Reset the previews too, or a rescan keeps showing the previous scan until
        // the next dispatch lands.
        if (_colorDisplayRT != null) ClearRT(_colorDisplayRT, Color.magenta);
        if (_depthDisplayRT != null) ClearRT(_depthDisplayRT, Color.black);

        _state = State.Idle;
        IsReady = false;
        _dispatchPending = false;   // drop any dispatch Update() queued for the maps we just cleared
        _measuredMaxDepth = 0f;     // the range we measured belongs to the scan we just threw away
    }

    private void OnDestroy()
    {
        if (_colorRT != null) { _colorRT.Release(); Destroy(_colorRT); }
        if (_depthRT != null) { _depthRT.Release(); Destroy(_depthRT); }
        if (_colorDisplayRT != null) { _colorDisplayRT.Release(); Destroy(_colorDisplayRT); }
        if (_depthDisplayRT != null) { _depthDisplayRT.Release(); Destroy(_depthDisplayRT); }
        if (_colorVizMat != null) Destroy(_colorVizMat);
        if (_depthVizMat != null) Destroy(_depthVizMat);
        if (_depthAccum != null) { _depthAccum.Release(); _depthAccum = null; }

        if (_depthRangeRequest.HasValue) { _depthRangeRequest.Value.WaitForCompletion(); _depthRangeRequest = null; }
        if (_depthRange != null) { _depthRange.Release(); _depthRange = null; }
    }
}
