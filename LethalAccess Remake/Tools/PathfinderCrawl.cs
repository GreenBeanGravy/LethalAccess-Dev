using UnityEngine;
using UnityEngine.AI;

namespace LethalAccess
{
    /// <summary>
    /// Crawl spaces: the factory's "UnderServerRack" NavMesh links, a gap under a server rack
    /// 1.79 m high and about 1.0 m wide. The crouched player is 1.5 m tall and 0.96 m wide with
    /// the controller's skin, so the way through is a line a few centimetres wide, and standing
    /// up or sprinting (which stands the player up) at its mouth walks the head into the rack.
    ///
    /// The link itself is that line: it runs down the middle of the gap. When the path uses one,
    /// auto mode walks to its entry, faces along it, crouches, waits until the crouch has
    /// finished, then crawls along the link holding itself on the line, and stands up only once
    /// past the far end. Manual mode gets the crouch cue, pings along the line and a sideways
    /// touch on the move input that keeps a crouched player on it.
    /// </summary>
    internal partial class Pathfinder
    {
        private static readonly float[] CrawlSearchAngles = { 0f, -10f, 10f, -20f, 20f };
        private struct CrawlSpace
        {
            public bool active;
            public Vector3 entry;
            public Vector3 exit;
            public bool cued;
        }

        // The factory NavMesh's "UnderServerRack" area. Area names are editor-only, so the index;
        // any other level link with a low ceiling over its middle counts too.
        private const int CRAWL_AREA = 5;
        // Within this of the entry (and the path heading for the exit), the crawl takes over.
        private const float CRAWL_ENGAGE_DISTANCE = 2.5f;
        // The crouched controller is 1.5 m; wait for it before going under.
        private const float CRAWL_CROUCHED_HEIGHT = 1.6f;
        // Line the feet up this well before going under.
        private const float CRAWL_LINE_TOLERANCE = 0.06f;

        private CrawlSpace crawl;
        // Progress along the crawl line, and a crawl space given up on (blocked inside, or not
        // a crawl space at all), so it isn't picked straight up again.
        private float crawlBestAlong;
        private float crawlProgressTime;
        private float crawlBestLateral;
        private Vector3 crawlRejectedEntry;
        private float crawlRejectedUntil = -999f;
        private const float CRAWL_STALL_TIME = 2.5f;
        private OffMeshLink[] crawlLinks;
        private float crawlLinksTime = -999f;
        // Manual crawl aim assist (world space), set each frame by UpdateManualCrawl.
        private Vector3 manualCrawlAssist;

        private OffMeshLink[] GetCrawlLinks()
        {
            if (crawlLinks == null || Time.time - crawlLinksTime > 5f)
            {
                var found = new System.Collections.Generic.List<OffMeshLink>();
                foreach (var link in FindObjectsOfType<OffMeshLink>())
                {
                    if (link.startTransform == null || link.endTransform == null)
                        continue;
                    Vector3 a = link.startTransform.position;
                    Vector3 b = link.endTransform.position;
                    if (Mathf.Abs(a.y - b.y) > 0.6f)
                        continue;
                    if (link.area == CRAWL_AREA || LowCeiling((a + b) * 0.5f))
                        found.Add(link);
                }
                crawlLinks = found.ToArray();
                crawlLinksTime = Time.time;
            }
            return crawlLinks;
        }

        /// <summary>
        /// Starts a crawl when the player is near one end of a crawl link and the path goes on to
        /// the other end, or when the path ahead runs under a ceiling too low to stand under (the
        /// NavMesh is baked for enemies, and passes under racks without a link). Keeps the
        /// current one otherwise.
        /// </summary>
        private bool CrawlRejected(Vector3 entry) =>
            Time.time < crawlRejectedUntil && HorizontalDistance(entry, crawlRejectedEntry) < 1.5f;

        private void StartCrawl(Vector3 entry, Vector3 exit)
        {
            crawl = new CrawlSpace { active = true, entry = entry, exit = exit };
            crawlBestAlong = float.MinValue;
            crawlBestLateral = float.MaxValue;
            crawlProgressTime = Time.time;
        }

