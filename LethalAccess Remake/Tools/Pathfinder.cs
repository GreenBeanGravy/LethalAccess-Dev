using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Networking;

namespace LethalAccess
{
    /// <summary>
    /// Guides the player to a target, in manual mode (audio pings on path nodes) or auto mode
    /// (the player walks the path by itself), with a drawn path. Destinations off the NavMesh are
    /// handled too.
    ///
    /// The path is computed with the static NavMesh.CalculatePath; nothing is ever added to the
    /// player object. The previous version put a NavMeshAgent on the player: it took a 0.3 s
    /// settle delay before every pathfind, snapped the player onto the mesh when it was created,
    /// and in auto mode moved the transform itself at 6 m/s while PlayerControllerB kept moving
    /// the CharacterController from the keyboard. That was the slow start, the floating, the
    /// missing animations and the double speed.
    ///
    /// Auto mode now drives the player the way a person does: it turns the body toward the next
    /// node and feeds the game's own Move and Sprint inputs (AutoWalkInputPatch). Gravity, steps,
    /// walk and sprint animations, footsteps, stamina and network sync all come from the game.
    /// </summary>
    internal partial class Pathfinder : MonoBehaviour
    {
        private static readonly float[] LegWidenOffsets = { 0f, 0.3f, -0.3f, 0.5f, -0.5f, 0.7f, -0.7f, 0.9f, -0.9f, 1.2f, -1.2f };
        private static readonly float[] NodeMoveRadii = { 0.2f, 0.35f, 0.5f, 0.65f, 0.8f };
        private static readonly float[] NodeBodyHeights = { 0.6f, 1.05f };
        private static readonly float[] WalkLineHeights = { 0.6f, 1.2f };
        private static readonly float[] SideOffsets = { 0.6f, -0.6f };
        private static readonly float[] OvershootFractions = { 0.75f, 0.5f, 0.25f, 0f };
        private static readonly float[] BodyLineHeights = { 0.8f, 1.5f };
        private static readonly float[] SmoothFractions = { 1f, 0.66f, 0.33f };
        #region Types
        private enum PathfindingMode
        {
            Manual,
            Auto
        }

        private class PathNode
        {
            public Vector3 position;
            public GameObject nodeObject;
            public AudioSource audioSource;
            public bool isReached;
            public int index;

            public PathNode(Vector3 pos, int idx)
            {
                position = pos;
                index = idx;
            }
        }
        #endregion

        #region Constants
        // NavMesh path corners sit only where the path turns. On a moon the straight line between
        // two corners runs through every hill in between, so nodes are found by walking the mesh
        // surface in short steps and carrying the height along, never by interpolating corners.
        private const float SURFACE_STEP = 0.5f;
        private const float NODE_SPACING = 2f;
        private const float CORNER_NODE_MIN_GAP = 0.75f;

        // The NavMesh is voxelised and can sit a little under the colliders the player walks on.
        // The ground ray starts this far above the mesh point; a player can stand there, so there
        // is always more headroom than this above the real floor.
        private const float GROUND_RAY_UP = 1.8f;
        private const float GROUND_RAY_LENGTH = 4f;

        // Node spheres have a 0.4 m radius; this puts them on top of the ground.
        private const float NODE_RADIUS = 0.4f;
        private const float NODE_GROUND_OFFSET = NODE_RADIUS;

        // Intermediate nodes count as reached at this horizontal distance. The last one uses the
        // configured stopping radius.
        private const float NODE_REACH_RADIUS = 1.25f;
        // Less than a flight of stairs: on switchback stairs the next flight down passes under
        // the one above, and a looser limit reached nodes (and the destination) through the floor.
        private const float NODE_REACH_HEIGHT = 1.4f;

        private const float NODE_AUDIO_PING_INTERVAL = 0.5f;

        // Re-plan from the current position when the player is this far from the node they
        // should be heading to (wandered off in manual mode, pushed or fell in auto mode).
        private const float OFF_PATH_DISTANCE = 8f;
        private const float REPLAN_COOLDOWN = 3f;

        // Auto mode faces along the path and strafes: the body turns toward the next node at
        // this rate while the move input points wherever the player actually needs to go, so
        // stepping around a crate never needs a full turn. It only stops to turn when the node
        // is behind.
        private const float AUTO_TURN_SPEED = 360f;
        private const float AUTO_MIN_CROUCH_TIME = 0.5f;
        private const float AUTO_STAND_CLEAR_TIME = 0.25f;
        private float autoCrouchTime = -999f;
        private float standClearSince = -1f;
        private const float AUTO_TURN_IN_PLACE_ANGLE = 120f;
        private const float AUTO_SPRINT_MAX_ANGLE = 35f;
        private const float STUCK_CHECK_INTERVAL = 1.5f;
        private const float STUCK_MIN_PROGRESS = 0.4f;

        // How far ahead obstacles are looked for, plus a bit more per m/s of speed.
        private const float PROBE_BASE_DISTANCE = 0.7f;
        private const float PROBE_SPEED_FACTOR = 0.15f;
        private const float JUMP_COOLDOWN = 0.9f;
        private const float AROUND_HOLD_TIME = 0.5f;

        // Path nodes are pushed at least this far from the NavMesh edge, which is where walls,
        // railings and drops are.
        private const float WALL_CLEARANCE = 0.6f;

        // Manual mode cues.
        private const float MANUAL_JUMP_CUE_DISTANCE = 1.4f;
        private const float MANUAL_JUMP_CUE_COOLDOWN = 2.5f;
        private const float MANUAL_BLOCKED_CUE_COOLDOWN = 4f;

        private const float DOOR_DETECTION_DISTANCE = 2.25f;
        private const float DOOR_CACHE_REFRESH_INTERVAL = 2f;
        private const int INITIAL_POOL_SIZE = 50;

        private const string ON_TARGET_SOUND_PATH = "LethalAccessAssets/On Target.wav";
        private const string ALMOST_ON_TARGET_SOUND_PATH = "LethalAccessAssets/Almost On Target.wav";
        private const string OFF_TARGET_SOUND_PATH = "LethalAccessAssets/Off Target.wav";
        #endregion

        #region Fields
        public bool isPathfinding = false;
        public float stoppingRadius = 1.4f;
        public static bool ShouldPreventFallDamage = false;
        public PlayerControllerB playerController;

        public bool IsPathfinding => isPathfinding;
        public static Pathfinder Instance { get; private set; }

        // Read by AutoWalkInputPatch from inside PlayerControllerB.Update.
        public static bool AutoWalkActive => Instance != null && Instance.isPathfinding && Instance.currentMode == PathfindingMode.Auto;
        public static Vector2 AutoMoveInput { get; private set; }
        public static bool AutoSprintInput { get; private set; }

        private PathfindingMode currentMode = PathfindingMode.Manual;
        private GameObject currentTargetObject;
        private Vector3 currentDestination;
        private bool isOffNavMeshTarget;
        private Vector3 offNavMeshTargetPos;

        private readonly List<PathNode> pathNodes = new List<PathNode>();
        private PathNode currentTargetNode;
        private float nodeAudioPingTimer;
        private bool hasAnnouncedDestinationReached;
        private float lastReplanTime = -999f;

        private Vector3 stuckCheckPosition;
        private float stuckCheckTime;
        private int stuckCount;

        private ObstacleProbe probe;
        private bool autoCrouched;
        private object stuckNode;
        private Vector3 stuckNodePosition;
        private int stuckIdleChecks;
        private float stuckBestDistance;
        private float nextJumpTime;
        private Vector3 aroundDirection;
        private float aroundUntil;
        private float probeTimer;
        private ObstacleProbe.Result manualProbe;
        private float lastJumpCueTime = -999f;
        private float lastBlockedCueTime = -999f;
        private Vector3 manualStuckPosition;
        private float manualStuckTime;
        private object climbCueNode;
        private InteractTrigger manualLadder;
        private bool manualCrouchCued;
        private bool manualWasCrouching;
        private float manualCrouchedAt;
        private bool pathIsPartial;

        // Ladders. The NavMesh crosses them with off-mesh links; a player crosses them by using
        // the ladder trigger and climbing.
        private InteractTrigger[] cachedLadders;
        private float cachedLaddersTime = -999f;
        private float ladderDirection = 1f;
        private float nextLadderTry;
        // Presses on one ladder that didn't start a climb. Each press resets the stuck check, so
        // a ladder that never takes the player (the ship's side ladder, once) stood them at its
        // top in silence for minutes.
        private InteractTrigger ladderTriedOn;
        private int ladderTries;
        private const float LADDER_SEARCH_RADIUS = 6f;
        private const float LADDER_USE_DISTANCE = 1.2f;

        // A drop bigger than this with a ladder down it is climbed instead of jumped. The game
        // only hurts a fall of about 18 m, but a player drops a step, not a storey.
        private const float LADDER_DESCENT_MIN_DROP = 3f;
        private bool manualLadderDown;
        private InteractTrigger activeLadder;
        private float ladderStallY;
        private float ladderStallTime;

        // Locked doors carve the NavMesh, so no plan goes through one. With a key in the
        // inventory the planner may route through a single locked door when that saves this much
        // walking (or is the only way), and the walk stops there until the player unlocks it.
        private const float KEY_ROUTE_MIN_SAVING = 15f;
        private const float LOCKED_DOOR_STOP_DISTANCE = 1.6f;
        private DoorLock lockedDoorOnPath;
        private int lockedDoorCrossIndex = -1;
        private bool lockedDoorCued;

        // Auto mode steers at the furthest node it can walk to in a straight line, up to this far
        // ahead plus a bit per m/s, instead of turning to face each node in turn.
        // Far enough to cut straight across to a node several ahead whenever nothing is in the
        // way, which is what skips nodes and saves time.
        private const float AIM_LOOKAHEAD_BASE = 3f;
        private const float AIM_LOOKAHEAD_PER_SPEED = 1f;
        private const float AIM_LOOKAHEAD_MAX = 10f;
        private const float AIM_MAX_RISE_PER_METER = 0.6f;

        private int groundMask;
        private int bodyMask;
        private readonly RaycastHit[] groundHitCache = new RaycastHit[16];

        private LineRenderer lineRenderer;
        private AudioSource audioSource;
        private Mesh nodeSphereMesh;
        private Material nodeBaseMaterial;
        private readonly Queue<GameObject> nodeObjectPool = new Queue<GameObject>();
        private AudioClip clickSound;

        private AudioClip onTargetSound;
        private AudioClip almostOnTargetSound;
        private AudioClip offTargetSound;

        // FindObjectsOfType<DoorLock>() walks every loaded component of that type and allocates a
        // new array each call. Doors are created with the dungeon and do not appear mid-round, so
        // the set is cached and refreshed on an interval.
        private DoorLock doorBeingOpened;
        private float doorHoldTime;
        private float doorWaitTime;
        private InteractTrigger cannotClimbCueLadder;
        private DoorLock[] cachedDoors;
        private float cachedDoorsTime = -999f;
        #endregion

