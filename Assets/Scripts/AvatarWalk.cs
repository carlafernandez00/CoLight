using UnityEngine;

/// <summary>
/// Fa caminar l'avatar entre dos punts, amb la posició sempre acotada dins del volum
/// de light probes. L'animació ha de ser "in place" (sense root motion): aquest script
/// és qui mou el transform.
///
/// Per defecte fa un recorregut LATERAL davant teu. És el millor per les figures:
/// la distància a la càmera es manté constant, així que l'enquadrament no canvia entre
/// fotogrames, i l'avatar travessa el camp de probes de banda a banda.
///
/// El clamp és la part important: si l'avatar surt de l'envolupant de les probes, Unity
/// extrapola la interpolació i el resultat deixa de tenir sentit.
/// </summary>
public class AvatarWalkPath : MonoBehaviour
{
    [Header("Trajectòria (coordenades de món)")]
    [Tooltip("Extrem A del recorregut.")]
    [SerializeField] private Vector3 m_pointA = new Vector3(-0.8f, 0f, 0.9f);
    [Tooltip("Extrem B del recorregut.")]
    [SerializeField] private Vector3 m_pointB = new Vector3(0.8f, 0f, 0.9f);

    [Tooltip("Velocitat en m/s. 0.4 és un caminar lent, bo per vídeo.")]
    [SerializeField] private float m_speed = 0.4f;

    [Tooltip("Segons d'aturada a cada extrem. Et dona fotogrames nets als dos límits.")]
    [SerializeField] private float m_dwellSeconds = 1.5f;

    [Tooltip("Graus per segon en girar al final de cada trajecte.")]
    [SerializeField] private float m_turnSpeed = 180f;

    [Header("Volum de probes (clamp de seguretat)")]
    [Tooltip("Centre del volum de light probes.")]
    [SerializeField] private Vector3 m_probeBoxCenter = Vector3.zero;
    [Tooltip("Mitja mida del volum de probes en X i Z. Amb probes a ±1.3, posa 1.3.")]
    [SerializeField] private Vector2 m_probeBoxHalfExtents = new Vector2(1.3f, 1.3f);
    [Tooltip("Marge interior. L'avatar mai s'acostarà més que això a la cara del volum.")]
    [SerializeField] private float m_margin = 0.3f;

    [Header("Animator")]
    [Tooltip("Animator de l'avatar. Si és null, s'agafa el d'aquest GameObject.")]
    [SerializeField] private Animator m_animator;
    [Tooltip("Paràmetre float de velocitat de l'Animator. Deixa'l buit si el teu controlador " +
             "només té un clip de caminar en bucle.")]
    [SerializeField] private string m_speedParam = "";

    [Header("Control")]
    [Tooltip("Comença caminant. Si no, es queda quiet fins que cridis StartWalking().")]
    [SerializeField] private bool m_walkOnStart = true;
    [Tooltip("Botó per engegar i aturar el moviment durant la sessió. None per desactivar-ho.")]
    [SerializeField] private OVRInput.Button m_toggleButton = OVRInput.Button.None;

    public bool IsWalking => _walking;
    /// <summary>0 = al punt A, 1 = al punt B. Útil per etiquetar les captures.</summary>
    public float PathT { get; private set; }

    private bool    _walking;
    private Vector3 _from, _to;
    private float   _dwellTimer;

    private void Awake()
    {
        if (m_animator == null) m_animator = GetComponent<Animator>();
        if (m_animator != null) m_animator.applyRootMotion = false;

        _from = Clamp(m_pointA);
        _to   = Clamp(m_pointB);

        transform.position = _from;
        FaceTowards(_to, instant: true);

        _walking = m_walkOnStart;
    }

    private void Update()
    {
        if (m_toggleButton != OVRInput.Button.None && OVRInput.GetDown(m_toggleButton))
            _walking = !_walking;

        if (m_animator != null && !string.IsNullOrEmpty(m_speedParam))
            m_animator.SetFloat(m_speedParam, _walking && _dwellTimer <= 0f ? m_speed : 0f);

        if (!_walking) return;

        if (_dwellTimer > 0f)
        {
            _dwellTimer -= Time.deltaTime;
            FaceTowards(_to, instant: false);
            return;
        }

        Vector3 target = _to;
        Vector3 pos    = transform.position;
        Vector3 delta  = target - pos;
        delta.y = 0f;

        float step = m_speed * Time.deltaTime;

        if (delta.magnitude <= step)
        {
            transform.position = target;

            // Gira el recorregut
            Vector3 tmp = _from; _from = _to; _to = tmp;
            _dwellTimer = m_dwellSeconds;
        }
        else
        {
            transform.position = Clamp(pos + delta.normalized * step);
            FaceTowards(target, instant: false);
        }

        PathT = Vector3.Distance(transform.position, Clamp(m_pointA)) /
                Mathf.Max(0.01f, Vector3.Distance(Clamp(m_pointA), Clamp(m_pointB)));
    }

    private void FaceTowards(Vector3 target, bool instant)
    {
        Vector3 d = target - transform.position;
        d.y = 0f;
        if (d.sqrMagnitude < 1e-6f) return;

        Quaternion want = Quaternion.LookRotation(d);
        transform.rotation = instant
            ? want
            : Quaternion.RotateTowards(transform.rotation, want, m_turnSpeed * Time.deltaTime);
    }

    /// <summary>Retalla la posició perquè no surti del volum de probes menys el marge.</summary>
    private Vector3 Clamp(Vector3 p)
    {
        float hx = Mathf.Max(0f, m_probeBoxHalfExtents.x - m_margin);
        float hz = Mathf.Max(0f, m_probeBoxHalfExtents.y - m_margin);
        p.x = Mathf.Clamp(p.x, m_probeBoxCenter.x - hx, m_probeBoxCenter.x + hx);
        p.z = Mathf.Clamp(p.z, m_probeBoxCenter.z - hz, m_probeBoxCenter.z + hz);
        return p;
    }

    public void StartWalking() => _walking = true;
    public void StopWalking()  => _walking = false;

    // Dibuixa el recorregut i la zona permesa, per comprovar-ho a l'editor.
    private void OnDrawGizmosSelected()
    {
        float hx = Mathf.Max(0f, m_probeBoxHalfExtents.x - m_margin);
        float hz = Mathf.Max(0f, m_probeBoxHalfExtents.y - m_margin);

        Gizmos.color = new Color(0.2f, 0.9f, 0.4f, 0.9f);
        Gizmos.DrawWireCube(m_probeBoxCenter + Vector3.up * 0.9f, new Vector3(hx * 2f, 1.8f, hz * 2f));

        Gizmos.color = Color.cyan;
        Vector3 a = Clamp(m_pointA), b = Clamp(m_pointB);
        Gizmos.DrawLine(a, b);
        Gizmos.DrawWireSphere(a, 0.1f);
        Gizmos.DrawWireSphere(b, 0.1f);
    }
}