using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Makes any object boardable: a dropship, a car, a turret, a docked pod.
///
/// Put this on the object's root, give it a trigger collider (the doorway/hatch volume), and
/// author two child transforms — a seat and an exit spot. A player who walks into the trigger
/// is offered a prompt; pressing Interact seats them and hands the camera to this object;
/// pressing Interact again puts them back on their feet at the exit spot.
///
/// Nothing here is ship-specific. The player is reached only through <see cref="PlayerRig"/>'s
/// two-method interface, entrants are detected by component rather than by tag or layer, and
/// the Rigidbody is optional — so a static object is boardable too. Adding a new vehicle is
/// authoring work, not code.
///
/// This covers boarding, riding and disembarking only; actually flying the vehicle is a
/// separate feature that will add its own controller component for this script to enable
/// alongside <see cref="IsPiloted"/>.
/// </summary>
[DisallowMultipleComponent]
public class VehicleBoarding : MonoBehaviour
{
    [Header("Anchors (author these as child objects)")]
    [Tooltip("Where the pilot sits. Its ROTATION decides which way the pilot faces.")]
    public Transform seatAnchor;

    [Tooltip("Where the player is placed on exit. Author this OUTSIDE the boarding trigger " +
             "volume and clear of the hull colliders, with its forward pointing away from the " +
             "vehicle — that placement is what stops a disembark from immediately re-boarding.")]
    public Transform exitPoint;

    [Header("Input")]
    [Tooltip("The 'Interact' action from InputSystem_Actions. One action both boards and exits.")]
    public InputActionReference interactAction;

    [Header("Prompt")]
    [Tooltip("On-screen label. If left empty, the one in the scene is found automatically, so " +
             "a newly added vehicle needs no UI wiring.")]
    public BoardingPrompt prompt;

    [Tooltip("Shown while standing in the trigger, before boarding.")]
    public string boardPrompt = "Press E to pilot the dropship";

    [Tooltip("Shown while piloting.")]
    public string exitPrompt = "Press E to exit";

    [Header("Piloting Camera")]
    [Tooltip("Camera distance while piloting. A big ship wants much more than a walking " +
             "character: ~18 for the dropship, ~8 for a car.")]
    public float pilotCamDistance = 18f;

    [Tooltip("Camera focus height while piloting: ~4 for the dropship, ~1.5 for a car.")]
    public float pilotCamFocusHeight = 4f;

    [Header("Dev Visualization")]
    [Tooltip("Draw the seat and exit anchors in the Scene view. The exit placement is the " +
             "thing most likely to be authored wrong, so this is on by default.")]
    public bool drawGizmos = true;

    /// <summary>True while a player is seated in this vehicle.</summary>
    public bool IsPiloted { get; private set; }

    /// <summary>The seated player, or null.</summary>
    public PlayerRig Pilot { get; private set; }

    // The rig currently standing in the trigger volume and eligible to board.
    PlayerRig rigInZone;

    Rigidbody vehicleRb;              // optional: only used to hand over momentum on exit
    PlanetGravityBody vehicleGravity; // optional: preferred source of "up" when placing the player

    /// <summary>
    /// All enabled boardables. Needed because a rig can stand in two overlapping zones at once:
    /// when it boards one, the others must drop their now-stale reference to it. Mirrors the
    /// registry pattern in <see cref="GravityField"/>.
    /// </summary>
    static readonly List<VehicleBoarding> All = new List<VehicleBoarding>();

    /// <summary>
    /// Frame in which some boardable already acted on an Interact press.
    ///
    /// <see cref="InputActionReference"/> resolves to the SHARED InputAction in the asset, so a
    /// single press invokes the handler on EVERY boardable in the scene. Without this guard one
    /// press could disembark from one vehicle and board another. One press, one action.
    /// </summary>
    static int lastActionFrame = -1;

    void Awake()
    {
        vehicleRb = GetComponent<Rigidbody>();
        vehicleGravity = GetComponent<PlanetGravityBody>();

        if (prompt == null)
        {
            // Include inactive: the prompt object may legitimately be hidden at startup.
            prompt = FindFirstObjectByType<BoardingPrompt>(FindObjectsInactive.Include);
        }
    }

