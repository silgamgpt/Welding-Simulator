using System;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Measures distance between a welding torch tip and a metal plate surface.
/// Welding is allowed ONLY when distance is within [minDistanceMm, maxDistanceMm].
/// Outside the range (or no surface detected), emits warning events.
/// </summary>
public sealed class DistanceChecker : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Torch tip position (ray origin).")]
    [SerializeField] private Transform torchTip;

    [Tooltip("Ray direction transform. If null, torchTip.forward is used.")]
    [SerializeField] private Transform rayDirection;

    [Header("Detection")]
    [Tooltip("Only surfaces on these layers are considered 'metal plate'.")]
    [SerializeField] private LayerMask metalLayerMask = ~0;

    [Tooltip("Max raycast distance in meters.")]
    [SerializeField] private float maxRayDistanceMeters = 0.05f; // 50mm

    [Tooltip("Optional: require collider tag match (leave empty to ignore tag).")]
    [SerializeField] private string requiredTag = "";

    [Header("Distance Threshold (mm)")]
    [SerializeField] private float minDistanceMm = 3f;
    [SerializeField] private float maxDistanceMm = 5f;

    [Header("Output / Control")]
    [Tooltip("Optional: enable this behaviour only when within range.")]
    [SerializeField] private Behaviour weldingBehaviourToToggle;

    [Tooltip("Throttle warnings (seconds). 0 = no throttle.")]
    [SerializeField] private float warningCooldownSeconds = 0.25f;

    [Header("Events")]
    public UnityEvent<float> OnDistanceMeasuredMm;
    public UnityEvent<float> OnWithinRangeMm;
    public UnityEvent<float> OnOutOfRangeMm;
    public UnityEvent OnNoSurfaceDetected;

    /// <summary>Latest measured distance (mm). Null when no surface detected.</summary>
    public float? CurrentDistanceMm { get; private set; }

    /// <summary>True only when surface is detected and distance is within threshold.</summary>
    public bool IsWithinRange { get; private set; }

    private bool _hadSurfaceLastFrame;
    private bool _withinRangeLastFrame;
    private float _lastWarningTime;

    private void Reset()
    {
        torchTip = transform;
        rayDirection = transform;
        metalLayerMask = ~0;
        maxRayDistanceMeters = 0.05f;
        minDistanceMm = 3f;
        maxDistanceMm = 5f;
        warningCooldownSeconds = 0.25f;
    }

    private void Awake()
    {
        if (torchTip == null) torchTip = transform;
        if (rayDirection == null) rayDirection = torchTip;
        ValidateConfig();
    }

    private void OnValidate()
    {
        if (maxRayDistanceMeters < 0f) maxRayDistanceMeters = 0f;
        if (minDistanceMm < 0f) minDistanceMm = 0f;
        if (maxDistanceMm < 0f) maxDistanceMm = 0f;
        if (warningCooldownSeconds < 0f) warningCooldownSeconds = 0f;
        ValidateConfig();
    }

    private void ValidateConfig()
    {
        if (maxDistanceMm < minDistanceMm)
        {
            // Keep user intent: swap if entered backwards.
            (minDistanceMm, maxDistanceMm) = (maxDistanceMm, minDistanceMm);
        }
    }

    private void Update()
    {
        MeasureAndUpdateState();
        ApplyWeldingToggle();
        EmitEvents();
    }

    /// <summary>
    /// Convenience API for other scripts (e.g. WeldingController) to query.
    /// </summary>
    public bool CanWeld() => IsWithinRange;

    private void MeasureAndUpdateState()
    {
        var origin = torchTip.position;
        var dir = (rayDirection != null ? rayDirection.forward : torchTip.forward);

        bool hitSomething = Physics.Raycast(
            origin,
            dir,
            out RaycastHit hit,
            maxRayDistanceMeters,
            metalLayerMask,
            QueryTriggerInteraction.Ignore
        );

        if (!hitSomething)
        {
            CurrentDistanceMm = null;
            IsWithinRange = false;
            return;
        }

        if (!string.IsNullOrEmpty(requiredTag) && !hit.collider.CompareTag(requiredTag))
        {
            CurrentDistanceMm = null;
            IsWithinRange = false;
            return;
        }

        float distanceMm = hit.distance * 1000f;
        CurrentDistanceMm = distanceMm;
        IsWithinRange = distanceMm >= minDistanceMm && distanceMm <= maxDistanceMm;
    }

    private void ApplyWeldingToggle()
    {
        if (weldingBehaviourToToggle == null) return;
        weldingBehaviourToToggle.enabled = IsWithinRange;
    }

    private void EmitEvents()
    {
        // Distance event
        if (CurrentDistanceMm.HasValue)
        {
            OnDistanceMeasuredMm?.Invoke(CurrentDistanceMm.Value);
        }

        bool hasSurface = CurrentDistanceMm.HasValue;

        // State transitions
        if (!hasSurface)
        {
            if (_hadSurfaceLastFrame)
            {
                // Just lost surface
                EmitWarningThrottled(() => OnNoSurfaceDetected?.Invoke());
            }
            else
            {
                // Still no surface: optional, but keep throttled warnings
                EmitWarningThrottled(() => OnNoSurfaceDetected?.Invoke());
            }
        }
        else
        {
            float mm = CurrentDistanceMm!.Value;
            if (IsWithinRange)
            {
                if (!_withinRangeLastFrame)
                {
                    OnWithinRangeMm?.Invoke(mm);
                }
            }
            else
            {
                // Out of range
                EmitWarningThrottled(() => OnOutOfRangeMm?.Invoke(mm));
            }
        }

        _hadSurfaceLastFrame = hasSurface;
        _withinRangeLastFrame = IsWithinRange;
    }

    private void EmitWarningThrottled(Action emit)
    {
        if (warningCooldownSeconds <= 0f)
        {
            emit?.Invoke();
            return;
        }

        if (Time.time - _lastWarningTime < warningCooldownSeconds) return;
        _lastWarningTime = Time.time;
        emit?.Invoke();
    }

    private void OnDrawGizmosSelected()
    {
        if (torchTip == null) return;

        var origin = torchTip.position;
        var dir = (rayDirection != null ? rayDirection.forward : torchTip.forward);

        Gizmos.color = IsWithinRange ? Color.green : Color.red;
        Gizmos.DrawRay(origin, dir.normalized * Mathf.Max(0f, maxRayDistanceMeters));
    }
}

