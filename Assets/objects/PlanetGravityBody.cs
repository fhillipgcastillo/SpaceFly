using System;
using UnityEngine;

/// <summary>
/// Receiver for the range-based gravity system. Attach to any dynamic body (player, props, debris).
///
/// Each physics step it finds the dominant <see cref="GravityField"/> whose range currently
/// contains this body, gets pulled toward that field's center, and (optionally) aligns its "up" to
/// the surface normal. When the body is inside no field it feels NO force and simply coasts on its
/// existing velocity (drifts) — leave a planet's range and gravity lets go.
///
/// Other systems (e.g. PlayerController) read <see cref="InGravity"/> / <see cref="GravityUp"/>
/// instead of hard-wiring a planet, so this works for any number of planets/moons of any size.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class PlanetGravityBody : MonoBehaviour
{
    [Tooltip("Rotate this body so its \"up\" points away from the attractor center. " +
             "Turn OFF for the player: PlayerController owns the player's rotation " +
             "(surface alignment + facing) so the two don't fight over transform.rotation.")]
    public bool alignToSurface = true;

    [Tooltip("How quickly the body rotates to align with the surface (only used when alignToSurface is on).")]
    public float alignSpeed = 5f;

    Rigidbody rb;
    GravityField previousField;

    /// <summary>True while this body is inside at least one gravity field.</summary>
    public bool InGravity { get; private set; }

    /// <summary>Direction away from the current attractor center (the body's "up"). Stays at the
    /// last known value when out of gravity so orientation doesn't snap.</summary>
    public Vector3 GravityUp { get; private set; } = Vector3.up;

    /// <summary>World-space center of the field currently affecting this body.</summary>
    public Vector3 GravityCenter { get; private set; }

    /// <summary>The field currently affecting this body, or null when out of gravity.</summary>
    public GravityField CurrentField { get; private set; }

    /// <summary>Raised when the body enters a field (from none, or switching to a different one).</summary>
    public event Action<GravityField> EnteredField;

    /// <summary>Raised when the body leaves its field (passes the argument that was just left).</summary>
    public event Action<GravityField> ExitedField;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false; // we supply our own gravity

        if (alignToSurface)
            rb.constraints = RigidbodyConstraints.FreezeRotation; // for things that shouldn't tumble
    }

    void FixedUpdate()
    {
        GravityField field = FindDominantField(rb.position);

        if (field != previousField)
        {
            if (previousField != null)
                ExitedField?.Invoke(previousField);
            if (field != null)
                EnteredField?.Invoke(field);
            previousField = field;
        }

        CurrentField = field;
        InGravity = field != null;

        if (!InGravity)
            return; // no field in range -> coast on existing velocity

        GravityCenter = field.Center;
        Vector3 up = (rb.position - GravityCenter).normalized;
        if (up.sqrMagnitude > 0f)
            GravityUp = up;

        rb.AddForce(-GravityUp * field.AccelerationAt(rb.position), ForceMode.Acceleration);

        if (alignToSurface)
        {
            // Through the Rigidbody, not the Transform. Physics.autoSyncTransforms is off, so
            // a MoveRotation issued earlier in this same step - PlayerController does one every
            // step on this body - has not reached the Transform yet. Reading it here and
            // writing it back would undo that turn.
            Quaternion rotation = rb.rotation;
            Quaternion targetRot = Quaternion.FromToRotation(rotation * Vector3.up, GravityUp) * rotation;
            rb.MoveRotation(Quaternion.Slerp(rotation, targetRot, Time.fixedDeltaTime * alignSpeed));
        }
    }

    /// <summary>
    /// Of the fields whose range contains <paramref name="pos"/>, returns the one applying the
    /// strongest acceleration there (tie-break: nearest center). Null if none are in range.
    /// </summary>
    static GravityField FindDominantField(Vector3 pos)
    {
        GravityField best = null;
        float bestAccel = 0f;
        float bestSqrDist = float.MaxValue;

        var fields = GravityField.All;
        for (int i = 0; i < fields.Count; i++)
        {
            GravityField field = fields[i];
            if (field == null || !field.Contains(pos))
                continue;

            float accel = field.AccelerationAt(pos);
            float sqrDist = (pos - field.Center).sqrMagnitude;

            if (accel > bestAccel || (Mathf.Approximately(accel, bestAccel) && sqrDist < bestSqrDist))
            {
                best = field;
                bestAccel = accel;
                bestSqrDist = sqrDist;
            }
        }

        return best;
    }
}
