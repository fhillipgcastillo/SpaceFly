# Vehicle Boarding & Control Handoff — Design

Date: 2026-09-01
Status: Approved, ready for implementation plan

## Problem

The player walks the planet surface on foot. A Dropship_R35 sits in the scene with
colliders and gravity already wired, but there is no way to get into it. We want the
player to walk to the ship's hatch, board it, be seated in the cockpit, ride the ship as
one unit, and disembark somewhere safe without immediately re-boarding.

Flight itself (thrust, pitch, yaw, roll) is explicitly a later, separate feature.

## Scope

### In scope

- A boarding zone on the vehicle: the player enters a trigger volume and is offered a prompt.
- Explicit `Interact` press to board (not automatic on contact).
- Player seated in the cockpit: parented to a seat anchor, physics suspended, mesh still
  visible, riding the vehicle as one unit.
- Camera reframed from the player to the vehicle while piloting, restored on exit.
- Explicit `Interact` press to disembark, placing the player at an authored exit point
  with physics and controls restored.
- Re-trigger safety: exiting must not immediately re-board.
- The whole feature must be drop-on-anything: a car, a different ship, or a static object
  must become boardable with no code changes.

### Out of scope (next feature)

- Vehicle movement of any kind: thrust, steering, pitch/yaw/roll, takeoff, landing.
- Any HUD beyond a single prompt label.
- Multiple seats, passengers, or entering while the vehicle is moving.

## Existing state (verified in SampleScene)

- **Player** — `Rigidbody`, `CapsuleCollider` (r 0.5, h 1), `PlayerController`,
  `PlanetGravityBody` with `alignToSurface = 0` (the controller owns rotation). The
  scene Camera is currently a **child of Player** and is driven by `PlayerCameraControl`.
- **Dropship_R35** — already instanced at (49.19, 13.97, 3.02), scale 1.4172. On its root:
  `Rigidbody`, `PlanetGravityBody` with `alignToSurface = 1`, four solid `BoxCollider`s,
  and **one `BoxCollider` already marked `isTrigger`** — a thin vertical panel, size
  3.16 x 4.66 x 0.5, center (-0.07, 2.67, -0.82). That is the hatch doorway and serves as
  the boarding zone. Child mesh parts carry convex `MeshCollider`s.
- **Input** — `InputSystem_Actions` already defines an unused `Interact` action alongside
  Move / Look / Jump / Sprint. **It carries a `Hold` interaction**, and the project has no
  `InputSettings` asset, so the 0.4s `defaultHoldTime` applies: `performed` fires only after a
  0.4s hold and a tap never fires it at all. The boarding code therefore subscribes to
  `started`, which fires on the press itself and keeps the "Press E ..." prompt honest.
  Subscribing to `performed` — the obvious choice — would have made the feature silently
  unresponsive to a tap. Found in adversarial review, not by the compile check.
- **Packages** — `com.unity.ugui` 2.0.0 present; TextMeshPro is **not** imported and there
  is **no Canvas** in the scene. The prompt therefore uses legacy `UnityEngine.UI.Text`
  with Unity's built-in `LegacyRuntime.ttf`, requiring no package import.

## Architecture

Three new scripts in `Assets/objects/`, split so that each has one clear owner:

| File | Lives on | Responsibility |
| --- | --- | --- |
| `PlayerRig.cs` | Player | Owns *how a player suspends and restores itself*. Holds the references to `PlayerController`, `PlanetGravityBody`, `Rigidbody`, `CapsuleCollider` and `PlayerCameraControl`. |
| `VehicleBoarding.cs` | Any vehicle root | Boarding zone, `Interact` input, board/exit transitions, `IsPiloted` state. Knows nothing about what a player is made of. |
| `BoardingPrompt.cs` | A Canvas in the scene | Shows and hides the prompt label. Knows nothing about vehicles. |

### The seam

