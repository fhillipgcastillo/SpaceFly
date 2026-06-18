using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A range-based source of gravity. Attach to any attractor (planet, moon, station, ...).
///
/// Bodies are only affected while they are within <see cref="radius"/> of the field's center.
/// Detection is a plain world-space distance check, so the radius is independent of this object's
/// transform scale (handy: the globe is uniformly scaled ~5000x, which would otherwise distort a
/// SphereCollider). Drop the same component on as many bodies of as many sizes as you like.
///
/// This component only describes the field and registers itself; <see cref="PlanetGravityBody"/>
/// (the receiver) reads the registry and applies the actual physics to each body.
/// </summary>
[DisallowMultipleComponent]
public class GravityField : MonoBehaviour
{
    public enum FalloffMode
    {
        /// <summary>Full strength everywhere inside the radius, zero outside.</summary>
        Constant,
        /// <summary>Full strength at the center, fading linearly to zero at the radius.</summary>
        LinearToEdge,
        /// <summary>Physically-flavored 1/d^2 falloff, clamped near the center.</summary>
        InverseSquare,
    }

    [Header("Gravity")]
    [Tooltip("Acceleration applied to bodies inside the field (m/s^2 before falloff).")]
    public float gravityStrength = 9.8f;

    [Tooltip("Field range in WORLD units. Independent of this object's scale.")]
    public float radius = 2700f;

    [Tooltip("How strength varies with distance from the center. Constant = on/off at the edge.")]
    public FalloffMode falloff = FalloffMode.Constant;

    [Tooltip("Optional: use another transform as the attraction center. Defaults to this object.")]
    public Transform centerOverride;

    [Header("Dev Visualization")]
    public Color gizmoColor = new Color(0.3f, 0.6f, 1f, 0.9f);

    [Tooltip("Draw the range gizmo even when this object is not selected.")]
    public bool alwaysDrawGizmo = true;

    /// <summary>All currently-enabled fields. Receivers iterate this to find their attractor.</summary>
    public static readonly List<GravityField> All = new List<GravityField>();

    /// <summary>World-space center of attraction.</summary>
    public Vector3 Center => centerOverride != null ? centerOverride.position : transform.position;

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    /// <summary>True if the world position is within this field's range.</summary>
    public bool Contains(Vector3 worldPos)
    {
        return (worldPos - Center).sqrMagnitude <= radius * radius;
    }

    /// <summary>
    /// Acceleration magnitude this field applies at the given world position (after falloff).
    /// Returns 0 outside the range.
    /// </summary>
    public float AccelerationAt(Vector3 worldPos)
    {
        float sqrDist = (worldPos - Center).sqrMagnitude;
        if (sqrDist > radius * radius)
            return 0f;

        switch (falloff)
        {
            case FalloffMode.LinearToEdge:
            {
                float dist = Mathf.Sqrt(sqrDist);
                return gravityStrength * Mathf.Clamp01(1f - dist / radius);
            }
            case FalloffMode.InverseSquare:
            {
                // Reference the strength at 1/10th of the radius so it stays well-behaved at the
                // center instead of shooting to infinity, then fall off as 1/d^2.
                float reference = radius * 0.1f;
                float clampedSqr = Mathf.Max(sqrDist, reference * reference);
                return gravityStrength * (reference * reference) / clampedSqr;
            }
            default: // Constant
                return gravityStrength;
        }
    }

    void OnDrawGizmos()
    {
        if (alwaysDrawGizmo)
            DrawRangeGizmo();
    }

    void OnDrawGizmosSelected()
    {
        if (!alwaysDrawGizmo) // avoid drawing twice when alwaysDrawGizmo is on
            DrawRangeGizmo();
    }

    void DrawRangeGizmo()
    {
        // Drawn with an identity matrix (world space) so the planet's huge scale never distorts it.
        Gizmos.color = gizmoColor;
        Gizmos.DrawWireSphere(Center, radius);
    }
}
