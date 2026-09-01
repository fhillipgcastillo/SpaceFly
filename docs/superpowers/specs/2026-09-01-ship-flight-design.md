# Ship Flight Controller — Design

Date: 2026-09-01
Status: Approved, ready for implementation plan
Follows: `2026-09-01-vehicle-boarding-design.md` (this fills the `pilotedController` hook it left)

## Problem

Boarding works: the player can sit in the Dropship and ride it, but nothing flies it. This adds
the flight controller — Star Citizen-flavoured, keyboard and mouse only, with thrust that has to
fight the planet's gravity rather than ignoring it.

## Scope

### In scope

- A persistent throttle driven by W/S, plus a hard brake on X.
- Ship-relative thrust on all three axes: nose (throttle), roof/belly (Space/Ctrl), sides (A/D).
- Mouse pitch and yaw, Q/E roll.
- Hold-Alt free-look that releases the mouse to the camera without changing ship attitude.
- A speed cap sized to the small play area, and light damping so cutting throttle settles the ship.
- Gravity continues to act on the ship, unmodified, from the existing `PlanetGravityBody`.

### Out of scope

- Gamepad, joystick or HOTAS bindings. Keyboard and mouse only, by request.
- Landing gear, docking, autopilot, flight-assist toggles, cruise/quantum modes.
- A HUD, throttle gauge or speedometer. `ShipController` exposes the state a HUD would need;
  drawing it is a separate feature.
- Any change to how boarding works.

## Existing state (verified)

- **Gravity:** the scene's `GravityField` is `gravityStrength: 9.8`, `radius: 70`,
  `falloff: Constant`. Constant means full 9.8 everywhere inside 70 world units and exactly zero
  outside — there is no gradient. The surface sits roughly 48 units from the centre (the player
  spawns at ~48, the ship at ~49), so usable altitude before leaving the field is about 20 units.
- **The ship** already has a `Rigidbody` and a `PlanetGravityBody` with `alignToSurface: 1`.
- **Input:** `Jump` is bound to Space but `Crouch` is bound to **C**, not Ctrl. More importantly
  `PlayerController.OnDisable` disables `Move`, `Sprint` and `Jump` when the player boards, so
  those actions are unavailable while piloting and cannot be borrowed for flight.
- **Camera:** `PlayerCameraControl` already eases its heading back behind its target when look
  input goes idle. That behaviour is a chase cam already; it does not need a second script.
- **No generated C# wrapper** exists for `InputSystem_Actions.inputactions`, so the asset can be
  edited without regenerating anything. Bindings are consumed via `InputActionReference`.

## Control scheme

| Input | Action |
| --- | --- |
| **W / S** | Throttle up / down, 0-100%, holds its value when released |
| **X** | Hard brake: throttle to zero *and* active counter-thrust against current velocity |
| **A / D** | Lateral strafe (side thrusters) |
| **Q / E** | Roll |
| **Space / Ctrl** | Thrust along the ship's own roof / belly |
| **Mouse** | Pitch and yaw the nose |
| **Alt (held)** | Free-look: the mouse drives the camera, ship attitude unchanged |

Vertical thrust is **ship-relative, not planet-relative**: nose up plus Space climbs, banked 90°
plus Space pushes sideways. Fighting gravity means pointing the ship first. There is no
auto-hover — release everything inside the field and the ship falls.

## Architecture

| File | Change | Responsibility |
| --- | --- | --- |
| `Assets/objects/ShipController.cs` | **new** | Flight input and physics. Enabled only while piloted. |
| `Assets/InputSystem_Actions.inputactions` | **new `Ship` action map** | Throttle, Strafe, Roll, Vertical, Brake, FreeLook. Reuses the existing `Look` for the mouse. |
| `Assets/objects/VehicleBoarding.cs` | one serialized field | `pilotedController`, enabled on board and disabled on exit — the hook the boarding design reserved. |
| `Assets/objects/PlayerCameraControl.cs` | one public flag | `lookEnabled`, so piloting can take the mouse away from the orbit camera. |

`ShipController` is deliberately ship-specific and does **not** live in `VehicleBoarding`.
`VehicleBoarding` stays generic — it enables *a* `MonoBehaviour` while piloted and neither knows
nor cares that this one flies. A future car controller plugs into the same field.

### Why a separate `Ship` action map

Not merely tidiness: `PlayerController.OnDisable` disables `Move`/`Sprint`/`Jump` on boarding, so
W, S and Space are genuinely dead while piloting. The `Ship` map is enabled in
`ShipController.OnEnable` and disabled in `OnDisable`, so flight controls exist exactly as long as
someone is flying. `Look` is the one action shared with on-foot play, because
`PlayerCameraControl` is never disabled and keeps it enabled.

## Behaviour

### Lifecycle

```
OnEnable (VehicleBoarding just seated a pilot):
  save gravityBody.alignToSurface -> set false   // else it re-levels the ship every frame
  save rb.linearDamping           -> set linearDamping
  save cameraControl.lookEnabled  -> set false   // mouse now steers the nose
  save cameraControl.idleReturnDelay -> set 0    // camera chases the tail immediately
  enable the Ship action map
  throttle = 0

OnDisable (pilot leaving):
  restore all four saved values
  disable the Ship action map
  throttle = 0
```

