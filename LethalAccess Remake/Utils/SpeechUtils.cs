using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace LethalAccess
{
    /// <summary>
    /// The mod's single speech front door.
    ///
    /// There used to be two. Utilities.SpeakText had no config gate and no normalisation;
    /// this one honours EnableAudioCues, expands "%" and "$", and collapses whitespace. The same
    /// string was therefore spoken two different ways depending on which layer raised it, and
    /// turning audio cues off silenced the mod's own menus while leaving every game-event
    /// announcement audible. Utilities.SpeakText now forwards here.
    /// </summary>
    public static class SpeechUtils
    {
        // Dictionary of text replacements for specific object names
        private static readonly Dictionary<string, string> textReplacements = new Dictionary<string, string>
        {
            { "EntranceTeleportA", "Enter Factory" },
            { "EntranceTeleportA(Clone)", "Exit Factory" },
            { "EntranceTeleportB", "Enter Fire Escape" },
            { "EntranceTeleportB(Clone)", "Exit Fire Escape" },
            { "TerminalScript", "Terminal" },
            { "StartGameLever", "Start Ship Lever" },
            { "ShipInside", "Inside of Ship" },
            { "StorageCloset", "Storage Closet" },
            { "Bunkbeds", "Bunk Beds" },
            { "LightSwitch", "Light Switch" },
            { "ItemShip", "Item Ship" },
            { "RedButton", "Teleporter Button" },
            { "BellDinger", "Sell Bell" },
            { "ItemCounter", "Item Counter" },
            { "PlacementBlocker (5)", "Charging Station" }
        };

        // Keys ordered longest first. Without this the table is applied in dictionary order, so
        // "EntranceTeleportA" rewrites the prefix of "EntranceTeleportA(Clone)" and the exit
        // teleports announce as "Enter Factory(Clone)"; the "Exit Factory" and "Exit Fire Escape"
        // entries could never fire at all. Rebuilt when the table changes.
        private static string[] replacementOrder;

        /// <summary>
        /// Primary speech function with text processing.
        ///
        /// The old implementation returned early (silently dropping the utterance) if ANY
        /// speech had been raised in the previous 50 ms, regardless of content. Nothing is
        /// dropped now: SpeechQueue speaks everything, in order. See SpeechQueue.Enqueue.
        /// </summary>
        public static void SpeakText(string text)
        {
            // Check if speech is enabled
            if (!ConfigManager.EnableAudioCues.Value)
            {
                return;
            }

            // Apply text replacements and processing
            text = ProcessTextForSpeech(text);

            SpeechSynthesizer.SpeakText(text);
        }

        /// <summary>
        /// Historical alias for SpeakText. Kept because callers exist; there is no longer any
        /// suppression for it to bypass, so it is the same call.
        /// </summary>
        public static void SpeakTextImmediate(string text)
        {
            SpeakText(text);
        }

        /// <summary>
        /// Speak text that supersedes anything still queued from the SAME control.
        ///
        /// Use this, not <see cref="CancelQueued"/> + <see cref="SpeakText"/>, for a repeated
        /// re-announcement of one control's value. CancelQueued flushes the WHOLE backlog, so a
        /// held arrow key on a slider would destroy an unrelated "Took flashlight." or a health
        /// warning raised while the slider was moving. This drops only the stale value of the
        /// control named by <paramref name="ownerInstanceId"/>, which carries no information the
        /// new line does not.
        /// </summary>
        public static void SpeakSuperseding(string text, int ownerInstanceId)
        {
            if (!ConfigManager.EnableAudioCues.Value)
            {
                return;
            }

            if (ownerInstanceId == 0)
            {
                // A zero tag would mean "supersede nothing", which silently turns this into an
                // ordinary Enqueue and reintroduces the backlog. Say so rather than degrade.
                Debug.LogError("[LethalAccess] SpeakSuperseding called with ownerInstanceId 0; " +
                               "speaking without superseding. Pass the control's GetInstanceID().");
                SpeakText(text);
                return;
            }

            SpeechQueue.Enqueue(ProcessTextForSpeech(text), ownerInstanceId);
        }

        /// <summary>
        /// Speak text with custom volume (if supported by speech system)
        /// </summary>
        public static void SpeakTextWithVolume(string text, float volumeMultiplier = 1f)
        {
            // Note: Volume adjustment would need to be implemented in the speech synthesizer
            // For now, we'll just use the standard speak function
            SpeakText(text);
        }

        // ------------------------------------------------------------------
        // Barge-in
        // ------------------------------------------------------------------

        /// <summary>
        /// Drop everything queued but not yet handed to the screen reader.
        ///
        /// This is the ONLY place in the mod that discards speech, and it must stay that way. Two
        /// earlier designs filtered by time (anything within 50 ms of the previous utterance, then
        /// exact repeats within the same window) and both lost real announcements, which makes
        /// "I did not hear it" impossible to reason about. This is not a filter: it drops nothing
        /// unless a caller explicitly asks, and the intended caller is a FOCUS CHANGE, at which
        /// point everything still queued describes a control the user has already left.
        ///
        /// Do NOT call this to suppress a repeated value from one control; that is what
        /// <see cref="SpeakSuperseding"/> is for. This flushes the whole backlog, including
        /// unrelated lines like "Took flashlight." that happened to be queued at the time.
        /// </summary>
        public static void CancelQueued()
        {
            int dropped = SpeechQueue.CancelAll();

            if (dropped > 0)
            {
                // Discarded speech has to be visible in the log or a "why did it go quiet" report
                // is unanswerable.
                Debug.Log("[LethalAccess] Barge-in dropped " + dropped + " queued utterance(s).");
            }
        }

        /// <summary>
        /// Tell the speech layer that keyboard focus moved, so the announcement of the control the
        /// user has just left stops instead of having to be sat through. Cancels the backlog only
        /// when the focused object actually changed, so calling it every frame is safe and calling
        /// it on a re-announce of the same control does not swallow that announcement.
        ///
        /// CALL IT BEFORE THE NEW CONTROL'S ANNOUNCEMENT IS RAISED, from the announcer's
        /// selection-change branch, above the AnnounceUIElement call. The cancel is synchronous and
        /// empties the queue as it stands at the moment of the call, so anything enqueued
        /// afterwards (i.e. the newly focused control's own announcement) is untouched. Called
        /// after that announcement instead, it would discard exactly the line the user is waiting
        /// for.
        ///
        /// It is NOT the answer to a rapid state change: holding an arrow key on a slider
        /// re-announces the SAME control, this returns early, and the backlog builds up. That has to
        /// be solved by coalescing the repeated re-announcement BEFORE it is queued, at the site
        /// that raises it; do not "fix" it by cancelling on an unchanged focus, which would race
        /// the announcement it is meant to replace.
        ///
        /// Main thread only: it reads Unity object state.
        /// </summary>
        public static void NoteFocusChanged(GameObject newFocus)
        {
            // Unity's overloaded == reports a destroyed object as null too, which is what is wanted
            // here: a destroyed selection is focus being LOST, not focus arriving somewhere new.
            if (newFocus == null)
            {
                // Losing focus entirely is not a barge-in: FocusManager's watchdog is about to
                // re-seed, and whatever is queued is still the last thing the user asked for.
                //
                // lastFocusInstanceId is deliberately NOT cleared here. It used to be, and that
                // undid this very guard: a one-frame null blip (every panel rebuild and every
                // "Set to defaults" causes one) made the re-seed of the SAME control look like a
                // new focus, so the barge-in fired and destroyed that control's own announcement
                // while it was still queued. Remembering the last REAL focus makes a blip a no-op.
                return;
            }

            int id = newFocus.GetInstanceID();
            if (id == lastFocusInstanceId)
            {
                return;
            }

            // A live Unity object never has instance ID 0, so 0 means "no focus has ever been
            // recorded in this session". If that assumption were ever wrong the cost is one
            // skipped barge-in: an extra announcement, never a lost one.
            bool firstFocusOfSession = lastFocusInstanceId == 0;
            lastFocusInstanceId = id;

            // Consume the one-shot unconditionally so it can never leak into a later, unrelated
            // focus change.
            bool preserving = Time.unscaledTime <= preserveQueuedUntil;
            preserveQueuedUntil = -1f;

            if (firstFocusOfSession)
            {
                // There is no earlier control, so nothing queued can be describing one. Cancelling
                // here would only discard startup announcements that nothing supersedes.
                return;
            }

            if (preserving)
            {
                // What is queued is the FIRST HALF of this same focus change (rule 4's screen
                // title), not the tail of the previous control. See
                // PreserveQueuedThroughNextFocusChange.
                return;
            }

            CancelQueued();
        }

        /// <summary>
        /// Protect what is queued right now from the next focus change's barge-in.
        ///
        /// AnnouncementPolicy rule 4 is "announce the screen title, THEN the newly focused
        /// control", and the seed that produces that focus change runs immediately after the title
        /// is queued. Without this the barge-in would flush the queue between the two halves of one
        /// announcement and the screen title would be lost. It is the only case where a queued
        /// utterance is not describing a control the user has already left.
        ///
        /// One-shot and time-bounded: the next focus change consumes it, and it lapses by itself if
        /// that focus change never arrives (FocusManager.Seed refuses invisible or unseedable
        /// targets and returns). A stale flag therefore costs at most one skipped barge-in (the
        /// user hears one extra announcement) and can never drop an utterance.
        /// </summary>
        public static void PreserveQueuedThroughNextFocusChange()
        {
            preserveQueuedUntil = Time.unscaledTime + PreserveQueuedSeconds;
        }

        // Generous next-frame allowance. Menus commonly run at timeScale 0, where Time.time stops
        // advancing, so this is measured on the unscaled clock like every other timer in the mod.
        private const float PreserveQueuedSeconds = 1f;

        // Negative sentinel: Time.unscaledTime is never negative, so an unset value can never
        // satisfy the "<=" test above.
        private static float preserveQueuedUntil = -1f;

        private static int lastFocusInstanceId;

        // SpeechQueue now exposes CancelAll / CancelTagged / Enqueue(text, tag) directly, so the
        // reflection that used to reach its private 'pending'/'gate' fields from here is gone. That
        // reflection was a standing hazard: renaming a private field would have disabled barge-in
        // with only a log line to say so.

        // ------------------------------------------------------------------
        // Text processing
        // ------------------------------------------------------------------

        /// <summary>
        /// Process text for speech by applying replacements and formatting
        /// </summary>
        private static string ProcessTextForSpeech(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            // Apply text replacements, longest key first (see replacementOrder).
            string result = text;
            string[] order = GetReplacementOrder();
            for (int i = 0; i < order.Length; i++)
            {
                result = result.Replace(order[i], textReplacements[order[i]]);
            }

            // Clean up common formatting issues
            result = CleanTextForSpeech(result);

            return result;
        }

        private static string[] GetReplacementOrder()
        {
            string[] order = replacementOrder;
            if (order != null)
            {
                return order;
            }

            order = new string[textReplacements.Count];
            textReplacements.Keys.CopyTo(order, 0);
            Array.Sort(order, (a, b) => b.Length.CompareTo(a.Length));

            replacementOrder = order;
            return order;
        }

        /// <summary>
        /// Clean text for better speech synthesis
        /// </summary>
        private static string CleanTextForSpeech(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            // Collapse all whitespace runs in one pass. This replaced three Replace calls plus a
            // `while (text.Contains("  ")) text = text.Replace("  ", " ")` loop that allocated a
            // whole new string on every iteration, for a long announcement with deep
            // indentation that is many full-string copies per utterance.
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

            // Currency and percent read correctly only if the unit ends up AFTER the number.
            // A plain Replace turns "$500" into "dollars500" and "60%" into "60percent", which is
            // worse than leaving the symbol alone, because a screen reader already pronounces
            // "$500" as "500 dollars". This mattered the moment Utilities.SpeakText was folded in
            // here: ~150 call sites that previously bypassed all normalisation include
            // "Profit Quota: {x} of ${y}" and "Sold {item} for ${value}".
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"\$\s*(\d+(?:[.,]\d+)*)", "$1 dollars");
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"(\d+(?:[.,]\d+)*)\s*%", "$1 percent");

            // Remove special characters that might cause issues
            text = text.Replace("&", "and");
            text = text.Replace("@", "at");
            text = text.Replace("#", "number");

            // Whatever is left is a bare symbol with no number attached.
            text = text.Replace("$", "dollars");
            text = text.Replace("%", "percent");

            // Clean up punctuation for better flow
            text = text.Replace("...", ".");
            text = text.Replace("!!", "!");
            text = text.Replace("??", "?");

            return text.Trim();
        }

        /// <summary>
        /// Announce direction/orientation information
        /// </summary>
        public static void AnnounceDirection(Vector3 direction, bool includeVertical = false)
        {
            string directionText = GetDirectionText(direction, includeVertical);
            SpeakText(directionText);
        }

        /// <summary>
        /// Convert direction vector to speech-friendly text
        /// </summary>
        public static string GetDirectionText(Vector3 direction, bool includeVertical = false)
        {
            // Normalize the direction
            direction = direction.normalized;

            // Get horizontal angle
            float angle = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            if (angle < 0) angle += 360f;

            // Convert to cardinal directions
            string[] directions = { "North", "Northeast", "East", "Southeast", "South", "Southwest", "West", "Northwest" };
            int directionIndex = Mathf.RoundToInt(angle / 45f) % 8;
            string horizontalDirection = directions[directionIndex];

            if (!includeVertical)
            {
                return horizontalDirection;
            }

            // Add vertical component if requested
            string verticalDirection = "";
            if (direction.y > 0.3f)
            {
                verticalDirection = " above";
            }
            else if (direction.y < -0.3f)
            {
                verticalDirection = " below";
            }

            return horizontalDirection + verticalDirection;
        }

        /// <summary>
        /// Announce distance with appropriate units
        /// </summary>
        public static void AnnounceDistance(float distance)
        {
            string distanceText = GetDistanceText(distance);
            SpeakText(distanceText);
        }

        /// <summary>
        /// Convert distance to speech-friendly text
        /// </summary>
        public static string GetDistanceText(float distance)
        {
            if (distance < 1f)
            {
                return "very close";
            }
            else if (distance < 2f)
            {
                return "close";
            }
            else if (distance < 5f)
            {
                return $"{distance:F1} meters away";
            }
            else if (distance < 20f)
            {
                return $"{Mathf.RoundToInt(distance)} meters away";
            }
            else
            {
                return "far away";
            }
        }

        /// <summary>
        /// Queue multiple speech items with delays
        /// </summary>
        public static async void SpeakSequence(List<string> texts, float delayBetweenItems = 0.5f)
        {
            foreach (string text in texts)
            {
                SpeakTextImmediate(text);
                await Task.Delay(TimeSpan.FromSeconds(delayBetweenItems));
            }
        }

        /// <summary>
        /// Add a custom text replacement
        /// </summary>
        public static void AddTextReplacement(string original, string replacement)
        {
            textReplacements[original] = replacement;
            replacementOrder = null;
        }

        /// <summary>
        /// Remove a text replacement
        /// </summary>
        public static void RemoveTextReplacement(string original)
        {
            textReplacements.Remove(original);
            replacementOrder = null;
        }

        /// <summary>
        /// Get all current text replacements
        /// </summary>
        public static Dictionary<string, string> GetTextReplacements()
        {
            return new Dictionary<string, string>(textReplacements);
        }
    }
}