```csharp
// PlayerRig — the player decides what "suspended" means
public void Suspend(Transform seatAnchor, Transform cameraTarget,
                    float camDistance, float camFocusHeight);
public void Restore(Vector3 position, Quaternion rotation, Vector3 inheritedVelocity);
```

`VehicleBoarding` calls exactly these two methods. Adding a component to the player later
changes only `PlayerRig`; no vehicle script is touched. This is the reusability boundary:
vehicles depend on a two-method interface, not on a component list.

### Why no ShipController this step

The handoff option originally described a `ShipController` owning the piloted state. Since
flight is deliberately the next feature, that file would be an empty class — a stub, which
the project's hard rules reject. `VehicleBoarding.IsPiloted` carries the state now.
`ShipController.cs` arrives with the flight work, at which point `VehicleBoarding` gains a
single serialized `MonoBehaviour pilotedController` field that it enables on board and
disables on exit.

## Behaviour

### Board

Preconditions: a `PlayerRig` is inside the trigger volume, and `IsPiloted` is false.

```
save originals (parent, rb.isKinematic, collider.enabled, cam distance/focusHeight)
PlayerController.enabled  = false
PlanetGravityBody.enabled = false
playerRb.linearVelocity   = zero;  playerRb.angularVelocity = zero
playerRb.isKinematic      = true       // physics must not fight the parent transform
playerCapsule.enabled     = false      // stop colliding with the hull we now sit in
player.SetParent(seatAnchor); localPosition = zero; localRotation = identity
cameraControl.SetTarget(vehicle)
cameraControl.distance = pilotCamDistance; focusHeight = pilotCamFocusHeight
cameraControl.SnapBehindPlayer()
IsPiloted = true
```

The seat anchor's own rotation defines which way the pilot faces, so seating is fully
authored per vehicle.

### Exit

```
player.SetParent(originalParent)
player.position = exitPoint.position
player.rotation = LookRotation(exitPoint.forward flattened onto gravityUp, gravityUp)
playerCapsule.enabled   = true
playerRb.isKinematic    = false
playerRb.linearVelocity = vehicleRb != null ? vehicleRb.linearVelocity : Vector3.zero
PlanetGravityBody.enabled = true;  PlayerController.enabled = true
cameraControl.SetTarget(player); restore saved distance/focusHeight; SnapBehindPlayer()
IsPiloted = false
```

Inheriting the vehicle's velocity means disembarking from a moving vehicle later will not
launch or strand the player.

### Re-trigger safety

Three concrete mechanisms, each guarding a distinct failure:

1. **One input listener, not two — plus a scene-wide frame guard.** A single
   `interactAction.started` handler in `VehicleBoarding` branches on `IsPiloted`. Two separate
   listeners — one to board, one to exit — would let a single keypress exit and re-board. This
   is the primary defect this design exists to prevent.

   One listener per component is **not sufficient on its own**, which adversarial review
   caught: `InputActionReference.action` resolves to the *shared* `InputAction` in the asset, so
   one press invokes the handler on *every* boardable in the scene. With two boardables, vehicle
   A's disembark and vehicle B's board can both run on one press. A static `lastActionFrame`
   guard therefore enforces one press → one action across all instances, and a successful board
   clears the boarded rig from every other zone's `rigInZone` so no stale reference survives.
2. **`exitPoint` is authored outside the trigger volume.** After exiting, the player is not
   in the boarding zone at all, so no prompt is armed.
3. **`playerInZone` is forced to `false` on board.** Disabling the player's capsule while it
   sits inside a trigger does not reliably raise `OnTriggerExit` across Unity versions, so
   the flag would otherwise be left stale and the zone would think the player is still there
   after disembarking elsewhere.

## Reusability

The feature must work on a car, another ship, or a static object with no code change.

- **Detection is by component, not tag or layer:** `other.GetComponentInParent<PlayerRig>()`.
  Any rig-bearing character can board any boardable object.
