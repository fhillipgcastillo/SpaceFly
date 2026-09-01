# Vehicle Boarding — Editor Setup & Playtest

The code is in. This is the scene authoring it needs, then the checklist that actually
verifies it. Nothing here has been run — the only automated check performed was a headless
compile, which proves the scripts build and proves nothing about behaviour.

Scene: `Assets/Scenes/SampleScene.unity`.

---

## 1. Player setup

Select **Player** in the Hierarchy → **Add Component** → `PlayerRig`.

Leave every field empty. `controller`, `gravityBody` and `bodyCollider` auto-fill from the
same GameObject in `Awake`, and `cameraControl` auto-finds the scene's `PlayerCameraControl`.
Assign `cameraControl` explicitly only if you ever have more than one in the scene.

## 2. Seat anchor

1. Right-click **Dropship_R35** → **Create Empty**. Name it `Cockpit_Seat`.
2. Move it to where the pilot should sit inside the cockpit.
3. Rotate it so its **blue Z axis points where the pilot should look**. The seat's rotation is
   what sets the pilot's facing — the player is snapped to `localRotation = identity` against
   it.

Once step 4 is done, a green wire sphere and a forward ray mark it in the Scene view.

## 3. Exit point — the important one

1. Right-click **Dropship_R35** → **Create Empty**. Name it `Exit_Point`.
2. Place it on the ground beside the hatch, **outside the trigger BoxCollider**. That is the
   collider on the Dropship root with **Is Trigger** ticked — size `3.16 × 4.66 × 0.5`, center
   `(-0.07, 2.67, -0.82)`. Select the Dropship root and look at the collider gizmos to see it.
3. Keep it clear of the four solid BoxColliders and the child MeshColliders, or physics will
   shove the player on restore.
4. Point its forward (blue Z) **away from the hull**, so the player faces out when they step
   off.

It shows as an orange wire sphere with a forward ray. **Check the orange sphere is not inside
the trigger box.** That placement is one of the three things preventing an instant re-board.

## 4. VehicleBoarding

Select the **Dropship_R35** root → **Add Component** → `VehicleBoarding`.

| Field | Value |
| --- | --- |
| `seatAnchor` | `Cockpit_Seat` |
| `exitPoint` | `Exit_Point` |
| `interactAction` | the `Interact` action from `InputSystem_Actions` |
| `prompt` | leave empty — auto-finds the one in the scene |
| `boardPrompt` | `Press E to pilot the dropship` (default) |
| `exitPrompt` | `Press E to exit` (default) |
| `pilotCamDistance` | `18` (default, suits the dropship) |
| `pilotCamFocusHeight` | `4` (default) |

To assign `interactAction`, click the field's circle-select and pick `Interact` from
`InputSystem_Actions`, the same way `Move` / `Jump` / `Sprint` are assigned on the Player's
`PlayerController`.

## 5. Prompt UI

