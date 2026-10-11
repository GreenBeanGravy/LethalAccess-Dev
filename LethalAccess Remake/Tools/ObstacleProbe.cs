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
        /// with some margin.
        /// </summary>
        public float MaxJumpHeight
        {
            get
            {
                float force = player != null ? player.jumpForce : 5f;
                return Mathf.Clamp(force * force / (2f * PLAYER_GRAVITY) * 0.85f, 0.3f, 2f);
            }
        }

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

            JumpMethod.Invoke(player, new object[] { default(InputAction.CallbackContext) });
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

        private bool Blocked(Vector3 direction, float distance, float extraLift, out RaycastHit hit, float height = STAND_HEIGHT)
        {
            CharacterController cc = Controller;
            GetCapsule(cc.stepOffset + 0.05f + extraLift, out Vector3 bottom, out Vector3 top, out float radius, out _, height);
            if (extraLift > 0f)
            {
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

            if (IsJumpable(direction, hit, lookahead, dropAhead))
            {
                result.kind = Kind.Jumpable;
                return result;
            }

            // Low beams and pipes: too tall to jump, open underneath at crouch height.
            if (!Blocked(direction, lookahead, 0f, out _, CROUCH_HEIGHT))
            {
                result.kind = Kind.Crouch;
                return result;
            }

            result.kind = Kind.Wall;
            FindWayAround(direction, lookahead, ref result);
            return result;
        }

        private bool IsJumpable(Vector3 direction, RaycastHit hit, float lookahead, bool dropAhead)
        {
            GetCapsule(0f, out _, out Vector3 top, out _, out float feetY);
            float maxJump = MaxJumpHeight;

            // Top of the obstacle where the capsule touched it, then a little past it.
            float rise = float.MaxValue;
            foreach (float inset in new[] { 0.05f, 0.3f })
            {
                Vector3 probe = hit.point + direction * inset;
                probe.y = feetY + maxJump + 0.3f;
                if (Physics.Raycast(probe, Vector3.down, out RaycastHit topHit, maxJump + 0.3f, mask, QueryTriggerInteraction.Ignore) &&
                    topHit.normal.y >= WALKABLE_NORMAL_Y)
                {
                    rise = topHit.point.y - feetY;
                    break;
                }
            }

            // A thin railing with a drop behind it: no surface to find on top, so its height
            // comes from its bounds.
            if (rise == float.MaxValue && dropAhead && hit.collider != null)
            {
                rise = hit.collider.bounds.max.y - feetY;
            }

            if (rise > maxJump)
                return false;

            // Room for the head on the way up.
            float headTop = top.y + Controller.radius;
            if (Physics.Raycast(new Vector3(top.x, headTop, top.z), Vector3.up, rise + 0.1f, mask, QueryTriggerInteraction.Ignore))
                return false;

            // And for the whole body once at the obstacle's height.
            return !Blocked(direction, lookahead + 0.4f, rise, out _);
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

            GetCapsule(0f, out _, out _, out _, out float feetY);
            if (target.y - feetY <= MaxJumpHeight)
                return false;

            UnityEngine.AI.NavMeshHit from, to, edge;
            if (!UnityEngine.AI.NavMesh.SamplePosition(player.transform.position, out from, 1.5f, UnityEngine.AI.NavMesh.AllAreas) ||
                !UnityEngine.AI.NavMesh.SamplePosition(target, out to, 1.5f, UnityEngine.AI.NavMesh.AllAreas))
                return false;

            return UnityEngine.AI.NavMesh.Raycast(from.position, to.position, out edge, UnityEngine.AI.NavMesh.AllAreas);
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
