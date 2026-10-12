using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LethalAccess
{
    /// <summary>
    /// What stands between the player and the next path node, judged with the player's own
    /// capsule. The NavMesh is baked for enemies and ignores a lot of what a player collides
    /// with (crates, railings, pipes, low walls), so the path alone walks straight into them.
    /// </summary>
    internal class ObstacleProbe
    {
        private static readonly float[] LedgeInsets = { 0.05f, 0.3f };
        private static readonly float[] StandingRoomShifts = { 0.3f, 0.5f };
        internal enum Kind
        {
            Clear,
            Jumpable,
            Crouch,
            Wall
        }

        internal struct Result
        {
            public Kind kind;

            // For a wall: a free direction around it, or zero when there is none.
            public Vector3 aroundDirection;

            // For a wall: which side aroundDirection is on relative to the probed direction,
            // -1 left, +1 right, 0 none.
            public int side;

            // For a crouch: how far, and which way, to shift sideways onto the line that fits
            // under (a crawl space barely wider than the crouched player). Zero when already on it.
            public Vector3 laneOffset;
        }

        // Gravity in PlayerControllerB.Update: fallValue -= 38 * deltaTime.
        private const float PLAYER_GRAVITY = 38f;
        private const float WALKABLE_NORMAL_Y = 0.6f;
        // Up to 155 degrees: in a concave corner the only way round starts by backing off.
        private static readonly float[] AroundAngles = { 25f, 45f, 65f, 85f, 105f, 130f, 155f };

        // PlayerControllerB.Update lerps the controller to these. Casts always use the standing
        // height, so a crouched player is still told when they can stand again.
        private const float STAND_HEIGHT = 2.5f;
        private const float CROUCH_HEIGHT = 1.5f;

        private static readonly MethodInfo JumpMethod = AccessTools.Method(typeof(PlayerControllerB), "Jump_performed");

        private readonly PlayerControllerB player;
        private readonly int mask;
        private int stickySide = 1;

        // Once a side is chosen the probe keeps it this long. Sliding along a wall makes the
        // chosen side look blocked a moment later, and re-deciding every frame turned that into
        // a left-right shuffle on the spot.
        private const float SIDE_COMMIT_TIME = 1.5f;
        private float sideCommittedUntil;
        private readonly RaycastHit[] hits = new RaycastHit[8];

        public ObstacleProbe(PlayerControllerB player)
        {
            this.player = player;
            mask = BuildObstacleMask(player);
        }

        /// <summary>
        /// Everything the player's layer collides with except players, enemies and triggers.
        /// Railings and doors stay in: they are exactly what a player bumps into.
        /// </summary>
        private static int BuildObstacleMask(PlayerControllerB player)
        {
            int playerLayer = player != null ? player.gameObject.layer : LayerMask.NameToLayer("Player");
            int result = 0;
            for (int layer = 0; layer < 32; layer++)
            {
                if (playerLayer < 0 || !Physics.GetIgnoreLayerCollision(playerLayer, layer))
                {
                    result |= 1 << layer;
                }
            }
            if (playerLayer >= 0)
            {
                result &= ~(1 << playerLayer);
            }
            result &= ~LayerMask.GetMask("Ignore Raycast", "UI", "Triggers", "Enemies", "EnemiesNotRendered",
                "PlayerRagdoll", "ScanNode", "Props", "PhysicsObject");
            return result;
        }

        private CharacterController Controller => player != null ? player.thisController : null;

        /// <summary>
        /// The tallest ledge a jump clears, from the player's jump force and the game's gravity,
        /// with some margin. 0.93 of the 2.22 m peak (measured 2.26 m) is 2.07 m, enough for the
        /// factory's 2 m "Jump" links onto machines and ledges.
        /// </summary>
        public float MaxJumpHeight
        {
            get
            {
                float force = player != null ? player.jumpForce : 5f;
                return Mathf.Clamp(force * force / (2f * PLAYER_GRAVITY) * 0.93f, 0.3f, 2.2f);
            }
        }

        /// <summary>Seconds from leaving the ground until the feet come back down to rise metres
        /// above the takeoff height, counted from the moment the jump lifts off.</summary>
        public float AirTimeTo(float rise)
        {
            float force = player != null ? player.jumpForce : 5f;
            float disc = force * force - 2f * PLAYER_GRAVITY * rise;
            if (disc < 0f)
                return 0f;
            return (force + Mathf.Sqrt(disc)) / PLAYER_GRAVITY;
        }

        /// <summary>True while the mod itself presses jump, so the manual jump buffer lets it through.</summary>
        public static bool ModJumping { get; private set; }

        public bool IsGrounded => Controller != null && Controller.isGrounded;

        public bool CanJumpNow =>
            player != null && IsGrounded && !player.isExhausted && !player.isCrouching &&
            !player.inSpecialInteractAnimation && !player.isTypingChat;

        /// <summary>
        /// Presses jump through the game's own handler, so the jump sound, animation, stamina
        /// cost and network message all happen as for a key press. The handler does not read its
        /// context argument.
        /// </summary>
        public bool Jump()
        {
            if (JumpMethod == null || !CanJumpNow)
                return false;

            ModJumping = true;
            try
            {
                JumpMethod.Invoke(player, new object[] { default(InputAction.CallbackContext) });
            }
            finally
            {
                ModJumping = false;
            }
            return true;
        }

        private void GetCapsule(float lift, out Vector3 bottom, out Vector3 top, out float radius, out float feetY, float height = STAND_HEIGHT)
        {
            CharacterController cc = Controller;
            Transform t = player.transform;
            Vector3 controllerCenter = t.TransformPoint(cc.center);
            radius = cc.radius * 0.95f;
            feetY = controllerCenter.y - cc.height * 0.5f;
            Vector3 center = new Vector3(controllerCenter.x, feetY + height * 0.5f, controllerCenter.z);
            float half = Mathf.Max(height * 0.5f - cc.radius, 0.01f);

            bottom = center - Vector3.up * half;
            top = center + Vector3.up * half;

            // Lifting the bottom sphere makes the cast skip anything the controller steps over.
            bottom.y = Mathf.Min(bottom.y + lift, top.y);
        }

        private bool Blocked(Vector3 direction, float distance, float extraLift, out RaycastHit hit, float height = STAND_HEIGHT,
            Vector3 originOffset = default)
        {
            CharacterController cc = Controller;
            GetCapsule(cc.stepOffset + 0.05f, out Vector3 bottom, out Vector3 top, out float radius, out _, height);
            bottom += originOffset;
            top += originOffset;
            // The whole body raised: lifting only the bottom (capped at the top) left a tall
            // lift reaching back down into what it was meant to clear.
            if (extraLift > 0f)
            {
                bottom.y += extraLift;
                top.y += extraLift;
            }

            int count = Physics.CapsuleCastNonAlloc(bottom, top, radius, direction, hits, distance, mask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            hit = default;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                // Hits at distance 0 are colliders the capsule already overlaps (the floor edge
                // it stands on); they say nothing about what is ahead.
                if (hits[i].distance <= 0f || hits[i].normal.y > WALKABLE_NORMAL_Y)
                    continue;
                if (hits[i].distance < best)
                {
                    best = hits[i].distance;
                    hit = hits[i];
                    found = true;
                }
            }
            return found;
        }

        /// <param name="dropAhead">The path goes down past this point (off a catwalk, off a
        /// bridge). A railing there has nothing to land on behind it, and is still the way.</param>
        public Result Assess(Vector3 direction, float lookahead, bool dropAhead = false)
        {
            Result result = new Result { kind = Kind.Clear };
            if (player == null || Controller == null)
                return result;

            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f)
                return result;
            direction.Normalize();

            if (!Blocked(direction, lookahead, 0f, out RaycastHit hit))
                return result;

            // The cast keeps one height, so a flight of stairs reads as a wall (the third riser
            // up) or a low ceiling (the stairwell's roof on the way down). Following the floor
            // step by step shows the player just walks it.
            if (StairsAhead(direction, hit.distance))
                return result;

            // Brushing something side-on (a railing beside the path, the corner of a landing):
            // the controller slides along it, so it is not in the way.
            Vector3 face = -hit.normal;
            face.y = 0f;
            // Only a side face: the underside of a beam, met by the top of the capsule, has a
            // mostly downward normal and is very much in the way.
            if (Mathf.Abs(hit.normal.y) < 0.3f && face.sqrMagnitude > 0.0001f && Vector3.Dot(face.normalized, direction) < 0.5f)
                return result;

            // The underside of something overhead (the round belly of Experimentation's train car
            // tank, met by the top of the capsule) is never jumped: jumping only hits it harder.
            if (hit.normal.y > -0.3f && IsJumpable(direction, hit, lookahead, dropAhead))
            {
                result.kind = Kind.Jumpable;
                return result;
            }

            // Low beams and pipes: too tall to jump, open underneath at crouch height.
            if (!Blocked(direction, lookahead, 0f, out _, CROUCH_HEIGHT))
            {
                // Only the end of something low, with standing room a short step to the side
                // (a corner cut under the end of a stair railing): step round it, no crouch.
                if (StandingRoomBeside(direction, lookahead, out Vector3 side))
                {
                    result.kind = Kind.Wall;
                    result.aroundDirection = (side + direction * 0.5f).normalized;
                    return result;
                }
                result.kind = Kind.Crouch;
                return result;
            }

            // A crawl space only a little wider than the crouched player (under a server rack):
            // open at crouch height a few centimetres to one side.
            if (FindCrouchLane(direction, lookahead, out Vector3 lane))
            {
                result.kind = Kind.Crouch;
                result.laneOffset = lane;
                return result;
            }

            result.kind = Kind.Wall;
            FindWayAround(direction, lookahead, ref result);
            return result;
        }

        /// <summary>
        /// The floor ahead, out to just past distance, changes height only in steps the
        /// controller climbs or walks down by itself, and a standing player fits at its end. Flat
        /// floor is not stairs: an obstacle there is real.
        /// </summary>
        private bool StairsAhead(Vector3 direction, float distance)
        {
            CharacterController cc = Controller;
            GetCapsule(0f, out _, out _, out float radius, out float feetY);
            float step = cc.stepOffset + 0.05f;
            Vector3 origin = player.transform.position;
            float y = feetY;
            bool changed = false;
            float end = distance + radius + 0.2f;
            for (float d = 0f; d <= end; d += 0.15f)
            {
                Vector3 p = origin + direction * d;
                // From head height, the highest surface below it: a ray starting lower can begin
                // inside a ledge (a concrete bridge's end) and see only the ground under it.
                int count = Physics.RaycastNonAlloc(new Vector3(p.x, y + 1.9f, p.z), Vector3.down, hits,
                    1.9f + 0.6f, mask, QueryTriggerInteraction.Ignore);
                RaycastHit floor = default;
                bool found = false;
                for (int i = 0; i < count; i++)
                {
                    if (!found || hits[i].point.y > floor.point.y)
                    {
                        floor = hits[i];
                        found = true;
                    }
                }
                if (!found)
                    return false;
                if (floor.normal.y < WALKABLE_NORMAL_Y)
                    return false;
                float dy = floor.point.y - y;
                if (dy > step)
                    return false;
                if (Mathf.Abs(dy) > 0.05f)
                    changed = true;
                // The controller moves across first and settles down after, so going down the
                // head is still at the higher of the two floors when it gets here.
                float headFloor = Mathf.Max(y, floor.point.y);
                y = floor.point.y;

                // Head room all the way, not just at the end: a beam over the stairs is real.
                // Only from crouch height up, and slimmer than the player: lower down a steep
                // flight's own ramp and the railings beside the path touch the full capsule.
                const float headRadius = 0.25f;
                Vector3 bottom = new Vector3(p.x, y + CROUCH_HEIGHT + headRadius, p.z);
                Vector3 top = new Vector3(p.x, y + STAND_HEIGHT - headRadius, p.z);
                if (Physics.CheckCapsule(bottom, top, headRadius, mask, QueryTriggerInteraction.Ignore))
                    return false;
                // The head itself is full width: an edge overhead (a catwalk over the bottom of
                // a slope) stops a standing player even where the floor ahead drops away.
                if (Physics.CheckSphere(new Vector3(p.x, headFloor + STAND_HEIGHT - radius, p.z), radius, mask, QueryTriggerInteraction.Ignore))
                    return false;
            }
            return changed;
        }

        /// <summary>
        /// A standing player a step further along direction would be pressed against something
        /// above crouch height, and a crouched one would not: an overhang the player has walked
        /// their head into, which the forward casts miss since they skip what they already touch.
        /// </summary>
        public bool HeadAgainstOverhang(Vector3 direction)
        {
            if (player == null || Controller == null)
                return false;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f)
                return false;
            Vector3 shift = direction.normalized * 0.3f;
            GetCapsule(Controller.stepOffset + 0.05f, out Vector3 bottom, out Vector3 top, out float radius, out _);
            if (!Physics.CheckCapsule(bottom + shift, top + shift, radius, mask, QueryTriggerInteraction.Ignore))
                return false;
            GetCapsule(Controller.stepOffset + 0.05f, out bottom, out top, out radius, out _, CROUCH_HEIGHT);
            return !Physics.CheckCapsule(bottom + shift, top + shift, radius, mask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// A standing player fits here and along direction for distance. The casts in Assess skip
        /// whatever the capsule already overlaps, so with the head already under an overhang they
        /// read clear; this checks the overlap too.
        /// </summary>
        public bool RoomToStand(Vector3 direction, float distance)
        {
            if (player == null || Controller == null)
                return true;
            CharacterController cc = Controller;
            GetCapsule(cc.stepOffset + 0.05f, out Vector3 bottom, out Vector3 top, out float radius, out _);
            if (Physics.CheckCapsule(bottom, top, radius, mask, QueryTriggerInteraction.Ignore))
                return false;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f)
                return true;
            return !Blocked(direction.normalized, distance, 0f, out _);
        }

        /// <summary>Standing room ahead from 0.3 or 0.5 m to either side, reachable by stepping across.</summary>
        private bool StandingRoomBeside(Vector3 direction, float lookahead, out Vector3 side)
        {
            Vector3 perp = Vector3.Cross(Vector3.up, direction).normalized;
            foreach (float shift in StandingRoomShifts)
            {
                foreach (int s in new[] { 1, -1 })
                {
                    Vector3 offset = perp * (s * shift);
                    if (Blocked(perp * s, shift, 0f, out _) || Blocked(direction, lookahead, 0f, out _, STAND_HEIGHT, offset))
                        continue;
                    side = perp * s;
                    return true;
                }
            }
            side = Vector3.zero;
            return false;
        }

        // Sideways search for a crouch lane, in 4 cm steps out to 0.6 m either side.
        private const float LANE_STEP = 0.04f;
        private const int LANE_STEPS = 15;

        /// <summary>
        /// The nearest sideways shift from which the way ahead is open at crouch height (and
        /// still closed standing, so it is the low gap and not a way round), reachable by
        /// shifting crouched. False when there is none within 0.6 m.
        /// </summary>
        private bool FindCrouchLane(Vector3 direction, float lookahead, out Vector3 lane)
        {
            lane = Vector3.zero;
            Vector3 perp = Vector3.Cross(Vector3.up, direction).normalized;
            for (int k = 1; k <= LANE_STEPS; k++)
            {
                foreach (int side in new[] { 1, -1 })
                {
                    Vector3 offset = perp * (side * k * LANE_STEP);
                    if (Blocked(direction, lookahead, 0f, out _, CROUCH_HEIGHT, offset))
                        continue;
                    if (!Blocked(direction, lookahead, 0f, out _, STAND_HEIGHT, offset))
                        continue;
                    if (Blocked(perp * side, k * LANE_STEP, 0f, out _, CROUCH_HEIGHT))
                        continue;
                    lane = offset;
                    return true;
                }
            }
            return false;
        }

        private bool IsJumpable(Vector3 direction, RaycastHit hit, float lookahead, bool dropAhead)
        {
            GetCapsule(0f, out _, out Vector3 top, out _, out float feetY);
            // What a jump gets over: its peak, plus most of the controller's step offset, which
            // still lifts a body that meets an edge on the way up.
            float maxJump = MaxJumpHeight + Controller.stepOffset * 0.75f;

            // Top of the obstacle where the capsule touched it, then a little past it.
            float rise = float.MaxValue;
            foreach (float inset in LedgeInsets)
            {
                Vector3 probe = hit.point + direction * inset;
                probe.y = feetY + maxJump + 0.3f;
                if (Physics.Raycast(probe, Vector3.down, out RaycastHit topHit, maxJump + 0.3f, mask, QueryTriggerInteraction.Ignore) &&
                    topHit.normal.y >= WALKABLE_NORMAL_Y)
                {
                    // Room to stand: the top of a 0.2 m catwalk railing is not a ledge.
                    Vector3 further = probe + direction * 0.45f;
                    if (Physics.Raycast(further, Vector3.down, out RaycastHit furtherHit, maxJump + 0.3f, mask, QueryTriggerInteraction.Ignore))
                    {
                        if (Mathf.Abs(furtherHit.point.y - topHit.point.y) < 0.3f)
                        {
                            rise = topHit.point.y - feetY;
                        }
                        else if (furtherHit.point.y > topHit.point.y)
                        {
                            // A railing standing at the ledge's edge (a raised walkway fenced
                            // across its end): the jump has to clear the railing, and land on
                            // the ledge past it.
                            Vector3 past = further + direction * 0.5f;
                            if (Physics.Raycast(past, Vector3.down, out RaycastHit pastHit, maxJump + 0.3f, mask, QueryTriggerInteraction.Ignore) &&
                                pastHit.normal.y >= WALKABLE_NORMAL_Y && Mathf.Abs(pastHit.point.y - topHit.point.y) < 0.3f)
                            {
                                rise = furtherHit.point.y - feetY;
                            }
                        }
                    }
                    break;
                }
            }

            // A thin railing with a drop behind it: no surface to find on top, so its height
            // comes from its bounds.
            // Only with floor to come down on within a few metres: over a factory shaft there is
            // none, and jumping the railing is jumping into the pit.
            if (rise == float.MaxValue && dropAhead && hit.collider != null)
            {
                Vector3 beyond = hit.point + direction * 0.8f;
                beyond.y = feetY + 0.5f;
                if (Physics.Raycast(beyond, Vector3.down, 4f, mask, QueryTriggerInteraction.Ignore))
                {
                    rise = hit.collider.bounds.max.y - feetY;
                }
            }

            // A thin railing with floor at about this height straight behind it (standing on
            // the lip in front of a walkway's end rail): hop it.
            if (rise == float.MaxValue && hit.collider != null)
            {
                float railTop = hit.collider.bounds.max.y;
                Vector3 beyond = hit.point + direction * 0.8f;
                beyond.y = railTop + 0.2f;
                if (railTop - feetY > 0.3f &&
                    Physics.Raycast(beyond, Vector3.down, out RaycastHit behind, railTop - feetY + 1.2f, mask, QueryTriggerInteraction.Ignore) &&
                    behind.normal.y >= WALKABLE_NORMAL_Y && Mathf.Abs(behind.point.y - feetY) < 0.6f)
                {
                    rise = railTop - feetY;
                }
            }

            if (rise > maxJump)
                return false;

            // Room for the head on the way up.
            float headTop = top.y + Controller.radius;
            if (Physics.Raycast(new Vector3(top.x, headTop, top.z), Vector3.up, rise + 0.1f, mask, QueryTriggerInteraction.Ignore))
                return false;

            // And for the whole body once at the obstacle's height.
            if (!Blocked(direction, lookahead + 0.4f, rise, out RaycastHit onTop))
                return true;

            // Something standing on the ledge (a railing across a walkway's end): the jump
            // has to clear its top instead, if that is still within reach.
            float over = onTop.collider != null ? onTop.collider.bounds.max.y - feetY : float.MaxValue;
            if (over <= rise || over > maxJump)
                return false;
            if (Physics.Raycast(new Vector3(top.x, headTop, top.z), Vector3.up, over + 0.1f, mask, QueryTriggerInteraction.Ignore))
                return false;
            return !Blocked(direction, lookahead + 0.4f, over, out _);
        }

        private void FindWayAround(Vector3 direction, float lookahead, ref Result result)
        {
            // Committed: the same side at any angle first, the other side only if this one is
            // closed all the way round.
            if (Time.time < sideCommittedUntil && TrySide(direction, lookahead, stickySide, ref result))
                return;

            int[] order = { stickySide, -stickySide };
            foreach (float angle in AroundAngles)
            {
                foreach (int side in order)
                {
                    if (TryAngle(direction, lookahead, angle, side, ref result))
                    {
                        if (side != stickySide || Time.time >= sideCommittedUntil)
                        {
                            sideCommittedUntil = Time.time + SIDE_COMMIT_TIME;
                        }
                        stickySide = side;
                        return;
                    }
                }
            }
        }

        private bool TrySide(Vector3 direction, float lookahead, int side, ref Result result)
        {
            foreach (float angle in AroundAngles)
            {
                if (TryAngle(direction, lookahead, angle, side, ref result))
                    return true;
            }
            return false;
        }

        private bool TryAngle(Vector3 direction, float lookahead, float angle, int side, ref Result result)
        {
            Vector3 candidate = Quaternion.AngleAxis(angle * side, Vector3.up) * direction;
            if (Blocked(candidate, lookahead, 0f, out _) || !HasGround(candidate, lookahead))
                return false;

            result.aroundDirection = candidate;
            result.side = side;
            return true;
        }

        /// <summary>
        /// True when the NavMesh between the player and target is not one connected surface and
        /// the target is higher than a jump: an off-mesh link enemies climb and players cannot.
        /// </summary>
        public bool IsClimbLink(Vector3 target)
        {
            if (player == null || Controller == null)
                return false;

            // The same reach IsJumpable allows: a jump plus the controller's step at the top.
            GetCapsule(0f, out _, out _, out _, out float feetY);
            if (target.y - feetY <= MaxJumpHeight + Controller.stepOffset * 0.75f)
                return false;

            return IsDisconnected(target);
        }

        /// <summary>No walkable NavMesh joins the player's spot to target: the step between them is
        /// an off-mesh link (a ladder, a ledge), not a slope or stairs.</summary>
        public bool IsDisconnected(Vector3 target)
        {
            if (player == null)
                return false;

            UnityEngine.AI.NavMeshHit from, to;
            if (!UnityEngine.AI.NavMesh.SamplePosition(player.transform.position, out from, 1.5f, UnityEngine.AI.NavMesh.AllAreas) ||
                !UnityEngine.AI.NavMesh.SamplePosition(target, out to, 1.5f, UnityEngine.AI.NavMesh.AllAreas))
                return false;

            // Not NavMesh.Raycast: it is broken by any turn (a switchback staircase) and passes
            // straight up between two levels the mesh overlaps (the ship's side ladder). A short
            // walk, leaving out the Jump, Climb, ship climb and ladder link areas that are the
            // actual off-mesh steps, says whether the two are really joined.
            int walkMask = UnityEngine.AI.NavMesh.AllAreas & ~((1 << 2) | (1 << 7) | (1 << Pathfinder.LADDER_LINK_AREA) | (1 << Pathfinder.PLAYER_JUMP_LINK_AREA));
            UnityEngine.AI.NavMeshPath walk = new UnityEngine.AI.NavMeshPath();
            if (!Pathfinder.CalculatePlayerPath(from.position, to.position, walkMask, walk) ||
                walk.status != UnityEngine.AI.NavMeshPathStatus.PathComplete)
                return true;
            float length = 0f;
            for (int i = 1; i < walk.corners.Length; i++)
                length += Vector3.Distance(walk.corners[i - 1], walk.corners[i]);
            return length > Vector3.Distance(from.position, to.position) * 3f + 6f;
        }

        /// <summary>Somewhere to stand in that direction, so going around never means walking off a ledge.</summary>
        private bool HasGround(Vector3 direction, float distance)
        {
            GetCapsule(0f, out _, out _, out _, out float feetY);
            Vector3 origin = player.transform.position + direction * distance;
            origin.y = feetY + 0.5f;
            return Physics.Raycast(origin, Vector3.down, 1.6f, mask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>Lets a new obstacle pick its own side instead of inheriting the last one.</summary>
        public void ResetSide(int preferred)
        {
            stickySide = preferred >= 0 ? 1 : -1;
            sideCommittedUntil = Time.time + SIDE_COMMIT_TIME;
        }
    }
}
