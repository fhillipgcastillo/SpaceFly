using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Keyboard-and-mouse flight for a ship or other flying vehicle.
///
/// Enabled only while someone is piloting — <see cref="VehicleBoarding"/> switches it on when a
/// player takes the seat and off when they leave. Attitude is driven directly (like
/// <see cref="PlayerController"/> does for the player) so the pilot has full authority and
/// collisions cannot tumble the ship; translation is applied as acceleration so the planet's
/// gravity, supplied unchanged by <see cref="PlanetGravityBody"/>, is something thrust has to
/// fight rather than something this script replaces.
///
/// Thrust is ship-relative on every axis: nose up plus "up thruster" climbs, banked 90 degrees
/// plus the same input pushes sideways. There is no auto-hover — let go inside a gravity field
/// and the ship falls.
///
/// Every tuning value is serialized so it can be adjusted in the Inspector while playing.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(VehicleBoarding))]
[DisallowMultipleComponent]
public class ShipController : MonoBehaviour
{
    [Header("Input (the 'Ship' action map)")]
    [Tooltip("'Throttle' (Axis): W raises thruster power, S lowers it.")]
    public InputActionReference throttleAction;

    [Tooltip("'Strafe' (Axis): A/D slide the ship sideways without turning it.")]
    public InputActionReference strafeAction;

    [Tooltip("'Roll' (Axis): Q/E bank the ship. Negate rollRate if they feel swapped.")]
    public InputActionReference rollAction;

    [Tooltip("'Vertical' (Axis): Space thrusts out of the roof, Ctrl out of the belly.")]
    public InputActionReference verticalAction;

    [Tooltip("'Brake' (Button): X kills the throttle and actively fights current velocity.")]
    public InputActionReference brakeAction;

    [Tooltip("'FreeLook' (Button): hold Alt to hand the mouse back to the camera without " +
             "changing the ship's attitude.")]
    public InputActionReference freeLookAction;

    [Tooltip("The SAME 'Look' action the camera uses (Player map). Shared on purpose: the " +
             "camera keeps it enabled, and this script only borrows the value while piloting.")]
    public InputActionReference lookAction;

    [Header("Thrust (m/s^2)")]
    [Tooltip("Main engine at 100% throttle. Must exceed the planet's gravity (9.8) to climb, " +
             "but not by so much that climbing is free.")]
    public float mainThrust = 25f;

    [Tooltip("Manoeuvring thrusters along the ship's own up/down axis (Space / Ctrl).")]
    public float verticalThrust = 15f;

    [Tooltip("Side thrusters (A / D).")]
    public float strafeThrust = 10f;

    [Tooltip("Braking deceleration applied against current velocity while X is held. Well " +
             "above the throttle rate, so X stops you faster than easing off with S.")]
    public float brakeDeceleration = 20f;

    [Header("Throttle")]
    [Tooltip("Throttle change per second as a fraction: 0.5 means about 2 seconds from idle " +
             "to full. The throttle HOLDS its value when you release W and S.")]
    public float throttleRate = 0.5f;

    [Header("Limits")]
    [Tooltip("Hard cap on total speed (m/s). The player sprints at 8; the gravity field is " +
             "140 units across. This also caps fall speed, which is intended in a world " +
             "this small.")]
    public float maxSpeed = 15f;

    [Tooltip("Rigidbody linear damping applied while piloted, so cutting the throttle lets " +
             "the ship settle instead of coasting forever. Restored on exit.")]
    public float linearDamping = 0.5f;

    [Header("Attitude")]
    [Tooltip("Roll speed in degrees/second (Q / E). Negative flips which key rolls which way.")]
    public float rollRate = 90f;

    [Tooltip("Degrees of pitch/yaw per unit of mouse movement. Matches the camera's own " +
             "sensitivity so steering feels like looking.")]
    public float lookSensitivity = 0.15f;

    [Tooltip("Ceiling on mouse-driven turn rate (degrees/second), so a fast flick cannot " +
             "snap the ship around instantly.")]
    public float maxTurnRate = 120f;

    /// <summary>Current thruster power, 0-1. Exposed for a future HUD gauge.</summary>
    public float Throttle { get; private set; }

    /// <summary>Current speed in m/s. Exposed for a future HUD readout.</summary>
    public float Speed => rb != null ? rb.linearVelocity.magnitude : 0f;

    Rigidbody rb;
    VehicleBoarding boarding;
    PlanetGravityBody gravityBody;
    PlayerCameraControl cameraControl;

    // Mouse movement accumulated in Update, consumed in FixedUpdate.
    Vector2 pendingLook;

    // True only between a successful OnEnable and its matching OnDisable. Gates the restore:
    // OnEnable can bail out by setting enabled = false, which itself fires OnDisable, and
    // restoring state that was never captured would leave alignToSurface off forever.
    bool engaged;

