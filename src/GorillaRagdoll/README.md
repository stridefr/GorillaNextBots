# GorillaRagdoll — a GMod-style ragdoll engine for Gorilla Tag

Press a key, your gorilla goes limp and tumbles with real jointed physics. Press it again and
you stand up where you landed. Camera modes let you watch it from inside your own head or
from a free-flying camera on the monitor.

**Status: working in game, still being tuned.** The rig layout and input backend below are
verified from live runs, not assumed. See [First run](#first-run) for how to check a build.

Separate plugin from NextBots — its own GUID, config file and plugin folder — so neither can
break the other.

---

## Build

```
cd src/GorillaRagdoll
dotnet build -c Release
```

Deploys `GorillaRagdoll.dll` into
`C:\SteamLibrary\steamapps\common\Gorilla Tag\BepInEx\plugins\GorillaRagdoll\`.
Pass `-p:NoDeploy=true` to skip. Game path is set once in `GorillaRagdoll.csproj` (`<GameDir>`).

## Controls

| Input | Does |
|---|---|
| **F6** | Go ragdoll / get up |
| **F4** | Settings overlay (monitor) |
| **F10** | Panic — unconditional restore |
| **F11** | Dump the avatar's bone hierarchy to the log |
| Left controller **X** | Go ragdoll / get up, in VR (hold ~0.3s) |
| Hold **right mouse** | Look around (`ThirdPersonFree`, `OrbitAim = Free`); swing around the body with `OrbitAim = LockOnBody` |
| **A / D**, **W / S** | Circle the body / raise and lower the camera (`ThirdPersonFree`, `OrbitAim = Free`) |
| **C** or middle mouse | Look back at the body |
| Scroll | Zoom in / out |
| WASD + QE | Fly the camera (in `ThirdPersonFly` mode only) |
| Shift / Ctrl | Fly-cam boost / crawl |

All bindings are configurable, and the VR button can be changed from the overlay's Ragdoll tab.
Keyboard only reaches the game **while the desktop window has focus** — i.e. never while you are
wearing the headset — so the VR binding is the one that matters in play.

`VrBinding` defaults to `LeftPrimary` (left **X**). Left **Y** is deliberately avoided because
NextBots uses it for its wrist panel on this install. Every binding, not just the grip gesture,
honours `VrGestureHold`: a face button that ragdolls the instant it is brushed will fire while
you are climbing or tagging, and the first you know about it is lying on the floor.

---

## How it works

**The visible gorilla is never ragdolled.** That is the whole trick, and it took several wrong
turns to find. Two objects do the work:

1. **[RagdollPuppet.cs](Runtime/RagdollPuppet.cs)** — an *invisible* jointed skeleton (torso,
   head, shoulders, arms, hands) built at runtime from your live rig's bone poses. It has
   rigidbodies, capsules and `CharacterJoint`s, and it falls over. Nothing renders it.
2. **[RigDriver.cs](Runtime/RigDriver.cs)** — switches `VRRig` off and feeds the puppet's head
   and hand poses into GT's own IK targets. **The game draws the ragdoll for us.**

```csharp
TickSystem<object>.RemovePostTickCallback(rig);          // stop it writing tracking data
rig.transform.position += puppet.Body.position - sourceBody.position;
rig.leftHand.rigTarget.SetPositionAndRotation(puppet.HandL...);
rig.head.rigTarget.rotation = puppet.Head.rotation;
```

### A Burst job owns the bones

This is the one that matters, and it invalidates the obvious approach entirely.
`GorillaIKMgr` schedules a Burst `IJobParallelForTransform` **from its own LateUpdate**, which
writes the avatar's bones directly:

```csharp
if (index % 8 <= 4) xform.localRotation = transformRotations[index];  // arms, body
else                xform.rotation      = transformRotations[index];  // head, hands
if (index % 8 >= 6) xform.localPosition = transformPositions[index];  // hands
```

Read the indices against `CopyOutput`, which fills them 8 per rig in this order: `leftUpperArm`,
`leftLowerArm`, `rightUpperArm`, `rightLowerArm`, `bodyBone`, `headBone`, `leftHand`,
`rightHand`. So the **head bone gets a world rotation and nothing else** — only the hands
(`% 8 >= 6`) get a position. Nothing in the game ever writes the head bone's position; it hangs
off `rig` at a fixed `localPosition (0, 0.0114, 1.6582)`.

That single fact rules out a whole class of theory. A head that appears detached from the torso
has been *rotated*, or has stopped being written at all. It cannot have been moved.

**This job is the mechanism, not an obstacle.** An earlier version fought it by unregistering
the rig (`GorillaIKMgr.Instance.DeregisterIK`) and posing the bones directly. That is a bad
trade: it takes the arms away from the solver that knows how to bend them, and `DeregisterIK`
has a real index bug — it finds the *list* index and then shifts `job.constantInput` from that
index rather than from `index * 2`, corrupting the IK constants of every rig after it in the
list. Stay away from it. Feed the targets and let the job draw the gorilla.

### The head target is never written back

`head.rigTarget` is `VR Constraints/Head Constraint` — outside the rig hierarchy, confirmed at
runtime, `localPosition (0, 1.6582, -0.0114)`. Two things follow, and the second one caused the
longest-lived bug in this mod.

**It is driven differently from the hands.** `VRMap.MapMine` writes its `rigTarget` from
`overrideTarget` if there is one, and otherwise from `InputDevices` — and only if that device
reports both `devicePosition` and `deviceRotation`:

| Map | `overrideTarget` | Rewritten every frame? |
|---|---|---|
| `leftHand` | `LeftHandGorilla` | yes |
| `rightHand` | `RightHandGorilla` | yes |
| `head` | **`null`** | **no** — the device read does not come back on this headset |

Measured across a whole session, `Head Constraint`'s local pose never moves off
`lp=(0, 1.6582, -0.0114) lr=(7.61, 0, 0)`. It is a **fixed rest offset**. Your head tracks
because its *parent* follows the headset and the Burst job copies its world rotation onto the
head bone — not because anything writes the target itself.

**So writing to it is permanent.** `RigDriver.Tick` sets `head.rigTarget.rotation` every frame
while ragdolled, and nothing ever puts it back. Each collapse baked the corpse's final head
angle into that rest offset for good; the head hung off the neck with the hat (`head_end`) and
glasses (`head`) still correctly attached to it, and it compounded every cycle because the next
puppet was built from the already-wrong bone. The hands were always fine, and that asymmetry is
the whole tell.

`RigDriver` therefore records the rest pose in `Begin` and restores it in `End`. That is exact
rather than approximate, precisely because the value is a constant. It is also harmless on a rig
where `MapMine` *does* drive the head, since the next frame simply overwrites it.

The head target gets **rotation only** from the puppet, because that is all the job reads from
it. The hands get position and rotation.

### The driver moves the skeleton root, so it must put it back

`RigDriver` poses the avatar by moving **`rig`** — the object under `VRRig.transform` that the
entire visible body hangs off, VRRig's `renderTransform` — rather than the VRRig root. That is
deliberate: the main camera is a child of the VRRig object, so moving the root drags the headset
along and welds your view to the corpse. Moving `rig` moves the body and leaves the camera, a
sibling, free. It is also the same lever GT pulls itself to show a rig somewhere it is not
(displacement zones).

The catch is putting it back. `rig` rests at a constant `localPosition (0, -1.65, 0)`, and VRRig
only restores it in `OnDisable` and inside a `PostTick` block guarded by **`if (!isLocal ...)`**
— neither of which runs for your own avatar. The first version of this change restored only its
*rotation*, on the assumption, true of the VRRig root and false of this, that `PostTick` would
handle position. A probe diff caught the result:

```
t.rig   was lp=(0.0000,-1.6500,0.0000)
        now lp=(-1.2386,-1.4426,3.4912)      d(head,vrrig): 0.014 -> 3.70
```

The whole body — head, torso, arms, cosmetics — hanging 3.7m from where you were, drifting
further every collapse, with every bone individually correct relative to a root in the wrong
place. `RigDriver` now records its full local pose at collapse and restores both halves on the way
out. The `InPlace` get-up blend eases it in local space towards that rest pose, since easing
towards "what the game just wrote" means easing towards the corpse when the game writes nothing.

**Do not pin it during a ragdoll.** A later attempt to cure the drift by holding `rig` at rest
every frame undid the driver's own write in the same frame, and the torso stopped moving
entirely. The drift is not a stray writer to be fought — it is the ragdoll. It only needs undoing
once, at the end. The watchdog's idle check is the safety net for anything else.

### The rig is a frame behind the headset

`VRRig.PostTick` poses the whole avatar from the camera **in LateUpdate**. So for the entire
following Update — which is when a collapse is triggered — the rig corresponds to where your head
*was*, not where it is. Standing still the two are the same place and nothing shows.

That gap is why the cosmetics drifted further the faster you moved. Head cosmetics hang off
`Main Camera/HeadCosmetics` and so are always current; re-parenting one onto the head bone with
`worldPositionStays` preserves its *current* world pose against a *stale* anchor, baking the
difference in as a fixed local offset for the rest of the ragdoll. One frame of head travel — a
few centimetres at a walk, a foot at speed.

[RagdollController.cs](Runtime/RagdollController.cs) therefore records the camera pose every
LateUpdate, but **only while `PostTickRunning`** — during a ragdoll the game is not posing the rig
from the camera at all, and recording then would poison the next collapse. The re-anchor
re-evaluates each headset-riding item against that pose instead of the live one, which cancels
the lag exactly rather than approximately.

> The diagnostic that finally caught this is [RigProbe.cs](Runtime/RigProbe.cs): it dumps the
> local TRS of every transform under the rig, plus the IK wiring and cosmetic parents, and prints
> only what differs from a baseline. Local TRS is the point — world positions all move together
> when the rig moves and prove nothing. An earlier hand-rolled check missed the bug by one field:
> it logged the head target's local *position*, which this never damages, and not its rotation.

### Never disable VRRig

`VRRig.enabled = false` looks like the obvious way to stop the rig fighting you. It is a trap:
`OnDisable` is a **teardown**, not a pause. It nulls `netView`, `voiceAudio` and `creator`,
clears `initialized` and `initializedCosmetics`, resets the skin and material, resets scale to
one, and resets the render transform — and `OnEnable` restores almost none of it. Every ragdoll
therefore destroyed a little more rig state permanently, which showed up as an avatar that got
visibly worse with each cycle rather than failing outright. Cosmetic drift and a mangled head
pose after two collapses were both this.

What actually needs stopping is one method: `VRRig.PostTick()`, which writes the rig transform
and maps the head/hand IK targets from tracking. It runs off `TickSystem<object>`, so removing
that single callback stops exactly the thing that conflicts and touches nothing else. The type
and its interface are `internal`, so [RigDriver.cs](Runtime/RigDriver.cs) reaches them by
reflection, with the old `enabled = false` kept only as a loudly-logged fallback.

This also makes restoring free. `PostTick` writes the rig's position **and** rotation from live
tracking every frame (`SetPositionAndRotation(syncPos, GTPlayerTransform.BodyRotation)`) and
re-maps the IK targets — so letting it run again *is* the restore. Nothing needs recording and
replaying, which is what earlier attempts got wrong in both directions.

*(Driving the rig through its IK targets is an approach GoldenTrophy's RagdollMod also uses —
worth acknowledging. Everything here is built independently: the puppet is assembled at runtime
from your live rig rather than shipped as an authored prefab, the body is aligned by measured
delta rather than by hardcoded paths, and cosmetics are re-anchored structurally.)*

### Why not just ragdoll the bones

Because GT's avatar is an **IK rig, not an animation skeleton**. Verified layout:

```
rig                        <- sits 1.65m BELOW the avatar (VRRig renderTransform offset)
├── hand.L, hand.R         <- IK targets, direct children of rig
├── head                   <- IK target, direct child of rig
└── body_pivot
    ├── body
    ├── shoulder.L -> upper_arm.L -> forearm.L
    └── shoulder.R -> upper_arm.R -> forearm.R
```

`GorillaIK` solves `upper_arm`/`forearm` to span shoulder-to-hand. **Those rotations are solver
output.** Give them rigidbodies and you overwrite the solver: the arm stops connecting to
anything, so it reads as detached and over-long, and it sweeps through geometry because a joint
constraint outranks a contact. No amount of mass or limit tuning reaches that — the fix is to
stop simulating them and let the IK do its job.

Two more traps in the same rig. The 32 mesh bones are `*_new` **leaves** (`body_new` under
`body`, with `_nhd`/`_skl` siblings for the other body types) — zero-length markers, useless as
physics bones. And the renderer's `rootBone` is `body_new`, which has **no children at all**, so
reading the hierarchy from it finds a one-bone skeleton and silently tears the mesh apart.

Because the puppet is invisible, it is freed from all of this: it does not have to look like a
gorilla or satisfy the IK, only to fall like a body. That is the shape physics engines are
actually good at.

Also worth knowing: **GT gorillas have no legs.** A gorilla is a torso, a head and two arms,
and the arms are long enough to reach the floor.

### Active ragdoll (experimental)

`ActiveMode = Assisted` gives every joint a motor. The implementation leans on one convenient
fact: a `ConfigurableJoint` with `targetRotation = identity` holds **the configuration it was
created in** — and the joints are created at the instant you collapse, so the body's target pose
*is* the pose you were standing in. No authored pose, no animation data, nothing to keep in sync
with the rig.

The spring starts at zero and ramps over `ActiveRecover`, so the body crumples first and gathers
itself afterwards rather than snapping rigid on frame one. Impacts are detected from the torso's
frame-to-frame change in velocity rather than collision callbacks — that needs no extra
components and catches being launched or yanked by a joint just as well as hitting a wall.
Anything over `ActiveImpactThreshold` knocks it limp and the ramp restarts.

`Off` keeps the original `CharacterJoint` path untouched, so the limp ragdoll cannot regress.

**Roadmap towards Euphoria-style behaviour.** The motors above are the foundation; the
behaviours are what would sit on top, roughly in order of value per unit of risk:

1. **Protective bracing** — raycast along the torso's velocity; if an impact is coming within
   ~0.3s, drive the hands toward the predicted contact point. This is the single most
   recognisable Euphoria behaviour and needs only a per-joint target override.
2. **Head protection** — when falling and the head leads, pull the arms toward the head.
3. **Righting** — torque the torso toward upright once the body is slow and supported, so it
   rolls onto its front instead of settling on its back.
4. **Windmilling** — while airborne with no predicted contact, swing the arms.
5. **Grab** — if a hand ends up near a `GorillaClimbable`, latch onto it.

All five need the same missing piece: **per-joint target rotations that can be steered at
runtime**, rather than the single frozen target used today. That means storing each joint's
start local rotation and driving `targetRotation` through the standard joint-space conversion.
Worth building once, then each behaviour is a small function that proposes hand/head targets and
a weight, blended by priority.

The honest constraint: **GT gorillas have no legs**, so balance-and-step — the thing Euphoria is
most famous for — is not available. What is available is arm bracing, head protection and
righting, which is most of what actually reads on screen.

### The physics layer is chosen at runtime

Hardcoding it fails in two opposite ways: pick a layer the collision matrix excludes from
world geometry and the ragdoll falls through the map; pick a gameplay layer like
`Gorilla Tag Collider` and the ragdoll starts tagging people.

So [RagdollLayer.cs](Runtime/RagdollLayer.cs) scores a whitelist of inert layers
(`Prop`, `BuilderProp`, `Default`, `Gorilla Object`) against the layers GT actually walks on —
`GTPlayer.LocomotionEnabledLayers`, which is `Default`, `Gorilla Object`, `BuilderProp`,
`NoMirror` on this build — and takes whichever can touch the most of them. **The decision is
logged**, so "my ragdoll fell through the floor" is a one-line diagnosis.

---

## Cameras

The headset and the monitor are driven completely differently, because they have to be.

**The headset has no camera object to move.** The HMD's local pose is written by XR tracking
every frame — GT itself works around this by subtracting `mainCamera.transform.localPosition`
from the player root. So [VrView.cs](Cameras/VrView.cs) relocates the view by translating and
rotating the *player rig* about the current eye position. Which means the rig — and therefore
your networked position — goes where the camera goes.

**The monitor gets its own camera** ([MonitorCamera.cs](Cameras/MonitorCamera.cs)), using the
pattern QuickFreeCam already proves on this install: clone the highest-depth non-stereo
camera, force `stereoTargetEye = None`, sit above the source in depth, and carry the URP
renderer data across. Build a camera from scratch under URP instead and you get a grey screen.

| Mode | Headset | Monitor |
|---|---|---|
| `FirstPersonUnlocked` *(VR default)* | Eye rides the ragdoll's head, you still aim | Same, mouse-look |
| `FirstPersonLocked` | Also rides the head's rotation — comfort-clamped | Rides it fully |
| `ThirdPersonFree` *(monitor default)* | Orbits the ragdoll on the sticks; your head's look is never touched | Tethered to the body; the mouse aims it (`OrbitAim = Free`) or it stays aimed at the body (`LockOnBody`) |
| `ThirdPersonFly` | Same as above | WASD free cam |
| `Off` | Headset untouched | No mod camera |

The monitor orbit camera eases in from wherever the game camera was rather than cutting, and
damps its follow so a tumbling body does not make it judder. Its **aim is exact, never eased** —
smoothing the rotation lets a moving body drift out of frame, so the *point it looks at* is
smoothed instead and the gorilla stays centred.

Occlusion is handled twice over, because either alone fails:

- A spherecast from the body outwards **pulls the camera in front of anything blocking the
  view**. On its own, in a tight room, that means the lens ends up against the gorilla's back.
- So anything still in the way is **faded to `OccluderAlpha`** ([OccluderFade.cs](Cameras/OccluderFade.cs)).

The hazard there is that GT's world geometry reuses one material asset across the whole map —
editing it would turn *every* wall using it transparent. So the fade works on per-renderer
material instances (`renderer.materials`), never the shared asset, and restores the originals
and destroys the instances the moment a surface stops blocking. Every shader property write is
guarded by `HasProperty`, so a custom GT shader that lacks them simply does not fade rather than
throwing.

One honest caveat: a material is not per-camera, so a wall faded for the monitor is faded in the
headset too while it blocks. It only ever affects surfaces directly between the camera and your
own body, and everything is restored on get-up. `SeeThroughWalls` turns it off.

Two things stated plainly rather than buried:

- **VR third person moves your networked body to the camera.** Other players see you floating
  behind your own ragdoll. That is the honest cost of putting the headset where the body
  isn't, and it is exactly why the monitor gets a separate camera.
- **`FirstPersonLocked` in VR will make most people ill.** A tumbling ragdoll driving a VR
  horizon is genuinely unpleasant. Roll is off by default and pitch is clamped to 35°; both
  are under *Camera (VR comfort)*. **F10 always exits**, whatever the camera is doing.

`BodyFollow` only applies when `VrMode` is `Off` — otherwise the headset mode already decides
where the rig goes.

### VR orbit and GT's own turning

**GT's snap/smooth turn keeps running while you are ragdolled**, and it reads the right stick's
X axis, which is the same axis the VR orbit uses. Unpaused, every orbit push also snap-turned
you about your head: two unrelated rotations at once. `GorillaSnapTurn` has a supported way to
pause it (`SetTurningOverride` with an `ISnapTurnOverride`; GT's own `SnapTurnOverrideOnEnable`
uses it), and [VrView.cs](Cameras/VrView.cs) holds that pause only while the orbit is on screen.
One trap there: GT removes inactive overriders from the set *while iterating over it*, which
throws. So the override always reports active and is removed explicitly instead.

The orbit also follows the body with a deadzone (`VrFollowDeadzone`, 0.5 m). A ragdoll rocks and
settles for seconds after it lands, and following each of those small movements shifts your whole
view in the headset. A real flight is still followed.

If anything else turns the view during the orbit, the log says so once per ragdoll:
`[VrView] something other than the ragdoll turned your view while orbiting`.

---

## First run

Test in this order; each step de-risks the next.

1. **Offline or a private room.** Public lobbies are refused by default
   (`RestrictToPrivateRooms`) — your networked body moves for everyone else, so this is not a
   cosmetic mod from their side.
2. **Press F11 first** and read the skeleton dump in `BepInEx/LogOutput.log`. It prints every
   bone with depth, length and child count, plus a full `[Probe]` snapshot. This is the ground
   truth the bone settings should be tuned against.

   The probe also logs itself at `pre-collapse`, `post-getup` and `settled-after-getup`
   (1.5s later, skipped if you have already collapsed again). The first `pre-collapse` becomes
   the baseline — fully dressed, idle, untouched — and everything after is a diff against it.
   **A clean `settled-after-getup` diff is the definition of "the mod put everything back".**
3. **Check the layer line** — `[Layer] ragdoll on 'Prop' (30) | ... scores: ...`. If the
   winning score is 0, nothing will collide and the ragdoll will fall through the world.
4. **Set `MonitorMode = ThirdPersonFree` and `VrMode = Off`** for the first collapse. That
   watches the ragdoll from outside without moving your view at all — the safest possible
   first test.
5. Then try the first-person modes.

### If something looks wrong

| Symptom | Likely cause |
|---|---|
| Falls through the floor | Layer scores all 0 — see the `[Layer]` log line |
| Ragdoll stops dead instead of carrying your speed | `InheritPlayerVelocity` off, or you were in a state where both the rigidbody and the body tracker read zero |
| Cosmetics drift further the faster you move | The camera-lag correction did not run — check `[Cosmetics]` named the item as "rode the headset" |
| "no usable bones - see log" | Raise `MaxBoneDepth`; check the F11 dump for the real names |
| Limbs missing | `MaxBoneDepth` too low, or names hit a finger exclusion |
| Jitters / explodes | Lower `JointLooseness`, raise `SolverIterations`, turn off `SelfCollision` |
| Locked first person looks sideways | Set `HeadRotationOffset` — the head bone's forward axis is a rig detail |
| Head detached from the body after getting up | Something wrote a rig transform the game never restores. Read the `[Probe] settled-after-getup` diff in the log — anything listed there is by definition not being put back |
| Whole body floating away from you | The skeleton root was not restored — check the `[Restore] ... pose root 'rig' restored from` line, and `t.rig` / `d(head,vrrig)` in the probe diff |
| Ragdoll invisible | Skin layer vs camera culling mask; check the `[Clone]` log line |
| Grey monitor image | URP data copy failed — logged as a warning |

Config lives in `BepInEx/config/com.stridefr.gorillaragdoll.cfg`. **BepInEx prefers the file over
the code default**, so editing a default in source does nothing once the file exists — delete
it to re-default. (NextBots paid for that lesson already.)

---

## Momentum

A ragdoll that stops dead the instant you trigger it reads as a bug even when everything else is
right, so momentum is carried in **both** directions.

**Going down.** `PlayerSuspension.Begin` captures your velocity and *then* freezes you, and the
puppet is seeded from what it captured. That ordering is the whole feature: the hold zeroes
`playerRigidBody`, so anything that asks `GTPlayer` for its velocity afterwards gets exactly
zero — which is what used to happen, and why `InheritPlayerVelocity` did nothing at all and
every ragdoll dropped from a standstill. The capture lives inside the method that destroys the
value so the two cannot be separated again. It prefers the rigidbody and falls back to
`bodyVelocityTracker`, which still reads true mid-climb or on a handhold where the rigidbody
does not.

**Coming back up.** `TeleportTo` always lands you stopped — it zeroes the rigidbody unless told
to keep velocity, and what it would keep is the zero the hold left there. So the torso's velocity
is handed back explicitly with `SetPlayerVelocity` after the teleport.

## Getting up

`GetUp = Respawn` (default) drops you in just above where the body came to rest, then hard-resets
the rig — no blending, nothing left half-restored.

The height is not a fixed offset: standing up exactly where a ragdoll landed is asking for
trouble, because a corpse settles half-inside geometry all the time — wedged against a log, under
a ledge, on a slope. So the ground beneath the body is found first, the spot above it is **tested
for clearance**, and it is raised until it is genuinely empty. If nothing works it falls back to
where you collapsed, which was by definition a legal place to stand.

**Unless the body is still in the air**, which changes the answer completely. All of the above
assumes there is a floor to stand on; a body mid-fall has none, and "just above the ground" when
the search never found ground is how you ended up hanging in mid-air, stopped dead, half way
through a drop. So getting up asks whether the ground search actually *hit* anything rather than
trusting the position it returns. If it did not, you stand up exactly where the body is and the
ragdoll's velocity comes with you — the fall simply continues as though you had never gone limp.

Finding no ground is **not** the same as being airborne, though, and conflating the two put people
back floating. The search only looks for surfaces GT lets you walk on, so a body resting on a
prop, a table, or any non-walkable layer comes back empty too. A body only counts as airborne if
it is also still *moving*; at rest it falls through to the clearance-tested placement, which has
the collapse point as its final fallback.

`GetUp = InPlace` keeps the older behaviour: come round exactly where you fell, easing back into
tracking over `GetUpBlend`.

### Standing up the right way round

The rotation to stand up in is worked out *before* the teleport and used whether or not it
succeeds. It used to be derived inline and applied only through `TeleportTo`, so anything that
threw in there — `ClearHandHolds` on a rig that collapsed mid-climb, for one — left you at the
right place still wearing the corpse's tilt.

Two things it must not do. It must not read `eulerAngles.y` for the heading: that decomposition
is degenerate once a rotation carries roll, which after `FirstPersonLocked` has been rolling the
rig around is exactly the case. Forward projected onto the up plane is well defined however
tilted things got. And "level" must not mean *world* up — GT rotates the player itself for
gravity zones (`GTPlayerTransform` is a `MonkeGravityController`), so forcing world upright would
stand you sideways anywhere gravity is not down. The orientation captured at collapse is the
reference: legal by definition, and the one the ragdoll took you away from.

### The watchdog

Two failures survive a get-up silently, so [RagdollController.cs](Runtime/RagdollController.cs)
checks for both once a second while idle:

- **`PostTick` left suspended.** The avatar is stranded wherever the ragdoll ended while you walk
  away underneath it. This mod suspended it, so this mod repairs it — logged as an error, and
  followed by a probe capture.
- **The player tilted off GT's own up axis.** Reported only, never corrected. Gravity zones do
  this legitimately and quietly levelling someone standing on a wall would be worse than the bug.
- **The skeleton root off its rest pose.** The avatar is stranded somewhere you are not; put back,
  and a probe captured.

## Multiplayer

Everyone in the room who has the mod sees everyone else's ragdoll. Players without it see
normal avatars, exactly as before — they are never sent anything they would act on.

### How it travels

Photon `RaiseEvent` with a `byte[]` payload, the same channel NextBots already runs on this
install ([RagdollNet.cs](Net/RagdollNet.cs)):

| Code | Reliability | When | Contents |
|---|---|---|---|
| 150 | unreliable | `NetSendRate` times a second while you are down (15 by default) | protocol byte, `PhotonNetwork.Time`, torso / head / left hand / right hand as position + rotation — 121 bytes |
| 151 | reliable | once, when you get up | protocol byte, timestamp |

The codes are clear of Gorilla Tag's own traffic (1-3, 8, 50, 51, 100-103, 176, 177, 180, 186,
199, 202), clear of NextBots' 140-146, and below Photon's reserved 200+.

**Players without the mod drop the packets, silently.** That was checked in the decompile, not
assumed: every event handler in the game — `RoomSystem`, `PlayerCosmeticsSystem`, and the four
raw Photon listeners — filters on its own code and returns on anything else. None reports an
unknown code, and `MonkeAgent`'s RPC counter only runs inside those handlers, so this traffic
never reaches the anti-cheat. It is also only ever sent where ragdolling is allowed at all:
private rooms, per `RestrictToPrivateRooms`.

There is no "start" packet. A receiver begins drawing someone's ragdoll on the first pose it
sees, so there is nothing to lose in transit and a late joiner picks up a fall already in
progress. A second and a half of silence ends it, which covers crashes, disconnects and the
owner switching sharing off.

### How it is drawn

The owner's physics is authoritative and nothing is re-simulated remotely — two clients running
the same ragdoll from slightly different starting poses land in different places within a
second, and a video needs everyone to see the same fall. Receivers buffer the snapshots and draw
**2.5 send intervals in the past** — the sender's interval, measured from the snapshot spacing,
not the receiver's own setting — so there is almost always a snapshot either side to interpolate
between and one lost packet does not show. Out of future, they hold the newest pose
rather than extrapolate: a ragdoll guessed forward through a floor looks worse than one that
waits a frame for a packet.

The drawing is the ordinary [RigDriver](Runtime/RigDriver.cs), in a remote mode, fed from four
hidden transforms standing in for the owner's puppet through [IPoseSource](Runtime/IPoseSource.cs).
Remote mode differs in three ways, each of which would do real damage on a rig that is not yours:

- **No cosmetic re-anchoring.** It reads *your* `GTPlayer.CosmeticsHeadTarget`, so on someone
  else's rig it would lift your hat off your head and weld it to theirs.
- **No `PostTick` suspension.** Their network state is live; only the pose is overridden.
- **No face layer swap.** That only exists because you cannot normally see your own face.

### Winning against the game's own pose

A remote avatar is written twice a frame by the game. `VRRigJobManager.Update` runs every remote
rig's `RemoteRigUpdate`, whose `MapOther` **lerps** the head and hand IK targets toward the
network values; then `GorillaIKMgr.LateUpdate` reads those targets and poses the bones. An
ordinary mod LateUpdate lands before or after the IK at Unity's discretion, and after means the
IK already consumed the network pose — the ragdoll flickers against the standing one.

So remote ragdolls are posed from a Harmony prefix on `GorillaIKMgr.LateUpdate`
([IkHook.cs](Net/IkHook.cs)): after the network write, before the read, every frame, by
construction. The prefix is `void`, so the original always runs, and it swallows its own
exceptions — a throw there would stop the IK for every avatar in the room. If the patch ever
fails to install, posing falls back to LateUpdate and the overlay says so.

### Safety on the receiving end

- Every position and rotation is checked finite, and rotations normalised, before it touches a
  transform. One NaN written into a rig spams errors every frame until the scene reloads.
- A torso more than 500m from where the game already has that player is ignored. Generous,
  because a ragdolling player's own networked position freezes where they collapsed and a long
  fall legitimately carries the body away from it — but finite, so a bad packet cannot fling an
  avatar across the world.
- **VRRigs are pooled.** When a player leaves, their rig can be handed straight to someone who
  just joined, so a remote ragdoll that outlived its owner would start posing a stranger.
  Ownership is re-checked every frame against `VRRig.Creator`, not left to the timeout.
- The rig lookup uses the game's own `VRRigCache.TryGetVrrig`, which is `internal` and so
  reached by reflection, falling back to scanning rigs for a matching `Creator` if it ever moves.

### Cost

Each pose goes to every other player, so a full ten-player lobby all ragdolling at once is ten
times fifteen times nine messages a second on top of the game's own traffic. That is why the
default is 15 rather than higher: with interpolation it is smooth, and the slider goes to 30 for
a small group that wants it smoother.

## Not built

- **Active ragdoll / get-up animation.** You snap upright rather than pushing yourself up.
- **Damage, limb detachment, ragdoll-vs-ragdoll.** Nothing gameplay-facing — and remote ragdolls
  are drawn, not simulated, so they cannot collide with yours.

## Layout

```
src/GorillaRagdoll/
  Plugin.cs                  BepInEx entry; self-starting, no Utilla dependency
  Config/RagdollConfig.cs     every knob, with ranges the overlay reads back
  Runtime/
    RagdollPuppet.cs          the invisible jointed skeleton that actually falls over
    RigDriver.cs              poses the real avatar from any IPoseSource, yours or a remote one
    IPoseSource.cs            the four poses the driver needs - puppet or network
    RagdollPlan.cs            the verified rig layout + the puppet's bone chain
    BoneProfile.cs            body part -> mass, joint limits, GT's own collider sizes
    RagdollLayer.cs           runtime physics-layer choice, scored and logged
    PlayerSuspension.cs       holds GT locomotion; moves the rig about the eye
    RagdollController.cs      the state machine
    CosmeticReanchor.cs       keeps hats/badges on the body while it is ragdolled
    RigBones.cs               breadth-first bone lookup that ignores cosmetic sub-rigs
    SkeletonDump.cs           read-only F11 rig dump
    RigProbe.cs               whole-rig local-TRS snapshot, diffed against a baseline
    Venue.cs                  no public lobbies
    SafeInput.cs              keyboard/mouse over whichever input backend the game has
  Cameras/
    MonitorCamera.cs          mod-owned URP camera: first person, orbit, free fly
    OccluderFade.cs           fades scenery between the camera and the body, safely
    VrView.cs                 headset view by moving the rig, with comfort limits
  Net/
    RagdollNet.cs             share your ragdoll, receive everyone else's, over Photon events
    RemoteRagdoll.cs          one remote player's ragdoll: snapshot buffer + interpolation
    IkHook.cs                 Harmony prefix that poses remote ragdolls right before the IK reads
  UI/
    RagdollMenu.cs            IMGUI overlay
    RagdollInput.cs           keys + optional controller binding
```
