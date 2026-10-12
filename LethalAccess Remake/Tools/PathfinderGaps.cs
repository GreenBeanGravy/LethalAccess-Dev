using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;

namespace LethalAccess
{
    /// <summary>
    /// Gaps: a break in the floor along the path with floor again on the far side, like the
    /// factory's girder rooms, where the NavMesh links catwalks and a narrow girder across a shaft.
    ///
    /// Each gap gets a jump plan: candidate jump lines (angles and takeoff points along the near
    /// edge) and takeoff speeds (a walk, or a sprint) are tried in a simulation of the game's own
    /// jump (0.15 s windup, jumpForce lift, 38 m/s² gravity, speed kept in the air while the
    /// input is held), with a player-sized capsule checked against floors, walls and railings at
    /// every step. The plan kept is one that lands on solid floor even when the takeoff speed is
    /// half a metre a second off, preferring a walk and the line nearest the path.
    ///
    /// Auto mode runs the plan: walks to the start of the run-up, runs the line, presses jump a
    /// windup before the takeoff point. Manual mode uses it for a sensor tone that speeds up
    /// toward the takeoff point, a "jump now" tone, a jump pressed a little early held until the
    /// takeoff point, a stop at the edge instead of a fall, and a light steer onto the line.
    /// </summary>
    internal partial class Pathfinder
    {
        private static readonly float[] GapSearchAngles = { 0f, -10f, 10f, -20f, 20f, -30f, 30f, -40f, 40f };
        private static readonly float[] EdgeProbeAngles = { 0f, -45f, 45f };
        private static readonly float[] GapFineAngles = { 0f, -6f, 6f, -12f, 12f };
        private static readonly float[] GapTurnAngles = { 10f, -10f, 20f, -20f, 30f, -30f, 45f, -45f, 60f, -60f };
        private struct Gap
        {
            public Vector3 edge;       // last floor before the gap along the path, at floor height
            public Vector3 landing;    // first floor after it
            public Vector3 target;     // where the path goes on to past the landing
            public Vector3 axisTarget; // further on, for the landing's direction
            public Vector3 approach;   // where the path comes from before the edge, for the run-up's direction
            public float width;        // flat distance edge to landing
            public float rise;         // landing height above the edge
        }

        private struct JumpPlan
        {
            public bool valid;
            public Vector3 takeoff;    // where the feet leave the floor
            public Vector3 direction;  // flat: the run-up line
            public Vector3 steer;      // flat: the move input from the jump press on (the run-up line unless the jump turns)
            public float speed;        // horizontal speed at takeoff
            public bool sprint;
            public float inputMagnitude;
            public float runUp;        // floor behind the takeoff to build the speed on
            public Vector3 landing;    // where the simulation came down
            public Vector3 forEdge;    // the gap edge this plan was made for
            public bool forManual;     // made for a keyboard player: full walk or sprint only
        }

        private enum GapPhase
        {
            Approach,
            RunUp
        }

        // How far along the path to look. A full sprint takes about 2 s to build up, so the
        // run-up starts well before the edge.
        private const float GAP_SCAN_DISTANCE = 12f;
        private const float GAP_SCAN_STEP = 0.25f;
        // Narrower breaks are cracks a stride crosses.
        private const float GAP_MIN_WIDTH = 0.6f;
        // Floor beyond a hole this much higher than the near side is a ledge to jump onto, however
        // narrow the hole (the controller's step offset is far lower).
        private const float GAP_STEP_UP = 0.6f;
        // Run-up for a walking jump: the press comes a windup (about 0.7 m) before the edge, the
        // walk takes about 0.3 m to reach full speed, and the start is reached to within 0.3 m.
        private const float WALK_RUN_UP = 1.6f;
        // No floor within this far below counts as a hole; a drop to floor nearer than this is a step down.
        private const float GAP_HOLE_DEPTH = 2f;
        // Floor resuming lower than this below the near side is a drop to walk off, not a gap.
        // Just under the hole depth: a girder 1.2 m below a catwalk is still jumped to.
        private const float GAP_MAX_LANDING_DROP = 1.9f;
        // PlayerControllerB.PlayerJump waits this long after the press before lifting off.
        private const float JUMP_WINDUP = 0.15f;
        // Sprint multiplier the game lerps toward (PlayerControllerB.Update).
        private const float SPRINT_MULTIPLIER = 2.25f;
        private const float GRAVITY = 38f;
        // A plan has to land at these takeoff speeds either side of its own.
        private const float SPEED_TOLERANCE_LOW = 0.45f;
        private const float SPEED_TOLERANCE_HIGH = 0.55f;
        // The run-up keeps the feet this close to the jump line.
        private const float LINE_TOLERANCE = 0.2f;
        // Manual run-up assist: the most sideways input added to draw the feet onto the line.
        private const float MANUAL_LINE_ASSIST = 0.4f;
        // A plan has to land from takeoffs this far either side of its line.
        private const float LATERAL_TOLERANCE = 0.15f;

        private bool hasGap;
        private Gap gap;
        private JumpPlan plan;
        private GapPhase gapPhase;
        private int gapAttempts;
        private float gapScanTime = -999f;
        private bool gapJumpInProgress;
        private float gapJumpStart;
        private JumpPlan jumpingPlan;
        private Vector3 gapCuedEdge = Vector3.positiveInfinity;
        private float gapToneTimer;
        private float lastEdgeCueTime = -999f;
        private bool jumpBuffered;
        // Manual aim assist added to the move input (world space), set each frame by UpdateManualGap.
        private Vector3 manualAssist;
        // Set during a manual turning jump: the world direction a forward move input flies.
        private Vector3 manualSteer;
        private bool manualEdgeGuard;
        // PlayerControllerB.Update: walking speed is movementSpeed / carryWeight, times the
        // sprint multiplier.
        private float walkSpeedEstimate => playerController != null
            ? playerController.movementSpeed / Mathf.Max(playerController.carryWeight, 1f)
            : 4.6f;

        private AudioClip gapTickClip;
        private AudioClip gapNowClip;
        private AudioClip gapBufferClip;
        private readonly List<Vector3> gapLine = new List<Vector3>();

        #region Detection
        private void UpdateGapScan()
        {
            // Mid-jump the plan being flown stays the plan.
            if (gapJumpInProgress)
                return;
            // Every frame when moving fast: at 6 m/s a tenth of a second is most of a metre.
            if (Time.time - gapScanTime < 0.1f && HorizontalSpeed() < 2f)
                return;
            gapScanTime = Time.time;

            bool found = currentTargetNode != null && playerController.thisController.isGrounded && FindGap(out gap);
            if (!found)
            {
                // Keep a plan for a moment through a missed scan near the edge.
                if (hasGap && plan.valid && DistanceToTakeoff() < 1.5f && DistanceToTakeoff() > -0.3f)
                    return;
                hasGap = false;
                return;
            }

            hasGap = true;
            bool manual = currentMode == PathfindingMode.Manual;
            if (!plan.valid || plan.forManual != manual || (plan.forEdge - gap.edge).sqrMagnitude > 0.75f * 0.75f)
            {
                plan = PlanJump(gap);
                gapPhase = GapPhase.Approach;
                gapAttempts = 0;
            }
        }

