using UnityEngine;

/// <summary>
/// The player's own description of how it suspends and restores itself.
///
/// This exists so that vehicles never have to know what a player is made of. A vehicle calls
/// <see cref="Suspend"/> to seat the player and <see cref="Restore"/> to put them back on
/// their feet; everything about which components to switch off, what to do with the
/// Rigidbody, and where the camera points lives here. Add a component to the player later and
/// only this file changes — no vehicle script is touched.
///
/// While suspended the player is parented to a seat, made kinematic and non-colliding, so it
/// rides its vehicle as one rigid unit while staying visible in the cockpit.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[DisallowMultipleComponent]
public class PlayerRig : MonoBehaviour
{
    [Header("References (auto-filled from this object when left empty)")]
    [Tooltip("Movement controller, switched off while riding a vehicle.")]
    public PlayerController controller;

    [Tooltip("Gravity receiver, switched off while riding a vehicle so it stops pulling the " +
             "seated player toward the planet.")]
    public PlanetGravityBody gravityBody;

    [Tooltip("The collider that represents the player's body. Disabled while seated so the " +
             "player does not collide with the hull it is sitting inside.")]
    public Collider bodyCollider;

    [Tooltip("The orbit camera. Retargeted to the vehicle while riding, back to the player on " +
             "exit. If left empty, the one in the scene is found automatically.")]
    public PlayerCameraControl cameraControl;

    Rigidbody rb;

    // --- state captured at Suspend, put back at Restore ---
    Transform savedParent;
    Vector3 savedLocalScale;
    bool savedKinematic;
    RigidbodyInterpolation savedInterpolation;
    bool savedColliderEnabled;
    float savedCamDistance;
    float savedCamFocusHeight;
    bool savedCamera; // whether the camera fields above actually hold anything

    /// <summary>True while the player is seated in something and not under its own control.</summary>
    public bool IsSuspended { get; private set; }

    /// <summary>
    /// The player's current "up" (planet surface normal), taken from the gravity receiver.
    /// Falls back to the transform's own up when there is no receiver. Keeps returning a usable
    /// value while suspended, because the receiver holds its last known up when disabled.
    /// </summary>
    public Vector3 GravityUp => gravityBody != null ? gravityBody.GravityUp : transform.up;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();

        if (controller == null)
            controller = GetComponent<PlayerController>();
        if (gravityBody == null)
            gravityBody = GetComponent<PlanetGravityBody>();
        if (bodyCollider == null)
            bodyCollider = GetComponent<Collider>();
        if (cameraControl == null)
            cameraControl = FindFirstObjectByType<PlayerCameraControl>();
    }

    /// <summary>
    /// Seat the player at <paramref name="seatAnchor"/> and hand the camera to
    /// <paramref name="cameraTarget"/>. Returns false, changing nothing, when already suspended
    /// or the seat is missing, so a caller can safely abort.
    /// </summary>
    public bool Suspend(Transform seatAnchor, Transform cameraTarget,
                        float camDistance, float camFocusHeight)
    {
        if (IsSuspended || seatAnchor == null)
            return false;

        savedParent = transform.parent;
        savedLocalScale = transform.localScale;
        savedKinematic = rb.isKinematic;
        savedInterpolation = rb.interpolation;
        savedColliderEnabled = bodyCollider != null && bodyCollider.enabled;

        if (controller != null)
            controller.enabled = false;
        if (gravityBody != null)
            gravityBody.enabled = false;

        // Zero the velocities BEFORE going kinematic: assignments to a kinematic Rigidbody are
        // ignored, so the order matters.
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.isKinematic = true;

        // Riding is rigid parenting, not physics. Interpolation smooths a body toward its OWN
        // pose history, which lags the seat once the vehicle is moving - so an interpolated
        // pilot visibly slides around the cockpit at speed. Restored on exit.
        rb.interpolation = RigidbodyInterpolation.None;

        if (bodyCollider != null)
            bodyCollider.enabled = false;

        // The seat may sit under a scaled vehicle (the dropship is ~1.42x) and the player would
        // inherit that scale. Counter-scale so being parented never changes the player's size.
        Vector3 worldScale = transform.lossyScale;
        transform.SetParent(seatAnchor, worldPositionStays: false);
        transform.localScale = DivideComponentwise(worldScale, seatAnchor.lossyScale);
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.identity;

        savedCamera = false;
        if (cameraControl != null && cameraTarget != null)
        {
            savedCamDistance = cameraControl.distance;
            savedCamFocusHeight = cameraControl.focusHeight;
            savedCamera = true;

            cameraControl.SetTarget(cameraTarget);
            cameraControl.distance = camDistance;
            cameraControl.focusHeight = camFocusHeight;
            cameraControl.SnapBehindPlayer();
        }

        IsSuspended = true;
        return true;
    }

    /// <summary>
    /// Put the player back on their feet at the given world pose, carrying
    /// <paramref name="inheritedVelocity"/> (normally the vehicle's) so stepping off something
    /// moving neither launches nor strands them. No-op when not suspended.
    /// </summary>
    public void Restore(Vector3 position, Quaternion rotation, Vector3 inheritedVelocity)
    {
        if (!IsSuspended)
            return;

        transform.SetParent(savedParent, worldPositionStays: true);
        transform.localScale = savedLocalScale;
        transform.SetPositionAndRotation(position, rotation);

        if (bodyCollider != null)
            bodyCollider.enabled = savedColliderEnabled;

        // Leave kinematic BEFORE assigning velocity, otherwise the assignment is dropped and
        // the inherited momentum is silently lost.
        rb.isKinematic = savedKinematic;
        rb.interpolation = savedInterpolation;
        rb.position = position;
        rb.rotation = rotation;
        rb.linearVelocity = inheritedVelocity;
        rb.angularVelocity = Vector3.zero;

        if (gravityBody != null)
            gravityBody.enabled = true;
        if (controller != null)
            controller.enabled = true;

        if (cameraControl != null)
        {
            cameraControl.SetTarget(transform);
            if (savedCamera)
            {
                cameraControl.distance = savedCamDistance;
                cameraControl.focusHeight = savedCamFocusHeight;
            }
            cameraControl.SnapBehindPlayer();
        }

        savedParent = null;
        savedCamera = false;
        IsSuspended = false;
    }

    /// <summary>
    /// Componentwise a/b, leaving a's component untouched where b's is near zero.
    ///
    /// Exact only for uniformly-scaled seats. lossyScale is itself an approximation once a
    /// non-uniform scale is combined with rotation in the parent chain, so a seat under, say, a
    /// (2,1,1)-scaled vehicle rotated about Y will leave the seated player slightly squashed.
    /// Cosmetic, and undone on Restore, which puts the original localScale back. Author vehicles
    /// with uniform scale if it matters.
    /// </summary>
    static Vector3 DivideComponentwise(Vector3 a, Vector3 b)
    {
        const float MinScale = 1e-5f;
        return new Vector3(
            Mathf.Abs(b.x) < MinScale ? a.x : a.x / b.x,
            Mathf.Abs(b.y) < MinScale ? a.y : a.y / b.y,
            Mathf.Abs(b.z) < MinScale ? a.z : a.z / b.z);
    }
}