        private void UpdateCrawlDetection()
        {
            // A gap ahead is the gap code's to handle; the floor of the far side overhead is not
            // a crawl space.
            if (crawl.active || currentTargetNode == null || hasGap || gapJumpInProgress)
                return;
            if (Time.time < nextCrawlScan)
                return;
            nextCrawlScan = Time.time + 0.2f;

            foreach (OffMeshLink link in GetCrawlLinks())
            {
                Vector3 a = link.startTransform.position;
                Vector3 b = link.endTransform.position;
                foreach (bool forward in new[] { true, false })
                {
                    Vector3 entry = forward ? a : b;
                    Vector3 exit = forward ? b : a;
                    if (HorizontalDistance(transform.position, entry) > CRAWL_ENGAGE_DISTANCE ||
                        Mathf.Abs(transform.position.y - entry.y) > 1.2f)
                        continue;
                    if (!PathReaches(exit))
                        continue;
                    // Already past the entry and on through: nothing to start.
                    Vector3 along = exit - entry;
                    along.y = 0f;
                    Vector3 rel = transform.position - entry;
                    rel.y = 0f;
                    if (Vector3.Dot(rel, along.normalized) > along.magnitude)
                        continue;
                    if (CrawlRejected(entry))
                        continue;

                    StartCrawl(entry, exit);
                    return;
                }
            }

            Vector3 toNode = currentTargetNode.position - transform.position;
            toNode.y = 0f;
            if (toNode.sqrMagnitude > 0.01f && FindCrawlLine(toNode.normalized, out Vector3 lineEntry, out Vector3 lineExit) &&
                PathReaches(lineExit) && !CrawlRejected(lineEntry))
            {
                StartCrawl(lineEntry, lineExit);
            }
        }

        private float nextCrawlScan;
        private float lastCrawlCueTime = -999f;
        private Vector3 lastCrawlCuePosition;

        /// <summary>
        /// A crawl line along the way ahead: where a ceiling too low to stand under begins and
        /// ends within a few metres, and, across that, the middle of the widest opening a crouched
        /// player fits through nearest the path. Tries the path's direction and a little either
        /// side of it. False when nothing low is ahead or nothing fits.
        /// </summary>
        private bool FindCrawlLine(Vector3 pathDir, out Vector3 entry, out Vector3 exit)
        {
            entry = exit = Vector3.zero;
            Vector3 feet = transform.position;

            // Where the overhang starts and ends along the path.
            float first = -1f;
            float last = -1f;
            for (float d = 0.2f; d <= 3.5f; d += 0.1f)
            {
                bool low = LowCeiling(feet + pathDir * d);
                if (low && first < 0f)
                    first = d;
                if (low)
                    last = d;
                else if (first >= 0f)
                    break;
            }
            if (first < 0f || first > 2.5f)
                return false;

            float bestCost = float.MaxValue;
            foreach (float angle in CrawlSearchAngles)
            {
                Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * pathDir;
                Vector3 perp = Vector3.Cross(Vector3.up, dir);
                Vector3 mid = feet + pathDir * ((first + last) * 0.5f);
                float length = last - first + 0.6f;

                // Clear offsets across the line, in 4 cm steps.
                float runStart = float.NaN;
                for (float offset = -0.9f; offset <= 0.9001f; offset += 0.04f)
                {
                    bool fits = CrouchFitsAlong(mid + perp * offset, dir, length);
                    if (fits && float.IsNaN(runStart))
                        runStart = offset;
                    bool runEnds = !fits || offset > 0.88f;
                    if (runEnds && !float.IsNaN(runStart))
                    {
                        float runEnd = fits ? offset : offset - 0.04f;
                        float centre = (runStart + runEnd) * 0.5f;
                        float cost = Mathf.Abs(centre) + Mathf.Abs(angle) / 40f;
                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            Vector3 c = mid + perp * centre;
                            entry = c - dir * (length * 0.5f + 0.3f);
                            exit = c + dir * (length * 0.5f + 0.3f);
                        }
                        runStart = float.NaN;
                    }
                }
            }
            if (bestCost == float.MaxValue)
                return false;
            entry.y = FloorY(entry, feet.y);
            exit.y = FloorY(exit, feet.y);
            return true;
        }

        /// <summary>A crouched player fits at every step along the line through centre.</summary>
        private bool CrouchFitsAlong(Vector3 centre, Vector3 dir, float length)
        {
            CharacterController cc = playerController.thisController;
            float radius = cc.radius + cc.skinWidth * 0.5f;
            // Everything the player collides with: the room mask leaves out the Railing layer and
            // layer 11, where Vow's pipe-room shelves are, and a line under one "fitted".
            int mask = groundMask;
            for (float d = -length * 0.5f; d <= length * 0.5f + 0.001f; d += 0.15f)
            {
                Vector3 p = centre + dir * d;
                float floor = FloorY(p, transform.position.y);
                Vector3 bottom = new Vector3(p.x, floor + 0.12f + radius, p.z);
                Vector3 top = new Vector3(p.x, floor + 1.52f - radius, p.z);
                if (Physics.CheckCapsule(bottom, top, radius, mask, QueryTriggerInteraction.Ignore))
                    return false;
            }
            return true;
        }

