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
            if (!plan.valid || (plan.forEdge - gap.edge).sqrMagnitude > 0.75f * 0.75f)
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
                    if (width < GAP_MIN_WIDTH)
                        continue; // A crack a stride crosses.

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
            RaycastHit hit;
            if (!Physics.Raycast(new Vector3(point.x, refY + 2.3f, point.z), Vector3.down, out hit,
                    2.3f + GAP_HOLE_DEPTH, groundMask, QueryTriggerInteraction.Ignore))
                return false;
            groundY = hit.point.y;
            return true;
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
        private int BlockerMask => groundMask | LayerMask.GetMask("Railing");

        private static readonly float[] TakeoffOffsets = { 0f, -0.25f, 0.25f, -0.5f, 0.5f, -0.75f, 0.75f, -1f, 1f, -1.25f, 1.25f, -1.5f, 1.5f };

        /// <summary>Tries jump lines and speeds in simulation and keeps the most comfortable one
        /// that lands with room to spare. See the class summary.</summary>
        private JumpPlan PlanJump(Gap g)
        {
            JumpPlan best = new JumpPlan { valid = false, forEdge = g.edge };
            float bestCost = float.MaxValue;

            Vector3 across = g.landing - g.edge;
            across.y = 0f;
            if (across.sqrMagnitude < 0.0001f)
                return best;
            across.Normalize();
            Vector3 perpAcross = Vector3.Cross(Vector3.up, across);

            var directions = new List<Vector3>();
            foreach (float angle in new[] { 0f, -10f, 10f, -20f, 20f, -30f, 30f, -40f, 40f })
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

            float walk = walkSpeedEstimate;
            float maxRun = walk * SPRINT_MULTIPLIER * 0.92f;
            // Partial walks for short hops onto narrow landings (the game walks at the move
            // input's size; under about 0.45 it stops playing the walk animation), a full walk,
            // then sprints.
            var speeds = new List<float> { walk * 0.6f, walk * 0.8f, walk };
            for (float s = walk + 1f; s <= maxRun; s += 1f)
            {
                speeds.Add(s);
            }

            PlanPass(g, across, perpAcross, directions, speeds, walk, steering: false, ref best, ref bestCost);
            // None straight: run up one line and steer in the air. A girder angled to the gap
            // gives a long run-up only along itself, and its own line can end in a railing.
            if (!best.valid)
                PlanPass(g, across, perpAcross, directions, speeds, walk, steering: true, ref best, ref bestCost);
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
                    foreach (float turn in new[] { 10f, -10f, 20f, -20f, 30f, -30f, 45f, -45f })
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
                    if (!FindTakeoff(g, g.edge + perpAcross * offset, dir, out Vector3 takeoff, out float runUpAvailable))
                        continue;

                    foreach (float speed in speeds)
                    {
                        bool sprint = speed > walk + 0.05f;
                        // A walk holds its speed exactly; a sprint only roughly.
                        float low = sprint ? SPEED_TOLERANCE_LOW : 0.25f;
                        float high = sprint ? SPEED_TOLERANCE_HIGH : 0.25f;
                        float runUp = sprint ? RunUpDistance(speed) : 1.0f;
                        if (runUpAvailable < runUp)
                            break; // Faster ones need even more.

                        float magnitude = sprint ? 1f : Mathf.Clamp01(speed / walk);
                        foreach (Vector3 steer in steers)
                        {
                            float cost = angleCost + Mathf.Abs(offset) * 1.5f + (sprint ? 1f + (speed - walk) * 0.4f : (walk - speed) * 0.3f) +
                                         Vector3.Angle(dir, steer) / 30f;
                            if (cost >= bestCost)
                                continue;

                            if (!SimulateJump(takeoff, dir, steer, speed, sprint, magnitude, g, out Vector3 landing) ||
                                !SimulateJump(takeoff, dir, steer, speed - low, sprint, sprint ? 1f : magnitude * (speed - low) / speed, g, out _) ||
                                !SimulateJump(takeoff, dir, steer, speed + high, sprint, sprint ? 1f : magnitude * (speed + high) / speed, g, out _))
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
                                forEdge = g.edge
                            };
                        }
                    }
                }
            }
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
                    floor.normal.y > 0.6f)
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

        /// <summary>The start of the run-up: as far back from the takeoff as the plan needs.</summary>
        private Vector3 RunUpStart() => plan.takeoff - plan.direction * plan.runUp;

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
            if (gapPhase == GapPhase.Approach && toTakeoff > plan.runUp + 2.5f)
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
                float low = plan.sprint ? SPEED_TOLERANCE_LOW : 0.25f;
                float high = plan.sprint ? SPEED_TOLERANCE_HIGH : 0.25f;
                bool onSpeed = speed >= plan.speed - low + 0.05f && speed <= plan.speed + high - 0.05f;
                if (onSpeed && lateral < LINE_TOLERANCE + 0.1f && Probe != null && Probe.CanJumpNow)
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
            Transform body = playerController.thisPlayerBody != null ? playerController.thisPlayerBody : transform;

            if (gapJumpInProgress)
            {
                // Aim assist in the air: drift back toward the jump line.
                if (!playerController.thisController.isGrounded)
                {
                    Vector3 offset = ChordOffset(jumpingPlan);
                    Vector3 correction = -offset.normalized * Mathf.Min(offset.magnitude, 1.5f * Time.deltaTime);
                    playerController.thisController.Move(correction);
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
                                    (plan.sprint ? "Sprint, and jump at the high tone." : "Walk, and jump at the high tone."));
            }

            Vector3 v = playerController.thisController.velocity;
            v.y = 0f;
            bool headingIn = v.magnitude > 0.5f && Vector3.Dot(v.normalized, plan.direction) > 0.5f;
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
                if (!headingIn || toTakeoff > lead + 2.5f)
                {
                    jumpBuffered = false;
                }
                else if (toTakeoff <= lead)
                {
                    StartGapJump();
                    return;
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
            Vector3 v = playerController.thisController.velocity;
            v.y = 0f;
            bool headingIn = v.magnitude > 0.5f && Vector3.Dot(v.normalized, plan.direction) > 0.5f;
            if (!headingIn || toTakeoff <= lead || toTakeoff > lead + 2f)
                return false;

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
            if (!manualEdgeGuard || currentMode != PathfindingMode.Manual || !isPathfinding || !plan.valid)
                return input;

            Transform body = playerController.thisPlayerBody != null ? playerController.thisPlayerBody : transform;
            Vector3 world = body.right * input.x + body.forward * input.y;
            float into = Vector3.Dot(world, plan.direction);
            if (into <= 0f)
                return input;
            world -= plan.direction * into;
            Vector3 local = body.InverseTransformDirection(world);
            return new Vector2(local.x, local.z);
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
            gapCuedEdge = Vector3.positiveInfinity;
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