        /// <summary>
        /// Walks the path nodes in short steps from the node behind the player, dropping a ray at
        /// each step, and returns the first hole ahead of the player with floor again on the far
        /// side. Nodes sit on the centre of the floor; a line from the player's own feet, off
        /// centre on a narrow girder, runs off its side and reads as a gap that is not there.
        /// </summary>
        private bool FindGap(out Gap found)
        {
            found = default;
            gapLine.Clear();

            Vector3 start = transform.position;
            float refY = transform.position.y;
            int first = currentTargetNode.index;
            if (first > 0)
            {
                Vector3 previous = pathNodes[first - 1].position - Vector3.up * NODE_GROUND_OFFSET;
                if (GroundAt(previous, previous.y, out float previousY) && Mathf.Abs(previousY - previous.y) < 0.5f)
                {
                    start = new Vector3(previous.x, previousY, previous.z);
                    refY = previousY;
                }
            }
            gapLine.Add(start);
            for (int i = first; i < pathNodes.Count; i++)
            {
                gapLine.Add(pathNodes[i].position);
            }

            float scanDistance = GAP_SCAN_DISTANCE + HorizontalDistance(start, transform.position);
            Vector3 lastSolid = start;
            Vector3 firstMissing = Vector3.zero;
            bool inHole = false;
            float walked = 0f;

            for (int seg = 0; seg < gapLine.Count - 1 && walked < scanDistance; seg++)
            {
                Vector3 a = gapLine[seg];
                Vector3 b = gapLine[seg + 1];
                // A segment rising or falling more than a jump is a ladder, a climb link or a
                // drop: nothing to jump, and nothing past it is reached by jumping from here.
                if (Mathf.Abs(b.y - a.y) > (Probe != null ? Probe.MaxJumpHeight : 2f) + 0.5f || SegmentIsLadder(a, b))
                    return false;
                float length = HorizontalDistance(a, b);
                int steps = Mathf.Max(1, Mathf.CeilToInt(length / GAP_SCAN_STEP));
                for (int s = 1; s <= steps && walked < scanDistance; s++)
                {
                    walked += length / steps;
                    Vector3 p = Vector3.Lerp(a, b, (float)s / steps);
                    bool solid = GroundAt(p, refY, out float groundY);

                    if (!inHole)
                    {
                        if (solid)
                        {
                            lastSolid = new Vector3(p.x, groundY, p.z);
                            refY = groundY;
                        }
                        else
                        {
                            inHole = true;
                            firstMissing = p;
                        }
                        continue;
                    }

                    if (!solid)
                        continue;
                    if (groundY < refY - GAP_MAX_LANDING_DROP)
                        return false; // Floor resumes lower down: a drop, walked off as normal.

                    Vector3 landing = RefineBoundary(new Vector3(p.x, groundY, p.z), firstMissing, refY);
                    Vector3 edge = RefineBoundary(lastSolid, firstMissing, refY);
                    float width = HorizontalDistance(edge, landing);
                    inHole = false;
                    lastSolid = new Vector3(p.x, groundY, p.z);
                    refY = groundY;
                    // A crack a stride crosses; but a crack with a ledge up behind it is a jump.
                    if (width < GAP_MIN_WIDTH && groundY - edge.y < GAP_STEP_UP)
                        continue;

                    // Higher than any jump, or a ladder there: a climb, which the ladder and
                    // climb-link handling takes care of.
                    float maxRise = Probe != null ? Probe.MaxJumpHeight : 2f;
                    if (groundY - edge.y > maxRise || LadderForStep(new Vector3(p.x, groundY, p.z), out _) != null)
                        return false;

                    // Already behind the player: the gap just crossed. Look on past it.
                    Vector3 across = landing - edge;
                    across.y = 0f;
                    Vector3 toEdge = edge - transform.position;
                    toEdge.y = 0f;
                    if (Vector3.Dot(toEdge, across.normalized) < -0.3f)
                        continue;

                    found = new Gap
                    {
                        edge = edge,
                        landing = landing,
                        target = PathPointPast(landing, seg + 1, 1.0f),
                        axisTarget = PathPointPast(landing, seg + 1, 2.0f),
                        approach = PathPointBefore(edge, seg, 2.0f),
                        width = width,
                        rise = landing.y - edge.y
                    };
                    return true;
                }
            }
            return false;
        }