        private static float FloorY(Vector3 point, float nearY)
        {
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            return Physics.Raycast(new Vector3(point.x, nearY + 0.6f, point.z), Vector3.down, out RaycastHit hit, 1.6f, mask, QueryTriggerInteraction.Ignore)
                ? hit.point.y
                : nearY;
        }

        /// <summary>
        /// Something overhead a crouched player fits under and a standing one does not, above
        /// the floor at point. Kept well under standing height (a rack is 1.79 m) so a doorway's
        /// header never reads as a crawl space; doors themselves never count.
        /// </summary>
        private static bool LowCeiling(Vector3 point)
        {
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            if (!Physics.Raycast(point + Vector3.up * 0.5f, Vector3.down, out RaycastHit floor, 1.5f, mask, QueryTriggerInteraction.Ignore))
                return false;
            if (!Physics.Raycast(floor.point + Vector3.up * 0.1f, Vector3.up, out RaycastHit ceiling, CRAWL_MAX_CEILING - 0.1f, mask, QueryTriggerInteraction.Ignore))
                return false;
            return ceiling.distance + 0.1f >= CRAWL_MIN_CEILING &&
                   ceiling.collider.GetComponentInParent<DoorLock>() == null &&
                   ceiling.collider.GetComponentInParent<AnimatedObjectTrigger>() == null;
        }

        // A crawl space's ceiling: over the crouched controller's 1.59 m, and clearly under the
        // standing 2.5 m.
        private const float CRAWL_MIN_CEILING = 1.62f;
        private const float CRAWL_MAX_CEILING = 2.2f;

        /// <summary>An unreached path node within a metre and a half of point.</summary>
        private bool PathReaches(Vector3 point)
        {
            foreach (PathNode node in pathNodes)
            {
                if (!node.isReached && HorizontalDistance(node.position, point) < 1.5f &&
                    Mathf.Abs(node.position.y - NODE_GROUND_OFFSET - point.y) < 1.2f)
                    return true;
            }
            return false;
        }

        /// <summary>Along the crawl line from the entry, and the sideways offset from it, flat.</summary>
        private void CrawlFrame(out Vector3 dir, out float along, out Vector3 lateral, out float length)
        {
            dir = crawl.exit - crawl.entry;
            dir.y = 0f;
            length = dir.magnitude;
            dir = length > 0.01f ? dir / length : transform.forward;
            Vector3 rel = transform.position - crawl.entry;
            rel.y = 0f;
            along = Vector3.Dot(rel, dir);
            lateral = rel - dir * along;
        }

        /// <summary>Auto mode's crawl. True while it is steering.</summary>
        private bool UpdateAutoCrawl(Transform body)
        {
            UpdateCrawlDetection();
            if (!crawl.active)
                return false;

            CrawlFrame(out Vector3 dir, out float along, out Vector3 lateral, out float length);

            // Out the far side with room to stand: done.
            if (along > length + 0.6f)
            {
                crawl.active = false;
                MarkNodesFlownPast();
                return false;
            }
            // Wandered off (a replan, a shove): let the path take over again.
            if (along < -CRAWL_ENGAGE_DISTANCE - 1f || lateral.magnitude > 1.5f)
            {
                crawl.active = false;
                return false;
            }

            // Getting nowhere along the line (something in the way under there, or no crawl
            // space at all): give it up, and the path and stuck handling take over again.
            // Lining up counts as progress too, but not for ever: on Embrion a line-up pushing
            // sideways into a wall held auto mode at the mouth for two minutes.
            bool crouchedDown = playerController.thisController.height <= CRAWL_CROUCHED_HEIGHT;
            if (along > crawlBestAlong + 0.15f || lateral.magnitude < crawlBestLateral - 0.1f)
            {
                crawlBestAlong = Mathf.Max(crawlBestAlong, along);
                crawlBestLateral = Mathf.Min(crawlBestLateral, lateral.magnitude);
                crawlProgressTime = Time.time;
            }
            else if (Time.time - crawlProgressTime > (crouchedDown ? CRAWL_STALL_TIME : CRAWL_STALL_TIME * 2f))
            {
                Debug.Log($"Pathfinder: crawl given up at {along:F2} of {length:F2}");
                crawl.active = false;
                crawlRejectedEntry = crawl.entry;
                crawlRejectedUntil = Time.time + 15f;
                return false;
            }

            ResetStuckTracking();
            CenterCamera(body);
            AutoSprintInput = false;

            // Outside the mouth: get onto the line, a little back from the entry.
            if (along < -0.3f && (lateral.magnitude > CRAWL_LINE_TOLERANCE || along < -1.2f))
            {
                Vector3 lineup = crawl.entry - dir * 0.6f;
                Vector3 to = lineup - transform.position;
                to.y = 0f;
                if (to.magnitude > 0.05f && lateral.magnitude > CRAWL_LINE_TOLERANCE)
                {
                    // Sideways onto the line without turning away from it.
                    TurnBodyToward(body, dir);
                    Vector3 move = -lateral.normalized * Mathf.Clamp(lateral.magnitude * 5f, 0.25f, 1f) + dir * 0.1f;
                    AutoMoveInput = InputToward(body, move, Mathf.Clamp(move.magnitude, 0.25f, 1f));
                    return true;
                }
                TurnBodyToward(body, dir);
                AutoMoveInput = InputToward(body, dir, 0.6f);
                return true;
            }

            // At the mouth: crouch, and wait for the crouch to finish facing along the line.
            if (!playerController.isCrouching)
            {
                playerController.Crouch(true);
                autoCrouched = playerController.isCrouching;
            }
            float turn = TurnBodyToward(body, dir);
            if (playerController.thisController.height > CRAWL_CROUCHED_HEIGHT || turn > 10f)
            {
                ClearAutoInput();
                return true;
            }

            // Under: along the line, steering back onto it.
            Vector3 crawlMove = dir - lateral * 4f;
            AutoMoveInput = InputToward(body, crawlMove, 1f);
            return true;
        }

