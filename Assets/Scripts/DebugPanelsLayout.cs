using UnityEngine;

/// <summary>
/// Amaga o encongeix els panells de debug (els dos equirect + el status text) quan
/// el reconstructor passa a Tracking, perquè l'avatar es vegi net al casting.
///
///   Scanning → panells grans (els necessites per la figura del test 2)
///   Tracking → panells petits en una cantonada, o amagats del tot
///
/// Configuració SENSE escriure coordenades a mà: crees dos RectTransform buits
/// ("SlotScan" i "SlotTrack") com a germans del pare dels panells, els col·loques
/// i els escales a l'editor on vols cada estat, i els assignes aquí. L'script copia
/// la seva posició i escala.
/// </summary>
public class DebugPanelsLayout : MonoBehaviour
{
    public enum Mode { Auto, Big, Small, Hidden }

    [Header("References")]
    [SerializeField] private EnvironmentMapReconstructor m_reconstructor;

    [Tooltip("Pare dels dos RawImage + el status text. És el que es mou i s'escala.")]
    [SerializeField] private RectTransform m_panels;

    [Tooltip("RectTransform buit col·locat on vols els panells durant el SCANNING (grans, centrats).")]
    [SerializeField] private RectTransform m_slotScan;

    [Tooltip("RectTransform buit col·locat on vols els panells durant el TRACKING (petits, a una cantonada).")]
    [SerializeField] private RectTransform m_slotTrack;

    [Header("Opcions")]
    [Tooltip("En Tracking, amaga els panells del tot en comptes d'encongir-los.")]
    [SerializeField] private bool m_hideInTracking = false;

    [Range(0f, 1f), Tooltip("Opacitat dels panells petits durant el tracking.")]
    [SerializeField] private float m_trackAlpha = 0.85f;

    [Tooltip("Segons de transició. 0 = instantani.")]
    [SerializeField] private float m_lerpSeconds = 0.3f;

    [Tooltip("Botó per cicrar Auto → Big → Small → Hidden durant la sessió.")]
    [SerializeField] private OVRInput.Button m_cycleButton = OVRInput.Button.SecondaryThumbstick;

    public Mode CurrentMode => m_mode;

    private Mode        m_mode = Mode.Auto;
    private CanvasGroup _group;

    private void Awake()
    {
        if (m_panels == null) m_panels = transform as RectTransform;

        _group = m_panels.GetComponent<CanvasGroup>();
        if (_group == null) _group = m_panels.gameObject.AddComponent<CanvasGroup>();

        // Els slots són només referències de col·locació: no s'han de veure.
        if (m_slotScan  != null) m_slotScan.gameObject.SetActive(false);
        if (m_slotTrack != null) m_slotTrack.gameObject.SetActive(false);

        Apply(1f);
    }

    private void Update()
    {
        if (OVRInput.GetDown(m_cycleButton))
        {
            m_mode = (Mode)(((int)m_mode + 1) % 4);
            Debug.Log($"[DebugPanelsLayout] {m_mode}");
        }

        float k = m_lerpSeconds <= 0f
            ? 1f
            : 1f - Mathf.Exp(-Time.deltaTime / (m_lerpSeconds * 0.4f));

        Apply(k);
    }

    private void Apply(float k)
    {
        if (m_panels == null) return;

        Mode effective = m_mode;
        if (effective == Mode.Auto)
        {
            bool tracking = m_reconstructor != null && m_reconstructor.IsTracking;
            effective = !tracking ? Mode.Big : (m_hideInTracking ? Mode.Hidden : Mode.Small);
        }

        RectTransform slot = (effective == Mode.Big) ? m_slotScan : m_slotTrack;
        float alpha = effective == Mode.Hidden ? 0f
                    : effective == Mode.Small  ? m_trackAlpha
                    : 1f;

        if (slot != null)
        {
            m_panels.anchorMin        = slot.anchorMin;
            m_panels.anchorMax        = slot.anchorMax;
            m_panels.pivot            = slot.pivot;
            m_panels.anchoredPosition = Vector2.Lerp(m_panels.anchoredPosition, slot.anchoredPosition, k);
            m_panels.localScale       = Vector3.Lerp(m_panels.localScale, slot.localScale, k);
        }

        _group.alpha          = Mathf.Lerp(_group.alpha, alpha, k);
        _group.blocksRaycasts = alpha > 0.01f;
    }
}