    void OnEnable()
    {
        All.Add(this);

        InputAction action = interactAction != null ? interactAction.action : null;
        if (action == null)
            return;

        action.Enable();

        // 'started', not 'performed': the Interact action in InputSystem_Actions carries a Hold
        // interaction, so 'performed' would only fire after the default 0.4s hold and a tap
        // would do nothing at all. 'started' fires on the press itself, which is what the
        // "Press E ..." prompt promises. It is also correct for an action with no interaction.
        action.started += OnInteract;
    }

    void OnDisable()
    {
        // The player is parented to this vehicle. If it is being deactivated or destroyed, they
        // would go inactive - or be destroyed - along with it. Put them back on their feet first.
        if (IsPiloted)
            Disembark();

        All.Remove(this);

        InputAction action = interactAction != null ? interactAction.action : null;
        if (action == null)
            return;

        // Deliberately no action.Disable() to match: InputAction.Enable/Disable is not reference
        // counted, so disabling here would kill Interact for every other boardable in the scene.
        action.started -= OnInteract;
    }

    // ---------------------------------------------------------------------
    // Zone detection
    // ---------------------------------------------------------------------

    void OnTriggerEnter(Collider other)
    {
        if (IsPiloted)
            return;

        // Detect by component, not by tag or layer: anything with a rig can board anything.
        PlayerRig rig = other.GetComponentInParent<PlayerRig>();
        if (rig == null || rig.IsSuspended)
            return;

        rigInZone = rig;
        ShowPrompt(boardPrompt);
    }

    void OnTriggerExit(Collider other)
    {
        if (rigInZone == null)
            return;

        PlayerRig rig = other.GetComponentInParent<PlayerRig>();
        if (rig != rigInZone)
            return;

        rigInZone = null;
        HidePrompt();
    }

    // ---------------------------------------------------------------------
    // Input — ONE listener, branching on state.
    //
    // Two separate subscriptions (one to board, one to exit) would let a single keypress exit
    // and immediately re-board. Keep this as the only handler, and note that the shared action
    // means every boardable in the scene sees the same press, hence the frame guard.
    // ---------------------------------------------------------------------

    void OnInteract(InputAction.CallbackContext _)
    {
        if (lastActionFrame == Time.frameCount)
            return; // another boardable already consumed this press

        if (IsPiloted)
        {
            lastActionFrame = Time.frameCount;
            Disembark();
            return;
        }

        if (rigInZone == null)
            return;

        PlayerRig candidate = rigInZone;

        // The press consumes the zone arming either way; walking back in re-arms it. Leaving it
        // set on a failed board is what would let a later press seat a player who is nowhere
        // near this vehicle.
        rigInZone = null;

        if (candidate.IsSuspended)
            return; // already riding something else

        if (Board(candidate))
            lastActionFrame = Time.frameCount;
        else
            HidePrompt();
    }

    // ---------------------------------------------------------------------
    // Transitions
    // ---------------------------------------------------------------------

    /// <summary>
    /// Seat <paramref name="rig"/> in this vehicle. Returns false and changes nothing when the
    /// vehicle is already occupied or its anchors are not authored.
    /// </summary>
    public bool Board(PlayerRig rig)
    {
        if (IsPiloted || rig == null)
            return false;

        if (seatAnchor == null || exitPoint == null)
        {
            Debug.LogError($"{name}: VehicleBoarding needs both seatAnchor and exitPoint " +
                           "assigned before it can be boarded.", this);
            return false;
        }

        if (!rig.Suspend(seatAnchor, transform, pilotCamDistance, pilotCamFocusHeight))
            return false;

        Pilot = rig;
        IsPiloted = true;

        // Disabling the player's collider inside a trigger does not reliably raise
        // OnTriggerExit, so clear the zone flag explicitly rather than trusting physics to.
        rigInZone = null;

        // This rig may have been standing in other overlapping zones. Those references are now
        // stale, and a later press there would otherwise seat a player who has since walked off.
        for (int i = 0; i < All.Count; i++)
        {
            if (All[i] != this && All[i].rigInZone == rig)
                All[i].rigInZone = null;
        }

        ShowPrompt(exitPrompt);
        return true;
    }