        #region Initialization
        private void Awake()
        {
            try
            {
                Instance = this;
                playerController = GetComponent<PlayerControllerB>();

                stoppingRadius = ConfigManager.PathfindingStoppingRadius.Value;
                groundMask = BuildGroundMask();
                bodyMask = BuildBodyMask();

                audioSource = gameObject.AddComponent<AudioSource>();
                AudioSystemBypass.ConfigureAudioSourceForBypass(audioSource);

                lineRenderer = GetComponent<LineRenderer>();
                if (lineRenderer == null)
                {
                    lineRenderer = gameObject.AddComponent<LineRenderer>();
                }
                SetupLineRenderer();

                nodeSphereMesh = CreateSphereMesh(NODE_RADIUS);
                nodeBaseMaterial = new Material(Shader.Find("Sprites/Default")) { color = Color.cyan };
                clickSound = GenerateClickSound();
                CreateGapSounds();
                StartCoroutine(LoadAudioResources());
                InitializeNodePool();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in Pathfinder.Awake: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// The layers the player's own collider stands on, from the physics collision matrix, so a
        /// node lands where the player's feet would. The old mask listed "Terrain", "Default",
        /// "Room" and "Colliders" by hand and missed MiscLevelGeometry, which moons build ground
        /// and rock out of.
        /// </summary>
        /// <summary>What a walking body bumps into: the ground mask plus doors, which sit on the
        /// InteractableObject layer. An open door's panel stands in the room, where the NavMesh
        /// still runs.</summary>
        private int BuildBodyMask()
        {
            int playerLayer = playerController != null ? playerController.gameObject.layer : LayerMask.NameToLayer("Player");
            int doors = LayerMask.NameToLayer("InteractableObject");
            if (doors >= 0 && (playerLayer < 0 || !Physics.GetIgnoreLayerCollision(playerLayer, doors)))
                return groundMask | (1 << doors);
            return groundMask;
        }

        private int BuildGroundMask()
        {
            int playerLayer = playerController != null ? playerController.gameObject.layer : LayerMask.NameToLayer("Player");
            if (playerLayer < 0)
            {
                return StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : Physics.DefaultRaycastLayers;
            }

            int mask = 0;
            for (int layer = 0; layer < 32; layer++)
            {
                if (!Physics.GetIgnoreLayerCollision(playerLayer, layer))
                {
                    mask |= 1 << layer;
                }
            }

            // Collided with, but never a floor to put a node on.
            mask &= ~(1 << playerLayer);
            mask &= ~LayerMask.GetMask("Ignore Raycast", "UI", "Triggers", "Enemies", "EnemiesNotRendered",
                "PlayerRagdoll", "MapHazards", "InteractableObject", "Props", "PhysicsObject", "ScanNode");
            return mask;
        }

        /// <summary>
        /// A ground ray's hit is floor to stand on. The Railing layer holds railings, which are
        /// never floor, but also some staircases' walkable ramps (the factory's stair rooms);
        /// left out entirely, a flight of stairs reads as a hole. A railing is thin, a staircase
        /// is not.
        /// </summary>
        private static bool IsFloorHit(RaycastHit hit)
        {
            if (hit.collider == null || hit.collider.gameObject.layer != RailingLayer)
                return true;
            if (hit.normal.y < 0.6f)
                return false;
            Vector3 local;
            if (hit.collider is BoxCollider box)
                local = box.size;
            else if (hit.collider is MeshCollider meshCollider && meshCollider.sharedMesh != null)
                local = meshCollider.sharedMesh.bounds.size;
            else
            {
                Vector3 bounds = hit.collider.bounds.size;
                return Mathf.Min(bounds.x, bounds.z) >= 0.8f;
            }

            // Floor spreads out horizontally along two of its edges; a railing (however it is
            // rotated or mirrored) along one at most. Measured in the world, since these boxes
            // are often turned on their side.
            Transform t = hit.collider.transform;
            float a = Horizontal(t.TransformVector(new Vector3(local.x, 0f, 0f)));
            float b = Horizontal(t.TransformVector(new Vector3(0f, local.y, 0f)));
            float c = Horizontal(t.TransformVector(new Vector3(0f, 0f, local.z)));
            float second = Mathf.Max(Mathf.Min(a, b), Mathf.Min(Mathf.Max(a, b), c));
            return second >= 0.8f;
        }

        private static float Horizontal(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        private static int railingLayer = -2;
        private static int RailingLayer => railingLayer != -2 ? railingLayer : (railingLayer = LayerMask.NameToLayer("Railing"));

        private IEnumerator LoadAudioResources()
        {
            yield return StartCoroutine(LoadAudioClip(ON_TARGET_SOUND_PATH, clip => onTargetSound = clip));
            yield return StartCoroutine(LoadAudioClip(ALMOST_ON_TARGET_SOUND_PATH, clip => almostOnTargetSound = clip));
            yield return StartCoroutine(LoadAudioClip(OFF_TARGET_SOUND_PATH, clip => offTargetSound = clip));
        }

        private IEnumerator LoadAudioClip(string resourcePath, Action<AudioClip> onLoaded)
        {
            string modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string fileURL = "file://" + Path.Combine(modDirectory, resourcePath);

            UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(fileURL, AudioType.WAV);
            yield return request.SendWebRequest();

            try
            {
                if (request.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip != null)
                    {
                        onLoaded(clip);
                    }
                }
                else
                {
                    Debug.LogError($"Error loading audio clip {resourcePath}: {request.error}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception loading audio clip {resourcePath}: {ex.Message}");
            }
            finally
            {
                request.Dispose();
            }
        }

        private void InitializeNodePool()
        {
            for (int i = 0; i < INITIAL_POOL_SIZE; i++)
            {
                GameObject nodeObj = CreateNodeObject("PooledPathNode");
                nodeObj.SetActive(false);
                nodeObjectPool.Enqueue(nodeObj);
            }
        }

        private GameObject CreateNodeObject(string name)
        {
            GameObject nodeObj = new GameObject(name);
            nodeObj.AddComponent<MeshFilter>().mesh = nodeSphereMesh;
            nodeObj.AddComponent<MeshRenderer>().material = new Material(nodeBaseMaterial);

            AudioSource nodeAudio = nodeObj.AddComponent<AudioSource>();
            AudioSystemBypass.ConfigureAudioSourceForBypass(nodeAudio);
            nodeAudio.spatialBlend = 1.0f;
            nodeAudio.rolloffMode = AudioRolloffMode.Linear;
            nodeAudio.minDistance = 1.0f;
            nodeAudio.maxDistance = 35.0f;
            nodeAudio.playOnAwake = false;
            return nodeObj;
        }

        private GameObject GetNodeFromPool()
        {
            GameObject nodeObj = nodeObjectPool.Count > 0 ? nodeObjectPool.Dequeue() : CreateNodeObject("PathNode");
            nodeObj.SetActive(true);
            return nodeObj;
        }

        private void ReturnNodeToPool(GameObject nodeObj)
        {
            if (nodeObj == null) return;
            nodeObj.SetActive(false);
            nodeObjectPool.Enqueue(nodeObj);
        }
        #endregion

        #region Public API
        public void NavigateTo(GameObject targetObject)
        {
            try
            {
                // Pressing the key again for the same target switches mode.
                if (isPathfinding && currentTargetObject == targetObject)
                {
                    TogglePathfindingMode();
                    return;
                }

                if (StartOfRound.Instance == null || !StartOfRound.Instance.shipHasLanded)
                {
                    Utilities.SpeakText("The ship has not landed yet. Pathfinding is not allowed.");
                    return;
                }

                if (targetObject == null)
                {
                    Utilities.SpeakText("Selected object not found at current location.");
                    return;
                }

                StopPathfinding(playSound: false);
                stoppingRadius = ConfigManager.PathfindingStoppingRadius.Value;
                currentTargetObject = targetObject;
                currentMode = PathfindingMode.Manual;
                blockedSpots.Clear();
                blockedSpotRadii.Clear();
                autoStuckCuts = 0;

                if (!ResolveDestination(targetObject, out currentDestination))
                {
                    Utilities.SpeakText("Could not pathfind! No valid NavMesh near the target.");
                    return;
                }

                if (!BuildPath())
                {
                    Utilities.SpeakText("Cannot find a valid path to the target. Please try a different location.");
                    return;
                }

                isPathfinding = true;
                hasAnnouncedDestinationReached = false;
                ResetStuckTracking();

                Utilities.SpeakText(lockedDoorOnPath != null
                    ? $"Pathfinding to {GetTargetName()}, through a locked door. You'll need your key."
                    : elevatorOnPath != null
                        ? $"Pathfinding to {GetTargetName()}, by the elevator."
                        : $"Pathfinding to {GetTargetName()}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in NavigateTo: {ex.Message}\n{ex.StackTrace}");
                Utilities.SpeakText("An error occurred while trying to navigate. Please try again.");
                StopPathfinding(playSound: false);
            }
        }

        public void TogglePathfindingMode()
        {
            if (!isPathfinding || currentTargetObject == null)
                return;

            if (currentMode == PathfindingMode.Manual)
            {
                currentMode = PathfindingMode.Auto;
                ResetStuckTracking();
                Utilities.SpeakText("Switched to automatic pathfinding mode");
            }
            else
            {
                currentMode = PathfindingMode.Manual;
                ClearAutoInput();
                nodeAudioPingTimer = 0f;
                Utilities.SpeakText("Switched to manual pathfinding mode");
            }

            ShouldPreventFallDamage = currentMode == PathfindingMode.Auto;
        }

        public void StopPathfinding()
        {
            StopPathfinding(playSound: true);
        }

        private void StopPathfinding(bool playSound)
        {
            bool wasActive = isPathfinding;

            isPathfinding = false;
            isOffNavMeshTarget = false;
            ShouldPreventFallDamage = false;
            nearDoorOpen.Clear();
            doorReplanAt = -1f;
            ClearCarvedSpots();
            if (autoCrouched && playerController != null && playerController.isCrouching)
            {
                playerController.Crouch(false);
                // Still under something (arrived at the mouth of a crawl space): stand when
                // there is room, for a few seconds.
                if (playerController.isCrouching)
                    standUpPendingUntil = Time.time + 3f;
            }
            autoCrouched = false;
            ResetGapState();
            ResetCrawlState();
            manualCrouchCued = false;
            manualLadder = null;
            lockedDoorOnPath = null;
            lockedDoorCrossIndex = -1;
            ResetElevatorRoute();
            ForgetElevatorPress();
            gapGiveUps = 0;
            ledgeReplans = 0;
            narrowLandingReplans = 0;
            blockedReplans = 0;
            ladderTriedOn = null;
            ladderTries = 0;
            RestoreRefusedLadders();
            plansThisTrip = 0;
            keptLowSpots.Clear();
            trialCut = null;
            doorBeingOpened = null;
            cannotClimbCueLadder = null;
            ClearAutoInput();
            ClearPath();
            ClearPathNodes();

            if (wasActive && playSound && audioSource != null && audioSource.clip != null)
            {
                audioSource.Play();
            }
        }
        #endregion

        #region Path Building
        /// <summary>
        /// Picks the NavMesh point to plan to. Registered menu items that are off the mesh (ship
        /// furniture, the terminal) get a final node at their real position.
        /// </summary>
        private bool ResolveDestination(GameObject targetObject, out Vector3 destination)
        {
            isOffNavMeshTarget = false;
            Vector3 targetPos = targetObject.transform.position;
            NavMeshHit hit;

            // Small radius first: a wide search returns the nearest mesh point in 3D, which for
            // something on a raised platform (Experimentation's main entrance) can be the ground
            // underneath it, where no path reaches.
            if (NavMesh.SamplePosition(targetPos, out hit, 2.5f, NavMesh.AllAreas) ||
                NavMesh.SamplePosition(targetPos, out hit, 5f, NavMesh.AllAreas) ||
                NavMesh.SamplePosition(targetPos, out hit, 10f, NavMesh.AllAreas))
            {
                destination = hit.position;
                return true;
            }

            if (NavMenu.IsRegisteredMenuItem(targetObject.name))
            {
                isOffNavMeshTarget = true;
                offNavMeshTargetPos = targetPos;

                Vector3 toTarget = targetPos - transform.position;
                Vector3 flat = new Vector3(toTarget.x, 0f, toTarget.z).normalized;
                for (float distance = 5f; distance <= 15f; distance += 5f)
                {
                    if (NavMesh.SamplePosition(transform.position + flat * distance, out hit, 5f, NavMesh.AllAreas))
                    {
                        destination = hit.position;
                        return true;
                    }
                }
            }

            destination = targetPos;
            return false;
        }

        /// <summary>
        /// Plans from where the player stands to currentDestination and lays the nodes down.
        /// Synchronous: a few hundred NavMesh samples and a raycast per node is well under a
        /// millisecond-scale cost, so there is no "generating path" wait.
        /// </summary>
        private static readonly float[] StartSearchDistances = { 0.75f, 1.5f, 2.25f, 3f, 4f };

        /// <summary>
        /// The NavMesh point nearest the player on the floor they stand on. Nearest in 3D can be
        /// the ground under the platform they are on: the walkway in front of Experimentation's
        /// main entrance is off the mesh right at the door, and the ground 2.6 m below it is
        /// nearer than the mesh 3 m along the walkway.
        /// </summary>
        private bool SampleStartOnOwnFloor(out NavMeshHit hit)
        {
            Vector3 feet = transform.position;
            if (NavMesh.SamplePosition(feet, out hit, 0.75f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - feet.y) < 1f)
                return true;
            foreach (float distance in StartSearchDistances)
            {
                for (int k = 0; k < 12; k++)
                {
                    Vector3 probe = feet + Quaternion.Euler(0f, k * 30f, 0f) * Vector3.forward * distance;
                    if (NavMesh.SamplePosition(probe, out hit, 0.6f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - feet.y) < 1f &&
                        !Physics.Linecast(feet + Vector3.up * 0.6f, hit.position + Vector3.up * 0.6f, groundMask, QueryTriggerInteraction.Ignore))
                        return true;
                }
            }
            return false;
        }

        private bool BuildPath()
        {
            EnsureLadderLinks();
            EnsureJumpLinks();
            NavMeshHit startHit;
            if (!SampleStartOnOwnFloor(out startHit) &&
                !NavMesh.SamplePosition(transform.position, out startHit, 5f, NavMesh.AllAreas))
            {
                Debug.LogWarning("Pathfinder: no NavMesh near the player");
                return false;
            }

            // The NavMesh is baked for enemies, and its Jump and Climb links are moves only they
            // can make (Experimentation has one straight up onto the raised bridge). Plan without
            // them, and use them only when there is no complete path otherwise.
            NavMeshPath navPath = new NavMeshPath();
            bool planned = CalculatePlayerPath(startHit.position, currentDestination, PlayerAreaMask(), navPath);
            if (!planned || navPath.status != NavMeshPathStatus.PathComplete)
            {
                NavMeshPath anyPath = new NavMeshPath();
                if (CalculatePlayerPath(startHit.position, currentDestination, NavMesh.AllAreas, anyPath) &&
                    anyPath.status != NavMeshPathStatus.PathInvalid && anyPath.corners.Length >= 2 &&
                    (!planned || anyPath.status == NavMeshPathStatus.PathComplete ||
                     PathEndGap(anyPath) < PathEndGap(navPath) - 1f))
                {
                    navPath = anyPath;
                    planned = true;
                }
            }

            if (!planned || navPath.status == NavMeshPathStatus.PathInvalid || navPath.corners.Length < 2)
            {
                Debug.LogWarning($"Pathfinder: path failed, status {navPath.status}");
                return false;
            }
            pathIsPartial = navPath.status == NavMeshPathStatus.PathPartial && PathEndGap(navPath) > stoppingRadius;

            List<Vector3> positions;
            lockedDoorOnPath = null;
            lockedDoorCrossIndex = -1;
            lockedDoorCued = false;
            ResetElevatorRoute();
            float normalLength = pathIsPartial ? float.MaxValue : PathLength(navPath.corners);
            if (TryKeyRoute(startHit.position, normalLength, out Vector3[] toDoor, out Vector3[] fromDoor, out DoorLock door))
            {
                // Two halves laid separately: the straight line through the doorway crosses
                // carved NavMesh, which the surface walk would snap off to one side.
                positions = BuildNodePositions(AvoidLow(startHit.position, AvoidQuicksand(startHit.position, toDoor)), finalLeg: false);
                lockedDoorCrossIndex = positions.Count;
                positions.AddRange(BuildNodePositions(AvoidLow(fromDoor[0], AvoidQuicksand(fromDoor[0], fromDoor))));
                lockedDoorOnPath = door;
                pathIsPartial = false;
            }
            else if (pathIsPartial && TryElevatorRoute(startHit.position, out Vector3[] toStop, out Vector3[] fromStop,
                         out MineshaftElevatorController elevator, out bool goingDown))
            {
                // The Mineshaft's elevator joins two levels the NavMesh does not: plan to its stop
                // here and on from its stop there.
                positions = BuildNodePositions(AvoidLow(startHit.position, AvoidQuicksand(startHit.position, toStop)), finalLeg: false);
                elevatorCrossIndex = positions.Count;
                positions.AddRange(BuildNodePositions(AvoidLow(fromStop[0], AvoidQuicksand(fromStop[0], fromStop))));
                elevatorOnPath = elevator;
                elevatorGoingDown = goingDown;
                pathIsPartial = false;
                Debug.Log($"Pathfinder: route by the elevator, going {(goingDown ? "down" : "up")}, crossing at node {elevatorCrossIndex}");
            }
            else
            {
                Vector3[] corners = AvoidLow(startHit.position, AvoidQuicksand(startHit.position, navPath.corners));
                // Still through somewhere a player cannot go: cut it out of the NavMesh and plan
                // again once the cut is in (a frame or two). A whole hillside takes several, and
                // the NavMesh finds ways round the detour search misses (Vow's rock faces).
                // If the plan after the cut no longer reaches the target, the cut is undone and
                // the spot kept: it was the only way (a flooded rock tunnel in Adamance's mine,
                // 1.62 m high, that a crouched player does get through).
                if (FirstTooLow(corners, out Vector3 impassable) && !NearKeptLowSpot(impassable))
                {
                    Debug.Log($"Pathfinder: still impassable after detours at {impassable} [{tooLowWhy}]");
                    int before = carvedSpots.Count;
                    CarveImpassable(impassable, 2.5f);
                    if (carvedSpots.Count > before)
                    {
                        trialCut = carvedSpots[carvedSpots.Count - 1];
                        trialCutReachedBefore = !pathIsPartial;
                    }
                }
                positions = BuildNodePositions(corners);
            }

            plansThisTrip++;
            ClearPathNodes();
            for (int i = 0; i < positions.Count; i++)
            {
                CreateNode(positions[i], i);
            }

            currentTargetNode = null;
            FindNextTargetNode();
            nodeAudioPingTimer = 0f;
            lastReplanTime = Time.time;
            HighlightNodes();
            DrawPath();
            return pathNodes.Count > 0;
        }

        #region Hazards
        // Quicksand zones are trigger volumes the NavMesh knows nothing about, so a path runs
        // straight through them. Flood water is one huge volume whose top is the water line.
        private readonly List<Bounds> quicksandZones = new List<Bounds>();
        private readonly List<Bounds> waterVolumes = new List<Bounds>();
        private float hazardsTime = -999f;
        private object hazardCueNode;
        private float lastHazardCueTime = -999f;
        private const float QUICKSAND_DETOUR_MARGIN = 2.5f;
        private const float DEEP_WATER = 1.0f;

        private void RefreshHazards()
        {
            if (Time.time - hazardsTime < 5f)
                return;
            hazardsTime = Time.time;
            quicksandZones.Clear();
            waterVolumes.Clear();

            foreach (var zone in FindObjectsOfType<QuicksandTrigger>())
            {
                Collider collider = zone.GetComponent<Collider>();
                if (collider == null || !collider.enabled)
                    continue;
                (zone.isWater ? waterVolumes : quicksandZones).Add(collider.bounds);
            }
        }

        private bool InQuicksand(Vector3 point, out Bounds zone)
        {
            RefreshHazards();
            foreach (var b in quicksandZones)
            {
                if (point.x >= b.min.x && point.x <= b.max.x && point.z >= b.min.z && point.z <= b.max.z &&
                    point.y >= b.min.y - 1f && point.y <= b.max.y + 1f)
                {
                    zone = b;
                    return true;
                }
            }
            zone = default;
            return false;
        }

        private bool InDeepWater(Vector3 ground)
        {
            RefreshHazards();
            foreach (var b in waterVolumes)
            {
                if (b.Contains(ground) && b.max.y - ground.y > DEEP_WATER)
                    return true;
            }
            return false;
        }

        private bool FirstQuicksandCrossed(Vector3[] corners, out Bounds zone)
        {
            for (int i = 0; i < corners.Length - 1; i++)
            {
                float length = Vector3.Distance(corners[i], corners[i + 1]);
                int steps = Mathf.Max(1, Mathf.CeilToInt(length));
                for (int s = 0; s <= steps; s++)
                {
                    if (InQuicksand(Vector3.Lerp(corners[i], corners[i + 1], (float)s / steps), out zone))
                        return true;
                }
            }
            zone = default;
            return false;
        }

        private static bool Crosses(Vector3[] corners, Bounds zone)
        {
            for (int i = 0; i < corners.Length - 1; i++)
            {
                float length = Vector3.Distance(corners[i], corners[i + 1]);
                int steps = Mathf.Max(1, Mathf.CeilToInt(length));
                for (int s = 0; s <= steps; s++)
                {
                    Vector3 p = Vector3.Lerp(corners[i], corners[i + 1], (float)s / steps);
                    if (p.x >= zone.min.x && p.x <= zone.max.x && p.z >= zone.min.z && p.z <= zone.max.z &&
                        p.y >= zone.min.y - 1f && p.y <= zone.max.y + 1f)
                        return true;
                }
            }
            return false;
        }

        private static float PathLength(Vector3[] corners)
        {
            float length = 0f;
            for (int i = 0; i < corners.Length - 1; i++)
            {
                length += Vector3.Distance(corners[i], corners[i + 1]);
            }
            return length;
        }

        private static Vector3[] PlanCorners(Vector3 from, Vector3 to)
        {
            NavMeshPath path = new NavMeshPath();
            if (CalculatePlayerPath(from, to, PlayerAreaMask(), path) && path.status == NavMeshPathStatus.PathComplete)
                return path.corners;
            if (CalculatePlayerPath(from, to, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete)
                return path.corners;
            return null;
        }

        /// <summary>
        /// Re-routes a path that crosses a quicksand zone through whichever corner of the zone
        /// gives the shortest way round. Up to three zones; a path that cannot avoid one keeps
        /// going through it, and the hazard cue warns about it.
        /// </summary>
        private Vector3[] AvoidQuicksand(Vector3 start, Vector3[] corners)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                if (!FirstQuicksandCrossed(corners, out Bounds zone) || InQuicksand(start, out _))
                    return corners;

                Vector3 destination = corners[corners.Length - 1];
                Vector3[] best = null;
                float bestLength = float.MaxValue;
                float m = QUICKSAND_DETOUR_MARGIN;
                Vector3[] around =
                {
                    new Vector3(zone.min.x - m, zone.center.y, zone.min.z - m),
                    new Vector3(zone.min.x - m, zone.center.y, zone.max.z + m),
                    new Vector3(zone.max.x + m, zone.center.y, zone.min.z - m),
                    new Vector3(zone.max.x + m, zone.center.y, zone.max.z + m),
                };

                foreach (var point in around)
                {
                    NavMeshHit hit;
                    if (!NavMesh.SamplePosition(point, out hit, zone.extents.y + 5f, NavMesh.AllAreas) || InQuicksand(hit.position, out _))
                        continue;

                    Vector3[] first = PlanCorners(start, hit.position);
                    Vector3[] second = first == null ? null : PlanCorners(hit.position, destination);
                    if (second == null)
                        continue;

                    Vector3[] combined = new Vector3[first.Length + second.Length - 1];
                    first.CopyTo(combined, 0);
                    Array.Copy(second, 1, combined, first.Length, second.Length - 1);
                    if (Crosses(combined, zone))
                        continue;

                    float length = PathLength(combined);
                    if (length < bestLength)
                    {
                        bestLength = length;
                        best = combined;
                    }
                }

                if (best == null)
                    return corners;
                corners = best;
            }
            return corners;
        }

        // Half a metre and one as well: in a narrow rock tunnel the low part is one side wall and
        // the way round is the middle of the same tunnel, 0.25 m over (Adamance's mine). After
        // 1.5, not before: each try plans the whole path twice, and on a 240 m route on Offense
        // the small rings used up the time budget before 1.5 m, where the way round was.
        private static readonly float[] LowDetourRadii = { 1.5f, 0.5f, 1f, 2.5f, 3.5f, 6f, 10f, 15f, 25f, 40f };

        /// <summary>
        /// Somewhere a crouched player does not fit: the enemy NavMesh runs under shelves and pipe
        /// work 1.3 m up (the tubing in the middle of Vow's diagonal rooms), lower than the 1.5 m
        /// crouched controller. Server racks, 1.8 m, still fit.
        /// </summary>
        private bool TooLowAt(Vector3 feet)
        {
            for (int b = 0; b < blockedSpots.Count; b++)
            {
                Vector3 spot = blockedSpots[b];
                if (HorizontalDistance(spot, feet) < blockedSpotRadii[b] && Mathf.Abs(spot.y - feet.y) < 1.5f)
                {
                    tooLowWhy = "blocked spot " + spot;
                    return true;
                }
            }
            // Overhead only: straight up from the floor, in the middle and a little to each side.
            // A sphere at head height also caught the railing beside a catwalk.
            int hits = 0;
            for (int i = 0; i < OverheadProbeOffsets.Length; i++)
            {
                if (Physics.Raycast(feet + OverheadProbeOffsets[i] + Vector3.up * 0.2f, Vector3.up, CROUCHED_CLEARANCE - 0.2f, groundMask, QueryTriggerInteraction.Ignore))
                    hits++;
                else if (i + 1 - hits > OverheadProbeOffsets.Length - 3)
                    return false;
            }
            return hits >= 3;
        }

        // The crouched controller reaches 1.59 m above the floor (height 1.5 from just below the
        // feet, plus the 0.08 skin), so 1.55 let it into a 1.53 m gap under the ship's catwalk.
        private const float CROUCHED_CLEARANCE = 1.62f;
        private static readonly Vector3[] OverheadProbeOffsets =
        {
            Vector3.zero, new Vector3(0.2f, 0f, 0f), new Vector3(-0.2f, 0f, 0f), new Vector3(0f, 0f, 0.2f), new Vector3(0f, 0f, -0.2f)
        };

        // Where the player got stuck on this trip, whatever the geometry checks say: replanning
        // goes round them (AvoidTooLow) instead of back into the same spot.
        private readonly List<Vector3> blockedSpots = new List<Vector3>();
        private readonly List<float> blockedSpotRadii = new List<float>();
        // Stuck again near the last stuck spot: each cut wider than the last. On Artifice's
        // hillside 1.2 m cuts sent four detours in a row back onto the same rock face.
        private int autoStuckCuts;
        private Vector3 autoStuckCutPosition;

        private void RememberBlockedSpot(Vector3 heading)
        {
            heading.y = 0f;
            // 1.2 m ahead: 0.6 m put the spot so close that every way round, whichever way it
            // set off, passed within its 0.8 m and was refused (Artifice's hillside).
            RememberBlockedSpot(transform.position + (heading.sqrMagnitude > 0.0001f ? heading.normalized * 1.2f : Vector3.zero), 1.2f);
        }

        private void RememberBlockedSpot(Vector3 spot, float size)
        {
            if (blockedSpots.Count >= 8)
            {
                blockedSpots.RemoveAt(0);
                blockedSpotRadii.RemoveAt(0);
            }
            blockedSpots.Add(spot);
            blockedSpotRadii.Add(Mathf.Max(0.8f, size * 0.5f));
            CarveImpassable(spot, size);
            Debug.Log($"Pathfinder: remembering blocked spot {spot}");
        }

        /// <summary>Halfway up a climb no player makes, where cutting the NavMesh takes the climb out.</summary>
        private void RememberClimb(Vector3 top)
        {
            RememberBlockedSpot(Vector3.Lerp(transform.position, top, 0.5f), 2f);
        }

        /// <summary>
        /// The first point along corners, sampled every 0.4 m, a player cannot get past: somewhere
        /// a crouched player does not fit, or a climb steeper than the player's controller walks
        /// up. The enemy NavMesh climbs 56 degree slopes, the player 48 (Vow's hillsides); a steep
        /// stretch counts from 1.2 m long or 1.5 m high. Legs off the mesh (ladders,
        /// jumps, drops) are not walked and are skipped, and so is where the player already is.
        /// With near, only legs within within metres of it are looked at.
        /// </summary>
        private string tooLowWhy = "";

        private bool FirstTooLow(Vector3[] corners, out Vector3 point, Vector3 near = default, float within = float.MaxValue)
        {
            point = Vector3.zero;
            tooLowWhy = "";
            float walked = 0f;
            float slopeLimit = playerController.thisController.slopeLimit;
            float steepTan = Mathf.Tan((slopeLimit + 1f) * Mathf.Deg2Rad);
            // A short steep lip is jumped (auto mode: UpdateSlopeJump; manual: the Jump cue), so
            // only a long one counts, or one rising more than a jump on a slope clears. Counting
            // every lip over a step made the detours on Vow's hills run out of time.
            float steepRise = 0f;
            float steepLength = 0f;
            float slidingLength = 0f;
            bool havePrevious = false;
            Vector3 previous = Vector3.zero;
            for (int i = 0; i < corners.Length - 1; i++)
            {
                Vector3 a = corners[i];
                Vector3 b = corners[i + 1];
                float flat = HorizontalDistance(a, b);
                bool outOfReach = within < float.MaxValue && HorizontalDistance(a, near) > within && HorizontalDistance(b, near) > within;
                if (outOfReach || IsLinkLeg(a, b))
                {
                    walked += flat;
                    steepRise = 0f;
                    steepLength = 0f;
                    havePrevious = false;
                    slidingLength = 0f;
                    continue;
                }
                int steps = Mathf.Max(1, Mathf.CeilToInt(flat / 0.4f));
                for (int s = 1; s <= steps; s++)
                {
                    Vector3 q = Vector3.Lerp(a, b, (float)s / steps);
                    if (walked + flat * s / steps <= 0.3f)
                        continue;
                    bool onFloor = FloorNearLine(q, out float floorY);
                    q.y = floorY;
                    if (TooLowAt(q))
                    {
                        point = q;
                        if (!tooLowWhy.StartsWith("blocked"))
                            tooLowWhy = onFloor ? "too low" : "too low, no floor near the line";
                        return true;
                    }
                    if (!onFloor)
                        continue;
                    // Rising ground, judged from the floor rather than the corners: a leg across a
                    // valley runs downhill while the far side of the valley climbs. The rise since
                    // the last floor found also counts, for a face too steep to give a floor.
                    float rise = havePrevious ? q.y - previous.y : 0f;
                    float run = havePrevious ? HorizontalDistance(q, previous) : 0f;
                    // A jump of metres in one sample is the floor found switching surfaces, not a
                    // slope: under a staircase (Adamance) the line between its corners finds the
                    // ground below, then the stairs above it.
                    if (rise > run * 3.7f + 0.5f)
                    {
                        previous = q;
                        steepRise = 0f;
                        steepLength = 0f;
                        slidingLength = 0f;
                        continue;
                    }
                    bool slidingGround = rise > 0.05f &&
                                         Physics.Raycast(q + Vector3.up * 1.5f, Vector3.down, out RaycastHit ground, 3f, groundMask, QueryTriggerInteraction.Ignore) &&
                                         Vector3.Angle(ground.normal, Vector3.up) > slopeLimit + 1f;
                    bool steep = rise > 0.05f && ((run > 0.01f && rise > run * steepTan) || slidingGround);
                    previous = q;
                    havePrevious = true;
                    // Judged over the whole climb, not sample by sample: Artifice's hillside by the
                    // ship alternates 45 and 55 degrees every 0.4 m, and resetting on each gentler
                    // sample never counted the 4 m face as too steep.
                    // Ground the game slides a player off counts whichever way the path crosses it: a
                    // diagonal up the same face rises gently along the path and still slides.
                    bool climbing = steep || (rise > 0.05f && run > 0.01f && rise > run * 0.7f);
                    if (!climbing)
                    {
                        steepRise = 0f;
                        steepLength = 0f;
                        slidingLength = 0f;
                    }
                    else
                    {
                        steepLength += run;
                        steepRise += rise;
                        if (slidingGround)
                            slidingLength += run;
                        if (slidingLength >= 1.2f ||
                            (steepRise > steepLength * steepTan && (steepLength >= 1.2f || steepRise > 1.5f)))
                        {
                            point = q;
                            tooLowWhy = $"steep: rise {steepRise:F2} over {steepLength:F2}, sliding {slidingLength:F2}";
                            return true;
                        }
                    }
                }
                walked += flat;
            }
            return false;
        }

        private static readonly RaycastHit[] floorLineHits = new RaycastHit[16];

        /// <summary>
        /// The floor under a point on the straight line between two corners. First just below it:
        /// off an edge (the ship's catwalk) the line runs under the floor it leaves. Then whatever
        /// surface is nearest the line: a long leg across a valley on Vow ran 6 m above the ground,
        /// where a short ray found nothing and the 48 degree rock on the far side went untested.
        /// Only a surface the NavMesh lies on counts there, never a tree's crown.
        /// </summary>
        private bool FloorNearLine(Vector3 q, out float y)
        {
            y = q.y;
            if (Physics.Raycast(q + Vector3.up * 1f, Vector3.down, out RaycastHit floorHit, 2.5f, groundMask, QueryTriggerInteraction.Ignore))
            {
                y = floorHit.point.y;
                return true;
            }
            int count = Physics.RaycastNonAlloc(q + Vector3.up * 15f, Vector3.down, floorLineHits, 40f, groundMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                Vector3 point = floorLineHits[i].point;
                float offset = Mathf.Abs(point.y - q.y);
                if (offset < best && NavMesh.SamplePosition(point, out NavMeshHit mesh, 0.6f, NavMesh.AllAreas) &&
                    HorizontalDistance(mesh.position, point) < 0.3f)
                {
                    best = offset;
                    y = point.y;
                }
            }
            return best < float.MaxValue;
        }

        private const int MAX_CARVED_SPOTS = 16;
        private readonly List<GameObject> carvedSpots = new List<GameObject>();
        private int carveReplanFrame = -1;
        private GameObject trialCut;
        private bool trialCutReachedBefore;
        private readonly List<Vector3> keptLowSpots = new List<Vector3>();

        private bool NearKeptLowSpot(Vector3 spot)
        {
            foreach (Vector3 kept in keptLowSpots)
            {
                if (HorizontalDistance(kept, spot) < 2.5f && Mathf.Abs(kept.y - spot.y) < 2f)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Cuts a spot a player cannot get past out of the NavMesh, the way the game cuts locked
        /// doors out, so the next plan goes round it. The cut takes effect over the next frame or
        /// two, hence the replan is scheduled, not done here. Undone when pathfinding stops.
        /// </summary>
        private void CarveImpassable(Vector3 spot, float size)
        {
            // Not where the player stands: that only takes the floor from under them.
            if (carvedSpots.Count >= MAX_CARVED_SPOTS || HorizontalDistance(spot, transform.position) < 1.5f)
                return;
            GameObject block = new GameObject("LethalAccessImpassable");
            block.transform.position = spot;
            NavMeshObstacle obstacle = block.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.size = new Vector3(size, 3f, size);
            obstacle.carveOnlyStationary = false;
            obstacle.carving = true;
            carvedSpots.Add(block);
            carveReplanFrame = Time.frameCount + 2;
            Debug.Log($"Pathfinder: cut impassable spot {spot} out of the NavMesh ({carvedSpots.Count})");
        }

        private void ClearCarvedSpots()
        {
            foreach (GameObject block in carvedSpots)
            {
                if (block != null)
                    Destroy(block);
            }
            carvedSpots.Clear();
            carveReplanFrame = -1;
        }

        private const long AVOID_LOW_BUDGET_MS = 15;
        // The first plan of a trip, once, when the player asks for it: on a 240 m route on
        // Offense the detour took 14 ms one run and ran out at 16 the next, and running out cuts
        // the spot out of the NavMesh, which trapped the player in a pond under a grate.
        private const long AVOID_LOW_FIRST_BUDGET_MS = 30;
        private int plansThisTrip;

        private Vector3[] PlanPlayerCorners(Vector3 from, Vector3 to)
        {
            NavMeshPath path = new NavMeshPath();
            if (CalculatePlayerPath(from, to, PlayerAreaMask(), path) && path.status == NavMeshPathStatus.PathComplete)
                return path.corners;
            return null;
        }

        /// <summary>
        /// Re-routes a path through somewhere a player cannot get through even crouched, by way of
        /// the waypoint near it that gives the shortest route clear of every such spot. Up to
        /// three spots; one with no way round stays, and the stuck handling deals with it.
        /// </summary>
        private Vector3[] AvoidTooLow(Vector3 start, Vector3[] corners)
        {
            // A search over every ring cost up to 76 ms in one frame on Vow. Past the budget the
            // spot is left for BuildPath to cut out of the NavMesh, which finds the way round
            // over the next frames instead.
            var timer = System.Diagnostics.Stopwatch.StartNew();
            for (int pass = 0; pass < 3; pass++)
            {
                if (!FirstTooLow(corners, out Vector3 low))
                    return corners;
                string lowWhy = tooLowWhy;

                Vector3 destination = corners[corners.Length - 1];
                Vector3[] best = null;
                float bestLength = float.MaxValue;
                bool outOfTime = false;
                foreach (float radius in LowDetourRadii)
                {
                    for (int k = 0; k < 12; k++)
                    {
                        if (timer.ElapsedMilliseconds > (plansThisTrip == 0 ? AVOID_LOW_FIRST_BUDGET_MS : AVOID_LOW_BUDGET_MS))
                        {
                            outOfTime = true;
                            break;
                        }
                        Vector3 around = low + Quaternion.Euler(0f, k * 30f, 0f) * Vector3.forward * radius;
                        NavMeshHit hit;
                        if (!NavMesh.SamplePosition(around, out hit, radius > 4f ? 3f : 0.6f, NavMesh.AllAreas) ||
                            Mathf.Abs(hit.position.y - low.y) > Mathf.Max(1.5f, radius) ||
                            Vector3.Distance(start, hit.position) + Vector3.Distance(hit.position, destination) >= bestLength ||
                            TooLowAt(hit.position))
                            continue;
                        Vector3[] first = PlanPlayerCorners(start, hit.position);
                        Vector3[] second = first == null ? null : PlanPlayerCorners(hit.position, destination);
                        if (second == null)
                            continue;
                        Vector3[] combined = new Vector3[first.Length + second.Length - 1];
                        first.CopyTo(combined, 0);
                        Array.Copy(second, 1, combined, first.Length, second.Length - 1);
                        float length = PathLength(combined);
                        if (length >= bestLength || FirstTooLow(combined, out _, low, radius + 10f))
                            continue;
                        bestLength = length;
                        best = combined;
                    }
                    // The nearest ring with a way round is the one to take.
                    if (best != null || outOfTime)
                        break;
                }
                if (best == null)
                {
                    Debug.Log($"Pathfinder: no way round the impassable spot at {low} [{lowWhy}]{(outOfTime ? " found in time" : "")} ({timer.ElapsedMilliseconds} ms)");
                    return corners;
                }
                Debug.Log($"Pathfinder: routed round the impassable spot at {low} [{lowWhy}], {PathLength(corners):F0} m -> {bestLength:F0} m ({timer.ElapsedMilliseconds} ms)");
                corners = best;
            }
            return corners;
        }

        private Vector3[] AvoidLow(Vector3 start, Vector3[] corners) => AvoidCrouch(AvoidTooLow(start, corners));

        private const long AVOID_CROUCH_BUDGET_MS = 6;
        private const long AVOID_CROUCH_FIRST_BUDGET_MS = 12;
        private static readonly float[] CrouchDetourRadii = { 1.5f, 2.5f, 3.5f };

        /// <summary>
        /// Somewhere only a crouched player fits, with standing room a few metres to the side: the
        /// NavMesh hugs whatever it passes, and on Offense the path back to the ship ran under the
        /// edge of its hull, between the skids, with open ground 1.5 m away. Crawling there took
        /// 18 s of sidestepping. A way round costing a few metres more is taken instead; a crawl
        /// space with none (a server rack) is left as it is. Planned locally, from just before
        /// the crouch to just past it, and spliced in: whole-route plans per try ran a 240 m
        /// route out of time on the first crawl.
        /// </summary>
        private Vector3[] AvoidCrouch(Vector3[] corners)
        {
            if (corners == null || corners.Length < 2)
                return corners;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            long budget = plansThisTrip == 0 ? AVOID_CROUCH_FIRST_BUDGET_MS : AVOID_CROUCH_BUDGET_MS;
            // Past a crawl space with no way round, on to the next one.
            float after = 0f;
            for (int pass = 0; pass < 6 && timer.ElapsedMilliseconds <= budget; pass++)
            {
                if (!FirstCrouchSpot(corners, out Vector3 low, out float lowAt, after))
                    return corners;
                float clearAt = CrouchStretchEnd(corners, lowAt);
                after = clearAt + 0.5f;
                float inAt = Mathf.Max(0f, lowAt - 3f);
                float outAt = Mathf.Min(FlatLength(corners), clearAt + 3f);
                if (!PointAlong(corners, inAt, out Vector3 pin, out int inSeg) || !PointAlong(corners, outAt, out Vector3 pout, out int outSeg) ||
                    SpansLink(corners, inSeg, outSeg) || !OntoFloor(ref pin) || !OntoFloor(ref pout) ||
                    !NavMesh.SamplePosition(pin, out NavMeshHit inHit, 1.5f, NavMesh.AllAreas) ||
                    !NavMesh.SamplePosition(pout, out NavMeshHit outHit, 1.5f, NavMesh.AllAreas))
                {
                    Debug.Log($"Pathfinder: crouch at {low} left as it is: no ends to plan between");
                    continue;
                }
                PointAlong(corners, (lowAt + clearAt) * 0.5f, out Vector3 middle, out _);
                float bestLength = outAt - inAt + Mathf.Max(4f, (outAt - inAt) * 0.25f);
                Vector3[] best = null;
                // A stretch along the edge of something (a hull, a pipe's lip): waypoints every 2 m
                // along it, all shifted the same way, so the way round can't cut back under it.
                foreach (float offset in CrouchDetourRadii)
                {
                    foreach (float side in new[] { 1f, -1f })
                    {
                        if (timer.ElapsedMilliseconds > budget)
                            break;
                        Vector3[] chain = ShiftedChain(corners, lowAt, clearAt, side * offset, inHit.position, outHit.position, bestLength);
                        if (chain == null)
                            continue;
                        float length = FlatLength(chain);
                        if (length >= bestLength || FirstCrouchSpot(chain, out _, out _, strict: true) || FirstTooLow(chain, out _))
                            continue;
                        bestLength = length;
                        best = chain;
                    }
                }
                // One waypoint to the side: only for a short crouch (under a beam's end); a long
                // stretch pulls the way round back under it, and the search is the costly part.
                foreach (Vector3 centre in best != null || clearAt - lowAt > 3f ? new Vector3[0] : new[] { low, middle })
                {
                    foreach (float radius in CrouchDetourRadii)
                    {
                        for (int k = 0; k < 12 && timer.ElapsedMilliseconds <= budget; k++)
                        {
                            Vector3 around = centre + Quaternion.Euler(0f, k * 30f, 0f) * Vector3.forward * radius;
                            if (!NavMesh.SamplePosition(around, out NavMeshHit hit, 0.6f, NavMesh.AllAreas) ||
                                Mathf.Abs(hit.position.y - centre.y) > 1.5f ||
                                HorizontalDistance(inHit.position, hit.position) + HorizontalDistance(hit.position, outHit.position) >= bestLength ||
                                NeedsCrouchAt(hit.position, strict: true))
                                continue;
                            Vector3[] first = PlanPlayerCorners(inHit.position, hit.position);
                            Vector3[] second = first == null ? null : PlanPlayerCorners(hit.position, outHit.position);
                            if (second == null)
                                continue;
                            Vector3[] local = new Vector3[first.Length + second.Length - 1];
                            first.CopyTo(local, 0);
                            Array.Copy(second, 1, local, first.Length, second.Length - 1);
                            float length = FlatLength(local);
                            if (length >= bestLength || FirstCrouchSpot(local, out _, out _, strict: true) || FirstTooLow(local, out _))
                                continue;
                            bestLength = length;
                            best = local;
                        }
                    }
                }
                if (best == null)
                {
                    Debug.Log($"Pathfinder: no standing way round the crouch at {low} ({clearAt - lowAt:F0} m, {timer.ElapsedMilliseconds} ms)");
                    continue;
                }
                var spliced = new List<Vector3>();
                for (int i = 0; i <= inSeg; i++)
                    spliced.Add(corners[i]);
                spliced.AddRange(best);
                for (int i = outSeg + 1; i < corners.Length; i++)
                    spliced.Add(corners[i]);
                Debug.Log($"Pathfinder: stood up round a crouch at {low}, {outAt - inAt:F0} m -> {bestLength:F0} m ({timer.ElapsedMilliseconds} ms)");
                corners = spliced.ToArray();
                // On from the end of the way round: the crouches before it were already tried.
                after = inAt + bestLength + 0.5f;
            }
            return corners;
        }

        /// <summary>
        /// A route from start to end through points every 2 m along corners between from and to
        /// metres, each moved shift metres to the right of the path there (left when negative).
        /// Null if any of them is off the NavMesh, still needs a crouch, or the route is too long.
        /// </summary>
        private Vector3[] ShiftedChain(Vector3[] corners, float from, float to, float shift, Vector3 start, Vector3 end, float maxLength)
        {
            var points = new List<Vector3> { start };
            int count = Mathf.Max(1, Mathf.CeilToInt((to - from) / 2f));
            for (int i = 0; i <= count; i++)
            {
                float d = Mathf.Lerp(from, to, count == 0 ? 0f : (float)i / count);
                if (!PointAlong(corners, d, out Vector3 p, out int seg) || !OntoFloor(ref p))
                    return null;
                Vector3 dir = corners[seg + 1] - corners[seg];
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.0001f)
                    continue;
                Vector3 right = Vector3.Cross(Vector3.up, dir.normalized);
                if (!NavMesh.SamplePosition(p + right * shift, out NavMeshHit hit, 0.6f, NavMesh.AllAreas) ||
                    Mathf.Abs(hit.position.y - p.y) > 1.5f || NeedsCrouchAt(hit.position, strict: true))
                    return null;
                points.Add(hit.position);
            }
            points.Add(end);
            var route = new List<Vector3> { start };
            float length = 0f;
            for (int i = 0; i < points.Count - 1; i++)
            {
                Vector3[] leg = PlanPlayerCorners(points[i], points[i + 1]);
                if (leg == null)
                    return null;
                for (int j = 1; j < leg.Length; j++)
                {
                    length += HorizontalDistance(route[route.Count - 1], leg[j]);
                    route.Add(leg[j]);
                }
                if (length >= maxLength)
                    return null;
            }
            return route.ToArray();
        }

        /// <summary>Where the crouch starting at from ends: standing room again, and for the next
        /// 2.5 m (Offense's pipe reads as two crouches a metre apart; going round one only leads
        /// under the other).</summary>
        private float CrouchStretchEnd(Vector3[] corners, float from)
        {
            float end = FlatLength(corners);
            for (int i = 0; i < 20; i++)
            {
                if (!FirstCrouchSpot(corners, out _, out float clear, from, crouch: false))
                    return end;
                if (!FirstCrouchSpot(corners, out _, out float next, clear) || next > clear + 2.5f)
                    return clear;
                from = next;
            }
            return end;
        }

        /// <summary>A point on a straight leg between corners, moved down (or up) onto the floor
        /// there: a long leg over a hill runs metres off the ground.</summary>
        private bool OntoFloor(ref Vector3 point)
        {
            if (!FloorNearLine(point, out float y))
                return false;
            point.y = y;
            return true;
        }

        private static float FlatLength(Vector3[] corners)
        {
            float length = 0f;
            for (int i = 0; i < corners.Length - 1; i++)
                length += HorizontalDistance(corners[i], corners[i + 1]);
            return length;
        }

        /// <summary>The point distance metres along corners (measured flat), and the corner it
        /// follows.</summary>
        private static bool PointAlong(Vector3[] corners, float distance, out Vector3 point, out int segment)
        {
            point = corners.Length > 0 ? corners[0] : Vector3.zero;
            segment = 0;
            float walked = 0f;
            for (int i = 0; i < corners.Length - 1; i++)
            {
                float flat = HorizontalDistance(corners[i], corners[i + 1]);
                if (walked + flat >= distance)
                {
                    point = Vector3.Lerp(corners[i], corners[i + 1], flat > 0.0001f ? (distance - walked) / flat : 0f);
                    segment = i;
                    return true;
                }
                walked += flat;
            }
            return false;
        }

        private bool SpansLink(Vector3[] corners, int fromSeg, int toSeg)
        {
            for (int i = fromSeg; i <= toSeg && i < corners.Length - 1; i++)
            {
                if (IsLinkLeg(corners[i], corners[i + 1]))
                    return true;
            }
            return false;
        }

        /// <summary>A standing player's head would hit something overhead here. Strict: anything
        /// over the whole width of the body, for a way round that must not brush the edge of the
        /// overhang it avoids (Offense: a standing player pushed into a pipe's lip and stuck).</summary>
        private bool NeedsCrouchAt(Vector3 feet, bool strict = false)
        {
            if (strict)
            {
                foreach (Vector3 offset in StrictOverheadOffsets)
                {
                    if (Physics.Raycast(feet + offset + Vector3.up * 0.2f, Vector3.up, STANDING_CLEARANCE - 0.2f, groundMask, QueryTriggerInteraction.Ignore))
                        return true;
                }
                return false;
            }
            int hits = 0;
            for (int i = 0; i < OverheadProbeOffsets.Length; i++)
            {
                if (Physics.Raycast(feet + OverheadProbeOffsets[i] + Vector3.up * 0.2f, Vector3.up, STANDING_CLEARANCE - 0.2f, groundMask, QueryTriggerInteraction.Ignore))
                    hits++;
            }
            return hits >= 3;
        }

        private static readonly Vector3[] StrictOverheadOffsets =
        {
            Vector3.zero, new Vector3(0.4f, 0f, 0f), new Vector3(-0.4f, 0f, 0f), new Vector3(0f, 0f, 0.4f), new Vector3(0f, 0f, -0.4f),
            new Vector3(0.28f, 0f, 0.28f), new Vector3(-0.28f, 0f, 0.28f), new Vector3(0.28f, 0f, -0.28f), new Vector3(-0.28f, 0f, -0.28f)
        };

        // The standing controller is 2.5 m tall, plus its skin.
        private const float STANDING_CLEARANCE = 2.55f;

        /// <summary>The first point along corners, every 0.4 m and from after metres in, where only a
        /// crouched player fits (or, with crouch false, where a standing one does again). Legs off
        /// the mesh and where the player already is are skipped, as in FirstTooLow.</summary>
        private bool FirstCrouchSpot(Vector3[] corners, out Vector3 point, out float at, float after = 0f, Vector3 near = default, float within = float.MaxValue, bool crouch = true, bool strict = false)
        {
            point = Vector3.zero;
            at = 0f;
            float walked = 0f;
            for (int i = 0; i < corners.Length - 1; i++)
            {
                Vector3 a = corners[i];
                Vector3 b = corners[i + 1];
                float flat = HorizontalDistance(a, b);
                bool outOfReach = within < float.MaxValue && HorizontalDistance(a, near) > within && HorizontalDistance(b, near) > within;
                if (outOfReach || IsLinkLeg(a, b))
                {
                    walked += flat;
                    continue;
                }
                int steps = Mathf.Max(1, Mathf.CeilToInt(flat / 0.4f));
                for (int s = 1; s <= steps; s++)
                {
                    float along = walked + flat * s / steps;
                    if (along <= 0.3f || along < after)
                        continue;
                    Vector3 q = Vector3.Lerp(a, b, (float)s / steps);
                    if (!FloorNearLine(q, out float floorY))
                        continue;
                    q.y = floorY;
                    if (NeedsCrouchAt(q, strict) == crouch)
                    {
                        point = q;
                        at = along;
                        return true;
                    }
                }
                walked += flat;
            }
            return false;
        }

        /// <summary>"Quicksand ahead" or "Water ahead" when the next node is in one, once per node.</summary>
        private void UpdateHazardCue()
        {
            if (currentTargetNode == null || hazardCueNode == currentTargetNode)
                return;
            hazardCueNode = currentTargetNode;

            Vector3 ground = currentTargetNode.position - Vector3.up * NODE_GROUND_OFFSET;
            string cue = InQuicksand(ground, out _) ? "Quicksand ahead" : InDeepWater(ground) ? "Water ahead" : null;
            if (cue == null || Time.time - lastHazardCueTime < 8f)
                return;

            // Already in it: nothing new to say.
            if ((cue == "Water ahead" && playerController.isUnderwater) || (cue == "Quicksand ahead" && playerController.isSinking))
                return;

            lastHazardCueTime = Time.time;
            Utilities.SpeakText(cue);
        }
        #endregion

        #region Locked Doors
        private bool HasKey()
        {
            if (playerController == null || playerController.ItemSlots == null)
                return false;
            foreach (var item in playerController.ItemSlots)
            {
                if (item is KeyItem)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The middle of a closed door's doorway and a NavMesh point on each side of it. The door
        /// panel's collider is thin across the doorway, so its thinnest horizontal side is the way
        /// through.
        /// </summary>
        private static bool DoorPassage(DoorLock door, out Vector3 center, out Vector3 sideA, out Vector3 sideB)
        {
            center = sideA = sideB = default;
            Collider collider = door.GetComponent<Collider>();
            if (collider == null)
                return false;

            Bounds bounds = collider.bounds;
            center = bounds.center;
            Vector3 across = bounds.size.x < bounds.size.z ? Vector3.right : Vector3.forward;

            NavMeshHit a, b;
            Vector3 wantA = center - across * 1.2f;
            Vector3 wantB = center + across * 1.2f;
            if (!NavMesh.SamplePosition(wantA, out a, 2.5f, NavMesh.AllAreas) ||
                !NavMesh.SamplePosition(wantB, out b, 2.5f, NavMesh.AllAreas))
                return false;
            if (HorizontalDistance(a.position, wantA) > 0.8f || HorizontalDistance(b.position, wantB) > 0.8f)
                return false;

            sideA = a.position;
            sideB = b.position;
            return true;
        }

        /// <summary>
        /// With a key in the inventory, the shortest route that goes through one locked door,
        /// when it beats the normal route by KEY_ROUTE_MIN_SAVING or the normal route does not
        /// get there at all.
        /// </summary>
        private bool TryKeyRoute(Vector3 start, float normalLength, out Vector3[] toDoor, out Vector3[] fromDoor, out DoorLock door)
        {
            toDoor = fromDoor = null;
            door = null;
            DoorLock[] doors = GetDoors();
            if (doors == null || !HasKey())
                return false;

            float best = normalLength == float.MaxValue ? float.MaxValue : normalLength - KEY_ROUTE_MIN_SAVING;
            foreach (var candidate in doors)
            {
                if (candidate == null || !candidate.isLocked || !DoorPassage(candidate, out _, out Vector3 a, out Vector3 b))
                    continue;

                for (int side = 0; side < 2; side++)
                {
                    Vector3 near = side == 0 ? a : b;
                    Vector3 far = side == 0 ? b : a;
                    Vector3[] first = PlanCorners(start, near);
                    if (first == null)
                        continue;
                    Vector3[] second = PlanCorners(far, currentDestination);
                    if (second == null)
                        continue;

                    float length = PathLength(first) + Vector3.Distance(near, far) + PathLength(second);
                    if (length < best)
                    {
                        best = length;
                        toDoor = first;
                        fromDoor = second;
                        door = candidate;
                    }
                }
            }
            return door != null;
        }

        private enum LockedDoorState
        {
            None,
            Approach,
            Wait
        }

        /// <summary>
        /// Where the walk stands with the locked door on the path: not there yet, walking up to
        /// it, or waiting in front of it for the key. Drops the door once it is unlocked (the
        /// normal door handling opens it), and re-plans without it if the key is gone.
        /// </summary>
        private LockedDoorState UpdateLockedDoor(out Vector3 doorCenter)
        {
            doorCenter = default;
            if (lockedDoorOnPath == null || currentTargetNode == null)
                return LockedDoorState.None;

            if (!lockedDoorOnPath.isLocked)
            {
                Utilities.SpeakText("Unlocked");
                lockedDoorOnPath = null;
                return LockedDoorState.None;
            }

            if (currentTargetNode.index < lockedDoorCrossIndex - 1 || !DoorPassage(lockedDoorOnPath, out doorCenter, out _, out _))
                return LockedDoorState.None;

            if (!HasKey())
            {
                lockedDoorOnPath = null;
                lastReplanTime = -999f;
                Replan("No key. Finding another way.");
                return LockedDoorState.None;
            }

            if (HorizontalDistance(transform.position, doorCenter) > LOCKED_DOOR_STOP_DISTANCE)
                return LockedDoorState.Approach;

            if (!lockedDoorCued)
            {
                lockedDoorCued = true;
                Utilities.SpeakText(playerController.currentlyHeldObjectServer is KeyItem
                    ? "Locked door. Use your key on it."
                    : "Locked door. Switch to your key and use it on the door.");
            }
            return LockedDoorState.Wait;
        }
        #endregion

        private float PathEndGap(NavMeshPath path)
        {
            if (path.corners == null || path.corners.Length == 0)
                return float.MaxValue;
            return Vector3.Distance(path.corners[path.corners.Length - 1], currentDestination);
        }

        /// <summary>The area of the ship's own climb links (ShipLadder, ShipLadder2, the side
        /// panels). The area is unnamed. Its ladders get links of their own (EnsureLadderLinks);
        /// the panels are a 3 m climb only enemies make. The ship's outer rim is baked into the
        /// same area, so it can't be masked out: CalculatePlayerPath switches the links off
        /// instead.</summary>
        public const int SHIP_CLIMB_AREA = 4;

        private static readonly List<OffMeshLink> shipClimbLinks = new List<OffMeshLink>();
        private static readonly List<OffMeshLink> switchedOff = new List<OffMeshLink>();
        private static float shipClimbScanTime = -100f;

        /// <summary>NavMesh.CalculatePath with the ship's climb links switched off. The query is
        /// synchronous, so no enemy ever sees them off; a link an enemy is crossing is left on.</summary>
        public static bool CalculatePlayerPath(Vector3 from, Vector3 to, int mask, NavMeshPath path)
        {
            if (Time.time - shipClimbScanTime > 10f || shipClimbLinks.Exists(l => l == null))
            {
                shipClimbScanTime = Time.time;
                shipClimbLinks.Clear();
                foreach (OffMeshLink link in FindObjectsOfType<OffMeshLink>())
                {
                    if (link.area == SHIP_CLIMB_AREA)
                        shipClimbLinks.Add(link);
                }
            }
            switchedOff.Clear();
            foreach (OffMeshLink link in shipClimbLinks)
            {
                if (link.activated && !link.occupied)
                {
                    link.activated = false;
                    switchedOff.Add(link);
                }
            }
            foreach (OffMeshLink link in refusedLadderLinks)
            {
                if (link != null && link.activated && !link.occupied)
                {
                    link.activated = false;
                    switchedOff.Add(link);
                }
            }
            try
            {
                return NavMesh.CalculatePath(from, to, mask, path);
            }
            finally
            {
                foreach (OffMeshLink link in switchedOff)
                    link.activated = true;
            }
        }

        private static int PlayerAreaMask()
        {
            int mask = NavMesh.AllAreas;
            foreach (string area in new[] { "Jump", "Climb" })
            {
                int index = NavMesh.GetAreaFromName(area);
                if (index >= 0)
                {
                    mask &= ~(1 << index);
                }
            }
            return mask;
        }

        /// <param name="finalLeg">False for the half of a path that ends at a locked door, which
        /// gets no extra node for an off-mesh target.</param>
        private List<Vector3> BuildNodePositions(Vector3[] corners, bool finalLeg = true)
        {
            List<Vector3> positions = new List<Vector3>();
            Vector3 surface = corners[0];
            positions.Add(SnapToGround(surface));
            float sinceLastNode = 0f;

            for (int i = 0; i < corners.Length - 1; i++)
            {
                Vector3 a = corners[i];
                Vector3 b = corners[i + 1];
                Vector3 flat = new Vector3(b.x - a.x, 0f, b.z - a.z);
                float flatLength = flat.magnitude;

                // Vertical segments (off-mesh links, drops) have no ground to walk in between.
                int steps = Mathf.Max(1, Mathf.CeilToInt(flatLength / SURFACE_STEP));
                float stepLength = flatLength / steps;

                for (int s = 1; s <= steps; s++)
                {
                    bool atCorner = s == steps;
                    if (atCorner)
                    {
                        surface = b;
                    }
                    else
                    {
                        Vector3 linePoint = Vector3.Lerp(a, b, (float)s / steps);
                        surface = FollowSurface(new Vector3(linePoint.x, surface.y, linePoint.z), linePoint);
                    }

                    sinceLastNode += stepLength;
                    // Either end of a ladder or jump link stays exactly where it is: pushed off the
                    // ship's hull, the foot of its back ladder ended up a metre out, past the ladder.
                    if (atCorner && (IsLinkLeg(a, b) || (i + 2 < corners.Length && IsLinkLeg(b, corners[i + 2]))))
                    {
                        if (sinceLastNode < CORNER_NODE_MIN_GAP && positions.Count > 1 && !IsLinkLeg(a, b))
                            positions[positions.Count - 1] = SnapToGround(surface);
                        else
                            positions.Add(SnapToGround(surface));
                        sinceLastNode = 0f;
                    }
                    else if (sinceLastNode >= NODE_SPACING || (atCorner && sinceLastNode >= CORNER_NODE_MIN_GAP))
                    {
                        positions.Add(KeepOffEdges(SnapToGround(KeepOffWalls(surface))));
                        sinceLastNode = 0f;
                    }
                }
            }

            // The end of the path always gets a node, even if it falls close to the last one.
            Vector3 end = SnapToGround(corners[corners.Length - 1]);
            if (HorizontalDistance(positions[positions.Count - 1], end) > 0.1f)
            {
                if (HorizontalDistance(positions[positions.Count - 1], end) < CORNER_NODE_MIN_GAP && positions.Count > 1)
                {
                    positions[positions.Count - 1] = end;
                }
                else
                {
                    positions.Add(end);
                }
            }

            if (isOffNavMeshTarget && finalLeg)
            {
                Vector3 finalPos = SnapToGround(offNavMeshTargetPos);
                if (HorizontalDistance(positions[positions.Count - 1], finalPos) > CORNER_NODE_MIN_GAP)
                {
                    positions.Add(finalPos);
                }
            }

            for (int i = 1; i < positions.Count - 1; i++)
            {
                // Ladder ends and jump takeoffs stay exactly where they are.
                if (Mathf.Abs(positions[i].y - positions[i - 1].y) > 1f || Mathf.Abs(positions[i + 1].y - positions[i].y) > 1f)
                    continue;
                positions[i] = KeepBodyClear(positions[i], positions[i - 1], positions[i + 1]);
                positions[i] = KeepHeadClear(positions[i], positions[i - 1], positions[i + 1]);
            }
            WidenClippedLegs(positions);
            SmoothJogs(positions);
            return positions;
        }

        /// <summary>
        /// A leg between two path corners that no one walks: a ladder, a jump or a drop. Steep is
        /// not enough. A flight of stairs is as steep, and so is 54 degree rock the enemy mesh
        /// walks up on Vow; the leg has to leave the mesh. The mesh walk can also arrive under
        /// the far end (the ground below the ship's deck at its ladder), so the height it arrives
        /// at has to match too.
        /// </summary>
        private static bool IsLinkLeg(Vector3 a, Vector3 b)
        {
            float rise = Mathf.Abs(b.y - a.y);
            float flat = HorizontalDistance(a, b);
            if (rise <= Mathf.Max(0.6f, flat * 1.5f) && (flat >= 2.5f || rise <= 1f))
                return false;
            return NavMesh.Raycast(a, b, out NavMeshHit meshWalk, NavMesh.AllAreas) || Mathf.Abs(meshWalk.position.y - b.y) > 0.5f;
        }

        /// <summary>
        /// Pulls each node toward the straight line between its neighbours, as far as the legs
        /// either side stay clear. A node added to round a door edge, or a pair of corners on
        /// either side of a doorway, otherwise leaves a sideways jog the ping swings across.
        /// </summary>
        private void SmoothJogs(List<Vector3> positions)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 1; i < positions.Count - 1; i++)
                {
                    Vector3 a = positions[i - 1];
                    Vector3 node = positions[i];
                    Vector3 b = positions[i + 1];
                    // Ladders, drops and jumps stay put, and so do nodes on a slope.
                    if (Mathf.Abs(node.y - a.y) > 0.3f || Mathf.Abs(b.y - node.y) > 0.3f)
                        continue;
                    Vector3 chord = (a + b) * 0.5f;
                    if (HorizontalDistance(node, chord) < 0.15f)
                        continue;
                    Vector3 down = Vector3.up * NODE_GROUND_OFFSET;
                    bool meshLegs = !NavMesh.Raycast(a - down, node - down, out _, NavMesh.AllAreas) &&
                                    !NavMesh.Raycast(node - down, b - down, out _, NavMesh.AllAreas);
                    float edgeBefore = NavMesh.FindClosestEdge(node - down, out NavMeshHit edgeHit, NavMesh.AllAreas) ? edgeHit.distance : 0f;
                    foreach (float t in SmoothFractions)
                    {
                        Vector3 guess = Vector3.Lerp(node, chord, t);
                        NavMeshHit hit;
                        if (!NavMesh.SamplePosition(guess - Vector3.up * NODE_GROUND_OFFSET, out hit, 0.3f, NavMesh.AllAreas))
                            continue;
                        Vector3 moved = SnapToGround(hit.position);
                        if (Mathf.Abs(moved.y - node.y) > 0.3f || BodyOverlaps(moved) || !LegWalkable(a, moved) || !LegWalkable(moved, b))
                            continue;
                        // Not off the edge of a catwalk: legs that stayed on the mesh still do,
                        // and the node keeps its distance from the mesh's edge.
                        if (meshLegs && (NavMesh.Raycast(a - down, hit.position, out _, NavMesh.AllAreas) ||
                                         NavMesh.Raycast(hit.position, b - down, out _, NavMesh.AllAreas)))
                            continue;
                        if (NavMesh.FindClosestEdge(hit.position, out NavMeshHit movedEdge, NavMesh.AllAreas) &&
                            movedEdge.distance < Mathf.Min(edgeBefore, EDGE_CLEARANCE))
                            continue;
                        positions[i] = moved;
                        break;
                    }
                }
            }
        }

        private readonly Collider[] bodyOverlaps = new Collider[16];

        /// <summary>A player standing at node point would be inside something: an open door's
        /// panel, a pipe on the wall. Closed doors don't count. Knee and waist height only, so a
        /// node in a crawl space, under a rack, isn't moved out of it.</summary>
        private bool BodyOverlaps(Vector3 point, float radius = LEG_BODY_RADIUS + 0.03f)
        {
            Vector3 feet = point - Vector3.up * NODE_GROUND_OFFSET;
            foreach (float h in NodeBodyHeights)
            {
                int count = Physics.OverlapSphereNonAlloc(feet + Vector3.up * h, radius, bodyOverlaps, bodyMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < count; i++)
                {
                    DoorLock door = bodyOverlaps[i].GetComponentInParent<DoorLock>();
                    if (door != null && DoorIsClosed(door))
                        continue;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The nearest spot, within 0.8 m, where a node's body overlaps nothing; first choice one
        /// in clear view of both neighbours, then of the one before. Nearest alone can be the far
        /// side of an open door's panel, a dead end.
        /// </summary>
        private Vector3 KeepBodyClear(Vector3 point, Vector3 previous, Vector3 next)
        {
            if (!BodyOverlaps(point))
                return point;
            // Only close to something (a doorway a metre wide is that close to both sides): move
            // only to a spot in clear view of both neighbours, or a doorway node leaves its
            // doorway and the path zigzags. Inside something (an open door's panel): any spot.
            bool inside = BodyOverlaps(point, 0.3f);
            List<Vector3> candidates = new List<Vector3>();
            foreach (float radius in NodeMoveRadii)
            {
                for (int k = 0; k < 12; k++)
                {
                    Vector3 offset = Quaternion.Euler(0f, k * 30f, 0f) * Vector3.forward * radius;
                    NavMeshHit hit;
                    if (!NavMesh.SamplePosition(point - Vector3.up * NODE_GROUND_OFFSET + offset, out hit, 0.4f, NavMesh.AllAreas))
                        continue;
                    Vector3 candidate = SnapToGround(hit.position);
                    if (Mathf.Abs(candidate.y - point.y) >= 0.6f || BodyOverlaps(candidate))
                        continue;
                    if (LegClear(previous, candidate) && LegClear(candidate, next))
                        return candidate;
                    candidates.Add(candidate);
                }
            }

            if (!inside)
                return point;

            // Nothing in clear view of both: the first spot whose legs can be routed round
            // what is in the way. Which side of an open door's panel to pass is only found
            // this way; the nearest free spot is as often the dead end behind it.
            List<Vector3> trial = new List<Vector3>(8);
            for (int c = 0; c < candidates.Count && c < 12; c++)
            {
                trial.Clear();
                trial.Add(previous);
                trial.Add(candidates[c]);
                trial.Add(next);
                WidenClippedLegs(trial);
                bool clear = true;
                for (int i = 0; i < trial.Count - 1 && clear; i++)
                {
                    clear = LegClear(trial[i], trial[i + 1]);
                }
                if (clear)
                    return candidates[c];
            }
            return candidates.Count > 0 ? candidates[0] : point;
        }

        private const float LEG_BODY_RADIUS = 0.42f;

        private readonly RaycastHit[] legHits = new RaycastHit[16];

        /// <summary>A body walking from node a to node b, parallel to the ground between them,
        /// touches nothing at knee or chest height. A closed door doesn't count, since it gets
        /// opened; an open one does, its panel standing out into the room.</summary>
        private bool LegClear(Vector3 a, Vector3 b)
        {
            Vector3 from = a - Vector3.up * NODE_GROUND_OFFSET;
            Vector3 to = b - Vector3.up * NODE_GROUND_OFFSET - from;
            if (new Vector2(to.x, to.z).sqrMagnitude < 0.0001f)
                return true;
            foreach (float h in BodyLineHeights)
            {
                int count = Physics.SphereCastNonAlloc(from + Vector3.up * h, LEG_BODY_RADIUS, to.normalized, legHits, to.magnitude, bodyMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < count; i++)
                {
                    // Overlapping at the start (distance 0): the node itself sits that close, and
                    // the leg can't be moved off it.
                    if (legHits[i].distance <= 0f)
                        continue;
                    DoorLock door = legHits[i].collider.GetComponentInParent<DoorLock>();
                    if (door != null && DoorIsClosed(door))
                        continue;
                    return false;
                }
            }
            return true;
        }

        /// <summary>The path within maxDistance of the player passes somewhere without room to
        /// stand: the next server rack, a couple of metres round the corner from the last. Sampled
        /// along the legs, since nodes sit either side of a rack, never under it.</summary>
        private bool LowCeilingAhead(float maxDistance)
        {
            if (currentTargetNode == null)
                return false;
            Vector3 from = transform.position + Vector3.up * NODE_GROUND_OFFSET;
            float distance = 0f;
            for (int i = currentTargetNode.index; i < pathNodes.Count && distance < maxDistance; i++)
            {
                Vector3 node = pathNodes[i].position;
                float leg = HorizontalDistance(from, node);
                for (float along = 0.5f; along <= leg && distance + along <= maxDistance; along += 0.5f)
                {
                    if (!HeadRoomAt(Vector3.Lerp(from, node, along / leg)))
                        return true;
                }
                distance += leg;
                from = node;
            }
            return false;
        }

        private static readonly float[] HeadMoveRadii = { 0.35f, 0.6f, 0.85f, 1.1f };

        /// <summary>
        /// A node under something a standing player hits their head on (the flared top of a
        /// silo) moves out from under it, when there is a spot nearby walkable at full height
        /// from both neighbours. Under a real crawl space there is none, and the node stays.
        /// </summary>
        private Vector3 KeepHeadClear(Vector3 point, Vector3 previous, Vector3 next)
        {
            if (HeadRoomAt(point) || !HeadRoomAt(previous) || !HeadRoomAt(next))
                return point;
            foreach (float radius in HeadMoveRadii)
            {
                for (int k = 0; k < 12; k++)
                {
                    Vector3 offset = Quaternion.Euler(0f, k * 30f, 0f) * Vector3.forward * radius;
                    NavMeshHit hit;
                    if (!NavMesh.SamplePosition(point - Vector3.up * NODE_GROUND_OFFSET + offset, out hit, 0.4f, NavMesh.AllAreas))
                        continue;
                    Vector3 candidate = SnapToGround(hit.position);
                    if (Mathf.Abs(candidate.y - point.y) < 0.6f && HeadRoomAt(candidate) && !BodyOverlaps(candidate) &&
                        LegWalkable(previous, candidate) && LegWalkable(candidate, next))
                        return candidate;
                }
            }
            return point;
        }

        private const float HEAD_SWEEP_HEIGHT = 2.2f;
        private const float HEAD_SWEEP_RADIUS = 0.35f;

        /// <summary>Room for a standing player's head at node point.</summary>
        private bool HeadRoomAt(Vector3 point)
        {
            Vector3 head = point - Vector3.up * NODE_GROUND_OFFSET + Vector3.up * HEAD_SWEEP_HEIGHT;
            return !Physics.CheckSphere(head, HEAD_SWEEP_RADIUS, groundMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// A leg a standing player can walk: clear at knee and chest, and, between two nodes with
        /// room to stand, at head height too. The flared top of a silo, 2.4 m up, stops a
        /// standing player dead. A leg into a crawl space starts or ends without head room, and
        /// is left to the crawl handling.
        /// </summary>
        private bool LegWalkable(Vector3 a, Vector3 b)
        {
            if (!LegClear(a, b))
                return false;
            if (!HeadRoomAt(a) || !HeadRoomAt(b))
                return true;
            Vector3 from = a - Vector3.up * NODE_GROUND_OFFSET + Vector3.up * HEAD_SWEEP_HEIGHT;
            Vector3 to = b - a;
            if (new Vector2(to.x, to.z).sqrMagnitude < 0.0001f)
                return true;
            return !Physics.SphereCast(from, HEAD_SWEEP_RADIUS, to.normalized, out _, to.magnitude, groundMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// The NavMesh is baked for enemies and leaves out wall pipes and trim, so a leg out of a
        /// doorway corner can run through the edge of the frame for a player's wider body. Such
        /// a leg gets a node in its middle, moved sideways until both halves are clear.
        /// </summary>
        private void WidenClippedLegs(List<Vector3> positions)
        {
            int added = 0;
            int chain = 0;
            for (int i = 0; i < positions.Count - 1 && added < 24; i++)
            {
                Vector3 a = positions[i];
                Vector3 b = positions[i + 1];
                Vector3 flat = new Vector3(b.x - a.x, 0f, b.z - a.z);
                // Ladders, drops and jumps are not walked.
                if (flat.magnitude < 0.8f || Mathf.Abs(b.y - a.y) > 1f || LegWalkable(a, b))
                {
                    chain = 0;
                    continue;
                }

                Vector3 side = Vector3.Cross(Vector3.up, flat).normalized;
                Vector3 mid = (a + b) * 0.5f;
                Vector3? firstHalf = null;
                bool done = false;
                foreach (float offset in LegWidenOffsets)
                {
                    NavMeshHit hit;
                    if (!NavMesh.SamplePosition(mid - Vector3.up * NODE_GROUND_OFFSET + side * offset, out hit, 0.5f, NavMesh.AllAreas))
                        continue;
                    Vector3 candidate = SnapToGround(hit.position);
                    if (Mathf.Abs(candidate.y - mid.y) > 0.6f || BodyOverlaps(candidate) || !LegWalkable(a, candidate))
                        continue;
                    if (LegWalkable(candidate, b))
                    {
                        positions.Insert(i + 1, candidate);
                        added++;
                        i++;
                        done = true;
                        break;
                    }
                    if (firstHalf == null)
                        firstHalf = candidate;
                }

                // Round something with a corner in the way of either half (an open door's panel
                // across a doorway): a node clearing the first half, then the rest from there.
                if (done)
                {
                    chain = 0;
                }
                else if (firstHalf.HasValue && chain < 3 && HorizontalDistance(firstHalf.Value, b) > 0.8f)
                {
                    positions.Insert(i + 1, firstHalf.Value);
                    added++;
                    chain++;
                }
                else
                {
                    chain = 0;
                }
            }
        }

        /// <summary>
        /// The NavMesh point under or over guess, where guess carries the previous step's height.
        /// A half-metre step changes the height by well under the search radius on anything
        /// walkable, so the nearest mesh point is the surface the player would be walking on.
        /// </summary>
        private static Vector3 FollowSurface(Vector3 guess, Vector3 linePoint)
        {
            NavMeshHit hit;
            if (NavMesh.SamplePosition(guess, out hit, 1.5f, NavMesh.AllAreas))
            {
                return hit.position;
            }
            if (NavMesh.SamplePosition(linePoint, out hit, 2f, NavMesh.AllAreas))
            {
                return hit.position;
            }
            return guess;
        }

        /// <summary>
        /// Pushes a node away from the NavMesh edge. Corners in particular sit right on it, as
        /// close to the wall as an enemy's radius allows, and a player cutting that corner
        /// catches the wall or the railing beside it. In a corridor narrower than twice the
        /// clearance the push stops wherever it gets furthest from both sides.
        /// </summary>
        private static Vector3 KeepOffWalls(Vector3 point)
        {
            NavMeshHit edge;
            if (!NavMesh.FindClosestEdge(point, out edge, NavMesh.AllAreas) || edge.distance >= WALL_CLEARANCE)
                return point;

            Vector3 away = point - edge.position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.0001f)
            {
                away = new Vector3(edge.normal.x, 0f, edge.normal.z);
            }
            if (away.sqrMagnitude < 0.0001f)
                return point;

            Vector3 candidate = point + away.normalized * (WALL_CLEARANCE - edge.distance);
            NavMeshHit moved;
            if (!NavMesh.SamplePosition(candidate, out moved, 0.5f, NavMesh.AllAreas))
                return point;

            NavMeshHit newEdge;
            if (NavMesh.FindClosestEdge(moved.position, out newEdge, NavMesh.AllAreas) && newEdge.distance <= edge.distance)
                return point;

            return moved.position;
        }

        /// <summary>
        /// Moves a NavMesh point onto the collider the player would stand on there, plus the node
        /// sphere's radius so the sphere rests on the ground instead of half inside it.
        /// </summary>
        private Vector3 SnapToGround(Vector3 meshPoint)
        {
            if (NearestGroundY(meshPoint, out float groundY) <= SOLID_GROUND_TOLERANCE)
                return new Vector3(meshPoint.x, groundY + NODE_GROUND_OFFSET, meshPoint.z);

            // Nothing a player can stand on here. Some NavMesh is baked on enemy-only colliders
            // (layer NavigationSurface, which players pass through): girder rooms bake a 1.9 m
            // beam over a 0.7 m girder, so the mesh overhangs the real girder by half a metre on
            // each side. Move the node to the nearest real floor at the same height.
            for (float radius = 0.25f; radius <= 1.01f; radius += 0.25f)
            {
                float bestOffset = float.MaxValue;
                Vector3 best = Vector3.zero;
                for (int a = 0; a < 12; a++)
                {
                    float angle = a * Mathf.PI / 6f;
                    Vector3 probePoint = meshPoint + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                    float offset = NearestGroundY(probePoint, out float y);
                    if (offset <= SOLID_GROUND_TOLERANCE && offset < bestOffset)
                    {
                        bestOffset = offset;
                        best = new Vector3(probePoint.x, y + NODE_GROUND_OFFSET, probePoint.z);
                    }
                }
                if (bestOffset < float.MaxValue)
                    return best;
            }

            // Over a gap (or a link with no floor in between): keep the mesh point.
            NearestGroundY(meshPoint, out groundY);
            return new Vector3(meshPoint.x, groundY + NODE_GROUND_OFFSET, meshPoint.z);
        }

        /// <summary>
        /// Centres a node on narrow floor: rings of rays round it find where the real floor
        /// stops, and the node moves away from that side. KeepOffWalls works on the NavMesh,
        /// which overhangs a factory girder; this works on what a player stands on.
        /// </summary>
        private Vector3 KeepOffEdges(Vector3 node)
        {
            Vector3 ground = node - Vector3.up * NODE_GROUND_OFFSET;
            if (NearestGroundY(ground, out _) > SOLID_GROUND_TOLERANCE)
                return node; // Over a gap: nothing to centre on.

            for (int pass = 0; pass < 3; pass++)
            {
                Vector3 push = Vector3.zero;
                int missing = 0;
                for (int a = 0; a < 12; a++)
                {
                    float angle = a * Mathf.PI / 6f;
                    Vector3 dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                    Vector3 sample = ground + dir * EDGE_CLEARANCE;
                    if (NearestGroundY(sample, out float y) > 0.35f || Mathf.Abs(y - ground.y) > 0.35f)
                    {
                        push -= dir;
                        missing++;
                    }
                }
                // Floor all round, or none to speak of on any side (a lone pillar top): leave it.
                if (missing == 0 || missing >= 10 || push.sqrMagnitude < 0.01f)
                    break;

                Vector3 moved = ground + push.normalized * 0.12f;
                if (NearestGroundY(moved, out float movedY) > 0.35f)
                    break;
                ground = new Vector3(moved.x, movedY, moved.z);
            }
            return ground + Vector3.up * NODE_GROUND_OFFSET;
        }

        // Nodes are kept this far from where the real floor stops, room for the player's 0.4 m
        // radius; on a 0.7 m girder that lands them on its centre line.
        private const float EDGE_CLEARANCE = 0.45f;

        // A floor this far above or below the NavMesh still counts as the floor it was baked from.
        private const float SOLID_GROUND_TOLERANCE = 0.8f;

        /// <summary>The player-solid surface under point nearest its height; returns how far off
        /// that is (MaxValue when there is none), with groundY = point.y in that case.</summary>
        private float NearestGroundY(Vector3 point, out float groundY)
        {
            int hitCount = Physics.RaycastNonAlloc(point + Vector3.up * GROUND_RAY_UP, Vector3.down,
                groundHitCache, GROUND_RAY_LENGTH, groundMask, QueryTriggerInteraction.Ignore);

            // The surface nearest the mesh height, so a pipe or ledge overhead never wins over
            // the floor the mesh was baked from.
            float best = float.MaxValue;
            groundY = point.y;
            for (int i = 0; i < hitCount; i++)
            {
                if (!IsFloorHit(groundHitCache[i]))
                    continue;
                float offset = Mathf.Abs(groundHitCache[i].point.y - point.y);
                if (offset < best)
                {
                    best = offset;
                    groundY = groundHitCache[i].point.y;
                }
            }
            return best;
        }

        private void CreateNode(Vector3 position, int index)
        {
            PathNode node = new PathNode(position, index);
            GameObject nodeObj = GetNodeFromPool();
            nodeObj.name = $"PathNode_{index}";
            nodeObj.transform.position = position;
            nodeObj.GetComponent<MeshRenderer>().enabled = ConfigManager.EnableVisualMarkers.Value;

            node.nodeObject = nodeObj;
            node.audioSource = nodeObj.GetComponent<AudioSource>();
            pathNodes.Add(node);
        }

        private void ClearPathNodes()
        {
            foreach (var node in pathNodes)
            {
                ReturnNodeToPool(node.nodeObject);
            }
            pathNodes.Clear();
            currentTargetNode = null;
        }

        private void Replan(string announcement)
        {
            if (Time.time - lastReplanTime < REPLAN_COOLDOWN || ElevatorCrossingInProgress)
                return;

            lastReplanTime = Time.time;
            if (currentTargetObject != null)
            {
                ResolveDestination(currentTargetObject, out currentDestination);
            }

            if (BuildPath())
            {
                if (announcement != null)
                {
                    Utilities.SpeakText(announcement);
                }
            }
            else
            {
                StopPathfinding(playSound: false);
                Utilities.SpeakText("Lost the path. Pathfinding stopped.");
            }
        }
        #endregion

        #region Node Progress
        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private bool IsWithin(PathNode node, float radius)
        {
            Vector3 feet = transform.position;
            return HorizontalDistance(feet, node.position) <= radius &&
                   Mathf.Abs(node.position.y - NODE_GROUND_OFFSET - feet.y) <= NODE_REACH_HEIGHT;
        }

        private bool IsLastNode(PathNode node) => node.index == pathNodes.Count - 1;

        private const float NODE_CLOSE_RADIUS = 0.6f;
        private const float MANUAL_MIN_CROUCH_TIME = 0.8f;

        /// <summary>Nothing at body height between the player and point, for a body a little
        /// narrower than the player's (the capsule slides past what it only brushes).</summary>
        /// <summary>The chest-high line alone, for seeing a gap's run-up start from a girder.</summary>
        private bool ClearChestLine(Vector3 point)
        {
            Vector3 chest = transform.position + Vector3.up * 1.2f;
            Vector3 to = point - Vector3.up * NODE_GROUND_OFFSET + Vector3.up * 1.2f - chest;
            if (to.sqrMagnitude < 0.0001f)
                return true;
            return !Physics.SphereCast(chest, 0.3f, to.normalized, out _, to.magnitude, groundMask, QueryTriggerInteraction.Ignore);
        }

        private bool ClearWalkLine(Vector3 point)
        {
            Vector3 feet = transform.position;
            Vector3 to = point - Vector3.up * NODE_GROUND_OFFSET - feet;
            if (to.sqrMagnitude < 0.0001f)
                return true;
            // Knee and chest, parallel to the ground between the two: a chest-high line alone
            // passes over the low wall between two flights of a switchback stair.
            foreach (float h in WalkLineHeights)
            {
                if (Physics.SphereCast(feet + Vector3.up * h, 0.3f, to.normalized, out _, to.magnitude, groundMask, QueryTriggerInteraction.Ignore))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Marks reached nodes and moves the target forward. Shared by both modes.
        /// Returns false once the destination has been reached.
        /// </summary>
        private bool UpdateNodeProgress()
        {
            if (pathNodes.Count == 0)
                return false;

            // In the air over a gap nothing is reached yet: arriving mid-jump would drop the
            // jump's input, and the landing marks what was flown past.
            if (gapJumpInProgress)
                return true;

            // Skipping ahead: when a later node is in reach, everything before it is done.
            for (int i = pathNodes.Count - 1; i >= 0; i--)
            {
                PathNode node = pathNodes[i];
                if (node.isReached)
                    break;

                float radius = IsLastNode(node) ? stoppingRadius : NODE_REACH_RADIUS;
                if (IsWithin(node, radius))
                {
                    // A manual player heads straight for whatever pings next. Moving the ping on
                    // from a node still a metre away cuts the corner, into the end of a wall or
                    // stair rail; so only once the next node is in clear view, or this one is
                    // close.
                    // Auto mode the same: a corner by a doorway reached at 1.2 m leaves the wall
                    // between the player and the next node, and it bounces off the door frame.
                    if (!IsLastNode(node) && !IsWithin(node, NODE_CLOSE_RADIUS) &&
                        !(currentMode == PathfindingMode.Manual
                            ? ClearWalkLine(pathNodes[node.index + 1].position)
                            : BodyLineClear(pathNodes[node.index + 1].position)))
                        continue;
                    MarkReachedThrough(node, announce: currentMode == PathfindingMode.Manual);
                    break;
                }
            }

            // Standing on something right over a node (a table jumped onto in the mansion, a rock
            // 5 m up on Offense): too high to count as there, and nothing to steer toward either.
            // Count it, and the next node leads off whatever this is. Up to a drop that is safe.
            for (int i = 0; i < pathNodes.Count - 1; i++)
            {
                PathNode node = pathNodes[i];
                if (node.isReached)
                    continue;
                float above = transform.position.y - (node.position.y - NODE_GROUND_OFFSET);
                if (playerController.thisController.isGrounded && above > NODE_REACH_HEIGHT && above < 8f &&
                    HorizontalDistance(transform.position, node.position) < NODE_CLOSE_RADIUS)
                {
                    MarkReachedThrough(node, announce: false);
                }
                break;
            }

            // Auto mode steers past nodes toward one further on, so a node it went by without
            // touching counts once the player is beyond it along the path.
            if (currentMode == PathfindingMode.Auto)
            {
                for (int i = 0; i < pathNodes.Count - 1; i++)
                {
                    PathNode node = pathNodes[i];
                    if (node.isReached)
                        continue;
                    // Beyond it along the next leg yet still behind the door frame it turns
                    // round is not past it.
                    if (!PassedNode(node, pathNodes[i + 1]) ||
                        (!IsWithin(node, NODE_CLOSE_RADIUS) && !BodyLineClear(pathNodes[i + 1].position)))
                        break;
                    MarkReachedThrough(node, announce: false);
                }
            }

            if (pathNodes[pathNodes.Count - 1].isReached)
            {
                OnReachedDestination();
                return false;
            }

            FindNextTargetNode();
            return true;
        }

        private bool PassedNode(PathNode node, PathNode next)
        {
            // The doorway of a locked door is not passed until it is unlocked.
            if (lockedDoorOnPath != null && lockedDoorOnPath.isLocked && node.index >= lockedDoorCrossIndex - 1)
                return false;
            if (HeldByElevator(node))
                return false;

            Vector3 feet = transform.position;
            Vector3 along = next.position - node.position;
            along.y = 0f;
            if (along.sqrMagnitude < 0.01f)
                return false;

            Vector3 offset = feet - node.position;
            offset.y = 0f;

            // Past it along the way in as well as the way out. With only the outgoing side, a
            // short sideways step after a node (along the far catwalk of a girder room) counted
            // the player as past that node from six metres short of it, on the girder.
            if (node.index > 0)
            {
                Vector3 incoming = node.position - pathNodes[node.index - 1].position;
                incoming.y = 0f;
                if (incoming.sqrMagnitude > 0.01f && Vector3.Dot(offset, incoming.normalized) < -0.5f)
                    return false;
            }

            // Wide enough for a corner cut by the look-ahead steering; a node left behind unmarked
            // would pull the aim back to it.
            return Vector3.Dot(offset, along.normalized) > 0f && offset.magnitude < AIM_LOOKAHEAD_MAX &&
                   Mathf.Abs(node.position.y - NODE_GROUND_OFFSET - feet.y) <= NODE_REACH_HEIGHT;
        }

        private void MarkReachedThrough(PathNode node, bool announce)
        {
            // Not past the elevator's stop before the ride.
            if (HeldByElevator(node))
            {
                if (elevatorCrossIndex - 2 < 0)
                    return;
                node = pathNodes[elevatorCrossIndex - 2];
            }
            for (int i = 0; i <= node.index; i++)
            {
                if (!pathNodes[i].isReached)
                {
                    pathNodes[i].isReached = true;
                    SetNodeColor(pathNodes[i], Color.green);
                }
            }

            if (announce && audioSource != null && clickSound != null)
            {
                audioSource.PlayOneShot(clickSound, 0.7f);
            }
        }

        private void FindNextTargetNode()
        {
            PathNode next = null;
            foreach (var node in pathNodes)
            {
                if (!node.isReached)
                {
                    next = node;
                    break;
                }
            }

            if (next != null && next != currentTargetNode)
            {
                currentTargetNode = next;
                nodeAudioPingTimer = Mathf.Min(nodeAudioPingTimer, NODE_AUDIO_PING_INTERVAL);
                HighlightNodes();
            }
        }

        private void HighlightNodes()
        {
            foreach (var node in pathNodes)
            {
                SetNodeColor(node, node == currentTargetNode ? Color.yellow : node.isReached ? Color.green : Color.cyan);
            }
        }

        private static void SetNodeColor(PathNode node, Color color)
        {
            if (node.nodeObject == null) return;
            MeshRenderer renderer = node.nodeObject.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.material.color = color;
            }
        }

        private void OnReachedDestination()
        {
            if (hasAnnouncedDestinationReached)
                return;

            hasAnnouncedDestinationReached = true;
            string name = GetTargetName();
            bool partial = pathIsPartial && currentTargetObject != null &&
                           Vector3.Distance(transform.position, currentTargetObject.transform.position) > stoppingRadius * 2f;
            bool wasAuto = currentMode == PathfindingMode.Auto;
            // Arrived crouched after being told to crouch: say when standing is possible, since
            // the stand-up cue stops with pathfinding.
            bool sayStand = !wasAuto && manualCrouchCued && playerController != null && playerController.isCrouching &&
                            Probe != null && Probe.RoomToStand(transform.forward, 0f);
            StopPathfinding(playSound: true);
            if (wasAuto && currentTargetObject != null)
            {
                StartCoroutine(FaceTarget(currentTargetObject));
            }

            // currentTargetObject is kept so the player can still look at it afterwards.
            Utilities.SpeakText((partial ? $"Cannot get any closer to {name}" : $"Reached {name}") + (sayStand ? ". Stand up" : ""));
            LACore.Instance?.MarkReachedAnnounced(currentTargetObject);
        }

        private string GetTargetName()
        {
            if (currentTargetObject == null)
                return "destination";
            return LACore.Instance != null ? LACore.Instance.GetReachedName(currentTargetObject) : currentTargetObject.name;
        }
        #endregion

        #region Crouch Step Offset
        private float defaultStepOffset = -1f;
        private float standUpPendingUntil;
        // How far ahead the way must be clear at full height before auto mode stands up again.
        private const float STAND_UP_LOOKAHEAD = 1.6f;

        /// <summary>
        /// Crouched under something low (a server rack is 1.79 m over the floor, the crouched
        /// player 1.5 m tall), the CharacterController's step offset snags: each move first lifts
        /// the capsule by the step offset, the lifted top meets the overhang's front face, and
        /// at walking speed the player stops dead in front of a gap they fit through. A smaller
        /// step offset while a ceiling is that close lets them slide under; it comes back as
        /// soon as the head is clear. Every player, not only while pathfinding.
        /// </summary>
        private void UpdateCrouchStepOffset()
        {
            if (playerController == null || playerController.thisController == null)
                return;
            CharacterController cc = playerController.thisController;
            if (defaultStepOffset < 0f)
                defaultStepOffset = cc.stepOffset;

            if (Time.time < standUpPendingUntil && playerController.isCrouching && !isPathfinding)
            {
                playerController.Crouch(false);
                if (!playerController.isCrouching)
                    standUpPendingUntil = 0f;
            }

            float wanted = defaultStepOffset;
            if (playerController.isCrouching)
            {
                // Ahead and overhead: the front face snags before the player is under it.
                Vector3 head = transform.position + Vector3.up * (cc.center.y + cc.height * 0.5f);
                Vector3 forward = playerController.thisController.velocity;
                forward.y = 0f;
                forward = forward.sqrMagnitude > 0.01f ? forward.normalized : transform.forward;
                float clearance = float.MaxValue;
                foreach (Vector3 point in new[] { head, head + forward * (cc.radius + 0.3f) })
                {
                    if (Physics.Raycast(point - Vector3.up * 0.1f, Vector3.up, out RaycastHit ceiling, defaultStepOffset + 0.3f,
                            StartOfRound.Instance.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore))
                    {
                        clearance = Mathf.Min(clearance, ceiling.distance - 0.1f);
                    }
                }
                if (clearance < defaultStepOffset + cc.skinWidth)
                    wanted = Mathf.Clamp(clearance - cc.skinWidth - 0.02f, 0.05f, defaultStepOffset);
            }
            if (!Mathf.Approximately(cc.stepOffset, wanted))
                cc.stepOffset = wanted;
        }
        #endregion

        #region Manual Mode
        private void UpdateManualAudio()
        {
            if (currentTargetNode == null)
                return;

            nodeAudioPingTimer -= Time.deltaTime;
            if (nodeAudioPingTimer <= 0f)
            {
                PingCurrentNode();
                nodeAudioPingTimer = NODE_AUDIO_PING_INTERVAL;
            }
        }

        /// <summary>
        /// Tells a manual-mode player what the auto mode would do: "Jump" just before a ledge they
        /// can clear, and which way to step when they are pushing into something they cannot.
        /// </summary>
        private void UpdateManualObstacleCues()
        {
            ObstacleProbe probeNow = Probe;
            if (probeNow == null || currentTargetNode == null || !CanSteer())
                return;
            // At the elevator its own cues do the talking; walking into the car's wall on the way
            // down is not "blocked", and the pillars and button panels round its doors are not
            // something to crouch under.
            if (elevatorOnPath != null &&
                ((!elevatorRideDone && currentTargetNode.index >= elevatorCrossIndex - 1) || NearElevator()))
                return;

            Vector3 toTarget = currentTargetNode.position - transform.position;
            if (ManualGapGuide(out Vector3 gapGuide))
                toTarget = gapGuide - transform.position;
            toTarget.y = 0f;

            probeTimer -= Time.deltaTime;
            if (probeTimer <= 0f)
            {
                probeTimer = 0.1f;
                bool dropAhead = currentTargetNode.position.y - NODE_GROUND_OFFSET < transform.position.y - 1f;
                manualProbe = probeNow.Assess(toTarget, MANUAL_JUMP_CUE_DISTANCE, dropAhead);

                Vector3 nodeGround = currentTargetNode.position - Vector3.up * NODE_GROUND_OFFSET;
                manualLadder = toTarget.magnitude < LADDER_SEARCH_RADIUS ? LadderForStep(nodeGround, out manualLadderDown) : null;

                bool ladderNear = manualLadder != null &&
                                  HorizontalDistance(transform.position, LadderMountPoint(manualLadder, manualLadderDown)) < 5f;
                if (climbCueNode != currentTargetNode &&
                    (ladderNear || (toTarget.magnitude < 4f && probeNow.IsClimbLink(nodeGround))))
                {
                    climbCueNode = currentTargetNode;
                    if (manualLadder == null && carvedSpots.Count < MAX_CARVED_SPOTS)
                    {
                        RememberClimb(nodeGround);
                        lastReplanTime = -999f;
                        Replan("Too high to climb here. Finding another way.");
                        return;
                    }
                    Utilities.SpeakText(manualLadder == null
                        ? "Too high to climb here. Press the pathfind key to let auto mode take you up."
                        : manualLadderDown
                            ? $"Ladder down {DescribeDirection(LadderMountPoint(manualLadder, true))}. Walk to it to climb down."
                            : $"Ladder {DescribeDirection(manualLadder.ladderHorizontalPosition.position)}. Walk into it to climb.");
                }
            }

            // Locked door on a key route: say so once in front of it, and when the key is in hand
            // turn the camera onto the door so using the key hits it.
            if (UpdateLockedDoor(out Vector3 doorCenter) == LockedDoorState.Wait &&
                playerController.currentlyHeldObjectServer is KeyItem)
            {
                Utilities.LookAtPoint(doorCenter);
            }

            bool pushing = playerController.moveInputVector.sqrMagnitude > 0.04f;

            // Grabbing a ladder means aiming the camera at it, which a blind player cannot do
            // reliably. Walking into the one the path uses grabs it; the game's own climb input
            // takes over from there.
            // Interact on a ladder already in use lets go of it, hence the climbing check.
            if (manualLadder != null && pushing && !playerController.isClimbingLadder && Time.time >= nextLadderTry)
            {
                Vector3 toLadder = LadderMountPoint(manualLadder, manualLadderDown) - transform.position;
                toLadder.y = 0f;
                if (toLadder.magnitude <= LADDER_USE_DISTANCE)
                {
                    if (!CanUseTrigger(manualLadder))
                    {
                        SayCannotClimb(manualLadder);
                        return;
                    }
                    nextLadderTry = Time.time + 2f;
                    manualLadder.Interact(transform);
                    StartedLadder(manualLadder);
                    if (playerController.isClimbingLadder || playerController.inSpecialInteractAnimation)
                    {
                        // A ladder taken from the top is climbed down with the back key; holding
                        // forward climbs straight back off it.
                        Utilities.SpeakText(manualLadderDown ? "Climbing down. Hold back to go down." : "Climbing");
                    }
                    return;
                }
            }

            // Crouching is a toggle, so whoever was told to crouch also needs telling when the
            // way is clear at full height again.
            // The cue comes before the key press, so only standing up after crouching ends it.
            bool crouching = playerController.isCrouching;
            if (manualWasCrouching && !crouching)
            {
                manualCrouchCued = false;
            }
            if (crouching && !manualWasCrouching)
            {
                manualCrouchedAt = Time.time;
            }
            manualWasCrouching = crouching;
            // Crouched under something low (cued or not): say when there is room to stand again.
            // RoomToStand checks the standing capsule itself, so a low but standable ceiling
            // (vents, factory halls) still gets the cue.
            Vector3 ahead = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : transform.forward;
            if (crouching && Probe != null && !Probe.RoomToStand(ahead, 0f))
            {
                manualCrouchCued = true;
            }
            // Not straight after crouching: under the edge of the ship's catwalk the ground drops
            // away within a step, and "Crouch" then "Stand up" a moment later is a bob.
            if (crouching && manualCrouchCued && !crawl.active && manualProbe.kind != ObstacleProbe.Kind.Crouch &&
                Time.time - manualCrouchedAt >= MANUAL_MIN_CROUCH_TIME && !LowCeilingAhead(4.5f) &&
                Probe != null && Probe.RoomToStand(ahead, 1f))
            {
                manualCrouchCued = false;
                Utilities.SpeakText("Stand up");
            }
            Vector3 velocity = playerController.thisController.velocity;
            velocity.y = 0f;
            bool headingThere = velocity.magnitude > 0.5f && toTarget.sqrMagnitude > 0.0001f &&
                                Vector3.Dot(velocity.normalized, toTarget.normalized) > 0.5f;

            if (headingThere && playerController.thisController.isGrounded && !gapJumpInProgress &&
                Time.time - lastJumpCueTime > MANUAL_JUMP_CUE_COOLDOWN)
            {
                // Not crouched under something: a jump there only hits the ceiling.
                if (manualProbe.kind == ObstacleProbe.Kind.Jumpable && !crawl.active &&
                    !(playerController.isCrouching && Probe != null && !Probe.RoomToStand(toTarget.normalized, 0f)))
                {
                    lastJumpCueTime = Time.time;
                    Vector3 flatTo = new Vector3(toTarget.x, 0f, toTarget.z);
                    if (flatTo.sqrMagnitude > 0.0001f && NarrowLandingOverDrop(flatTo.normalized))
                    {
                        // Over a railing onto a beam with a pit either side (March's girder room):
                        // a hop a few degrees off line falls 22 m. Not a way to send anyone.
                        narrowLandingReplans = HorizontalDistance(transform.position, narrowLandingPosition) < 4f ? narrowLandingReplans + 1 : 1;
                        narrowLandingPosition = transform.position;
                        if (narrowLandingReplans >= 3)
                        {
                            StopPathfinding(playSound: false);
                            Utilities.SpeakText("Narrow landing over a drop. No safe way on from here. Pathfinding stopped.");
                            return;
                        }
                        RememberBlockedSpot(flatTo);
                        lastReplanTime = -999f;
                        Replan("Narrow landing over a drop. Finding another way.");
                        return;
                    }
                    // The game ignores jump while crouched.
                    Utilities.SpeakText(playerController.isCrouching ? "Stand up and jump" : "Jump");
                }
                // Not at the foot of a ladder: climbing stands the player straight back up.
                else if (manualProbe.kind == ObstacleProbe.Kind.Crouch && !playerController.isCrouching && !crawl.active &&
                         !LadderJustAhead() &&
                         !(manualLadder != null && HorizontalDistance(transform.position, LadderMountPoint(manualLadder, manualLadderDown)) < 2f))
                {
                    lastJumpCueTime = Time.time;
                    manualCrouchCued = true;
                    // Say what it is in the same breath, so the crawl cue does not follow it.
                    Vector3 flat = new Vector3(toTarget.x, 0f, toTarget.z);
                    bool crawlSpace = flat.sqrMagnitude > 0.0001f && FindCrawlLine(flat.normalized, out _, out _);
                    if (crawlSpace)
                    {
                        lastCrawlCueTime = Time.time;
                        lastCrawlCuePosition = transform.position;
                    }
                    Utilities.SpeakText(crawlSpace ? "Crouch, crawl space" : "Crouch");
                }
            }

            // Blocked: pushing a movement key and not getting anywhere.
            if (!pushing || HorizontalDistance(transform.position, manualStuckPosition) > STUCK_MIN_PROGRESS)
            {
                manualStuckPosition = transform.position;
                manualStuckTime = Time.time;
                return;
            }

            if (Time.time - manualStuckTime < STUCK_CHECK_INTERVAL ||
                Time.time - lastBlockedCueTime < MANUAL_BLOCKED_CUE_COOLDOWN)
                return;

            lastBlockedCueTime = Time.time;
            manualStuckTime = Time.time;

            // The same cue again in the same place has not worked: say something else, and in
            // the end look for another way.
            // Counted from where the trouble started: stepping left and right as told moves the
            // player a metre or two each time, and on Artifice that reset the count for ever.
            if (blockedCueCount == 0 || HorizontalDistance(transform.position, blockedCuePosition) > 2.5f)
            {
                blockedCueCount = 0;
                lastBlockedCue = null;
                blockedCuePosition = transform.position;
            }
            blockedCueCount++;
            if (blockedCueCount >= 3)
            {
                blockedCueCount = 0;
                lastBlockedCue = null;
                // Blocked again near the last cut: cut wider. On Artifice's hillside each 1.2 m cut
                // brought the next plan back to the same rock face a metre over, five times.
                blockedReplans = HorizontalDistance(transform.position, blockedReplanPosition) < 4f ? blockedReplans + 1 : 0;
                blockedReplanPosition = transform.position;
                Vector3 heading = new Vector3(toTarget.x, 0f, toTarget.z);
                float size = 1.2f + 1.3f * Mathf.Min(blockedReplans, 3);
                RememberBlockedSpot(transform.position + (heading.sqrMagnitude > 0.0001f ? heading.normalized * (1.2f + size * 0.5f) : Vector3.zero), size);
                lastReplanTime = -999f;
                Replan("Blocked. Finding another way.");
                return;
            }
            string cue = DescribeBlocked(manualProbe);
            if (cue == lastBlockedCue && (cue == "Jump" || cue == "Stand up and jump"))
                cue = "Blocked, back up";
            // The probe's free side can swap from one cue to the next at the same spot; "left,
            // right, left" walks the player back and forth. Keep the side already given.
            else if (cue != null && cue.StartsWith("Blocked, step ") && lastBlockedCue != null && lastBlockedCue.StartsWith("Blocked, step "))
                cue = lastBlockedCue;
            lastBlockedCue = cue;
            Utilities.SpeakText(cue);
        }

        private Vector3 blockedCuePosition;
        private int blockedReplans;
        private Vector3 blockedReplanPosition = Vector3.positiveInfinity;
        private int blockedCueCount;
        private string lastBlockedCue;

        private string DescribeBlocked(ObstacleProbe.Result result)
        {
            // The wall is the climb itself; the way on is the ladder.
            if (manualLadder != null)
            {
                Vector3 ladder = LadderMountPoint(manualLadder, manualLadderDown);
                int meters = Mathf.Max(1, Mathf.RoundToInt(HorizontalDistance(transform.position, ladder)));
                return $"Ladder {DescribeDirection(ladder)}, {meters} {(meters == 1 ? "meter" : "meters")}";
            }

            Vector3 facing = playerController.thisPlayerBody != null ? playerController.thisPlayerBody.forward : transform.forward;
            // Pushing into a rock face too steep to walk and low enough to jump, on a path that
            // climbs: jump it, as auto mode does. The probe calls it a wall, and stepping aside
            // stood a player 5 s on Artifice's hillside.
            Vector3 flatFacing = new Vector3(facing.x, 0f, facing.z);
            if (flatFacing.sqrMagnitude > 0.0001f && currentTargetNode != null &&
                currentTargetNode.position.y - NODE_GROUND_OFFSET > transform.position.y + 0.1f &&
                SteepLipAhead(flatFacing.normalized, out _))
                return playerController.isCrouching ? CrouchedLipCue(facing) : "Jump";
            // The game never jumps a crouched player.
            if (result.kind == ObstacleProbe.Kind.Jumpable)
                return playerController.isCrouching ? CrouchedLipCue(facing) : "Jump";
            if (result.kind == ObstacleProbe.Kind.Crouch && !playerController.isCrouching)
            {
                manualCrouchCued = true;
                return "Crouch";
            }

            if (result.kind == ObstacleProbe.Kind.Wall && result.aroundDirection != Vector3.zero)
            {
                // Relative to where the player faces, which is what "left" means to them.
                float side = Vector3.Dot(transform.right, result.aroundDirection);
                float forward = Vector3.Dot(transform.forward, result.aroundDirection);
                if (forward < -0.5f)
                    return "Blocked, back up";
                return side < 0f ? "Blocked, step left" : "Blocked, step right";
            }

            // Boxed in on every side the probe tried: the way out is back the way they came.
            if (result.kind == ObstacleProbe.Kind.Wall)
                return "Blocked, back up";

            // Head against an overhang the forward casts skip (they ignore what the capsule
            // already touches): crouch under it.
            // The controller's own Above flag catches a curved underside (the belly of
            // Experimentation's train car tank) that the head probe passes beside.
            if ((Probe != null && Probe.HeadAgainstOverhang(facing)) ||
                (playerController.thisController.collisionFlags & CollisionFlags.Above) != 0)
            {
                if (playerController.isCrouching)
                    return "Blocked, back up";
                manualCrouchCued = true;
                return "Crouch";
            }

            // Nothing ahead at body height, so something low is catching the feet.
            return playerController.isCrouching ? CrouchedLipCue(facing) : "Jump";
        }

        /// <summary>
        /// Crouched against a lip: stand and jump where there is room, as auto mode does. Under
        /// low rock with none, back up and come at it again (in Adamance's flooded tunnel a step
        /// to the side moved the player a few centimetres; backing up freed them).
        /// </summary>
        private int narrowLandingReplans;
        private Vector3 narrowLandingPosition = Vector3.positiveInfinity;

        /// <summary>
        /// The far side of whatever is jumped here drops away more than 6 m straight ahead or
        /// half a metre to either side: a narrow beam over a pit, not a landing.
        /// </summary>
        private bool NarrowLandingOverDrop(Vector3 dir)
        {
            Vector3 side = Vector3.Cross(Vector3.up, dir) * 0.5f;
            Vector3 beyond = transform.position + dir * 1.3f;
            foreach (Vector3 q in new[] { beyond, beyond + side, beyond - side })
            {
                if (!Physics.Raycast(q + Vector3.up * 2.5f, Vector3.down, out RaycastHit hit, 40f, groundMask, QueryTriggerInteraction.Ignore) ||
                    hit.point.y < transform.position.y - 6f)
                    return true;
            }
            return false;
        }

        private string CrouchedLipCue(Vector3 facing)
        {
            if (Probe != null && Probe.RoomToStand(facing, 0f))
                return "Stand up and jump";
            return "Blocked, back up";
        }

        /// <summary>"ahead", "to your left", "to your right" or "behind you", from where the player faces.</summary>
        private string DescribeDirection(Vector3 point)
        {
            Vector3 to = point - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude < 0.25f)
                return "here";
            float angle = Vector3.SignedAngle(transform.forward, to, Vector3.up);
            if (Mathf.Abs(angle) <= 35f)
                return "ahead";
            if (Mathf.Abs(angle) >= 135f)
                return "behind you";
            return angle < 0f ? "to your left" : "to your right";
        }

        private void PingCurrentNode()
        {
            if (currentTargetNode == null || currentTargetNode.audioSource == null)
                return;

            // Before a gap the ping leads to the run-up and along it, not over the hole.
            AudioSource source = currentTargetNode.audioSource;
            Vector3 nodePos = currentTargetNode.position;
            if (ManualElevatorGuide(out Vector3 guide) || ManualGapGuide(out guide) || ManualCrawlGuide(out guide))
            {
                source = GapGuideAudio(guide);
                nodePos = guide;
            }

            Vector3 playerPos = transform.position;
            Vector3 toNode = nodePos - playerPos;
            Vector3 horizontalToNode = new Vector3(toNode.x, 0, toNode.z).normalized;
            Vector3 horizontalForward = new Vector3(transform.forward.x, 0, transform.forward.z).normalized;
            float dot = Vector3.Dot(horizontalForward, horizontalToNode);

            AudioClip clip = dot > 0.3f ? onTargetSound : dot < -0.3f ? offTargetSound : almostOnTargetSound;
            if (clip == null)
                return;

            source.clip = clip;
            source.volume = AudioSystemBypass.CalculateVolumeBasedOnDistance(
                Vector3.Distance(playerPos, nodePos), 1.0f, 20.0f, 0.6f);
            source.Play();
        }
        #endregion

        #region Ladder links
        /// <summary>NavMesh area for the ladder links added below. Not Jump or Climb, so the
        /// planner uses them; ObstacleProbe.IsDisconnected leaves it out of its walking check, so
        /// a step along one still counts as a climb and the ladder code takes it.</summary>
        public const int LADDER_LINK_AREA = 20;
        /// <summary>NavMesh area for the enemy Jump links a player can also do (see EnsureJumpLinks).</summary>
        public const int PLAYER_JUMP_LINK_AREA = 21;
        // A drop this deep or less is taken as a plain walk off the edge (fall damage starts
        // at about 18 m).
        private const float PLAYER_DROP_MAX = 8f;

        private static readonly Dictionary<int, NavMeshLinkInstance> addedJumpLinks = new Dictionary<int, NavMeshLinkInstance>();

        /// <summary>
        /// The enemy NavMesh's Jump links are left out of planning, since most are leaps no player
        /// makes. Some are not: a step up no higher than a player's jump (onto the walkway by
        /// Experimentation's main entrance), or a drop a player just walks off. Those get a link
        /// of their own the planner may use: both ways for a step, downward only for a drop.
        /// </summary>
        private void EnsureJumpLinks()
        {
            ObstacleProbe probeNow = Probe;
            float maxUp = (probeNow != null ? probeNow.MaxJumpHeight : 2f) - 0.25f;
            int settingsCount = NavMesh.GetSettingsCount();
            foreach (var link in FindObjectsOfType<OffMeshLink>())
            {
                if (link == null || link.area != 2 || link.startTransform == null || link.endTransform == null)
                    continue;
                int id = link.GetInstanceID();
                if (addedJumpLinks.TryGetValue(id, out var existing) && existing.valid)
                    continue;

                Vector3 a = link.startTransform.position;
                Vector3 b = link.endTransform.position;
                Vector3 high = a.y >= b.y ? a : b;
                Vector3 low = a.y >= b.y ? b : a;
                float rise = high.y - low.y;
                float across = HorizontalDistance(a, b);
                if (across > 4f || rise > PLAYER_DROP_MAX)
                    continue;
                // Any leap down can be dropped by a player, whichever way the enemy link goes.
                bool step = rise <= maxUp;
                if (!FloorAllAlong(high, low))
                    continue; // a leap across a hole, not a step or a drop

                for (int i = 0; i < settingsCount; i++)
                {
                    NavMeshLinkData data = new NavMeshLinkData
                    {
                        startPosition = step ? low : high,
                        endPosition = step ? high : low,
                        width = 0f,
                        costModifier = -1f,
                        bidirectional = step,
                        area = PLAYER_JUMP_LINK_AREA,
                        agentTypeID = NavMesh.GetSettingsByIndex(i).agentTypeID
                    };
                    NavMeshLinkInstance added = NavMesh.AddLink(data);
                    if (added.valid)
                        addedJumpLinks[id] = added;
                }
            }
        }

        private static readonly Dictionary<int, List<NavMeshLinkInstance>> addedLadderLinks = new Dictionary<int, List<NavMeshLinkInstance>>();

        /// <summary>
        /// Some ladders have no working off-mesh link (the ship's side ladder: its link starts and
        /// ends at the same point), so paths take a long way round, or under the ship where the
        /// enemy NavMesh fits and a player does not. Each ladder whose top and bottom are not
        /// already joined by a short route gets a link of its own, both ways.
        /// </summary>
        private void EnsureLadderLinks()
        {
            int settingsCount = NavMesh.GetSettingsCount();
            foreach (var ladder in GetLadders())
            {
                if (ladder == null)
                    continue;
                int id = ladder.GetInstanceID();
                if (addedLadderLinks.TryGetValue(id, out var existing) && existing.Count > 0 && existing[0].valid)
                    continue;
                if (refusedLadders.Contains(id))
                    continue;

                Vector3 top = ladder.topOfLadderPosition.position;
                Vector3 bottom = ladder.bottomOfLadderPosition.position;
                if (!NavMesh.SamplePosition(top, out NavMeshHit topHit, 1.5f, NavMesh.AllAreas) ||
                    !NavMesh.SamplePosition(bottom, out NavMeshHit bottomHit, 2.5f, NavMesh.AllAreas) ||
                    topHit.position.y - bottomHit.position.y < 1f)
                    continue;

                NavMeshPath walk = new NavMeshPath();
                if (CalculatePlayerPath(bottomHit.position, topHit.position, PlayerAreaMask(), walk) &&
                    walk.status == NavMeshPathStatus.PathComplete && PathLength(walk) < Vector3.Distance(bottomHit.position, topHit.position) * 2f + 4f)
                {
                    addedLadderLinks[id] = new List<NavMeshLinkInstance>();
                    continue;
                }

                var links = new List<NavMeshLinkInstance>();
                for (int i = 0; i < settingsCount; i++)
                {
                    NavMeshLinkData data = new NavMeshLinkData
                    {
                        startPosition = bottomHit.position,
                        endPosition = topHit.position,
                        width = 0f,
                        costModifier = -1f,
                        bidirectional = true,
                        area = LADDER_LINK_AREA,
                        agentTypeID = NavMesh.GetSettingsByIndex(i).agentTypeID
                    };
                    NavMeshLinkInstance link = NavMesh.AddLink(data);
                    if (link.valid)
                        links.Add(link);
                }
                addedLadderLinks[id] = links;
                Debug.Log($"Pathfinder: added ladder link {bottomHit.position} -> {topHit.position} ({links.Count})");
            }
        }

        // Ladders that would not take the player this trip, and the game's own links up them that
        // were switched off with them.
        private readonly HashSet<int> refusedLadders = new HashSet<int>();
        // Switched off only around the player's own path queries (CalculatePlayerPath), like the
        // ship's climb links, so enemies keep them.
        private static readonly List<OffMeshLink> refusedLadderLinks = new List<OffMeshLink>();

        /// <summary>
        /// Takes a ladder that won't take the player out of planning for the rest of the trip:
        /// its added link, and any link of the game's between the same top and bottom. A cut in
        /// the NavMesh leaves links alone, so cutting it out replanned straight back up it. False
        /// if it was already out.
        /// </summary>
        private bool RefuseLadder(InteractTrigger ladder)
        {
            int id = ladder.GetInstanceID();
            if (!refusedLadders.Add(id))
                return false;
            if (addedLadderLinks.TryGetValue(id, out var links))
            {
                foreach (var link in links)
                {
                    if (link.valid)
                        NavMesh.RemoveLink(link);
                }
                addedLadderLinks.Remove(id);
            }
            Vector3 top = ladder.topOfLadderPosition.position;
            Vector3 bottom = ladder.bottomOfLadderPosition.position;
            foreach (var link in FindObjectsOfType<OffMeshLink>())
            {
                if (!link.activated || link.startTransform == null || link.endTransform == null)
                    continue;
                Vector3 a = link.startTransform.position;
                Vector3 b = link.endTransform.position;
                if ((Vector3.Distance(a, top) < 2.5f && Vector3.Distance(b, bottom) < 2.5f) ||
                    (Vector3.Distance(a, bottom) < 2.5f && Vector3.Distance(b, top) < 2.5f))
                    refusedLadderLinks.Add(link);
            }
            Debug.Log($"Pathfinder: ladder at {bottom} refused the player, out of planning for this trip");
            return true;
        }

        private void RestoreRefusedLadders()
        {
            refusedLadderLinks.Clear();
            refusedLadders.Clear();
        }

        /// <summary>Floor all the way between the two ends of a link, no lower than its low end:
        /// walking (or stepping up) from one to the other never meets a hole.</summary>
        private bool FloorAllAlong(Vector3 high, Vector3 low)
        {
            for (int i = 0; i <= 10; i++)
            {
                Vector3 p = Vector3.Lerp(high, low, i / 10f);
                if (!Physics.Raycast(new Vector3(p.x, high.y + 1f, p.z), Vector3.down, out RaycastHit hit,
                        high.y - low.y + 2.5f, groundMask, QueryTriggerInteraction.Ignore) ||
                    hit.point.y < low.y - 0.6f)
                    return false;
            }
            return true;
        }

        private static float PathLength(NavMeshPath path)
        {
            float length = 0f;
            for (int i = 1; i < path.corners.Length; i++)
                length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
            return length;
        }
        #endregion

        #region Auto Mode
        private InteractTrigger[] GetLadders()
        {
            if (cachedLadders == null || Time.time - cachedLaddersTime > 5f)
            {
                List<InteractTrigger> ladders = new List<InteractTrigger>();
                foreach (var trigger in FindObjectsOfType<InteractTrigger>())
                {
                    if (trigger.isLadder && trigger.ladderHorizontalPosition != null &&
                        trigger.topOfLadderPosition != null && trigger.bottomOfLadderPosition != null)
                    {
                        ladders.Add(trigger);
                    }
                }
                cachedLadders = ladders.ToArray();
                cachedLaddersTime = Time.time;
            }
            return cachedLadders;
        }

        /// <summary>The ladder near the player that spans from their height up to target.</summary>
        private InteractTrigger FindLadderTo(Vector3 target)
        {
            InteractTrigger best = null;
            float bestDistance = LADDER_SEARCH_RADIUS;
            float low = Mathf.Min(transform.position.y, target.y);
            float high = Mathf.Max(transform.position.y, target.y);

            foreach (var ladder in GetLadders())
            {
                if (ladder == null || !ladder.interactable)
                    continue;
                // The bottom rung can be above the ground (the ship's side ladder where the ship sits
                // high on uneven ground, 1.9 m up on Vow) and still be grabbed from it.
                if (ladder.bottomOfLadderPosition.position.y > low + 2.2f || ladder.topOfLadderPosition.position.y < high - 1.5f)
                    continue;

                float distance = HorizontalDistance(transform.position, ladder.ladderHorizontalPosition.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = ladder;
                }
            }
            return best;
        }

        private bool CanSteer()
        {
            PlayerControllerB p = playerController;
            if (p == null || p.isPlayerDead || !p.isPlayerControlled)
                return false;
            if (p.inTerminalMenu || p.isTypingChat || p.inSpecialInteractAnimation)
                return false;
            if (p.quickMenuManager != null && p.quickMenuManager.isMenuOpen)
                return false;
            return true;
        }

        private ObstacleProbe Probe
        {
            get
            {
                if (probe == null && playerController != null && playerController.thisController != null)
                {
                    probe = new ObstacleProbe(playerController);
                }
                return probe;
            }
        }

        private float ProbeDistance()
        {
            float speed = 0f;
            if (playerController != null && playerController.thisController != null)
            {
                Vector3 v = playerController.thisController.velocity;
                speed = new Vector2(v.x, v.z).magnitude;
            }
            return PROBE_BASE_DISTANCE + speed * PROBE_SPEED_FACTOR;
        }

        private void UpdateAutoWalk()
        {
            // On a ladder the Move input climbs: up is forward. The game lets go at the top or
            // bottom by itself.
            if (playerController != null && playerController.isClimbingLadder)
            {
                ladderTriedOn = null;
                ladderTries = 0;
                AutoMoveInput = new Vector2(0f, ladderDirection);
                AutoSprintInput = false;
                return;
            }

            if (currentTargetNode == null || !CanSteer())
            {
                ClearAutoInput();
                return;
            }

            // Stand at a closed door while holding interact on it, as a player would, instead of
            // walking into it.
            if (doorBeingOpened != null && DoorIsClosed(doorBeingOpened) && CanUseTrigger(doorBeingOpened.doorTrigger) &&
                HorizontalDistance(transform.position, doorBeingOpened.transform.position) < 1.3f &&
                doorWaitTime < 3f)
            {
                // Capped, so a door that never opens hands over to the stuck handling.
                doorWaitTime += Time.deltaTime;
                ClearAutoInput();
                ResetStuckTracking();
                return;
            }

            // The door just opened: let it swing and the path be laid round its panel before
            // walking on, instead of into the panel as it comes round.
            if (doorReplanAt > 0f && Time.time - doorOpenedAt < 1.5f)
            {
                ClearAutoInput();
                ResetStuckTracking();
                return;
            }

            Transform body = playerController.thisPlayerBody != null ? playerController.thisPlayerBody : transform;

            // The Mineshaft's elevator: call it, ride it, step out.
            if (UpdateAutoElevator(body))
                return;

            // A locked door on a key route: walk up to it, then stand facing it, camera on the
            // door so the key's use ray hits it, until the player unlocks it.
            switch (UpdateLockedDoor(out Vector3 doorCenter))
            {
                case LockedDoorState.Wait:
                    Utilities.LookAtPoint(doorCenter);
                    ClearAutoInput();
                    ResetStuckTracking();
                    return;

                case LockedDoorState.Approach:
                    Vector3 toDoor = doorCenter - transform.position;
                    toDoor.y = 0f;
                    CenterCamera(body);
                    TurnBodyToward(body, toDoor.normalized);
                    Vector3 doorLocal = body.InverseTransformDirection(toDoor.normalized);
                    AutoMoveInput = new Vector2(doorLocal.x, doorLocal.z).normalized;
                    AutoSprintInput = false;
                    return;
            }

            // A gap ahead: run up and jump it.
            if (UpdateAutoGap(body))
                return;

            // A crawl space ahead: line up, crouch, crawl through.
            if (UpdateAutoCrawl(body))
                return;

            Vector3 toNode = currentTargetNode.position - transform.position;
            toNode.y = 0f;

            Vector3 nodeGround = currentTargetNode.position - Vector3.up * NODE_GROUND_OFFSET;
            bool ladderStep = false;
            if (toNode.magnitude < LADDER_SEARCH_RADIUS && Probe != null)
            {
                InteractTrigger ladder = LadderForStep(nodeGround, out bool down);
                ladderStep = ladder != null || (!down && Probe.IsClimbLink(nodeGround));
                if (ladder == null && ladderStep)
                {
                    // A link only enemies can climb, on a path that had no other way. A player
                    // cannot get up there, so say so rather than jump at the wall until the stuck
                    // check gives up.
                    if (toNode.magnitude < 3f && playerController.thisController.isGrounded)
                    {
                        // Cut it out and look for another way; stop only once out of tries.
                        if (carvedSpots.Count < MAX_CARVED_SPOTS)
                        {
                            RememberClimb(nodeGround);
                            lastReplanTime = -999f;
                            Replan("Too high to climb here. Finding another way.");
                            return;
                        }
                        StopPathfinding(playSound: false);
                        Utilities.SpeakText("Too high to climb here. Pathfinding stopped.");
                        return;
                    }
                }
                else if (ladder != null)
                {
                    // Up: the foot of the ladder. Down: its top, back from the edge, where the
                    // game puts a player who climbs off it.
                    Vector3 toLadder = LadderMountPoint(ladder, down) - transform.position;
                    toLadder.y = 0f;
                    if (toLadder.magnitude <= LADDER_USE_DISTANCE)
                    {
                        // Wait at the foot of it until the player's hands are free; the stuck
                        // check ignores a player who is not trying to move.
                        if (!CanUseTrigger(ladder))
                        {
                            SayCannotClimb(ladder);
                            ClearAutoInput();
                            return;
                        }
                        if (Time.time >= nextLadderTry)
                        {
                            ladderTries = ladder == ladderTriedOn ? ladderTries + 1 : 1;
                            ladderTriedOn = ladder;
                            if (ladderTries > 3)
                            {
                                ladderTriedOn = null;
                                ladderTries = 0;
                                if (RefuseLadder(ladder))
                                {
                                    lastReplanTime = -999f;
                                    Replan("Can't get on this ladder. Finding another way.");
                                    return;
                                }
                                StopPathfinding(playSound: false);
                                Utilities.SpeakText("Can't get on this ladder. Pathfinding stopped.");
                                return;
                            }
                            // Same call as pressing interact on the ladder.
                            nextLadderTry = Time.time + 2f;
                            ladderDirection = down ? -1f : 1f;
                            ladder.Interact(transform);
                            StartedLadder(ladder);
                            ResetStuckTracking();
                        }
                        ClearAutoInput();
                        return;
                    }
                    toNode = toLadder;
                }
            }
            // Steer at the furthest node ahead that is a straight walk away, so the player runs
            // one smooth line past several nodes instead of turning at each one.
            if (!ladderStep)
            {
                Vector3 aim = PursuitAim() - transform.position;
                aim.y = 0f;
                if (aim.sqrMagnitude > 0.0001f)
                {
                    toNode = aim;
                }
            }

            if (toNode.sqrMagnitude < 0.0001f)
            {
                ClearAutoInput();
                return;
            }
            Vector3 pathDirection = toNode.normalized;

            // Face along the path. Not in the air: off a 7 m ledge on Offense the node was all but
            // straight below, and the body swung 35 degrees toward it and back on landing.
            CenterCamera(body);
            if (!playerController.thisController.isGrounded)
            {
                Vector3 facing = body.forward;
                facing.y = 0f;
                if (Vector3.Angle(facing, pathDirection) > AUTO_TURN_IN_PLACE_ANGLE)
                    ClearAutoInput();
                return;
            }
            if (TurnBodyToward(body, pathDirection) > AUTO_TURN_IN_PLACE_ANGLE)
            {
                ClearAutoInput();
                return;
            }

            // Move wherever the way is actually free.
            Vector3 moveDirection = pathDirection;
            bool clear = true;
            float laneMagnitude = 1f;
            ObstacleProbe probeNow = Probe;
            if (probeNow != null)
            {
                bool dropAhead = currentTargetNode.position.y - NODE_GROUND_OFFSET < transform.position.y - 1f;
                // No further than just past the point being steered at: when that is a corner,
                // looking on past it finds the wall the path turns away from, and a sidestep
                // starts for nothing.
                float probeDistance = Mathf.Min(ProbeDistance(), Mathf.Max(toNode.magnitude + 0.4f, PROBE_BASE_DISTANCE));
                ObstacleProbe.Result ahead = probeNow.Assess(pathDirection, probeDistance, dropAhead);
                switch (ahead.kind)
                {
                    case ObstacleProbe.Kind.Jumpable:
                        clear = false;
                        // Jump toward the path, not along a sidestep begun while it still read as
                        // a wall: a March railing hopped sideways put the player down beside the
                        // narrow beam behind it, over a 22 m pit.
                        if (!aroundForced)
                            aroundUntil = 0f;
                        // Jumping needs stamina; wait for it rather than walking into the ledge.
                        // Never in water or quicksand, where stamina does not come back and
                        // standing still means drowning or sinking.
                        bool hindered = playerController.isUnderwater || playerController.isSinking ||
                                        playerController.isMovementHindered > 0;
                        if (playerController.isExhausted && !hindered)
                        {
                            ClearAutoInput();
                            return;
                        }
                        if (Time.time >= nextJumpTime && probeNow.Jump("obstacle " + ahead.kind))
                        {
                            nextJumpTime = Time.time + JUMP_COOLDOWN;
                        }
                        break;

                    case ObstacleProbe.Kind.Crouch:
                        clear = false;
                        // About to take a ladder: grabbing it stands the player up anyway.
                        if (!playerController.isCrouching && !ladderStep && !LadderJustAhead() && Time.time >= noAutoCrouchUntil)
                        {
                            playerController.Crouch(true);
                            autoCrouched = playerController.isCrouching;
                            autoCrouchTime = Time.time;
                        }
                        // A crawl space barely wider than the player: line up on it first,
                        // mostly sideways, slowing in as the gap closes.
                        if (ahead.laneOffset.sqrMagnitude > 0.0004f)
                        {
                            moveDirection = (ahead.laneOffset.normalized + pathDirection * 0.15f).normalized;
                            aroundUntil = 0f;
                            laneMagnitude = Mathf.Clamp(ahead.laneOffset.magnitude * 4f, 0.25f, 1f);
                        }
                        break;

                    case ObstacleProbe.Kind.Wall:
                        // The steering point was chosen as a straight line a body wider than the
                        // player fits along; a "wall" the probe reports on that line is a railing
                        // end or corner brushed in passing, and sidestepping it is what turned the
                        // player back and forth at corridor mouths.
                        if (!ladderStep && toNode.magnitude > 0.6f && BodyLineClear(transform.position + toNode + Vector3.up * NODE_GROUND_OFFSET))
                            break;
                        clear = false;
                        if (ahead.aroundDirection != Vector3.zero && !(aroundForced && Time.time < aroundUntil))
                        {
                            aroundDirection = ahead.aroundDirection;
                            aroundUntil = Time.time + AROUND_HOLD_TIME;
                            aroundForced = false;
                        }
                        break;
                }

                // Stand back up once the way ahead is clear at full height. Crouch(false) checks
                // the space overhead itself and does nothing if the player would not fit.
                // Clear well ahead too: the short probe passes the mouth of a crawl space, and
                // standing there walks the head into its edge.
                // Only once it has stayed clear a moment, and not straight after crouching: at
                // the edge of something low the probe can flip each frame, and standing on the
                // first clear frame bobbed the player up and down.
                bool roomToStand = ahead.kind == ObstacleProbe.Kind.Clear && autoCrouched && playerController.isCrouching &&
                    probeNow.Assess(pathDirection, STAND_UP_LOOKAHEAD).kind == ObstacleProbe.Kind.Clear &&
                    probeNow.RoomToStand(pathDirection, STAND_UP_LOOKAHEAD);
                if (!roomToStand)
                {
                    standClearSince = -1f;
                }
                else if (standClearSince < 0f)
                {
                    standClearSince = Time.time;
                }
                if (roomToStand && Time.time - standClearSince >= AUTO_STAND_CLEAR_TIME &&
                    Time.time - autoCrouchTime >= AUTO_MIN_CROUCH_TIME)
                {
                    playerController.Crouch(false);
                    autoCrouched = playerController.isCrouching;
                    standClearSince = -1f;
                }

                // Keep sidestepping briefly after the probe clears, or the player turns back into
                // the corner it just got past.
                if (Time.time < aroundUntil && aroundDirection != Vector3.zero)
                {
                    // A stuck recovery (backing out of a pocket the probe can't see): that way
                    // and no other, or blending it with the path cancels it out.
                    if (aroundForced)
                    {
                        moveDirection = aroundDirection;
                        clear = false;
                    }
                    else if (ahead.kind == ObstacleProbe.Kind.Clear)
                    {
                        // Past it already: ease back onto the path instead of a pure sidestep,
                        // which walks sideways into whatever is beside the obstacle. Drop the
                        // sidestep if that way is now blocked.
                        if (probeNow.Assess(aroundDirection, 0.5f).kind != ObstacleProbe.Kind.Clear)
                        {
                            aroundUntil = 0f;
                        }
                        else
                        {
                            moveDirection = (aroundDirection + pathDirection).normalized;
                            clear = false;
                        }
                    }
                    else
                    {
                        moveDirection = aroundDirection;
                        clear = false;
                    }
                }
            }

            // Never walk off a drop far bigger than the path's own.
            if (playerController.thisController.isGrounded && !SafeStep(ref moveDirection, toNode))
            {
                ClearAutoInput();
                return;
            }

            Vector3 local = body.InverseTransformDirection(moveDirection);
            AutoMoveInput = new Vector2(local.x, local.z).normalized * laneMagnitude;

            // The game's own Sprint input: stamina drains and exhaustion stops the run exactly as
            // it does for a player holding the key. Only on a clear way forward; sprinting into a
            // sidestep overshoots it.
            float moveAngle = Vector3.Angle(body.forward, moveDirection);
            bool narrow = OnNarrowFloor(moveDirection);
            // Never crouched: the game stands a sprinting player up (PlayerControllerB.Update), in
            // the mouth of the crawl space they crouched for.
            AutoSprintInput = ConfigManager.AutoPathfindingSprint.Value && clear && !narrow && !playerController.isCrouching &&
                              moveAngle < AUTO_SPRINT_MAX_ANGLE && StaminaToSprint();

            // On a girder or plank: a steadier walk, where a sideways drift of half a metre is a fall.
            if (narrow)
            {
                AutoMoveInput *= 0.75f;
            }
        }

        /// <summary>
        /// Sprint only with stamina to spare. An exhausted player can't jump, and the game makes
        /// them exhausted the moment sprint is pressed under 0.3 stamina (or held to under 0.1),
        /// until stamina is back over 0.2, which never happens in water. In Adamance's flooded
        /// mine a sprint across the level left nothing, and every jump at a rock lip was refused
        /// for seconds. Start over 0.5, stop at 0.3: a jump costs 0.08.
        /// </summary>
        private bool StaminaToSprint()
        {
            float meter = playerController.sprintMeter;
            return !playerController.isExhausted && (playerController.isSprinting ? meter > 0.3f : meter > 0.5f);
        }

        /// <summary>A drop of a metre or more 0.6 m to one side or the other: a girder, a plank.
        /// Anything solid there counts as no drop, so the side of a hill, too steep to be floor,
        /// is not a girder (it was, and auto mode stopped sprinting on every slope).</summary>
        private bool OnNarrowFloor(Vector3 direction)
        {
            Vector3 side = Vector3.Cross(Vector3.up, direction).normalized;
            Vector3 feet = transform.position;
            foreach (float s in SideOffsets)
            {
                if (!Physics.Raycast(feet + side * s + Vector3.up * 1.5f, Vector3.down, 2.5f, groundMask, QueryTriggerInteraction.Ignore))
                    return true;
            }
            return false;
        }

        private static void ClearAutoInput()
        {
            AutoMoveInput = Vector2.zero;
            AutoSprintInput = false;
        }

        /// <summary>Levels the camera on the way, so auto mode always looks where it walks.</summary>
        private static void CenterCamera(Transform body)
        {
            if (LACore.CameraTransform == null)
                return;
            Vector3 forward = body.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                return;
            Utilities.LookAtPoint(LACore.CameraTransform.position + forward.normalized * 10f);
        }

        /// <summary>After auto mode arrives, turns the camera onto the target for a moment.</summary>
        private IEnumerator FaceTarget(GameObject target)
        {
            float end = Time.time + 1.5f;
            while (Time.time < end && !isPathfinding && target != null)
            {
                Utilities.LookAtPoint(target.transform.position);
                yield return null;
            }
        }

        /// <summary>
        /// Turns the body toward direction, quickly when far off and gently when nearly there, so
        /// small corrections never show as a snap. Returns the angle still to turn.
        /// </summary>
        private static float TurnBodyToward(Transform body, Vector3 direction)
        {
            Vector3 euler = body.eulerAngles;
            float desiredYaw = Quaternion.LookRotation(direction).eulerAngles.y;
            float error = Mathf.Abs(Mathf.DeltaAngle(euler.y, desiredYaw));
            float speed = Mathf.Clamp(error * 6f, 60f, AUTO_TURN_SPEED);
            float yaw = Mathf.MoveTowardsAngle(euler.y, desiredYaw, speed * Time.deltaTime);
            body.eulerAngles = new Vector3(euler.x, yaw, euler.z);
            return Mathf.Abs(Mathf.DeltaAngle(yaw, desiredYaw));
        }

        /// <summary>
        /// The furthest point along the path, up to the lookahead, that the player can walk to in a
        /// straight line: the NavMesh is unbroken on the way (no ledge, no carved locked door),
        /// nothing at body height is in between, no quicksand is crossed, and the ground does not
        /// climb or drop steeply (stairs are fine; a ladder or drop ends the line). The point
        /// slides along the path segments instead of jumping from node to node, so the heading
        /// changes smoothly.
        /// </summary>
        private Vector3 PursuitAim()
        {
            Vector3 aim = currentTargetNode.position;
            Vector3 feet = transform.position;
            NavMeshHit from;
            if (!NavMesh.SamplePosition(feet, out from, 1f, NavMesh.AllAreas))
                return aim;

            Vector3 v = playerController.thisController.velocity;
            float lookahead = Mathf.Min(AIM_LOOKAHEAD_BASE + new Vector2(v.x, v.z).magnitude * AIM_LOOKAHEAD_PER_SPEED, AIM_LOOKAHEAD_MAX);
            float travelled = HorizontalDistance(feet, aim);
            Vector3 previous = aim;
            Vector3 chest = feet + Vector3.up * 1.2f;

            bool Walkable(Vector3 point)
            {
                Vector3 ground = point - Vector3.up * NODE_GROUND_OFFSET;
                NavMeshHit to, edge;
                if (!NavMesh.SamplePosition(ground, out to, 1f, NavMesh.AllAreas) ||
                    NavMesh.Raycast(from.position, to.position, out edge, NavMesh.AllAreas))
                    return false;
                if (!BodyLineClear(point))
                    return false;
                if (FirstQuicksandCrossed(new[] { feet, ground }, out _))
                    return false;

                // Real floor under the whole line: the NavMesh can overhang a narrow girder.
                float length = HorizontalDistance(feet, point);
                int steps = Mathf.CeilToInt(length / 0.4f);
                float lineY = feet.y;
                for (int s = 1; s <= steps; s++)
                {
                    Vector3 q = Vector3.Lerp(feet, point, (float)s / steps);
                    q.y = lineY;
                    if (NearestGroundY(q, out float y) > 1.0f)
                        return false;
                    lineY = y;
                }
                return true;
            }

            for (int i = currentTargetNode.index + 1; i < pathNodes.Count && travelled < lookahead; i++)
            {
                Vector3 next = pathNodes[i].position;
                float step = HorizontalDistance(previous, next);
                if (step < 0.01f)
                    continue;
                if (Mathf.Abs(next.y - previous.y) > Mathf.Max(0.5f, step * AIM_MAX_RISE_PER_METER))
                    break;
                if (lockedDoorOnPath != null && i >= lockedDoorCrossIndex)
                    break;
                if (elevatorOnPath != null && !elevatorRideDone && i >= elevatorCrossIndex)
                    break;

                // Only as far along this segment as the lookahead reaches; if the end of that is
                // out of sight (round a corner), the furthest of a few points short of it that is.
                float reach = Mathf.Min(1f, (lookahead - travelled) / step);
                bool found = false;
                foreach (float fraction in new[] { reach, reach * 0.66f, reach * 0.33f })
                {
                    Vector3 candidate = Vector3.Lerp(previous, next, fraction);
                    if (Walkable(candidate))
                    {
                        aim = candidate;
                        found = fraction == 1f;
                        break;
                    }
                }
                if (!found)
                    break;

                travelled += step;
                previous = next;
            }

            // Overshot a corner at a run: the node itself is round it, behind the wall. Head
            // for the furthest point back along the path into that node that is in clear view,
            // which brings the player back onto the path's line instead of into the wall.
            // Not into a node up or down a jump (the back of the ship where it sits on a slope):
            // the line there is blocked by the ledge itself, which the probe jumps, and pulling
            // back along it swung the player between the two for seconds.
            if (aim == currentTargetNode.position && currentTargetNode.index > 0 && !BodyLineClear(aim) &&
                Mathf.Abs(aim.y - pathNodes[currentTargetNode.index - 1].position.y) <= playerController.thisController.stepOffset + 0.1f)
            {
                Vector3 back = pathNodes[currentTargetNode.index - 1].position;
                foreach (float f in OvershootFractions)
                {
                    Vector3 candidate = Vector3.Lerp(back, aim, f);
                    // Only back onto the line, never back the way the player came.
                    Vector3 toCandidate = candidate - feet;
                    Vector3 toNodeFlat = aim - feet;
                    toCandidate.y = 0f;
                    toNodeFlat.y = 0f;
                    if (HorizontalDistance(feet, candidate) > 0.5f && Vector3.Dot(toCandidate, toNodeFlat) > 0f && BodyLineClear(candidate))
                    {
                        aim = candidate;
                        break;
                    }
                }
            }

            // Sticky: on uneven ground the straight-walk test flickers frame to frame, and an
            // aim jumping between a near and a far point turned the body back and forth. Keep
            // the previous aim while it is still ahead, still walkable and further on than the
            // new one; the new one wins once the old one fails or is passed.
            if (lastAimNode == currentTargetNode && lastAimValid)
            {
                float newDistance = HorizontalDistance(feet, aim);
                float oldDistance = HorizontalDistance(feet, lastAim);
                if (oldDistance > newDistance + 0.5f && oldDistance > 1f && Walkable(lastAim))
                    aim = lastAim;
            }
            lastAim = aim;
            lastAimValid = true;
            lastAimNode = currentTargetNode;
            return aim;
        }

        /// <summary>
        /// A body a little wider than the player's (radius 0.4) fits along the straight line from
        /// the feet to point, at knee and chest height: a line that only just clears a corner has
        /// the player clip it at a run.
        /// </summary>
        private static readonly float[] SafeStepAngles = { 15f, -15f, 30f, -30f, 45f, -45f, 60f, -60f, 90f, -90f };

        /// <summary>
        /// Whether a step along dir lands on floor the path allows: not far below both the feet
        /// and the node being walked to. A narrow beam reached by stepping off a railing (March's
        /// girder room) has a pit beside it; a step a little off line went down 22 m. When the
        /// step falls, turns dir to the nearest way that doesn't, toward the node; false when
        /// there is none.
        /// </summary>
        private bool SafeStep(ref Vector3 dir, Vector3 toNode)
        {
            if (currentTargetNode == null || dir.sqrMagnitude < 0.0001f)
                return true;
            float nodeFloor = currentTargetNode.position.y - NODE_GROUND_OFFSET;
            if (!StepFalls(dir, nodeFloor))
                return true;
            Vector3 toward = toNode;
            toward.y = 0f;
            Vector3 basis = toward.sqrMagnitude > 0.01f ? toward.normalized : dir;
            if (!StepFalls(basis, nodeFloor))
            {
                dir = basis;
                return true;
            }
            foreach (float angle in SafeStepAngles)
            {
                Vector3 d = Quaternion.AngleAxis(angle, Vector3.up) * basis;
                if (!StepFalls(d, nodeFloor))
                {
                    dir = d;
                    return true;
                }
            }
            return false;
        }

        private bool StepFalls(Vector3 dir, float nodeFloor)
        {
            Vector3 feet = transform.position;
            Vector3 ahead = feet + dir.normalized * 0.6f;
            float floorY = float.NegativeInfinity;
            if (Physics.Raycast(ahead + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 40f, groundMask, QueryTriggerInteraction.Ignore))
                floorY = hit.point.y;
            return floorY < feet.y - 3f && floorY < nodeFloor - 2f;
        }

        private bool BodyLineClear(Vector3 point)
        {
            // Parallel to the ground between the two (up a flight of stairs, over a hill), so
            // the slope itself is never in the way.
            Vector3 feet = transform.position;
            Vector3 end = point - Vector3.up * NODE_GROUND_OFFSET;
            Vector3 toPoint = end - feet;
            if (new Vector2(toPoint.x, toPoint.z).sqrMagnitude < 0.0001f)
                return true;
            foreach (float h in BodyLineHeights)
            {
                Vector3 castFrom = feet + Vector3.up * h;
                if (Physics.SphereCast(castFrom, 0.42f, toPoint.normalized, out _, toPoint.magnitude, bodyMask, QueryTriggerInteraction.Ignore))
                    return false;
            }
            return true;
        }

        private Vector3 lastAim;
        private bool lastAimValid;
        private PathNode lastAimNode;

        /// <summary>The ladder the path uses for the step to target, up or down, if there is one.
        /// Down counts only for a drop of more than LADDER_DESCENT_MIN_DROP with no slope joining it.</summary>
        private InteractTrigger LadderForStep(Vector3 target, out bool down)
        {
            down = false;
            ObstacleProbe probeNow = Probe;
            if (probeNow == null)
                return null;

            if (transform.position.y - target.y > LADDER_DESCENT_MIN_DROP)
            {
                down = true;
                return probeNow.IsDisconnected(target) ? FindLadderTo(target) : null;
            }
            if (probeNow.IsClimbLink(target))
                return FindLadderTo(target);

            // Within a jump, but with a ladder right there (the short one up the back of the
            // ship, 2.1 m): climb it. Jumping that close under the deck's edge mostly fails.
            float rise = target.y - transform.position.y;
            // Standing at its foot counts too: on Rend the snow piles up to the ship's rim, the
            // mesh joins the two over the railing, and "is there a walk" said yes.
            if (rise > playerController.thisController.stepOffset + 0.3f)
            {
                InteractTrigger ladder = FindLadderTo(target);
                if (ladder != null && HorizontalDistance(ladder.topOfLadderPosition.position, target) < 2f &&
                    Mathf.Abs(ladder.topOfLadderPosition.position.y - target.y) < 1.2f &&
                    (HorizontalDistance(ladder.bottomOfLadderPosition.position, transform.position) < 1.5f || probeNow.IsDisconnected(target)))
                    return ladder;
            }
            return null;
        }

        private void StartedLadder(InteractTrigger ladder)
        {
            activeLadder = ladder;
            ladderStallY = transform.position.y;
            ladderStallTime = Time.time;
        }

        /// <summary>
        /// The game ends a climb once the player passes the ladder's top or bottom marker, but on
        /// some ladders the bottom marker is below the floor, and climbing down stops on the ground
        /// with the player still on the ladder. A player lets go with interact there ("Let go"
        /// tip); this does the same when a climb has not moved for half a second while the player
        /// is pushing along it.
        /// </summary>
        private void UpdateLadderLetGo()
        {
            if (activeLadder == null || playerController == null)
                return;
            if (!playerController.isClimbingLadder)
            {
                if (!playerController.inSpecialInteractAnimation)
                    activeLadder = null;
                return;
            }

            float y = transform.position.y;
            if (Mathf.Abs(y - ladderStallY) > 0.05f)
            {
                ladderStallY = y;
                ladderStallTime = Time.time;
                return;
            }

            bool pushing = currentMode == PathfindingMode.Auto || Mathf.Abs(playerController.moveInputVector.y) > 0.2f;
            // Only standing on the ground at the foot of it: letting go part way up is a fall.
            bool onGround = Physics.Raycast(transform.position + Vector3.up * 0.2f, Vector3.down, 0.7f, groundMask, QueryTriggerInteraction.Ignore);
            if (pushing && onGround && Time.time - ladderStallTime > 0.5f)
            {
                activeLadder.Interact(transform);
                activeLadder = null;
            }
        }

        /// <summary>The foot of a ladder the path climbs is within a couple of metres.</summary>
        private bool LadderJustAhead()
        {
            bool climbAhead = false;
            foreach (PathNode node in pathNodes)
            {
                if (!node.isReached && HorizontalDistance(node.position, transform.position) < 4f &&
                    node.position.y - NODE_GROUND_OFFSET > transform.position.y + 1.5f)
                {
                    climbAhead = true;
                    break;
                }
            }
            if (!climbAhead)
                return false;
            foreach (var ladder in GetLadders())
            {
                if (ladder != null && HorizontalDistance(ladder.ladderHorizontalPosition.position, transform.position) < 3f &&
                    Mathf.Abs(ladder.bottomOfLadderPosition.position.y - transform.position.y) < 1.5f)
                    return true;
            }
            return false;
        }

        private static Vector3 LadderMountPoint(InteractTrigger ladder, bool down)
        {
            return down ? ladder.topOfLadderPosition.position : ladder.ladderHorizontalPosition.position;
        }

        private void ResetStuckTracking()
        {
            stuckCheckPosition = transform.position;
            stuckCheckTime = Time.time;
            stuckCount = 0;
            stuckIdleChecks = 0;
            stuckNode = null;
        }

        /// <summary>
        /// The game won't jump a crouched player, so the stuck handler's jump does nothing under
        /// low rock. Wedged with the head on the lip of a downhill overhang (Adamance's mine),
        /// half a metre to one side got through: sidestep for a moment, one side then the other.
        /// </summary>
        private Vector3 lastProgressPosition;
        private bool aroundForced;

        /// <summary>
        /// Crouched and wedged: back out the way it came, toward where it last made progress,
        /// and come at it again from there. In Adamance's flooded mine tunnel the crawl line-up
        /// put the player in a pocket beside a rock lip; the way past was from half a metre back
        /// and to the side.
        /// </summary>
        private void StuckCrouchedBackOut()
        {
            if (!playerController.isCrouching || currentTargetNode == null)
                return;
            Vector3 back = lastProgressPosition - transform.position;
            back.y = 0f;
            if (back.magnitude < 0.3f)
            {
                back = transform.position - currentTargetNode.position;
                back.y = 0f;
            }
            if (back.sqrMagnitude < 0.0001f)
                return;
            aroundDirection = back.normalized;
            aroundUntil = Time.time + 1f;
            aroundForced = true;
        }

        private void StuckCrouchedSidestep(float side)
        {
            if (!playerController.isCrouching || currentTargetNode == null)
                return;
            Vector3 toNode = currentTargetNode.position - transform.position;
            toNode.y = 0f;
            if (toNode.sqrMagnitude < 0.0001f)
                return;
            aroundDirection = (Vector3.Cross(Vector3.up, toNode.normalized) * side + toNode.normalized * 0.3f).normalized;
            aroundUntil = Time.time + 0.8f;
            aroundForced = true;
        }

        private void HandleStuckDetection()
        {
            if (playerController.inSpecialInteractAnimation)
            {
                ResetStuckTracking();
                return;
            }

            if (Time.time - stuckCheckTime < STUCK_CHECK_INTERVAL)
                return;

            // Progress means getting closer to the node, not just moving: shuffling left and right
            // against a wall moves plenty and gets nowhere.
            bool tryingToMove = AutoMoveInput.sqrMagnitude > 0.01f;
            float distance = currentTargetNode != null ? HorizontalDistance(transform.position, currentTargetNode.position) : 0f;
            // A new node counts only if it is somewhere else: a replan rebuilds the same node at
            // the same spot, and treating that as progress reset the count before it ever stopped.
            bool newNode = currentTargetNode != stuckNode &&
                           (stuckNode == null || currentTargetNode == null ||
                            Vector3.Distance(currentTargetNode.position, stuckNodePosition) > STUCK_MIN_PROGRESS);
            // ...and only if the player got anywhere: a replan from a standstill can lay the next
            // node somewhere new, and counting that reset the count every second check. Wedged
            // under the lip of a low rock slope in Adamance's mine, that stood 14 s in silence.
            bool moved = stuckNode == null ||
                         HorizontalDistance(transform.position, stuckCheckPosition) >= STUCK_MIN_PROGRESS;
            // The distance to a node laid somewhere else is no measure against the old one.
            if (newNode && !moved)
                stuckBestDistance = distance;
            bool progressed = (newNode && moved) || stuckBestDistance - distance >= STUCK_MIN_PROGRESS;
            if (progressed || distance < stuckBestDistance)
            {
                stuckBestDistance = distance;
            }
            if (progressed)
                lastProgressPosition = transform.position;
            stuckNode = currentTargetNode;
            if (currentTargetNode != null)
                stuckNodePosition = currentTargetNode.position;
            stuckCheckPosition = transform.position;
            stuckCheckTime = Time.time;

            // Standing still with nothing to wait for (the waits at doors, ladders and crawl
            // spaces reset this every frame) is stuck too, only quieter.
            if (!tryingToMove && !progressed && !playerController.isClimbingLadder)
            {
                stuckIdleChecks++;
                if (stuckIdleChecks < 3)
                    return;
                stuckIdleChecks = 0;
            }
            else if (!tryingToMove || progressed)
            {
                stuckIdleChecks = 0;
                stuckCount = 0;
                return;
            }

            stuckCount++;
            Debug.Log($"Pathfinder: stuck ({stuckCount})");

            if (stuckCount == 1)
            {
                // Whatever the probe missed: try a jump and the other side.
                Probe?.Jump("stuck 1");
                aroundUntil = 0f;
                Probe?.ResetSide(UnityEngine.Random.value < 0.5f ? -1 : 1);
                StuckCrouchedBackOut();
            }
            else if (stuckCount == 2)
            {
                if (currentTargetNode != null)
                {
                    autoStuckCuts = HorizontalDistance(transform.position, autoStuckCutPosition) < 4f ? autoStuckCuts + 1 : 0;
                    autoStuckCutPosition = transform.position;
                    Vector3 heading = currentTargetNode.position - transform.position;
                    heading.y = 0f;
                    float size = 1.2f + 1.3f * Mathf.Min(autoStuckCuts, 3);
                    // Centred further ahead as it grows, so it never covers where the player stands.
                    RememberBlockedSpot(transform.position + (heading.sqrMagnitude > 0.0001f ? heading.normalized : Vector3.zero) * (0.6f + size * 0.5f), size);
                }
                lastReplanTime = -999f;
                Replan(null);
            }
            else if (stuckCount == 3)
            {
                // Something neither the NavMesh nor the probe can get past: one more jump and
                // the other side before giving up. Never a teleport.
                Probe?.Jump("stuck 3");
                aroundUntil = 0f;
                Probe?.ResetSide(UnityEngine.Random.value < 0.5f ? -1 : 1);
                StuckCrouchedSidestep(-1f);
            }
            else if (stuckCount >= 5)
            {
                StopPathfinding(playSound: false);
                Utilities.SpeakText("Stuck. Pathfinding stopped.");
            }
        }

        private float slopeStallSince = -1f;
        private float noAutoCrouchUntil = -1f;
        private float slopeWiggleSide = 1f;
        private Vector3 slopeStallPosition;
        private float lastSlopeJumpTime = -999f;

        /// <summary>
        /// Auto mode pushing at a short steep lip the controller won't walk up (it stops dead on
        /// ground steeper than its slope limit, and the obstacle probe sees no obstacle): jump it
        /// after 0.4 s, the way a player does, instead of waiting out the stuck check.
        /// </summary>
        private static readonly FieldInfo PlayerSlidingField = AccessTools.Field(typeof(PlayerControllerB), "isPlayerSliding");

        private bool IsPlayerSliding() =>
            PlayerSlidingField != null && (bool)PlayerSlidingField.GetValue(playerController);

        /// <summary>Walk up a slope at 35 degrees to one side for a moment, the other side next time.</summary>
        private void AngleOffSlope(Vector3 ahead, float rise, string why)
        {
            lastSlopeJumpTime = Time.time - 0.5f;
            slopeStallSince = -1f;
            slopeWiggleSide = -slopeWiggleSide;
            aroundDirection = Quaternion.Euler(0f, 35f * slopeWiggleSide, 0f) * ahead;
            aroundUntil = Time.time + 0.7f;
            aroundForced = true;
            Debug.Log($"Pathfinder: {why} on a {rise:F1} m lip at {transform.position}, angling {(slopeWiggleSide > 0 ? "right" : "left")}");
        }

        private static readonly float[] SlopeSampleDistances = { 0.3f, 0.45f, 0.6f };

        /// <summary>Rising ground just ahead that stops the controller and a jump clears: a face
        /// steeper than the slope limit from 0.12 m, anything from 0.2 m.</summary>
        private bool SteepLipAhead(Vector3 ahead, out float rise)
        {
            rise = float.NegativeInfinity;
            bool steepFace = false;
            float steepNormalY = Mathf.Cos((playerController.thisController.slopeLimit + 1f) * Mathf.Deg2Rad);
            foreach (float d in SlopeSampleDistances)
            {
                if (Physics.Raycast(transform.position + ahead * d + Vector3.up * 2.5f, Vector3.down, out RaycastHit hit, 3.5f, groundMask, QueryTriggerInteraction.Ignore))
                {
                    rise = Mathf.Max(rise, hit.point.y - transform.position.y);
                    if (hit.normal.y < steepNormalY && hit.point.y > transform.position.y + 0.05f)
                        steepFace = true;
                }
            }
            return rise >= (steepFace ? 0.12f : 0.2f) && Probe != null && rise <= Probe.MaxJumpHeight;
        }

        private Vector3 slopeDebugPosition;
        private float slopeDebugSince;
        private float slopeDebugLogTime;

        private void UpdateSlopeJump()
        {
            CharacterController controller = playerController.thisController;
            // Why a stall isn't jumped, once a second while standing still with input held.
            bool stillHere = AutoMoveInput.sqrMagnitude >= 0.01f && HorizontalDistance(transform.position, slopeDebugPosition) < 0.15f;
            if (!stillHere)
            {
                slopeDebugPosition = transform.position;
                slopeDebugSince = Time.time;
            }
            bool explain = stillHere && Time.time - slopeDebugSince > 1.5f && Time.time - slopeDebugLogTime > 1f;
            if (explain)
                slopeDebugLogTime = Time.time;
            if (explain && (!controller.isGrounded || gapJumpInProgress || playerController.isClimbingLadder))
                Debug.Log($"Pathfinder: stalled at {transform.position}, no lip jump: grounded={controller.isGrounded} gapJump={gapJumpInProgress} ladder={playerController.isClimbingLadder}");
            // Stalled = not getting anywhere. Not the controller's velocity: pressed into rock it
            // flickers between 0.2 and 0.6 m/s standing still, and kept restarting the count.
            if (AutoMoveInput.sqrMagnitude < 0.01f || !controller.isGrounded ||
                playerController.isClimbingLadder || gapJumpInProgress ||
                (slopeStallSince >= 0f && HorizontalDistance(transform.position, slopeStallPosition) > 0.15f))
            {
                slopeStallSince = -1f;
            }
            if (AutoMoveInput.sqrMagnitude < 0.01f || !controller.isGrounded ||
                playerController.isClimbingLadder || gapJumpInProgress)
                return;
            if (slopeStallSince < 0f)
            {
                slopeStallSince = Time.time;
                slopeStallPosition = transform.position;
            }
            if (Time.time - slopeStallSince < 0.4f || Time.time - lastSlopeJumpTime < 1.5f)
            {
                if (explain)
                    Debug.Log($"Pathfinder: stalled at {transform.position}, lip jump waiting: stallFor={Time.time - slopeStallSince:F2} sinceLast={Time.time - lastSlopeJumpTime:F1}");
                return;
            }

            Transform body = playerController.thisPlayerBody;
            Vector3 ahead = body.TransformDirection(new Vector3(AutoMoveInput.x, 0f, AutoMoveInput.y));
            ahead.y = 0f;
            if (ahead.sqrMagnitude < 0.0001f || Probe == null)
                return;
            ahead.Normalize();
            // The highest ground a short step ahead: a rock bump in Adamance's flooded tunnel rose
            // 0.21 m at 0.4 m and 0.19 m at 0.6 m, and one sample read it as nothing to jump.
            float rise = float.NegativeInfinity;
            bool steepFace = false;
            float steepNormalY = Mathf.Cos((playerController.thisController.slopeLimit + 1f) * Mathf.Deg2Rad);
            foreach (float d in SlopeSampleDistances)
            {
                if (Physics.Raycast(transform.position + ahead * d + Vector3.up * 2.5f, Vector3.down, out RaycastHit hit, 3.5f, groundMask, QueryTriggerInteraction.Ignore))
                {
                    rise = Mathf.Max(rise, hit.point.y - transform.position.y);
                    if (hit.normal.y < steepNormalY && hit.point.y > transform.position.y + 0.05f)
                        steepFace = true;
                }
            }
            if (explain)
                Debug.Log($"Pathfinder: stalled at {transform.position}, lip ahead rise={rise:F2} steepFace={steepFace} sliding={IsPlayerSliding()} crouched={playerController.isCrouching} " +
                          $"target={(currentTargetNode != null ? currentTargetNode.position.ToString() : "none")} sinceLast={Time.time - lastSlopeJumpTime:F1} stallFor={(slopeStallSince < 0f ? -1f : Time.time - slopeStallSince):F1}");
            if (float.IsNegativeInfinity(rise))
                return;
            // From 0.2 m, not the step height: a 0.35 m rock lip with a 67 degree face stopped the
            // controller dead in Adamance's mine, though it is under stepOffset. A stall here
            // already shows the step didn't take.
            bool sliding = IsPlayerSliding();
            // A face steeper than the slope limit stops the controller from 0.12 m: that bump read
            // 0.19-0.21 m depending on where the player stood, either side of 0.2.
            if (rise < (sliding ? 0.1f : steepFace ? 0.12f : 0.2f) || rise > Probe.MaxJumpHeight)
            {
                // Crouched and stopped dead with nothing to jump: the crouched capsule snagged on
                // rock (Adamance's flooded tunnel stood a player 5.5 s on flat ground until the
                // stuck handling backed them out). Angle off as on a lip, sooner.
                if (playerController.isCrouching && Time.time - slopeStallSince >= 1.2f)
                    AngleOffSlope(ahead, Mathf.Max(rise, 0f), "crouched and stopped");
                return;
            }
            // Only where the path climbs: on Offense a downhill path met a rock beside it, and the
            // jump put the player on top of the rock, 5 m over the path.
            // (Above the feet at all, not by a whole step: past a 0.35 m lip the next node was
            // only 0.25 m up.)
            // A bump under half a metre is jumped wherever the path goes: on top of it is still
            // on the path. (That flooded-tunnel bump sat on a flat path and stood the player 4 s.)
            if (currentTargetNode == null || (currentTargetNode.position.y - NODE_GROUND_OFFSET < transform.position.y + 0.1f && rise > 0.5f))
                return;
            // Crouched under low rock (Adamance's mine tunnels): the game won't jump a crouched
            // player, and the crouched controller stalled 7 s on a 60 degree bump. Stand where
            // there is room and jump at once, before the low rock past the lip crouches it again.
            // Standing on the steep face of the lip: the game pushes a sliding player down the
            // slope against the input, and won't let them jump for 2.5 s (Jump_performed checks
            // playerSlidingTimer). Angle off as below, onto ground the slide doesn't take.
            if (sliding && !playerController.isCrouching)
            {
                AngleOffSlope(ahead, rise, "sliding");
                return;
            }
            if (playerController.isCrouching)
            {
                // No room to stand either. Rock slopes are seldom equally steep across: in the
                // mine a crouched player stopped dead on one 0.6 m strip and walked up 35 degrees
                // either side of it. Angle off, one side then the other.
                if (!Probe.RoomToStand(ahead, 0f))
                {
                    AngleOffSlope(ahead, rise, "crouched");
                    return;
                }
                playerController.Crouch(false);
                if (playerController.isCrouching)
                    return;
                autoCrouched = false;
                noAutoCrouchUntil = Time.time + 0.8f;
                // Taken for a crawl (low rock reads like a rack's underside): drop it, or the
                // crawl crouches the player straight back down before the jump.
                if (crawl.active)
                {
                    crawl.active = false;
                    crawlRejectedEntry = crawl.entry;
                    crawlRejectedUntil = Time.time + 15f;
                }
            }
            lastSlopeJumpTime = Time.time;
            slopeStallSince = -1f;
            if (Probe.Jump("lip"))
                Debug.Log($"Pathfinder: jumping a {rise:F1} m lip at {transform.position}");
        }

        private readonly Dictionary<DoorLock, bool> nearDoorOpen = new Dictionary<DoorLock, bool>();
        private float doorScanTime;
        private float doorReplanAt = -1f;
        private float doorOpenedAt = -999f;

        /// <summary>
        /// A door that opens near the player swings its panel into the room, often across the
        /// path laid through the closed doorway. Replan once it has finished swinging.
        /// </summary>
        private void UpdateDoorReplan()
        {
            if (doorReplanAt > 0f && Time.time >= doorReplanAt)
            {
                doorReplanAt = -1f;
                if (!gapJumpInProgress && !hasGap && !crawl.active && !playerController.isClimbingLadder)
                {
                    lastReplanTime = -999f;
                    Replan(null);
                }
                return;
            }
            if (Time.time - doorScanTime < 0.25f)
                return;
            doorScanTime = Time.time;

            foreach (DoorLock door in GetDoors())
            {
                if (door == null)
                    continue;
                if (Vector3.Distance(door.transform.position, transform.position) > 4f)
                {
                    nearDoorOpen.Remove(door);
                    continue;
                }
                bool open = !DoorIsClosed(door);
                if (nearDoorOpen.TryGetValue(door, out bool wasOpen) && open && !wasOpen)
                {
                    doorReplanAt = Time.time + 0.8f;
                }
                nearDoorOpen[door] = open;
            }
        }

        private DoorLock[] GetDoors()
        {
            if (cachedDoors == null || Time.time - cachedDoorsTime > DOOR_CACHE_REFRESH_INTERVAL)
            {
                cachedDoors = FindObjectsOfType<DoorLock>();
                cachedDoorsTime = Time.time;
            }
            return cachedDoors;
        }

        /// <summary>
        /// Opens the closed door the path goes through the way a player does: hold interact on it
        /// for its hold time (0.3 s on an unlocked door), then the trigger fires DoorLock.OpenOrCloseDoor,
        /// which plays the animation and sound and syncs it. Doors to the side or behind are left
        /// alone, and an open door is never closed.
        /// </summary>
        private void OpenNearbyDoor()
        {
            DoorLock door = FindDoorAhead();
            if (door != doorBeingOpened)
            {
                doorBeingOpened = door;
                doorHoldTime = 0f;
                doorWaitTime = 0f;
            }
            if (door == null)
                return;

            InteractTrigger trigger = door.doorTrigger;
            if (!CanUseTrigger(trigger))
            {
                doorHoldTime = 0f;
                return;
            }

            doorHoldTime += Time.deltaTime * Mathf.Max(trigger.timeToHoldSpeedMultiplier, 0.01f);
            if (doorHoldTime < (trigger.holdInteraction ? trigger.timeToHold : 0f))
                return;

            doorHoldTime = 0f;
            try
            {
                trigger.Interact(playerController.thisPlayerBody);
                doorOpenedAt = Time.time;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error opening door: {ex.Message}");
            }
        }

        private static bool DoorIsClosed(DoorLock door)
        {
            AnimatedObjectTrigger animated = door.GetComponent<AnimatedObjectTrigger>();
            return animated != null && !animated.boolValue;
        }

        /// <summary>The closest closed, unlocked door within reach, on this floor and in the direction of travel.</summary>
        private DoorLock FindDoorAhead()
        {
            DoorLock[] doors = GetDoors();
            if (doors == null || currentTargetNode == null)
                return null;

            Vector3 heading = currentTargetNode.position - transform.position;
            heading.y = 0f;
            heading = heading.sqrMagnitude > 0.01f ? heading.normalized : transform.forward;

            DoorLock best = null;
            float bestDistance = DOOR_DETECTION_DISTANCE;
            foreach (var door in doors)
            {
                if (door == null || door.isLocked || door.doorTrigger == null || !DoorIsClosed(door))
                    continue;

                Vector3 toDoor = door.transform.position - transform.position;
                if (Mathf.Abs(toDoor.y) > 2.5f)
                    continue;
                toDoor.y = 0f;
                float distance = toDoor.magnitude;
                if (distance >= bestDistance)
                    continue;
                if (distance > 0.8f && Vector3.Dot(toDoor / distance, heading) < 0.3f)
                    continue;

                bestDistance = distance;
                best = door;
            }
            return best;
        }

        /// <summary>The game's own checks before an interact press counts (PlayerControllerB.Interact_performed
        /// and InteractTriggerUseConditionsMet), minus aiming at the trigger.</summary>
        private bool CanUseTrigger(InteractTrigger trigger)
        {
            PlayerControllerB p = playerController;
            if (trigger == null || p == null || !trigger.interactable || trigger.isPlayingSpecialAnimation || p.disableInteract)
                return false;
            if (p.isHoldingObject && !trigger.oneHandedItemAllowed)
                return false;
            if (p.twoHanded && (!trigger.twoHandedItemAllowed || trigger.specialCharacterAnimation))
                return false;
            if (p.sinkingValue > 0.73f)
                return false;
            if (p.jetpackControls && (trigger.specialCharacterAnimation || trigger.isLadder))
                return false;
            return true;
        }

        /// <summary>Said once per ladder when the player's hands stop them using it.</summary>
        private void SayCannotClimb(InteractTrigger ladder)
        {
            if (cannotClimbCueLadder == ladder)
                return;
            cannotClimbCueLadder = ladder;
            Utilities.SpeakText(playerController.twoHanded
                ? "Can't climb while carrying a two-handed item. Drop it to use the ladder."
                : "Can't use the ladder right now.");
        }
        #endregion

        #region Path Visualization
        private void SetupLineRenderer()
        {
            lineRenderer.startWidth = 0.2f;
            lineRenderer.endWidth = 0.2f;
            lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
            lineRenderer.startColor = Color.yellow;
            lineRenderer.endColor = Color.yellow;
            lineRenderer.positionCount = 0;
            lineRenderer.enabled = true;
        }

        private readonly List<Vector3> linePoints = new List<Vector3>();

        private void DrawPath()
        {
            if (lineRenderer == null)
                return;

            linePoints.Clear();
            if (ConfigManager.EnableVisualMarkers.Value)
            {
                linePoints.Add(transform.position + Vector3.up * NODE_GROUND_OFFSET);
                foreach (var node in pathNodes)
                {
                    if (!node.isReached)
                    {
                        linePoints.Add(node.position);
                    }
                }
            }

            if (linePoints.Count < 2)
            {
                lineRenderer.positionCount = 0;
                return;
            }

            Color color = currentMode == PathfindingMode.Auto ? Color.red : Color.yellow;
            lineRenderer.startColor = color;
            lineRenderer.endColor = color;
            lineRenderer.positionCount = linePoints.Count;
            for (int i = 0; i < linePoints.Count; i++)
            {
                lineRenderer.SetPosition(i, linePoints[i]);
            }
        }

        private void ClearPath()
        {
            if (lineRenderer != null)
            {
                lineRenderer.positionCount = 0;
            }
        }
        #endregion

        #region Stance announcements
        // "Crouched", "Standing", "Sprinting", "Walking" on each change, whoever caused it (the
        // player or auto mode). A change has to hold this long first, so a flicker is not spoken:
        // auto mode lets go of sprint for a moment at corners and sidesteps.
        private const float STANCE_SETTLE_TIME = 0.25f;
        private const float SPRINT_SETTLE_TIME = 0.8f;
        private bool stanceKnown;
        private bool spokenCrouching;
        private bool spokenSprinting;
        private float stanceChangedAt;

        private void UpdateStanceAnnouncements()
        {
            if (playerController == null || playerController.isPlayerDead || !playerController.isPlayerControlled)
            {
                stanceKnown = false;
                return;
            }
            bool crouching = playerController.isCrouching;
            bool sprinting = playerController.isSprinting && !crouching;
            if (!stanceKnown)
            {
                stanceKnown = true;
                spokenCrouching = crouching;
                spokenSprinting = sprinting;
                return;
            }
            if (crouching == spokenCrouching && sprinting == spokenSprinting)
            {
                stanceChangedAt = Time.time;
                return;
            }
            if (Time.time - stanceChangedAt < (crouching != spokenCrouching ? STANCE_SETTLE_TIME : SPRINT_SETTLE_TIME))
                return;

            // Crouching ends a sprint by itself; say the crouch, not "Walking" as well.
            if (crouching != spokenCrouching)
            {
                Utilities.SpeakText(crouching ? "Crouched" : "Standing");
            }
            else
            {
                Utilities.SpeakText(sprinting ? "Sprinting" : "Walking");
            }
            spokenCrouching = crouching;
            spokenSprinting = sprinting;
            stanceChangedAt = Time.time;
        }
        #endregion

        #region Update and Cleanup
        private void Update()
        {
            try
            {
                UpdateCrouchStepOffset();
                UpdateStanceAnnouncements();

                if (!isPathfinding)
                    return;

                if (playerController != null && playerController.isPlayerDead)
                {
                    StopPathfinding(playSound: false);
                    Utilities.SpeakText("Pathfinding stopped because the player is dead.");
                    return;
                }

                if (!UpdateNodeProgress())
                    return;

                if (currentTargetNode != null &&
                    HorizontalDistance(transform.position, currentTargetNode.position) > OFF_PATH_DISTANCE)
                {
                    Replan("Recalculating path");
                    if (!isPathfinding)
                        return;
                }

                if (carveReplanFrame > 0 && Time.frameCount >= carveReplanFrame)
                {
                    carveReplanFrame = -1;
                    GameObject cut = trialCut;
                    bool reachedBefore = trialCutReachedBefore;
                    trialCut = null;
                    lastReplanTime = -999f;
                    Replan(null);
                    if (!isPathfinding)
                        return;
                    // The cut took away the only way there: put it back and keep the spot.
                    if (cut != null && reachedBefore && pathIsPartial)
                    {
                        keptLowSpots.Add(cut.transform.position);
                        carvedSpots.Remove(cut);
                        Destroy(cut);
                        Debug.Log($"Pathfinder: cut at {cut.transform.position} left no way there; undone");
                        carveReplanFrame = Time.frameCount + 2;
                    }
                }

                OpenNearbyDoor();
                UpdateDoorReplan();
                UpdateHazardCue();
                UpdateLadderLetGo();
                UpdateGapJumpState();
                UpdateGapScan();

                if (currentMode == PathfindingMode.Manual)
                {
                    UpdateManualAudio();
                    UpdateManualElevator();
                    // Crawl first: a crawl space's own cue replaces the plain crouch cue.
                    UpdateManualCrawl();
                    UpdateManualObstacleCues();
                    UpdateManualGap();
                }
                else
                {
                    UpdateAutoWalk();
                    UpdateSlopeJump();
                    HandleStuckDetection();
                }

                if (isPathfinding)
                {
                    DrawPath();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in Pathfinder.Update: {ex.Message}\n{ex.StackTrace}");
                StopPathfinding(playSound: false);
                Utilities.SpeakText("Pathfinding stopped due to an error.");
            }
        }

        private void OnDestroy()
        {
            try
            {
                StopAllCoroutines();
                ClearAutoInput();
                ShouldPreventFallDamage = false;
                ClearPathNodes();

                foreach (var nodeObj in nodeObjectPool)
                {
                    if (nodeObj != null)
                    {
                        Destroy(nodeObj);
                    }
                }
                nodeObjectPool.Clear();

                if (Instance == this)
                {
                    Instance = null;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in Pathfinder.OnDestroy: {ex.Message}");
            }
        }
        #endregion

        #region Utility Methods
        private Mesh CreateSphereMesh(float radius)
        {
            GameObject tempSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Mesh source = tempSphere.GetComponent<MeshFilter>().sharedMesh;
            Mesh mesh = new Mesh { name = "NodeSphere" };

            // The primitive has a 0.5 radius.
            Vector3[] vertices = source.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] *= radius / 0.5f;
            }
            mesh.vertices = vertices;
            mesh.triangles = source.triangles;
            mesh.normals = source.normals;
            mesh.uv = source.uv;

            Destroy(tempSphere);
            return mesh;
        }

        private static AudioClip GenerateClickSound()
        {
            int sampleRate = 44100;
            int length = (int)(sampleRate * 0.05f);
            AudioClip clip = AudioClip.Create("ClickSound", length, 1, sampleRate, false);

            float[] samples = new float[length];
            for (int i = 0; i < length; i++)
            {
                float t = (float)i / sampleRate;
                float envelope = Mathf.Exp(-(float)i / length * 10f);
                samples[i] = Mathf.Sin(2 * Mathf.PI * 2000f * t) * envelope * 0.6f;
            }

            clip.SetData(samples, 0);
            return clip;
        }
        #endregion
    }

    /// <summary>
    /// Feeds auto pathfinding into PlayerControllerB.Update by wrapping the two input reads:
    ///   moveInputVector = InputSystem.actions.FindAction("Move").ReadValue&lt;Vector2&gt;();
    ///   float num = InputSystem.actions.FindAction("Sprint").ReadValue&lt;float&gt;();
    /// Everything after those lines (menu and typing checks, walking/sprinting animator bools,
    /// stamina, CharacterController.Move, network sync) then runs on the auto values exactly as
    /// it does on the keyboard's. The keyboard's movement is replaced, not added to, while auto
    /// mode walks.
    /// </summary>
    [HarmonyPatch(typeof(PlayerControllerB), "Update")]
    internal static class AutoWalkInputPatch
    {
        private static readonly MethodInfo FilterMoveMethod = AccessTools.Method(typeof(AutoWalkInputPatch), nameof(FilterMove));
        private static readonly MethodInfo FilterSprintMethod = AccessTools.Method(typeof(AutoWalkInputPatch), nameof(FilterSprint));

        public static Vector2 FilterMove(Vector2 keyboard, PlayerControllerB player)
        {
            if (!IsPathfinderPlayer(player))
                return keyboard;

            if (!Pathfinder.AutoWalkActive)
            {
                // Manual mode: the gap edge guard. The filter works in the body's frame, so undo
                // the flipped-camera negation Update applies after this read, then redo it.
                bool flip = IngamePlayerSettings.Instance != null && IngamePlayerSettings.Instance.flipCamera;
                Vector2 raw = flip ? new Vector2(-keyboard.x, keyboard.y) : keyboard;
                Vector2 filtered = Pathfinder.Instance.FilterManualMove(raw);
                return flip ? new Vector2(-filtered.x, filtered.y) : filtered;
            }

            // Update negates x right after this read when the camera is flipped; undo that here so
            // the auto input means what it says.
            Vector2 input = Pathfinder.AutoMoveInput;
            if (IngamePlayerSettings.Instance != null && IngamePlayerSettings.Instance.flipCamera)
            {
                input.x = -input.x;
            }
            return input;
        }

        public static float FilterSprint(float keyboard, PlayerControllerB player)
        {
            if (!IsPathfinderPlayer(player))
                return keyboard;
            if (!Pathfinder.AutoWalkActive)
                return Pathfinder.Instance.FilterManualSprint(keyboard);
            return Pathfinder.AutoSprintInput ? 1f : 0f;
        }

        private static bool IsPathfinderPlayer(PlayerControllerB player)
        {
            return Pathfinder.Instance != null && Pathfinder.Instance.playerController == player;
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            bool move = WrapRead(codes, "Move", typeof(Vector2), FilterMoveMethod);
            bool sprint = WrapRead(codes, "Sprint", typeof(float), FilterSprintMethod);
            if (!move || !sprint)
            {
                Debug.LogError($"[LethalAccess] AutoWalkInputPatch: input read not found (Move {move}, Sprint {sprint}); auto pathfinding cannot walk.");
            }
            return codes;
        }

        /// <summary>
        /// After the first ReadValue&lt;T&gt;() that follows ldstr actionName, inserts
        /// ldarg.0; call filter(T, PlayerControllerB).
        /// </summary>
        private static bool WrapRead(List<CodeInstruction> codes, string actionName, Type valueType, MethodInfo filter)
        {
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode != System.Reflection.Emit.OpCodes.Ldstr || !(codes[i].operand is string s) || s != actionName)
                    continue;

                for (int j = i + 1; j < codes.Count && j < i + 8; j++)
                {
                    if (codes[j].operand is MethodInfo m && m.Name == "ReadValue" && m.ReturnType == valueType)
                    {
                        codes.Insert(j + 1, new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_0));
                        codes.Insert(j + 2, new CodeInstruction(System.Reflection.Emit.OpCodes.Call, filter));
                        return true;
                    }
                }
            }
            return false;
        }
    }

    /// <summary>
    /// A new round means every scrap instance is new. ItemIdentifier keys its numbering on
    /// GetInstanceID(), which Unity recycles, so carrying the old table over can attach a previous
    /// item's number to an unrelated new item.
    /// </summary>
    [HarmonyPatch(typeof(StartOfRound), "StartGame")]
    class StartOfRoundNewRoundPatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            try
            {
                ItemIdentifier.ResetForNewRound();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in StartOfRoundNewRoundPatch.Postfix: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// No fall damage while auto pathfinding walks the player. NavMesh paths can run over
    /// off-mesh links that drop further than a player would choose to.
    /// </summary>
    [HarmonyPatch(typeof(PlayerControllerB))]
    class PlayerControllerBFallDamagePatch
    {
        [HarmonyPrefix]
        [HarmonyPatch("PlayerHitGroundEffects")]
        static void Prefix(PlayerControllerB __instance)
        {
            // Landing sound and noise still happen; only the damage is skipped.
            if (Pathfinder.ShouldPreventFallDamage && Pathfinder.Instance != null && Pathfinder.Instance.playerController == __instance)
            {
                __instance.takingFallDamage = false;
            }
        }
    }
}
