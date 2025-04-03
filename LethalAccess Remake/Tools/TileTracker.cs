using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
    public class TileTracker : MonoBehaviour
    {
        public enum NavigationMode
        {
            Explore,
            Retrace,
            Off
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
        private float doorwayDiscoveryThreshold = 1.2f;
        private float hallwayOpeningDiscoveryThreshold = 3f;
        private float junctionDiscoveryThreshold = 3f;
        private float deadEndDiscoveryThreshold = 3f;
        private Dictionary<NavigationPoint.PointType, float> discoveryThresholds = new Dictionary<NavigationPoint.PointType, float>();
        private float junctionAnnounceCooldown = 0f;
        private float junctionAnnounceInterval = 3f;

        private readonly Dictionary<NavigationPoint.PointType, Color> pointColors = new Dictionary<NavigationPoint.PointType, Color>
    {
        { NavigationPoint.PointType.DoorWay, Color.green },
        { NavigationPoint.PointType.HallwayOpening, Color.cyan },
        { NavigationPoint.PointType.Junction, new Color(0.5f, 0f, 1f) },
        { NavigationPoint.PointType.DeadEnd, Color.white }
    };

        private readonly Dictionary<NavigationPoint.PointType, float> pointTypeVolumes = new Dictionary<NavigationPoint.PointType, float>
    {
        { NavigationPoint.PointType.DoorWay, 0.9f },
        { NavigationPoint.PointType.HallwayOpening, 0.8f },
        { NavigationPoint.PointType.Junction, 1f },
        { NavigationPoint.PointType.DeadEnd, 0.7f }
    };

        private Dungeon[] dungeons;
        private Tile currentTile;
        private bool dungeonSearchPending = true;
        private float dungeonSearchCooldown = 0f;
        private ManualLogSource Logger;
        private Transform playerTransform;
        private TileTriggerListener tileTriggerListener;
        private PlayerControllerB playerController;
        private bool isPeriodicSearchRunning = false;
        private ConcurrentBag<string> discoveredTileNames = new ConcurrentBag<string>();
        private bool isInitialized = false;
        private bool isStartupComplete = false;
        private float startupDelay = 5f;
        private ConcurrentBag<NavigationPoint> navigationPoints = new ConcurrentBag<NavigationPoint>();
        private Dictionary<string, List<NavigationPoint>> tileNavigationPoints = new Dictionary<string, List<NavigationPoint>>();
        private readonly object tileNavigationLock = new object();
        private bool markersInitialized = false;
        private float lastAudioPingTime = 0f;
        private bool isPlayingSequentialAudio = false;
        private AudioClip reachedPointSound;
        private Material markerMaterial;
        private ConcurrentDictionary<NavigationPoint.PointType, AudioClip> audioClips = new ConcurrentDictionary<NavigationPoint.PointType, AudioClip>();
        private Tile lastAnnouncedJunctionTile = null;
        private CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();
        private int updateFrameCounter = 0;
        private const int VISUALIZATION_UPDATE_INTERVAL = 10;
        private Vector3 cachedPlayerPosition;
        private readonly object playerPositionLock = new object();
        private List<NavigationPoint> currentAudioPoints = new List<NavigationPoint>();
        private readonly object audioPointsLock = new object();
        private RaycastHit[] raycastHitCache = new RaycastHit[8];
        private ConcurrentQueue<Tile> tileProcessingQueue = new ConcurrentQueue<Tile>();
        private bool isProcessingTiles = false;
        private Queue<GameObject> markerPool = new Queue<GameObject>();
        private const int INITIAL_POOL_SIZE = 50;
        private const int MAX_POOL_SIZE = 500;
        private volatile bool isDungeonProcessingComplete = false;
        private volatile bool isTileProcessingComplete = false;

        public void Initialize(ManualLogSource logger, Transform player)
        {
            try
            {
                Logger = logger;
                playerTransform = player;

                if (Logger != null)
                {
                    Logger.LogInfo("TileTracker initialization started");
                }
                else
                {
                    Debug.Log("TileTracker initialization started (Logger is null)");
                }

                discoveryThresholds[NavigationPoint.PointType.DoorWay] = doorwayDiscoveryThreshold;
                discoveryThresholds[NavigationPoint.PointType.HallwayOpening] = hallwayOpeningDiscoveryThreshold;
                discoveryThresholds[NavigationPoint.PointType.Junction] = junctionDiscoveryThreshold;
                discoveryThresholds[NavigationPoint.PointType.DeadEnd] = deadEndDiscoveryThreshold;

                wallLayerMask = LayerMask.GetMask(new string[4] { "Default", "Environment", "Blocking", "Facility" });

                if (StartOfRound.Instance != null)
                {
                    wallLayerMask |= StartOfRound.Instance.collidersAndRoomMaskAndDefault;
                }

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

                markerMaterial = new Material(Shader.Find("Sprites/Default"));
                markerMaterial.SetFloat("_Mode", 3f);

                LACore.Instance.RegisterKeybind("CycleNavigationMode", Key.M, CycleNavigationMode);
                LACore.Instance.RegisterKeybind("ToggleVisuals", Key.Backspace, ToggleVisuals);
                LACore.Instance.RegisterKeybind("AnnounceNearbyNavPoints", Key.U, AnnounceNearbyNavigationPoints);

                InitializeObjectPool();

                Task.Run(delegate
                {
                    GenerateAudioClips();
                });

                reachedPointSound = GenerateReachedPointSound();
                isInitialized = true;
                StartCoroutine(DelayedStartup());

                LogMessage("TileTracker initialization completed, waiting for delayed startup");
            }
            catch (Exception ex)
            {
                Debug.LogError("Error during TileTracker initialization: " + ex.Message + "\n" + ex.StackTrace);
            }
        }

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
                Debug.LogError("Error initializing object pool: " + ex.Message);
            }
        }

        private void LogMessage(string message)
        {
            if (Logger != null)
            {
                Logger.LogInfo(message);
            }
            else
            {
                Debug.Log("TileTracker: " + message);
            }
        }

        private IEnumerator DelayedStartup()
        {
            LogMessage("Delaying dungeon search to ensure game is fully loaded");
            yield return new WaitForSeconds(startupDelay);
            try
            {
                StartBackgroundDungeonSearch();
            }
            catch (Exception ex)
            {
                Debug.LogError("Error starting dungeon search: " + ex.Message + "\n" + ex.StackTrace);
            }
            isStartupComplete = true;
            LogMessage("Delayed startup complete, game state should be stable now");
        }

        private async void StartBackgroundDungeonSearch()
        {
            try
            {
                await Task.Run(delegate
                {
                    FindDungeonsAsync(cancellationTokenSource.Token);
                }, cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                LogMessage("Dungeon search was canceled");
            }
            catch (Exception ex)
            {
                Debug.LogError("Error in background dungeon search: " + ex.Message);
            }
        }

        private async void FindDungeonsAsync(CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            LogMessage("Starting background dungeon search...");
            bool dungeonsFetched = false;
            List<Dungeon> foundDungeons = new List<Dungeon>();
            bool foundRuntimeButNoDungeons = false;
            int runtimeDungeonCount = 0;

            UnityMainThreadDispatcher.Instance().Enqueue(delegate
            {
                try
                {
                    RuntimeDungeon[] runtimeDungeons = FindObjectsOfType<RuntimeDungeon>();
                    runtimeDungeonCount = runtimeDungeons != null ? runtimeDungeons.Length : 0;

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

                        if (foundDungeons.Count == 0)
                        {
                            foundRuntimeButNoDungeons = true;
                            LogMessage("Found RuntimeDungeon objects but no valid Dungeons with tiles inside them");
                        }
                    }

                    if (foundDungeons.Count == 0)
                    {
                        Dungeon[] directDungeons = FindObjectsOfType<Dungeon>();
                        if (directDungeons != null && directDungeons.Length > 0)
                        {
                            LogMessage($"Found {directDungeons.Length} direct Dungeon(s)");
                            foundDungeons.AddRange(directDungeons);
                        }
                    }

                    if (foundDungeons.Count > 0)
                    {
                        dungeons = foundDungeons.ToArray();
                        dungeonSearchPending = false;
                        StartTileProcessing();
                    }
                    else
                    {
                        LogMessage("No dungeons found, will retry later");
                    }

                    dungeonsFetched = true;
                }
                catch (Exception ex)
                {
                    Debug.LogError("Error finding dungeons on main thread: " + ex.Message);
                    dungeonsFetched = true;
                }
            });

            while (!dungeonsFetched && !token.IsCancellationRequested)
            {
                await Task.Delay(100, token);
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (dungeons == null || dungeons.Length == 0)
            {
                if (foundRuntimeButNoDungeons && runtimeDungeonCount > 0)
                {
                    LogMessage("Detected location with RuntimeDungeon but no usable dungeons (likely Company Building)");
                    LogMessage("Disabling dungeon-based tracking for this location");
                    isDungeonProcessingComplete = true;
                    dungeonSearchPending = false;
                    dungeons = new Dungeon[0];
                }
                else if (!isPeriodicSearchRunning)
                {
                    UnityMainThreadDispatcher.Instance().Enqueue(delegate
                    {
                        StartCoroutine(PeriodicDungeonSearch());
                    });
                }
                else
                {
                    LogMessage("Periodic search already running, not starting another");
                }
            }
            else
            {
                isDungeonProcessingComplete = true;
                LogMessage("Dungeon search completed successfully");
            }
        }

        private IEnumerator PeriodicDungeonSearch()
        {
            int maxRetries = 1000;
            int retryCount = 0;
            isPeriodicSearchRunning = true;
            LogMessage("Starting periodic dungeon search");

            try
            {
                while ((dungeons == null || dungeons.Length == 0) && retryCount < maxRetries)
                {
                    yield return new WaitForSeconds(5f);
                    retryCount++;

                    if (!isStartupComplete || GameNetworkManager.Instance == null || GameNetworkManager.Instance.localPlayerController == null)
                    {
                        LogMessage("Skipping dungeon search - game not fully initialized");
                        continue;
                    }

                    LogMessage($"Retrying dungeon search (attempt {retryCount}/{maxRetries})...");
                    StartBackgroundDungeonSearch();
                    yield return new WaitForSeconds(2f);

                    if (dungeons != null && dungeons.Length > 0)
                    {
                        LogMessage("Periodic search found dungeons successfully");
                        break;
                    }
                }

                if (dungeons == null || dungeons.Length == 0)
                {
                    LogMessage("No dungeons found after multiple attempts - continuing without dungeon-based tracking");
                    isDungeonProcessingComplete = true;
                    dungeonSearchPending = false;
                    dungeons = new Dungeon[0];
                }
            }
            finally
            {
                isPeriodicSearchRunning = false;
                LogMessage("Periodic dungeon search completed");
            }
        }

        public void ResetDungeonSearch()
        {
            if (cancellationTokenSource != null)
            {
                cancellationTokenSource.Cancel();
                cancellationTokenSource = new CancellationTokenSource();
            }

            isPeriodicSearchRunning = false;
            isDungeonProcessingComplete = false;
            dungeonSearchPending = true;
            dungeons = null;

            if (isStartupComplete)
            {
                LogMessage("Resetting dungeon search - starting new search");
                StartBackgroundDungeonSearch();
            }
        }

        private void StartTileProcessing()
        {
            if (dungeons == null || dungeons.Length == 0)
            {
                LogMessage("No dungeons available for tile processing");
                return;
            }

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
                        tileProcessingQueue.Enqueue(tile);
                    }
                }
            }

            LogMessage($"Queued {tileProcessingQueue.Count} tiles for processing");

            if (!isProcessingTiles)
            {
                isProcessingTiles = true;
                Task.Run(delegate
                {
                    ProcessTilesAsync(cancellationTokenSource.Token);
                });
            }
        }

        private async void ProcessTilesAsync(CancellationToken token)
        {
            LogMessage("Starting background tile processing...");
            int processedCount = 0;
            int batchSize = 5;
            List<Tile> currentBatch = new List<Tile>(batchSize);

            while (!token.IsCancellationRequested)
            {
                currentBatch.Clear();

                for (int i = 0; i < batchSize; i++)
                {
                    if (tileProcessingQueue.IsEmpty)
                    {
                        break;
                    }

                    Tile tile;
                    if (tileProcessingQueue.TryDequeue(out tile))
                    {
                        currentBatch.Add(tile);
                    }
                    tile = null;
                }

                if (currentBatch.Count == 0)
                {
                    break;
                }

                bool batchComplete = false;

                UnityMainThreadDispatcher.Instance().Enqueue(delegate
                {
                    try
                    {
                        foreach (Tile tile in currentBatch)
                        {
                            if (tile != null)
                            {
                                ProcessTileForNavigationPoints(tile);
                                processedCount++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("Error processing tile batch: " + ex.Message);
                    }
                    batchComplete = true;
                });

                while (!batchComplete && !token.IsCancellationRequested)
                {
                    await Task.Delay(50, token);
                }

                await Task.Delay(10, token);
            }

            if (!token.IsCancellationRequested)
            {
                LogMessage($"Tile processing complete. Processed {processedCount} tiles.");
                markersInitialized = true;
                isTileProcessingComplete = true;

                UnityMainThreadDispatcher.Instance().Enqueue(delegate
                {
                    UpdateAllMarkerVisibility();
                });
            }

            while (!tileProcessingQueue.IsEmpty)
            {
                Tile tile;
                tileProcessingQueue.TryDequeue(out tile);
            }

            isProcessingTiles = false;
        }

        private void GenerateAudioClips()
        {
            try
            {
                float[] doorwayToneData = GenerateDoorwayToneData();
                float[] hallwayOpeningToneData = GenerateHallwayOpeningToneData();
                float[] junctionToneData = GenerateJunctionToneData();
                float[] deadEndToneData = GenerateDeadEndToneData();
                float[] reachedPointSoundData = GenerateReachedPointSoundData();

                UnityMainThreadDispatcher.Instance().Enqueue(delegate
                {
                    try
                    {
                        audioClips[NavigationPoint.PointType.DoorWay] = CreateAudioClipFromData("DoorwayTone", doorwayToneData);
                        audioClips[NavigationPoint.PointType.HallwayOpening] = CreateAudioClipFromData("HallwayOpeningTone", hallwayOpeningToneData);
                        audioClips[NavigationPoint.PointType.Junction] = CreateAudioClipFromData("JunctionTone", junctionToneData);
                        audioClips[NavigationPoint.PointType.DeadEnd] = CreateAudioClipFromData("DeadEndTone", deadEndToneData);
                        LogMessage("Audio tones generated successfully");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("Error creating audio clips on main thread: " + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.LogError("Error generating audio data: " + ex.Message);
            }
        }

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

            // Second tone
            for (int i = 0; i < Mathf.RoundToInt(sampleRate * toneDuration) && index < sampleCount; i++)
            {
                float normalizedTime = i / (sampleRate * toneDuration);
                float envelope = Mathf.Sin(normalizedTime * Mathf.PI);
                samples[index++] = Mathf.Sin(Mathf.PI * 2f * (baseFreq * 1.5f) * i / sampleRate) * envelope * 0.7f;
            }

            return samples;
        }

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

        private float[] GenerateReachedPointSoundData()
        {
            int sampleRate = 44100;
            float frequency = 2000f;
            float duration = 0.05f;
            int sampleCount = Mathf.RoundToInt(sampleRate * duration);
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float normalizedTime = i / (float)sampleCount;
                float envelope = Mathf.Exp(-normalizedTime * 10f);
                samples[i] = Mathf.Sin(Mathf.PI * 2f * frequency * i / sampleRate) * envelope * 0.8f;
            }

            return samples;
        }

        private AudioClip CreateAudioClipFromData(string name, float[] samples)
        {
            int sampleRate = 44100;
            AudioClip clip = AudioClip.Create(name, samples.Length, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

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

        private void ToggleVisuals()
        {
            showVisuals = !showVisuals;
            Utilities.SpeakText(showVisuals ? "Navigation markers visible." : "Navigation markers hidden.");
            UpdateAllMarkerVisibility();
        }

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

                if (tile.UsedDoorways.Count > 2)
                {
                    Vector3 center = tile.Bounds.center + new Vector3(0f, 0.2f, 0f);
                    int connectionCount = tile.UsedDoorways.Count;
                    NavigationPoint junction = new NavigationPoint(center, NavigationPoint.PointType.Junction, tile, $"Junction ({connectionCount} connections)", connectionCount);
                    pointsInTile.Add(junction);
                    navigationPoints.Add(junction);
                }
                else if (tile.UsedDoorways.Count == 1)
                {
                    Vector3 center = tile.Bounds.center + new Vector3(0f, 0.2f, 0f);
                    NavigationPoint deadEnd = new NavigationPoint(center, NavigationPoint.PointType.DeadEnd, tile, "Dead End");
                    pointsInTile.Add(deadEnd);
                    navigationPoints.Add(deadEnd);
                }

                CheckForMissedDoors(tile, pointsInTile);

                lock (tileNavigationLock)
                {
                    tileNavigationPoints[tileId] = pointsInTile;
                }

                UnityMainThreadDispatcher.Instance().Enqueue(delegate
                {
                    try
                    {
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
                        Debug.LogError("Error creating visual markers: " + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.LogError("Error processing navigation points for tile: " + ex.Message);
            }
        }

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
                        LogMessage("Added missed door in tile: " + tile.name);
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage("Error checking for missed doors: " + ex.Message);
            }
        }

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
                Debug.LogError("Error creating visual marker: " + ex.Message);
            }
        }

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
                Debug.LogError("Error creating compound visual marker: " + ex.Message);
            }
        }

        private bool IsPointInProximity(Vector3 playerPos, Vector3 point)
        {
            return Vector3.Distance(playerPos, point) <= proximityThreshold;
        }

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

        private bool IsPointInDiscoveryProximity(Vector3 playerPos, Vector3 point, NavigationPoint.PointType pointType)
        {
            float threshold;
            float defaultThreshold = 1f;
            float discoveryThreshold = discoveryThresholds.TryGetValue(pointType, out threshold) ? threshold : defaultThreshold;

            return Vector3.Distance(playerPos, point) <= discoveryThreshold;
        }

        private bool IsPointBehindPlayer(Vector3 playerPos, Vector3 playerForward, Vector3 point)
        {
            Vector3 directionToPoint = point - playerPos;
            float dotProduct = Vector3.Dot(directionToPoint.normalized, playerForward);
            return dotProduct < 0f;
        }

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
                    point.audioSource.pitch = 0.5f;
                }
                else
                {
                    point.audioSource.pitch = 1f;
                }

                point.audioSource.volume = finalVolume;
                point.audioSource.PlayOneShot(clip);
            }
        }

        private void PlayReachedPointSound(Vector3 playerPos)
        {
            if (reachedPointSound == null)
                return;

            GameObject tempAudio = new GameObject("TempClickAudio");
            tempAudio.transform.position = playerPos;

            AudioSource audioSource = tempAudio.AddComponent<AudioSource>();
            AudioSystemBypass.ConfigureAudioSourceForBypass(audioSource, 0.8f * masterVolume);
            audioSource.clip = reachedPointSound;
            audioSource.spatialBlend = 0f;
            audioSource.Play();

            Destroy(tempAudio, reachedPointSound.length + 0.1f);
        }

        private void PlayAudioForNearbyPoints()
        {
            if (currentMode == NavigationMode.Off || !markersInitialized || playerTransform == null || isPlayingSequentialAudio)
            {
                return;
            }

            Vector3 playerPos;
            lock (playerPositionLock)
            {
                if (playerTransform == null)
                {
                    return;
                }
                playerPos = playerTransform.position;
                Vector3 forward = playerTransform.forward;
            }

            List<NavigationPoint> pointsToPlay = new List<NavigationPoint>();

            foreach (NavigationPoint point in navigationPoints)
            {
                // Only consider points based on the current navigation mode
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

            pointsToPlay = pointsToPlay.OrderBy(p => Vector3.Distance(playerPos, p.position)).ToList();

            if (pointsToPlay.Count > 0)
            {
                StartCoroutine(PlaySequentialAudio(pointsToPlay));
            }
        }

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

            int maxPointsPerUpdate = 20;
            List<NavigationPoint> pointsToUpdate = new List<NavigationPoint>();

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

        private void UpdateAllMarkerVisibility()
        {
            if (!markersInitialized || !LethalAccess.Patches.IsInsideFactoryPatch.IsInFactory)
            {
                // If not in factory, hide all markers
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

            // Rest of the original method...
            Vector3 position;
            lock (playerPositionLock)
            {
                if (playerTransform == null)
                {
                    return;
                }
                position = playerTransform.position;
            }

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

            foreach (NavigationPoint point in newlyDiscoveredPoints)
            {
                point.isDiscovered = true;
                PlayReachedPointSound(position);
            }

            UpdateAllMarkerVisibility();
        }

        private void Update()
        {
            try
            {
                if (!isInitialized || playerTransform == null)
                {
                    return;
                }

                if (junctionAnnounceCooldown > 0f)
                {
                    junctionAnnounceCooldown -= Time.deltaTime;
                }

                lock (playerPositionLock)
                {
                    if (playerTransform != null)
                    {
                        cachedPlayerPosition = playerTransform.position;
                    }
                }

                // Only perform tile and position checking if the player is inside a factory
                bool isInFactory = LethalAccess.Patches.IsInsideFactoryPatch.IsInFactory;

                if (isStartupComplete && isInFactory)
                {
                    if (currentTile == null)
                    {
                        CheckCurrentTileManually();
                    }
                    else if (Time.frameCount % 60 == 0)
                    {
                        CheckCurrentTileManually();
                    }
                }

                // Only update markers and check for discovered points when in factory
                if (markersInitialized && isInFactory)
                {
                    updateFrameCounter++;
                    if (updateFrameCounter >= VISUALIZATION_UPDATE_INTERVAL)
                    {
                        UpdateMarkersVisibility();
                        updateFrameCounter = 0;
                    }

                    CheckForDiscoveredPoints();
                }

                // Audio pings should still work even when not in factory
                if (markersInitialized && currentMode != NavigationMode.Off && Time.time - lastAudioPingTime > audioPingInterval && !isPlayingSequentialAudio)
                {
                    PlayAudioForNearbyPoints();
                    lastAudioPingTime = Time.time;
                }

                ProcessMainThreadActions();
            }
            catch (Exception ex)
            {
                Debug.LogError("Error in TileTracker.Update: " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        private void ProcessMainThreadActions()
        {
            int maxActionsPerFrame = 5;
            int processedCount = 0;

            while (processedCount < maxActionsPerFrame)
            {
                Action action;
                if (!mainThreadActions.TryDequeue(out action))
                {
                    break;
                }

                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogError("Error executing queued action: " + ex.Message);
                }

                processedCount++;
            }
        }

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

            LogMessage("Player entered tile: " + newTileName + " (Previous: " + previousTileName + "), Type: " + tileType);

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

        public void OnPlayerExitedTile(Tile tile)
        {
            // Only process if player is in factory
            if (!LethalAccess.Patches.IsInsideFactoryPatch.IsInFactory)
            {
                return;
            }

            LogMessage("Player exited tile: " + tile.name);

            if (tile == lastAnnouncedJunctionTile)
            {
                lastAnnouncedJunctionTile = null;
            }
        }

        public List<NavigationPoint> GetNavigationPointsByType(NavigationPoint.PointType type)
        {
            if (navigationPoints == null)
            {
                return new List<NavigationPoint>();
            }

            return navigationPoints.Where(p => p.type == type).ToList();
        }

        private void CheckCurrentTileManually()
        {
            try
            {
                // Skip the entire method if not in factory
                if (!LethalAccess.Patches.IsInsideFactoryPatch.IsInFactory)
                {
                    return;
                }

                if (dungeons == null || dungeons.Length == 0 || playerTransform == null)
                {
                    return;
                }

                Vector3 position = playerTransform.position;
                Tile closestTile = null;
                float closestDistance = float.MaxValue;

                foreach (Dungeon dungeon in dungeons)
                {
                    try
                    {
                        if (dungeon == null)
                        {
                            continue;
                        }

                        Bounds dungeonBounds = dungeon.Bounds;
                        if (!dungeonBounds.Contains(position))
                        {
                            continue;
                        }

                        if (dungeon.AllTiles == null)
                        {
                            LogMessage("Dungeon " + dungeon.name + " has null AllTiles collection");
                            continue;
                        }

                        foreach (Tile tile in dungeon.AllTiles)
                        {
                            if (tile == null)
                            {
                                continue;
                            }

                            try
                            {
                                Bounds tileBounds = tile.Bounds;
                                if (tileBounds.Contains(position))
                                {
                                    closestTile = tile;
                                    closestDistance = 0f;
                                    break;
                                }

                                float distanceToTile = Vector3.Distance(position, tileBounds.center);
                                if (distanceToTile < closestDistance)
                                {
                                    closestDistance = distanceToTile;
                                    closestTile = tile;
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError("Error checking tile: " + ex.Message);
                            }
                        }

                        if (closestDistance == 0f)
                        {
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("Error processing dungeon in CheckCurrentTileManually: " + ex.Message);
                    }
                }

                if (closestTile != null && closestTile != currentTile)
                {
                    OnPlayerEnteredTile(closestTile);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("Error in CheckCurrentTileManually: " + ex.Message + "\n" + ex.StackTrace);
            }
        }

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
                    Debug.LogError("Error in GetTileType: " + ex.Message);
                }
            }

            return "Unknown";
        }

        private string GetFriendlyTileName(Tile tile)
        {
            if (tile == null)
            {
                return "Unknown Area";
            }

            string name = tile.name;
            name = name.Replace("(Clone)", "");
            name = name.Replace("Prefab", "");

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

            string formattedName = Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
            formattedName = Regex.Replace(formattedName, "^([0-9]+)([A-Za-z])", "Room $1 $2");

            return formattedName.Trim();
        }

        public Tile GetCurrentTile()
        {
            return currentTile;
        }

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

        public bool IsPlayerInFactoryArea()
        {
            if (currentTile != null)
            {
                string tileName = currentTile.name.ToLower();
                return tileName.Contains("factory") || currentTile.Tags != null && currentTile.Tags.Any(tag => tag.ToString().ToLower().Contains("factory"));
            }

            if (StartOfRound.Instance != null && StartOfRound.Instance.currentLevel != null)
            {
                string planetName = StartOfRound.Instance.currentLevel.PlanetName?.ToLower() ?? "";
                return planetName.Contains("factory");
            }

            return false;
        }

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

            if (currentMode == NavigationMode.Explore)
            {
                nearbyPoints = navigationPoints
                    .Where(p => !p.isDiscovered && IsPointInAudioProximity(playerPos, p.position))
                    .OrderBy(p => Vector3.Distance(playerPos, p.position))
                    .Take(3)
                    .ToList();
            }
            else if (currentMode == NavigationMode.Retrace)
            {
                nearbyPoints = navigationPoints
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

        public void ResetAllDiscoveredPoints()
        {
            foreach (NavigationPoint point in navigationPoints)
            {
                point.isDiscovered = false;
            }

            UpdateAllMarkerVisibility();
        }

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

        private void LogDetailedTileInfo(Tile tile)
        {
            if (tile == null)
            {
                return;
            }

            try
            {
                LogMessage("  Tile: " + tile.name);
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
                    LogMessage("    Tags: " + string.Join(", ", tile.Tags));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("Error logging tile info: " + ex.Message);
            }
        }

        private GameObject GetMarkerFromPool(NavigationPoint.PointType type)
        {
            if (markerPool.Count > 0)
            {
                GameObject marker = markerPool.Dequeue();
                marker.SetActive(true);
                return marker;
            }

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

        private void ReturnNodeToPool(GameObject nodeObj)
        {
            if (nodeObj == null)
                return;

            nodeObj.SetActive(false);
            markerPool.Enqueue(nodeObj);
        }

        private void OnDestroy()
        {
            try
            {
                if (cancellationTokenSource != null)
                {
                    cancellationTokenSource.Cancel();
                    cancellationTokenSource.Dispose();
                    cancellationTokenSource = null;
                }

                StopAllCoroutines();

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

                lock (tileNavigationLock)
                {
                    tileNavigationPoints.Clear();
                }

                int pooledObjectCount = markerPool.Count;
                while (markerPool.Count > 0)
                {
                    GameObject marker = markerPool.Dequeue();
                    if (marker != null)
                    {
                        Destroy(marker);
                    }
                }

                LogMessage($"TileTracker destroyed, {pooledObjectCount} pooled objects and all resources cleaned up");
            }
            catch (Exception ex)
            {
                Debug.LogError("Error in TileTracker.OnDestroy: " + ex.Message);
            }
        }
    }
}