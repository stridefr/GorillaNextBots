using BepInEx.Configuration;
using UnityEngine;

namespace GorillaRagdoll.Config
{
    /// <summary>How a view is placed while you are a ragdoll.</summary>
    public enum CameraMode
    {
        /// <summary>Leave this output alone entirely.</summary>
        Off,

        /// <summary>Eye rides the ragdoll's head, but you still aim the view yourself.</summary>
        FirstPersonUnlocked,

        /// <summary>Eye rides the ragdoll's head *and* its rotation. The nauseating one.</summary>
        FirstPersonLocked,

        /// <summary>Detached camera orbiting the body, with collision pull-in.</summary>
        ThirdPersonFree,

        /// <summary>Detached camera you fly around yourself with WASD.</summary>
        ThirdPersonFly
    }

    /// <summary>What happens when you get up.</summary>
    public enum GetUpMode
    {
        /// <summary>
        /// Respawn cleanly a little above where the body came to rest. Hard reset, no blending -
        /// which means no half-restored poses and no chance of standing up inside the floor.
        /// </summary>
        Respawn,

        /// <summary>Ease back into live tracking exactly where the body landed.</summary>
        InPlace
    }

    /// <summary>How hard the ragdoll tries to hold itself together.</summary>
    public enum ActiveRagdollMode
    {
        /// <summary>Fully limp. Joints have limits but no motors.</summary>
        Off,

        /// <summary>
        /// Experimental. Joint motors pull the body back towards the pose it collapsed in, so
        /// it goes limp on impact and tenses up again as it recovers.
        /// </summary>
        Assisted
    }

    /// <summary>Where the monitor's orbit camera points.</summary>
    public enum OrbitAim
    {
        /// <summary>
        /// The mouse aims the view, like the headset does in VR. The camera stays tethered to
        /// the body; keys move it around the body.
        /// </summary>
        Free,

        /// <summary>Always aimed at the body; the mouse swings the camera around it.</summary>
        LockOnBody
    }

    /// <summary>How the right stick swings the VR orbit camera round the body.</summary>
    public enum OrbitTurning
    {
        /// <summary>Snap if the game is set to snap turn, smooth otherwise.</summary>
        MatchGame,
        Snap,
        Smooth
    }

    /// <summary>What the real (hidden) GT player body does while the ragdoll simulates.</summary>
    public enum BodyFollowMode
    {
        /// <summary>Body tracks the ragdoll pelvis, so zones and networking stay roughly right.</summary>
        FollowRagdoll,

        /// <summary>Body stays where you collapsed; you snap back to it on get-up.</summary>
        StayPut
    }

    /// <summary>
    /// Every knob, bound once. BepInEx persists these to
    /// <c>BepInEx/config/com.stridefr.gorillaragdoll.cfg</c>.
    ///
    /// Note the trap NextBots already paid for: BepInEx prefers the .cfg over the code
    /// default, so editing a default here does nothing once the file exists. Delete the
    /// file to re-default.
    /// </summary>
    public static class RagdollConfig
    {
        // ---- input
        public static ConfigEntry<KeyCode> ToggleKey;
        public static ConfigEntry<KeyCode> MenuKey;
        public static ConfigEntry<KeyCode> PanicKey;
        public static ConfigEntry<KeyCode> DumpSkeletonKey;
        public static ConfigEntry<string> VrBinding;
        public static ConfigEntry<float> VrGestureHold;

        // ---- safety
        public static ConfigEntry<bool> RestrictToPrivateRooms;
        public static ConfigEntry<float> AutoGetUpSeconds;
        public static ConfigEntry<float> GetUpBlend;
        public static ConfigEntry<GetUpMode> GetUp;
        public static ConfigEntry<float> GetUpHeight;

        // ---- physics
        public static ConfigEntry<float> TotalMass;
        public static ConfigEntry<float> LinearDamping;
        public static ConfigEntry<float> AngularDamping;
        public static ConfigEntry<float> JointLooseness;
        public static ConfigEntry<bool> SelfCollision;
        public static ConfigEntry<int> MaxBoneDepth;
        public static ConfigEntry<int> MaxBodies;
        public static ConfigEntry<bool> ContinuousCollision;
        public static ConfigEntry<float> ExtraGravity;
        public static ConfigEntry<int> SolverIterations;

