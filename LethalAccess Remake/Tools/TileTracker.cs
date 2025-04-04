using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using DunGen;
using DunGen.Tags;
using GameNetcodeStuff;
using LethalAccess;
using UnityEngine;
using UnityEngine.InputSystem;
using Key = UnityEngine.InputSystem.Key;

namespace LethalAccess
{
    /// <summary>
    /// Tracks the player's position within the dungeon tile system
    /// and provides navigation assistance via visual and audio cues.
    /// </summary>
    public class TileTracker : MonoBehaviour
    {
        #region Enums and Classes

        public enum NavigationMode
        {
            Explore,   // Show undiscovered navigation points
            Retrace,   // Show only discovered navigation points
            Off        // No navigation points shown
        }

        public class NavigationPoint
        {
            public enum PointType
            {
                DoorWay,
                HallwayOpening,
                Junction,
                DeadEnd
            }

            public Vector3 position;
            public PointType type;
            public GameObject visualMarker;
            public Tile parentTile;
            public string description;
            public AudioSource audioSource;
            public AudioClip audioClip;
            public int connectionCount;
            public bool isDiscovered = false;
            public List<GameObject> additionalMarkers = new List<GameObject>();

            public NavigationPoint(Vector3 position, PointType type, Tile parentTile, string description = "", int connectionCount = 0)
            {
                this.position = position;
                this.type = type;
                this.parentTile = parentTile;
                this.description = description;
                this.connectionCount = connectionCount;
            }
        }

        #endregion

        #region Configuration Properties

        private NavigationMode currentMode = NavigationMode.Explore;
        private bool showVisuals = true;
        private float proximityThreshold = 40f;
        private float markerSize = 0.5f;
        private float masterVolume = 2f;
        private float audioProximityThreshold = 10f;
        private float audioPingInterval = 0.75f;
        private float audioDelayBetweenPoints = 0.3f;
        private bool checkLineOfSight = true;
        private LayerMask wallLayerMask;

        // Discovery thresholds for different point types
        private Dictionary<NavigationPoint.PointType, float> discoveryThresholds = new Dictionary<NavigationPoint.PointType, float>
        {
            { NavigationPoint.PointType.DoorWay, 1.2f },
            { NavigationPoint.PointType.HallwayOpening, 3f },
            { NavigationPoint.PointType.Junction, 3f },
            { NavigationPoint.PointType.DeadEnd, 3f }
        };

        private float junctionAnnounceCooldown = 0f;
        private float junctionAnnounceInterval = 3f;

        // Colors for different navigation point types
        private readonly Dictionary<NavigationPoint.PointType, Color> pointColors = new Dictionary<NavigationPoint.PointType, Color>
        {
            { NavigationPoint.PointType.DoorWay, Color.green },
            { NavigationPoint.PointType.HallwayOpening, Color.cyan },
            { NavigationPoint.PointType.Junction, new Color(0.5f, 0f, 1f) },
            { NavigationPoint.PointType.DeadEnd, Color.white }
        };

        // Volume levels for different navigation point types
        private readonly Dictionary<NavigationPoint.PointType, float> pointTypeVolumes = new Dictionary<NavigationPoint.PointType, float>
        {
            { NavigationPoint.PointType.DoorWay, 0.9f },
            { NavigationPoint.PointType.HallwayOpening, 0.8f },
            { NavigationPoint.PointType.Junction, 1f },
            { NavigationPoint.PointType.DeadEnd, 0.7f }
        };

        #endregion

        #region State Variables

        // Dungeon tracking
        private Dungeon[] dungeons;
        private Tile currentTile;

        // Dungeon initialization state
        private bool dungeonInitialized = false;
        private bool isStartupComplete = false;
        private float startupDelay = 3f;
        private int dungeonCheckAttempts = 0;
        private const int MAX_DUNGEON_CHECK_ATTEMPTS = 3;

        // Navigation points
        private ConcurrentBag<NavigationPoint> navigationPoints = new ConcurrentBag<NavigationPoint>();
        private Dictionary<string, List<NavigationPoint>> tileNavigationPoints = new Dictionary<string, List<NavigationPoint>>();
        private readonly object tileNavigationLock = new object();

        // Component references
        private ManualLogSource Logger;
        private Transform playerTransform;
        private TileTriggerListener tileTriggerListener;
        private PlayerControllerB playerController;

        // State tracking
        private bool markersInitialized = false;
        private float lastAudioPingTime = 0f;
        private bool isPlayingSequentialAudio = false;
        private Tile lastAnnouncedJunctionTile = null;
        private ConcurrentBag<string> discoveredTileNames = new ConcurrentBag<string>();

        // Audio generation
        private AudioClip reachedPointSound;
        private Material markerMaterial;
        private Dictionary<NavigationPoint.PointType, AudioClip> audioClips = new Dictionary<NavigationPoint.PointType, AudioClip>();

        // Visualization update
        private int updateFrameCounter = 0;
        private const int VISUALIZATION_UPDATE_INTERVAL = 10;
        private Vector3 cachedPlayerPosition;
        private readonly object playerPositionLock = new object();

        // Object pooling
        private Queue<GameObject> markerPool = new Queue<GameObject>();
        private const int INITIAL_POOL_SIZE = 30;
        private const int MAX_POOL_SIZE = 200;

        #endregion

        #region Unity Lifecycle Methods

