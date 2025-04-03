using LethalAccess;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace LethalAccess
{
    /// <summary>
    /// Provides aim assist functionality for navigation nodes in LethalAccess
    /// </summary>
    public class NavigationAimAssist : MonoBehaviour
    {
        #region Configuration
        // The angle in degrees that will trigger aim assist detection
        private const float AIM_ASSIST_ANGLE_THRESHOLD = 25f;

        // The maximum distance at which aim assist will work
        private const float AIM_ASSIST_MAX_DISTANCE = 8f;

        // Sound volume when snapping to a node
        private const float SNAP_SOUND_VOLUME = 0.6f;

        // Cooldown between target checks (seconds)
        private const float TARGET_CHECK_COOLDOWN = 0.3f;

        // How strongly the aim assist pulls toward targets (higher = stickier)
        private const float STICKY_FACTOR = 3f;

        // Maximum rotation speed in degrees per second
        private const float MAX_ROTATION_SPEED = 100f;

        // Minimum rotation speed in degrees per second (to ensure some movement)
        private const float MIN_ROTATION_SPEED = 20f;

        // How much influence the player's input has to escape sticky behavior
        private const float ESCAPE_INFLUENCE = 5f;
        #endregion

        #region State Variables
        // Current aim assist target
        private GameObject currentTarget = null;

        // Current target position (to avoid thread sync issues)
        private Vector3 currentTargetPosition;

        // Whether we're currently assisting
        private bool isAssisting = false;

        // Whether aim assist is enabled
        private bool isEnabled = true;

        // References to the player transform and camera
        private Transform playerTransform;
        private Transform cameraTransform;

        // Reference to player controller
        private GameNetcodeStuff.PlayerControllerB playerController;

        // Audio clip for the snap sound
        private AudioClip snapSoundClip;

        // Audio source for playing the snap sound
        private AudioSource audioSource;

        // Player's current looking direction
        private Vector3 playerLookDirection;

        // Target check timer
        private float targetCheckTimer = 0f;

        // Target check is in progress
        private bool isCheckingTargets = false;

        // Cancellation token source for background tasks
        private CancellationTokenSource cancellationSource;

        // Last player input direction
        private float lastHorizontalInput = 0f;

        // Layer mask for line of sight checks
        private LayerMask lineOfSightMask;

        // Last target ID to prevent sound spam
        private int lastTargetId = -1;

        // Performance monitoring
        private float lastFindTargetDuration = 0f;
        #endregion

        public void Initialize()
        {
            try
            {
                Debug.Log("Initializing NavigationAimAssist");

                // Create audio source
                audioSource = gameObject.AddComponent<AudioSource>();

                // Configure to bypass Unity audio system
                AudioSystemBypass.ConfigureAudioSourceForBypass(audioSource, SNAP_SOUND_VOLUME);

                // Set additional properties
                audioSource.spatialBlend = 0f; // 2D sound
                audioSource.playOnAwake = false;

                // Generate snap sound
                snapSoundClip = GenerateSnapSound();

                // Set up layer mask for line of sight
                lineOfSightMask = LayerMask.GetMask("Default", "Environment", "Blocking", "Facility") |
                                 (StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : 0);

                // Create cancellation token source
                cancellationSource = new CancellationTokenSource();

                // Update references
                UpdateReferences();

                // Log success
                Debug.Log("NavigationAimAssist initialized successfully");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error initializing NavigationAimAssist: {ex.Message}\n{ex.StackTrace}");
            }
        }

        void OnDisable()
        {
            // Cancel any running tasks when disabled
            try
            {
                if (cancellationSource != null && !cancellationSource.IsCancellationRequested)
                {
                    cancellationSource.Cancel();
                    cancellationSource = new CancellationTokenSource();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error cancelling background tasks: {ex.Message}");
            }
        }

        void OnDestroy()
        {
            // Clean up resources
            try
            {
                if (cancellationSource != null)
                {
                    cancellationSource.Cancel();
                    cancellationSource.Dispose();
                    cancellationSource = null;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error cleaning up NavigationAimAssist: {ex.Message}");
            }
        }

        void Update()
        {
            // Update target check timer
            if (targetCheckTimer > 0)
            {
                targetCheckTimer -= Time.deltaTime;
            }

            // Only run if we have valid references and aim assist is enabled
            if (!isEnabled || playerTransform == null || cameraTransform == null || playerController == null)
            {
                if (playerTransform == null || cameraTransform == null)
                {
                    UpdateReferences();
                }
                return;
            }

            // Cache player input
            lastHorizontalInput = 0f;
            if (UnityEngine.InputSystem.Keyboard.current.leftArrowKey.isPressed)
                lastHorizontalInput = -1f;
            else if (UnityEngine.InputSystem.Keyboard.current.rightArrowKey.isPressed)
                lastHorizontalInput = 1f;
            else
                lastHorizontalInput = playerController.moveInputVector.x;

            // Cache player look direction
            playerLookDirection = cameraTransform.forward;
            playerLookDirection.y = 0; // Only use horizontal component
            playerLookDirection.Normalize();

            // Check if current target is still valid
            bool currentTargetValid = false;
            if (currentTarget != null && currentTarget.activeInHierarchy && isAssisting)
            {
                // Get direction to current target
                Vector3 toTarget = currentTarget.transform.position - cameraTransform.position;
                toTarget.y = 0;
                toTarget.Normalize();

                // Check if still in range and angle
                float distance = Vector3.Distance(playerTransform.position, currentTarget.transform.position);
                float angle = Vector3.Angle(playerLookDirection, toTarget);

                currentTargetValid = (distance <= AIM_ASSIST_MAX_DISTANCE && angle <= AIM_ASSIST_ANGLE_THRESHOLD);
            }

            // If it's time to check for targets, we're not already checking, and we don't have a valid target
            if (targetCheckTimer <= 0 && !isCheckingTargets && !currentTargetValid)
            {
                // Start the target check in a background thread
                targetCheckTimer = TARGET_CHECK_COOLDOWN;
                StartTargetCheck();
            }

            // If we have a target and are assisting, apply the rotation
            if (currentTarget != null && isAssisting)
            {
                // Cache target position on the main thread
                if (currentTarget != null && currentTarget.activeInHierarchy)
                {
                    currentTargetPosition = currentTarget.transform.position;
                }

                // Apply sticky aim rotation
                ApplyStickyAim();
            }
        }

        /// <summary>
        /// Start a background task to check for potential targets
        /// </summary>
        private void StartTargetCheck()
        {
            if (cancellationSource == null || cancellationSource.IsCancellationRequested)
            {
                cancellationSource = new CancellationTokenSource();
            }

            // Capture current values for the background thread
            Vector3 currentPos = playerTransform.position;
            Vector3 currentLook = playerLookDirection;
            Vector3 cameraPos = cameraTransform.position;

            isCheckingTargets = true;

            // Start the background task
            Task.Run(() =>
            {
                // Track the start time for performance monitoring
                DateTime startTime = DateTime.Now;

                try
                {
                    // Find potential targets off the main thread
                    TargetInfo bestTarget = FindBestTarget(currentPos, currentLook, cameraPos, cancellationSource.Token);

                    // Calculate elapsed time
                    TimeSpan elapsed = DateTime.Now - startTime;
                    lastFindTargetDuration = (float)elapsed.TotalMilliseconds;

                    // If we found a target, handle it on the main thread
                    if (bestTarget != null)
                    {
                        UnityMainThreadDispatcher.Instance().Enqueue(() =>
                        {
                            HandleFoundTarget(bestTarget);
                        });
                    }
                    else
                    {
                        // No target found, clear on main thread
                        UnityMainThreadDispatcher.Instance().Enqueue(() =>
                        {
                            currentTarget = null;
                            isAssisting = false;
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    // Task was cancelled, this is expected
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error in target check task: {ex.Message}");
                }
                finally
                {
                    // Always mark as done on the main thread
                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        isCheckingTargets = false;

                        // Log performance stats occasionally (every ~5 seconds)
                        if (Time.frameCount % 300 == 0)
                        {
                            Debug.Log($"Target finding took {lastFindTargetDuration:F2}ms");
                        }
                    });
                }
            }, cancellationSource.Token);
        }

        /// <summary>
        /// Find the best target for aim assist (runs on background thread)
        /// </summary>
        private TargetInfo FindBestTarget(Vector3 playerPos, Vector3 lookDir, Vector3 cameraPos, CancellationToken token)
        {
            // Results collection
            List<TargetInfo> potentialTargets = new List<TargetInfo>();

            // Check if we should cancel
            if (token.IsCancellationRequested)
                return null;

            // Get all GameObjects (must run on main thread)
            GameObject[] allObjects = null;
            bool objectsRetrieved = false;

            UnityMainThreadDispatcher.Instance().Enqueue(() =>
            {
                try
                {
                    allObjects = GameObject.FindObjectsOfType<GameObject>();
                    objectsRetrieved = true;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error finding GameObjects: {ex.Message}");
                    objectsRetrieved = true;
                }
            });

            // Wait for objects to be retrieved
            while (!objectsRetrieved && !token.IsCancellationRequested)
            {
                Thread.Sleep(5);
            }

            // Check if we should cancel
            if (token.IsCancellationRequested || allObjects == null)
                return null;

            // Process objects in batches to avoid excessive delays
            const int batchSize = 50;
            for (int i = 0; i < allObjects.Length; i += batchSize)
            {
                // Check if we should cancel between batches
                if (token.IsCancellationRequested)
                    return null;

                // Process a batch of objects
                int endIdx = Math.Min(i + batchSize, allObjects.Length);

                for (int j = i; j < endIdx; j++)
                {
                    GameObject obj = allObjects[j];

                    // Skip invalid objects
                    if (obj == null || !obj.activeInHierarchy)
                        continue;

                    // Check if this is a pathfinder node or navigation point
                    string objName = obj.name;
                    bool isPathfinderNode = objName.StartsWith("PathNode_") || objName.StartsWith("PooledPathNode");
                    bool isNavPoint = objName.StartsWith("NavMarker_");

                    if (!isPathfinderNode && !isNavPoint)
                        continue;

                    // Check on main thread if this object is within range
                    bool isInRange = false;
                    bool checkComplete = false;

                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        try
                        {
                            if (obj != null && obj.activeInHierarchy)
                            {
                                // Check distance
                                float distance = Vector3.Distance(playerPos, obj.transform.position);
                                isInRange = (distance <= AIM_ASSIST_MAX_DISTANCE);
                            }
                            checkComplete = true;
                        }
                        catch
                        {
                            checkComplete = true;
                        }
                    });

                    // Wait for check to complete
                    while (!checkComplete && !token.IsCancellationRequested)
                    {
                        Thread.Sleep(1);
                    }

                    // Skip if not in range or cancelled
                    if (!isInRange || token.IsCancellationRequested)
                        continue;

                    // Get position and check angle on main thread
                    Vector3 objPos = Vector3.zero;
                    float angleToView = float.MaxValue;
                    bool hasLOS = false;
                    bool angleCheckComplete = false;

                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        try
                        {
                            if (obj != null && obj.activeInHierarchy)
                            {
                                objPos = obj.transform.position;

                                // Calculate horizontal angle only to view direction
                                Vector3 toTarget = objPos - cameraPos;
                                Vector3 horizontalToTarget = new Vector3(toTarget.x, 0, toTarget.z).normalized;
                                float angle = Vector3.Angle(lookDir, horizontalToTarget);

                                // Only check line of sight if angle is within threshold
                                if (angle < AIM_ASSIST_ANGLE_THRESHOLD)
                                {
                                    angleToView = angle;
                                    hasLOS = !Physics.Linecast(cameraPos, objPos, lineOfSightMask);
                                }
                            }
                            angleCheckComplete = true;
                        }
                        catch
                        {
                            angleCheckComplete = true;
                        }
                    });

                    // Wait for angle check to complete
                    while (!angleCheckComplete && !token.IsCancellationRequested)
                    {
                        Thread.Sleep(1);
                    }

                    // Skip if angle too large or cancelled
                    if (angleToView >= AIM_ASSIST_ANGLE_THRESHOLD || !hasLOS || token.IsCancellationRequested)
                        continue;

                    // Calculate priority - pathfinder nodes have higher priority
                    int priority = isPathfinderNode ? 2 : 1;

                    // Add to potential targets
                    float objDistance = Vector3.Distance(playerPos, objPos);

                    potentialTargets.Add(new TargetInfo
                    {
                        GameObject = obj,
                        Position = objPos,
                        AngleToView = angleToView,
                        Distance = objDistance,
                        Priority = priority
                    });
                }

                // Small delay between batches to avoid thread hogging
                if (i + batchSize < allObjects.Length && !token.IsCancellationRequested)
                {
                    Thread.Sleep(1);
                }
            }

            // If cancelled, return null
            if (token.IsCancellationRequested)
                return null;

            // Sort targets by priority and angle
            if (potentialTargets.Count > 0)
            {
                // First by priority (high to low), then by angle (low to high)
                var sortedTargets = potentialTargets
                    .OrderByDescending(t => t.Priority)
                    .ThenBy(t => t.AngleToView)
                    .ToList();

                return sortedTargets.FirstOrDefault();
            }

            return null;
        }

        /// <summary>
        /// Handle a found target from the background thread on the main thread
        /// </summary>
        private void HandleFoundTarget(TargetInfo target)
        {
            // Validity check
            if (target == null || target.GameObject == null || !target.GameObject.activeInHierarchy)
            {
                currentTarget = null;
                isAssisting = false;
                return;
            }

            // Check if this is a new target
            bool isNewTarget = (currentTarget == null || currentTarget.GetInstanceID() != target.GameObject.GetInstanceID());

            // Update current target and state
            currentTarget = target.GameObject;
            currentTargetPosition = target.Position;
            isAssisting = true;

            // Play sound for new targets
            if (isNewTarget && audioSource != null && snapSoundClip != null)
            {
                // Only log and play sound if this is a different target than the previous one
                if (target.GameObject.GetInstanceID() != lastTargetId)
                {
                    Debug.Log($"Aim assist found target: {target.GameObject.name}, angle: {target.AngleToView}°");

                    // Play with our modified audio source that bypasses Unity audio system
                    audioSource.PlayOneShot(snapSoundClip);
                    lastTargetId = target.GameObject.GetInstanceID();
                }
            }
        }

        /// <summary>
        /// Apply sticky aim toward the current target (runs on main thread)
        /// </summary>
        private void ApplyStickyAim()
        {
            if (currentTarget == null || !currentTarget.activeInHierarchy)
            {
                isAssisting = false;
                return;
            }

            // Get current facing and target direction
            Vector3 currentForward = new Vector3(playerLookDirection.x, 0, playerLookDirection.z).normalized;
            Vector3 toTarget = currentTargetPosition - cameraTransform.position;
            toTarget.y = 0; // Zero out vertical component
            toTarget.Normalize();

            // Check angle - stop assisting if looking away
            float currentAngle = Vector3.Angle(currentForward, toTarget);
            if (currentAngle > AIM_ASSIST_ANGLE_THRESHOLD)
            {
                isAssisting = false;
                return;
            }

            // Check if player is trying to escape the aim assist
            float escapeInput = lastHorizontalInput;
            bool isEscaping = false;

            if (Mathf.Abs(escapeInput) > 0.2f)
            {
                // Determine if input is moving away from target
                float horizontalAngle = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
                float currentHorizontalAngle = Mathf.Atan2(currentForward.x, currentForward.z) * Mathf.Rad2Deg;

                // Calculate the direction to turn to reach the target (positive = right, negative = left)
                float angleDiff = Mathf.DeltaAngle(currentHorizontalAngle, horizontalAngle);

                // If input direction is opposite to the direction needed to turn toward target
                isEscaping = (angleDiff > 0 && escapeInput < -0.2f) || (angleDiff < 0 && escapeInput > 0.2f);
            }

            // If actively escaping, reduce sticky factor
            float effectiveStickyFactor = isEscaping ?
                STICKY_FACTOR / ESCAPE_INFLUENCE :
                STICKY_FACTOR;

            // Calculate rotation speed based on angle (closer = slower)
            float rotationSpeed = Mathf.Lerp(MIN_ROTATION_SPEED, MAX_ROTATION_SPEED,
                Mathf.InverseLerp(0, AIM_ASSIST_ANGLE_THRESHOLD, currentAngle));

            // Apply the rotation based on sticky factor
            rotationSpeed *= effectiveStickyFactor;

            // Calculate target rotation around Y axis only
            float targetYRotation = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
            float currentYRotation = playerTransform.eulerAngles.y;

            // Find shortest rotation path
            float angleDelta = Mathf.DeltaAngle(currentYRotation, targetYRotation);

            // Calculate rotation step
            float step = Mathf.Sign(angleDelta) * Mathf.Min(rotationSpeed * Time.deltaTime, Mathf.Abs(angleDelta));

            // Apply rotation
            Vector3 euler = playerTransform.eulerAngles;
            playerTransform.eulerAngles = new Vector3(euler.x, euler.y + step, euler.z);
        }

        /// <summary>
        /// Update references to player transform and camera
        /// </summary>
        private void UpdateReferences()
        {
            playerTransform = LACore.PlayerTransform;
            cameraTransform = LACore.CameraTransform;

            if (playerTransform != null)
            {
                playerController = playerTransform.GetComponent<GameNetcodeStuff.PlayerControllerB>();
            }
        }

        /// <summary>
        /// Generate a short snap sound for aim assist
        /// </summary>
        private AudioClip GenerateSnapSound()
        {
            int sampleRate = 44100;
            float duration = 0.07f;
            float frequency = 1500f;

            AudioClip clip = AudioClip.Create("SnapSound", (int)(sampleRate * duration), 1, sampleRate, false);

            float[] samples = new float[(int)(sampleRate * duration)];
            for (int i = 0; i < samples.Length; i++)
            {
                float t = (float)i / (float)samples.Length;
                float envelope = t * (1 - t) * 4f; // Envelope shape that rises and falls
                samples[i] = Mathf.Sin(2 * Mathf.PI * frequency * t) * envelope;
            }

            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// Toggle aim assist on/off and return the new state
        /// </summary>
        public bool ToggleEnabled()
        {
            isEnabled = !isEnabled;

            // If disabling, clear current target
            if (!isEnabled)
            {
                currentTarget = null;
                isAssisting = false;
            }

            return isEnabled;
        }

        /// <summary>
        /// Check if aim assist is currently enabled
        /// </summary>
        public bool IsEnabled()
        {
            return isEnabled;
        }

        /// <summary>
        /// Represents a potential aim assist target with calculated properties
        /// </summary>
        private class TargetInfo
        {
            public GameObject GameObject { get; set; }
            public Vector3 Position { get; set; }
            public float AngleToView { get; set; }
            public float Distance { get; set; }
            public int Priority { get; set; } // Higher number = higher priority
        }
    }
}