        /// <summary>Manual mode's crawl: the crouch cue and the sideways aim assist.</summary>
        private void UpdateManualCrawl()
        {
            manualCrawlAssist = Vector3.zero;
            UpdateCrawlDetection();
            if (!crawl.active)
                return;

            CrawlFrame(out Vector3 dir, out float along, out Vector3 lateral, out float length);
            if (along > length + 0.6f || along < -CRAWL_ENGAGE_DISTANCE - 1f || lateral.magnitude > 1.5f)
            {
                crawl.active = false;
                return;
            }

            // Crouched, pushing on, and getting nowhere along it: not a way through after all.
            bool pushing = playerController.moveInputVector.sqrMagnitude > 0.04f;
            if (along > crawlBestAlong + 0.15f || !pushing || !playerController.isCrouching)
            {
                crawlBestAlong = Mathf.Max(crawlBestAlong, along);
                crawlProgressTime = Time.time;
            }
            else if (Time.time - crawlProgressTime > CRAWL_STALL_TIME)
            {
                Debug.Log($"Pathfinder: manual crawl given up at {along:F2} of {length:F2}");
                crawl.active = false;
                crawlRejectedEntry = crawl.entry;
                crawlRejectedUntil = Time.time + 15f;
                return;
            }

            if (!crawl.cued && along > -1.5f)
            {
                crawl.cued = true;
                manualCrouchCued = true;
                // Once per crawl space: the crouch cue may have named this one already. The next
                // rack along is a new one, however soon it comes.
                if (Time.time - lastCrawlCueTime > 4f || HorizontalDistance(transform.position, lastCrawlCuePosition) > 2f)
                {
                    lastCrawlCueTime = Time.time;
                    lastCrawlCuePosition = transform.position;
                    Utilities.SpeakText(playerController.isCrouching ? "Crawl space" : "Crouch, crawl space");
                }
            }

            // A gentle draw onto the line once close, harder when crouched and under.
            if (along > -1.5f)
            {
                float strength = playerController.isCrouching ? 6f : 3f;
                manualCrawlAssist = -lateral.normalized * Mathf.Clamp(lateral.magnitude * strength, 0f, 0.9f);
            }
        }

        /// <summary>Where a manual player's ping comes from during a crawl: along the line.</summary>
        private bool ManualCrawlGuide(out Vector3 guide)
        {
            guide = Vector3.zero;
            if (currentMode != PathfindingMode.Manual || !crawl.active)
                return false;
            CrawlFrame(out Vector3 dir, out float along, out _, out float length);
            float ahead = Mathf.Clamp(along + 1.5f, 0f, length + 1f);
            guide = crawl.entry + dir * ahead;
            guide.y = crawl.entry.y + NODE_GROUND_OFFSET;
            return true;
        }

        private void ResetCrawlState()
        {
            crawl = default;
            manualCrawlAssist = Vector3.zero;
        }
    }
}
