# Ship Flight Controller Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fly the Dropship with keyboard and mouse — persistent W/S throttle, X hard brake, A/D strafe, Q/E roll, ship-relative Space/Ctrl thrust, mouse pitch/yaw with hold-Alt free-look — against the planet's real gravity, capped at 15 m/s.

**Architecture:** One new `ShipController` on the vehicle, enabled only while piloted via the `pilotedController` hook the boarding design reserved. It owns attitude (driving rotation directly) and adds thrust as acceleration; gravity keeps coming from the existing `PlanetGravityBody`, untouched. A new `Ship` action map holds the flight bindings, because the on-foot ones are disabled while piloting. The chase camera is the existing `PlayerCameraControl` with look input suppressed.

**Tech Stack:** Unity 6000.0.76f1, `com.unity.inputsystem` 1.19.0. C# 9, target `v4.7.1`.

**Source spec:** `docs/superpowers/specs/2026-09-01-ship-flight-design.md`

## Global Constraints

- New script goes in `Assets/objects/`, matching the house style there: XML `<summary>` on every public type explaining *why* it exists, `[Tooltip]` on every serialized field, `[Header]` grouping.
- **Every tuning value is a serialized public field**, so it can be adjusted in the Inspector while playing. This was an explicit requirement.
- Use `rb.linearVelocity` and `rb.linearDamping` (Unity 6 names), never `velocity`/`drag`.
- Use `FindFirstObjectByType<T>()`, never `FindObjectOfType<T>()`.
- Never modify `Assembly-CSharp.csproj` — it is Unity-generated.
- Do not hand-write `.cs.meta` files; the running editor generates them.
- No commit trailers of any kind.
- Exact tuning defaults, copied from the spec: `mainThrust 25`, `verticalThrust 15`,
  `strafeThrust 10`, `throttleRate 0.5` (fraction/sec), `brakeDeceleration 20`, `maxSpeed 15`,
  `linearDamping 0.5`, `rollRate 90`, `lookSensitivity 0.15`, `maxTurnRate 120`.

## Verification approach

Same as the boarding feature, and the same honest limits. No TDD: the project has no test assembly, and a PlayMode rig would need the Unity Editor closed to take the project lock. The user chose a compile check.

- **Per-task automated check:** `./tools/build-check.sh` → must report `0 Error(s)`. For Task 1 additionally a JSON re-parse of the edited `.inputactions` asset.
- **Behaviour is verified by the user**, by flight test, against the checklist in Task 5. No task may be reported as "working" on the strength of a compile. The honest claim is "compiles clean; flight unverified."

---

### Task 1: Ship action map

**Files:**
- Modify: `Assets/InputSystem_Actions.inputactions` (add one map; touch nothing existing)

**Interfaces:**
- Produces: a `Ship` action map containing `Throttle`, `Strafe`, `Roll`, `Vertical` (all `Value`/`Axis`), and `Brake`, `FreeLook` (both `Button`). Task 4 references these by name from the Inspector.

The asset is JSON. Bindings for the axes are `1DAxis` composites: one entry with `isComposite: true`, followed by `negative` and `positive` parts with `isPartOfComposite: true`. Existing maps use `"groups": "Keyboard&Mouse"` on simple bindings and `";Keyboard&Mouse"` on composite parts — match that.

- [ ] **Step 1: Add the map with a one-off script**

```bash
cd /c/Users/Owner/Unity/Projects/SpaceFly && python - <<'PY'
import json, uuid, collections

path = 'Assets/InputSystem_Actions.inputactions'
with open(path, encoding='utf-8') as fh:
    doc = json.load(fh, object_pairs_hook=collections.OrderedDict)

if any(m['name'] == 'Ship' for m in doc['maps']):
    raise SystemExit('Ship map already present - nothing to do')

gid = lambda: str(uuid.uuid4())

def axis(name):
    return collections.OrderedDict([
        ('name', name), ('type', 'Value'), ('id', gid()),
        ('expectedControlType', 'Axis'), ('processors', ''),
        ('interactions', ''), ('initialStateCheck', True)])

def button(name):
    return collections.OrderedDict([
        ('name', name), ('type', 'Button'), ('id', gid()),
        ('expectedControlType', 'Button'), ('processors', ''),
        ('interactions', ''), ('initialStateCheck', False)])

def binding(name, path_, action, composite=False, part=False, groups='Keyboard&Mouse'):
    return collections.OrderedDict([
        ('name', name), ('id', gid()), ('path', path_),
        ('interactions', ''), ('processors', ''), ('groups', groups),
        ('action', action), ('isComposite', composite), ('isPartOfComposite', part)])

def axis_bindings(action, negative, positive):
    return [
        binding(action, '1DAxis', action, composite=True, groups=''),
        binding('negative', negative, action, part=True, groups=';Keyboard&Mouse'),
        binding('positive', positive, action, part=True, groups=';Keyboard&Mouse'),
    ]

actions = [axis('Throttle'), axis('Strafe'), axis('Roll'), axis('Vertical'),
           button('Brake'), button('FreeLook')]

bindings = []
bindings += axis_bindings('Throttle', '<Keyboard>/s',        '<Keyboard>/w')
bindings += axis_bindings('Strafe',   '<Keyboard>/a',        '<Keyboard>/d')
bindings += axis_bindings('Roll',     '<Keyboard>/q',        '<Keyboard>/e')
bindings += axis_bindings('Vertical', '<Keyboard>/leftCtrl', '<Keyboard>/space')
bindings.append(binding('', '<Keyboard>/x',       'Brake'))
bindings.append(binding('', '<Keyboard>/leftAlt', 'FreeLook'))

doc['maps'].append(collections.OrderedDict([
    ('name', 'Ship'), ('id', gid()),
    ('actions', actions), ('bindings', bindings)]))

with open(path, 'w', encoding='utf-8') as fh:
    json.dump(doc, fh, indent=4)
    fh.write('\n')
print('Ship map added:', [a['name'] for a in actions])
PY
```

