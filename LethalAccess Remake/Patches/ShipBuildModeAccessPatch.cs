using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LethalAccess.Patches
{
    /// <summary>
    /// Ship build mode, which had no mod coverage at all.
    ///
    /// Build mode is not an EventSystem screen - there are no Selectables, nothing is focused, and
    /// the only feedback the game gives is a red/green material swap on the ghost mesh plus a
    /// "deny" sound that arrives AFTER you have already tried to commit. Legality lives in the
    /// private ShipBuildModeManager.CanConfirmPosition, recomputed every frame in Update.
    ///
    /// This file turns that material swap into a tone plus a word, on each legality TRANSITION,
    /// so the state is known before the confirm key is pressed rather than after.
    /// </summary>
    [HarmonyPatch(typeof(ShipBuildModeManager))]
    public static class ShipBuildModeAccessPatch
    {
        // Both are private on ShipBuildModeManager and both are load-bearing: CanConfirmPosition
        // is the entire legality signal, and placingObject is the only way to name the furniture.
        private static readonly AccessTools.FieldRef<ShipBuildModeManager, bool> CanConfirmRef =
            AccessTools.FieldRefAccess<ShipBuildModeManager, bool>("CanConfirmPosition");

        private static readonly AccessTools.FieldRef<ShipBuildModeManager, PlaceableShipObject> PlacingObjectRef =
            AccessTools.FieldRefAccess<ShipBuildModeManager, PlaceableShipObject>("placingObject");

        // Needed only to tell "you are not looking at movable furniture" apart from "you placed
        // something less than a second ago and the cooldown ate the press". Both leave InBuildMode
        // false, and only the first one is worth a sentence.
        private static readonly AccessTools.FieldRef<ShipBuildModeManager, float> TimeSincePlacingRef =
            AccessTools.FieldRefAccess<ShipBuildModeManager, float>("timeSincePlacingObject");

        /// <summary>
        /// True while the local player is positioning furniture.
        ///
        /// LACore.CheckKeybinds reads this to skip the mod's "announce current room" hotkey, which
        /// is bound to R - also the game's rotate key in build mode (CreateGhostObjectAndHighlight
        /// writes "Rotate: [R]" into the control tip).
        /// </summary>
        public static bool InBuildMode
        {
            get
            {
                ShipBuildModeManager mgr = ShipBuildModeManager.Instance;
                return mgr != null && mgr.InBuildMode;
            }
        }

        /// <summary>
        /// The build-mode control tip, or null when it is not showing.
        ///
        /// HUDManager.buildModeControlTip is a separate field from controlTipLines[], and
        /// ControlTipAccessPatch.SpeakControlTip walks the hard-coded ControlTip1..3 objects, so
        /// the build-mode tip - the only place the confirm, rotate and store keys are written down
        /// - is invisible to that loop. SpeakControlTip appends this to cover it.
        /// </summary>
        public static string GetBuildModeControlTip()
        {
            HUDManager hud = HUDManager.Instance;
            if (hud == null || hud.buildModeControlTip == null || !hud.buildModeControlTip.enabled)
            {
                return null;
            }
            return hud.buildModeControlTip.text;
        }

        // Public only because it appears in the signature of the public Harmony patch methods
        // below; C# refuses a public method whose parameter type is less accessible.
        public struct EnterState
        {
            public bool WasInBuildMode;
            public bool CanConfirm;
            public bool InPlacementCooldown;
        }

        // EnterBuildMode is private and doubles as the CONFIRM handler: pressed while already in
        // build mode it either places the object or plays the deny sound. Keying off the
        // InBuildMode transition is what separates those three outcomes.
        [HarmonyPatch("EnterBuildMode", new Type[] { typeof(InputAction.CallbackContext) })]
        [HarmonyPrefix]
        public static void PrefixEnterBuildMode(ShipBuildModeManager __instance, ref EnterState __state)
        {
            __state = default(EnterState);
            __state.WasInBuildMode = __instance.InBuildMode;
            __state.CanConfirm = CanConfirmRef(__instance);
            __state.InPlacementCooldown = TimeSincePlacingRef(__instance) <= 1f;
        }

        [HarmonyPatch("EnterBuildMode", new Type[] { typeof(InputAction.CallbackContext) })]
        [HarmonyPostfix]
        public static void PostfixEnterBuildMode(ShipBuildModeManager __instance, EnterState __state)
        {
            bool now = __instance.InBuildMode;

            if (!__state.WasInBuildMode && now)
            {
                string name = DescribePlacingObject(__instance);
                AnnouncementPolicy.Speak("Moving " + name + ".");
                BuildModeWatcher.Ensure(__instance);
                BuildModeWatcher.ResetForNewSession();
                return;
            }

            if (__state.WasInBuildMode && !now)
            {
                // InBuildMode only drops here via the CancelBuildMode(cancelBeforePlacement:false)
                // that immediately follows a successful PlaceShipObject. CancelBuildMode leaves
                // placingObject set, so the furniture can still be named.
                AnnouncementPolicy.Speak("Placed " + DescribePlacingObject(__instance) + ".");
                return;
            }

            if (__state.WasInBuildMode && now)
            {
                if (!__state.CanConfirm)
                {
                    // Still in build mode after a confirm press, and the position was illegal:
                    // this is the denyPlacementSFX case.
                    AnnouncementPolicy.Speak("Cannot place here.");
                }
                // Still legal and still in build mode means the one-second placement cooldown
                // swallowed the press; the deny sound already covers that.
                return;
            }

            if (!__state.InPlacementCooldown && LocalPlayerCouldHaveMeantIt())
            {
                // Never entered build mode and was not on cooldown: the raycast found no
                // PlaceableObject, or PlayerMeetsConditionsToBuild refused. The game answers this
                // with complete silence, which reads as a dead key.
                AnnouncementPolicy.Speak("Nothing here to move.");
            }
        }

        /// <summary>
        /// The action fires even while the chat box has the keyboard, and while dead. Neither is a
        /// deliberate "move this furniture" press, so neither should get an answer.
        /// </summary>
        private static bool LocalPlayerCouldHaveMeantIt()
        {
            GameNetworkManager net = GameNetworkManager.Instance;
            if (net == null || net.localPlayerController == null)
            {
                return false;
            }
            return !net.localPlayerController.isTypingChat && !net.localPlayerController.isPlayerDead;
        }

        [HarmonyPatch(nameof(ShipBuildModeManager.CancelBuildMode))]
        [HarmonyPrefix]
        public static void PrefixCancelBuildMode(ShipBuildModeManager __instance, ref bool __state)
        {
            // CancelBuildMode is called unconditionally from Update every single frame and
            // early-returns on !InBuildMode. An unconditional postfix would speak 60 times a
            // second; only the true -> false transition is an event.
            __state = __instance.InBuildMode;
        }

        [HarmonyPatch(nameof(ShipBuildModeManager.CancelBuildMode))]
        [HarmonyPostfix]
        public static void PostfixCancelBuildMode(ShipBuildModeManager __instance, bool __state, bool cancelBeforePlacement)
        {
            if (!__state || __instance.InBuildMode)
            {
                return;
            }

            BuildModeWatcher.ResetForNewSession();

            // cancelBeforePlacement is false when the caller is a successful placement or a store;
            // those announce themselves and must not also say "cancelled".
            if (cancelBeforePlacement)
            {
                AnnouncementPolicy.Speak("Placement cancelled.");
            }
        }

        public struct StoreState
        {
            public bool WasInBuildMode;
            public bool ContainedScrap;
            public bool AlreadyInStorage;
            public string Name;
        }

        [HarmonyPatch(nameof(ShipBuildModeManager.StoreObjectLocalClient))]
        [HarmonyPrefix]
        public static void PrefixStoreObjectLocalClient(ShipBuildModeManager __instance, ref StoreState __state)
        {
            __state = default(StoreState);
            __state.WasInBuildMode = __instance.InBuildMode;
            __state.Name = DescribePlacingObject(__instance);

            PlaceableShipObject placing = PlacingObjectRef(__instance);
            if (placing == null || placing.parentObject == null)
            {
                return;
            }

            // Re-evaluated rather than inferred from the DisplayTip, because the tip only fires on
            // the scrap branch and this prefix has to tell all three outcomes apart.
            __state.ContainedScrap = placing.parentObject.gameObject.GetComponentInChildren<GrabbableObject>() != null;

            UnlockableItem item = GetUnlockable(placing.unlockableID);
            __state.AlreadyInStorage = item != null && item.inStorage;
        }

        [HarmonyPatch(nameof(ShipBuildModeManager.StoreObjectLocalClient))]
        [HarmonyPostfix]
        public static void PostfixStoreObjectLocalClient(ShipBuildModeManager __instance, StoreState __state)
        {
            // The method's whole body is inside one guard that ends by dropping out of build mode,
            // so this transition is an exact "the store actually went through" test.
            if (!__state.WasInBuildMode || __instance.InBuildMode)
            {
                return;
            }

            if (__state.ContainedScrap)
            {
                // HUDManager.DisplayTip("Cannot store!", ...) already ran and TooltipPatch speaks
                // it for every client, so saying it again here would double the utterance.
                return;
            }

            if (__state.AlreadyInStorage)
            {
                AnnouncementPolicy.Speak(__state.Name + " is already in storage.");
                return;
            }

            // The host never hears the game's own confirmation: StoreShipObjectClientRpc returns
            // on IsServer before reaching its DisplayTip("Item stored!") call, and DisplayTip is
            // the only thing TooltipPatch listens to. This is the host's missing confirmation.
            AnnouncementPolicy.Speak("Stored " + __state.Name + ".");
        }

        internal static string DescribePlacingObject(ShipBuildModeManager mgr)
        {
            PlaceableShipObject placing = PlacingObjectRef(mgr);
            if (placing == null)
            {
                return "furniture";
            }

            UnlockableItem item = GetUnlockable(placing.unlockableID);
            if (item == null || string.IsNullOrEmpty(item.unlockableName))
            {
                // Naming it after the GameObject is a poor label, but inventing a furniture name
                // would be worse. Say what we actually have.
                return placing.gameObject.name;
            }
            return item.unlockableName;
        }

        internal static bool GetCanConfirm(ShipBuildModeManager mgr)
        {
            return CanConfirmRef(mgr);
        }

        private static UnlockableItem GetUnlockable(int unlockableID)
        {
            StartOfRound sor = StartOfRound.Instance;
            if (sor == null || sor.unlockablesList == null || sor.unlockablesList.unlockables == null)
            {
                return null;
            }
            if (unlockableID < 0 || unlockableID >= sor.unlockablesList.unlockables.Count)
            {
                return null;
            }
            return sor.unlockablesList.unlockables[unlockableID];
        }
    }

    /// <summary>
    /// Per-frame build-mode feedback: placement legality and rotation.
    ///
    /// Lives on the ShipBuildModeManager's own GameObject so it dies with it, and does nothing at
    /// all outside build mode.
    /// </summary>
    public class BuildModeWatcher : MonoBehaviour
    {
        // Fast enough that the answer tracks the crosshair, slow enough that a raycast flickering
        // across an edge cannot machine-gun the speech queue.
        private const float MinLegalityIntervalSeconds = 0.35f;

        // Below this the readout would fire on the tiniest nudge; the game rotates at 155 deg/s,
        // so 15 degrees is about a tenth of a second of held input.
        private const float MinRotationDeltaDegrees = 15f;
        private const float MinRotationIntervalSeconds = 0.4f;

        private static BuildModeWatcher instance;

        private ShipBuildModeManager manager;

        private bool haveLegality;
        private bool lastAnnouncedLegal;
        private float nextLegalityTime;

        private float lastFrameYaw;
        private float lastAnnouncedYaw;
        private bool haveYaw;
        private float nextRotationTime;

        private AudioSource toneSource;
        private AudioClip clearTone;
        private AudioClip blockedTone;

        public static void Ensure(ShipBuildModeManager mgr)
        {
            if (mgr == null)
            {
                Debug.LogError("[LethalAccess] BuildModeWatcher.Ensure was given no ShipBuildModeManager; " +
                               "placement legality will not be announced.");
                return;
            }

            if (instance != null && instance.manager == mgr)
            {
                return;
            }

            instance = mgr.gameObject.GetComponent<BuildModeWatcher>();
            if (instance == null)
            {
                instance = mgr.gameObject.AddComponent<BuildModeWatcher>();
            }
            instance.manager = mgr;
        }

        /// <summary>Forget the last legality and rotation so the next session announces from scratch.</summary>
        public static void ResetForNewSession()
        {
            if (instance == null)
            {
                return;
            }
            instance.haveLegality = false;
            instance.haveYaw = false;
            instance.nextLegalityTime = 0f;
            instance.nextRotationTime = 0f;
        }

        private void Update()
        {
            if (manager == null || !manager.InBuildMode)
            {
                return;
            }

            UpdateLegality();
            UpdateRotation();
        }

        private void UpdateLegality()
        {
            bool legal = ShipBuildModeAccessPatch.GetCanConfirm(manager);

            if (haveLegality && legal == lastAnnouncedLegal)
            {
                return;
            }

            // A transition inside the cooldown is not dropped - the condition above stays true, so
            // it is announced on the first frame past the window and the user always ends up with
            // the current state rather than a stale one.
            if (Time.unscaledTime < nextLegalityTime)
            {
                return;
            }

            haveLegality = true;
            lastAnnouncedLegal = legal;
            nextLegalityTime = Time.unscaledTime + MinLegalityIntervalSeconds;

            PlayLegalityTone(legal);
            AnnouncementPolicy.Speak(legal ? "Clear." : "Blocked.");
        }

        private void UpdateRotation()
        {
            Transform ghost = manager.ghostObject;
            if (ghost == null)
            {
                return;
            }

            float yaw = ghost.eulerAngles.y;

            if (!haveYaw)
            {
                haveYaw = true;
                lastFrameYaw = yaw;
                lastAnnouncedYaw = yaw;
                return;
            }

            bool stillTurning = Mathf.Abs(Mathf.DeltaAngle(lastFrameYaw, yaw)) > 0.01f;
            lastFrameYaw = yaw;

            // Announce when the user STOPS turning, not while they hold the key: a continuous
            // readout during a 155 deg/s spin is unusable, and the number only matters once it
            // has settled.
            if (stillTurning)
            {
                return;
            }

            if (Mathf.Abs(Mathf.DeltaAngle(lastAnnouncedYaw, yaw)) < MinRotationDeltaDegrees)
            {
                return;
            }
            if (Time.unscaledTime < nextRotationTime)
            {
                return;
            }

            lastAnnouncedYaw = yaw;
            nextRotationTime = Time.unscaledTime + MinRotationIntervalSeconds;

            AnnouncementPolicy.Speak("Facing " + Mathf.RoundToInt(Mathf.Repeat(yaw, 360f)) + " degrees.");
        }

        private void PlayLegalityTone(bool legal)
        {
            EnsureTones();
            if (toneSource == null)
            {
                return;
            }

            AudioClip clip = legal ? clearTone : blockedTone;
            if (clip != null)
            {
                toneSource.PlayOneShot(clip);
            }
        }

        private void EnsureTones()
        {
            if (toneSource != null)
            {
                return;
            }

            // Same generator shape as Patches/ItemActionPatch.cs (two sine bursts written into a
            // procedural clip); its helpers are private to that file, so this is a local copy
            // rather than a shared call. Same 0.35 volume so build mode is not louder than pickup.
            toneSource = gameObject.AddComponent<AudioSource>();
            toneSource.volume = 0.35f;
            toneSource.playOnAwake = false;

            clearTone = GenerateTwoTone("BuildModeClearTone", 544.5f, 720f);
            blockedTone = GenerateTwoTone("BuildModeBlockedTone", 220f, 165f);
        }

        private static AudioClip GenerateTwoTone(string name, float frequency1, float frequency2)
        {
            const int sampleRate = 44100;
            const float toneDuration = 0.06f;
            const float pauseDuration = 0.02f;

            int sampleLength = Mathf.RoundToInt(sampleRate * ((toneDuration * 2f) + pauseDuration));
            AudioClip clip = AudioClip.Create(name, sampleLength, 1, sampleRate, false);
            float[] samples = new float[sampleLength];

            int index = 0;
            int toneSamples = Mathf.RoundToInt(sampleRate * toneDuration);

            for (int i = 0; i < toneSamples && index < sampleLength; i++)
            {
                samples[index++] = Mathf.Sin(2f * Mathf.PI * frequency1 * i / sampleRate);
            }

            index = Mathf.Min(index + Mathf.RoundToInt(sampleRate * pauseDuration), sampleLength);

            for (int i = 0; i < toneSamples && index < sampleLength; i++)
            {
                samples[index++] = Mathf.Sin(2f * Mathf.PI * frequency2 * i / sampleRate);
            }

            clip.SetData(samples, 0);
            return clip;
        }
    }
}
