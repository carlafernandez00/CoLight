using Meta.XR.EnvironmentDepth;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Wraps EnvironmentDepthManager and exposes the depth texture for other components.
/// Also blits the left-eye depth slice to an optional RawImage for debug visualization.
///
/// Wire up: add EnvironmentDepthManager to the scene, assign it to m_depthManager.
/// For the debug preview, assign a RawImage to m_depthPreview — it will show the depth
/// map as greyscale (white = near, black = far).
///
/// Depth texture properties (set globally by EnvironmentDepthManager each frame):
///   - Format:        R16_UNorm — single channel, 16-bit, values in 0–1
///   - Type:          Texture2DArray with 2 slices (left eye = 0, right eye = 1)
///   - Depth encoding: reversed-Z — ~1.0 = near, ~0.0 = far (not linear metres)
///   - Resolution:    ~200×200 px — low resolution but sufficient for depth sensing
///   - Update rate:   tied to Application.onBeforeRender, updates every frame automatically
///
/// Note: depth is not available in the Unity Editor unless you have a Quest 3 connected
/// over Meta Quest Link with "Spatial Data over Meta Quest Link" enabled in Link settings.
/// </summary>
public class EnvironmentDepthReader : MonoBehaviour
{
    [SerializeField] private EnvironmentDepthManager m_depthManager;

    [Header("Debug Preview")]
    [Tooltip("Optional RawImage that shows a greyscale depth map (left eye, white = near).")]
    [SerializeField] private RawImage m_depthPreview;

    /// <summary>True once the depth provider is ready and delivering frames.</summary>
    public bool IsDepthAvailable => m_depthManager != null && m_depthManager.IsDepthAvailable;

    private RenderTexture _previewRT;
    private Material _visualizeMat;
    private static readonly int DepthTextureID = Shader.PropertyToID("_EnvironmentDepthTexture");

    private void Awake()
    {
#if UNITY_EDITOR
        // Suppress "Depth not supported" error spam in the editor.
        if (m_depthManager != null)
            m_depthManager.enabled = false;
#endif
    }

    private void Start()
    {
#if UNITY_EDITOR
        if (m_depthPreview != null)
            m_depthPreview.gameObject.SetActive(false);
        Debug.Log("[EnvironmentDepthReader] Editor mode — depth unavailable without Quest Link + Spatial Data.");
        return;
#endif
        var shader = Shader.Find("DepthVisualize");
        if (shader == null)
        {
            Debug.LogError("[EnvironmentDepthReader] Shader 'DepthVisualize' not found. " +
                           "Make sure DepthVisualize.shader is inside Assets/Resources/.");
            return;
        }
        _visualizeMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
    }

    private void Update()
    {
#if UNITY_EDITOR
        return;
#endif
        if (!IsDepthAvailable || _visualizeMat == null || m_depthPreview == null) return;

        // EnvironmentDepthManager writes _EnvironmentDepthTexture in OnBeforeRender(),
        // which runs before Update() — so by the time we get here the texture is already current.
        var depthTex = Shader.GetGlobalTexture(DepthTextureID) as RenderTexture;
        if (depthTex == null) return;

        // Recreate preview RT only when the depth texture resolution changes (~200×200 on device).
        if (_previewRT == null || _previewRT.width != depthTex.width || _previewRT.height != depthTex.height)
        {
            if (_previewRT != null) _previewRT.Release();
            _previewRT = new RenderTexture(depthTex.width, depthTex.height, 0, RenderTextureFormat.ARGB32);
            m_depthPreview.texture = _previewRT;
        }

        // DepthVisualize.shader reads _EnvironmentDepthTexture (Texture2DArray) directly as a global,
        // samples slice 0 (left eye), and outputs greyscale: white = near (~1.0), black = far (~0.0).
        // null source is intentional — the shader doesn't use _MainTex.
        // Both eyes are computed by the system simultaneously — slice 1 (right eye) is there and accessible.
        Graphics.Blit(null, _previewRT, _visualizeMat);
    }

    private void OnDestroy()
    {
        if (_previewRT != null) { _previewRT.Release(); Destroy(_previewRT); }
        if (_visualizeMat != null) Destroy(_visualizeMat);
    }
}
