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
    internal class Pathfinder : MonoBehaviour
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

        private const float AUTO_TURN_SPEED = 540f;
        private const float AUTO_TURN_IN_PLACE_ANGLE = 75f;
        private const float AUTO_SPRINT_MAX_ANGLE = 35f;
        private const float STUCK_CHECK_INTERVAL = 2f;
        private const float STUCK_MIN_PROGRESS = 0.4f;

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

                Utilities.SpeakText($"Pathfinding to {GetTargetName()}");
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

            if (NavMesh.SamplePosition(targetPos, out hit, 5f, NavMesh.AllAreas) ||
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

            NavMeshPath navPath = new NavMeshPath();
            if (!NavMesh.CalculatePath(startHit.position, currentDestination, NavMesh.AllAreas, navPath) ||
                navPath.status == NavMeshPathStatus.PathInvalid || navPath.corners.Length < 2)
            {
                Debug.LogWarning($"Pathfinder: path failed, status {navPath.status}");
                return false;
            }

            List<Vector3> positions = BuildNodePositions(navPath.corners);

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

        private List<Vector3> BuildNodePositions(Vector3[] corners)
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
                        positions.Add(SnapToGround(surface));
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

            if (isOffNavMeshTarget)
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
        /// Moves a NavMesh point onto the collider the player would stand on there, plus the node
        /// sphere's radius so the sphere rests on the ground instead of half inside it.
        /// </summary>
        private Vector3 SnapToGround(Vector3 meshPoint)
        {
            int hitCount = Physics.RaycastNonAlloc(meshPoint + Vector3.up * GROUND_RAY_UP, Vector3.down,
                groundHitCache, GROUND_RAY_LENGTH, groundMask, QueryTriggerInteraction.Ignore);

            // The surface nearest the mesh height, so a pipe or ledge overhead never wins over
            // the floor the mesh was baked from.
            float best = float.MaxValue;
            float groundY = meshPoint.y;
            for (int i = 0; i < hitCount; i++)
            {
                float offset = Mathf.Abs(groundHitCache[i].point.y - meshPoint.y);
                if (offset < best)
                {
                    best = offset;
                    groundY = groundHitCache[i].point.y;
                }
            }

            return new Vector3(meshPoint.x, groundY + NODE_GROUND_OFFSET, meshPoint.z);
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

            if (pathNodes[pathNodes.Count - 1].isReached)
            {
                OnReachedDestination();
                return false;
            }

            FindNextTargetNode();
            return true;
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
            StopPathfinding(playSound: true);

            // currentTargetObject is kept so the player can still look at it afterwards.
            Utilities.SpeakText($"Reached {name}");
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

        private void PingCurrentNode()
        {
            if (currentTargetNode == null || currentTargetNode.audioSource == null)
                return;

            Vector3 playerPos = transform.position;
            Vector3 nodePos = currentTargetNode.position;
            Vector3 toNode = nodePos - playerPos;
            Vector3 horizontalToNode = new Vector3(toNode.x, 0, toNode.z).normalized;
            Vector3 horizontalForward = new Vector3(transform.forward.x, 0, transform.forward.z).normalized;
            float dot = Vector3.Dot(horizontalForward, horizontalToNode);

            AudioClip clip = dot > 0.3f ? onTargetSound : dot < -0.3f ? offTargetSound : almostOnTargetSound;
            if (clip == null)
                return;

            currentTargetNode.audioSource.clip = clip;
            currentTargetNode.audioSource.volume = AudioSystemBypass.CalculateVolumeBasedOnDistance(
                Vector3.Distance(playerPos, nodePos), 1.0f, 20.0f, 0.6f);
            currentTargetNode.audioSource.Play();
        }
        #endregion

        #region Auto Mode
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

        private void UpdateAutoWalk()
        {
            if (currentTargetNode == null || !CanSteer())
            {
                ClearAutoInput();
                return;
            }

            Vector3 toNode = currentTargetNode.position - transform.position;
            toNode.y = 0f;
            if (toNode.sqrMagnitude < 0.0001f)
            {
                ClearAutoInput();
                return;
            }

            Transform body = playerController.thisPlayerBody != null ? playerController.thisPlayerBody : transform;
            Vector3 euler = body.eulerAngles;
            float desiredYaw = Quaternion.LookRotation(toNode).eulerAngles.y;
            float yaw = Mathf.MoveTowardsAngle(euler.y, desiredYaw, AUTO_TURN_SPEED * Time.deltaTime);
            body.eulerAngles = new Vector3(euler.x, yaw, euler.z);

            float remaining = Mathf.Abs(Mathf.DeltaAngle(yaw, desiredYaw));
            if (remaining > AUTO_TURN_IN_PLACE_ANGLE)
            {
                // Turn first; walking now would be a backwards walk.
                AutoMoveInput = Vector2.zero;
                AutoSprintInput = false;
                return;
            }

            Vector3 local = body.InverseTransformDirection(toNode.normalized);
            AutoMoveInput = new Vector2(local.x, local.z).normalized;

            // The game's own Sprint input: stamina drains and exhaustion stops the run exactly as
            // it does for a player holding the key.
            AutoSprintInput = ConfigManager.AutoPathfindingSprint.Value && remaining < AUTO_SPRINT_MAX_ANGLE;
        }

        private static void ClearAutoInput()
        {
            AutoMoveInput = Vector2.zero;
            AutoSprintInput = false;
        }

        private void ResetStuckTracking()
        {
            stuckCheckPosition = transform.position;
            stuckCheckTime = Time.time;
            stuckCount = 0;
        }

        private void HandleStuckDetection()
        {
            if (Time.time - stuckCheckTime < STUCK_CHECK_INTERVAL)
                return;

            bool tryingToMove = AutoMoveInput.sqrMagnitude > 0.01f;
            bool progressed = HorizontalDistance(transform.position, stuckCheckPosition) >= STUCK_MIN_PROGRESS;
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
                lastReplanTime = -999f;
                Replan(null);
            }
            else if (stuckCount == 2 && currentTargetNode != null)
            {
                // Something the NavMesh does not know about is in the way (a closed vent, a
                // ledge the mesh links over). Step past it the way the old agent warp did.
                playerController.TeleportPlayer(currentTargetNode.position - Vector3.up * NODE_GROUND_OFFSET);
            }
            else if (stuckCount >= 4)
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

        private void OpenNearbyDoor()
        {
            DoorLock closestDoor = null;
            float closestDistance = DOOR_DETECTION_DISTANCE;

            DoorLock[] doors = GetDoors();
            if (doors == null)
                return;

            foreach (var door in doors)
            {
                if (door == null)
                    continue;

                float distance = Vector3.Distance(transform.position, door.transform.position);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestDoor = door;
                }
            }

            if (closestDoor == null)
                return;

            var trigger = closestDoor.GetComponent<AnimatedObjectTrigger>();
            if (trigger != null && !trigger.boolValue && !closestDoor.isLocked)
            {
                try
                {
                    closestDoor.OpenOrCloseDoor(playerController);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error opening door: {ex.Message}");
                }
            }
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

                if (currentMode == PathfindingMode.Manual)
                {
                    UpdateManualAudio();
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
            if (!Pathfinder.AutoWalkActive || !IsPathfinderPlayer(player))
                return keyboard;

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
