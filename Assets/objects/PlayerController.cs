using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Camera-relative, planet-aware third-person movement (BotW/Genshin-style exploration).
///
/// While stationary, free-looking with the camera does NOT turn the character. Pressing a move
/// direction moves the player along the camera's facing projected onto the ground (the planet's
/// tangent plane); the character then rotates to face that direction. Camera pitch is ignored,
/// so looking up/down never moves you vertically. Only gravity and jump act along the surface
/// normal.
///
/// Movement is done through the Rigidbody so it cooperates with <see cref="PlanetGravityBody"/>
/// (which supplies gravity toward the planet center). Set the player's PlanetGravityBody
/// <c>alignToSurface = false</c> — this controller owns the player's full rotation.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class PlayerController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Optional fallback only. 'Up' normally comes from the PlanetGravityBody receiver " +
             "(whichever gravity field the player is inside). Used only if no receiver is present.")]
    public Transform planet;

    [Tooltip("The controlled camera's transform. Its facing (projected onto the ground) is the " +
             "movement reference direction.")]
    public Transform cameraTransform;

    [Header("Input (from InputSystem_Actions)")]
    [Tooltip("'Move' action (Vector2).")]
    public InputActionReference moveAction;

    [Tooltip("'Sprint' action (button, held).")]
    public InputActionReference sprintAction;

    [Tooltip("'Jump' action (button).")]
    public InputActionReference jumpAction;

    [Header("Movement")]
    public float walkSpeed = 4f;
    public float runSpeed = 8f;

    [Tooltip("How fast the character turns to face the move direction (deg/sec).")]
    public float rotationSpeed = 720f;

    [Header("Jump & Ground")]
    [Tooltip("Upward velocity added on jump, along the surface normal.")]
    public float jumpSpeed = 5f;

    [Tooltip("How far BELOW the player's pivot to probe for ground. Should reach just past the " +
             "capsule's feet (capsule half-height ~1 -> ~1.1 here).")]
    public float groundCheckDistance = 1.1f;

    [Tooltip("Ray start offset ABOVE the player's pivot, so the ray begins inside the body.")]
    public float groundCheckOriginHeight = 1f;

    [Tooltip("Layers considered ground for the jump check. Default: everything.")]
    public LayerMask groundMask = ~0;

    Rigidbody rb;
    PlanetGravityBody body;
    Vector3 lastPlanarForward = Vector3.forward; // reused when the camera looks near-vertical
    bool jumpQueued;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        body = GetComponent<PlanetGravityBody>();

        // This controller drives rotation explicitly (surface align + facing). Freeze physics
        // rotation so collisions can't tip the character over.
        rb.freezeRotation = true;
    }

    /// <summary>
    /// Current "up" (surface normal). Comes from the gravity receiver — i.e. whichever field the
    /// player is in. Returns false when the player is in no field (in space): the caller should
    /// skip movement so the body just drifts.
    /// </summary>
    bool TryGetUp(out Vector3 up)
    {
        if (body != null)
        {
            if (!body.InGravity)
            {
                up = body.GravityUp; // last known up; movement is skipped anyway
                return false;
            }
            up = body.GravityUp;
            return true;
        }

        // Fallback for setups without a PlanetGravityBody.
        if (planet != null)
        {
            up = (transform.position - planet.position).normalized;
            return true;
        }

        up = Vector3.up;
        return false;
    }

    void OnEnable()
    {
        moveAction?.action?.Enable();
        sprintAction?.action?.Enable();
        if (jumpAction != null && jumpAction.action != null)
        {
            jumpAction.action.Enable();
            jumpAction.action.performed += OnJump;
        }
    }

    void OnDisable()
    {
        if (jumpAction != null && jumpAction.action != null)
            jumpAction.action.performed -= OnJump;
        moveAction?.action?.Disable();
        sprintAction?.action?.Disable();
        jumpAction?.action?.Disable();
    }

    void OnJump(InputAction.CallbackContext _)
    {
        // Queue the jump; it's consumed (with a ground check) in the next FixedUpdate.
        jumpQueued = true;
    }

    void FixedUpdate()
    {
        // No gravity reference (e.g. drifted out of every field) -> don't drive movement; coast.
        if (!TryGetUp(out Vector3 up))
            return;

        Vector2 input = moveAction != null && moveAction.action != null
            ? moveAction.action.ReadValue<Vector2>()
            : Vector2.zero;
        Vector3 moveDir = ComputeMoveDirection(input, up);

        ApplyRotation(moveDir, up);
        ApplyMovement(moveDir, up);
        ApplyJump(up);
    }

    Vector3 ComputeMoveDirection(Vector2 input, Vector3 up)
    {
        if (input.sqrMagnitude < 1e-4f || cameraTransform == null)
            return Vector3.zero;

        // Flatten the camera's facing onto the tangent plane -> heading (pitch removed).
        Vector3 camFwd = Vector3.ProjectOnPlane(cameraTransform.forward, up);
        if (camFwd.sqrMagnitude < 1e-6f)
            camFwd = lastPlanarForward; // camera is looking nearly straight up/down
        camFwd.Normalize();
        lastPlanarForward = camFwd;

        Vector3 camRight = Vector3.Cross(up, camFwd); // left-handed: up x forward = right

        Vector3 dir = camFwd * input.y + camRight * input.x;
        return Vector3.ClampMagnitude(dir, 1f);
    }

    void ApplyRotation(Vector3 moveDir, Vector3 up)
    {
        // Always keep "up" aligned to the surface normal. Only change facing while moving, so
        // free-looking the camera while stationary doesn't spin the character.
        Vector3 desiredForward = moveDir.sqrMagnitude > 1e-6f
            ? moveDir
            : Vector3.ProjectOnPlane(transform.forward, up);

        if (desiredForward.sqrMagnitude < 1e-6f)
            return;

        Quaternion targetRot = Quaternion.LookRotation(desiredForward.normalized, up);
        rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, targetRot, rotationSpeed * Time.fixedDeltaTime));
    }

    void ApplyMovement(Vector3 moveDir, Vector3 up)
    {
        float speed = IsSprinting() ? runSpeed : walkSpeed;

        // Replace planar velocity with the desired movement, but keep the velocity component
        // along "up" so gravity (falling) and jumping are unaffected.
        Vector3 verticalVel = Vector3.Project(rb.linearVelocity, up);
        rb.linearVelocity = moveDir * speed + verticalVel;
    }

    void ApplyJump(Vector3 up)
    {
        if (!jumpQueued)
            return;

        jumpQueued = false;

        if (!IsGrounded(up))
            return;

        // Cancel any downward velocity, then launch along the surface normal for a consistent jump.
        Vector3 verticalVel = Vector3.Project(rb.linearVelocity, up);
        rb.linearVelocity -= verticalVel;
        rb.AddForce(up * jumpSpeed, ForceMode.VelocityChange);
    }

    bool IsSprinting()
    {
        return sprintAction != null && sprintAction.action != null && sprintAction.action.IsPressed();
    }

    bool IsGrounded(Vector3 up)
    {
        Vector3 origin = transform.position + up * groundCheckOriginHeight;
        float distance = groundCheckOriginHeight + groundCheckDistance;
        return Physics.Raycast(origin, -up, distance, groundMask, QueryTriggerInteraction.Ignore);
    }
}