1. **GameObject → UI → Canvas**. This also creates an `EventSystem` — keep it.
2. With the Canvas selected: **GameObject → UI → Text**. Choose the **legacy** `Text`, not
   TextMeshPro (TMP's essential resources are not imported in this project).
3. Anchor the Text bottom-center, set a readable font size and a colour that reads against the
   sky, and give it any placeholder string.
4. Select the **Canvas** → **Add Component** → `BoardingPrompt`, and drag the `Text` into its
   `label` field.

It hides itself in `Awake`, so the placeholder string will not be visible at play time.

## 6. Recommended: move the Camera out of Player

Drag the **Camera** from under **Player** to the scene root.

It works either way — `PlayerCameraControl` writes a full world pose every `LateUpdate`, so
inheritance gets overwritten each frame. But while the player is parented to the ship, the
camera inherits ship motion *before* its own smoothing lerp runs, which makes
`followSmoothing` effectively rigid. Not a bug, just worse feel.

---

## 7. Playtest checklist

Run these in order. This is the real verification.

1. **Approach.** Walk into the hatch. The prompt reads **"Press E to pilot the dropship"**.
2. **Walk away without pressing.** The prompt disappears. Nothing else changes.
3. **Board.** Standing in the zone, press **E**. The player snaps into the cockpit seat, the
   camera pulls back to frame the whole ship, and the prompt switches to **"Press E to exit"**.
   The player should be the **same size** as before — if they visibly grow by about 1.4x, the
   scale compensation is wrong.
4. **Ride.** WASD and Jump do nothing. The player does not fall through the ship, does not
   drift off the seat, and stays visible in the cockpit.
5. **Exit.** Press **E**. The player appears at `Exit_Point`, on the ground, standing upright
   relative to the planet surface, with walking controls back and the camera framed on them at
   its original distance.
6. **No re-board.** That single press in step 5 must not put you straight back in the ship, and
   the prompt must not immediately reappear.
7. **Repeat.** Walk back into the hatch and board again. The whole cycle repeats cleanly, with
   the camera distance and the player's scale identical to the first time.

If any step fails, say which number and what happened — the failure point maps directly onto a
specific mechanism in the design.

---

## 8. Adding another vehicle later

No code changes and no UI changes:

1. Add a **trigger collider** to the new vehicle's root for the doorway volume.
2. Add **`VehicleBoarding`** to that root.
3. Author two empty children: a seat and an exit point (exit outside the trigger, as above).
4. Set `boardPrompt` to suit it — `"Press E to drive the rover"`.
5. Tune `pilotCamDistance` / `pilotCamFocusHeight` — roughly `8` / `1.5` for a car-sized thing.

A `Rigidbody` and a `PlanetGravityBody` on the vehicle are both optional. Without a Rigidbody
the player simply inherits no momentum on exit, so a static object — a turret, a console, a
docked pod — is boardable too.

---

## 9. Notes from adversarial review

Fixes applied after review, worth knowing when you playtest:

- **The `Interact` action has a `Hold` interaction** (0.4s default). The code subscribes to
  `started`, not `performed`, so a **tap** works as the prompt says. If you ever swap the action,
  check its interaction — with `performed` a tap does nothing at all and the feature looks dead.
- **One press = one action, scene-wide.** `InputActionReference` resolves to the shared action,
  so every boardable receives the same press. A static frame guard plus clearing the boarded rig
  from all other zones stops one press from exiting one vehicle and boarding another.
- **The vehicle disembarks its pilot in `OnDisable`.** The player is parented to the ship, so
  deactivating or destroying the ship would otherwise deactivate or destroy the player with it.
- **`BoardingPrompt` toggles `label.enabled`, never `SetActive`.** Deactivating its own
  GameObject would hide it from the scene lookup that finds it, permanently.
- **A queued jump is now cleared** when `PlayerController` is disabled, so pressing Space and E
  in the same frame no longer makes you jump the moment you disembark.

Known limitations, deliberately not fixed:

- With two overlapping boarding zones, walking out of one hides the shared prompt even if you
  are still standing in the other. Cosmetic; the second vehicle is still boardable.
- Seat scale compensation is exact only for uniformly-scaled vehicles. The dropship is uniform
  (1.4172), so this does not affect it.

---

## 10. Troubleshooting

- **The prompt vanished and E does nothing.** Check the Console. If `seatAnchor` or `exitPoint`
  is unassigned, the first press logs an error, consumes the zone arming and hides the prompt —
  you have to walk out of the trigger and back in to retry. Assign both (§2, §3) and re-enter.
- **No prompt ever appears, no errors.** The `BoardingPrompt`'s GameObject is probably inactive;
  a warning naming it is logged on start. Activate it — `BoardingPrompt` hides the *label*, so
  its own object must stay active.
- **E does nothing at all, anywhere.** Confirm `interactAction` is assigned. The action carries a
  `Hold` interaction; the code subscribes to `started` so a tap works, but an unassigned
  reference silently disables the whole feature.

## 11. Before adding a second vehicle

Two things to know, neither of which affects the single dropship:

- Which vehicle wins a press, when you stand in two overlapping zones, is decided by component
  activation order, not by proximity. If you want proximity, that needs a deliberate rule.
- The two zones share one prompt label, so walking out of one hides the prompt while the other
  is still boardable.
