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
        private const float NODE_REACH_HEIGHT = 2.5f;

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
        private bool pathIsPartial;

        // Ladders. The NavMesh crosses them with off-mesh links; a player crosses them by using
        // the ladder trigger and climbing.
        private InteractTrigger[] cachedLadders;
        private float cachedLaddersTime = -999f;
        private float ladderDirection = 1f;
        private float nextLadderTry;
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
                "PlayerRagdoll", "MapHazards", "InteractableObject", "Railing", "Props", "PhysicsObject", "ScanNode");
            return mask;
        }

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
            if (autoCrouched && playerController != null && playerController.isCrouching)
            {
                playerController.Crouch(false);
            }
            autoCrouched = false;
            ResetGapState();
            manualCrouchCued = false;
            manualLadder = null;
            lockedDoorOnPath = null;
            lockedDoorCrossIndex = -1;
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
        private bool BuildPath()
        {
            NavMeshHit startHit;
            if (!NavMesh.SamplePosition(transform.position, out startHit, 5f, NavMesh.AllAreas))
            {
                Debug.LogWarning("Pathfinder: no NavMesh near the player");
                return false;
            }

            // The NavMesh is baked for enemies, and its Jump and Climb links are moves only they
            // can make (Experimentation has one straight up onto the raised bridge). Plan without
            // them, and use them only when there is no complete path otherwise.
            NavMeshPath navPath = new NavMeshPath();
            bool planned = NavMesh.CalculatePath(startHit.position, currentDestination, PlayerAreaMask(), navPath);
            if (!planned || navPath.status != NavMeshPathStatus.PathComplete)
            {
                NavMeshPath anyPath = new NavMeshPath();
                if (NavMesh.CalculatePath(startHit.position, currentDestination, NavMesh.AllAreas, anyPath) &&
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
            float normalLength = pathIsPartial ? float.MaxValue : PathLength(navPath.corners);
            if (TryKeyRoute(startHit.position, normalLength, out Vector3[] toDoor, out Vector3[] fromDoor, out DoorLock door))
            {
                // Two halves laid separately: the straight line through the doorway crosses
                // carved NavMesh, which the surface walk would snap off to one side.
                positions = BuildNodePositions(AvoidQuicksand(startHit.position, toDoor), finalLeg: false);
                lockedDoorCrossIndex = positions.Count;
                positions.AddRange(BuildNodePositions(AvoidQuicksand(fromDoor[0], fromDoor)));
                lockedDoorOnPath = door;
                pathIsPartial = false;
            }
            else
            {
                positions = BuildNodePositions(AvoidQuicksand(startHit.position, navPath.corners));
            }

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
            if (NavMesh.CalculatePath(from, to, PlayerAreaMask(), path) && path.status == NavMeshPathStatus.PathComplete)
                return path.corners;
            if (NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete)
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
                    if (sinceLastNode >= NODE_SPACING || (atCorner && sinceLastNode >= CORNER_NODE_MIN_GAP))
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

            return positions;
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
            if (Time.time - lastReplanTime < REPLAN_COOLDOWN)
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
                    MarkReachedThrough(node, announce: currentMode == PathfindingMode.Manual);
                    break;
                }
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
                    if (!PassedNode(node, pathNodes[i + 1]))
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
            StopPathfinding(playSound: true);
            if (wasAuto && currentTargetObject != null)
            {
                StartCoroutine(FaceTarget(currentTargetObject));
            }

            // currentTargetObject is kept so the player can still look at it afterwards.
            Utilities.SpeakText(partial ? $"Cannot get any closer to {name}" : $"Reached {name}");
        }

        private string GetTargetName()
        {
            if (currentTargetObject == null)
                return "destination";
            return LACore.Instance?.navMenu?.GetDisplayNameForObject(currentTargetObject) ?? currentTargetObject.name;
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
            manualWasCrouching = crouching;
            if (crouching && manualCrouchCued && manualProbe.kind == ObstacleProbe.Kind.Clear &&
                     !Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.up, 2.2f, StartOfRound.Instance.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore))
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
                if (manualProbe.kind == ObstacleProbe.Kind.Jumpable)
                {
                    lastJumpCueTime = Time.time;
                    Utilities.SpeakText("Jump");
                }
                else if (manualProbe.kind == ObstacleProbe.Kind.Crouch && !playerController.isCrouching)
                {
                    lastJumpCueTime = Time.time;
                    manualCrouchCued = true;
                    Utilities.SpeakText("Crouch");
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
            Utilities.SpeakText(DescribeBlocked(manualProbe));
        }

        private string DescribeBlocked(ObstacleProbe.Result result)
        {
            // The wall is the climb itself; the way on is the ladder.
            if (manualLadder != null)
            {
                Vector3 ladder = LadderMountPoint(manualLadder, manualLadderDown);
                int meters = Mathf.Max(1, Mathf.RoundToInt(HorizontalDistance(transform.position, ladder)));
                return $"Ladder {DescribeDirection(ladder)}, {meters} {(meters == 1 ? "meter" : "meters")}";
            }

            if (result.kind == ObstacleProbe.Kind.Jumpable)
                return "Blocked, jump";
            if (result.kind == ObstacleProbe.Kind.Crouch)
            {
                manualCrouchCued = true;
                return "Blocked, crouch";
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

            // Nothing ahead at body height, so something low is catching the feet.
            return "Blocked, jump";
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
            if (ManualGapGuide(out Vector3 guide))
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
                if (ladder.bottomOfLadderPosition.position.y > low + 1.5f || ladder.topOfLadderPosition.position.y < high - 1.5f)
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

            Transform body = playerController.thisPlayerBody != null ? playerController.thisPlayerBody : transform;

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
                    // A link only enemies can climb, on a path that had no other way: cross it the
                    // way the old agent did instead of jumping at a wall until the stuck check
                    // gives up.
                    // Only from the ground: a player falling past a link is not at its foot, and
                    // the path is replanned from wherever they land.
                    if (toNode.magnitude < 3f && playerController.thisController.isGrounded)
                    {
                        playerController.TeleportPlayer(nodeGround);
                        ResetStuckTracking();
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

            // Face along the path.
            CenterCamera(body);
            if (TurnBodyToward(body, pathDirection) > AUTO_TURN_IN_PLACE_ANGLE)
            {
                ClearAutoInput();
                return;
            }

            // Move wherever the way is actually free.
            Vector3 moveDirection = pathDirection;
            bool clear = true;
            ObstacleProbe probeNow = Probe;
            if (probeNow != null)
            {
                bool dropAhead = currentTargetNode.position.y - NODE_GROUND_OFFSET < transform.position.y - 1f;
                ObstacleProbe.Result ahead = probeNow.Assess(pathDirection, ProbeDistance(), dropAhead);
                switch (ahead.kind)
                {
                    case ObstacleProbe.Kind.Jumpable:
                        clear = false;
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
                        if (Time.time >= nextJumpTime && probeNow.Jump())
                        {
                            nextJumpTime = Time.time + JUMP_COOLDOWN;
                        }
                        break;

                    case ObstacleProbe.Kind.Crouch:
                        clear = false;
                        if (!playerController.isCrouching)
                        {
                            playerController.Crouch(true);
                            autoCrouched = playerController.isCrouching;
                        }
                        break;

                    case ObstacleProbe.Kind.Wall:
                        clear = false;
                        if (ahead.aroundDirection != Vector3.zero)
                        {
                            aroundDirection = ahead.aroundDirection;
                            aroundUntil = Time.time + AROUND_HOLD_TIME;
                        }
                        break;
                }

                // Stand back up once the way ahead is clear at full height. Crouch(false) checks
                // the space overhead itself and does nothing if the player would not fit.
                if (ahead.kind == ObstacleProbe.Kind.Clear && autoCrouched && playerController.isCrouching)
                {
                    playerController.Crouch(false);
                    autoCrouched = playerController.isCrouching;
                }

                // Keep sidestepping briefly after the probe clears, or the player turns back into
                // the corner it just got past.
                if (Time.time < aroundUntil && aroundDirection != Vector3.zero)
                {
                    moveDirection = aroundDirection;
                    clear = false;
                }
            }

            Vector3 local = body.InverseTransformDirection(moveDirection);
            AutoMoveInput = new Vector2(local.x, local.z).normalized;

            // The game's own Sprint input: stamina drains and exhaustion stops the run exactly as
            // it does for a player holding the key. Only on a clear way forward; sprinting into a
            // sidestep overshoots it.
            float moveAngle = Vector3.Angle(body.forward, moveDirection);
            bool narrow = OnNarrowFloor(moveDirection);
            AutoSprintInput = ConfigManager.AutoPathfindingSprint.Value && clear && !narrow && moveAngle < AUTO_SPRINT_MAX_ANGLE;

            // On a girder or plank: a steadier walk, where a sideways drift of half a metre is a fall.
            if (narrow)
            {
                AutoMoveInput *= 0.75f;
            }
        }

        /// <summary>No floor within 0.6 m to one side or the other of the way ahead: a girder, a plank.</summary>
        private bool OnNarrowFloor(Vector3 direction)
        {
            Vector3 side = Vector3.Cross(Vector3.up, direction).normalized;
            Vector3 feet = transform.position;
            foreach (float s in new[] { 0.6f, -0.6f })
            {
                if (NearestGroundY(feet + side * s, out float y) > 0.5f || Mathf.Abs(y - feet.y) > 0.5f)
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
                Vector3 toPoint = new Vector3(point.x, chest.y, point.z) - chest;
                if (toPoint.sqrMagnitude > 0.0001f &&
                    Physics.SphereCast(chest, 0.35f, toPoint.normalized, out _, toPoint.magnitude, groundMask, QueryTriggerInteraction.Ignore))
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
            return aim;
        }

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
            return probeNow.IsClimbLink(target) ? FindLadderTo(target) : null;
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
            if (pushing && Time.time - ladderStallTime > 0.5f)
            {
                activeLadder.Interact(transform);
                activeLadder = null;
            }
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
            stuckNode = null;
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
            bool progressed = currentTargetNode != stuckNode || stuckBestDistance - distance >= STUCK_MIN_PROGRESS;
            if (progressed || distance < stuckBestDistance)
            {
                stuckBestDistance = distance;
            }
            stuckNode = currentTargetNode;
            stuckCheckPosition = transform.position;
            stuckCheckTime = Time.time;

            if (!tryingToMove || progressed)
            {
                stuckCount = 0;
                return;
            }

            stuckCount++;
            Debug.Log($"Pathfinder: stuck ({stuckCount})");

            if (stuckCount == 1)
            {
                // Whatever the probe missed: try a jump and the other side.
                Probe?.Jump();
                aroundUntil = 0f;
                Probe?.ResetSide(UnityEngine.Random.value < 0.5f ? -1 : 1);
            }
            else if (stuckCount == 2)
            {
                lastReplanTime = -999f;
                Replan(null);
            }
            else if (stuckCount == 3 && currentTargetNode != null)
            {
                // Something neither the NavMesh nor the probe can get past (a ledge the mesh links
                // over, a gap too tight for the capsule). Step past it the way the old agent warp
                // did.
                playerController.TeleportPlayer(currentTargetNode.position - Vector3.up * NODE_GROUND_OFFSET);
            }
            else if (stuckCount >= 5)
            {
                StopPathfinding(playSound: false);
                Utilities.SpeakText("Stuck. Pathfinding stopped.");
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

        #region Update and Cleanup
        private void Update()
        {
            try
            {
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

                OpenNearbyDoor();
                UpdateHazardCue();
                UpdateLadderLetGo();
                UpdateGapJumpState();
                UpdateGapScan();

                if (currentMode == PathfindingMode.Manual)
                {
                    UpdateManualAudio();
                    UpdateManualObstacleCues();
                    UpdateManualGap();
                }
                else
                {
                    UpdateAutoWalk();
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
            return Pathfinder.AutoWalkActive && IsPathfinderPlayer(player) ? (Pathfinder.AutoSprintInput ? 1f : 0f) : keyboard;
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