        // ---- active ragdoll (experimental)
        public static ConfigEntry<ActiveRagdollMode> ActiveMode;
        public static ConfigEntry<float> ActiveStrength;
        public static ConfigEntry<float> ActiveDamping;
        public static ConfigEntry<float> ActiveRecover;
        public static ConfigEntry<float> ActiveImpactThreshold;

        // ---- launch
        public static ConfigEntry<bool> InheritPlayerVelocity;
        public static ConfigEntry<bool> InheritLimbVelocity;
        public static ConfigEntry<float> LaunchImpulse;
        public static ConfigEntry<float> LaunchSpin;

        // ---- camera (shared)
        public static ConfigEntry<CameraMode> VrMode;
        public static ConfigEntry<CameraMode> MonitorMode;
        public static ConfigEntry<BodyFollowMode> BodyFollow;
        public static ConfigEntry<float> ThirdPersonDistance;
        public static ConfigEntry<float> ThirdPersonHeight;
        public static ConfigEntry<float> CameraSmoothing;
        public static ConfigEntry<Vector3> EyeOffset;

        // ---- camera (monitor only)
        public static ConfigEntry<float> FreeCamSpeed;
        public static ConfigEntry<float> FreeCamSensitivity;
        public static ConfigEntry<float> FreeCamFov;
        public static ConfigEntry<bool> InvertY;
        public static ConfigEntry<float> ThirdPersonMinDistance;
        public static ConfigEntry<float> OcclusionRadius;
        public static ConfigEntry<float> CameraTransition;
        public static ConfigEntry<bool> SeeThroughWalls;
        public static ConfigEntry<float> OccluderAlpha;
        public static ConfigEntry<float> CameraDepthOffset;
        public static ConfigEntry<OrbitAim> MonitorOrbitAim;
        public static ConfigEntry<float> MonitorOrbitKeySpeed;

        // ---- camera (VR orbit)
        public static ConfigEntry<float> VrOrbitYawSpeed;
        public static ConfigEntry<float> VrOrbitPitchSpeed;
        public static ConfigEntry<float> VrOrbitZoomSpeed;
        public static ConfigEntry<float> VrOrbitDeadzone;
        public static ConfigEntry<bool> VrOrbitInvertPitch;
        public static ConfigEntry<OrbitTurning> VrOrbitTurning;
        public static ConfigEntry<float> VrOrbitSnapAngle;
        public static ConfigEntry<float> VrFollowDeadzone;

        // ---- camera (VR comfort)
        public static ConfigEntry<bool> VrClampPitch;
        public static ConfigEntry<float> VrMaxPitch;
        public static ConfigEntry<bool> VrAllowRoll;

        // ---- appearance
        public static ConfigEntry<bool> HideRealRig;
        public static ConfigEntry<bool> CloneFace;
        public static ConfigEntry<Vector3> HeadRotationOffset;
        public static ConfigEntry<string> MenuFont;

        // ---- network
        public static ConfigEntry<bool> ShareMyRagdoll;
        public static ConfigEntry<bool> ShowOtherRagdolls;
        public static ConfigEntry<float> NetSendRate;
        public static ConfigEntry<float> NetSmoothness;
        public static ConfigEntry<float> NetExtrapolate;
        public static ConfigEntry<bool> NetFlushSends;

        // ---- debug
        public static ConfigEntry<bool> VerboseLog;