        public void Initialize(ManualLogSource logger, Transform player)
        {
            try
            {
                Logger = logger;
                playerTransform = player;

                LogMessage("TileTracker initialization started");

                // Setup layer mask for wall detection
                wallLayerMask = LayerMask.GetMask(new string[] { "Default", "Environment", "Blocking", "Facility" });
                if (StartOfRound.Instance != null)
                {
                    wallLayerMask |= StartOfRound.Instance.collidersAndRoomMaskAndDefault;
                }

                // Add the TileTriggerListener to the player if it doesn't exist
                if (player != null)
                {
                    playerController = player.GetComponent<PlayerControllerB>();
                    if (playerController != null)
                    {
                        tileTriggerListener = playerController.gameObject.AddComponent<TileTriggerListener>();
                        tileTriggerListener.SetTracker(this);
                        LogMessage("Added TileTriggerListener to player");

                        lock (playerPositionLock)
                        {
                            cachedPlayerPosition = player.position;
                        }
                    }
                }

                // Create marker material
                markerMaterial = new Material(Shader.Find("Sprites/Default"));
                markerMaterial.SetFloat("_Mode", 3f);

                // Register keybinds with LACore
                LACore.Instance.RegisterKeybind("CycleNavigationMode", Key.M, CycleNavigationMode);
                LACore.Instance.RegisterKeybind("ToggleVisuals", Key.Backspace, ToggleVisuals);
                LACore.Instance.RegisterKeybind("AnnounceNearbyNavPoints", Key.U, AnnounceNearbyNavigationPoints);

                // Initialize object pool for markers
                InitializeObjectPool();

                // Generate audio clips on a background thread
                GenerateAudioClips();

                // Generate the "reached point" sound
                reachedPointSound = GenerateReachedPointSound();

                // Start the delayed initialization
                StartCoroutine(DelayedStartup());

                LogMessage("TileTracker initialization completed, waiting for delayed startup");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error during TileTracker initialization: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private IEnumerator DelayedStartup()
        {
            LogMessage("Waiting for game to be fully initialized before dungeon search");
            yield return new WaitForSeconds(startupDelay);

            isStartupComplete = true;

            // Wait for StartOfRound to indicate level is ready
            while (StartOfRound.Instance == null || !StartOfRound.Instance.shipHasLanded)
            {
                LogMessage("Waiting for ship to land before searching for dungeons");
                yield return new WaitForSeconds(1f);
            }

            // Try to initialize dungeon information
            InitializeDungeonData();
        }

        private void Update()
        {
            try
            {
                if (!isStartupComplete || playerTransform == null)
                {
                    return;
                }

                // Update junction announcement cooldown
                if (junctionAnnounceCooldown > 0f)
                {
                    junctionAnnounceCooldown -= Time.deltaTime;
                }

                // Update cached player position
                lock (playerPositionLock)
                {
                    if (playerTransform != null)
                    {
                        cachedPlayerPosition = playerTransform.position;
                    }
                }

                // Only perform tile checks if the player is inside a factory
                bool isInFactory = LethalAccess.Patches.IsInsideFactoryPatch.IsInFactory;

                if (isInFactory)
                {
                    // Periodically check current tile if needed
                    if (currentTile == null || Time.frameCount % 60 == 0)
                    {
                        CheckCurrentTileManually();
                    }

                    // Update markers and check for discovered points
                    if (markersInitialized)
                    {
                        updateFrameCounter++;
                        if (updateFrameCounter >= VISUALIZATION_UPDATE_INTERVAL)
                        {
                            UpdateMarkersVisibility();
                            updateFrameCounter = 0;
                        }

                        CheckForDiscoveredPoints();
                    }
                }

                // Audio pings should still work even when not in factory
                if (markersInitialized && currentMode != NavigationMode.Off &&
                    Time.time - lastAudioPingTime > audioPingInterval && !isPlayingSequentialAudio)
                {
                    PlayAudioForNearbyPoints();
                    lastAudioPingTime = Time.time;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in TileTracker.Update: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void OnDestroy()
        {
            try
            {
                // Clean up pooled objects
                int pooledObjectCount = markerPool.Count;
                while (markerPool.Count > 0)
                {
                    GameObject marker = markerPool.Dequeue();
                    if (marker != null)
                    {
                        Destroy(marker);
                    }
                }

                // Clean up navigation points
                if (navigationPoints != null)
                {
                    foreach (NavigationPoint point in navigationPoints)
                    {
                        if (point.visualMarker != null)
                        {
                            Destroy(point.visualMarker);
                        }

                        foreach (GameObject marker in point.additionalMarkers)
                        {
                            if (marker != null)
                            {
                                Destroy(marker);
                            }
                        }
                    }

                    navigationPoints = new ConcurrentBag<NavigationPoint>();
                }

                // Clear tile navigation points
                lock (tileNavigationLock)
                {
                    tileNavigationPoints.Clear();
                }

                LogMessage($"TileTracker destroyed, {pooledObjectCount} pooled objects and all resources cleaned up");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in TileTracker.OnDestroy: {ex.Message}");
            }
        }

        #endregion

        #region Dungeon Detection and Initialization

        /// <summary>
        /// Smartly initializes dungeon data by checking game state first
        /// </summary>
        private void InitializeDungeonData()
        {
            try
            {
                LogMessage("Attempting to initialize dungeon data");

                // If we're not in a level with dungeons, skip the search
                if (StartOfRound.Instance != null &&
                    StartOfRound.Instance.currentLevel != null &&
                    !StartOfRound.Instance.currentLevel.spawnEnemiesAndScrap)
                {
                    LogMessage("Current level doesn't have a dungeon to search for (spawnEnemiesAndScrap=false)");
                    dungeonInitialized = true;
                    return;
                }

                // Check if we're in the Company Building
                if (IsInCompanyBuilding())
                {
                    LogMessage("Detected Company Building - no dungeon to search for");
                    dungeonInitialized = true;
                    return;
                }

                // Try to find dungeons directly
                FindDungeons();

                // If dungeons were found, we're done
                if (dungeons != null && dungeons.Length > 0)
                {
                    return;
                }

                // If we've exceeded max attempts, give up
                dungeonCheckAttempts++;
                if (dungeonCheckAttempts >= MAX_DUNGEON_CHECK_ATTEMPTS)
                {
                    LogMessage($"Failed to find dungeons after {MAX_DUNGEON_CHECK_ATTEMPTS} attempts - continuing without dungeon-based tracking");
                    dungeonInitialized = true;
                    return;
                }

                // Otherwise, try again after a delay
                StartCoroutine(RetryDungeonInitialization());
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error initializing dungeon data: {ex.Message}\n{ex.StackTrace}");
                dungeonInitialized = true; // Mark as initialized to prevent further attempts
            }
        }

        /// <summary>
        /// Wait a bit then retry initialization
        /// </summary>
        private IEnumerator RetryDungeonInitialization()
        {
            LogMessage($"Will retry dungeon initialization in 2 seconds (attempt {dungeonCheckAttempts}/{MAX_DUNGEON_CHECK_ATTEMPTS})");
            yield return new WaitForSeconds(2f);
            InitializeDungeonData();
        }

        /// <summary>
        /// Checks if the player is in the Company Building
        /// </summary>
        private bool IsInCompanyBuilding()
        {
            // Check if the current level name matches the Company Building
            if (StartOfRound.Instance != null &&
                StartOfRound.Instance.currentLevel != null)
            {
                string planetName = StartOfRound.Instance.currentLevel.PlanetName;
                if (planetName.Contains("Company") || planetName.Contains("Building"))
                {
                    return true;
                }
            }

            // Check if we're in a level without enemies (Company Building has no enemies)
            if (StartOfRound.Instance != null &&
                StartOfRound.Instance.currentLevel != null &&
                (StartOfRound.Instance.currentLevel.Enemies == null ||
                 StartOfRound.Instance.currentLevel.Enemies.Count == 0))
            {
                return true;
            }

            // Check for specific Company Building objects
            GameObject itemCounter = GameObject.Find("ItemCounter");
            GameObject sellDesk = GameObject.Find("DepositItemsDesk");

            return itemCounter != null || sellDesk != null;
        }

        /// <summary>
        /// Directly finds dungeons in the scene without using async/await
        /// </summary>
        private void FindDungeons()
        {
            try
            {
                LogMessage("Searching for dungeons directly");
                List<Dungeon> foundDungeons = new List<Dungeon>();

                // First try to find dungeons via RuntimeDungeon components
                RuntimeDungeon[] runtimeDungeons = FindObjectsOfType<RuntimeDungeon>();
                if (runtimeDungeons != null && runtimeDungeons.Length > 0)
                {
                    LogMessage($"Found {runtimeDungeons.Length} RuntimeDungeon(s) in the scene");

                    foreach (RuntimeDungeon runtimeDungeon in runtimeDungeons)
                    {
                        if (runtimeDungeon != null)
                        {
                            Dungeon dungeon = runtimeDungeon.GetComponentInChildren<Dungeon>();
                            if (dungeon != null && dungeon.AllTiles != null && dungeon.AllTiles.Count > 0)
                            {
                                foundDungeons.Add(dungeon);
                                LogMessage($"Found Dungeon '{dungeon.name}' with {dungeon.AllTiles.Count} tiles");
                            }
                        }
                    }
                }

                // If no dungeons found via RuntimeDungeon, try direct search
                if (foundDungeons.Count == 0)
                {
                    Dungeon[] directDungeons = FindObjectsOfType<Dungeon>();
                    if (directDungeons != null && directDungeons.Length > 0)
                    {
                        LogMessage($"Found {directDungeons.Length} direct Dungeon(s)");
                        foundDungeons.AddRange(directDungeons);
                    }
                }

                // If dungeons were found, set them and process them
                if (foundDungeons.Count > 0)
                {
                    dungeons = foundDungeons.ToArray();
                    dungeonInitialized = true;
                    LogMessage($"Successfully found {dungeons.Length} dungeons");

                    // Process all tiles in the dungeons
                    ProcessAllTiles();
                }
                else
                {
                    LogMessage("No dungeons found during direct search");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error finding dungeons: {ex.Message}\n{ex.StackTrace}");

                // Consider initialization complete even if it failed, to avoid endless retries
                dungeonInitialized = true;
            }
        }

        /// <summary>
        /// Processes all tiles in all dungeons to set up navigation points
        /// </summary>
        private void ProcessAllTiles()
        {
            try
            {
                LogMessage("Processing all tiles in all dungeons");

                if (dungeons == null || dungeons.Length == 0)
                {
                    LogMessage("No dungeons available for tile processing");
                    return;
                }

                int tileCount = 0;
                foreach (Dungeon dungeon in dungeons)
                {
                    if (dungeon == null || dungeon.AllTiles == null)
                    {
                        continue;
                    }

                    foreach (Tile tile in dungeon.AllTiles)
                    {
                        if (tile != null)
                        {
                            ProcessTileForNavigationPoints(tile);
                            tileCount++;
                        }
                    }
                }

                LogMessage($"Processed {tileCount} tiles and created navigation points");
                markersInitialized = true;
                UpdateAllMarkerVisibility();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error processing dungeon tiles: {ex.Message}\n{ex.StackTrace}");
            }
        }

        #endregion

        #region Object Pooling

        /// <summary>
        /// Initialize the object pool for navigation markers
        /// </summary>
        private void InitializeObjectPool()
        {
            try
            {
                Mesh mesh = CreateSphereMesh(markerSize);
                for (int i = 0; i < INITIAL_POOL_SIZE; i++)
                {
                    GameObject obj = new GameObject("PooledPathNode");
                    obj.SetActive(false);

                    MeshFilter meshFilter = obj.AddComponent<MeshFilter>();
                    meshFilter.mesh = mesh;

                    MeshRenderer renderer = obj.AddComponent<MeshRenderer>();
                    renderer.material = new Material(markerMaterial);

                    AudioSource audioSource = obj.AddComponent<AudioSource>();
                    AudioSystemBypass.ConfigureAudioSourceForBypass(audioSource);
                    audioSource.minDistance = 1f;
                    audioSource.maxDistance = audioProximityThreshold;
                    audioSource.playOnAwake = false;

                    markerPool.Enqueue(obj);
                }

                LogMessage($"Initialized object pool with {INITIAL_POOL_SIZE} markers");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error initializing object pool: {ex.Message}");
            }
        }

        /// <summary>
        /// Get a marker from the object pool (or create a new one if needed)
        /// </summary>
        private GameObject GetMarkerFromPool(NavigationPoint.PointType type)
        {
            if (markerPool.Count > 0)
            {
                GameObject marker = markerPool.Dequeue();
                marker.SetActive(true);
                return marker;
            }

            // If pool is empty, create a new marker
            GameObject newMarker = new GameObject($"NavMarker_{type}");
            MeshFilter meshFilter = newMarker.AddComponent<MeshFilter>();
            meshFilter.mesh = CreateSphereMesh(markerSize);

            MeshRenderer renderer = newMarker.AddComponent<MeshRenderer>();
            renderer.material = new Material(markerMaterial);

            if (pointColors.TryGetValue(type, out Color color))
            {
                renderer.material.color = new Color(color.r, color.g, color.b, 0.8f);
            }

            AudioSource audioSource = newMarker.AddComponent<AudioSource>();
            AudioSystemBypass.ConfigureAudioSourceForBypass(audioSource);
            audioSource.minDistance = 1f;
            audioSource.maxDistance = audioProximityThreshold;
            audioSource.playOnAwake = false;

            return newMarker;
        }

        /// <summary>
        /// Return a marker to the object pool
        /// </summary>
        private void ReturnNodeToPool(GameObject nodeObj)
        {
            if (nodeObj == null)
                return;

            nodeObj.SetActive(false);

            // Only add to pool if it's not already too large
            if (markerPool.Count < MAX_POOL_SIZE)
            {
                markerPool.Enqueue(nodeObj);
            }
            else
            {
                Destroy(nodeObj);
            }
        }

        /// <summary>
        /// Create a sphere mesh for navigation markers
        /// </summary>
        private Mesh CreateSphereMesh(float radius)
        {
            Mesh mesh = new Mesh();
            GameObject tempSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            mesh.name = "NodeSphere";

            mesh.vertices = tempSphere.GetComponent<MeshFilter>().sharedMesh.vertices;
            mesh.triangles = tempSphere.GetComponent<MeshFilter>().sharedMesh.triangles;
            mesh.normals = tempSphere.GetComponent<MeshFilter>().sharedMesh.normals;
            mesh.uv = tempSphere.GetComponent<MeshFilter>().sharedMesh.uv;

            Vector3[] vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] *= radius;
            }
            mesh.vertices = vertices;

            Destroy(tempSphere);
            return mesh;
        }

        #endregion

        #region Navigation Points Management

        /// <summary>
        /// Process a tile to find and create navigation points
        /// </summary>
        private void ProcessTileForNavigationPoints(Tile tile)
        {
            try
            {
                string tileId = tile.GetInstanceID().ToString();

                lock (tileNavigationLock)
                {
                    if (tileNavigationPoints.ContainsKey(tileId))
                    {
                        return;
                    }
                    tileNavigationPoints[tileId] = new List<NavigationPoint>();
                }

                List<NavigationPoint> pointsInTile = new List<NavigationPoint>();

                // Look for doorways (passages between rooms)
                if (tile.UsedDoorways.Count == 2)
                {
                    foreach (Doorway doorway in tile.UsedDoorways)
                    {
                        if (doorway != null)
                        {
                            Vector3 position = doorway.transform.position + new Vector3(0f, 0.1f, 0f);
                            NavigationPoint navPoint = new NavigationPoint(position, NavigationPoint.PointType.DoorWay, tile, "Doorway");
                            pointsInTile.Add(navPoint);
                            navigationPoints.Add(navPoint);
                        }
                    }
                }

                // Look for unused doorways (potential hallway openings)
                foreach (Doorway unusedDoorway in tile.UnusedDoorways)
                {
                    if (unusedDoorway != null)
                    {
                        Vector3 position = unusedDoorway.transform.position + new Vector3(0f, 0.1f, 0f);
                        NavigationPoint navPoint = new NavigationPoint(position, NavigationPoint.PointType.HallwayOpening, tile, "Hallway Opening");
                        pointsInTile.Add(navPoint);
                        navigationPoints.Add(navPoint);
                    }
                }

                // Look for junctions (tiles with more than 2 used doorways)
                if (tile.UsedDoorways.Count > 2)
                {
                    Vector3 center = tile.Bounds.center + new Vector3(0f, 0.2f, 0f);
                    int connectionCount = tile.UsedDoorways.Count;
                    NavigationPoint junction = new NavigationPoint(center, NavigationPoint.PointType.Junction, tile, $"Junction ({connectionCount} connections)", connectionCount);
                    pointsInTile.Add(junction);
                    navigationPoints.Add(junction);
                }
                // Look for dead ends (tiles with only 1 used doorway)
                else if (tile.UsedDoorways.Count == 1)
                {
                    Vector3 center = tile.Bounds.center + new Vector3(0f, 0.2f, 0f);
                    NavigationPoint deadEnd = new NavigationPoint(center, NavigationPoint.PointType.DeadEnd, tile, "Dead End");
                    pointsInTile.Add(deadEnd);
                    navigationPoints.Add(deadEnd);
                }

                // Check for any doors in the tile that might not be associated with doorways
                CheckForMissedDoors(tile, pointsInTile);

                lock (tileNavigationLock)
                {
                    tileNavigationPoints[tileId] = pointsInTile;
                }

                // Create visual markers for all navigation points in this tile
                foreach (NavigationPoint point in pointsInTile)
                {
                    if (point.type == NavigationPoint.PointType.HallwayOpening)
                    {
                        CreateCompoundVisualMarker(point);
                    }
                    else
                    {
                        CreateVisualMarker(point);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error processing navigation points for tile: {ex.Message}");
            }
        }

        /// <summary>
        /// Check for doors in the tile that might not be associated with doorways
        /// </summary>
        private void CheckForMissedDoors(Tile tile, List<NavigationPoint> pointsInTile)
        {
            try
            {
                Door[] doors = tile.GetComponentsInChildren<Door>(true);
                if (doors == null || doors.Length == 0)
                {
                    return;
                }

                foreach (Door door in doors)
                {
                    if (door == null)
                        continue;

                    Vector3 doorPos = door.transform.position + new Vector3(0f, 0.1f, 0f);
                    bool doorAlreadyAdded = pointsInTile.Any(p => Vector3.Distance(p.position, doorPos) < 2f);

                    if (!doorAlreadyAdded)
                    {
                        NavigationPoint navPoint = new NavigationPoint(doorPos, NavigationPoint.PointType.DoorWay, tile, "Door");
                        pointsInTile.Add(navPoint);
                        navigationPoints.Add(navPoint);
                        LogMessage($"Added missed door in tile: {tile.name}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error checking for missed doors: {ex.Message}");
            }
        }

        /// <summary>
        /// Create a visual marker for a navigation point
        /// </summary>
        private void CreateVisualMarker(NavigationPoint point)
        {
            try
            {
                GameObject markerObj = GetMarkerFromPool(point.type);
                if (markerObj == null)
                {
                    Debug.LogError("Failed to get marker from pool");
                    return;
                }

                markerObj.name = $"NavMarker_{point.type}";
                markerObj.transform.position = point.position;
                markerObj.transform.localScale = new Vector3(markerSize, markerSize, markerSize);

                Renderer renderer = markerObj.GetComponent<Renderer>();
                if (renderer != null && pointColors.TryGetValue(point.type, out Color color))
                {
                    renderer.material.color = new Color(color.r, color.g, color.b, 0.8f);
                }

                AudioSource audioSource = markerObj.GetComponent<AudioSource>();
                if (audioSource != null)
                {
                    float volume;
                    float volumeMultiplier = pointTypeVolumes.TryGetValue(point.type, out volume) ? volume : 0.8f;

                    AudioSystemBypass.ConfigureAudioSourceForBypass(audioSource, volumeMultiplier * masterVolume);

                    if (audioClips.TryGetValue(point.type, out AudioClip clip))
                    {
                        audioSource.clip = clip;
                        point.audioClip = clip;
                    }
                }

                point.audioSource = audioSource;
                markerObj.SetActive(false);
                point.visualMarker = markerObj;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error creating visual marker: {ex.Message}");
            }
        }

        /// <summary>
        /// Create a compound visual marker (multiple objects) for a hallway opening
        /// </summary>
        private void CreateCompoundVisualMarker(NavigationPoint point)
        {
            try
            {
                // Red marker
                GameObject redMarker = GetMarkerFromPool(NavigationPoint.PointType.HallwayOpening);
                redMarker.name = "NavMarker_HallwayOpening_Red";
                redMarker.transform.position = point.position - new Vector3(0.2f, 0f, 0f);
                redMarker.transform.localScale = new Vector3(markerSize * 0.8f, markerSize * 0.8f, markerSize * 0.8f);

                Renderer redRenderer = redMarker.GetComponent<Renderer>();
                if (redRenderer != null)
                {
                    redRenderer.material.color = new Color(1f, 0.2f, 0.2f, 0.8f);
                }

                // Yellow marker
                GameObject yellowMarker = GetMarkerFromPool(NavigationPoint.PointType.HallwayOpening);
                yellowMarker.name = "NavMarker_HallwayOpening_Yellow";
                yellowMarker.transform.position = point.position + new Vector3(0.2f, 0f, 0f);
                yellowMarker.transform.localScale = new Vector3(markerSize * 0.8f, markerSize * 0.8f, markerSize * 0.8f);

                Renderer yellowRenderer = yellowMarker.GetComponent<Renderer>();
                if (yellowRenderer != null)
                {
                    yellowRenderer.material.color = new Color(1f, 0.92f, 0.016f, 0.8f);
                }

                // Green marker (main)
                GameObject greenMarker = GetMarkerFromPool(NavigationPoint.PointType.HallwayOpening);
                greenMarker.name = "NavMarker_HallwayOpening_Green";
                greenMarker.transform.position = point.position + new Vector3(0f, 0.25f, 0f);
                greenMarker.transform.localScale = new Vector3(markerSize, markerSize, markerSize);

                Renderer greenRenderer = greenMarker.GetComponent<Renderer>();
                if (greenRenderer != null)
                {
                    greenRenderer.material.color = new Color(0.2f, 1f, 0.2f, 0.8f);
                }

                AudioSource audioSource = greenMarker.GetComponent<AudioSource>();
                if (audioSource != null)
                {
                    float volume;
                    float volumeMultiplier = pointTypeVolumes.TryGetValue(point.type, out volume) ? volume : 0.8f;

                    AudioSystemBypass.ConfigureAudioSourceForBypass(audioSource, volumeMultiplier * masterVolume);

                    if (audioClips.TryGetValue(point.type, out AudioClip clip))
                    {
                        audioSource.clip = clip;
                        point.audioClip = clip;
                    }
                }

                point.audioSource = audioSource;

                redMarker.SetActive(false);
                yellowMarker.SetActive(false);
                greenMarker.SetActive(false);

                point.visualMarker = greenMarker;
                point.additionalMarkers.Add(redMarker);
                point.additionalMarkers.Add(yellowMarker);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error creating compound visual marker: {ex.Message}");
            }
        }

        #endregion

        #region Audio Generation and Playback

        /// <summary>
        /// Generate all audio clips for navigation points
        /// </summary>
        private void GenerateAudioClips()
        {
            try
            {
                // Create an audio clip for each navigation point type
                audioClips[NavigationPoint.PointType.DoorWay] = CreateAudioClipFromData("DoorwayTone", GenerateDoorwayToneData());
                audioClips[NavigationPoint.PointType.HallwayOpening] = CreateAudioClipFromData("HallwayOpeningTone", GenerateHallwayOpeningToneData());
                audioClips[NavigationPoint.PointType.Junction] = CreateAudioClipFromData("JunctionTone", GenerateJunctionToneData());
                audioClips[NavigationPoint.PointType.DeadEnd] = CreateAudioClipFromData("DeadEndTone", GenerateDeadEndToneData());

                LogMessage("Audio tones generated successfully");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error generating audio clips: {ex.Message}");
            }
        }

        /// <summary>
        /// Generate tone data for doorway navigation points
        /// </summary>
        private float[] GenerateDoorwayToneData()
        {
            int sampleRate = 44100;
            float frequency = 880f;
            float duration = 0.15f;
            int sampleCount = Mathf.RoundToInt(sampleRate * duration);
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float normalizedTime = i / (float)sampleCount;
                float envelope = Mathf.Sin(normalizedTime * Mathf.PI);
                samples[i] = Mathf.Sin(Mathf.PI * 2f * frequency * i / sampleRate) * envelope * 0.7f;
            }

            return samples;
        }

        /// <summary>
        /// Generate tone data for hallway opening navigation points
        /// </summary>
        private float[] GenerateHallwayOpeningToneData()
        {
            int sampleRate = 44100;
            float frequency = 660f;
            float duration = 0.15f;
            int sampleCount = Mathf.RoundToInt(sampleRate * duration);
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float normalizedTime = i / (float)sampleCount;
                float envelope = Mathf.Sin(normalizedTime * Mathf.PI);
                samples[i] = Mathf.Sin(Mathf.PI * 2f * frequency * i / sampleRate) * envelope * 0.7f;
            }

            return samples;
        }

        /// <summary>
        /// Generate tone data for junction navigation points (double tone)
        /// </summary>
        private float[] GenerateJunctionToneData()
        {
            int sampleRate = 44100;
            float baseFreq = 440f;
            float toneDuration = 0.1f;
            float pauseDuration = 0.05f;

            float totalDuration = toneDuration * 2 + pauseDuration;
            int sampleCount = Mathf.RoundToInt(sampleRate * totalDuration);
            float[] samples = new float[sampleCount];

            int index = 0;

            // First tone
            for (int i = 0; i < Mathf.RoundToInt(sampleRate * toneDuration) && index < sampleCount; i++)
            {
                float normalizedTime = i / (sampleRate * toneDuration);
                float envelope = Mathf.Sin(normalizedTime * Mathf.PI);
                samples[index++] = Mathf.Sin(Mathf.PI * 2f * baseFreq * i / sampleRate) * envelope * 0.7f;
            }

            // Short pause
            index = Mathf.Min(index + Mathf.RoundToInt(sampleRate * pauseDuration), sampleCount);

            // Second tone (higher pitch)
            for (int i = 0; i < Mathf.RoundToInt(sampleRate * toneDuration) && index < sampleCount; i++)
            {
                float normalizedTime = i / (sampleRate * toneDuration);
                float envelope = Mathf.Sin(normalizedTime * Mathf.PI);
                samples[index++] = Mathf.Sin(Mathf.PI * 2f * (baseFreq * 1.5f) * i / sampleRate) * envelope * 0.7f;
            }

            return samples;
        }

        /// <summary>
        /// Generate tone data for dead end navigation points (descending tone)
        /// </summary>
        private float[] GenerateDeadEndToneData()
        {
            int sampleRate = 44100;
            float highFreq = 440f;
            float lowFreq = 220f;
            float duration = 0.3f;
            int sampleCount = Mathf.RoundToInt(sampleRate * duration);
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float normalizedTime = i / (float)sampleCount;
                float currentFreq = Mathf.Lerp(highFreq, lowFreq, normalizedTime);
                samples[i] = Mathf.Sin(Mathf.PI * 2f * currentFreq * i / sampleRate) * 0.5f;
            }

            return samples;
        }

        /// <summary>
        /// Generate a "reached point" sound (short click)
        /// </summary>
        private AudioClip GenerateReachedPointSound()
        {
            int sampleRate = 44100;
            float frequency = 2000f;
            float duration = 0.05f;
            int sampleCount = Mathf.RoundToInt(sampleRate * duration);

            AudioClip clip = AudioClip.Create("ReachedPointSound", sampleCount, 1, sampleRate, false);
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float normalizedTime = i / (float)sampleCount;
                float envelope = Mathf.Exp(-normalizedTime * 10f);
                samples[i] = Mathf.Sin(Mathf.PI * 2f * frequency * i / sampleRate) * envelope * 0.8f;
            }

            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// Create an AudioClip from sample data
        /// </summary>
        private AudioClip CreateAudioClipFromData(string name, float[] samples)
        {
            int sampleRate = 44100;
            AudioClip clip = AudioClip.Create(name, samples.Length, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// Play a navigation point's audio
        /// </summary>
        private void PlayPointAudio(NavigationPoint point, Vector3 playerPos, Vector3 playerForward)
        {
            if (point.audioSource == null || point.audioClip == null)
                return;

            if (IsPointInAudioProximity(playerPos, point.position))
            {
                AudioClip clip = point.audioClip;
                bool isBehind = IsPointBehindPlayer(playerPos, playerForward, point.position);
                float distance = Vector3.Distance(playerPos, point.position);
                float minDistance = 1f;
                float maxDistance = audioProximityThreshold;

                float baseVolume;
                float volumeMultiplier = pointTypeVolumes.TryGetValue(point.type, out baseVolume) ? baseVolume : 0.8f;
                volumeMultiplier *= masterVolume;

                float finalVolume = AudioSystemBypass.CalculateVolumeBasedOnDistance(distance, minDistance, maxDistance, volumeMultiplier);

                if (isBehind)
                {
                    point.audioSource.pitch = 0.5f;  // Lower pitch for points behind the player
                }
                else
                {
                    point.audioSource.pitch = 1f;
                }

                point.audioSource.volume = finalVolume;
                point.audioSource.PlayOneShot(clip);
            }
        }

        /// <summary>
        /// Play a "reached point" sound
        /// </summary>
        private void PlayReachedPointSound(Vector3 playerPos)
        {
            if (reachedPointSound == null)
                return;

            GameObject tempAudio = new GameObject("TempClickAudio");
            tempAudio.transform.position = playerPos;

            AudioSource audioSource = tempAudio.AddComponent<AudioSource>();
            AudioSystemBypass.ConfigureAudioSourceForBypass(audioSource, 0.8f * masterVolume);
            audioSource.clip = reachedPointSound;
            audioSource.spatialBlend = 0f; // 2D sound
            audioSource.Play();

            Destroy(tempAudio, reachedPointSound.length + 0.1f);
        }

        /// <summary>
        /// Play audio for all nearby navigation points in sequence
        /// </summary>
        private void PlayAudioForNearbyPoints()
        {
            if (currentMode == NavigationMode.Off || !markersInitialized || playerTransform == null || isPlayingSequentialAudio)
            {
                return;
            }

            Vector3 playerPos;
            Vector3 forward;

            lock (playerPositionLock)
            {
                if (playerTransform == null)
                {
                    return;
                }
                playerPos = playerTransform.position;
                forward = playerTransform.forward;
            }

            List<NavigationPoint> pointsToPlay = new List<NavigationPoint>();

            // Find points to play based on current mode
            foreach (NavigationPoint point in navigationPoints)
            {
                bool shouldConsider = false;
                if (currentMode == NavigationMode.Explore && !point.isDiscovered)
                {
                    shouldConsider = true;
                }
                else if (currentMode == NavigationMode.Retrace && point.isDiscovered)
                {
                    shouldConsider = true;
                }

                if (shouldConsider && IsPointInAudioProximity(playerPos, point.position))
                {
                    pointsToPlay.Add(point);
                }
            }

            // Sort by distance (closest first) - implemented manually instead of using LINQ
            pointsToPlay.Sort((a, b) =>
                Vector3.Distance(playerPos, a.position).CompareTo(Vector3.Distance(playerPos, b.position))
            );

            if (pointsToPlay.Count > 0)
            {
                StartCoroutine(PlaySequentialAudio(pointsToPlay));
            }
        }

        /// <summary>
        /// Play audio for a sequence of navigation points with delays between them
        /// </summary>
        private IEnumerator PlaySequentialAudio(List<NavigationPoint> points)
        {
            isPlayingSequentialAudio = true;

            Vector3 playerPos = Vector3.zero;
            Vector3 playerFwd = Vector3.forward;

            if (playerTransform != null)
            {
                playerPos = playerTransform.position;
                playerFwd = playerTransform.forward;
            }

            foreach (NavigationPoint point in points)
            {
                PlayPointAudio(point, playerPos, playerFwd);
                yield return new WaitForSeconds(audioDelayBetweenPoints);
            }

            isPlayingSequentialAudio = false;
        }

        #endregion

        #region User Interaction Methods

        /// <summary>
        /// Cycle through navigation modes (Explore -> Retrace -> Off -> Explore)
        /// </summary>
        private void CycleNavigationMode()
        {
            switch (currentMode)
            {
                case NavigationMode.Explore:
                    currentMode = NavigationMode.Retrace;
                    Utilities.SpeakText("Navigation mode: Retrace. Showing only visited locations.");
                    break;
                case NavigationMode.Retrace:
                    currentMode = NavigationMode.Off;
                    Utilities.SpeakText("Navigation mode: Off. Navigation disabled.");
                    break;
                case NavigationMode.Off:
                    currentMode = NavigationMode.Explore;
                    Utilities.SpeakText("Navigation mode: Explore. Showing new locations.");
                    break;
            }
            UpdateAllMarkerVisibility();
        }

        /// <summary>
        /// Toggle visibility of navigation markers
        /// </summary>
        private void ToggleVisuals()
        {
            showVisuals = !showVisuals;
            Utilities.SpeakText(showVisuals ? "Navigation markers visible." : "Navigation markers hidden.");
            UpdateAllMarkerVisibility();
        }

        /// <summary>
        /// Announce all nearby navigation points
        /// </summary>
        public void AnnounceNearbyNavigationPoints()
        {
            if (playerTransform == null || !markersInitialized || currentMode == NavigationMode.Off)
            {
                return;
            }

            Vector3 playerPos = playerTransform.position;
            Vector3 forward = playerTransform.forward;
            Vector3 right = playerTransform.right;

            List<NavigationPoint> nearbyPoints = new List<NavigationPoint>();

            // Convert ConcurrentBag to List before using LINQ
            List<NavigationPoint> allPoints = new List<NavigationPoint>();
            foreach (NavigationPoint point in navigationPoints)
            {
                allPoints.Add(point);
            }

            if (currentMode == NavigationMode.Explore)
            {
                nearbyPoints = allPoints
                    .Where(p => !p.isDiscovered && IsPointInAudioProximity(playerPos, p.position))
                    .OrderBy(p => Vector3.Distance(playerPos, p.position))
                    .Take(3)
                    .ToList();
            }
            else if (currentMode == NavigationMode.Retrace)
            {
                nearbyPoints = allPoints
                    .Where(p => p.isDiscovered && IsPointInAudioProximity(playerPos, p.position))
                    .OrderBy(p => Vector3.Distance(playerPos, p.position))
                    .Take(3)
                    .ToList();
            }

            if (nearbyPoints.Count > 0)
            {
                string announcement = "Nearby navigation points: ";

                for (int i = 0; i < nearbyPoints.Count; i++)
                {
                    NavigationPoint point = nearbyPoints[i];
                    float distance = Vector3.Distance(playerPos, point.position);
                    string direction = GetRelativeDirection(playerPos, forward, right, point.position);
                    string connectionInfo = point.type == NavigationPoint.PointType.Junction ? $"({point.connectionCount} connections)" : "";

                    announcement += $"{point.type} {connectionInfo} {direction} ({distance:F1} meters), ";
                }

                Utilities.SpeakText(announcement.TrimEnd(',', ' '));
            }
            else
            {
                Utilities.SpeakText("No navigation points nearby.");
            }
        }

        /// <summary>
        /// Reset all discovered points
        /// </summary>
        public void ResetAllDiscoveredPoints()
        {
            foreach (NavigationPoint point in navigationPoints)
            {
                point.isDiscovered = false;
            }

            UpdateAllMarkerVisibility();
        }

        /// <summary>
        /// Reset when leaving the factory
        /// </summary>
        public void ResetWhenLeavingFactory()
        {
            currentTile = null;
            lastAnnouncedJunctionTile = null;

            // Hide all markers
            if (markersInitialized)
            {
                foreach (NavigationPoint point in navigationPoints)
                {
                    if (point.visualMarker != null)
                    {
                        point.visualMarker.SetActive(false);
                        foreach (GameObject marker in point.additionalMarkers)
                        {
                            if (marker != null)
                            {
                                marker.SetActive(false);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Called when player enters a tile
        /// </summary>
        public void OnPlayerEnteredTile(Tile tile)
        {
            // First check if player is in factory before processing
            if (!LethalAccess.Patches.IsInsideFactoryPatch.IsInFactory)
            {
                return;
            }

            if (tile == currentTile)
            {
                return;
            }

            string previousTileName = currentTile != null ? currentTile.name : "None";
            currentTile = tile;
            string newTileName = tile.name;
            string tileType = GetTileType(tile);

            LogMessage($"Player entered tile: {newTileName} (Previous: {previousTileName}), Type: {tileType}");

            if (!discoveredTileNames.Contains(newTileName))
            {
                discoveredTileNames.Add(newTileName);
                LogDetailedTileInfo(tile);

                if (markersInitialized)
                {
                    string tileId = tile.GetInstanceID().ToString();
                    bool needsProcessing = false;

                    lock (tileNavigationLock)
                    {
                        needsProcessing = !tileNavigationPoints.ContainsKey(tileId);
                    }

                    if (needsProcessing)
                    {
                        ProcessTileForNavigationPoints(tile);
                    }
                }
            }

            CheckAndAnnounceJunction(tile);
        }

        /// <summary>
        /// Called when player exits a tile
        /// </summary>
        public void OnPlayerExitedTile(Tile tile)
        {
            // Only process if player is in factory
            if (!LethalAccess.Patches.IsInsideFactoryPatch.IsInFactory)
            {
                return;
            }

            LogMessage($"Player exited tile: {tile.name}");

            if (tile == lastAnnouncedJunctionTile)
            {
                lastAnnouncedJunctionTile = null;
            }
        }

        #endregion

        #region Visibility and Proximity Methods

        /// <summary>
        /// Check if the player is in proximity to a navigation point
        /// </summary>
        private bool IsPointInProximity(Vector3 playerPos, Vector3 point)
        {
            return Vector3.Distance(playerPos, point) <= proximityThreshold;
        }

        /// <summary>
        /// Check if a navigation point is in audio proximity and line of sight
        /// </summary>
        private bool IsPointInAudioProximity(Vector3 playerPos, Vector3 point)
        {
            float distance = Vector3.Distance(playerPos, point);
            if (distance > audioProximityThreshold)
            {
                return false;
            }

            if (checkLineOfSight)
            {
                Vector3 eyePos = playerPos + Vector3.up * 0.5f;
                Vector3 pointEyeLevel = point + Vector3.up * 0.5f;

                return !Physics.Linecast(eyePos, pointEyeLevel, wallLayerMask);
            }

            return true;
        }

        /// <summary>
        /// Check if player is close enough to discover a navigation point
        /// </summary>
        private bool IsPointInDiscoveryProximity(Vector3 playerPos, Vector3 point, NavigationPoint.PointType pointType)
        {
            float threshold;
            float defaultThreshold = 1f;
            float discoveryThreshold = discoveryThresholds.TryGetValue(pointType, out threshold) ? threshold : defaultThreshold;

            return Vector3.Distance(playerPos, point) <= discoveryThreshold;
        }

        /// <summary>
        /// Check if a point is behind the player
        /// </summary>
        private bool IsPointBehindPlayer(Vector3 playerPos, Vector3 playerForward, Vector3 point)
        {
            Vector3 directionToPoint = point - playerPos;
            float dotProduct = Vector3.Dot(directionToPoint.normalized, playerForward);
            return dotProduct < 0f;
        }

        /// <summary>
        /// Get the relative direction to a point from the player (ahead, behind, left, right, etc.)
        /// </summary>
        private string GetRelativeDirection(Vector3 playerPos, Vector3 playerForward, Vector3 playerRight, Vector3 point)
        {
            Vector3 directionToPoint = point - playerPos;
            directionToPoint.y = 0f;

            Vector3 forwardFlat = playerForward;
            forwardFlat.y = 0f;
            forwardFlat.Normalize();

            Vector3 rightFlat = playerRight;
            rightFlat.y = 0f;
            rightFlat.Normalize();

            float dotForward = Vector3.Dot(directionToPoint.normalized, forwardFlat);
            float dotRight = Vector3.Dot(directionToPoint.normalized, rightFlat);

            string heightDesc = "";
            if (point.y > playerPos.y + 2f)
            {
                heightDesc = " above";
            }
            else if (point.y < playerPos.y - 2f)
            {
                heightDesc = " below";
            }

            if (dotForward > 0.7f)
            {
                return "ahead" + heightDesc;
            }

            if (dotForward < -0.7f)
            {
                return "behind" + heightDesc;
            }

            if (dotRight > 0.7f)
            {
                return "to the right" + heightDesc;
            }

            if (dotRight < -0.7f)
            {
                return "to the left" + heightDesc;
            }

            if (dotForward > 0.3f && dotRight > 0.3f)
            {
                return "ahead and to the right" + heightDesc;
            }

            if (dotForward > 0.3f && dotRight < -0.3f)
            {
                return "ahead and to the left" + heightDesc;
            }

            if (dotForward < -0.3f && dotRight > 0.3f)
            {
                return "behind and to the right" + heightDesc;
            }

            if (dotForward < -0.3f && dotRight < -0.3f)
            {
                return "behind and to the left" + heightDesc;
            }

            return "nearby" + heightDesc;
        }

        /// <summary>
        /// Check if the junction contains any navigation points
        /// </summary>
        private void CheckAndAnnounceJunction(Tile tile)
        {
            if (currentMode == NavigationMode.Off || junctionAnnounceCooldown > 0f || tile == lastAnnouncedJunctionTile)
            {
                return;
            }

            string tileId = tile.GetInstanceID().ToString();

            lock (tileNavigationLock)
            {
                if (tileNavigationPoints.TryGetValue(tileId, out List<NavigationPoint> tilePoints))
                {
                    NavigationPoint junctionPoint = tilePoints.FirstOrDefault(p => p.type == NavigationPoint.PointType.Junction);

                    if (junctionPoint != null)
                    {
                        string connectionText = junctionPoint.connectionCount == 1 ? "connection" : "connections";
                        Utilities.SpeakText($"Junction with {junctionPoint.connectionCount} {connectionText}");
                        junctionAnnounceCooldown = junctionAnnounceInterval;
                        lastAnnouncedJunctionTile = tile;
                    }
                }
            }
        }

        /// <summary>
        /// Update visibility of navigation markers
        /// </summary>
        private void UpdateMarkersVisibility()
        {
            if (!markersInitialized)
            {
                return;
            }

            Vector3 position;
            lock (playerPositionLock)
            {
                if (playerTransform == null)
                {
                    return;
                }
                position = playerTransform.position;
            }

            // If navigation is off, hide all markers
            if (currentMode == NavigationMode.Off)
            {
                foreach (NavigationPoint point in navigationPoints)
                {
                    if (point.visualMarker != null)
                    {
                        point.visualMarker.SetActive(false);
                        foreach (GameObject marker in point.additionalMarkers)
                        {
                            if (marker != null)
                            {
                                marker.SetActive(false);
                            }
                        }
                    }
                }
                return;
            }

            // Process a batch of points each frame for better performance
            int maxPointsPerUpdate = 20;
            List<NavigationPoint> pointsToUpdate = new List<NavigationPoint>();

            // First, identify which points need updating
            foreach (NavigationPoint point in navigationPoints)
            {
                bool isInProximity = IsPointInProximity(position, point.position);
                bool shouldBeVisible = false;

                if (currentMode == NavigationMode.Explore && !point.isDiscovered && isInProximity)
                {
                    shouldBeVisible = true;
                }
                else if (currentMode == NavigationMode.Retrace && point.isDiscovered && isInProximity)
                {
                    shouldBeVisible = true;
                }

                shouldBeVisible = shouldBeVisible && showVisuals;

                bool currentlyVisible = point.visualMarker != null && point.visualMarker.activeSelf;

                if (currentlyVisible != shouldBeVisible)
                {
                    pointsToUpdate.Add(point);
                    if (pointsToUpdate.Count >= maxPointsPerUpdate)
                    {
                        break;
                    }
                }
            }

            // Then update them
            foreach (NavigationPoint point in pointsToUpdate)
            {
                bool isInProximity = IsPointInProximity(position, point.position);
                bool shouldBeVisible = false;

                if (currentMode == NavigationMode.Explore && !point.isDiscovered && isInProximity)
                {
                    shouldBeVisible = true;
                }
                else if (currentMode == NavigationMode.Retrace && point.isDiscovered && isInProximity)
                {
                    shouldBeVisible = true;
                }

                shouldBeVisible = shouldBeVisible && showVisuals;

                if (point.visualMarker != null && point.visualMarker.activeSelf != shouldBeVisible)
                {
                    point.visualMarker.SetActive(shouldBeVisible);
                    foreach (GameObject marker in point.additionalMarkers)
                    {
                        if (marker != null)
                        {
                            marker.SetActive(shouldBeVisible);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Update visibility of all navigation markers at once
        /// </summary>
        private void UpdateAllMarkerVisibility()
        {
            if (!markersInitialized)
            {
                return;
            }

            Vector3 position;
            lock (playerPositionLock)
            {
                if (playerTransform == null)
                {
                    return;
                }
                position = playerTransform.position;
            }

            // If navigation is off, hide all markers
            if (currentMode == NavigationMode.Off)
            {
                foreach (NavigationPoint point in navigationPoints)
                {
                    if (point.visualMarker != null)
                    {
                        point.visualMarker.SetActive(false);
                        foreach (GameObject marker in point.additionalMarkers)
                        {
                            if (marker != null)
                            {
                                marker.SetActive(false);
                            }
                        }
                    }
                }
                return;
            }

            // Update all markers at once
            foreach (NavigationPoint point in navigationPoints)
            {
                if (point.visualMarker == null)
                    continue;

                bool isInProximity = IsPointInProximity(position, point.position);
                bool shouldBeVisible = false;

                if (currentMode == NavigationMode.Explore && !point.isDiscovered && isInProximity)
                {
                    shouldBeVisible = true;
                }
                else if (currentMode == NavigationMode.Retrace && point.isDiscovered && isInProximity)
                {
                    shouldBeVisible = true;
                }

                shouldBeVisible = shouldBeVisible && showVisuals;

                point.visualMarker.SetActive(shouldBeVisible);
                foreach (GameObject marker in point.additionalMarkers)
                {
                    if (marker != null)
                    {
                        marker.SetActive(shouldBeVisible);
                    }
                }
            }
        }

        /// <summary>
        /// Check for and mark discovered navigation points
        /// </summary>
        private void CheckForDiscoveredPoints()
        {
            if (!markersInitialized)
            {
                return;
            }

            Vector3 position;
            lock (playerPositionLock)
            {
                if (playerTransform == null)
                {
                    return;
                }
                position = playerTransform.position;
            }

            List<NavigationPoint> newlyDiscoveredPoints = new List<NavigationPoint>();

            // Find points that should be discovered
            foreach (NavigationPoint point in navigationPoints)
            {
                if (!point.isDiscovered && IsPointInDiscoveryProximity(position, point.position, point.type))
                {
                    newlyDiscoveredPoints.Add(point);
                }
            }

            if (newlyDiscoveredPoints.Count <= 0)
            {
                return;
            }

            // Mark points as discovered and play sound
            foreach (NavigationPoint point in newlyDiscoveredPoints)
            {
                point.isDiscovered = true;
            }

            PlayReachedPointSound(position);
            UpdateAllMarkerVisibility();
        }

        #endregion

        #region Tile Tracking and Information

        /// <summary>
        /// Manually check which tile the player is in
        /// </summary>
        private void CheckCurrentTileManually()
        {
            try
            {
                // Skip this method if not in factory, or if dungeons aren't initialized
                if (!LethalAccess.Patches.IsInsideFactoryPatch.IsInFactory ||
                    !dungeonInitialized ||
                    dungeons == null ||
                    dungeons.Length == 0 ||
                    playerTransform == null)
                {
                    return;
                }

                Vector3 position = playerTransform.position;
                Tile closestTile = null;
                float closestDistance = float.MaxValue;

                // Check each dungeon
                foreach (Dungeon dungeon in dungeons)
                {
                    if (dungeon == null)
                    {
                        continue;
                    }

                    // Check if player is within dungeon bounds
                    Bounds dungeonBounds = dungeon.Bounds;
                    if (!dungeonBounds.Contains(position))
                    {
                        continue;
                    }

                    if (dungeon.AllTiles == null)
                    {
                        LogMessage($"Dungeon {dungeon.name} has null AllTiles collection");
                        continue;
                    }

                    // Check each tile in the dungeon
                    foreach (Tile tile in dungeon.AllTiles)
                    {
                        if (tile == null)
                        {
                            continue;
                        }

                        try
                        {
                            Bounds tileBounds = tile.Bounds;

                            // If player is directly inside this tile, we're done
                            if (tileBounds.Contains(position))
                            {
                                closestTile = tile;
                                closestDistance = 0f;
                                break;
                            }

                            // Otherwise, remember the closest tile
                            float distanceToTile = Vector3.Distance(position, tileBounds.center);
                            if (distanceToTile < closestDistance)
                            {
                                closestDistance = distanceToTile;
                                closestTile = tile;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"Error checking tile: {ex.Message}");
                        }
                    }

                    // If we found a tile that contains the player, no need to check other dungeons
                    if (closestDistance == 0f)
                    {
                        break;
                    }
                }

                // If we found a new tile, update current tile
                if (closestTile != null && closestTile != currentTile)
                {
                    OnPlayerEnteredTile(closestTile);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in CheckCurrentTileManually: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Get the type of a tile (main path, branch path, etc.)
        /// </summary>
        private string GetTileType(Tile tile)
        {
            if (tile == null)
            {
                return "Unknown";
            }

            if (dungeons == null)
            {
                return "Unknown (No Dungeons)";
            }

            foreach (Dungeon dungeon in dungeons)
            {
                try
                {
                    if (dungeon == null)
                        continue;

                    if (dungeon.MainPathTiles != null && dungeon.MainPathTiles.Contains(tile))
                    {
                        return "Main Path";
                    }

                    if (dungeon.BranchPathTiles != null && dungeon.BranchPathTiles.Contains(tile))
                    {
                        return "Branch Path";
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error in GetTileType: {ex.Message}");
                }
            }

            return "Unknown";
        }

        /// <summary>
        /// Get a display-friendly name for a tile
        /// </summary>
        private string GetFriendlyTileName(Tile tile)
        {
            if (tile == null)
            {
                return "Unknown Area";
            }

            string name = tile.name;
            name = name.Replace("(Clone)", "");
            name = name.Replace("Prefab", "");

            // Check for common room types
            if (name.Contains("Bathroom"))
            {
                return "Bathroom";
            }
            if (name.Contains("Storage") || name.Contains("Closet"))
            {
                return "Storage Room";
            }
            if (name.Contains("Office"))
            {
                return "Office";
            }
            if (name.Contains("Hall"))
            {
                return "Hallway";
            }
            if (name.Contains("Entrance"))
            {
                return "Entrance";
            }
            if (name.Contains("Exit"))
            {
                return "Exit";
            }
            if (name.Contains("Stairs"))
            {
                return "Stairwell";
            }

            // Format the name if it doesn't match any known patterns
            string formattedName = Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
            formattedName = Regex.Replace(formattedName, "^([0-9]+)([A-Za-z])", "Room $1 $2");

            return formattedName.Trim();
        }

        /// <summary>
        /// Get the current tile
        /// </summary>
        public Tile GetCurrentTile()
        {
            return currentTile;
        }

        /// <summary>
        /// Get the name of the current tile
        /// </summary>
        public string GetCurrentTileName()
        {
            if (currentTile != null)
            {
                return GetFriendlyTileName(currentTile);
            }

            if (StartOfRound.Instance != null && StartOfRound.Instance.shipHasLanded)
            {
                if (StartOfRound.Instance.currentLevel == null)
                {
                    return "Company Building";
                }
                return StartOfRound.Instance.currentLevel.PlanetName;
            }

            return "Unknown Area";
        }

        /// <summary>
        /// Check if the player is on the main path
        /// </summary>
        public bool IsOnMainPath()
        {
            if (currentTile == null || dungeons == null || dungeons.Length == 0)
            {
                return false;
            }

            foreach (Dungeon dungeon in dungeons)
            {
                if (dungeon != null && dungeon.MainPathTiles != null && dungeon.MainPathTiles.Contains(currentTile))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Log detailed information about a tile
        /// </summary>
        private void LogDetailedTileInfo(Tile tile)
        {
            if (tile == null)
            {
                return;
            }

            try
            {
                LogMessage($"  Tile: {tile.name}");
                LogMessage($"    Position: {tile.transform.position}");
                LogMessage($"    Rotation: {tile.transform.eulerAngles}");
                LogMessage($"    Bounds: {tile.Bounds}");
                LogMessage($"    Local Bounds: {tile.Placement.LocalBounds}");

                if (tile.UsedDoorways != null)
                {
                    LogMessage($"    Used Doorways: {tile.UsedDoorways.Count}");
                }
                else
                {
                    LogMessage("    Used Doorways: null");
                }

                if (tile.UnusedDoorways != null)
                {
                    LogMessage($"    Unused Doorways: {tile.UnusedDoorways.Count}");
                }
                else
                {
                    LogMessage("    Unused Doorways: null");
                }

                if (tile.Tags != null && tile.Tags.Count() > 0)
                {
                    LogMessage($"    Tags: {string.Join(", ", tile.Tags)}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error logging tile info: {ex.Message}");
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Log a message with proper formatting
        /// </summary>
        private void LogMessage(string message)
        {
            if (Logger != null)
            {
                Logger.LogInfo(message);
            }
            else
            {
                Debug.Log($"TileTracker: {message}");
            }
        }

        /// <summary>
        /// Class representing a thread-safe collection of items
        /// </summary>
        public class ConcurrentBag<T>
        {
            private readonly object _lock = new object();
            private readonly List<T> _items = new List<T>();

            public void Add(T item)
            {
                lock (_lock)
                {
                    _items.Add(item);
                }
            }

            public bool Contains(T item)
            {
                lock (_lock)
                {
                    return _items.Contains(item);
                }
            }

            public void Clear()
            {
                lock (_lock)
                {
                    _items.Clear();
                }
            }

            public IEnumerator<T> GetEnumerator()
            {
                List<T> snapshot;
                lock (_lock)
                {
                    snapshot = new List<T>(_items);
                }
                return snapshot.GetEnumerator();
            }

            public void TrimExcess()
            {
                lock (_lock)
                {
                    _items.TrimExcess();
                }
            }

            public int Count
            {
                get
                {
                    lock (_lock)
                    {
                        return _items.Count;
                    }
                }
            }
        }

        #endregion
    }
}