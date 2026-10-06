using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Captura puntual dels dos panorames per les figures de la memòria.
///
/// Un botó = una captura etiquetada. Cada captura escriu:
///   color_01_baseline.png   — panorama de color, revelat amb exposició i gamma FIXES
///   depth_01_baseline.png   — panorama de profunditat, rang de metres FIX
///   shots.csv               — shot, etiqueta, temps, frame, posició avatar, SH banda 0
///
/// Mentre captura, mostra un rètol gran al HUD ("SHOT 04 · light_off") durant uns segons.
/// Com que el passthrough el composita Meta fora de l'eye buffer de Unity, la captura
/// de l'avatar ha de sortir del vídeo de casting; aquest rètol és el que et permet
/// aparellar el fotograma del vídeo amb els PNG guardats, sense mirar timestamps.
///
/// No cal cap canvi als teus scripts: fa servir ColorEquirect i DepthEquirect, que ja
/// són públics, i fa el revelat a CPU per garantir que totes les captures de la sessió
/// són comparables entre elles.
///
/// Botons per defecte:
///   B (dret)      → captura i avança a l'etiqueta següent
///   X (esquerre)  → captura repetint l'etiqueta actual (sufix _b, _c, ...)
/// </summary>
public class EnvMapSnapshot : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private EnvironmentMapReconstructor m_reconstructor;
    [Tooltip("Opcional. Per registrar la posició de l'avatar al CSV.")]
    [SerializeField] private Transform m_avatar;
    [Tooltip("Rètol gran del HUD. Ha de ser ben visible al vídeo de casting.")]
    [SerializeField] private TMPro.TMP_Text m_hud;

    [Header("Sessió")]
    [SerializeField] private string m_sessionTag = "sessio1";

    [Tooltip("Etiquetes en l'ordre del protocol. El botó B les consumeix una a una.")]
    [SerializeField]
    private string[] m_labels =
    {
        "baseline",
        "light_off",
        "light_on",
        "red_in",
        "red_out",
        "obj_far",
        "obj_near",
        "obj_out"
    };

    [Header("Revelat del mapa de color (FIX per tota la sessió)")]
    [Tooltip("Multiplicador abans del gamma. Puja'l si les captures surten fosques. " +
             "NO el canviïs entre captures d'una mateixa figura.")]
    [SerializeField] private float m_exposure = 1f;
    [Tooltip("Gamma de sortida. 2.2 converteix de lineal a sRGB.")]
    [SerializeField] private float m_gamma = 2.2f;
    [Tooltip("Color per les direccions encara no vistes (alpha = 0 al panorama).")]
    [SerializeField] private Color32 m_colorUnseen = new Color32(255, 0, 255, 255);

    [Header("Mapa de profunditat (rang FIX per tota la sessió)")]
    [Tooltip("Metres que es mapegen a negre. Blanc = 0 m. Deixa'l igual a totes les " +
             "captures o les parelles abans/després no seran comparables.")]
    [SerializeField] private float m_depthMaxMeters = 5f;
    [Tooltip("Color per les direccions sense profunditat mesurada (valor 0).")]
    [SerializeField] private Color32 m_depthUnseen = new Color32(20, 30, 90, 255);
    [Tooltip("Inverteix l'escala: si està marcat, a prop = negre i lluny = blanc.")]
    [SerializeField] private bool m_depthInvert = false;

    [Header("HUD")]
    [SerializeField] private float m_flashSeconds = 3f;

    [Header("Botons")]
    [SerializeField] private OVRInput.Button m_shotButton  = OVRInput.Button.Two;    // B
    [SerializeField] private OVRInput.Button m_extraButton = OVRInput.Button.Three;  // X

    // ── Intern ───────────────────────────────────────────────────────────────
    public int ShotCount => _shot;
    public string SessionDir => _dir;

    private string _dir;
    private string _csvPath;
    private int    _shot;
    private int    _labelIndex;
    private int    _repeatCount;
    private float  _flashUntil;
    private string _flashText = "";
    private int    _pending;

    private Texture2D _colorTex, _depthTex;
    private byte[]    _pixels;

    private void Start()
    {
#if !UNITY_EDITOR
        OpenSession();
#endif
    }

    private void Update()
    {
#if UNITY_EDITOR
        return;
#else
        if (OVRInput.GetDown(m_shotButton))  Capture(advance: true);
        if (OVRInput.GetDown(m_extraButton)) Capture(advance: false);
        UpdateHud();
#endif
    }

    // -------------------------------------------------------------------------
    private void OpenSession()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
        _dir = Path.Combine(Application.persistentDataPath, "Snapshots", $"{stamp}_{Sanitize(m_sessionTag)}");
        Directory.CreateDirectory(_dir);

        _csvPath = Path.Combine(_dir, "shots.csv");
        File.WriteAllText(_csvPath,
            "shot,label,t_s,frame,state,drift_m,avatar_x,avatar_y,avatar_z,sh0_r,sh0_g,sh0_b\n");

        // Deixa constància dels paràmetres de revelat: han de ser els mateixos a tota la sessió.
        File.WriteAllText(Path.Combine(_dir, "settings.txt"),
            $"exposure={m_exposure}\ngamma={m_gamma}\ndepthMaxMeters={m_depthMaxMeters}\n" +
            $"depthInvert={m_depthInvert}\ndate={DateTime.Now:O}\n");

        Debug.Log($"[EnvMapSnapshot] Sessió: {_dir}");
    }

    /// <summary>Captura els dos panorames amb l'etiqueta actual.</summary>
    public void Capture(bool advance)
    {
        if (m_reconstructor == null) { Debug.LogError("[EnvMapSnapshot] Falta el reconstructor."); return; }
        if (string.IsNullOrEmpty(_dir)) OpenSession();

        var color = m_reconstructor.ColorEquirect;
        var depth = m_reconstructor.DepthEquirect;
        if (color == null) { Debug.LogWarning("[EnvMapSnapshot] Els panorames encara no existeixen."); return; }

        string label = CurrentLabel();
        if (!advance)
        {
            _repeatCount++;
            label += "_" + (char)('b' + Mathf.Min(_repeatCount - 1, 23));
        }
        else
        {
            _repeatCount = 0;
            _labelIndex++;
        }

        _shot++;
        int shot = _shot;

        WriteCsvRow(shot, label);
        Flash($"SHOT {shot:D2}   {label}");

        GrabColor(color, $"color_{shot:D2}_{label}.png");
        if (depth != null) GrabDepth(depth, $"depth_{shot:D2}_{label}.png");

        Debug.Log($"[EnvMapSnapshot] shot {shot} · {label}");
    }

    // ── Color: ARGBHalf lineal → exposició → gamma → PNG ─────────────────────
    private void GrabColor(RenderTexture rt, string fileName)
    {
        int w = rt.width, h = rt.height;
        _pending++;
        AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBAFloat, req =>
        {
            _pending--;
            if (req.hasError) { Debug.LogWarning($"[EnvMapSnapshot] readback color fallit: {fileName}"); return; }

            var src = req.GetData<float>();
            var dst = Pixels(w * h * 4);
            float invGamma = 1f / Mathf.Max(0.01f, m_gamma);

            for (int i = 0, p = 0; i < w * h; i++, p += 4)
            {
                float a = src[p + 3];
                if (a < 0.5f)                      // direcció no vista encara
                {
                    dst[p]     = m_colorUnseen.r;
                    dst[p + 1] = m_colorUnseen.g;
                    dst[p + 2] = m_colorUnseen.b;
                    dst[p + 3] = 255;
                    continue;
                }
                dst[p]     = ToByte(src[p]     * m_exposure, invGamma);
                dst[p + 1] = ToByte(src[p + 1] * m_exposure, invGamma);
                dst[p + 2] = ToByte(src[p + 2] * m_exposure, invGamma);
                dst[p + 3] = 255;
            }

            WritePng(ref _colorTex, w, h, dst, fileName);
        });
    }

    // ── Profunditat: RFloat en metres → gris amb rang fix → PNG ──────────────
    private void GrabDepth(RenderTexture rt, string fileName)
    {
        int w = rt.width, h = rt.height;
        _pending++;
        AsyncGPUReadback.Request(rt, 0, TextureFormat.RFloat, req =>
        {
            _pending--;
            if (req.hasError) { Debug.LogWarning($"[EnvMapSnapshot] readback depth fallit: {fileName}"); return; }

            var src = req.GetData<float>();
            var dst = Pixels(w * h * 4);
            float maxM = Mathf.Max(0.01f, m_depthMaxMeters);

            for (int i = 0, p = 0; i < w * h; i++, p += 4)
            {
                float r = src[i];
                if (r <= 0f)                       // sense mesura
                {
                    dst[p]     = m_depthUnseen.r;
                    dst[p + 1] = m_depthUnseen.g;
                    dst[p + 2] = m_depthUnseen.b;
                    dst[p + 3] = 255;
                    continue;
                }
                float t = Mathf.Clamp01(r / maxM);          // 0 = a tocar, 1 = maxM o més lluny
                byte  g = (byte)(255f * (m_depthInvert ? t : 1f - t));
                dst[p] = dst[p + 1] = dst[p + 2] = g;
                dst[p + 3] = 255;
            }

            WritePng(ref _depthTex, w, h, dst, fileName);
        });
    }

    private static byte ToByte(float linear, float invGamma)
    {
        float c = Mathf.Pow(Mathf.Max(0f, linear), invGamma);
        return (byte)(Mathf.Clamp01(c) * 255f + 0.5f);
    }

    private void WritePng(ref Texture2D cache, int w, int h, byte[] data, string fileName)
    {
        if (cache == null || cache.width != w || cache.height != h)
        {
            if (cache != null) Destroy(cache);
            cache = new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        }
        cache.LoadRawTextureData(data);
        cache.Apply(false);
        File.WriteAllBytes(Path.Combine(_dir, fileName), cache.EncodeToPNG());
    }

    private byte[] Pixels(int n)
    {
        if (_pixels == null || _pixels.Length != n) _pixels = new byte[n];
        return _pixels;
    }

    // ── CSV i HUD ────────────────────────────────────────────────────────────
    private void WriteCsvRow(int shot, string label)
    {
        var ic = CultureInfo.InvariantCulture;
        Vector3 a = m_avatar != null ? m_avatar.position : Vector3.zero;
        string st = m_reconstructor.IsScanning ? "scanning"
                  : m_reconstructor.IsTracking ? "tracking" : "idle";
        var sh = RenderSettings.ambientProbe;

        File.AppendAllText(_csvPath, string.Format(ic,
            "{0},{1},{2:F2},{3},{4},{5:F3},{6:F3},{7:F3},{8:F3},{9:F6},{10:F6},{11:F6}\n",
            shot, label, Time.unscaledTime, Time.frameCount, st,
            m_reconstructor.ScanDrift, a.x, a.y, a.z, sh[0, 0], sh[1, 0], sh[2, 0]));
    }

    private string CurrentLabel()
    {
        if (m_labels != null && _labelIndex < m_labels.Length) return m_labels[_labelIndex];
        return $"extra_{_shot + 1}";
    }

    private string NextLabelPreview()
    {
        if (m_labels != null && _labelIndex < m_labels.Length) return m_labels[_labelIndex];
        return "extra";
    }

    private void Flash(string text)
    {
        _flashText  = text;
        _flashUntil = Time.unscaledTime + m_flashSeconds;
    }

    private void UpdateHud()
    {
        if (m_hud == null) return;
        string s = Time.unscaledTime < _flashUntil
            ? _flashText
            : $"shots: {_shot}   següent: {NextLabelPreview()}" + (_pending > 0 ? "   (guardant...)" : "");
        if (m_hud.text != s) m_hud.text = s;
    }

    private static string Sanitize(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return string.IsNullOrEmpty(s) ? "sessio" : s;
    }

    private void OnDisable() => AsyncGPUReadback.WaitAllRequests();

    private void OnDestroy()
    {
        if (_colorTex != null) Destroy(_colorTex);
        if (_depthTex != null) Destroy(_depthTex);
    }
}