- [ ] **Step 2: Verify the asset is still valid JSON and the map is intact**

```bash
cd /c/Users/Owner/Unity/Projects/SpaceFly && python - <<'PY'
import json
d = json.load(open('Assets/InputSystem_Actions.inputactions'))
names = [m['name'] for m in d['maps']]
assert names == ['Player', 'UI', 'Ship'], names
ship = [m for m in d['maps'] if m['name'] == 'Ship'][0]
assert [a['name'] for a in ship['actions']] == ['Throttle','Strafe','Roll','Vertical','Brake','FreeLook']
ids = [b['id'] for b in ship['bindings']] + [a['id'] for a in ship['actions']]
assert len(ids) == len(set(ids)), 'duplicate ids'
for a in ship['actions']:
    assert any(b['action'] == a['name'] for b in ship['bindings']), a['name']
print('OK: Ship map valid,', len(ship['bindings']), 'bindings')
for b in ship['bindings']:
    if not b['isComposite']:
        print('   %-9s <- %s' % (b['action'], b['path']))
PY
```

Expected: `OK: Ship map valid, 14 bindings`, then W/S/A/D/Q/E/Ctrl/Space/X/LeftAlt listed. The `Player` and `UI` maps must be untouched.

- [ ] **Step 3: Confirm the existing maps did not change**

```bash
cd /c/Users/Owner/Unity/Projects/SpaceFly && git diff --stat Assets/InputSystem_Actions.inputactions
```

Expected: additions only. If the diff shows large deletions, `json.dump` reformatted the file — that is acceptable *only* if step 2 passed; note it in the commit message.

- [ ] **Step 4: Commit**

```bash
git add Assets/InputSystem_Actions.inputactions
git commit -m "Add Ship action map for flight controls"
```

---

### Task 2: PlayerCameraControl look suppression

**Files:**
- Modify: `Assets/objects/PlayerCameraControl.cs` — add a public field, and use it in `UpdateHeadingFromInput`

**Interfaces:**
- Produces: `public bool lookEnabled` (default `true`). Task 4 sets it `false` while piloting and `true` while Alt is held.

When `lookEnabled` is false, `UpdateHeadingFromInput` takes its existing idle branch, which eases the heading back behind the target. Combined with `idleReturnDelay = 0` that *is* a chase cam — no second camera script.

- [ ] **Step 1: Add the field next to the other look settings**

Insert after the `invertY` field:

```csharp
    [Tooltip("When false, look input is ignored and the camera eases back behind its target. " +
             "Piloting a vehicle turns this off so the mouse steers the ship instead of the " +
             "camera, and turns it back on while free-look is held.")]
    public bool lookEnabled = true;
```

- [ ] **Step 2: Honour it when reading input**

In `UpdateHeadingFromInput`, replace:

```csharp
        Vector2 look = ReadLook();
```

with:

```csharp
        // When look is suppressed the idle branch below runs every frame, easing the camera
        // behind the target - which is exactly the chase-cam behaviour piloting wants.
        Vector2 look = lookEnabled ? ReadLook() : Vector2.zero;
```

- [ ] **Step 3: Build check**