The camera is reached through `VehicleBoarding.Pilot.cameraControl`, so `ShipController` needs no
inspector reference to it and a second vehicle needs no camera wiring.

**`alignToSurface` is the critical one.** Left on, `PlanetGravityBody` slerps the ship level to
the planet surface every physics step and fights every mouse movement. Restoring it on exit means
a ship abandoned in mid-air re-levels and falls — accepted, and called out in the playtest notes.

### Per-frame

Mouse delta is accumulated in `Update` and consumed in `FixedUpdate`. Reading a delta control
directly inside `FixedUpdate` double-reads or misses samples depending on how the two rates line
up, which shows up as jittery aiming.

```
Update:
  pendingLook += Look.ReadValue<Vector2>()   // unless FreeLook is held, which
                                             // leaves the value to the camera instead

FixedUpdate:
  throttle = clamp01(throttle + (W - S) * throttleRate * dt)

  yaw   around transform.up      by  pendingLook.x * lookSensitivity  (rate-clamped)
  pitch around transform.right   by -pendingLook.y * lookSensitivity  (rate-clamped)
  roll  around transform.forward by  rollInput * rollRate * dt
  rb.MoveRotation(accumulated)               // freezeRotation stays on: collisions
  pendingLook = zero                         // must not tumble the ship

  accel  = transform.forward * mainThrust     * throttle
         + transform.up      * verticalThrust * verticalInput
         + transform.right   * strafeThrust   * strafeInput
  rb.AddForce(accel, ForceMode.Acceleration)  // gravity is added separately by
                                              // PlanetGravityBody, untouched

  if braking:
      throttle = 0
      velocity = MoveTowards(velocity, zero, brakeDeceleration * dt)

  if velocity.magnitude > maxSpeed:
      velocity = velocity.normalized * maxSpeed
```

Rotation is driven explicitly rather than by torque, matching how `PlayerController` owns the
player's rotation. The pilot has full authority over attitude; physics cannot spin the ship.

### Tuning values

Every one of these is a serialized public field with a `[Tooltip]`, editable in the Inspector
**while playing**, because the intent is to adapt them in game.

```
mainThrust        25 m/s²   at 100% throttle. Above 9.8, so it climbs but has to work for it.
verticalThrust    15 m/s²   Space / Ctrl
strafeThrust      10 m/s²   A / D
throttleRate      50 %/s    about 2s from idle to full
brakeDeceleration 20 m/s²   X
maxSpeed          15 m/s    ~9s to cross the 140-unit field; the player sprints at 8
linearDamping     0.5       cut throttle and the ship settles instead of coasting forever
rollRate          90 °/s    Q / E
lookSensitivity   0.15 °/px matches the existing camera sensitivity
maxTurnRate       120 °/s   clamp on mouse-driven pitch and yaw
```

The speed cap clamps total velocity, so it also caps fall speed — acceptable, and desirable, in a
world this small.

### Camera

Piloting stops feeding `Look` to `PlayerCameraControl` and sets `idleReturnDelay = 0`. Its
existing idle-return then eases the camera behind the ship every frame, which is exactly a chase
cam. Holding Alt feeds `Look` back to it for free-look. No second camera script, and the change to
`PlayerCameraControl` is a single public flag.

## Known hazards, accepted

- **The gravity edge.** Past r=70 gravity is zero and there is no gradient pulling back, so
  climbing out of the field means drifting away permanently. At 15 m/s that is roughly two seconds
  of climbing from the surface. Left as a real hazard by choice; not this feature's problem.
- **Exiting mid-air** re-levels the ship and drops it, because `alignToSurface` is restored.
- **No roll auto-level.** Q/E give direct roll authority, so the ship holds whatever bank you
  leave it at, Star Citizen style.

## Verification

- **Automated, run by the agent:** `./tools/build-check.sh` must report 0 errors, and the edited
  `InputSystem_Actions.inputactions` must re-parse as valid JSON with the new map and its bindings
  present. The compile proves nothing about flight.
- **Manual, run by the user:** the flight checklist below. The agent does not claim it flies.

### Flight checklist

1. Board the ship. The camera sits behind it and does not orbit when you move the mouse.
2. Move the mouse: the nose pitches and yaws, the camera follows the tail.
3. Hold Alt and move the mouse: the camera orbits, the ship holds its attitude. Release: the
   camera eases back behind the ship.
4. Tap W a few times and release: the ship accelerates and keeps accelerating — throttle persists.
5. Hold S until stopped, or tap X: X should kill speed noticeably faster than S.
6. With the nose level, press Space: the ship rises. Pitch the nose up 90°, press Space: it now
   pushes sideways, not up — thrust is ship-relative.
7. Release everything inside the gravity field: the ship falls. There is no auto-hover.
8. Q and E roll the ship; the roll is held when released.
9. A and D slide the ship sideways without turning it.
10. Speed never exceeds 15 m/s no matter how long you burn.
11. Fly up past ~20 units of altitude: gravity cuts out entirely and the ship coasts. Expected.
12. Land and press E: walking controls return, the ship re-levels to the surface, and the camera
    goes back to orbiting the player at its original distance.
