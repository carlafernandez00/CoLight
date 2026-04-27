using UnityEngine;
using TMPro;

/// <summary>
/// Receives luminance values from LightEstimationManager and fires events when
/// the brightness crosses the configured thresholds. Also changes an optional
/// scene Light colour so you can see at a glance whether the camera is working.
///
/// Wire up: LightEstimationManager.m_onBrightnessChange → OnChangeBrightness
/// </summary>
public class BrightnessEstimationDebugger : MonoBehaviour
{
    [SerializeField] private TMP_Text m_debugger;

    [Tooltip("Luminance in [0,1]. Below this → too dark.")]
    [Range(0, 1)][SerializeField] private float m_minBrightnessLevel = 0.1f;

    [Tooltip("Luminance in [0,1]. Above this → too bright.")]
    [Range(0, 1)][SerializeField] private float m_maxBrightnessLevel = 0.20f;

    [Header("Light Indicator")]
    [Tooltip("Optional scene Light whose colour changes to reflect the brightness state.")]
    [SerializeField] private Light m_indicatorLight;
    [SerializeField] private Color m_darkColor  = new Color(0.1f, 0.1f, 0.5f);  // dark blue = too dark
    [SerializeField] private Color m_brightColor = Color.white;                  // white = normal/bright

    private int m_isDark = 2;   // 2 = dark state, 1 = bright state (matches original logic)
    private string m_brightnessStatus = "";

    public void OnChangeBrightness(float value)
    {
        if (value <= m_minBrightnessLevel && m_isDark != 2)
        {
            TooDark();
            m_isDark = 2;
        }
        else if (value >= m_maxBrightnessLevel && m_isDark != 1)
        {
            TooLight();
            m_isDark = 1;
        }

        if (m_debugger != null)
            m_debugger.text = $"Luminance: {value:F3}\n\n{m_brightnessStatus}";
    }

    public void TooDark()
    {
        m_brightnessStatus = "TOO DARK — turn lights on!";
        if (m_indicatorLight != null)
            m_indicatorLight.color = m_darkColor;
    }

    public void TooLight()
    {
        m_brightnessStatus = "BRIGHT — lights ok.";
        if (m_indicatorLight != null)
            m_indicatorLight.color = m_brightColor;
    }
}
