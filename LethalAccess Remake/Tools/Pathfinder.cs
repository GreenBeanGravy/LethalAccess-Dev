using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using HarmonyLib;
using GameNetcodeStuff;
using System.IO;
using System.Reflection;
using System.Linq;
using UnityEngine.Networking;
using System;
using LethalAccess;

namespace LethalAccess
{
    /// <summary>
    /// Improved Pathfinder component that provides navigation capabilities for the player
    /// in both automatic and manual mode. Supports audio cues, visual path
    /// representation, and handles off-NavMesh destinations with improved performance.
    /// </summary>
    internal class Pathfinder : MonoBehaviour
    {
        #region Enums and Nested Classes
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
                isReached = false;
                index = idx;
            }
        }
        #endregion

        #region Fields and Properties
        public bool isPathfinding = false;
        private NavMeshAgent agent;
        private Vector3 currentDestination;
        public float stoppingRadius = 1.4f;
        private float baseSpeed = 1.75f;
        public static bool ShouldPreventFallDamage = false;
        private LineRenderer lineRenderer;
        private AudioSource audioSource;
        public PlayerControllerB playerController;
        private Vector3 lastPosition;
        private float lastPositionUpdateTime = 0f;
        private GameObject currentTargetObject;
        private bool isRecoveringFromError = false;
        private float recoveryAttemptTime = 0f;
        private int recoveryAttempts = 0;
        private const int MAX_RECOVERY_ATTEMPTS = 3;
        private const float RECOVERY_COOLDOWN = 5f;
        private const float GROUND_CHECK_HEIGHT = 3f;
        private const float NODE_HEIGHT_OFFSET = 0.1f;
        private const float MAX_HEIGHT_ADJUSTMENT = 2f;
        private Queue<GameObject> nodeObjectPool = new Queue<GameObject>();
        private const int INITIAL_POOL_SIZE = 50;
        private bool isGeneratingPath = false;
        private bool pathGenerationComplete = false;
        private float pathGenerationStartTime = 0f;
        private const float PATH_GENERATION_TIMEOUT = 5f;
        private const float LOADING_ANNOUNCEMENT_INTERVAL = 1f;
        private float lastLoadingAnnouncementTime = 0f;
        private const float NODE_SPACING_MIN = 1f;
        private const float NODE_SPACING_MAX = 2f;
        private bool forcedPathActive = false;
        private List<Vector3> forcedWaypoints = new List<Vector3>();
        private int currentForcedWaypointIndex = 0;
        private bool isOffNavMeshTarget = false;
        private Vector3 offNavMeshTargetPos;
        private float offNavMeshVerticalOffset = 0f;
        private Vector3[] originalPathCorners;
        private PathfindingMode currentMode = PathfindingMode.Manual;
        private List<PathNode> pathNodes = new List<PathNode>();
        private PathNode currentTargetNode = null;
        private int lastReachedNodeIndex = -1;
        private float nodeAudioPingTimer = 0f;
        private const float NODE_AUDIO_PING_INTERVAL = 0.5f;
        private const float AUDIO_DELAY_BETWEEN_POINTS = 0.15f;
        private bool isOnNavMesh = false;
        private const float NAV_MESH_CHECK_INTERVAL = 0.5f;
        private LayerMask groundMask;
        private const float TERRAIN_CHECK_DISTANCE = 50f;
        private const float PATH_HEIGHT_CLEARANCE = 2f;
        private bool isInitializingPathfinding = false;
        private bool hasAnnouncedDestinationReached = false;
        private Mesh nodeSphereMesh;
        private Material nodeBaseMaterial;
        private readonly RaycastHit[] groundHitCache = new RaycastHit[4];

        // Add paths to audio resources
        private const string ON_TARGET_SOUND_PATH = "LethalAccessAssets/On Target.wav";
        private const string ALMOST_ON_TARGET_SOUND_PATH = "LethalAccessAssets/Almost On Target.wav";
        private const string OFF_TARGET_SOUND_PATH = "LethalAccessAssets/Off Target.wav";

        // References to loaded audio clips
        private AudioClip onTargetSound;
        private AudioClip almostOnTargetSound;
        private AudioClip offTargetSound;

        public bool IsPathfinding => isPathfinding;
        public static Pathfinder Instance { get; private set; }
        #endregion

        #region Initialization
        private void Awake()
        {
            try
            {
                // Clean up any existing NavMeshAgents to prevent duplicates
                NavMeshAgent[] existingAgents = GetComponents<NavMeshAgent>();
                if (existingAgents != null && existingAgents.Length > 0)
                {
                    Debug.Log($"Found {existingAgents.Length} existing NavMeshAgents at startup - cleaning up");
                    foreach (var existing in existingAgents)
                    {
                        DestroyImmediate(existing);
                    }
                }

                Instance = this;
                playerController = GetComponent<PlayerControllerB>();

                // Initialize the ground mask for terrain detection
                groundMask = LayerMask.GetMask("Terrain", "Ground", "Default") | StartOfRound.Instance.collidersAndRoomMaskAndDefault;

                // Setup audio source and load the "reached destination" sound
                audioSource = gameObject.AddComponent<AudioSource>();
                AudioSystemBypass.ConfigureAudioSourceForBypass(audioSource);

                // Setup line renderer for path visualization
                lineRenderer = GetComponent<LineRenderer>();
                if (lineRenderer == null)
                {
                    lineRenderer = gameObject.AddComponent<LineRenderer>();
                }
                SetupLineRenderer();

                // Initialize position tracking for stuck detection
                lastPosition = transform.position;
                lastPositionUpdateTime = Time.time;

                // Initialize node appearance resources
                InitializeNodeResources();

                // Initialize node object pool
                InitializeNodePool();

                // Check for NavMesh agent every 0.5 seconds
                InvokeRepeating("EnsureNavMeshAgentIsRemoved", 0.5f, 0.5f);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in Pathfinder.Awake: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void InitializeNodeResources()
        {
            try
            {
                // Create a standard sphere mesh for all nodes
                nodeSphereMesh = CreateSphereMesh(0.4f);

                // Create a reusable material
                nodeBaseMaterial = new Material(Shader.Find("Sprites/Default"));
                nodeBaseMaterial.color = Color.cyan;

                // Load audio clips
                StartCoroutine(LoadAudioResources());
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in InitializeNodeResources: {ex.Message}");
            }
        }

        // New method to load all audio resources
        private IEnumerator LoadAudioResources()
        {
            yield return StartCoroutine(LoadAudioClip(ON_TARGET_SOUND_PATH, clip => onTargetSound = clip));
            yield return StartCoroutine(LoadAudioClip(ALMOST_ON_TARGET_SOUND_PATH, clip => almostOnTargetSound = clip));
            yield return StartCoroutine(LoadAudioClip(OFF_TARGET_SOUND_PATH, clip => offTargetSound = clip));

            Debug.Log("Pathfinder audio resources loaded successfully");
        }

        // Helper method to load audio clips from embedded resources
        private IEnumerator LoadAudioClip(string resourcePath, System.Action<AudioClip> onLoaded)
        {
            string modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string fullPath = Path.Combine(modDirectory, resourcePath);
            string fileURL = "file://" + fullPath;

            UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(fileURL, AudioType.WAV);

            // This yield statement is outside any try-catch block
            yield return request.SendWebRequest();

            try
            {
                if (request.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip != null)
                    {
                        onLoaded(clip);
                        Debug.Log($"Successfully loaded audio clip: {resourcePath}");
                    }
                    else
                    {
                        Debug.LogError($"Failed to load audio clip content: {resourcePath}");
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
            try
            {
                // Pre-create node objects for the pool
                for (int i = 0; i < INITIAL_POOL_SIZE; i++)
                {
                    GameObject nodeObj = new GameObject("PooledPathNode");

                    // Add required components
                    MeshFilter meshFilter = nodeObj.AddComponent<MeshFilter>();
                    meshFilter.mesh = nodeSphereMesh;

                    MeshRenderer renderer = nodeObj.AddComponent<MeshRenderer>();
                    renderer.material = new Material(nodeBaseMaterial);

                    // Add an audio source
                    AudioSource nodeAudio = nodeObj.AddComponent<AudioSource>();

                    // Use our custom audio bypass configuration
                    AudioSystemBypass.ConfigureAudioSourceForBypass(nodeAudio);

                    // Additional settings specific to pathfinder nodes
                    nodeAudio.spatialBlend = 1.0f;
                    nodeAudio.rolloffMode = AudioRolloffMode.Linear;
                    nodeAudio.minDistance = 1.0f;
                    nodeAudio.maxDistance = 35.0f;
                    nodeAudio.playOnAwake = false;

                    // Hide and add to pool
                    nodeObj.SetActive(false);
                    nodeObjectPool.Enqueue(nodeObj);
                }

                Debug.Log($"Initialized node pool with {INITIAL_POOL_SIZE} objects");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in InitializeNodePool: {ex.Message}");
            }
        }

        private GameObject GetNodeFromPool()
        {
            if (nodeObjectPool.Count > 0)
            {
                GameObject nodeObj = nodeObjectPool.Dequeue();
                nodeObj.SetActive(true);
                return nodeObj;
            }
            else
            {
                // Create a new object if pool is empty
                GameObject nodeObj = new GameObject("PathNode");

                MeshFilter meshFilter = nodeObj.AddComponent<MeshFilter>();
                meshFilter.mesh = nodeSphereMesh;

                MeshRenderer renderer = nodeObj.AddComponent<MeshRenderer>();
                renderer.material = new Material(nodeBaseMaterial);

                AudioSource nodeAudio = nodeObj.AddComponent<AudioSource>();
                nodeAudio.spatialBlend = 1.0f;
                nodeAudio.rolloffMode = AudioRolloffMode.Linear;
                nodeAudio.minDistance = 1.0f;
                nodeAudio.maxDistance = 35.0f;
                nodeAudio.playOnAwake = false;

                return nodeObj;
            }
        }

        private void ReturnNodeToPool(GameObject nodeObj)
        {
            if (nodeObj == null) return;

            nodeObj.SetActive(false);
            nodeObjectPool.Enqueue(nodeObj);
        }
        #endregion

        #region Pathfinding Mode Management
        public void TogglePathfindingMode()
        {
            if (!isPathfinding || currentTargetObject == null)
                return;

            // Don't allow mode changes while path is being generated
            if (isGeneratingPath)
            {
                Utilities.SpeakText("Please wait, path is still being generated");
                return;
            }

            if (currentMode == PathfindingMode.Manual)
            {
                Debug.Log("Switching from Manual to Auto mode");

                // Make sure we don't have any lingering agent
                NavMeshAgent existingAgent = GetComponent<NavMeshAgent>();
                if (existingAgent != null)
                {
                    Debug.LogWarning("Found existing agent when switching to auto mode, removing first");
                    try
                    {
                        Destroy(existingAgent);
                    }
                    catch { }
                }

                currentMode = PathfindingMode.Auto;

                // Create a new agent for auto mode
                CreateOrRecreateAgent();

                if (IsAgentValid())
                {
                    ConfigureAgent();

                    // Use the destination that we've already calculated
                    try
                    {
                        agent.SetDestination(currentDestination);
                        agent.isStopped = false;
                        Debug.Log("Switched to automatic pathfinding mode and set destination");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error setting destination in auto mode: {ex.Message}");
                    }
                }

                // Hide all manual mode nodes
                foreach (var node in pathNodes)
                {
                    if (node.nodeObject != null)
                    {
                        node.nodeObject.SetActive(false);
                    }
                }

                // Clear the line renderer for manual path
                lineRenderer.positionCount = 0;

                Utilities.SpeakText("Switched to automatic pathfinding mode");
            }
            else
            {
                Debug.Log("Switching from Auto to Manual mode");

                // Switch to manual mode by using our dedicated method
                SwitchToManualMode();
            }
        }

        private void SwitchToManualMode()
        {
            // We need to get the complete path from the agent before removing it
            if (agent == null || !agent.hasPath)
            {
                Debug.LogWarning("No agent or agent has no path when switching to manual mode");
                StopPathfinding();
                Utilities.SpeakText("No valid path available. Please try again.");
                return;
            }

            NavMeshPath agentPath = new NavMeshPath();
            Vector3[] pathCornersCopy = null;

            try
            {
                // Copy the agent's path corners manually to preserve them
                Vector3[] pathCorners = agent.path.corners;
                pathCornersCopy = new Vector3[pathCorners.Length];
                System.Array.Copy(pathCorners, pathCornersCopy, pathCorners.Length);

                // Use reflection to set the corners in the new path
                var cornersField = typeof(NavMeshPath).GetField("m_Corners",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (cornersField != null)
                {
                    cornersField.SetValue(agentPath, pathCornersCopy);
                    Debug.Log($"Using agent path with {pathCornersCopy.Length} corners for manual mode");
                }
                else
                {
                    Debug.LogError("Could not access internal corners field via reflection");
                    StopPathfinding();
                    Utilities.SpeakText("Could not create a valid path. Please try again.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in SwitchToManualMode: {ex.Message}");
                StopPathfinding();
                Utilities.SpeakText("No valid path available. Please try again.");
                return;
            }

            // Properly remove the NavMeshAgent BEFORE generating nodes
            if (agent != null)
            {
                if (agent.isActiveAndEnabled && agent.isOnNavMesh)
                {
                    agent.isStopped = true;
                    agent.ResetPath();
                }
                Destroy(agent);
                agent = null;

                // Manually check for any remaining NavMeshAgents (can happen due to Unity lifecycle)
                NavMeshAgent[] remainingAgents = GetComponents<NavMeshAgent>();
                if (remainingAgents != null && remainingAgents.Length > 0)
                {
                    Debug.LogWarning($"Found {remainingAgents.Length} remaining NavMeshAgents - destroying");
                    foreach (var remaining in remainingAgents)
                    {
                        Destroy(remaining);
                    }
                }
            }

            // Generate nodes along the agent's path (if valid)
            if (agentPath.corners != null && agentPath.corners.Length >= 2)
            {
                StartPathGeneration(agentPath);
            }
            else
            {
                Debug.LogError("Agent path was invalid, cannot generate nodes for manual mode");
                StopPathfinding();
                Utilities.SpeakText("Could not create a valid path. Please try again.");
                return;
            }

            // Set the mode to manual
            currentMode = PathfindingMode.Manual;
            Utilities.SpeakText("Switching to manual pathfinding mode. Generating path nodes...");
        }
        #endregion

        #region Path Generation and Node Management
        private void StartPathGeneration(NavMeshPath path)
        {
            if (isGeneratingPath)
            {
                Debug.LogWarning("Path generation already in progress");
                return;
            }

            isGeneratingPath = true;
            pathGenerationComplete = false;
            pathGenerationStartTime = Time.time;
            lastLoadingAnnouncementTime = Time.time;

            // Clear any existing path nodes first
            ClearPathNodes();

            // Start path generation on a background thread
            StartCoroutine(GeneratePathNodesAsync(path));
        }

        private IEnumerator GeneratePathNodesAsync(NavMeshPath path)
        {
            if (path == null || path.corners == null || path.corners.Length < 2)
            {
                Debug.LogError("Cannot generate nodes: Invalid path or insufficient corners");
                isGeneratingPath = false;
                yield break;
            }

            Debug.Log($"Generating nodes from path with {path.corners.Length} corners");

            // Store original corners for reference
            originalPathCorners = new Vector3[path.corners.Length];
            System.Array.Copy(path.corners, originalPathCorners, path.corners.Length);

            // Initialize empty path nodes list (will be filled in batches)
            pathNodes = new List<PathNode>();

            // Create a list of positions first (optimization)
            List<Vector3> nodePositions = new List<Vector3>();

            // Generate node positions first (without creating GameObjects yet)
            yield return StartCoroutine(CalculateNodePositions(path, nodePositions));

            // Create the actual node objects at the positions
            yield return StartCoroutine(CreateNodeObjects(nodePositions));

            // Path generation is complete
            isGeneratingPath = false;
            pathGenerationComplete = true;

            // Update current target node
            if (pathNodes.Count > 0)
            {
                currentTargetNode = pathNodes[0];
                nodeAudioPingTimer = 0; // Force immediate ping
                HighlightNodes();
            }

            // Draw the manual path immediately
            DrawManualPath();

            int nodeCount = pathNodes.Count;
            Utilities.SpeakText($"Path generation complete with {nodeCount} nodes. Follow audio cues to reach your destination.");

            Debug.Log($"Path generation completed with {nodeCount} nodes");
        }

        private IEnumerator CalculateNodePositions(NavMeshPath path, List<Vector3> nodePositions)
        {
            // Always create a node at the starting position
            Vector3 startPos = EnsurePointAboveGround(path.corners[0]);
            nodePositions.Add(startPos);

            // Process path corners in batches to avoid frame drops
            for (int i = 0; i < path.corners.Length - 1; i++)
            {
                Vector3 startPoint = path.corners[i];
                Vector3 endPoint = path.corners[i + 1];
                Vector3 direction = endPoint - startPoint;
                float segmentLength = direction.magnitude;

                // Skip very short segments
                if (segmentLength < 0.5f) continue;

                direction.Normalize();

                // Calculate appropriate node spacing based on segment length
                float nodeSpacing = Mathf.Clamp(segmentLength / 3f, NODE_SPACING_MIN, NODE_SPACING_MAX);
                int nodeCount = Mathf.FloorToInt(segmentLength / nodeSpacing);

                // Place nodes at regular intervals with adaptive spacing
                for (int j = 1; j <= nodeCount; j++)
                {
                    Vector3 baseNodePos = startPoint + direction * (j * nodeSpacing);
                    Vector3 nodePos = EnsurePointAboveGround(baseNodePos);

                    // Only add if not too close to previous node
                    if (nodePositions.Count == 0 ||
                        Vector3.Distance(nodePositions[nodePositions.Count - 1], nodePos) > NODE_SPACING_MIN)
                    {
                        nodePositions.Add(nodePos);
                    }
                }

                // Add endpoint if it's far enough from last added node
                if (nodePositions.Count == 0 ||
                    Vector3.Distance(nodePositions[nodePositions.Count - 1], endPoint) > NODE_SPACING_MIN)
                {
                    nodePositions.Add(EnsurePointAboveGround(endPoint));
                }

                // Yield every few corners to avoid frame drops
                if (i % 3 == 0)
                {
                    yield return null;
                }
            }

            // If we have an off-NavMesh target, add it as the final node
            if (isOffNavMeshTarget)
            {
                Vector3 finalPos = EnsurePointAboveGround(offNavMeshTargetPos);
                if (nodePositions.Count == 0 ||
                    Vector3.Distance(nodePositions[nodePositions.Count - 1], finalPos) > NODE_SPACING_MIN)
                {
                    nodePositions.Add(finalPos);
                }
            }

            Debug.Log($"Generated {nodePositions.Count} node positions");
        }

        private IEnumerator CreateNodeObjects(List<Vector3> positions)
        {
            // Create nodes in batches to prevent frame rate drops
            for (int i = 0; i < positions.Count; i++)
            {
                CreateNodeAtPosition(positions[i], i);

                // Yield every few nodes to avoid frame drops
                if (i % 5 == 0)
                {
                    // Also use this opportunity to update the line renderer
                    // to show progress of node creation
                    if (lineRenderer != null && i > 1)
                    {
                        lineRenderer.positionCount = i;
                        for (int j = 0; j < i; j++)
                        {
                            lineRenderer.SetPosition(j, positions[j]);
                        }
                    }

                    yield return null;

                    // Check if we need to announce that we're still generating
                    if (Time.time - lastLoadingAnnouncementTime > LOADING_ANNOUNCEMENT_INTERVAL)
                    {
                        int percent = Mathf.RoundToInt((float)i / positions.Count * 100f);
                        Utilities.SpeakText($"Still generating path... {percent}% complete");
                        lastLoadingAnnouncementTime = Time.time;
                    }
                }
            }
        }

        private Vector3 EnsurePointAboveGround(Vector3 position)
        {
            // Store original position for comparison
            Vector3 originalPosition = position;
            float originalY = position.y;

            // First check if the point is on NavMesh
            NavMeshHit navHit;
            if (NavMesh.SamplePosition(position, out navHit, 2.0f, NavMesh.AllAreas))
            {
                // Use the NavMesh point but preserve original height if reasonable
                if (Mathf.Abs(navHit.position.y - originalY) < MAX_HEIGHT_ADJUSTMENT)
                {
                    position = navHit.position;
                }
                else
                {
                    // Take x,z from NavMesh but keep y close to original
                    position = new Vector3(navHit.position.x,
                                         originalY + Mathf.Sign(navHit.position.y - originalY) * MAX_HEIGHT_ADJUSTMENT,
                                         navHit.position.z);
                }
            }

            // Efficient ground check using object-pooled raycast
            int hitCount = Physics.RaycastNonAlloc(position + Vector3.up * GROUND_CHECK_HEIGHT,
                                             Vector3.down,
                                             groundHitCache,
                                             GROUND_CHECK_HEIGHT + 2f,
                                             groundMask);

            if (hitCount > 0)
            {
                // Find closest hit
                float closestDist = float.MaxValue;
                int closestHitIndex = -1;

                for (int i = 0; i < hitCount; i++)
                {
                    if (groundHitCache[i].distance < closestDist)
                    {
                        closestDist = groundHitCache[i].distance;
                        closestHitIndex = i;
                    }
                }

                if (closestHitIndex >= 0)
                {
                    // Calculate potential new height
                    float newY = groundHitCache[closestHitIndex].point.y + NODE_HEIGHT_OFFSET;

                    // Limit height adjustment to prevent extreme elevations
                    if (Mathf.Abs(newY - originalY) > MAX_HEIGHT_ADJUSTMENT)
                    {
                        newY = originalY + Mathf.Sign(newY - originalY) * MAX_HEIGHT_ADJUSTMENT;
                    }

                    return new Vector3(position.x, newY, position.z);
                }
            }

            // If all else fails, add a minimal height offset to the original position
            return new Vector3(position.x, position.y + 0.05f, position.z);
        }

        private void CreateNodeAtPosition(Vector3 position, int index)
        {
            try
            {
                PathNode node = new PathNode(position, index);

                // Get a node object from pool instead of creating new one
                GameObject nodeObj = GetNodeFromPool();
                nodeObj.name = $"PathNode_{index}";
                nodeObj.transform.position = position;

                // Configure components
                MeshRenderer renderer = nodeObj.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    renderer.material.color = Color.cyan; // Default unreached color
                }

                AudioSource audioSrc = nodeObj.GetComponent<AudioSource>();
                if (audioSrc != null)
                {
                    audioSrc.spatialBlend = 1.0f;
                    audioSrc.rolloffMode = AudioRolloffMode.Linear;
                    audioSrc.minDistance = 1.0f;
                    audioSrc.maxDistance = 35.0f;
                    audioSrc.playOnAwake = false;
                }

                node.nodeObject = nodeObj;
                node.audioSource = audioSrc;

                pathNodes.Add(node);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error creating node at position {position}: {ex.Message}");
            }
        }

        private void ClearPathNodes()
        {
            foreach (var node in pathNodes)
            {
                if (node.nodeObject != null)
                {
                    ReturnNodeToPool(node.nodeObject);
                }
            }
            pathNodes.Clear();
            currentTargetNode = null;
            lastReachedNodeIndex = -1;
        }

        private void HighlightNodes()
        {
            foreach (var node in pathNodes)
            {
                if (node.nodeObject != null)
                {
                    MeshRenderer renderer = node.nodeObject.GetComponent<MeshRenderer>();
                    if (renderer != null)
                    {
                        if (node == currentTargetNode)
                        {
                            renderer.material.color = Color.yellow; // Current target
                        }
                        else if (node.isReached)
                        {
                            renderer.material.color = Color.green; // Reached
                        }
                        else
                        {
                            renderer.material.color = Color.cyan; // Unreached
                        }
                    }
                }
            }
        }
        #endregion

        #region Navigation Control
        public void NavigateTo(GameObject targetObject)
        {
            try
            {
                Debug.Log("Attempting to pathfind to " + targetObject?.name);

                // If already generating a path, don't allow starting a new one
                if (isGeneratingPath)
                {
                    Utilities.SpeakText("Please wait, still generating path...");
                    return;
                }

                // Set the initialization flag to prevent NavMeshAgent from being removed during setup
                isInitializingPathfinding = true;

                // If already pathfinding, just toggle the mode
                if (isPathfinding && currentTargetObject == targetObject)
                {
                    TogglePathfindingMode();
                    isInitializingPathfinding = false;
                    return;
                }

                currentTargetObject = targetObject;
                isOffNavMeshTarget = false;
                offNavMeshVerticalOffset = 0f;
                pathGenerationComplete = false;

                // We want manual mode by default, but we'll still calculate the path using an agent
                currentMode = PathfindingMode.Manual;

                if (StartOfRound.Instance == null || !StartOfRound.Instance.shipHasLanded)
                {
                    Utilities.SpeakText("The ship has not landed yet. Pathfinding is not allowed.");
                    isInitializingPathfinding = false;
                    return;
                }

                if (targetObject == null)
                {
                    Utilities.SpeakText("Selected object not found at current location.");
                    Debug.LogError("Pathfinder.NavigateTo: targetObject is null.");
                    isInitializingPathfinding = false;
                    return;
                }

                // Set isPathfinding to true earlier
                // This indicates we're actively pathfinding so safety checks don't remove the agent
                isPathfinding = true;

                // Ensure the agent exists and is valid
                if (agent == null || !agent.isActiveAndEnabled)
                {
                    CreateOrRecreateAgent();

                    // Wait for agent to be created
                    StartCoroutine(NavigateWithDelayedAgent(targetObject));
                    return;
                }

                // Check if agent is on NavMesh
                if (!agent.isOnNavMesh)
                {
                    Debug.LogWarning("Agent exists but not on NavMesh. Attempting to fix...");
                    NavMeshHit navHit;

                    if (NavMesh.SamplePosition(transform.position, out navHit, 5f, NavMesh.AllAreas))
                    {
                        agent.Warp(navHit.position);

                        // Check again if on NavMesh after warping
                        if (!agent.isOnNavMesh)
                        {
                            Utilities.SpeakText("Cannot pathfind from current position. Please try moving to a different location.");
                            isPathfinding = false;
                            isInitializingPathfinding = false;
                            return;
                        }
                    }
                    else
                    {
                        Utilities.SpeakText("No valid NavMesh found near your position. Please try moving to a different location.");
                        isPathfinding = false;
                        isInitializingPathfinding = false;
                        return;
                    }
                }

                // Configure the agent with proper settings
                ConfigureAgent();

                // Begin the actual pathfinding process
                ContinueNavigateTo(targetObject);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in NavigateTo: {ex.Message}\n{ex.StackTrace}");
                Utilities.SpeakText("An error occurred while trying to navigate. Please try again.");
                StopPathfinding();
                isInitializingPathfinding = false;
            }
        }

        private IEnumerator NavigateWithDelayedAgent(GameObject targetObject)
        {
            yield return new WaitForSeconds(0.3f); // Wait for agent to initialize

            try
            {
                if (agent == null || !agent.isActiveAndEnabled)
                {
                    Utilities.SpeakText("Could not create navigation agent. Please try again from a different location.");
                    isPathfinding = false;
                    isInitializingPathfinding = false;
                    yield break;
                }

                if (!agent.isOnNavMesh)
                {
                    NavMeshHit navHit;
                    if (NavMesh.SamplePosition(transform.position, out navHit, 5f, NavMesh.AllAreas))
                    {
                        agent.Warp(navHit.position);

                        if (!agent.isOnNavMesh)
                        {
                            Utilities.SpeakText("Cannot pathfind from current position. Please try moving to a different location.");
                            isPathfinding = false;
                            isInitializingPathfinding = false;
                            yield break;
                        }
                    }
                    else
                    {
                        Utilities.SpeakText("No valid NavMesh found near your position. Please try moving to a different location.");
                        isPathfinding = false;
                        isInitializingPathfinding = false;
                        yield break;
                    }
                }

                // Continue with navigation
                ContinueNavigateTo(targetObject);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in NavigateWithDelayedAgent: {ex.Message}");
                isPathfinding = false;
                isInitializingPathfinding = false;
            }
        }

        private void ContinueNavigateTo(GameObject targetObject)
        {
            try
            {
                // Check if this is a registered menu item
                bool isRegisteredItem = NavMenu.IsRegisteredMenuItem(targetObject.name);
                Vector3 destination;

                if (isRegisteredItem)
                {
                    // For registered items, first try direct position approach
                    destination = targetObject.transform.position;

                    // Attempt to find a valid NavMesh position near the target
                    NavMeshHit targetHit;
                    bool targetOnNavMesh = NavMesh.SamplePosition(destination, out targetHit, 10f, NavMesh.AllAreas);

                    if (targetOnNavMesh)
                    {
                        // Use the valid NavMesh position for pathfinding
                        destination = targetHit.position;
                        Debug.Log($"Found valid NavMesh position near target: {destination}");
                    }
                    else
                    {
                        // Special handling for off-NavMesh targets
                        Debug.Log("Target is off NavMesh. Using special handling.");
                        isOffNavMeshTarget = true;
                        offNavMeshTargetPos = destination;

                        // Calculate vertical offset
                        offNavMeshVerticalOffset = destination.y - transform.position.y;

                        // Find nearest valid NavMesh point in target direction
                        Vector3 direction = (destination - transform.position).normalized;
                        Vector3 horizontalDirection = new Vector3(direction.x, 0, direction.z).normalized;

                        // Try multiple distances
                        for (float distance = 5f; distance <= 15f; distance += 5f)
                        {
                            Vector3 testPoint = transform.position + horizontalDirection * distance;

                            if (NavMesh.SamplePosition(testPoint, out targetHit, 5f, NavMesh.AllAreas))
                            {
                                destination = targetHit.position;
                                Debug.Log($"Using nearest NavMesh point at distance {distance}: {destination}");
                                break;
                            }
                        }
                    }
                }
                else
                {
                    // Normal pathfinding for unregistered items
                    NavMeshHit navHit;
                    bool foundValid = NavMesh.SamplePosition(targetObject.transform.position, out navHit, 5f, NavMesh.AllAreas);

                    if (!foundValid)
                    {
                        // Try with a larger radius
                        foundValid = NavMesh.SamplePosition(targetObject.transform.position, out navHit, 10f, NavMesh.AllAreas);

                        if (!foundValid)
                        {
                            Utilities.SpeakText("Could not pathfind! No valid NavMesh near the target.");
                            StopPathfinding();
                            isInitializingPathfinding = false;
                            return;
                        }
                    }

                    destination = navHit.position;
                }

                // Validate destination before using it
                if (!IsValidDestination(destination))
                {
                    Utilities.SpeakText("Cannot find a valid path to the target. Please try a different location.");
                    StopPathfinding();
                    isInitializingPathfinding = false;
                    return;
                }

                // Attempt to calculate a path to the destination
                NavMeshPath tempPath = new NavMeshPath();
                bool pathSuccess = agent.CalculatePath(destination, tempPath);

                if (!pathSuccess || tempPath.status == NavMeshPathStatus.PathInvalid || tempPath.corners.Length < 2)
                {
                    Debug.LogWarning("Failed to calculate a valid path to destination");
                    Utilities.SpeakText("Cannot find a valid path to the target. Please try a different location.");
                    StopPathfinding();
                    isInitializingPathfinding = false;
                    return;
                }

                // Store destination and start pathfinding
                currentDestination = destination;
                isPathfinding = true;
                ShouldPreventFallDamage = true;
                recoveryAttempts = 0;
                isRecoveringFromError = false;

                // Reset path generation flags
                isGeneratingPath = false;
                pathGenerationComplete = false;

                // Now start a coroutine to wait for the path calculation to complete
                // and then convert to manual mode
                StartCoroutine(WaitForPathAndSwitchToManual(destination, tempPath));

                // Clear initialization flag now that we've started the pathfinding process
                isInitializingPathfinding = false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in ContinueNavigateTo: {ex.Message}");
                Utilities.SpeakText("An error occurred while calculating the path. Please try again.");
                StopPathfinding();
                isInitializingPathfinding = false;
            }
        }

        private bool IsAgentValid()
        {
            if (agent == null)
                return false;

            if (!agent.isActiveAndEnabled)
                return false;

            // Check if the agent is on the NavMesh
            isOnNavMesh = agent.isOnNavMesh;
            return isOnNavMesh;
        }

        private bool IsValidDestination(Vector3 destination)
        {
            try
            {
                // Check if agent is valid and on NavMesh
                if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh)
                {
                    Debug.LogWarning("Agent is not valid or not on NavMesh when checking destination");
                    return false;
                }

                // Check if destination is too close (might be unnecessary)
                if (Vector3.Distance(transform.position, destination) < 0.5f)
                {
                    Debug.Log("Destination is too close to current position");
                    return true; // Close enough that it's valid
                }

                // Try to calculate a path to validate the destination
                NavMeshPath testPath = new NavMeshPath();
                bool success = NavMesh.CalculatePath(agent.nextPosition, destination, NavMesh.AllAreas, testPath);

                // If path calculation failed or path is invalid
                if (!success || testPath.status == NavMeshPathStatus.PathInvalid)
                {
                    Debug.LogWarning($"Path validation failed: success={success}, status={testPath.status}");
                    return false;
                }

                // Check if path has valid corners
                if (testPath.corners.Length < 2)
                {
                    Debug.LogWarning("Path has insufficient corners");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in IsValidDestination: {ex.Message}");
                return false;
            }
        }

        private IEnumerator WaitForPathAndSwitchToManual(Vector3 destination, NavMeshPath calculatedPath = null)
        {
            NavMeshPath agentPath = calculatedPath;

            if (agentPath == null)
            {
                // Set destination and wait for path calculation
                try
                {
                    if (agent.isOnNavMesh)
                    {
                        agent.SetDestination(destination);
                    }
                    else
                    {
                        Debug.LogError("Agent is not on NavMesh when attempting to set destination");
                        StopPathfinding();
                        Utilities.SpeakText("Error: You are not on a valid navigation surface. Please try again from a different location.");
                        yield break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Exception setting destination: {ex.Message}");
                    StopPathfinding();
                    Utilities.SpeakText("Error setting destination. Please try again.");
                    yield break;
                }

                // Wait a few frames to ensure the path is properly calculated
                for (int i = 0; i < 5; i++)
                {
                    yield return null;
                }

                // Wait until path is computed or timeout occurs
                float timeout = 2.0f;
                float elapsed = 0f;
                while (agent.pathPending && elapsed < timeout)
                {
                    yield return null;
                    elapsed += Time.deltaTime;
                }

                if (agent.pathPending)
                {
                    Debug.LogWarning("Path calculation timeout");
                    Utilities.SpeakText("Path calculation is taking too long. This may cause issues.");
                }

                // Now check if we have a valid path
                if (!agent.hasPath || agent.path.corners.Length < 2)
                {
                    Debug.LogError("Agent couldn't compute a valid path");
                    StopPathfinding();
                    Utilities.SpeakText("Could not create a valid path. Please try again.");
                    yield break;
                }

                agentPath = agent.path;
            }

            // Now we have a valid path, if we want manual mode, switch to it
            if (currentMode == PathfindingMode.Manual)
            {
                // Create a copy of the path
                NavMeshPath manualPath = new NavMeshPath();
                Vector3[] pathCornersCopy = null;

                try
                {
                    // Copy the agent's path corners manually to preserve them
                    Vector3[] pathCorners = agentPath.corners;
                    pathCornersCopy = new Vector3[pathCorners.Length];
                    System.Array.Copy(pathCorners, pathCornersCopy, pathCorners.Length);

                    // Use reflection to set the corners in the new path
                    var cornersField = typeof(NavMeshPath).GetField("m_Corners",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (cornersField != null)
                    {
                        cornersField.SetValue(manualPath, pathCornersCopy);
                        Debug.Log($"Using agent path with {pathCornersCopy.Length} corners for manual mode");
                    }
                    else
                    {
                        Debug.LogError("Could not access internal corners field via reflection");
                        StopPathfinding();
                        Utilities.SpeakText("Could not create a valid path. Please try again.");
                        yield break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Exception copying path: {ex.Message}");
                    StopPathfinding();
                    Utilities.SpeakText("Error preparing navigation path. Please try again.");
                    yield break;
                }

                // CRITICAL FIX: Properly remove the NavMeshAgent BEFORE generating nodes
                if (agent != null)
                {
                    if (agent.isActiveAndEnabled && agent.isOnNavMesh)
                    {
                        agent.isStopped = true;
                        agent.ResetPath();
                        agent.enabled = false;
                    }
                    // Use DestroyImmediate instead of Destroy for immediate effect
                    DestroyImmediate(agent);
                    agent = null;
                }

                // Double-check for any remaining NavMeshAgents and use DestroyImmediate
                NavMeshAgent[] remainingAgents = GetComponents<NavMeshAgent>();
                if (remainingAgents != null && remainingAgents.Length > 0)
                {
                    Debug.LogWarning($"Found {remainingAgents.Length} remaining NavMeshAgents - destroying immediately");
                    foreach (var remaining in remainingAgents)
                    {
                        if (remaining.isActiveAndEnabled && remaining.isOnNavMesh)
                        {
                            remaining.isStopped = true;
                            remaining.ResetPath();
                            remaining.enabled = false;
                        }
                        DestroyImmediate(remaining);
                    }
                }

                // Final verification
                remainingAgents = GetComponents<NavMeshAgent>();
                if (remainingAgents != null && remainingAgents.Length > 0)
                {
                    Debug.LogError($"CRITICAL: Failed to remove all NavMeshAgents before generating nodes. Still have {remainingAgents.Length} agents.");

                    // Last resort - normal Destroy and quit pathfinding
                    foreach (var remaining in remainingAgents)
                    {
                        Destroy(remaining);
                    }

                    StopPathfinding();
                    Utilities.SpeakText("Error preparing navigation path. Please try again.");
                    yield break;
                }

                // Start path generation async
                if (manualPath.corners != null && manualPath.corners.Length >= 2)
                {
                    StartPathGeneration(manualPath);
                    Utilities.SpeakText("Generating path nodes, please wait...");
                }
                else
                {
                    Debug.LogError("Agent path was invalid, cannot generate nodes for manual mode");
                    StopPathfinding();
                    Utilities.SpeakText("Could not create a valid path. Please try again.");
                    yield break;
                }
            }
            else
            {
                // Continue with auto mode
                agent.isStopped = false;
                Utilities.SpeakText("Automatic pathfinding enabled. Following path to destination.");
            }

            // Always announce when starting pathfinding
            string displayName = LACore.Instance?.navMenu?.GetDisplayNameForObject(currentTargetObject) ?? currentTargetObject.name;
            Utilities.SpeakText($"Starting pathfinding to {displayName}");
        }

        public void StopPathfinding()
        {
            try
            {
                // Ensure we don't spam this function
                if (!isPathfinding && agent == null && !isInitializingPathfinding && !isGeneratingPath)
                    return;

                Debug.Log("StopPathfinding called - cleaning up all NavMeshAgents");

                // First thing - update our state
                isPathfinding = false;
                forcedPathActive = false;
                isOffNavMeshTarget = false;
                hasAnnouncedDestinationReached = false;
                isInitializingPathfinding = false;
                isGeneratingPath = false;
                pathGenerationComplete = false;

                // First, handle our primary agent reference with DestroyImmediate
                if (agent != null)
                {
                    if (agent.isActiveAndEnabled && agent.isOnNavMesh)
                    {
                        agent.isStopped = true;
                        agent.ResetPath();
                        agent.enabled = false;
                    }
                    DestroyImmediate(agent);
                    agent = null;
                    Debug.Log("Primary NavMeshAgent removed in StopPathfinding");
                }

                // Also check for any other NavMeshAgents that might exist
                NavMeshAgent[] otherAgents = GetComponents<NavMeshAgent>();
                if (otherAgents != null && otherAgents.Length > 0)
                {
                    Debug.LogWarning($"Found {otherAgents.Length} additional NavMeshAgents in StopPathfinding - cleaning up immediately");
                    foreach (var otherAgent in otherAgents)
                    {
                        if (otherAgent.isActiveAndEnabled && otherAgent.isOnNavMesh)
                        {
                            otherAgent.isStopped = true;
                            otherAgent.ResetPath();
                            otherAgent.enabled = false;
                        }
                        DestroyImmediate(otherAgent);
                    }
                }

                // Double-check to make sure all agents are gone
                otherAgents = GetComponents<NavMeshAgent>();
                if (otherAgents != null && otherAgents.Length > 0)
                {
                    Debug.LogError($"CRITICAL: Still found {otherAgents.Length} NavMeshAgents after cleanup. Using last resort Destroy");
                    foreach (var a in otherAgents)
                    {
                        Destroy(a);
                    }
                }

                // Play sound effect
                if (audioSource != null && audioSource.clip != null)
                {
                    audioSource.Play();
                }

                // Clear path visualization and nodes
                ClearPath();
                ClearPathNodes();

                // Stop any coroutines that might be running
                StopAllCoroutines();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in StopPathfinding: {ex.Message}");
                isInitializingPathfinding = false;
                isGeneratingPath = false;

                // Try to clean up agents even if there was an error
                try
                {
                    NavMeshAgent[] allAgents = GetComponents<NavMeshAgent>();
                    if (allAgents != null && allAgents.Length > 0)
                    {
                        foreach (var a in allAgents)
                        {
                            DestroyImmediate(a);
                        }
                    }
                }
                catch { }
            }
        }
        #endregion

        #region Path Visualization
        void SetupLineRenderer()
        {
            try
            {
                if (lineRenderer == null)
                {
                    lineRenderer = gameObject.AddComponent<LineRenderer>();
                    Debug.Log("Created new LineRenderer");
                }

                lineRenderer.startWidth = 0.2f; // Thicker line
                lineRenderer.endWidth = 0.2f;
                lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
                lineRenderer.startColor = Color.yellow; // Brighter color
                lineRenderer.endColor = Color.yellow;
                lineRenderer.positionCount = 0;
                lineRenderer.enabled = true; // Explicitly enable

                Debug.Log("LineRenderer setup complete and enabled");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in SetupLineRenderer: {ex.Message}");
            }
        }

        void DrawPath(NavMeshPath path)
        {
            try
            {
                if (path == null || path.corners == null || path.corners.Length < 2)
                {
                    lineRenderer.positionCount = 0;
                    return;
                }

                // Only draw the path in auto mode - manual mode has its own visualization
                if (currentMode == PathfindingMode.Auto)
                {
                    lineRenderer.positionCount = path.corners.Length;
                    lineRenderer.startColor = Color.red;
                    lineRenderer.endColor = Color.red;

                    for (int i = 0; i < path.corners.Length; i++)
                    {
                        // Ensure points are above ground
                        Vector3 cornerPos = path.corners[i];
                        Vector3 elevatedCorner = EnsurePointAboveGround(cornerPos);
                        lineRenderer.SetPosition(i, elevatedCorner);
                    }

                    // If we have an off-NavMesh target, draw an additional line to the actual target
                    if (isOffNavMeshTarget && path.corners.Length > 0)
                    {
                        int originalPointCount = lineRenderer.positionCount;
                        lineRenderer.positionCount = originalPointCount + 1;
                        Vector3 finalPos = EnsurePointAboveGround(offNavMeshTargetPos);
                        lineRenderer.SetPosition(originalPointCount, finalPos);
                    }

                    // Ensure line is visible
                    lineRenderer.enabled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in DrawPath: {ex.Message}");
                lineRenderer.positionCount = 0;
            }
        }

        void DrawManualPath()
        {
            try
            {
                // Check if we have enough nodes to draw a path
                if (pathNodes.Count < 2)
                {
                    // Instead of warning, just don't draw and return silently
                    lineRenderer.positionCount = 0;
                    return;
                }

                // Draw a simple line connecting all nodes
                lineRenderer.startColor = Color.yellow; // Brighter yellow
                lineRenderer.endColor = Color.yellow;
                lineRenderer.startWidth = 0.2f;
                lineRenderer.endWidth = 0.2f;

                List<Vector3> pathPoints = new List<Vector3>();

                // Add all unreached nodes to the path
                bool foundUnreached = false;
                foreach (var node in pathNodes)
                {
                    if (!node.isReached)
                    {
                        foundUnreached = true;
                        pathPoints.Add(node.position);
                    }
                    else if (!foundUnreached) // Include the last reached node
                    {
                        pathPoints.Add(node.position);
                    }
                }

                if (pathPoints.Count < 2)
                {
                    // If not enough unreached nodes, draw the whole path
                    pathPoints.Clear();
                    foreach (var node in pathNodes)
                    {
                        pathPoints.Add(node.position);
                    }
                }

                // Draw the path
                lineRenderer.positionCount = pathPoints.Count;
                for (int i = 0; i < pathPoints.Count; i++)
                {
                    lineRenderer.SetPosition(i, pathPoints[i]);
                }

                // Force the line to be visible
                lineRenderer.enabled = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in DrawManualPath: {ex.Message}");
                lineRenderer.positionCount = 0;
            }
        }

        void ClearPath()
        {
            try
            {
                lineRenderer.positionCount = 0;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in ClearPath: {ex.Message}");
            }
        }
        #endregion

        #region Manual Navigation Logic
        private void UpdateCurrentTargetNode()
        {
            if (pathNodes.Count == 0 || !isPathfinding)
                return;

            // Find the closest unreached node that advances us along the path
            PathNode closestNode = null;
            float closestDistance = float.MaxValue;
            int currentIndex = currentTargetNode != null ? currentTargetNode.index : -1;

            // First try to find nodes ahead of our current target
            foreach (var node in pathNodes)
            {
                // Only consider nodes that are unreached and ahead of our current target
                if (node.isReached || (currentIndex >= 0 && node.index <= currentIndex))
                    continue;

                float distance = Vector3.Distance(transform.position, node.position);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestNode = node;
                }
            }

            // If no nodes ahead were found, find any unreached node
            if (closestNode == null)
            {
                foreach (var node in pathNodes)
                {
                    if (node.isReached)
                        continue;

                    float distance = Vector3.Distance(transform.position, node.position);
                    if (distance < closestDistance)
                    {
                        closestDistance = distance;
                        closestNode = node;
                    }
                }
            }

            // Update current target if a new one was found
            if (closestNode != null && closestNode != currentTargetNode)
            {
                currentTargetNode = closestNode;
                nodeAudioPingTimer = 0; // Force immediate ping for new target node

                // Highlight current target node
                HighlightNodes();

                Debug.Log($"Updated current target node to index {currentTargetNode.index}");
            }

            // Check if we need to stop pathfinding (all nodes reached)
            if (closestNode == null && !pathNodes.Any(n => !n.isReached))
            {
                if (!hasAnnouncedDestinationReached)
                {
                    OnManualReachedDestination();
                }
            }
        }

        private void UpdateManualPathfinding()
        {
            // Skip if still generating path or not enough nodes
            if (isGeneratingPath || !pathGenerationComplete || pathNodes.Count < 2)
                return;

            if (!isPathfinding || currentMode != PathfindingMode.Manual)
                return;

            // Check if player has skipped ahead - find the closest node regardless of index
            PathNode closestNode = null;
            float closestDistance = float.MaxValue;

            foreach (var node in pathNodes)
            {
                if (node.isReached)
                    continue;

                float distance = Vector3.Distance(transform.position, node.position);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestNode = node;
                }
            }

            // If the closest node is ahead of the current target, mark all previous ones as reached
            if (closestNode != null &&
                (currentTargetNode == null || closestNode.index > currentTargetNode.index))
            {
                // Mark all nodes up to this one as reached
                foreach (var node in pathNodes)
                {
                    if (node.index < closestNode.index && !node.isReached)
                    {
                        MarkNodeAsReached(node, false); // Don't announce for skipped nodes
                    }
                }

                // Update current target node without resetting the timer
                currentTargetNode = closestNode;
                // Do NOT reset nodeAudioPingTimer here
            }

            // Check if we've reached the current target node
            if (currentTargetNode != null)
            {
                float distance = Vector3.Distance(transform.position, currentTargetNode.position);

                // If we're close enough to this node, mark it as reached
                if (distance <= stoppingRadius && !currentTargetNode.isReached)
                {
                    MarkNodeAsReached(currentTargetNode, true); // Announce when we explicitly reach a targeted node

                    // Check if this is the last node - if so, we've reached the destination
                    bool isLastNode = true;
                    foreach (var checkNode in pathNodes)
                    {
                        if (!checkNode.isReached)
                        {
                            isLastNode = false;
                            break;
                        }
                    }

                    if (isLastNode)
                    {
                        OnManualReachedDestination();
                        return;
                    }

                    // Find the next node to target without resetting the timer
                    FindNextTargetNode(false); // Pass false to indicate we should not reset the timer
                }
            }
            else
            {
                // If we don't have a current target node, find one
                FindNextTargetNode(true); // First node should have the timer reset
            }

            // Handle audio pinging for the current target node
            if (currentTargetNode != null)
            {
                nodeAudioPingTimer -= Time.fixedDeltaTime;

                if (nodeAudioPingTimer <= 0)
                {
                    PingCurrentNode();
                    nodeAudioPingTimer = NODE_AUDIO_PING_INTERVAL; // Reset to interval (0.5 seconds)
                }
            }

            // Update the path visual
            DrawManualPath();
        }

        private void MarkNodeAsReached(PathNode node, bool announce)
        {
            if (node == null)
                return;

            // Mark this node as reached
            node.isReached = true;
            lastReachedNodeIndex = node.index;

            // Mark all prior nodes as reached too
            foreach (var otherNode in pathNodes)
            {
                if (otherNode.index < node.index && !otherNode.isReached)
                {
                    otherNode.isReached = true;
                }
            }

            // Change the node color to green
            if (node.nodeObject != null)
            {
                MeshRenderer renderer = node.nodeObject.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    renderer.material.color = Color.green;
                }
            }

            if (announce)
            {
                // Play procedurally generated click sound when reaching a node
                if (audioSource != null)
                {
                    AudioClip clickSound = GenerateClickSound();
                    audioSource.PlayOneShot(clickSound, 0.7f);
                }
            }

            Debug.Log($"Reached node {node.index} at position {node.position}");
        }

        private void FindNextTargetNode(bool resetTimer = false)
        {
            // Find the closest unreached node with an index higher than the last reached node
            PathNode nextNode = null;
            float closestDistance = float.MaxValue;

            foreach (var node in pathNodes)
            {
                if (node.isReached)
                    continue;

                // Only consider nodes after the last reached one
                if (node.index <= lastReachedNodeIndex)
                    continue;

                float distance = Vector3.Distance(transform.position, node.position);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    nextNode = node;
                }
            }

            // If no next node found, find any unreached node
            if (nextNode == null)
            {
                foreach (var node in pathNodes)
                {
                    if (node.isReached)
                        continue;

                    float distance = Vector3.Distance(transform.position, node.position);
                    if (distance < closestDistance)
                    {
                        closestDistance = distance;
                        nextNode = node;
                    }
                }
            }

            // Update current target node if we found a new one
            if (nextNode != null && nextNode != currentTargetNode)
            {
                currentTargetNode = nextNode;
                if (resetTimer)
                {
                    nodeAudioPingTimer = 0; // Only reset timer if explicitly requested
                }
                HighlightNodes();

                Debug.Log($"New target node: {currentTargetNode.index}");
            }
        }

        private void OnManualReachedDestination()
        {
            Debug.Log("Destination reached in manual mode");

            // Prevent multiple announcements
            if (hasAnnouncedDestinationReached)
                return;

            hasAnnouncedDestinationReached = true;
            isPathfinding = false;

            // Make sure to remove any agents if they exist
            NavMeshAgent[] remainingAgents = GetComponents<NavMeshAgent>();
            if (remainingAgents != null && remainingAgents.Length > 0)
            {
                Debug.LogWarning($"Found {remainingAgents.Length} NavMeshAgents in OnManualReachedDestination - destroying");
                foreach (var remaining in remainingAgents)
                {
                    Destroy(remaining);
                }
            }

            // Play the "reached destination" sound
            if (audioSource != null && audioSource.clip != null)
            {
                audioSource.Play();
            }

            // Announce reaching the destination
            if (currentTargetObject != null)
            {
                string displayName = LACore.Instance?.navMenu?.GetDisplayNameForObject(currentTargetObject) ?? "destination";
                Utilities.SpeakText($"Reached {displayName}");

                // Do not clear currentTargetObject so we can look at it after pathfinding
            }
            else
            {
                Utilities.SpeakText("Reached destination");
            }

            ClearPath();
            ClearPathNodes();

            // Reset announcement flag after delay
            StartCoroutine(ResetAnnouncementFlag());
        }

        private IEnumerator ResetAnnouncementFlag()
        {
            yield return new WaitForSeconds(2.0f);
            hasAnnouncedDestinationReached = false;
        }

        private void PingCurrentNode()
        {
            if (currentTargetNode == null || currentTargetNode.audioSource == null)
                return;

            Vector3 playerPos = transform.position;
            Vector3 nodePos = currentTargetNode.position;
            Vector3 directionToNode = (nodePos - playerPos).normalized;

            // Get forward vector of player (normalized)
            Vector3 playerForward = transform.forward.normalized;

            // Determine directional relationship - only consider horizontal component
            Vector3 horizontalToNode = new Vector3(directionToNode.x, 0, directionToNode.z).normalized;
            Vector3 horizontalForward = new Vector3(playerForward.x, 0, playerForward.z).normalized;

            float dotProduct = Vector3.Dot(horizontalForward, horizontalToNode);

            // Determine if node is in front, behind, or to the side
            bool isInFront = dotProduct > 0.3f;
            bool isBehind = dotProduct < -0.3f;
            bool isLeftRight = !isInFront && !isBehind;

            // Calculate vertical relationship
            bool isAbove = nodePos.y - playerPos.y > 3f;
            bool isBelow = nodePos.y - playerPos.y < -3f;

            // Calculate distance for volume adjustment
            float distance = Vector3.Distance(playerPos, nodePos);
            float minDistance = 1.0f;
            float maxDistance = 20.0f;
            float baseVolume = 0.6f;

            // Calculate attenuated volume
            float volume = AudioSystemBypass.CalculateVolumeBasedOnDistance(
                distance, minDistance, maxDistance, baseVolume);

            // Get the appropriate tone based on direction
            AudioClip clip = GetNodePingTone(isInFront, isLeftRight, isBelow, isAbove);

            // Set up the audio source
            if (clip != null)
            {
                currentTargetNode.audioSource.clip = clip;
                currentTargetNode.audioSource.volume = volume;

                // Play the audio
                currentTargetNode.audioSource.Play();
            }
            else
            {
                Debug.LogWarning("Node ping audio clip not loaded yet");
            }
        }

        private AudioClip GetNodePingTone(bool isInFront, bool isLeftRight, bool isBelow, bool isAbove)
        {
            // Use the appropriate audio clip based on direction
            if (isInFront)
            {
                return onTargetSound; // "On Target.wav" for when looking right at it
            }
            else if (isLeftRight)
            {
                return almostOnTargetSound; // "Almost On Target.wav" for left/right
            }
            else // behind
            {
                return offTargetSound; // "Off Target.wav" for behind
            }
        }

        private AudioClip GenerateClickSound()
        {
            int sampleRate = 44100;
            float duration = 0.05f;
            float frequency = 2000f;

            AudioClip clip = AudioClip.Create("ClickSound", (int)(sampleRate * duration), 1, sampleRate, false);

            float[] samples = new float[(int)(sampleRate * duration)];
            for (int i = 0; i < samples.Length; i++)
            {
                float t = (float)i / (float)samples.Length;
                float envelope = Mathf.Exp(-t * 10); // Sharp attack, quick decay
                samples[i] = Mathf.Sin(2 * Mathf.PI * frequency * t) * envelope * 0.6f;
            }

            clip.SetData(samples, 0);
            return clip;
        }
        #endregion

        #region Agent Management
        private void CreateOrRecreateAgent()
        {
            try
            {
                if (StartOfRound.Instance == null || !StartOfRound.Instance.shipHasLanded)
                {
                    Debug.Log("Ship not landed; not creating NavMeshAgent.");
                    return;
                }

                // First, check for and destroy any existing agents using DestroyImmediate
                NavMeshAgent[] existingAgents = GetComponents<NavMeshAgent>();
                if (existingAgents.Length > 0)
                {
                    Debug.Log($"Found {existingAgents.Length} existing NavMeshAgents, removing them immediately");

                    foreach (var existingAgent in existingAgents)
                    {
                        try
                        {
                            if (existingAgent.isActiveAndEnabled && existingAgent.isOnNavMesh)
                            {
                                existingAgent.isStopped = true;
                                existingAgent.ResetPath();
                                existingAgent.enabled = false;
                            }
                            DestroyImmediate(existingAgent);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"Error destroying existing agent: {ex.Message}");
                            // Last resort
                            try { Destroy(existingAgent); } catch { }
                        }
                    }

                    // Re-check to make sure all agents are gone
                    existingAgents = GetComponents<NavMeshAgent>();
                    if (existingAgents.Length > 0)
                    {
                        Debug.LogError("Failed to remove all existing NavMeshAgents before creating a new one");
                        return; // Don't continue if we couldn't remove existing agents
                    }
                }

                // Now create a new agent and store the reference
                agent = gameObject.AddComponent<NavMeshAgent>();
                Debug.Log($"Created new NavMeshAgent: {agent.GetInstanceID()}");

                // Configure immediately instead of waiting
                ConfigureAgent();

                // Check if the agent is on a navmesh
                isOnNavMesh = agent.isOnNavMesh;

                if (!isOnNavMesh)
                {
                    Debug.LogWarning("Agent created but not on NavMesh. Will try to warp to NavMesh.");

                    // Try to warp to a valid NavMesh position
                    NavMeshHit hit;
                    if (NavMesh.SamplePosition(transform.position, out hit, 5f, NavMesh.AllAreas))
                    {
                        agent.Warp(hit.position);
                        isOnNavMesh = agent.isOnNavMesh;

                        if (isOnNavMesh)
                        {
                            Debug.Log("Successfully warped agent to NavMesh.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in CreateOrRecreateAgent: {ex.Message}");
                agent = null; // Make sure we don't keep a reference to a failed agent
            }
        }

        void ConfigureAgent()
        {
            try
            {
                if (agent == null)
                    return;

                agent.speed = baseSpeed * 3f;
                agent.angularSpeed = 1200f;
                agent.acceleration = 12f;
                agent.radius = 0.3f;
                agent.baseOffset = currentMode == PathfindingMode.Auto ? 0.4f : 0.1f; // Lower offset in manual mode
                agent.stoppingDistance = stoppingRadius; // Use fixed distance
                agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
                agent.areaMask = NavMesh.AllAreas;
                agent.autoRepath = true;
                agent.autoTraverseOffMeshLink = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in ConfigureAgent: {ex.Message}");
            }
        }

        private void EnsureNavMeshAgentIsRemoved()
        {
            try
            {
                // Only run cleanup if we're not actively pathfinding
                if (!isPathfinding)
                {
                    // First check our tracked agent
                    if (agent != null)
                    {
                        RemoveAgent();
                        Debug.Log("NavMeshAgent removed from the player in periodic check.");
                    }

                    // Then thoroughly check for any stray agents
                    NavMeshAgent[] existingAgents = GetComponents<NavMeshAgent>();
                    if (existingAgents != null && existingAgents.Length > 0)
                    {
                        Debug.LogWarning($"Found {existingAgents.Length} stray NavMeshAgents in periodic check - removing them");
                        foreach (var strayAgent in existingAgents)
                        {
                            try
                            {
                                if (strayAgent.isActiveAndEnabled && strayAgent.isOnNavMesh)
                                {
                                    strayAgent.isStopped = true;
                                    strayAgent.ResetPath();
                                    strayAgent.enabled = false;
                                }
                                DestroyImmediate(strayAgent); // Use immediate destruction
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError($"Error destroying stray agent: {ex.Message}");
                                Destroy(strayAgent); // Fall back to normal Destroy
                            }
                        }
                        agent = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in EnsureNavMeshAgentIsRemoved: {ex.Message}");
            }
        }

        private void RemoveAgent()
        {
            try
            {
                // First attempt - standard cleanup
                if (agent != null)
                {
                    // Make sure to stop and reset the agent first if it's active
                    if (agent.isActiveAndEnabled)
                    {
                        if (agent.isOnNavMesh)
                        {
                            agent.isStopped = true;
                            agent.ResetPath();
                        }
                        agent.enabled = false;
                    }

                    Destroy(agent);
                    agent = null;
                    Debug.Log("NavMeshAgent component destroyed via RemoveAgent()");
                }

                // Second attempt - find any remaining NavMeshAgent on this GameObject
                NavMeshAgent[] existingAgents = GetComponents<NavMeshAgent>();
                if (existingAgents != null && existingAgents.Length > 0)
                {
                    foreach (NavMeshAgent existingAgent in existingAgents)
                    {
                        Debug.LogWarning($"Found additional NavMeshAgent, destroying: {existingAgent.GetInstanceID()}");

                        try
                        {
                            if (existingAgent.isActiveAndEnabled && existingAgent.isOnNavMesh)
                            {
                                existingAgent.isStopped = true;
                                existingAgent.ResetPath();
                            }
                            existingAgent.enabled = false;

                            // Use DestroyImmediate for guaranteed immediate removal
                            DestroyImmediate(existingAgent);
                        }
                        catch (Exception innerEx)
                        {
                            Debug.LogError($"Error destroying additional agent: {innerEx.Message}");
                        }
                    }
                }

                // Final check to ensure all agents are gone
                existingAgents = GetComponents<NavMeshAgent>();
                if (existingAgents != null && existingAgents.Length > 0)
                {
                    Debug.LogError("Failed to remove all NavMeshAgents despite multiple attempts");
                }
                else
                {
                    Debug.Log("NavMeshAgent removal verified successful");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in RemoveAgent: {ex.Message}");

                // Extreme fallback - try DestroyImmediate on any agent we can find
                try
                {
                    NavMeshAgent[] allAgents = GetComponents<NavMeshAgent>();
                    foreach (NavMeshAgent a in allAgents)
                    {
                        DestroyImmediate(a);
                    }
                }
                catch { }
            }
        }
        #endregion

        #region Auto Mode Navigation Logic
        void UpdateAgentSpeed()
        {
            try
            {
                if (playerController != null && agent != null)
                {
                    float carryWeight = playerController.carryWeight - 1f;
                    carryWeight = Mathf.Clamp(carryWeight, 0f, 1.4f);
                    carryWeight *= 1.5f;
                    carryWeight = Mathf.Min(carryWeight, 1.4f);
                    float speedFactor = Mathf.Lerp(1f, 0.15f, carryWeight / 1.4f);
                    agent.speed = baseSpeed * 3.5f * speedFactor;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in UpdateAgentSpeed: {ex.Message}");
            }
        }

        void OnReachedDestination()
        {
            try
            {
                // Prevent multiple announcements
                if (hasAnnouncedDestinationReached)
                    return;

                hasAnnouncedDestinationReached = true;
                isPathfinding = false;

                // First make sure to clean up the agent
                if (agent != null)
                {
                    if (agent.isActiveAndEnabled && agent.isOnNavMesh)
                    {
                        agent.isStopped = true;
                        agent.ResetPath();
                    }
                    Destroy(agent);
                    agent = null;
                }

                // Check for any other NavMeshAgents
                NavMeshAgent[] remainingAgents = GetComponents<NavMeshAgent>();
                if (remainingAgents != null && remainingAgents.Length > 0)
                {
                    Debug.LogWarning($"Found {remainingAgents.Length} NavMeshAgents in OnReachedDestination - destroying");
                    foreach (var remaining in remainingAgents)
                    {
                        Destroy(remaining);
                    }
                }

                // Play audio when destination reached
                if (audioSource != null && audioSource.clip != null)
                {
                    audioSource.Play();
                }

                // Announce reaching the destination
                if (currentTargetObject != null)
                {
                    string displayName = LACore.Instance?.navMenu?.GetDisplayNameForObject(currentTargetObject) ?? "destination";
                    Utilities.SpeakText($"Reached {displayName}");

                    // Do not clear currentTargetObject so we can look at it after pathfinding
                }
                else
                {
                    Utilities.SpeakText("Reached destination");
                }

                ClearPath();
                ClearPathNodes();

                // Reset announcement flag after delay
                StartCoroutine(ResetAnnouncementFlag());
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in OnReachedDestination: {ex.Message}");
                hasAnnouncedDestinationReached = false;
            }
        }

        void DetectAndSpeakDoor()
        {
            try
            {
                float doorDetectionDistance = 2.25f;
                DoorLock closestDoor = null;
                float closestDistance = doorDetectionDistance;

                var doors = FindObjectsOfType<DoorLock>();
                if (doors == null)
                    return;

                foreach (var doorGameObject in doors)
                {
                    if (doorGameObject == null)
                        continue;

                    float distanceToDoor = Vector3.Distance(transform.position, doorGameObject.transform.position);
                    if (distanceToDoor < closestDistance)
                    {
                        closestDistance = distanceToDoor;
                        closestDoor = doorGameObject;
                    }
                }

                if (closestDoor != null)
                {
                    var animatedObjectTrigger = closestDoor.GetComponent<AnimatedObjectTrigger>();
                    if (animatedObjectTrigger != null && !animatedObjectTrigger.boolValue)
                    {
                        try
                        {
                            closestDoor.OpenOrCloseDoor(playerController);
                        }
                        catch (Exception doorEx)
                        {
                            Debug.LogError($"Error opening door: {doorEx.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in DetectAndSpeakDoor: {ex.Message}");
            }
        }

        void HandleStuckDetection()
        {
            try
            {
                if (Time.time - lastPositionUpdateTime > 3f)
                {
                    if (Vector3.Distance(transform.position, lastPosition) < 0.5f && isPathfinding)
                    {
                        Debug.Log("Player appears to be stuck. Attempting teleport along path.");
                        TeleportAlongPath(5f);
                    }
                    lastPosition = transform.position;
                    lastPositionUpdateTime = Time.time;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in HandleStuckDetection: {ex.Message}");
            }
        }

        void TeleportAlongPath(float distance)
        {
            try
            {
                // First validate the agent's existence
                if (agent == null || !agent.isActiveAndEnabled)
                {
                    Debug.LogWarning("Agent is null or disabled during teleport attempt - trying to recreate");
                    CreateOrRecreateAgent();

                    // Wait a frame to let the agent initialize
                    StartCoroutine(DelayedTeleportAttempt(distance));
                    return;
                }

                // Then check if it's on NavMesh and has a valid path
                if (!agent.isOnNavMesh || agent.path == null || agent.path.corners.Length < 2)
                {
                    Debug.LogWarning("Agent is not on NavMesh or has invalid path - recalculating");

                    // Try to recalculate path
                    if (currentTargetObject != null && agent.isOnNavMesh)
                    {
                        bool success = agent.SetDestination(currentDestination);
                        if (!success)
                        {
                            Debug.LogError("Failed to set destination during teleport recovery");
                            ResetNavMeshAgent();
                            return;
                        }

                        // Wait a frame for path calculation
                        StartCoroutine(DelayedTeleportAttempt(distance));
                        return;
                    }
                    else
                    {
                        ResetNavMeshAgent();
                        return;
                    }
                }

                Vector3 direction = agent.path.corners[1] - transform.position;
                Vector3 teleportPoint = transform.position + direction.normalized * distance;

                if (TryTeleportTo(teleportPoint))
                {
                    Debug.Log("Teleported player to " + teleportPoint);
                    ContinuePathfinding();
                    return;
                }

                // Try multiple random points if direct teleport fails
                for (int attempts = 0; attempts < 10; attempts++)
                {
                    Vector3 randomOffset = UnityEngine.Random.insideUnitSphere * 2f;
                    // Keep the y component minimal to avoid teleporting too high/low
                    randomOffset.y = randomOffset.y * 0.5f;
                    Vector3 randomPoint = transform.position + randomOffset;

                    // Make sure the point is above ground
                    randomPoint = EnsurePointAboveGround(randomPoint);

                    if (TryTeleportTo(randomPoint))
                    {
                        Debug.Log("Teleported player to nearby random point: " + randomPoint);
                        ContinuePathfinding();
                        return;
                    }
                }

                // If all teleport attempts fail, try to reset the agent
                ResetNavMeshAgent();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in TeleportAlongPath: {ex.Message}");
                ResetNavMeshAgent();
            }
        }

        private IEnumerator DelayedTeleportAttempt(float distance)
        {
            // Wait for agent initialization and path calculation
            yield return new WaitForSeconds(0.2f);

            // Retry teleport
            if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
            {
                // If we still don't have a valid path, try to create one
                if (agent.path == null || agent.path.corners.Length < 2)
                {
                    if (agent.SetDestination(currentDestination))
                    {
                        // Wait another frame for path calculation
                        yield return null;
                    }
                }

                // Now check if we can teleport
                if (agent.path != null && agent.path.corners.Length >= 2)
                {
                    Vector3 direction = agent.path.corners[1] - transform.position;
                    Vector3 teleportPoint = transform.position + direction.normalized * distance;

                    if (TryTeleportTo(teleportPoint))
                    {
                        Debug.Log("Delayed teleport succeeded to " + teleportPoint);
                        ContinuePathfinding();
                        yield break;
                    }
                    else
                    {
                        // Try random teleport as in original method
                        Debug.Log("Attempting random teleport points");
                        for (int attempts = 0; attempts < 10; attempts++)
                        {
                            Vector3 randomOffset = UnityEngine.Random.insideUnitSphere * 2f;
                            randomOffset.y = randomOffset.y * 0.5f;
                            Vector3 randomPoint = transform.position + randomOffset;
                            randomPoint = EnsurePointAboveGround(randomPoint);

                            if (TryTeleportTo(randomPoint))
                            {
                                Debug.Log("Teleported player to nearby random point: " + randomPoint);
                                ContinuePathfinding();
                                yield break;
                            }
                        }
                    }
                }
            }

            // If we get here, teleport failed
            Debug.LogWarning("Delayed teleport attempt failed");
            ResetNavMeshAgent();
        }

        private void ResetNavMeshAgent()
        {
            try
            {
                Debug.Log("Resetting NavMeshAgent after failed teleport attempts");

                if (IsAgentValid())
                {
                    agent.isStopped = true;
                    agent.ResetPath();
                }

                RemoveAgent();
                CreateOrRecreateAgent();

                StartCoroutine(DelayedPathReset());
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in ResetNavMeshAgent: {ex.Message}");
                StopPathfinding();
            }
        }

        private IEnumerator DelayedPathReset()
        {
            yield return new WaitForSeconds(0.3f); // Wait for agent to initialize

            try
            {
                if (IsAgentValid() && currentTargetObject != null)
                {
                    Utilities.SpeakText("Recalculating path after getting stuck.");
                    NavigateTo(currentTargetObject);
                }
                else
                {
                    StopPathfinding();
                    Utilities.SpeakText("Could not recover from being stuck. Stopping pathfinding.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in DelayedPathReset: {ex.Message}");
                StopPathfinding();
                Utilities.SpeakText("Error resetting path. Pathfinding stopped.");
            }
        }

        bool TryTeleportTo(Vector3 point)
        {
            try
            {
                if (agent == null || !agent.isActiveAndEnabled)
                    return false;

                NavMeshHit hit;
                if (NavMesh.SamplePosition(point, out hit, 2.0f, NavMesh.AllAreas))
                {
                    agent.Warp(hit.position);
                    return agent.isOnNavMesh;
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in TryTeleportTo: {ex.Message}");
                return false;
            }
        }

        void ContinuePathfinding()
        {
            try
            {
                if (IsAgentValid())
                {
                    agent.SetDestination(currentDestination);
                    isPathfinding = true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in ContinuePathfinding: {ex.Message}");
            }
        }
        #endregion

        #region Utility Methods
        private Mesh CreateSphereMesh(float radius)
        {
            Mesh mesh = new Mesh();
            GameObject tempSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            mesh.name = "NodeSphere";
            mesh.vertices = tempSphere.GetComponent<MeshFilter>().sharedMesh.vertices;
            mesh.triangles = tempSphere.GetComponent<MeshFilter>().sharedMesh.triangles;
            mesh.normals = tempSphere.GetComponent<MeshFilter>().sharedMesh.normals;
            mesh.uv = tempSphere.GetComponent<MeshFilter>().sharedMesh.uv;

            // Scale the vertices to match the desired radius
            Vector3[] vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = vertices[i] * radius;
            }
            mesh.vertices = vertices;

            Destroy(tempSphere);
            return mesh;
        }
        #endregion

        #region Update and Cleanup
        void FixedUpdate()
        {
            try
            {
                // Check if player is dead
                if (playerController != null && playerController.isPlayerDead)
                {
                    if (isPathfinding)
                    {
                        StopPathfinding();
                        Utilities.SpeakText("Pathfinding stopped because the player is dead.");
                    }
                    return;
                }

                // Extra safety check - if we're not pathfinding, make sure no NavMeshAgent exists
                if (!isPathfinding && !isGeneratingPath && !isInitializingPathfinding)
                {
                    return; // Early return to prevent further processing
                }

                // For path generation timeout detection
                if (isGeneratingPath && Time.time - pathGenerationStartTime > PATH_GENERATION_TIMEOUT)
                {
                    Debug.LogWarning("Path generation timeout exceeded, stopping pathfinding");
                    isGeneratingPath = false;
                    StopPathfinding();
                    Utilities.SpeakText("Path generation is taking too long. Please try again.");
                    return;
                }

                // Check for path generation completion
                if (isGeneratingPath && pathGenerationComplete)
                {
                    isGeneratingPath = false;
                }

                // Handle manual or auto pathfinding
                if (currentMode == PathfindingMode.Manual)
                {
                    // Manual pathfinding node updates
                    UpdateManualPathfinding();

                    // In manual mode we still want to open doors
                    DetectAndSpeakDoor();
                }
                else if (currentMode == PathfindingMode.Auto)
                {
                    // Only proceed with auto mode if agent is valid
                    if (!IsAgentValid())
                    {
                        // Agent is not valid, try to recover
                        if (!isRecoveringFromError && Time.time - recoveryAttemptTime > RECOVERY_COOLDOWN)
                        {
                            AttemptRecoveryFromError();
                        }
                        return; // Early return if agent is invalid
                    }

                    // Auto mode with agent
                    if (agent.isOnOffMeshLink)
                    {
                        agent.CompleteOffMeshLink();
                    }
                    else if (agent.isOnNavMesh && !agent.pathPending)
                    {
                        // Check if we're at the end of the path or within stopping radius of it
                        bool shouldStopPathfinding = false;

                        // Guard against multiple stop checks in a frame
                        if (hasAnnouncedDestinationReached)
                        {
                            shouldStopPathfinding = false;
                        }
                        // Check if we've reached the end of the path
                        else if (agent.remainingDistance <= agent.stoppingDistance)
                        {
                            shouldStopPathfinding = true;
                            Debug.Log("Reached destination by agent stopping distance");
                        }
                        // Check if we're within stopping radius of the final destination
                        else if (agent.path != null && agent.path.corners.Length > 0)
                        {
                            Vector3 finalCorner = agent.path.corners[agent.path.corners.Length - 1];
                            float distToFinal = Vector3.Distance(transform.position, finalCorner);

                            if (distToFinal <= stoppingRadius)
                            {
                                shouldStopPathfinding = true;
                                Debug.Log($"Reached destination by proximity to final corner: {distToFinal}m");
                            }
                        }
                        // Special handling for off-NavMesh targets
                        else if (isOffNavMeshTarget && currentTargetObject != null)
                        {
                            float distToOriginal = Vector3.Distance(transform.position, offNavMeshTargetPos);
                            if (distToOriginal <= stoppingRadius)
                            {
                                shouldStopPathfinding = true;
                                Debug.Log($"Reached off-NavMesh target: {distToOriginal}m");
                            }
                        }
                        // Distance to set destination
                        else if (Vector3.Distance(transform.position, currentDestination) <= stoppingRadius)
                        {
                            shouldStopPathfinding = true;
                            Debug.Log($"Reached destination by proximity to destination point: {Vector3.Distance(transform.position, currentDestination)}m");
                        }

                        if (shouldStopPathfinding && !hasAnnouncedDestinationReached)
                        {
                            // Stop walking animation
                            if (playerController != null)
                            {
                                playerController.playerBodyAnimator.SetBool("Walking", false);
                            }

                            if (forcedPathActive)
                            {
                                if (currentForcedWaypointIndex < forcedWaypoints.Count - 1)
                                {
                                    currentForcedWaypointIndex++;
                                    currentDestination = forcedWaypoints[currentForcedWaypointIndex];

                                    try
                                    {
                                        if (agent.isOnNavMesh)
                                        {
                                            agent.SetDestination(currentDestination);
                                            Utilities.SpeakText("Proceeding to next waypoint");
                                        }
                                        else
                                        {
                                            Debug.LogError("Agent not on NavMesh when setting waypoint");
                                            StopPathfinding();
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.LogError($"Error setting waypoint destination: {ex.Message}");
                                        StopPathfinding();
                                    }
                                }
                                else
                                {
                                    forcedPathActive = false;
                                    OnReachedDestination();
                                    return; // Return immediately after OnReachedDestination
                                }
                            }
                            else
                            {
                                OnReachedDestination();
                                return; // Return immediately after OnReachedDestination
                            }
                        }
                        else if (!hasAnnouncedDestinationReached) // Skip if destination already reached
                        {
                            // Play walking animation while moving in auto mode
                            if (playerController != null)
                            {
                                playerController.playerBodyAnimator.SetBool("Walking", true);
                            }
                            UpdateAgentSpeed();

                            // Draw the path for visualization in auto mode
                            if (agent != null && agent.hasPath) // Extra null check
                            {
                                DrawPath(agent.path);
                            }
                        }
                    }

                    // Only perform these operations if we're still pathfinding and agent is valid
                    if (isPathfinding && agent != null)
                    {
                        DetectAndSpeakDoor();
                        HandleStuckDetection();
                    }

                    // Recovery handling if agent is valid but not on NavMesh
                    if (agent != null && !agent.isOnNavMesh)
                    {
                        if (!isRecoveringFromError && Time.time - recoveryAttemptTime > RECOVERY_COOLDOWN)
                        {
                            AttemptRecoveryFromError();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in FixedUpdate: {ex.Message}\n{ex.StackTrace}");
                if (isPathfinding && recoveryAttempts < MAX_RECOVERY_ATTEMPTS)
                {
                    AttemptRecoveryFromError();
                }
                else if (isPathfinding)
                {
                    StopPathfinding();
                    Utilities.SpeakText("Pathfinding stopped due to an error.");
                }
            }
        }

        private void AttemptRecoveryFromError()
        {
            try
            {
                isRecoveringFromError = true;
                recoveryAttemptTime = Time.time;
                recoveryAttempts++;

                Debug.Log($"Attempting pathfinding recovery #{recoveryAttempts}");

                if (recoveryAttempts >= MAX_RECOVERY_ATTEMPTS)
                {
                    StopPathfinding();
                    Utilities.SpeakText("Could not recover from pathfinding error after multiple attempts.");
                    return;
                }

                // Only try to recover in auto mode
                if (currentMode == PathfindingMode.Manual)
                {
                    isRecoveringFromError = false;
                    return;
                }

                // Check if ship is landed
                if (StartOfRound.Instance == null || !StartOfRound.Instance.shipHasLanded)
                {
                    StopPathfinding();
                    Utilities.SpeakText("Cannot pathfind while the ship is not landed.");
                    return;
                }

                // Recreate the agent
                RemoveAgent();
                CreateOrRecreateAgent();

                // Wait a moment for the agent to be created
                StartCoroutine(DelayedRecoveryAfterAgentCreation());
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in AttemptRecoveryFromError: {ex.Message}");
                isRecoveringFromError = false;
                StopPathfinding();
                Utilities.SpeakText("Error during pathfinding recovery. Stopped.");
            }
        }

        private IEnumerator DelayedRecoveryAfterAgentCreation()
        {
            yield return new WaitForSeconds(0.2f); // Wait a moment for agent to initialize

            try
            {
                if (agent == null)
                {
                    StopPathfinding();
                    Utilities.SpeakText("Failed to recreate navigation agent.");
                    isRecoveringFromError = false;
                    yield break;
                }

                // Check if the agent is on NavMesh
                if (!agent.isOnNavMesh)
                {
                    // Try to warp to a valid NavMesh position
                    NavMeshHit hit;
                    if (NavMesh.SamplePosition(transform.position, out hit, 5f, NavMesh.AllAreas))
                    {
                        agent.Warp(hit.position);

                        // Check again if on NavMesh after warping
                        if (!agent.isOnNavMesh)
                        {
                            StopPathfinding();
                            Utilities.SpeakText("Could not find valid navigation surface. Pathfinding stopped.");
                            isRecoveringFromError = false;
                            yield break;
                        }
                    }
                    else
                    {
                        StopPathfinding();
                        Utilities.SpeakText("No valid NavMesh found near your position. Pathfinding stopped.");
                        isRecoveringFromError = false;
                        yield break;
                    }
                }

                // Try to set destination
                if (currentTargetObject != null)
                {
                    // In auto mode, regenerate path
                    if (currentMode == PathfindingMode.Auto)
                    {
                        try
                        {
                            bool success = agent.SetDestination(currentDestination);
                            if (success)
                            {
                                Utilities.SpeakText("Resuming automatic pathfinding.");
                                isRecoveringFromError = false;
                            }
                            else
                            {
                                StopPathfinding();
                                Utilities.SpeakText("Failed to recover pathfinding. Stopped.");
                                isRecoveringFromError = false;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"Recovery SetDestination exception: {ex.Message}");
                            StopPathfinding();
                            Utilities.SpeakText("Failed to recover pathfinding. Stopped.");
                            isRecoveringFromError = false;
                        }
                    }
                }
                else
                {
                    // Fallback to current destination if target is gone
                    try
                    {
                        bool success = agent.SetDestination(currentDestination);
                        if (success)
                        {
                            Utilities.SpeakText("Resuming pathfinding.");
                            isRecoveringFromError = false;
                        }
                        else
                        {
                            StopPathfinding();
                            Utilities.SpeakText("Failed to recover pathfinding. Stopped.");
                            isRecoveringFromError = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Recovery SetDestination exception: {ex.Message}");
                        StopPathfinding();
                        Utilities.SpeakText("Failed to recover pathfinding. Stopped.");
                        isRecoveringFromError = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in DelayedRecoveryAfterAgentCreation: {ex.Message}");
                StopPathfinding();
                Utilities.SpeakText("Error during pathfinding recovery. Stopped.");
                isRecoveringFromError = false;
            }
        }

        void OnDestroy()
        {
            try
            {
                // Clean up any agents when this component is destroyed
                NavMeshAgent[] allAgents = GetComponents<NavMeshAgent>();
                if (allAgents.Length > 0)
                {
                    Debug.Log($"Cleaning up {allAgents.Length} NavMeshAgents during Pathfinder.OnDestroy");

                    foreach (var a in allAgents)
                    {
                        try
                        {
                            if (a.isActiveAndEnabled && a.isOnNavMesh)
                            {
                                a.isStopped = true;
                                a.ResetPath();
                            }
                            DestroyImmediate(a);
                        }
                        catch
                        {
                            Destroy(a);
                        }
                    }
                }

                // Clean up any coroutines
                StopAllCoroutines();

                // Clear and destroy all nodes
                ClearPathNodes();

                // Clean up node pool
                foreach (var nodeObj in nodeObjectPool)
                {
                    if (nodeObj != null)
                    {
                        Destroy(nodeObj);
                    }
                }
                nodeObjectPool.Clear();

                // Null reference to prevent use after destruction
                Instance = null;

                Debug.Log("Pathfinder component destroyed, all resources cleaned up");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in Pathfinder.OnDestroy: {ex.Message}");
            }
        }
        #endregion
    }

    /// <summary>
    /// Static helper class to safely handle NavMeshAgent operations
    /// </summary>
    public static class NavMeshProxy
    {
        // Static methods to safely handle NavMeshAgent operations
        public static void SafeDestroyAgent(MonoBehaviour owner, ref NavMeshAgent agent)
        {
            if (agent != null)
            {
                try
                {
                    if (agent.isActiveAndEnabled && agent.isOnNavMesh)
                    {
                        agent.isStopped = true;
                        agent.ResetPath();
                        agent.enabled = false;
                    }
                    UnityEngine.Object.DestroyImmediate(agent);
                    agent = null;
                    Debug.Log("NavMeshAgent destroyed successfully");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error in SafeDestroyAgent: {ex.Message}");
                    try { UnityEngine.Object.Destroy(agent); } catch { }
                    agent = null;
                }
            }
        }

        public static void ClearAllAgents(MonoBehaviour owner)
        {
            try
            {
                NavMeshAgent[] agents = owner.GetComponents<NavMeshAgent>();
                if (agents != null && agents.Length > 0)
                {
                    Debug.Log($"Clearing {agents.Length} NavMeshAgents");
                    foreach (var a in agents)
                    {
                        try
                        {
                            if (a.isActiveAndEnabled && a.isOnNavMesh)
                            {
                                a.isStopped = true;
                                a.ResetPath();
                                a.enabled = false;
                            }
                            UnityEngine.Object.DestroyImmediate(a);
                        }
                        catch
                        {
                            try { UnityEngine.Object.Destroy(a); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in ClearAllAgents: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Harmony patch to ensure the NavMeshAgent is created when the game starts
    /// </summary>
    [HarmonyPatch(typeof(StartOfRound), "StartGame")]
    class StartOfRoundNavMeshPatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            try
            {
                if (Pathfinder.Instance != null && (StartOfRound.Instance != null && StartOfRound.Instance.shipHasLanded))
                {
                    if (Pathfinder.Instance.GetComponent<NavMeshAgent>() == null)
                    {
                        Pathfinder.Instance.gameObject.AddComponent<NavMeshAgent>();
                        Pathfinder.Instance.SendMessage("ConfigureAgent");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in StartOfRoundNavMeshPatch.Postfix: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Harmony patch to prevent fall damage during pathfinding
    /// </summary>
    [HarmonyPatch(typeof(PlayerControllerB))]
    class PlayerControllerBFallDamagePatch
    {
        [HarmonyPrefix]
        [HarmonyPatch("PlayerHitGroundEffects")]
        static bool Prefix(PlayerControllerB __instance, ref float ___fallValue)
        {
            try
            {
                if (Pathfinder.ShouldPreventFallDamage)
                {
                    __instance.takingFallDamage = false;
                    ___fallValue = 0;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in PlayerControllerBFallDamagePatch.Prefix: {ex.Message}");
                return true; // Allow original method to run on error
            }
        }
    }
}