    /// <summary>Put the seated player back on their feet at the exit point. No-op when empty.</summary>
    public void Disembark()
    {
        if (!IsPiloted)
            return;

        PlayerRig rig = Pilot;

        // Defensive: a pilot destroyed out from under us leaves the vehicle claimable again.
        if (rig == null)
        {
            IsPiloted = false;
            Pilot = null;
            HidePrompt();
            return;
        }

        Vector3 up = ExitUp(rig);
        Vector3 position;
        Quaternion rotation;

        if (exitPoint != null)
        {
            position = exitPoint.position;
            rotation = ExitRotation(up, rig);
        }
        else
        {
            // The anchor was cleared or destroyed at runtime. Drop the player clear above the
            // hull rather than stranding them: a seated player with no way out is unrecoverable.
            Debug.LogError($"{name}: VehicleBoarding.exitPoint is missing; dropping the pilot " +
                           "above the vehicle instead. Assign an exit point.", this);
            position = transform.position + up * 3f;
            rotation = rig.transform.rotation;
        }

        Vector3 velocity = vehicleRb != null ? vehicleRb.linearVelocity : Vector3.zero;

        // Restore FIRST, then clear. The other order means an exception mid-restore leaves the
        // vehicle reporting itself empty while the player is still parented, kinematic and
        // controller-less — a soft-lock with no way out.
        rig.Restore(position, rotation, velocity);

        IsPiloted = false;
        Pilot = null;
        rigInZone = null;

        HidePrompt();
    }

    /// <summary>
    /// Surface normal to stand the player up along. Prefers the vehicle's own gravity receiver,
    /// which stays live while piloting, over the player's, which is frozen at the value it held
    /// when they boarded.
    /// </summary>
    Vector3 ExitUp(PlayerRig rig)
    {
        if (vehicleGravity != null && vehicleGravity.GravityUp.sqrMagnitude > 1e-6f)
            return vehicleGravity.GravityUp.normalized;

        Vector3 rigUp = rig.GravityUp;
        if (rigUp.sqrMagnitude > 1e-6f)
            return rigUp.normalized;

        return exitPoint != null ? exitPoint.up : transform.up;
    }

    /// <summary>Face the exit point's forward, flattened onto the surface, with sane fallbacks.</summary>
    Quaternion ExitRotation(Vector3 up, PlayerRig rig)
    {
        Vector3 forward = Vector3.ProjectOnPlane(exitPoint.forward, up);
        if (forward.sqrMagnitude < 1e-6f)
            forward = Vector3.ProjectOnPlane(transform.forward, up);
        if (forward.sqrMagnitude < 1e-6f)
            return rig.transform.rotation; // exit point points straight up/down; keep facing

        return Quaternion.LookRotation(forward.normalized, up);
    }

    // ---------------------------------------------------------------------
    // Prompt
    // ---------------------------------------------------------------------

    void ShowPrompt(string message)
    {
        if (prompt != null)
            prompt.Show(message);
    }

    void HidePrompt()
    {
        if (prompt != null)
            prompt.Hide();
    }

    // ---------------------------------------------------------------------
    // Authoring aid — the exit point placement is the thing most likely to be wrong.
    // ---------------------------------------------------------------------

    void OnDrawGizmos()
    {
        if (!drawGizmos)
            return;

        if (seatAnchor != null)
        {
            Gizmos.color = new Color(0.3f, 0.9f, 0.5f, 0.9f);
            Gizmos.DrawWireSphere(seatAnchor.position, 0.35f);
            Gizmos.DrawRay(seatAnchor.position, seatAnchor.forward * 1.2f);
        }

        if (exitPoint != null)
        {
            Gizmos.color = new Color(1f, 0.7f, 0.2f, 0.9f);
            Gizmos.DrawWireSphere(exitPoint.position, 0.5f);
            Gizmos.DrawRay(exitPoint.position, exitPoint.forward * 1.5f);
        }
    }
}
