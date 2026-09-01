using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Third-person orbit camera for a planet-gravity game (BotW/Genshin-style exploration cam).
///
/// The camera orbits the player at a distance. Look input (mouse / right-stick) swings it
/// around. Its heading is decoupled from the player's facing: the camera orbits around the
/// player's UP axis (the planet surface normal) using its own persistent heading vector, so the
/// player can turn to face the camera without the camera chasing it (no feedback-loop spin).
///
/// When the look input goes idle, the heading eases back to directly behind the player's current
/// facing and the pitch eases back to level.
///
/// All math is done relative to <c>player.up</c> (the surface normal kept by
/// <see cref="PlanetGravityBody"/>), so it stays correct anywhere on the planet.
///
/// References are injected from the outside: assign in the Inspector, or call
/// <see cref="SetCamera"/> / <see cref="SetTarget"/> at runtime.
/// </summary>
public class PlayerCameraControl : MonoBehaviour
{
    [Header("References (injected from outside)")]
    [Tooltip("The camera this controller moves. If left empty, Camera.main is used.")]
    public Camera targetCamera;

    [Tooltip("The player/object the camera orbits around.")]
    public Transform player;

    [Header("Input")]
    [Tooltip("The 'Look' action from InputSystem_Actions (Vector2). If unassigned, falls back " +
             "to reading the raw mouse delta.")]
    public InputActionReference lookAction;

    [Header("Framing")]
    [Tooltip("Distance from the focus point to the camera. Drives where the camera sits " +
             "relative to the player; the camera snaps onto this offset at Start.")]
    public float distance = 6f;

    [Tooltip("How far above the player's pivot the camera looks at / orbits around.")]
    public float focusHeight = 1.5f;

    [Header("Look")]
    [Tooltip("Degrees of orbit per unit of look input.")]
    public float lookSensitivity = 0.15f;

    public bool invertX = false;
    public bool invertY = false;

    [Tooltip("Lock and hide the hardware cursor while playing.")]
    public bool lockCursor = true;

    [Header("Angle Limits (degrees)")]
    [Tooltip("Max up/down tilt away from level. Kept under 90 to avoid the look-rotation flip. " +
             "Horizontal orbit (yaw) is intentionally free 360° so you can pick any heading.")]
    public float pitchLimit = 85f;

    [Header("Return To Default")]
    [Tooltip("Seconds of no look input before the camera starts easing back behind the player.")]
    public float idleReturnDelay = 2f;

    [Tooltip("How quickly the heading/pitch ease back to default once idle. Higher = faster.")]
    public float returnSpeed = 3f;

    [Header("Smoothing")]
    [Tooltip("How quickly the camera transform follows its target pose. Higher = snappier, 0 = instant.")]
    public float followSmoothing = 12f;

    // --- runtime state ---
    Vector3 cameraForward; // persistent world-space heading, kept on the player's tangent plane
    float pitch;           // current up/down tilt, degrees
    float idleTimer;       // seconds since the look input last moved

    const float LookMoveThreshold = 0.0001f;

    /// <summary>
    /// The camera's heading flattened onto the player's tangent plane (pitch removed).
    /// PlayerController uses this as the movement reference direction.
    /// </summary>
    public Vector3 PlanarForward => cameraForward;

    void OnEnable()
    {
        if (lookAction != null && lookAction.action != null)
            lookAction.action.Enable();
    }

    void OnDisable()
    {
        if (lookAction != null && lookAction.action != null)
            lookAction.action.Disable();
    }

    void Start()
    {
        if (targetCamera == null)
            targetCamera = Camera.main;

        if (player != null)
        {
            // Seed the heading from the player's current facing (camera starts behind them).
            Vector3 up = player.up;
            cameraForward = Vector3.ProjectOnPlane(player.forward, up).normalized;
            if (cameraForward.sqrMagnitude < 1e-6f)
                cameraForward = Vector3.ProjectOnPlane(transform.forward, up).normalized;

            // Snap the camera onto the player-relative offset immediately, so its authored
            // scene position doesn't matter — move the player and the camera follows.
            if (targetCamera != null)
                ApplyCameraPose(instant: true);
        }

        ApplyCursorLock();
    }