Run: `cd /c/Users/Owner/Unity/Projects/SpaceFly && ./tools/build-check.sh`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add Assets/objects/PlayerCameraControl.cs
git commit -m "Let look input be suppressed so a vehicle can steer with the mouse"
```

---

### Task 3: VehicleBoarding pilotedController hook

**Files:**
- Modify: `Assets/objects/VehicleBoarding.cs` — add a serialized field, enable it in `Board`, disable it first thing in `Disembark`

**Interfaces:**
- Produces: `public MonoBehaviour pilotedController`. Task 4's `ShipController` is what gets assigned to it.

**Ordering is load-bearing.** `pilotedController` must be disabled at the *top* of `Disembark`, before `rig.Restore(...)`. `ShipController.OnDisable` restores the camera's `lookEnabled`/`idleReturnDelay`; `PlayerRig.Restore` then restores `distance`/`focusHeight` and calls `SnapBehindPlayer()`. Reversing that order leaves the camera in chase-cam mode after disembarking.

- [ ] **Step 1: Add the field**

After the `Piloting Camera` header block, add:

```csharp
    [Header("Piloted Controller")]
    [Tooltip("Optional. A behaviour enabled while this vehicle is piloted and disabled on exit " +
             "- e.g. ShipController. Left empty the vehicle is boardable but inert. Kept as a " +
             "plain MonoBehaviour so this script stays vehicle-agnostic: a car controller drops " +
             "into the same slot.")]
    public MonoBehaviour pilotedController;
```

- [ ] **Step 2: Enable it on board**

In `Board`, immediately before `ShowPrompt(exitPrompt);`, add:

```csharp
        if (pilotedController != null)
            pilotedController.enabled = true;
```

- [ ] **Step 3: Disable it first thing on exit**

In `Disembark`, immediately after the `if (rig == null)` defensive block and before `Vector3 up = ExitUp(rig);`, add:

```csharp
        // Before the restore, not after: the controller puts back the camera settings it took
        // (look suppression, idle delay), and PlayerRig.Restore then puts back distance/focus
        // and snaps the camera. Reversing the order strands the camera in chase-cam mode.
        if (pilotedController != null)
            pilotedController.enabled = false;
```

- [ ] **Step 4: Build check**

Run: `cd /c/Users/Owner/Unity/Projects/SpaceFly && ./tools/build-check.sh`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add Assets/objects/VehicleBoarding.cs
git commit -m "Add the pilotedController hook the boarding design reserved"
```

---

### Task 4: ShipController

**Files:**
- Create: `Assets/objects/ShipController.cs`

**Interfaces:**
- Consumes: `VehicleBoarding.IsPiloted`, `VehicleBoarding.Pilot` (a `PlayerRig`), `PlayerRig.cameraControl`, `PlayerCameraControl.lookEnabled` / `.idleReturnDelay`, `PlanetGravityBody.alignToSurface`.
- Produces: `public float Throttle { get; private set; }` (0-1) and `public float Speed { get; }` for a future HUD.

**Three details that are easy to get wrong:**

1. **`alignToSurface` must be off while piloted.** `PlanetGravityBody.FixedUpdate` slerps the ship level to the surface every step; left on it fights every mouse movement. Save and restore it.
2. **Mouse delta is accumulated in `Update`, consumed in `FixedUpdate`.** Reading a delta control directly in `FixedUpdate` double-reads or drops samples depending on how the rates line up, which reads as jittery aiming.
3. **`OnDisable` must not restore state it never captured.** `OnEnable` bails out (setting `enabled = false`) when nothing is piloting, and that itself triggers `OnDisable` — so an `engaged` flag gates the restore. Without it, a stray enable permanently sets `alignToSurface = false` on the ship.

- [ ] **Step 1: Write the file**

```csharp
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Keyboard-and-mouse flight for a ship or other flying vehicle.
///
/// Enabled only while someone is piloting - <see cref="VehicleBoarding"/> switches it on when a
/// player takes the seat and off when they leave. Attitude is driven directly (like
/// <see cref="PlayerController"/> does for the player) so the pilot has full authority and
/// collisions cannot tumble the ship; translation is applied as acceleration so the planet's
/// gravity, supplied unchanged by <see cref="PlanetGravityBody"/>, is something thrust has to
/// fight rather than something this script replaces.
///
/// Thrust is ship-relative on every axis: nose up plus "up thruster" climbs, banked 90 degrees
/// plus the same input pushes sideways. There is no auto-hover - let go inside a gravity field
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
```

- [ ] **Step 2: Build check**

Run: `cd /c/Users/Owner/Unity/Projects/SpaceFly && ./tools/build-check.sh`
Expected: `ShipController.cs` in the compiled list; `Build succeeded.` / `0 Error(s)`.

If `rb.linearDamping` errors, the Unity 6 property name is wrong — check `Rigidbody` in the installed editor before changing it.

- [ ] **Step 3: Confirm the gravity handoff is actually there**

```bash
cd /c/Users/Owner/Unity/Projects/SpaceFly && grep -c "alignToSurface" Assets/objects/ShipController.cs
```

