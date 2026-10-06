using TMPro;
using UnityEngine;

/// <summary>
/// Col·loca tota la UI de debug per codi, amb números explícits, i canvia de disposició
/// segons l'estat del reconstructor.
///
///   Scanning → els dos panorames grans, un sobre l'altre, centrats
///   Tracking → els dos panorames petits, un al costat de l'altre, a dalt
///
/// Substitueix DebugPanelsLayout. No cal cap SlotScan / SlotTrack: esborra'ls, i esborra
/// també el component DebugPanelsLayout de l'escena.
///
/// Posa aquest component a qualsevol GameObject (p. ex. el mateix DebugPannels) i assigna-li
/// els quatre elements de UI. Configura mides, posicions, fonts i contorns a Awake, així que
/// el que tinguis posat a l'inspector dels RawImage i dels TMP no importa.
///
/// Les coordenades són en píxels del Canvas. Amb el teu canvas de 630 x 851, el centre és
/// (0,0), la vora dreta +315, la vora superior +425.
/// </summary>
public class DebugHudLayout : MonoBehaviour
{
    public enum Mode { Auto, Big, Small, Hidden }

    [Header("References")]
    [SerializeField] private EnvironmentMapReconstructor m_reconstructor;
    [Tooltip("El RawImage del panorama de color.")]
    [SerializeField] private RectTransform m_colorPreview;
    [Tooltip("El RawImage del panorama de profunditat.")]
    [SerializeField] private RectTransform m_depthPreview;
    [Tooltip("El TMP d'estat del reconstructor (scanning / drift / tracking).")]
    [SerializeField] private TMP_Text m_statusText;
    [Tooltip("El TMP del rètol de captura. És el que has d'assignar també al camp Hud de EnvMapSnapshot.")]
    [SerializeField] private TMP_Text m_shotLabel;

    [Header("Mode")]
    [SerializeField] private Mode m_mode = Mode.Auto;
    [Tooltip("Botó per cicrar Auto → Big → Small → Hidden. Posa'l a None si no el vols.")]
    [SerializeField] private OVRInput.Button m_cycleButton = OVRInput.Button.None;

    [Header("SCANNING — panorames grans, un sobre l'altre")]
    [SerializeField] private Vector2 m_bigPanelSize  = new Vector2(420f, 210f);
    [SerializeField] private Vector2 m_bigColorPos   = new Vector2(0f, 120f);
    [SerializeField] private Vector2 m_bigDepthPos   = new Vector2(0f, -120f);
    [SerializeField] private Vector2 m_bigStatusPos  = new Vector2(0f, 300f);
    [SerializeField] private float   m_bigStatusFont = 28f;

    [Header("TRACKING — panorames petits, un al costat de l'altre")]
    [SerializeField] private Vector2 m_smallPanelSize = new Vector2(190f, 95f);
    [SerializeField] private Vector2 m_smallColorPos  = new Vector2(-100f, 330f);
    [SerializeField] private Vector2 m_smallDepthPos  = new Vector2(100f, 330f);
    [SerializeField] private Vector2 m_smallStatusPos = new Vector2(0f, 262f);
    [SerializeField] private float   m_smallStatusFont = 18f;

    [Header("Rètol de captura (fix, sempre visible)")]
    [SerializeField] private Vector2 m_shotLabelPos  = new Vector2(0f, -340f);
    [SerializeField] private Vector2 m_shotLabelSize = new Vector2(600f, 90f);
    [SerializeField] private float   m_shotLabelFont = 48f;

    [Header("Transició")]
    [Tooltip("Segons d'interpolació entre estats. 0 = instantani.")]
    [SerializeField] private float m_lerpSeconds = 0.3f;

    public Mode CurrentMode => m_mode;

    private void Awake()
    {
        // El rètol de captura es configura un cop i no es mou mai més.
        if (m_shotLabel != null)
        {
            SetupRect(m_shotLabel.rectTransform, m_shotLabelSize, m_shotLabelPos);
            SetupText(m_shotLabel, m_shotLabelFont);
            m_shotLabel.gameObject.SetActive(true);
            m_shotLabel.enabled = true;
            if (string.IsNullOrEmpty(m_shotLabel.text)) m_shotLabel.text = "llest";
        }

        if (m_statusText != null) SetupText(m_statusText, m_bigStatusFont);

        Apply(1f);   // snap inicial
    }

    private void Update()
    {
        if (m_cycleButton != OVRInput.Button.None && OVRInput.GetDown(m_cycleButton))
        {
            m_mode = (Mode)(((int)m_mode + 1) % 4);
            Debug.Log($"[DebugHudLayout] {m_mode}");
        }

        float k = m_lerpSeconds <= 0f
            ? 1f
            : 1f - Mathf.Exp(-Time.deltaTime / (m_lerpSeconds * 0.4f));

        Apply(k);
    }

    private void Apply(float k)
    {
        Mode effective = m_mode;
        if (effective == Mode.Auto)
        {
            bool tracking = m_reconstructor != null && m_reconstructor.IsTracking;
            effective = tracking ? Mode.Small : Mode.Big;
        }

        bool visible = effective != Mode.Hidden;
        bool big     = effective == Mode.Big;

        Vector2 size      = big ? m_bigPanelSize  : m_smallPanelSize;
        Vector2 colorPos  = big ? m_bigColorPos   : m_smallColorPos;
        Vector2 depthPos  = big ? m_bigDepthPos   : m_smallDepthPos;
        Vector2 statusPos = big ? m_bigStatusPos  : m_smallStatusPos;
        float   statusFnt = big ? m_bigStatusFont : m_smallStatusFont;

        MoveTo(m_colorPreview, size, colorPos, visible, k);
        MoveTo(m_depthPreview, size, depthPos, visible, k);

        if (m_statusText != null)
        {
            MoveTo(m_statusText.rectTransform, new Vector2(600f, 60f), statusPos, visible, k);
            m_statusText.fontSize = Mathf.Lerp(m_statusText.fontSize, statusFnt, k);
        }
    }

    private static void MoveTo(RectTransform rt, Vector2 size, Vector2 pos, bool visible, float k)
    {
        if (rt == null) return;
        rt.gameObject.SetActive(visible);
        if (!visible) return;

        CenterAnchors(rt);
        rt.sizeDelta        = Vector2.Lerp(rt.sizeDelta, size, k);
        rt.anchoredPosition = Vector2.Lerp(rt.anchoredPosition, pos, k);
        rt.localScale       = Vector3.one;
    }

    private static void SetupRect(RectTransform rt, Vector2 size, Vector2 pos)
    {
        if (rt == null) return;
        CenterAnchors(rt);
        rt.sizeDelta        = size;
        rt.anchoredPosition = pos;
        rt.localScale       = Vector3.one;
    }

    private static void CenterAnchors(RectTransform rt)
    {
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot     = new Vector2(0.5f, 0.5f);
        rt.localRotation = Quaternion.identity;
    }

    // Text blanc amb contorn negre: sobre passthrough el blanc sol es perd.
    private static void SetupText(TMP_Text t, float fontSize)
    {
        if (t == null) return;
        t.fontSize            = fontSize;
        t.enableAutoSizing    = false;
        t.textWrappingMode    = TextWrappingModes.NoWrap;
        t.overflowMode        = TextOverflowModes.Overflow;
        t.alignment           = TextAlignmentOptions.Center;
        t.color               = Color.white;
        t.outlineColor        = Color.black;
        t.outlineWidth        = 0.25f;
    }
}