        private bool GroundAt(Vector3 point, float refY, out float groundY)
        {
            groundY = 0f;
            int count = Physics.RaycastNonAlloc(new Vector3(point.x, refY + 2.3f, point.z), Vector3.down, groundHitCache,
                2.3f + GAP_HOLE_DEPTH, groundMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            float level = float.MaxValue;
            float levelY = 0f;
            for (int i = 0; i < count; i++)
            {
                if (!IsFloorHit(groundHitCache[i]))
                    continue;
                float y = groundHitCache[i].point.y;
                // A floor at the path's own height wins over anything above it: under the edge
                // of a raised platform (Vow's pipe rooms) its top, 2.3 m up, is the first hit.
                if (Mathf.Abs(y - refY) < 0.6f && Mathf.Abs(y - refY) < level)
                {
                    level = Mathf.Abs(y - refY);
                    levelY = y;
                }
                if (groundHitCache[i].distance < best)
                {
                    best = groundHitCache[i].distance;
                    groundY = y;
                }
            }
            if (level < float.MaxValue)
                groundY = levelY;
            return best < float.MaxValue;
        }

        /// <summary>A ladder runs from one end of this path segment to the other (a short one,
        /// like the ship's, is less than a jump high but still a climb, not a gap).</summary>
        private bool SegmentIsLadder(Vector3 a, Vector3 b)
        {
            if (Mathf.Abs(b.y - a.y) < 1.2f)
                return false;
            Vector3 low = a.y < b.y ? a : b;
            Vector3 high = a.y < b.y ? b : a;
            foreach (var ladder in GetLadders())
            {
                if (ladder == null)
                    continue;
                if (HorizontalDistance(ladder.bottomOfLadderPosition.position, low) < 2.5f &&
                    HorizontalDistance(ladder.topOfLadderPosition.position, high) < 2.5f)
                    return true;
            }
            return false;
        }

        /// <summary>Narrows a floor/hole boundary between a solid and a missing sample to a few centimetres.</summary>
        private Vector3 RefineBoundary(Vector3 solid, Vector3 missing, float refY)
        {
            Vector3 s = solid;
            Vector3 m = missing;
            for (int i = 0; i < 4; i++)
            {
                Vector3 mid = Vector3.Lerp(s, m, 0.5f);
                if (GroundAt(mid, refY, out float y) && Mathf.Abs(y - solid.y) < 0.35f)
                {
                    s = new Vector3(mid.x, y, mid.z);
                }
                else
                {
                    m = mid;
                }
            }
            return s;
        }

        /// <summary>The path point about distance back from the edge, walking the scan line
        /// backward from segment seg; the edge itself if the line is shorter.</summary>
        private Vector3 PathPointBefore(Vector3 edge, int seg, float distance)
        {
            Vector3 previous = edge;
            float walked = 0f;
            for (int k = seg; k >= 0; k--)
            {
                Vector3 next = gapLine[k];
                float step = HorizontalDistance(previous, next);
                if (step < 0.01f)
                    continue;
                if (walked + step >= distance)
                    return Vector3.Lerp(previous, next, (distance - walked) / step);
                walked += step;
                previous = next;
            }
            return previous;
        }

        /// <summary>The path point about distance past landing, on floor near the landing's height;
        /// the landing itself if there is none.</summary>
        private Vector3 PathPointPast(Vector3 landing, int fromIndex, float distance)
        {
            Vector3 previous = landing;
            float walked = 0f;
            for (int k = fromIndex; k < gapLine.Count; k++)
            {
                Vector3 next = gapLine[k];
                float step = HorizontalDistance(previous, next);
                if (step < 0.01f)
                    continue;
                if (walked + step >= distance)
                {
                    Vector3 point = Vector3.Lerp(previous, next, (distance - walked) / step);
                    if (GroundAt(point, landing.y, out float y) && Mathf.Abs(y - landing.y) < 0.8f)
                        return new Vector3(point.x, y, point.z);
                    return landing;
                }
                walked += step;
                previous = next;
            }
            return landing;
        }
        #endregion

        #region Planning
        private int BlockerMask => groundMask;

        private static readonly float[] TakeoffOffsets = { 0f, -0.15f, 0.15f, -0.3f, 0.3f, -0.5f, 0.5f, -0.75f, 0.75f, -1f, 1f, -1.25f, 1.25f, -1.5f, 1.5f };

        /// <summary>Tries jump lines and speeds in simulation and keeps the most comfortable one
        /// that lands with room to spare. See the class summary.</summary>
        private JumpPlan PlanJump(Gap g)
        {
            JumpPlan best = new JumpPlan { valid = false, forEdge = g.edge, forManual = currentMode == PathfindingMode.Manual };
            float bestCost = float.MaxValue;

            Vector3 across = g.landing - g.edge;
            across.y = 0f;
            if (across.sqrMagnitude < 0.0001f)
                return best;
            across.Normalize();
            Vector3 perpAcross = Vector3.Cross(Vector3.up, across);

            var directions = new List<Vector3>();
            foreach (float angle in GapSearchAngles)
            {
                directions.Add(Quaternion.AngleAxis(angle, Vector3.up) * across);
            }
            // Along the floor the path arrives on: a run-up down the length of a girder angled
            // to the gap, where the straight line across runs off its side.
            foreach (Vector3 towards in new[] { g.target - g.edge, g.axisTarget - g.landing, g.edge - g.approach })
            {
                Vector3 d = towards;
                d.y = 0f;
                if (d.sqrMagnitude > 0.04f && Vector3.Dot(d.normalized, across) > 0.3f)
                    directions.Add(d.normalized);
            }
            // The floor's own length can run nearly sideways to the landing (a girder ending
            // beside a catwalk): a run along it and a turn in the air still makes it.
            Vector3 floorAxis = FloorAxis(g.edge, across);
            if (floorAxis != Vector3.zero && Vector3.Dot(floorAxis, across) > -0.1f)
            {
                // And a little either way: the measured axis is only as fine as its 10° steps.
                foreach (float angle in GapFineAngles)
                    directions.Add(Quaternion.AngleAxis(angle, Vector3.up) * floorAxis);
            }

            float walk = walkSpeedEstimate;
            float maxRun = walk * SPRINT_MULTIPLIER * 0.92f;
            // Partial walks for short hops onto narrow landings (the game walks at the move
            // input's size; under about 0.45 it stops playing the walk animation), a full walk,
            // then sprints.
            // A keyboard player walks at full input or sprints; partial walks are auto mode's.
            bool manual = currentMode == PathfindingMode.Manual;
            var speeds = manual ? new List<float> { walk } : new List<float> { walk * 0.6f, walk * 0.8f, walk };
            for (float s = walk + 1f; s <= maxRun; s += 1f)
            {
                speeds.Add(s);
            }

            planTakeoffs = 0;
            planSimulations = 0;
            PlanPass(g, across, perpAcross, directions, speeds, walk, steering: false, ref best, ref bestCost);
            // None straight: run up one line and steer in the air. A girder angled to the gap
            // gives a long run-up only along itself, and its own line can end in a railing.
            if (!best.valid)
                PlanPass(g, across, perpAcross, directions, speeds, walk, steering: true, ref best, ref bestCost);
            lastPlanStats = $"directions={directions.Count} takeoffs={planTakeoffs} simulations={planSimulations} valid={best.valid}";
            return best;
        }

        private void PlanPass(Gap g, Vector3 across, Vector3 perpAcross, List<Vector3> directions, List<float> speeds, float walk,
            bool steering, ref JumpPlan best, ref float bestCost)
        {
            var steers = new List<Vector3>();
            foreach (Vector3 dir in directions)
            {
                steers.Clear();
                if (steering)
                {
                    foreach (float turn in GapTurnAngles)
                    {
                        Vector3 s = Quaternion.AngleAxis(turn, Vector3.up) * dir;
                        if (Vector3.Dot(s, across) > 0.5f)
                            steers.Add(s);
                    }
                }
                else
                {
                    steers.Add(dir);
                }
                float angleCost = Vector3.Angle(dir, across) / 20f;
                // Takeoff points along the near edge either side of the path's own edge point,
                // which can sit behind a railing (the path squeezes past its end).
                foreach (float offset in TakeoffOffsets)
                {
                    // Offsets across the run-up line itself, so on a girder they stay on it.
                    Vector3 perpRun = Vector3.Cross(Vector3.up, dir);
                    if (!FindTakeoff(g, g.edge + perpRun * offset, dir, out Vector3 floorEnd, out float floorRunUp))
                        continue;

                    // Off the very end, or a little before it: under a ledge's lip the arc from
                    // the end meets the lip, from further back it clears it.
                    foreach (float pullback in TakeoffPullbacks)
                    {
                        float runUpAvailable = floorRunUp - pullback;
                        if (runUpAvailable < 0.75f)
                            break;
                        Vector3 takeoff = floorEnd;
                        if (pullback > 0f)
                        {
                            takeoff = floorEnd - dir * pullback;
                            // The run-up's floor, which may slope (a girder rises about 0.2 m a metre).
                            if (!GroundAt(takeoff, floorEnd.y, out float takeoffY) || Mathf.Abs(takeoffY - floorEnd.y) > 0.15f + 0.3f * pullback)
                                break;
                            takeoff.y = takeoffY;
                        }
                        planTakeoffs++;
                        TryTakeoff(g, dir, steers, speeds, walk, takeoff, runUpAvailable,
                            angleCost + Mathf.Abs(offset) * 1.5f + pullback * 0.8f, ref best, ref bestCost);
                    }
                }
            }
        }

        // Planner counters, for debugging plans from the console.
        private int planTakeoffs;
        private int planSimulations;
        private string lastPlanStats;

        private static readonly float[] TakeoffPullbacks = { 0f, 0.4f, 0.8f, 1.2f, 1.6f, 2.0f };

        /// <summary>Every speed and steer from one takeoff; keeps the cheapest that lands with the
        /// speed and line a little off.</summary>
        private void TryTakeoff(Gap g, Vector3 dir, List<Vector3> steers, List<float> speeds, float walk, Vector3 takeoff,
            float runUpAvailable, float baseCost, ref JumpPlan best, ref float bestCost)
        {
            foreach (float speed in speeds)
            {
                bool sprint = speed > walk + 0.05f;
                // A walk holds its speed exactly; a sprint only roughly.
                float low = sprint ? SPEED_TOLERANCE_LOW : 0.25f;
                float high = sprint ? SPEED_TOLERANCE_HIGH : 0.25f;
                float runUp = sprint ? RunUpDistance(speed) : WALK_RUN_UP;
                if (runUpAvailable < runUp)
                    break; // Faster ones need even more.

                float magnitude = sprint ? 1f : Mathf.Clamp01(speed / walk);
                foreach (Vector3 steer in steers)
                {
                    float cost = baseCost + (sprint ? 1f + (speed - walk) * 0.4f : (walk - speed) * 0.3f) +
                                 Vector3.Angle(dir, steer) / 30f;
                    if (cost >= bestCost)
                        continue;

                    if (!SimulateJump(takeoff, dir, steer, speed, sprint, magnitude, g, out Vector3 landing) ||
                        !SimulateJump(takeoff, dir, steer, speed - low, sprint, sprint ? 1f : magnitude * (speed - low) / speed, g, out _) ||
                        !SimulateJump(takeoff, dir, steer, speed + high, sprint, sprint ? 1f : magnitude * (speed + high) / speed, g, out _))
                        continue;
                    // And from a little to either side: nobody runs a line exactly.
                    Vector3 side = Vector3.Cross(Vector3.up, dir) * LATERAL_TOLERANCE;
                    if (!SimulateJump(takeoff + side, dir, steer, speed, sprint, magnitude, g, out _) ||
                        !SimulateJump(takeoff - side, dir, steer, speed, sprint, magnitude, g, out _))
                        continue;

                    cost += HorizontalDistance(landing, g.target) * 0.3f;
                    if (cost >= bestCost)
                        continue;

                    bestCost = cost;
                    best = new JumpPlan
                    {
                        valid = true,
                        takeoff = takeoff,
                        direction = dir,
                        steer = steer,
                        speed = speed,
                        sprint = sprint,
                        inputMagnitude = magnitude,
                        runUp = runUp,
                        landing = landing,
                        forEdge = g.edge,
                        forManual = currentMode == PathfindingMode.Manual
                    };
                }
            }
        }

        /// <summary>
        /// The direction to run in along the floor that reaches the edge from furthest back:
        /// the length of a girder or plank, whichever way the path arrived (or did not).
        /// Zero when nothing reaches back a metre.
        /// </summary>
        private Vector3 FloorAxis(Vector3 edge, Vector3 across)
        {
            Vector3 best = Vector3.zero;
            float bestLength = 1f;
            for (float angle = -80f; angle <= 80f; angle += 10f)
            {
                Vector3 back = Quaternion.AngleAxis(angle, Vector3.up) * -across;
                float previousY = edge.y;
                float length = 0f;
                for (float d = 0.25f; d <= 5f; d += 0.25f)
                {
                    if (!GroundAt(edge + back * d, previousY, out float y) || Mathf.Abs(y - previousY) > 0.15f)
                        break;
                    previousY = y;
                    length = d;
                }
                if (length > bestLength + 0.01f)
                {
                    bestLength = length;
                    best = -back;
                }
            }
            return best;
        }

        /// <summary>
        /// On the line through start along dir: the last floor before the gap (takeoff, a few
        /// centimetres short of it) and how much clear floor lies behind it for a run-up.
        /// </summary>
        private bool FindTakeoff(Gap g, Vector3 start, Vector3 dir, out Vector3 takeoff, out float runUpAvailable)
        {
            takeoff = start;
            runUpAvailable = 0f;
            float refY = g.edge.y;

            // Find floor on this line a little short of the edge. On a girder the line can run
            // off its side a metre back, so search outward from the edge rather than from a
            // fixed point behind it.
            Vector3 from = Vector3.zero;
            float fromY = 0f;
            bool anchored = false;
            for (float back = 0.3f; back <= 1.5f; back += 0.15f)
            {
                Vector3 q = start - dir * back;
                if (GroundAt(q, refY + 0.5f, out float y) && Mathf.Abs(y - refY) < 0.6f)
                {
                    from = q;
                    fromY = y;
                    anchored = true;
                    break;
                }
            }
            if (!anchored)
                return false;

            // Follow the surface to where it ends. Step to step, so a sloped girder counts as one
            // surface while a bevelled end or a step down marks the edge.
            Vector3 lastSolid = new Vector3(from.x, fromY, from.z);
            bool sawEdge = false;
            for (float d = 0.05f; d <= 3f; d += 0.05f)
            {
                Vector3 q = from + dir * d;
                if (GroundAt(q, lastSolid.y, out float y) && Mathf.Abs(y - lastSolid.y) < 0.08f)
                {
                    lastSolid = new Vector3(q.x, y, q.z);
                    continue;
                }
                sawEdge = true;
                break;
            }
            if (!sawEdge)
                return false;
            takeoff = lastSolid - dir * 0.05f;

            // Clear floor behind the takeoff with room for the body. Step by step, so a run-up
            // can follow a gentle slope (a girder rises 0.7 m over its length).
            int blockers = BlockerMask;
            float previousY = takeoff.y;
            for (float d = 0.25f; d <= 10f; d += 0.25f)
            {
                Vector3 q = takeoff - dir * d;
                if (!GroundAt(q, previousY, out float y) || Mathf.Abs(y - previousY) > 0.15f)
                    break;
                previousY = y;
                Vector3 feet = new Vector3(q.x, y, q.z);
                if (Physics.CheckCapsule(feet + Vector3.up * 0.55f, feet + Vector3.up * 2.1f, 0.35f, blockers, QueryTriggerInteraction.Ignore))
                    break;
                runUpAvailable = d;
            }
            return runUpAvailable >= 0.75f;
        }

        /// <summary>
        /// Flies the game's jump from takeoff: lift at jumpForce, gravity 38 m/s², horizontal
        /// speed held, and still building while sprint is held (the sprint multiplier keeps
        /// rising toward 2.25 at rate 1/s in the air; a recorded 6.3 m/s takeoff averaged
        /// 7.3 m/s over the flight). Fails on touching anything mid-air or falling below the
        /// gap; succeeds on coming down onto floor past the gap with room to stand.
        /// </summary>
        // Why the last simulated jump ended, for debugging plans from the console.
        private string lastSimulation;

        private bool SimulateJump(Vector3 takeoff, Vector3 dir, float speed, bool sprint, Gap g, out Vector3 landing)
        {
            float walk = Mathf.Max(walkSpeedEstimate, 0.5f);
            return SimulateJump(takeoff, dir, dir, speed, sprint, sprint ? 1f : Mathf.Clamp01(speed / walk), g, out landing);
        }

        /// <summary>
        /// From the jump press a windup short of takeoff: the windup on the ground, then the
        /// flight. The move input switches from the run-up line to steer at the press, and the
        /// game's move vector turns toward it at the ground rate until 0.1 s after liftoff, then
        /// at 1.33/s (PlayerControllerB.Update, PlayerJump). Velocity is that vector times the
        /// walking speed times the sprint multiplier.
        /// </summary>
        private bool SimulateJump(Vector3 takeoff, Vector3 dir, Vector3 steer, float speed, bool sprint, float magnitude, Gap g, out Vector3 landing)
        {
            planSimulations++;
            landing = takeoff;
            if (speed <= 0.5f)
                return false;

            float jumpForce = playerController != null ? playerController.jumpForce : 13f;
            float carry = playerController != null ? Mathf.Max(playerController.carryWeight, 1f) : 1f;
            float groundRate = sprint ? 5f / (carry * 1.5f) : 10f / carry;
            int blockers = BlockerMask;
            const float dt = 0.02f;

            float walk = Mathf.Max(walkSpeedEstimate, 0.5f);
            magnitude = Mathf.Max(magnitude, 0.05f);
            float multiplier = sprint ? Mathf.Clamp(speed / (walk * magnitude), 1f, SPRINT_MULTIPLIER) : 1f;
            Vector3 moveVector = dir * magnitude;
            Vector3 moveTarget = steer * magnitude;
            Vector3 across = g.landing - g.edge;
            across.y = 0f;
            across = across.sqrMagnitude > 0.0001f ? across.normalized : dir;

            Vector3 pos = takeoff - dir * (speed * JUMP_WINDUP);
            float vy = 0f;
            bool airborne = false;
            float time = 0f;
            for (int step = 0; step < 160; step++)
            {
                time += dt;
                if (!airborne && time >= JUMP_WINDUP)
                {
                    airborne = true;
                    vy = jumpForce;
                }
                float sinceLift = time - JUMP_WINDUP;
                float rate = sinceLift < 0.1f ? groundRate : 1.33f;
                moveVector = Vector3.MoveTowards(moveVector, moveTarget, rate * dt);
                if (sprint)
                    multiplier += (SPRINT_MULTIPLIER - multiplier) * (1f - Mathf.Exp(-dt));
                Vector3 horizontal = moveVector * (walk * multiplier);

                if (!airborne)
                {
                    // Windup: still on the run-up, following its floor.
                    Vector3 ahead = pos + horizontal * dt;
                    if (GroundAt(ahead, pos.y, out float y) && Mathf.Abs(y - pos.y) < 0.4f)
                        ahead.y = y;
                    else
                        ahead.y -= 7f * dt;
                    pos = ahead;
                    continue;
                }

                Vector3 next = pos + horizontal * dt + Vector3.up * (vy * dt);
                vy -= GRAVITY * dt;

                if (vy < 0f && Physics.Raycast(next + Vector3.up * 0.6f, Vector3.down, out RaycastHit floor, 0.65f, groundMask, QueryTriggerInteraction.Ignore) &&
                    floor.normal.y > 0.6f && IsFloorHit(floor))
                {
                    Vector3 down = new Vector3(next.x, floor.point.y, next.z);
                    Vector3 past = down - g.edge;
                    past.y = 0f;
                    // Back down on the near side, or on something in the gap: not a crossing.
                    if (Vector3.Dot(past, across) < 0.2f || floor.point.y < g.edge.y - GAP_MAX_LANDING_DROP)
                    {
                        lastSimulation = $"came down short at {down} step {step}";
                        return false;
                    }
                    landing = down;
                    Vector3 heading = horizontal.sqrMagnitude > 0.01f ? horizontal.normalized : dir;
                    bool room = RoomToLand(down, heading);
                    lastSimulation = room ? $"landed at {down}" : $"no room at {down}";
                    return room;
                }

                // A little under the controller's 0.4 m: it slides off a glancing touch.
                if (Physics.CheckCapsule(next + Vector3.up * 0.45f, next + Vector3.up * 2.15f, 0.28f, blockers, QueryTriggerInteraction.Ignore))
                {
                    // Head against the ceiling on the way up: the controller stops rising and
                    // carries on sideways; the jump's timing is unchanged (the game keeps counting
                    // its fall value down regardless).
                    Vector3 level = new Vector3(next.x, pos.y, next.z);
                    if (vy > -1f && !Physics.CheckCapsule(level + Vector3.up * 0.45f, level + Vector3.up * 2.15f, 0.28f, blockers, QueryTriggerInteraction.Ignore))
                    {
                        pos = level;
                        continue;
                    }
                    lastSimulation = $"hit something at {next} step {step}";
                    return false;
                }
                if (next.y < g.edge.y - 2.5f)
                {
                    lastSimulation = $"fell at {next} step {step}";
                    return false;
                }
                pos = next;
            }
            return false;
        }

        /// <summary>Floor a little ahead and to either side of a landing point, at its height.</summary>
        private bool RoomToLand(Vector3 point, Vector3 dir)
        {
            Vector3 side = Vector3.Cross(Vector3.up, dir);
            foreach (Vector3 offset in new[] { dir * 0.35f, side * 0.2f, -side * 0.2f })
            {
                if (!GroundAt(point + offset, point.y, out float y) || Mathf.Abs(y - point.y) > 0.3f)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Run-up length to reach speed from a standstill. The game lerps the sprint multiplier
        /// toward 2.25 at rate 1/s, so the multiplier after t seconds of sprinting is
        /// 2.25 - 1.25 e^-t; getting to a walk first takes about half a metre.
        /// </summary>
        private float RunUpDistance(float speed)
        {
            float walk = Mathf.Max(walkSpeedEstimate, 0.5f);
            float multiplier = Mathf.Clamp(speed / walk, 1f, SPRINT_MULTIPLIER - 0.02f);
            float t = -Mathf.Log((SPRINT_MULTIPLIER - multiplier) / (SPRINT_MULTIPLIER - 1f));
            float distance = walk * (SPRINT_MULTIPLIER * t - (SPRINT_MULTIPLIER - 1f) * (1f - Mathf.Exp(-t)));
            // Plus getting to a walk from a standstill, and a margin.
            return distance + 1.5f;
        }
        #endregion

        #region Plan Geometry
        private float HorizontalSpeed()
        {
            Vector3 v = playerController.thisController.velocity;
            return new Vector2(v.x, v.z).magnitude;
        }

        /// <summary>Signed distance from the feet to the takeoff point along the plan's line.</summary>
        private float DistanceToTakeoff()
        {
            Vector3 to = plan.takeoff - transform.position;
            to.y = 0f;
            return Vector3.Dot(to, plan.direction);
        }

        /// <summary>Feet offset sideways from the plan's line, flat.</summary>
        private Vector3 PlanLineOffset(JumpPlan p)
        {
            Vector3 rel = transform.position - p.takeoff;
            rel.y = 0f;
            return rel - p.direction * Vector3.Dot(rel, p.direction);
        }

        /// <summary>Feet offset sideways from the straight line takeoff to planned landing, flat:
        /// the jump's own path, for a turning jump as well as a straight one.</summary>
        private Vector3 ChordOffset(JumpPlan p)
        {
            Vector3 chord = p.landing - p.takeoff;
            chord.y = 0f;
            if (chord.sqrMagnitude < 0.01f)
                return PlanLineOffset(p);
            chord.Normalize();
            Vector3 rel = transform.position - p.takeoff;
            rel.y = 0f;
            return rel - chord * Vector3.Dot(rel, chord);
        }

        /// <summary>
        /// The start of the run-up: as far back from the takeoff as the plan needs, and after a
        /// run that came in short of speed, further back, where there is floor (a 2.2 m run-up on
        /// Experimentation's girders fell 0.5 m/s short four times running and gave up).
        /// </summary>
        private Vector3 RunUpStart()
        {
            Vector3 start = plan.takeoff - plan.direction * plan.runUp;
            for (int extra = Mathf.Min(gapAttempts, 3); extra > 0; extra--)
            {
                Vector3 further = start - plan.direction * (0.7f * extra);
                if (GroundAt(further, plan.takeoff.y, out float y) && Mathf.Abs(y - plan.takeoff.y) < 0.3f &&
                    GroundAt(Vector3.Lerp(start, further, 0.5f), plan.takeoff.y, out float mid) && Mathf.Abs(mid - plan.takeoff.y) < 0.3f)
                    return further;
            }
            return start;
        }

        private void StartGapJump()
        {
            if (Probe != null && Probe.Jump())
            {
                gapJumpInProgress = true;
                gapJumpStart = Time.time;
                jumpingPlan = plan;
                jumpBuffered = false;
            }
        }

        /// <summary>Ends the jump state once the player is back on the ground.</summary>
        private void UpdateGapJumpState()
        {
            if (!gapJumpInProgress)
                return;
            // Down as soon as the feet touch after the windup: staying in jump mode keeps the
            // player running, which carried them the length of a sloped girder and off its end.
            if (Time.time - gapJumpStart > JUMP_WINDUP + 0.15f && playerController.thisController.isGrounded)
            {
                gapJumpInProgress = false;
                hasGap = false;
                plan.valid = false;
                gapScanTime = -999f;
                MarkNodesFlownPast();
            }
            else if (Time.time - gapJumpStart > 4f)
            {
                gapJumpInProgress = false;
            }
        }

        /// <summary>
        /// After a landing, every node up to the nearest one on this floor is done. A jump can
        /// carry the player past several nodes without touching their reach radius, and left
        /// unmarked they would turn the player back toward them, at a run, along a narrow girder.
        /// </summary>
        private void MarkNodesFlownPast()
        {
            PathNode nearest = null;
            float best = 3f;
            foreach (var node in pathNodes)
            {
                if (node.isReached)
                    continue;
                if (Mathf.Abs(node.position.y - NODE_GROUND_OFFSET - transform.position.y) > 1.5f)
                    continue;
                float d = HorizontalDistance(node.position, transform.position);
                if (d < best)
                {
                    best = d;
                    nearest = node;
                }
            }
            if (nearest == null)
                return;

            PathNode through = nearest;
            if (nearest.index + 1 < pathNodes.Count)
            {
                Vector3 along = pathNodes[nearest.index + 1].position - nearest.position;
                Vector3 offset = transform.position - nearest.position;
                along.y = 0f;
                offset.y = 0f;
                // Not yet past the nearest one: it stays the target.
                if (Vector3.Dot(offset, along) <= 0f && nearest.index > 0)
                    through = pathNodes[nearest.index - 1];
            }
            MarkReachedThrough(through, announce: false);
            FindNextTargetNode();
        }

        /// <summary>Move input toward a world direction.</summary>
        private static Vector2 InputToward(Transform body, Vector3 world, float magnitude)
        {
            world.y = 0f;
            Vector3 local = body.InverseTransformDirection(world.normalized);
            return new Vector2(local.x, local.z).normalized * magnitude;
        }

        private static bool Steers(JumpPlan p) => Vector3.Angle(p.direction, p.steer) > 1f;

        /// <summary>Move input for the jump itself: the steer direction for a turning jump, the
        /// line with a pull back onto it for a straight one.</summary>
        private Vector2 JumpInput(Transform body, JumpPlan p) =>
            Steers(p) ? InputToward(body, p.steer, p.inputMagnitude) : InputAlongLine(body, p, p.inputMagnitude);

        /// <summary>Move input toward a world direction, plus a pull back onto the plan's line.</summary>
        private Vector2 InputAlongLine(Transform body, JumpPlan p, float magnitude)
        {
            Vector3 offset = PlanLineOffset(p);
            Vector3 move = p.direction - offset * 2f;
            move.y = 0f;
            Vector3 local = body.InverseTransformDirection(move.normalized);
            return new Vector2(local.x, local.z).normalized * magnitude;
        }
        #endregion

        #region Auto
        /// <summary>
        /// Auto mode's gap handling: walk to the start of the plan's run-up, run the line at the
        /// plan's speed, press jump a windup before the takeoff point, and keep the input held in
        /// the air. A run that arrives off speed or off line is abandoned at the edge and tried
        /// again. Returns false when no gap needs handling yet (normal path steering brings the
        /// player in, round railings and corners, until the run-up).
        /// </summary>
        private bool UpdateAutoGap(Transform body)
        {
            if (gapJumpInProgress)
            {
                TurnBodyToward(body, jumpingPlan.steer);
                AutoMoveInput = JumpInput(body, jumpingPlan);
                AutoSprintInput = jumpingPlan.sprint;
                ResetStuckTracking();
                return true;
            }

            if (!hasGap)
                return false;

            float toTakeoff = plan.valid ? DistanceToTakeoff() : Vector3.Dot(gap.edge - transform.position, (gap.landing - gap.edge).normalized);
            if (!plan.valid)
            {
                if (toTakeoff > 2f)
                    return false;
                StopPathfinding(playSound: false);
                Utilities.SpeakText("No safe jump across this gap. Pathfinding stopped.");
                return true;
            }

            Vector3 runUpStart = RunUpStart();
            float lateral = PlanLineOffset(plan).magnitude;
            if (gapPhase == GapPhase.Approach && (toTakeoff > plan.runUp + 2.5f || !RunUpInView(runUpStart)))
                return false;

            ResetStuckTracking();
            CenterCamera(body);
            float speed = HorizontalSpeed();

            if (gapPhase == GapPhase.Approach)
            {
                Vector3 toStart = runUpStart - transform.position;
                toStart.y = 0f;
                // Start from a standstill: reversing from walking back to the start costs most of
                // a short run-up.
                if (toStart.magnitude < 0.3f && lateral < LINE_TOLERANCE && speed < 0.6f)
                {
                    gapPhase = GapPhase.RunUp;
                }
                else if (toStart.magnitude < 0.3f && lateral < LINE_TOLERANCE)
                {
                    TurnBodyToward(body, plan.direction);
                    ClearAutoInput();
                    return true;
                }
                else
                {
                    // Turn toward the line while walking back to its start.
                    TurnBodyToward(body, plan.direction);
                    Vector3 local = body.InverseTransformDirection(toStart.normalized);
                    AutoMoveInput = new Vector2(local.x, local.z).normalized * Mathf.Clamp(toStart.magnitude * 1.5f, 0.45f, 1f);
                    AutoSprintInput = false;
                    return true;
                }
            }

            // Running the line.
            TurnBodyToward(body, plan.direction);

            if (plan.sprint && playerController.isExhausted && toTakeoff > plan.runUp * 0.5f)
            {
                ClearAutoInput();
                return true;
            }

            float lead = speed * JUMP_WINDUP;
            if (toTakeoff <= lead + 0.03f)
            {
                // Whether this run makes it: the jump flown from where the player is now, at the
                // speed and heading they actually have (a slope or a late turn moves both).
                if (LiveJumpLands(plan) && Probe != null && Probe.CanJumpNow)
                {
                    StartGapJump();
                    AutoMoveInput = JumpInput(body, plan);
                    AutoSprintInput = plan.sprint;
                    return true;
                }

                // Off speed or off line: never go over the edge without the jump. Back to the
                // start of the run-up and go again; give up after a few tries.
                gapAttempts++;
                gapPhase = GapPhase.Approach;
                if (gapAttempts >= 4)
                {
                    StopPathfinding(playSound: false);
                    Utilities.SpeakText("Could not make the jump. Pathfinding stopped.");
                    return true;
                }
                Vector3 back = body.InverseTransformDirection(-plan.direction);
                AutoMoveInput = new Vector2(back.x, back.z).normalized;
                AutoSprintInput = false;
                return true;
            }

            // Speed: hold sprint only while below the plan's speed. The game drops the sprint
            // multiplier at 10/s once it is let go and raises it at 1/s, so this settles close.
            // Sprinting from a standstill accelerates slowly (the game moves toward full input at
            // 3.3/s while sprinting, 10/s while walking), so walk up to walking speed first.
            AutoMoveInput = InputAlongLine(body, plan, plan.inputMagnitude);
            AutoSprintInput = plan.sprint && speed > walkSpeedEstimate * 0.85f && speed < plan.speed + 0.1f;
            return true;
        }
        #endregion

        #region Manual
        private void UpdateManualGap()
        {
            manualEdgeGuard = false;
            manualAssist = Vector3.zero;
            manualSteer = Vector3.zero;
            Transform body = playerController.thisPlayerBody != null ? playerController.thisPlayerBody : transform;

            if (gapJumpInProgress)
            {
                // Aim assist in the air. A turning jump turns for the player: holding forward
                // flies the planned turn (nobody can feel 45 degrees of turn mid-air). A straight
                // one is drawn back toward its path.
                if (Time.time - gapJumpStart < JUMP_WINDUP + 0.05f || !playerController.thisController.isGrounded)
                {
                    if (Steers(jumpingPlan))
                    {
                        manualSteer = jumpingPlan.steer;
                    }
                    else if (!playerController.thisController.isGrounded)
                    {
                        Vector3 offset = ChordOffset(jumpingPlan);
                        manualAssist = -offset.normalized * Mathf.Clamp(offset.magnitude * 2f, 0f, 0.6f);
                    }
                }
                return;
            }

            if (!hasGap)
            {
                jumpBuffered = false;
                return;
            }

            float toTakeoff = plan.valid ? DistanceToTakeoff() : 99f;
            if (!plan.valid || toTakeoff > 7f || toTakeoff < -0.5f)
            {
                jumpBuffered = false;
                if (!plan.valid && (gapCuedEdge - gap.edge).sqrMagnitude > 1f)
                {
                    gapCuedEdge = gap.edge;
                    Utilities.SpeakText("Gap ahead, no safe jump found.");
                }
                return;
            }

            if ((gapCuedEdge - gap.edge).sqrMagnitude > 1f)
            {
                gapCuedEdge = gap.edge;
                int meters = Mathf.Max(1, Mathf.RoundToInt(gap.width));
                Utilities.SpeakText($"Gap ahead, {meters} {(meters == 1 ? "meter" : "meters")}. " +
                                    (plan.sprint ? "Sprint, keep sprinting through the jump, and jump at the high tone." : "Walk, and jump at the high tone."));
            }

            Vector3 v = playerController.thisController.velocity;
            v.y = 0f;
            bool lined = LinedUp(body);
            bool headingIn = lined && v.magnitude > 0.5f && Vector3.Dot(v.normalized, plan.direction) > 0.3f;
            float lead = HorizontalSpeed() * JUMP_WINDUP;

            // Aim assist on the ground: ease the heading onto the plan's line when already roughly
            // facing along it.
            if (headingIn && toTakeoff < 5f && Vector3.Angle(body.forward, plan.direction) < 35f)
            {
                Vector3 offset = PlanLineOffset(plan);
                Vector3 aim = plan.direction * 2f - offset;
                Vector3 euler = body.eulerAngles;
                float yaw = Mathf.MoveTowardsAngle(euler.y, Quaternion.LookRotation(aim).eulerAngles.y, 60f * Time.deltaTime);
                body.eulerAngles = new Vector3(euler.x, yaw, euler.z);
            }

            // And the feet: a sideways touch on the move input during the run-up draws them onto
            // the line. Through the input, so the game's own movement does it (moving the
            // controller directly would also overwrite the velocity the jump timing reads).
            if (lined && headingIn && playerController.thisController.isGrounded && toTakeoff > lead && toTakeoff < plan.runUp + 1f)
            {
                Vector3 offset = PlanLineOffset(plan);
                manualAssist = -offset.normalized * Mathf.Clamp(offset.magnitude * 2f, 0f, MANUAL_LINE_ASSIST);
            }

            // Sensor tone: ticks closer together toward the takeoff point, then the high tone in
            // the window where pressing jump lifts off there.
            gapToneTimer -= Time.deltaTime;
            if (headingIn && gapToneTimer <= 0f && audioSource != null)
            {
                bool now = toTakeoff <= lead + 0.35f;
                if (now)
                {
                    if (gapNowClip != null) audioSource.PlayOneShot(gapNowClip, 0.8f);
                    gapToneTimer = 0.1f;
                }
                else
                {
                    if (gapTickClip != null) audioSource.PlayOneShot(gapTickClip, 0.6f);
                    gapToneTimer = Mathf.Lerp(0.08f, 0.45f, Mathf.Clamp01((toTakeoff - lead) / 5f));
                }
            }

            // A jump pressed early is held until the window.
            if (jumpBuffered)
            {
                if (!lined || toTakeoff > lead + 2.5f)
                {
                    jumpBuffered = false;
                }
                else if (headingIn && toTakeoff <= lead)
                {
                    jumpBuffered = false;
                    string why = WhyJumpWouldFail();
                    if (why == null)
                    {
                        StartGapJump();
                        return;
                    }
                    // Can't make it from here: the edge guard stops them instead.
                    Utilities.SpeakText(why);
                }
            }

            // At the edge without jumping: stop there instead of falling in.
            if (playerController.thisController.isGrounded && toTakeoff < 0.1f)
            {
                manualEdgeGuard = true;
                if (headingIn && Time.time - lastEdgeCueTime > 4f)
                {
                    lastEdgeCueTime = Time.time;
                    Utilities.SpeakText("Edge. Back up and take a run at it.");
                }
            }
        }

        /// <summary>
        /// Where a manual player's ping should come from while a gap is ahead: the start of the
        /// run-up until they reach it, then a point ahead along the jump line, so the ordinary
        /// on-target tones line them up. False when no gap guides the way.
        /// </summary>
        private bool ManualGapGuide(out Vector3 guide)
        {
            guide = Vector3.zero;
            if (currentMode != PathfindingMode.Manual || !hasGap || !plan.valid || gapJumpInProgress)
                return false;

            float toTakeoff = DistanceToTakeoff();
            // Past the takeoff but still on its floor (an early takeoff leaves floor beyond it):
            // still the near side, still sent back to the run-up.
            bool nearSide = Mathf.Abs(transform.position.y - plan.takeoff.y) < 0.8f && toTakeoff > -2.5f;
            if (!nearSide || toTakeoff > plan.runUp + 6f)
                return false;
            // Still round a corner from it: the path's own nodes lead there.
            if (gapPhase == GapPhase.Approach && !RunUpInView(RunUpStart()))
                return false;

            // Reaching the start puts the player on the run-up; wandering well off the line or
            // back past the start sends them to it again.
            float lateral = PlanLineOffset(plan).magnitude;
            bool farEnoughBack = toTakeoff > plan.runUp * 0.8f;
            bool onLineFarBack = lateral < 0.35f && farEnoughBack && toTakeoff < plan.runUp + 1f;
            if (gapPhase == GapPhase.Approach && farEnoughBack && (onLineFarBack || HorizontalDistance(transform.position, RunUpStart()) < 0.8f))
            {
                gapPhase = GapPhase.RunUp;
                Utilities.SpeakText("Run-up here. Face the tone, then go.");
            }
            // Stopped at the edge, or wandered off: back to the start of the run-up.
            else if (gapPhase == GapPhase.RunUp && (manualEdgeGuard || lateral > 0.8f || toTakeoff > plan.runUp + 1.5f || toTakeoff < -0.5f))
            {
                gapPhase = GapPhase.Approach;
            }

            // On the run-up: a point a little ahead on the line, so following the ping draws the
            // feet onto the line as well as turning them along it.
            guide = gapPhase == GapPhase.RunUp
                ? plan.takeoff - plan.direction * Mathf.Max(toTakeoff - 1.5f, -2f)
                : RunUpStart();
            guide.y = plan.takeoff.y + NODE_GROUND_OFFSET;
            return true;
        }

        /// <summary>
        /// The run-up's start can be walked to in a straight line. The gap code steers straight
        /// at it, ignoring the path, so from round a corner it walks into the wall (and strafes
        /// along it for ever); until then the ordinary path following brings the player there.
        /// </summary>
        private bool RunUpInView(Vector3 runUpStart)
        {
            if (HorizontalDistance(transform.position, runUpStart) < 1f)
                return true;
            return ClearChestLine(runUpStart + Vector3.up * NODE_GROUND_OFFSET);
        }

        private GameObject gapGuideObject;

        /// <summary>The ping source for the gap guide, moved to point.</summary>
        private AudioSource GapGuideAudio(Vector3 point)
        {
            if (gapGuideObject == null)
            {
                gapGuideObject = CreateNodeObject("GapGuide");
            }
            gapGuideObject.SetActive(true);
            gapGuideObject.GetComponent<MeshRenderer>().enabled = ConfigManager.EnableVisualMarkers.Value;
            gapGuideObject.transform.position = point;
            return gapGuideObject.GetComponent<AudioSource>();
        }

        /// <summary>Why a jump pressed now would not make the plan's landing: off the line, or
        /// too slow or fast for it. Null when it would.</summary>
        private string WhyJumpWouldFail()
        {
            if (LiveJumpLands(plan))
                return null;
            if (PlanLineOffset(plan).magnitude > LINE_TOLERANCE + LATERAL_TOLERANCE)
                return "Off the line. Back up and try again.";
            float speed = HorizontalSpeed();
            float low = plan.sprint ? SPEED_TOLERANCE_LOW : 0.25f;
            float high = plan.sprint ? SPEED_TOLERANCE_HIGH : 0.25f;
            if (speed < plan.speed - low - 0.1f)
                return plan.sprint ? "Too slow. Back up and sprint at it." : "Too slow. Back up and walk at it.";
            if (speed > plan.speed + high + 0.1f)
                return "Too fast. Back up and walk at it.";
            return "Off the line. Back up and try again.";
        }

        /// <summary>
        /// Flies a jump pressed now: from the feet, at the current speed and heading, a little
        /// slower and faster too, with the plan's steer in the air. True when all of them land.
        /// </summary>
        private bool LiveJumpLands(JumpPlan p)
        {
            Vector3 v = playerController.thisController.velocity;
            v.y = 0f;
            float speed = v.magnitude;
            if (speed < 0.5f || !hasGap)
                return false;
            Vector3 dir = v / speed;
            Vector3 liftoff = transform.position + dir * (speed * JUMP_WINDUP);
            bool sprint = p.sprint && playerController.isSprinting;
            float walk = Mathf.Max(walkSpeedEstimate, 0.5f);
            Vector3 steer = Steers(p) ? p.steer : p.direction;
            foreach (float s in new[] { speed, speed - 0.15f, speed + 0.15f })
            {
                float magnitude = sprint ? 1f : Mathf.Clamp(s / walk, 0.05f, 1f);
                if (!SimulateJump(liftoff, dir, steer, s, sprint, magnitude, gap, out _))
                    return false;
            }
            return true;
        }

        /// <summary>Near the plan's line and facing along it.</summary>
        private bool LinedUp(Transform body)
        {
            Vector3 facing = body.forward;
            facing.y = 0f;
            return PlanLineOffset(plan).magnitude < 0.5f && Vector3.Angle(facing, plan.direction) < 35f;
        }

        /// <summary>Called from the Jump_performed prefix: true swallows the press, which is
        /// held and released at the takeoff point.</summary>
        public bool ShouldBufferJump()
        {
            if (!isPathfinding || currentMode != PathfindingMode.Manual || !hasGap || !plan.valid || gapJumpInProgress || ObstacleProbe.ModJumping)
                return false;
            if (!playerController.thisController.isGrounded)
                return false;

            float toTakeoff = DistanceToTakeoff();
            float lead = HorizontalSpeed() * JUMP_WINDUP;
            if (toTakeoff < -0.5f || toTakeoff > lead + 2f)
                return false;

            Transform body = playerController.thisPlayerBody != null ? playerController.thisPlayerBody : transform;
            if (!LinedUp(body))
            {
                // Within reach of the edge and off the jump line: a jump from here goes into the
                // hole (a walking jump carries about 2 m). Hold it back and say why.
                if (Time.time - lastEdgeCueTime > 1.5f)
                {
                    lastEdgeCueTime = Time.time;
                    Utilities.SpeakText("Not lined up. Follow the tone.");
                }
                return true;
            }
            if (toTakeoff <= lead)
            {
                // In the window: a jump that cannot make it is held back, with the reason.
                string why = WhyJumpWouldFail();
                if (why == null)
                    return false;
                if (Time.time - lastEdgeCueTime > 1.5f)
                {
                    lastEdgeCueTime = Time.time;
                    Utilities.SpeakText(why);
                }
                return true;
            }

            jumpBuffered = true;
            if (audioSource != null && gapBufferClip != null)
            {
                audioSource.PlayOneShot(gapBufferClip, 0.6f);
            }
            return true;
        }

        /// <summary>Removes the part of a manual move input that points over the edge while the edge guard is on.</summary>
        public Vector2 FilterManualMove(Vector2 input)
        {
            if (currentMode != PathfindingMode.Manual || !isPathfinding || input.sqrMagnitude < 0.0001f)
                return input;

            Transform body = playerController.thisPlayerBody != null ? playerController.thisPlayerBody : transform;
            Vector3 world = body.right * input.x + body.forward * input.y;
            bool changed = false;
            if (manualSteer.sqrMagnitude > 0.0001f && Vector3.Dot(world, manualSteer) > 0f)
            {
                world = manualSteer * world.magnitude;
                changed = true;
            }
            else if (manualAssist.sqrMagnitude > 0.0001f)
            {
                float size = world.magnitude;
                world = (world + manualAssist).normalized * size;
                changed = true;
            }
            else if (manualCrawlAssist.sqrMagnitude > 0.0001f)
            {
                float size = world.magnitude;
                world = (world + manualCrawlAssist).normalized * size;
                changed = true;
            }
            else if (playerController.isCrouching && manualProbe.kind == ObstacleProbe.Kind.Crouch &&
                     manualProbe.laneOffset.sqrMagnitude > 0.0004f && Vector3.Dot(world, manualProbe.laneOffset) > -0.5f * world.magnitude * manualProbe.laneOffset.magnitude)
            {
                // Crawl space aim assist: draw a crouched player onto the line that fits under.
                float size = world.magnitude;
                Vector3 lane = manualProbe.laneOffset;
                world = (world + lane.normalized * Mathf.Clamp(lane.magnitude * 6f, 0.2f, 0.9f)).normalized * size;
                changed = true;
            }
            if (manualEdgeGuard && plan.valid)
            {
                float into = Vector3.Dot(world, plan.direction);
                if (into > 0f)
                {
                    world -= plan.direction * into;
                    changed = true;
                }
            }
            else if (StripDrops(body, ref world))
            {
                changed = true;
            }
            if (!changed)
                return input;
            Vector3 local = body.InverseTransformDirection(world);
            return new Vector2(local.x, local.z);
        }

        private float lastLedgeCueTime = -999f;
        private Vector3 lastLedgeCuePosition;
        private int ledgeCueCount;

        /// <summary>
        /// Manual ledge guard: removes the part of the move that walks off a drop deeper than a
        /// gap's hole depth, so a player feeling their way along a girder or catwalk slides along
        /// its edge instead of off it. Not on the planned run at a gap (the gap's own edge guard
        /// stops that one at the takeoff), and not where the path itself goes down. True when it
        /// changed the move.
        /// </summary>
        private bool StripDrops(Transform body, ref Vector3 world)
        {
            if (!playerController.thisController.isGrounded || gapJumpInProgress || playerController.isClimbingLadder ||
                playerController.inSpecialInteractAnimation || currentTargetNode == null)
                return false;
            if (currentTargetNode.position.y - NODE_GROUND_OFFSET < transform.position.y - GAP_HOLE_DEPTH + 0.5f)
                return false;
            if (hasGap && plan.valid && LinedUp(body) && DistanceToTakeoff() > -0.3f)
                return false;
            // A ledge to jump up onto: the jump cue handles it.
            if (manualProbe.kind == ObstacleProbe.Kind.Jumpable)
                return false;

            Vector3 move = world;
            move.y = 0f;
            if (move.sqrMagnitude < 0.0001f)
                return false;
            Vector3 dir = move.normalized;
            bool changed = false;
            bool straightAhead = false;
            foreach (float angle in EdgeProbeAngles)
            {
                Vector3 probe = Quaternion.AngleAxis(angle, Vector3.up) * dir;
                if (GroundAt(transform.position + probe * 0.5f, transform.position.y, out _))
                    continue;
                // Floor again within a stride: a crack, or the step to a ledge just past it.
                if (GroundAt(transform.position + probe * 1.0f, transform.position.y, out _))
                    continue;
                float into = Vector3.Dot(world, probe);
                if (into <= 0f)
                    continue;
                world -= probe * into;
                changed = true;
                // Only a drop straight ahead is worth saying; one off to the side (the open side
                // of a staircase, a catwalk's edge) is just slid along quietly.
                if (angle == 0f)
                    straightAhead = true;
            }
            if (straightAhead && Time.time - lastLedgeCueTime > 3f)
            {
                lastLedgeCueTime = Time.time;
                // Held at the same edge while the path leads over it: the path is wrong, not the
                // player. Cut the edge out and plan again rather than say "Ledge" for ever.
                if (HorizontalDistance(transform.position, lastLedgeCuePosition) < 1f)
                    ledgeCueCount++;
                else
                    ledgeCueCount = 1;
                lastLedgeCuePosition = transform.position;
                if (ledgeCueCount >= 3)
                {
                    ledgeCueCount = 0;
                    RememberBlockedSpot(dir);
                    Replan("Ledge. Finding another way.");
                }
                else
                {
                    Utilities.SpeakText("Ledge");
                }
            }
            return changed;
        }

        private void ResetGapState()
        {
            hasGap = false;
            plan.valid = false;
            gapJumpInProgress = false;
            gapPhase = GapPhase.Approach;
            gapAttempts = 0;
            jumpBuffered = false;
            manualEdgeGuard = false;
            manualAssist = Vector3.zero;
            manualSteer = Vector3.zero;
            gapCuedEdge = Vector3.positiveInfinity;
            if (gapGuideObject != null)
                gapGuideObject.SetActive(false);
        }

        private void CreateGapSounds()
        {
            gapTickClip = GenerateTone("GapTick", 1200f, 0.03f, 0.5f);
            gapNowClip = GenerateTone("GapNow", 2400f, 0.08f, 0.6f);
            gapBufferClip = GenerateTone("GapBuffered", 700f, 0.05f, 0.5f);
        }

        private static AudioClip GenerateTone(string name, float frequency, float seconds, float volume)
        {
            int sampleRate = 44100;
            int length = (int)(sampleRate * seconds);
            AudioClip clip = AudioClip.Create(name, length, 1, sampleRate, false);
            float[] samples = new float[length];
            for (int i = 0; i < length; i++)
            {
                float t = (float)i / sampleRate;
                float envelope = Mathf.Min(1f, i / (sampleRate * 0.003f)) * Mathf.Exp(-(float)i / length * 4f);
                samples[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * envelope * volume;
            }
            clip.SetData(samples, 0);
            return clip;
        }
        #endregion
    }

    /// <summary>
    /// Manual pathfinding's jump buffer: a jump pressed just before a gap's takeoff window is held
    /// and released at the takeoff point, since the game would otherwise jump at once and come up
    /// short.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(PlayerControllerB), "Jump_performed")]
    internal static class ManualJumpBufferPatch
    {
        [HarmonyLib.HarmonyPrefix]
        private static bool Prefix(PlayerControllerB __instance)
        {
            try
            {
                Pathfinder pathfinder = Pathfinder.Instance;
                if (pathfinder != null && pathfinder.playerController == __instance && pathfinder.ShouldBufferJump())
                    return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"ManualJumpBufferPatch: {ex.Message}");
            }
            return true;
        }
    }
}