    bool savedAlignToSurface;
    float savedLinearDamping;
    bool savedLookEnabled;
    float savedIdleReturnDelay;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        boarding = GetComponent<VehicleBoarding>();
        gravityBody = GetComponent<PlanetGravityBody>();
    }

    void OnEnable()
    {
        engaged = false;

        // Flight only exists while someone is flying. This also means a stray tick of the
        // checkbox in the Inspector cannot make an unmanned ship controllable.
        if (boarding == null || !boarding.IsPiloted)
        {
            enabled = false;
            return;
        }

        // Otherwise PlanetGravityBody slerps the ship level to the surface every physics step
        // and fights every mouse movement.
        if (gravityBody != null)
        {
            savedAlignToSurface = gravityBody.alignToSurface;
            gravityBody.alignToSurface = false;
        }

        savedLinearDamping = rb.linearDamping;
        rb.linearDamping = linearDamping;

        cameraControl = boarding.Pilot != null ? boarding.Pilot.cameraControl : null;
        if (cameraControl != null)
        {
            savedLookEnabled = cameraControl.lookEnabled;
            savedIdleReturnDelay = cameraControl.idleReturnDelay;

            // The mouse now steers the nose; the camera falls in behind the tail immediately.
            cameraControl.lookEnabled = false;
            cameraControl.idleReturnDelay = 0f;
        }

        SetShipActions(true);

        Throttle = 0f;
        pendingLook = Vector2.zero;
        engaged = true;
    }

    void OnDisable()
    {
        if (!engaged)
            return;

        engaged = false;

        if (gravityBody != null)
            gravityBody.alignToSurface = savedAlignToSurface;

        if (rb != null)
            rb.linearDamping = savedLinearDamping;

        if (cameraControl != null)
        {
            cameraControl.lookEnabled = savedLookEnabled;
            cameraControl.idleReturnDelay = savedIdleReturnDelay;
        }
        cameraControl = null;

        SetShipActions(false);

        Throttle = 0f;
        pendingLook = Vector2.zero;
    }

    void Update()
    {
        if (!engaged)
            return;

        // Free-look hands the mouse back to the camera and freezes the ship's attitude.
        bool freeLook = IsPressed(freeLookAction);
        if (cameraControl != null)
            cameraControl.lookEnabled = freeLook;

        if (freeLook)
        {
            pendingLook = Vector2.zero;
            return;
        }

        // Accumulate here rather than reading in FixedUpdate: mouse delta is a per-frame value,
        // and sampling it on the physics clock double-counts or drops movement.
        pendingLook += ReadVector(lookAction);
    }

    void FixedUpdate()
    {
        if (!engaged)
            return;

        float dt = Time.fixedDeltaTime;

        UpdateThrottle(dt);
        ApplyAttitude(dt);
        ApplyThrust();
        ApplyBrake(dt);
        ClampSpeed();
    }

    void UpdateThrottle(float dt)
    {
        float input = ReadAxis(throttleAction); // +1 while W, -1 while S
        Throttle = Mathf.Clamp01(Throttle + input * throttleRate * dt);
    }

    void ApplyAttitude(float dt)
    {
        float maxStep = maxTurnRate * dt;

        float yaw = Mathf.Clamp(pendingLook.x * lookSensitivity, -maxStep, maxStep);
        float pitch = Mathf.Clamp(-pendingLook.y * lookSensitivity, -maxStep, maxStep);
        float roll = -ReadAxis(rollAction) * rollRate * dt;
        pendingLook = Vector2.zero;

        if (Mathf.Approximately(yaw, 0f) && Mathf.Approximately(pitch, 0f)
            && Mathf.Approximately(roll, 0f))
            return;

        // World-space axes taken from the ship, so this is ship-relative rotation.
        Quaternion delta = Quaternion.AngleAxis(yaw, transform.up)
                           * Quaternion.AngleAxis(pitch, transform.right)
                           * Quaternion.AngleAxis(roll, transform.forward);

        rb.MoveRotation(delta * rb.rotation);
    }

    void ApplyThrust()
    {
        Vector3 accel = transform.forward * (mainThrust * Throttle)
                        + transform.up * (verticalThrust * ReadAxis(verticalAction))
                        + transform.right * (strafeThrust * ReadAxis(strafeAction));

        // Acceleration, not Force: mass should not change how the ship handles, and this sums
        // cleanly with the gravity PlanetGravityBody applies the same way.
        rb.AddForce(accel, ForceMode.Acceleration);
    }

    void ApplyBrake(float dt)
    {
        if (!IsPressed(brakeAction))
            return;

        Throttle = 0f;
        rb.linearVelocity = Vector3.MoveTowards(rb.linearVelocity, Vector3.zero,
                                                brakeDeceleration * dt);
    }

    void ClampSpeed()
    {
        Vector3 velocity = rb.linearVelocity;
        if (velocity.sqrMagnitude > maxSpeed * maxSpeed)
            rb.linearVelocity = velocity.normalized * maxSpeed;
    }

    /// <summary>
    /// Enable/disable the ship-only actions. <see cref="lookAction"/> is deliberately excluded:
    /// it belongs to the camera, which keeps it enabled on foot as well.
    /// </summary>
    void SetShipActions(bool enable)
    {
        SetAction(throttleAction, enable);
        SetAction(strafeAction, enable);
        SetAction(rollAction, enable);
        SetAction(verticalAction, enable);
        SetAction(brakeAction, enable);
        SetAction(freeLookAction, enable);
    }

    static void SetAction(InputActionReference reference, bool enable)
    {
        InputAction action = reference != null ? reference.action : null;
        if (action == null)
            return;

        if (enable)
            action.Enable();
        else
            action.Disable();
    }

    static float ReadAxis(InputActionReference reference)
    {
        return reference != null && reference.action != null
            ? reference.action.ReadValue<float>()
            : 0f;
    }

    static Vector2 ReadVector(InputActionReference reference)
    {
        return reference != null && reference.action != null
            ? reference.action.ReadValue<Vector2>()
            : Vector2.zero;
    }

    static bool IsPressed(InputActionReference reference)
    {
        return reference != null && reference.action != null && reference.action.IsPressed();
    }
}
