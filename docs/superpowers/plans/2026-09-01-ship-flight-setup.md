# Ship Flight — Editor Setup & Flight Test

The code is in. This is the scene wiring it needs, then the flight test that actually verifies
it. Nothing here has been flown — the only automated check was a headless compile plus a JSON
validation of the input asset. Neither proves the ship flies.

Assumes the boarding setup from `2026-09-01-vehicle-boarding-setup.md` is already done and working.

---

## 1. Add ShipController to the Dropship

Select the **Dropship_R35** root → **Add Component** → `ShipController`.

Then **untick its checkbox** so it starts disabled. `VehicleBoarding` switches it on when you
board and off when you leave.

If you forget, it is not fatal: `OnEnable` disables itself again whenever nobody is piloting, so
an unmanned ship can never be flown by accident.

## 2. Assign the input actions

Six come from the new **`Ship`** map, one from the existing **`Player`** map:

| Field | Action |
| --- | --- |
| `throttleAction` | `Ship/Throttle` |
| `strafeAction` | `Ship/Strafe` |
| `rollAction` | `Ship/Roll` |
| `verticalAction` | `Ship/Vertical` |
| `brakeAction` | `Ship/Brake` |
| `freeLookAction` | `Ship/FreeLook` |
| `lookAction` | **`Player/Look`** — the same one the camera uses |

`lookAction` pointing at the Player map is deliberate, not a mistake. The camera owns that action
and keeps it enabled; the ship only borrows its value while you are flying.

## 3. Connect it to boarding — and move the exit key

On the **`VehicleBoarding`** component (same GameObject):

1. Set **`pilotedController`** to the `ShipController`. Drag the component itself, not the
   GameObject. Without this the ship is still boardable — you just sit there and nothing flies.
2. Set **`exitAction`** to the new **`Player/ExitVehicle`** action (bound to **F**).
3. Change **`exitPrompt`** to `Press F to exit`. The scene still has the old "Press E to exit"
   string saved on the component, and the Inspector value wins over the code default.

**Why the exit key moved.** `E` was bound to both roll-right and disembark. Rolling right in
flight would have ejected you from the ship, mid-air, every time. Since you asked for Q/E roll
specifically, the exit moved rather than the roll. Boarding is still **E**; only exiting is F.

If you leave `exitAction` empty, `Interact` both boards and exits as before — which is still
right for an inert boardable like a turret that has no use for E.

## 4. Controls

| Input | Action |
| --- | --- |
| **W / S** | Throttle up / down, 0-100%. Holds its value when you let go. |
| **X** | Hard brake — throttle to zero *and* active counter-thrust |
| **A / D** | Strafe sideways without turning |
| **Q / E** | Roll |
| **Space / Ctrl** | Thrust out of the ship's own roof / belly |
| **Mouse** | Pitch and yaw the nose |
| **Alt (hold)** | Free-look — mouse goes to the camera, ship holds attitude |
| **F** | Disembark (moved off E, which now rolls) |

---

## 5. Flight test

Run these in order. This is the real verification.

1. **Board the ship.** The camera sits behind it and does *not* orbit when you move the mouse.
2. **Move the mouse.** The nose pitches and yaws; the camera follows the tail.
3. **Hold Alt and move the mouse.** The camera orbits, the ship holds its attitude. Release: the
   camera eases back behind the ship.
4. **Tap W a few times and release.** The ship keeps accelerating — the throttle persists rather
   than needing to be held.
5. **Hold S until stopped, then build speed again and tap X.** X should kill speed noticeably
   faster than S does.
6. **Nose level, press Space** — the ship rises. **Now pitch the nose up 90° and press Space** —
   it pushes sideways, not up. That is thrust being ship-relative, and it is the point.
7. **Release everything inside the gravity field.** The ship falls. There is no auto-hover.
8. **Q and E roll**, and the roll is held when you release.
9. **A and D slide** the ship sideways without turning it.
10. **Speed stays at about 15 m/s** no matter how long you burn. (The clamp is applied before
    the physics step integrates that step's thrust, so a brief overshoot of roughly 5% is
    expected and self-corrects.)
11. **Climb past ~20 units of altitude.** Gravity cuts out entirely and the ship coasts. Expected
    — see hazards below.
12. **Press E while flying** — nothing should happen. It rolls, it does not eject you.
13. **Land and press F.** Walking controls return, the ship re-levels to the surface, and the
    camera goes back to orbiting the player at its original distance.

If a step fails, say which number and what happened.

---

## 6. Tuning

Every value on `ShipController` is live-editable **while playing**, which is how these were meant
to be found. Play, fly, drag the numbers, then write down what you liked (Play-mode edits are
discarded on exit — set them again outside Play mode to keep them).

| Symptom | Field to change |
| --- | --- |
| Q/E roll the wrong way | Set `rollRate` **negative** |
| Steering too twitchy / too sluggish | `lookSensitivity` first, then `maxTurnRate` |
| Can't climb, or climbing is too easy | `mainThrust` (gravity is 9.8, so this must stay above it) |
| Ship coasts too long after cutting throttle | `linearDamping` up |
| Throttle ramps too slowly | `throttleRate` (0.5 = ~2s to full) |
| X doesn't stop you fast enough | `brakeDeceleration` |
| Too fast / too slow overall | `maxSpeed` |

If the ship's attitude judders visibly during turns, set the Dropship Rigidbody's **Interpolate**
to `Interpolate`. Physics runs at 50 Hz while the screen may run far faster, and rotation is
driven on the physics clock.

## 7. Known hazards, by design

- **The gravity edge.** The field is `Constant` falloff with radius 70, so past that boundary
  gravity is exactly zero with no gradient pulling you back. Climb roughly 20 units off the
  surface and you leave it — and then you drift away permanently. At 15 m/s that is a couple of
  seconds of climbing. Turn around and burn back before you coast out.
- **Exiting in mid-air drops the ship.** Disembarking restores `alignToSurface`, so the ship
  re-levels to the planet and falls. Land first.
- **No roll auto-level.** Q/E give you direct authority and the ship holds whatever bank you
  leave it at, Star Citizen style. If you get disoriented, roll back manually.

## 8. If something doesn't work

- **Nothing responds at all while seated.** Check `pilotedController` is assigned on
  `VehicleBoarding` (§3), and that the seven action fields are filled (§2).
- **Pressing E ejects you instead of rolling.** `exitAction` is not assigned (§3), so `Interact`
  is still handling exit and E is doing double duty.
- **The ship fights the mouse and keeps levelling itself.** `ShipController` sets
  `alignToSurface = false` on the ship's `PlanetGravityBody` while piloted. If the ship still
  self-levels, confirm the `PlanetGravityBody` is on the same GameObject as `ShipController`.
- **The camera still orbits instead of steering the nose.** `lookAction` must point at
  `Player/Look` — the same action the camera reads.
- **The camera stays in chase mode after disembarking.** Report it; that is the ordering between
  `ShipController.OnDisable` and `PlayerRig.Restore`.