Expected: `3` (save, clear, restore). Fewer means the ship will fight the mouse.

- [ ] **Step 4: Commit**

```bash
git add Assets/objects/ShipController.cs
git commit -m "Add ShipController: keyboard and mouse flight against planet gravity"
```

---

### Task 5: Setup guide, review, final verification

**Files:**
- Create: `docs/superpowers/plans/2026-09-01-ship-flight-setup.md`

- [ ] **Step 1: Write the setup guide**

It must contain, in order:

1. **Add `ShipController` to the `Dropship_R35` root**, then **untick its checkbox** so it starts disabled. Note that it self-disables anyway if enabled with nobody aboard.
2. **Assign the six Ship actions** — `throttleAction` = `Ship/Throttle`, `strafeAction` = `Ship/Strafe`, `rollAction` = `Ship/Roll`, `verticalAction` = `Ship/Vertical`, `brakeAction` = `Ship/Brake`, `freeLookAction` = `Ship/FreeLook` — and `lookAction` = **`Player/Look`**, the same one the camera uses.
3. **Assign `pilotedController`** on the `VehicleBoarding` component to the `ShipController`.
4. The full control table from the spec.
5. **The 12-step flight checklist**, reproduced verbatim from the spec's "Flight checklist".
6. **Tuning notes:** every value is live-editable while playing. If Q/E roll the wrong way, set `rollRate` negative. If steering feels too slow or fast, `lookSensitivity` then `maxTurnRate`.
7. **Known hazards:** climbing past ~20 units leaves the 70-unit gravity field, where gravity is zero with no gradient pulling back, so the ship drifts away permanently; and exiting mid-air re-levels the ship and drops it, because `alignToSurface` is restored on disembark.

- [ ] **Step 2: Commit the guide**

```bash
git add docs/superpowers/plans/2026-09-01-ship-flight-setup.md
git commit -m "Add setup and flight-test guide for the ship controller"
```

- [ ] **Step 3: Final build check**

Run: `cd /c/Users/Owner/Unity/Projects/SpaceFly && ./tools/build-check.sh`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 4: Dispatch the `adversarial-reviewer` agent**

Give it the diff and the spec, and ask it to try to prove the code wrong — with particular attention to: the `engaged` gate and whether any saved state can leak or be restored wrongly across an enable/disable cycle; the interaction between `ShipController.OnDisable`, `VehicleBoarding.Disembark` and `PlayerRig.Restore` ordering; whether `rb.MoveRotation` actually rotates a Rigidbody with `freezeRotation` set by `PlanetGravityBody`; whether disabling the shared `Look` action anywhere breaks the camera; the mouse accumulate/consume split; and whether the speed clamp fights gravity or the brake in a surprising way.

- [ ] **Step 5: Fix what the review confirms, re-run the build check, commit**

- [ ] **Step 6: Report honestly**

State that it compiles clean, that flight behaviour is **unverified** pending the user's flight test, and hand over the setup guide.

---

## Self-Review

**Spec coverage:** W/S throttle → Task 4 `UpdateThrottle`. X hard brake → Task 4 `ApplyBrake` (zeroes throttle *and* opposes velocity). A/D strafe, Q/E roll, Space/Ctrl ship-relative vertical → Task 4 `ApplyThrust` / `ApplyAttitude`, bindings in Task 1. Mouse pitch/yaw → Task 4 `Update` + `ApplyAttitude`. Hold-Alt free-look → Task 4 `Update`, `FreeLook` binding in Task 1, `lookEnabled` in Task 2. Speed cap + damping → Task 4 `ClampSpeed` and the `OnEnable` damping swap. Gravity unchanged, `alignToSurface` suspended → Task 4 `OnEnable`/`OnDisable`. Chase camera → Tasks 2 and 4. `pilotedController` hook → Task 3. All values serialized → Task 4 field block. Flight checklist → Task 5. Every spec section maps to a task.

**Placeholder scan:** no TBD/TODO; every code step carries complete code; Task 5 enumerates exact content rather than saying "write a guide".

**Type consistency:** `Throttle` (float property) and `Speed` (float property) are declared once in Task 4 and referenced nowhere else. `lookEnabled` is declared in Task 2 and used in Task 4's `OnEnable`/`OnDisable`/`Update` under that exact name. `idleReturnDelay` is an existing public field of `PlayerCameraControl`, read from the real source. `pilotedController` is `MonoBehaviour` in Task 3 and `ShipController` derives from `MonoBehaviour`, so the Inspector assignment in Task 5 is valid. Action names in Task 1 (`Throttle`, `Strafe`, `Roll`, `Vertical`, `Brake`, `FreeLook`) match the field names and the Task 5 assignment table exactly.
