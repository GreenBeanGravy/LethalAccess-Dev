using UnityEngine;
using System;
using System.Collections.Generic;
using System.Threading;
using GreenBean.LethalSpeechOutput;

namespace LethalAccess
{
    /// <summary>
    /// Serialised speech pipeline.
    ///
    /// Exactly one dedicated background thread. Utterances are spoken strictly in the order they
    /// were enqueued, and Tolk always sees the same thread. Enqueuing never blocks the caller, so
    /// the main thread is not stalled by speech.
    ///
    /// Do NOT dispatch speech with `async void` + `Task.Run(...)` instead. That hands each
    /// utterance to an arbitrary thread-pool thread, so two announcements raised in the same frame
    /// reach Tolk in either order - for a screen reader that is a correctness bug, not a nicety -
    /// and it enters Tolk, a COM/SAPI-backed native library, from a different thread each time.
    /// </summary>
    public static class SpeechQueue
    {
        /// <summary>
        /// One queued utterance. <see cref="Tag"/> is 0 for ordinary speech, or the instance id of
        /// the control that raised it when the caller wants a later utterance from that same
        /// control to replace this one.
        /// </summary>
        private struct Entry
        {
            public string Text;
            public int Tag;
        }

        private static readonly Queue<Entry> pending = new Queue<Entry>();
        private static readonly object gate = new object();
        private static Thread worker;
        private static bool running;

        /// <summary>
        /// Queue text to be spoken. Safe to call from any thread; ordering is preserved.
        ///
        /// NOTHING IS DROPPED. There is deliberately no duplicate suppression and no rate limit
        /// here. Two earlier designs both lost speech: the original dropped any utterance raised
        /// within 50 ms of the previous one regardless of content, and its replacement dropped
        /// exact repeats inside the same window. Both meant "line absent" could not be trusted to
        /// mean "line not spoken", which makes the output untrustworthy to reason about and makes
        /// a genuine repeat - "3 of 8" twice while moving through a list - silently vanish.
        /// If duplicate announcements are showing up, fix the thing raising them twice.
        /// </summary>
        public static bool Enqueue(string text)
        {
            return Enqueue(text, 0);
        }

        /// <summary>
        /// Queue text that SUPERSEDES anything still queued carrying the same
        /// <paramref name="supersedeTag"/>.
        ///
        /// This is the one sanctioned exception to the no-drop rule above, and the distinction is
        /// exact: what it removes is a STALE VALUE OF THE SAME CONTROL, never a distinct utterance.
        /// "Master volume, slider, 54 percent" followed by "…, 55 percent" from one held arrow key
        /// carries no information in the first line that the second does not, and speaking both
        /// puts the reader permanently behind the slider.
        ///
        /// It exists because the alternative that was tried first - emitting with a global
        /// barge-in - flushed the WHOLE backlog, so an unrelated "Took flashlight." or a health
        /// warning raised while the slider was moving was destroyed before it was ever spoken.
        /// Pass 0 (or use the single-argument overload) for anything that must never be replaced.
        /// </summary>
        public static bool Enqueue(string text, int supersedeTag)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            lock (gate)
            {
                EnsureWorkerStarted_NoLock();

                if (supersedeTag != 0)
                {
                    RemoveTagged_NoLock(supersedeTag);
                }

                pending.Enqueue(new Entry { Text = text, Tag = supersedeTag });
                Monitor.Pulse(gate);
            }

            return true;
        }

        /// <summary>
        /// Drop every queued utterance carrying <paramref name="tag"/>. Returns how many went.
        /// Order of the survivors is preserved - the queue is rebuilt in place rather than sorted.
        /// </summary>
        public static int CancelTagged(int tag)
        {
            if (tag == 0)
            {
                return 0;
            }

            lock (gate)
            {
                return RemoveTagged_NoLock(tag);
            }
        }

        private static int RemoveTagged_NoLock(int tag)
        {
            int removed = 0;
            int total = pending.Count;

            for (int i = 0; i < total; i++)
            {
                Entry e = pending.Dequeue();
                if (e.Tag == tag)
                {
                    removed++;
                    continue;
                }
                pending.Enqueue(e);
            }

            return removed;
        }

        /// <summary>
        /// Drop the entire backlog. The intended caller is a FOCUS CHANGE, where everything still
        /// queued describes a control the user has already left. Returns how many went, so the
        /// caller can log it - discarded speech that leaves no trace makes a "why did it go quiet"
        /// report unanswerable.
        /// </summary>
        public static int CancelAll()
        {
            lock (gate)
            {
                int dropped = pending.Count;
                pending.Clear();
                return dropped;
            }
        }

        /// <summary>
        /// Start the worker thread. Called from LACore.Awake so that first use is on the main
        /// thread; the worker itself never touches the Unity API.
        /// </summary>
        public static void Initialize()
        {
            lock (gate)
            {
                EnsureWorkerStarted_NoLock();
            }
        }

        private static void EnsureWorkerStarted_NoLock()
        {
            if (running && worker != null && worker.IsAlive)
            {
                return;
            }

            running = true;
            worker = new Thread(WorkerLoop)
            {
                // Background so it can never hold the process open on quit.
                IsBackground = true,
                Name = "LethalAccess Speech",
                // Single-threaded apartment: Tolk's SAPI backend is COM-based and is happiest
                // with a stable STA thread rather than rotating MTA thread-pool threads.
                Priority = System.Threading.ThreadPriority.AboveNormal
            };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        private static void WorkerLoop()
        {
            while (true)
            {
                string next;

                lock (gate)
                {
                    while (running && pending.Count == 0)
                    {
                        Monitor.Wait(gate);
                    }

                    if (!running && pending.Count == 0)
                    {
                        return;
                    }

                    next = pending.Dequeue().Text;
                }

                try
                {
                    LethalSpeechOutput.SpeakText(next);
                }
                catch (Exception ex)
                {
                    // Debug.Log/LogError are safe to call from a background thread in Unity.
                    Debug.LogError($"Error speaking text '{next}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Drain and stop. Called from LACore.OnDestroy.
        /// </summary>
        public static void Shutdown()
        {
            Thread toJoin;

            lock (gate)
            {
                running = false;
                pending.Clear();
                toJoin = worker;
                worker = null;
                Monitor.PulseAll(gate);
            }

            if (toJoin != null && toJoin.IsAlive)
            {
                // Bounded: the worker only ever blocks inside a single Tolk_Output call.
                toJoin.Join(TimeSpan.FromSeconds(1));
            }
        }
    }

    public static class SpeechSynthesizer
    {
        // NOTE: this type deliberately has NO static constructor. It used to have one that
        // called UnityMainThreadDispatcher.Instance(), which runs FindObjectOfType and
        // `new GameObject` - main-thread-only calls. Because every caller reached SpeakText
        // from inside Task.Run, the static constructor ran on a thread-pool thread, and a
        // throwing type initializer is cached by the CLR forever: one failure would have
        // silenced the screen reader for the whole session with no user-visible symptom.

        /// <summary>
        /// Queue text for speech. Ordering relative to other calls is guaranteed.
        /// </summary>
        public static void SpeakText(string text)
        {
            SpeechQueue.Enqueue(text);
        }

        public static void Initialize()
        {
            SpeechQueue.Initialize();
            Debug.Log("SpeechSynthesizer initialized.");
        }

        public static void Cleanup()
        {
            SpeechQueue.Shutdown();
            Debug.Log("SpeechSynthesizer cleanup completed.");
        }
    }
}