        public static void Bind(ConfigFile cfg)
        {
            const string I = "1. Input";
            ToggleKey = cfg.Bind(I, "ToggleKey", KeyCode.F6,
                "Go ragdoll / get up. Keyboard only reaches the game while the desktop window has focus.");
            MenuKey = cfg.Bind(I, "MenuKey", KeyCode.F4,
                "Show/hide the settings overlay on the monitor.");
            PanicKey = cfg.Bind(I, "PanicKey", KeyCode.F10,
                "Immediately end the ragdoll and restore everything. Works even if a setting has you stuck.");
            DumpSkeletonKey = cfg.Bind(I, "DumpSkeletonKey", KeyCode.F11,
                "Write the avatar bone hierarchy to the BepInEx log. Read-only; use it to tune bone settings.");
            VrBinding = cfg.Bind(I, "VrBinding", "LeftPrimary",
                new ConfigDescription(
                    "In-headset toggle. LeftPrimary is the left X button; left Y is avoided because NextBots uses it.",
                    new AcceptableValueList<string>("None", "LeftPrimary", "LeftSecondary",
                        "RightPrimary", "RightSecondary", "BothGrips")));
            VrGestureHold = cfg.Bind(I, "VrGestureHold", 0.3f,
                new ConfigDescription("Seconds any VR binding must be held before it fires. 0 = instant.",
                    new AcceptableValueRange<float>(0.1f, 3f)));

            const string S = "2. Safety";
            RestrictToPrivateRooms = cfg.Bind(S, "RestrictToPrivateRooms", true,
                "Refuse to ragdoll in a public lobby. Your networked body still moves for everyone else, so this is on by default.");
            AutoGetUpSeconds = cfg.Bind(S, "AutoGetUpSeconds", 0f,
                new ConfigDescription("Automatically get up after this many seconds. 0 = never.",
                    new AcceptableValueRange<float>(0f, 120f)));

            GetUp = cfg.Bind(S, "GetUp", GetUpMode.Respawn,
                "Respawn drops you in cleanly just above where the body landed - a hard reset, so nothing " +
                "can be left half-restored and you cannot stand up inside the floor. InPlace eases back " +
                "into tracking exactly where you fell.");
            GetUpHeight = cfg.Bind(S, "GetUpHeight", 0.6f,
                new ConfigDescription("How far above the ground to respawn, metres.",
                    new AcceptableValueRange<float>(0f, 3f)));
            GetUpBlend = cfg.Bind(S, "GetUpBlend", 0.35f,
                new ConfigDescription("Seconds spent easing back into live tracking. Only used when GetUp is InPlace.",
                    new AcceptableValueRange<float>(0f, 2f)));

            const string P = "3. Physics";
            TotalMass = cfg.Bind(P, "TotalMass", 20f,
                new ConfigDescription("Mass of the whole ragdoll, split across limbs by body part.",
                    new AcceptableValueRange<float>(1f, 200f)));
            LinearDamping = cfg.Bind(P, "LinearDamping", 0.05f,
                new ConfigDescription("Rigidbody linear damping. Higher = falls slower, less slidey.",
                    new AcceptableValueRange<float>(0f, 5f)));
            AngularDamping = cfg.Bind(P, "AngularDamping", 0.15f,
                new ConfigDescription("Rigidbody angular damping. Higher = less spin.",
                    new AcceptableValueRange<float>(0f, 10f)));
            JointLooseness = cfg.Bind(P, "JointLooseness", 1f,
                new ConfigDescription("Scales every joint angle limit. 1 = anatomical, 2 = rubbery, 0.3 = stiff.",
                    new AcceptableValueRange<float>(0.1f, 3f)));
            SelfCollision = cfg.Bind(P, "SelfCollision", false,
                "Let non-adjacent limbs collide with each other. Looks better but can jitter at gorilla scale.");
            MaxBoneDepth = cfg.Bind(P, "MaxBoneDepth", 4,
                new ConfigDescription("How deep into the skeleton to build bodies. Raise it if limbs are missing.",
                    new AcceptableValueRange<int>(1, 8)));
            MaxBodies = cfg.Bind(P, "MaxBodies", 16,
                new ConfigDescription("Hard cap on ragdoll rigidbodies.",
                    new AcceptableValueRange<int>(2, 48)));
            ContinuousCollision = cfg.Bind(P, "ContinuousCollision", true,
                "Continuous collision detection. Costs a little; stops a fast ragdoll tunnelling through the map.");
            ExtraGravity = cfg.Bind(P, "ExtraGravity", 0f,
                new ConfigDescription("Extra downward acceleration on top of normal gravity, m/s^2.",
                    new AcceptableValueRange<float>(-20f, 60f)));
            SolverIterations = cfg.Bind(P, "SolverIterations", 12,
                new ConfigDescription("Per-body solver iterations. Higher = joints stretch less.",
                    new AcceptableValueRange<int>(4, 40)));

            const string A = "3b. Active ragdoll (experimental)";
            ActiveMode = cfg.Bind(A, "ActiveMode", ActiveRagdollMode.Off,
                "EXPERIMENTAL. Off is fully limp. Assisted gives every joint a motor that pulls back " +
                "towards the pose you collapsed in, so the body crumples on impact and stiffens as it " +
                "recovers. Changing this needs a Rebuild - it swaps the joint type.");
            ActiveStrength = cfg.Bind(A, "ActiveStrength", 220f,
                new ConfigDescription("Motor spring. Low = a drunk stagger, high = fights to hold its shape.",
                    new AcceptableValueRange<float>(0f, 3000f)));
            ActiveDamping = cfg.Bind(A, "ActiveDamping", 22f,
                new ConfigDescription("Motor damping. Too low and the body vibrates instead of settling.",
                    new AcceptableValueRange<float>(0f, 300f)));
            ActiveRecover = cfg.Bind(A, "ActiveRecover", 1.6f,
                new ConfigDescription("Seconds to go from fully limp back to full strength after a hit.",
                    new AcceptableValueRange<float>(0.05f, 10f)));
            ActiveImpactThreshold = cfg.Bind(A, "ActiveImpactThreshold", 3f,
                new ConfigDescription("Speed change, m/s, that counts as an impact and knocks the body limp again.",
                    new AcceptableValueRange<float>(0.5f, 30f)));

            const string L = "4. Launch";
            InheritPlayerVelocity = cfg.Bind(L, "InheritPlayerVelocity", true,
                "Carry your body momentum into the ragdoll, so jumping and then collapsing actually launches you.");
            InheritLimbVelocity = cfg.Bind(L, "InheritLimbVelocity", true,
                "Also carry per-hand and head speed, measured over the last few frames. This is the fun one.");
            LaunchImpulse = cfg.Bind(L, "LaunchImpulse", 0f,
                new ConfigDescription("Extra shove along your view direction on collapse, m/s.",
                    new AcceptableValueRange<float>(-20f, 20f)));
            LaunchSpin = cfg.Bind(L, "LaunchSpin", 0.5f,
                new ConfigDescription("Random tumble applied on collapse, rad/s.",
                    new AcceptableValueRange<float>(0f, 20f)));

            const string C = "5. Camera";
            VrMode = cfg.Bind(C, "VrMode", CameraMode.FirstPersonUnlocked,
                "What the HEADSET does. FirstPersonUnlocked (position follows, you still aim) is the comfortable one. " +
                "FirstPersonLocked rides the ragdoll head rotation and will make most people sick.");
            MonitorMode = cfg.Bind(C, "MonitorMode", CameraMode.ThirdPersonFree,
                "What the MONITOR does, on its own mod-owned camera. Independent of VrMode. " +
                "ThirdPersonFree orbits the body (hold right mouse); ThirdPersonFly is a WASD free cam.");
            BodyFollow = cfg.Bind(C, "BodyFollow", BodyFollowMode.FollowRagdoll,
                "Whether your real body tracks the ragdoll pelvis or stays where you collapsed.");
            ThirdPersonDistance = cfg.Bind(C, "ThirdPersonDistance", 2.5f,
                new ConfigDescription("Start distance behind the ragdoll for third person.",
                    new AcceptableValueRange<float>(0.3f, 30f)));
            ThirdPersonHeight = cfg.Bind(C, "ThirdPersonHeight", 0.8f,
                new ConfigDescription("Start height above the ragdoll for third person.",
                    new AcceptableValueRange<float>(-5f, 15f)));
            CameraSmoothing = cfg.Bind(C, "CameraSmoothing", 0.12f,
                new ConfigDescription("Follow smoothing, seconds. 0 = rigid.",
                    new AcceptableValueRange<float>(0f, 1f)));
            EyeOffset = cfg.Bind(C, "EyeOffset", new Vector3(0f, 0.13f, 0.015f),
                "Eye position in head-bone local space. Default matches GT's own head anchor from GorillaPawn.");

            const string M = "6. Camera (monitor)";
            FreeCamSpeed = cfg.Bind(M, "FreeCamSpeed", 4f,
                new ConfigDescription("Free-cam fly speed, m/s. Shift boosts, Ctrl slows, scroll adjusts.",
                    new AcceptableValueRange<float>(0.1f, 50f)));
            FreeCamSensitivity = cfg.Bind(M, "FreeCamSensitivity", 2f,
                new ConfigDescription("Mouse look sensitivity. Hold RIGHT MOUSE to look around.",
                    new AcceptableValueRange<float>(0.1f, 10f)));
            FreeCamFov = cfg.Bind(M, "FreeCamFov", 75f,
                new ConfigDescription("Monitor camera field of view.",
                    new AcceptableValueRange<float>(20f, 140f)));
            InvertY = cfg.Bind(M, "InvertY", false, "Invert mouse pitch.");
            ThirdPersonMinDistance = cfg.Bind(M, "ThirdPersonMinDistance", 0.6f,
                new ConfigDescription("Closest the orbit camera may be pulled when something blocks the view.",
                    new AcceptableValueRange<float>(0.1f, 5f)));
            OcclusionRadius = cfg.Bind(M, "OcclusionRadius", 0.18f,
                new ConfigDescription("Thickness of the view-blocking check. Larger keeps the camera further off walls.",
                    new AcceptableValueRange<float>(0.01f, 1f)));
            SeeThroughWalls = cfg.Bind(M, "SeeThroughWalls", true,
                "Fade scenery that gets between the camera and your body, instead of only pulling the camera in.");
            OccluderAlpha = cfg.Bind(M, "OccluderAlpha", 0.2f,
                new ConfigDescription("How transparent a blocking surface becomes. 0 = invisible, 1 = unchanged.",
                    new AcceptableValueRange<float>(0f, 1f)));
            CameraTransition = cfg.Bind(M, "CameraTransition", 0.45f,
                new ConfigDescription("Seconds to glide from the normal view into third person, instead of cutting.",
                    new AcceptableValueRange<float>(0f, 3f)));
            CameraDepthOffset = cfg.Bind(M, "CameraDepthOffset", 40f,
                new ConfigDescription("Render depth above the source camera. Lower it if it fights QuickFreeCam.",
                    new AcceptableValueRange<float>(1f, 200f)));
            MonitorOrbitAim = cfg.Bind(M, "OrbitAim", OrbitAim.Free,
                "ThirdPersonFree on the monitor. Free: hold right mouse to look wherever you like, " +
                "A/D circle the body, W/S raise/lower, C faces the body again. LockOnBody: the old " +
                "orbit, always aimed at the body, right mouse swings around it.");
            MonitorOrbitKeySpeed = cfg.Bind(M, "OrbitKeySpeed", 90f,
                new ConfigDescription("Degrees per second A/D and W/S move the Free orbit camera round the body.",
                    new AcceptableValueRange<float>(10f, 360f)));

            const string O = "6b. Camera (VR orbit)";
            VrOrbitYawSpeed = cfg.Bind(O, "VrOrbitYawSpeed", 90f,
                new ConfigDescription("Right stick left/right: degrees per second orbiting around the body.",
                    new AcceptableValueRange<float>(10f, 360f)));
            VrOrbitPitchSpeed = cfg.Bind(O, "VrOrbitPitchSpeed", 60f,
                new ConfigDescription("Right stick up/down: degrees per second raising or lowering the camera.",
                    new AcceptableValueRange<float>(5f, 240f)));
            VrOrbitZoomSpeed = cfg.Bind(O, "VrOrbitZoomSpeed", 2.5f,
                new ConfigDescription("Left stick up/down: metres per second closer to or further from the body.",
                    new AcceptableValueRange<float>(0.1f, 15f)));
            VrOrbitDeadzone = cfg.Bind(O, "VrOrbitDeadzone", 0.2f,
                new ConfigDescription("Stick travel ignored before the camera moves. Stops thumbstick drift creeping.",
                    new AcceptableValueRange<float>(0f, 0.6f)));
            VrOrbitInvertPitch = cfg.Bind(O, "VrOrbitInvertPitch", false,
                "Invert right stick up/down.");
            VrOrbitTurning = cfg.Bind(O, "VrOrbitTurning", OrbitTurning.MatchGame,
                "How the right stick swings you round the body. MatchGame follows your Gorilla Tag turn " +
                "setting (snap or smooth), which is whatever your stomach is already used to.");
            VrOrbitSnapAngle = cfg.Bind(O, "VrOrbitSnapAngle", 30f,
                new ConfigDescription("Degrees per click when orbiting in snap mode.",
                    new AcceptableValueRange<float>(10f, 90f)));
            VrFollowDeadzone = cfg.Bind(O, "VrFollowDeadzone", 0.5f,
                new ConfigDescription("How far the body can move, metres, before the VR camera follows it. A " +
                                      "tumbling body rocks a few centimetres every frame; following that is " +
                                      "being on a boat. 0 = follow every movement.",
                    new AcceptableValueRange<float>(0f, 3f)));

            const string V = "7. Camera (VR comfort)";
            VrClampPitch = cfg.Bind(V, "VrClampPitch", true,
                "In FirstPersonLocked, limit how far the ragdoll can tip your view.");
            VrMaxPitch = cfg.Bind(V, "VrMaxPitch", 35f,
                new ConfigDescription("Pitch clamp, degrees.", new AcceptableValueRange<float>(0f, 90f)));
            VrAllowRoll = cfg.Bind(V, "VrAllowRoll", false,
                "Allow the ragdoll to roll your horizon. Off by default; this is the worst single offender for nausea.");

            const string R = "8. Appearance";
            HideRealRig = cfg.Bind(R, "HideRealRig", true,
                "Hide your normal avatar while ragdolled, so you do not see two of yourself.");
            CloneFace = cfg.Bind(R, "CloneFace", true, "Copy the face onto the ragdoll.");
            HeadRotationOffset = cfg.Bind(R, "HeadRotationOffset", Vector3.zero,
                "Euler correction if FirstPersonLocked looks sideways. The head bone forward axis is a rig detail.");
            MenuFont = cfg.Bind(R, "MenuFont", "",
                "Font for the F4 settings menu: the name of any font installed on this PC, e.g. Verdana or " +
                "Coolvetica. Empty = Unity's default.");

            const string N = "10. Multiplayer";
            ShareMyRagdoll = cfg.Bind(N, "ShareMyRagdoll", true,
                "Send your ragdoll to other players in the room who also have this mod, so they see " +
                "you fall. Players without the mod still see you where your camera is.");
            ShowOtherRagdolls = cfg.Bind(N, "ShowOtherRagdolls", true,
                "Draw other players' ragdolls when they share them.");
            NetSendRate = cfg.Bind(N, "NetSendRate", 15f,
                new ConfigDescription("Ragdoll poses sent per second while you are down. Each one goes " +
                                      "to every other player, so a full lobby all ragdolling at once " +
                                      "adds up fast; 15 is smooth with interpolation.",
                    new AcceptableValueRange<float>(5f, 30f)));
            NetSmoothness = cfg.Bind(N, "NetSmoothness", 1f,
                new ConfigDescription("How much buffer other people's ragdolls are drawn with. The buffer is " +
                                      "measured from how evenly their packets actually arrive; this scales it. " +
                                      "Higher is smoother but further behind what they are doing; lower is " +
                                      "closer to live and more likely to stutter.",
                    new AcceptableValueRange<float>(0.25f, 4f)));
            NetExtrapolate = cfg.Bind(N, "NetExtrapolate", 120f,
                new ConfigDescription("How long a ragdoll keeps moving the way it was when its next packet is " +
                                      "late, milliseconds, before it stops and waits. 0 = freeze at once. Too " +
                                      "high and a body guesses its way through the floor.",
                    new AcceptableValueRange<float>(0f, 250f)));
            NetFlushSends = cfg.Bind(N, "NetFlushSends", true,
                "Push each pose out of Photon's queue as it is sent instead of waiting for the next flush. " +
                "Photon flushes on a timer, so without this a faster send rate just makes packets arrive in " +
                "pairs, which is what makes a ragdoll look choppy to everyone else.");

            const string D = "9. Debug";
            VerboseLog = cfg.Bind(D, "VerboseLog", false,
                "Log every bone decision when building the ragdoll.");
        }
    }
}