    void OnValidate()
    {
        pitchLimit = Mathf.Clamp(pitchLimit, 0f, 89.9f); // avoid the look-rotation singularity
    }

    // Camera work happens after the player has moved/rotated for the frame.
    void LateUpdate()
    {
        if (targetCamera == null || player == null)
            return;

        UpdateHeadingFromInput();
        ApplyCameraPose();
    }

    // ---------------------------------------------------------------------
    // Public API — drive it from the outside / dynamically.
    // ---------------------------------------------------------------------

    public void SetTarget(Transform newPlayer) => player = newPlayer;
    public void SetCamera(Camera newCamera) => targetCamera = newCamera;

    /// <summary>Snap the camera heading back behind the player and level the pitch.</summary>
    public void ResetToDefault()
    {
        if (player != null)
            cameraForward = Vector3.ProjectOnPlane(player.forward, player.up).normalized;
        pitch = 0f;
        idleTimer = idleReturnDelay;
    }

    /// <summary>
    /// Re-seed the heading behind the player and jump the camera onto its offset instantly
    /// (no fly-in). Call after teleporting/warping the player at runtime.
    /// </summary>
    public void SnapBehindPlayer()
    {
        if (player == null || targetCamera == null)
            return;

        ResetToDefault();
        ApplyCameraPose(instant: true);
    }

    // ---------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------

    void UpdateHeadingFromInput()
    {
        Vector3 up = player.up;
        Vector2 look = ReadLook();

        if (look.sqrMagnitude > LookMoveThreshold)
        {
            idleTimer = 0f;

            float yawDelta = look.x * lookSensitivity * (invertX ? -1f : 1f);
            float pitchDelta = look.y * lookSensitivity * (invertY ? 1f : -1f);

            // Yaw: rotate the heading around the surface normal (free 360 degrees).
            cameraForward = Quaternion.AngleAxis(yawDelta, up) * cameraForward;
            pitch = Mathf.Clamp(pitch + pitchDelta, -pitchLimit, pitchLimit);
        }
        else
        {
            // Idle: after the delay, ease the heading behind the player and level the pitch.
            idleTimer += Time.deltaTime;
            if (idleTimer >= idleReturnDelay)
            {
                float t = 1f - Mathf.Exp(-returnSpeed * Time.deltaTime);
                Vector3 behind = Vector3.ProjectOnPlane(player.forward, up).normalized;
                if (behind.sqrMagnitude > 1e-6f)
                    cameraForward = Vector3.Slerp(cameraForward, behind, t);
                pitch = Mathf.Lerp(pitch, 0f, t);
            }
        }

        // Keep the heading on the (possibly moved) tangent plane and normalized.
        cameraForward = Vector3.ProjectOnPlane(cameraForward, up).normalized;
    }

    Vector2 ReadLook()
    {
        if (lookAction != null && lookAction.action != null)
            return lookAction.action.ReadValue<Vector2>();

        // Fallback so the camera still works before the Look action is wired up.
        return Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
    }

    void ApplyCameraPose(bool instant = false)
    {
        Vector3 up = player.up;

        // Build the look direction: face along the heading, then tilt by pitch around the right axis.
        Quaternion baseRot = Quaternion.LookRotation(cameraForward, up);
        Quaternion pitchedRot = baseRot * Quaternion.Euler(pitch, 0f, 0f);
        Vector3 viewDir = pitchedRot * Vector3.forward;

        Vector3 focus = player.position + up * focusHeight;
        Vector3 desiredPos = focus - viewDir * distance;
        Quaternion desiredRot = Quaternion.LookRotation(focus - desiredPos, up);

        Transform cam = targetCamera.transform;
        if (instant || followSmoothing <= 0f)
        {
            cam.SetPositionAndRotation(desiredPos, desiredRot);
        }
        else
        {
            float t = 1f - Mathf.Exp(-followSmoothing * Time.deltaTime);
            cam.position = Vector3.Lerp(cam.position, desiredPos, t);
            cam.rotation = Quaternion.Slerp(cam.rotation, desiredRot, t);
        }
    }

    void ApplyCursorLock()
    {
        if (!lockCursor)
            return;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }
}
