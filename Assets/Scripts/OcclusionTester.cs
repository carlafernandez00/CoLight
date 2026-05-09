using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Drives _OcclusionFactor on a target material so you can compare shader approaches
/// without yet having a real occlusion source.
///
/// Works with both:
///   - test.shadergraph material  (property "_OcclusionFactor" → AO slot, affects ambient only)
///   - OcclusionLit.shader material (same property name, affects ambient + optionally direct light)
///
/// Controls (keyboard):
///   Left / Right arrows  — decrease / increase occlusion factor
///   Space                — toggle between 0 (fully occluded) and 1 (fully lit)
///   A                    — toggle auto-animate sine wave
/// </summary>
[RequireComponent(typeof(Renderer))]
public class OcclusionTester : MonoBehaviour
{
    [SerializeField] private Renderer _targetRenderer;

    [Header("Occlusion")]
    [Range(0f, 1f)]
    [SerializeField] private float _occlusionFactor = 1f;

    [Header("Animation")]
    [SerializeField] private bool  _animate;
    [SerializeField] private float _animSpeed = 0.5f;

    private static readonly int OcclusionFactorID = Shader.PropertyToID("_OcclusionFactor");
    private Material _mat;

    private void Awake()
    {
        if (_targetRenderer == null)
            _targetRenderer = GetComponent<Renderer>();

        if (_targetRenderer != null)
            _mat = _targetRenderer.material;
    }

    private void Update()
    {
        HandleInput();

        if (_animate)
            _occlusionFactor = (Mathf.Sin(Time.time * _animSpeed * Mathf.PI) + 1f) * 0.5f;

        Apply();
    }

    private void HandleInput()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb.leftArrowKey.isPressed)
            _occlusionFactor = Mathf.Max(0f, _occlusionFactor - Time.deltaTime);

        if (kb.rightArrowKey.isPressed)
            _occlusionFactor = Mathf.Min(1f, _occlusionFactor + Time.deltaTime);

        if (kb.spaceKey.wasPressedThisFrame)
            _occlusionFactor = _occlusionFactor > 0.5f ? 0f : 1f;

        if (kb.aKey.wasPressedThisFrame)
            _animate = !_animate;
    }

    private void Apply()
    {
        if (_mat != null)
            _mat.SetFloat(OcclusionFactorID, _occlusionFactor);
    }

    /// <summary>Called from other scripts (e.g. when real occlusion data arrives).</summary>
    public void SetOcclusion(float value)
    {
        _occlusionFactor = Mathf.Clamp01(value);
        Apply();
    }

    private void OnValidate()
    {
        if (_mat != null)
            _mat.SetFloat(OcclusionFactorID, _occlusionFactor);
    }
}