- **The camera reference lives on `PlayerRig`, not on the vehicle.** The camera is wired once,
  on the player. A newly added vehicle needs no camera setup.
- **The vehicle `Rigidbody` is optional**, used only to inherit momentum on exit. A static
  object (turret, console, docked pod) is boardable.
- **Per-vehicle differences are inspector values only:**

| Field | Type | Purpose |
| --- | --- | --- |
| `seatAnchor` | Transform | Where the pilot sits; its rotation sets facing. |
| `exitPoint` | Transform | Where the player lands on exit. Author OUTSIDE the trigger volume. |
| `boardPrompt` | string | Default `"Press E to pilot the dropship"`. |
| `exitPrompt` | string | Default `"Press E to exit"`. |
| `pilotCamDistance` | float | 18 for the dropship; roughly 8 for a car. |
| `pilotCamFocusHeight` | float | 4 for the dropship; roughly 1.5 for a car. |
| `interactAction` | InputActionReference | The `Interact` action. |
| `prompt` | BoardingPrompt | Optional; falls back to finding the one in the scene. |

Adding a car later means: drop `VehicleBoarding` on it, add two empty child transforms, set
its inspector values (only `seatAnchor`, `exitPoint` and `interactAction` are required).

## Editor setup (performed by the user after implementation)

1. Empty child `Cockpit_Seat` under `Dropship_R35`, positioned at the pilot seat, its forward
   pointing the way the pilot should face.
2. Empty child `Exit_Point` under `Dropship_R35`, beside the hatch, **outside** the trigger
   `BoxCollider` and clear of the hull colliders, forward pointing away from the hull.
3. `PlayerRig` on `Player`, with the `PlayerCameraControl` reference assigned.
4. `VehicleBoarding` on the `Dropship_R35` root, with `seatAnchor`, `exitPoint` and
   `interactAction` assigned.
5. A Canvas with a legacy `Text`, plus `BoardingPrompt` pointing at that Text.
6. Recommended, not required: re-parent the Camera out of `Player` to the scene root. It
   works either way because `PlayerCameraControl` writes a world pose every `LateUpdate`, but
   while the Player is parented to the vehicle the camera inherits vehicle motion before its
   own smoothing lerp, making follow-smoothing effectively rigid.

## Verification

- **Automated, run by the agent:** `dotnet build` against a scratch copy of
  `Assembly-CSharp.csproj` with the new files added, so Unity's generated csproj is not
  modified. Must report 0 errors, with the output pasted. This was confirmed to run and pass
  on the unmodified tree first, establishing the baseline, and it does not require the Unity
  Editor to be closed.
- **Manual, run by the user:** the board / ride / exit playtest, against a written checklist.
  The agent does not claim the runtime behaviour works.

### Manual checklist

1. Walk into the hatch: prompt reads "Press E to pilot the dropship".
2. Walk out without pressing: prompt disappears, nothing else changes.
3. Press E inside the zone: player snaps to the cockpit seat, camera pulls back to frame the
   whole ship, prompt switches to "Press E to exit".
4. While seated, WASD and Jump do nothing; the player does not fall through the ship.
5. Press E: player appears at the exit point outside the hatch, on the ground, upright
   relative to the planet surface, with walking controls back.
6. The single press in step 5 does not re-board; the prompt does not immediately reappear.
7. Walk back into the hatch and board again: the cycle repeats cleanly.

## Risks

- The player's `CapsuleCollider` is only 1 unit tall with radius 0.5, while the ship is scaled
  1.4172. The authored `exitPoint` must clear the hull; if it does not, the player will be
  pushed out by physics on restore. Mitigated by authoring, and visible immediately in the
  playtest.
- `PlanetGravityBody.Awake` sets `rb.useGravity = false` and, when `alignToSurface` is on,
  sets `FreezeRotation`. Disabling and re-enabling the component does not re-run `Awake`, so
  those settings persist correctly across a board/exit cycle. This is relied upon rather than
  re-applied.
