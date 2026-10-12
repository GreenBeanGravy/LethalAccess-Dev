using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace LethalAccess
{
    /// <summary>
    /// The Mineshaft's elevator: the only way between the main entrance's level and the mine
    /// 37 m below, and not on the NavMesh at all. When the path cannot reach the target any other
    /// way it is planned in two halves, one to the elevator's stop on this level and one from its
    /// stop on the other. Auto mode calls the car, steps in, presses the button, rides and steps
    /// out. Manual mode says each step in a few words, pings the way in and out, and turns the
    /// camera onto each button once the player is in front of it, so interact presses it.
    /// </summary>
    internal partial class Pathfinder
    {
        private enum ElevatorStage
        {
            None,
            Call,
            Wait,
            Enter,
            Press,
            Ride,
            Exit
        }

        private MineshaftElevatorController elevatorOnPath;
        private int elevatorCrossIndex = -1;
        private bool elevatorGoingDown;
        private bool elevatorBoarded;
        private bool elevatorRideDone;
        private ElevatorStage spokenElevatorStage;
        private float elevatorButtonTime = -999f;

        // Within this of the stop, the elevator takes over from the path.
        private const float ELEVATOR_ENGAGE_DISTANCE = 3f;
        // A button is pressed from this close, as a player reaches it.
        private const float ELEVATOR_BUTTON_REACH = 1.4f;

        private readonly Dictionary<MineshaftElevatorController, List<InteractTrigger>> elevatorButtons =
            new Dictionary<MineshaftElevatorController, List<InteractTrigger>>();

        private void ResetElevatorRoute()
        {
            elevatorOnPath = null;
            elevatorCrossIndex = -1;
            elevatorBoarded = false;
            elevatorRideDone = false;
            spokenElevatorStage = ElevatorStage.None;
        }

        /// <summary>Pathfinding stopped: a press on an earlier route says nothing about the next
        /// one (a route started just after a ride said "Elevator coming" with the car standing
        /// still). Replans keep it, or the call cue would repeat while the car is on its way.</summary>
        private void ForgetElevatorPress() => elevatorButtonTime = -100f;

        /// <summary>Riding or about to: a replan now would plan from inside a moving car.</summary>
        private bool ElevatorCrossingInProgress => elevatorOnPath != null && elevatorBoarded && !elevatorRideDone;

        /// <summary>Nodes past the stop are not reached until the ride is over.</summary>
        private bool HeldByElevator(PathNode node) =>
            elevatorOnPath != null && !elevatorRideDone && node.index >= elevatorCrossIndex - 1;

        /// <summary>
        /// Start to the elevator's stop on this level, and its other stop to the target, when both
        /// halves are complete paths.
        /// </summary>
        private bool TryElevatorRoute(Vector3 start, out Vector3[] toStop, out Vector3[] fromStop,
            out MineshaftElevatorController elevator, out bool down)
        {
            toStop = fromStop = null;
            elevator = null;
            down = false;
            foreach (var e in FindObjectsOfType<MineshaftElevatorController>())
            {
                if (e == null || e.elevatorTopPoint == null || e.elevatorBottomPoint == null)
                    continue;
                if (!NavMesh.SamplePosition(e.elevatorTopPoint.position, out NavMeshHit top, 2f, NavMesh.AllAreas) ||
                    !NavMesh.SamplePosition(e.elevatorBottomPoint.position, out NavMeshHit bottom, 2f, NavMesh.AllAreas))
                    continue;

                for (int side = 0; side < 2; side++)
                {
                    Vector3 near = side == 0 ? top.position : bottom.position;
                    Vector3 far = side == 0 ? bottom.position : top.position;
                    Vector3[] first = PlanCorners(start, near);
                    if (first == null)
                        continue;
                    Vector3[] second = PlanCorners(far, currentDestination);
                    if (second == null)
                        continue;
                    toStop = first;
                    fromStop = second;
                    elevator = e;
                    down = side == 0;
                    return true;
                }
            }
            return false;
        }

        /// <summary>The call button at a stop, or the button inside the car.</summary>
        private InteractTrigger ElevatorButton(MineshaftElevatorController e, bool inside, Vector3 near)
        {
            if (!elevatorButtons.TryGetValue(e, out List<InteractTrigger> buttons) || buttons.Exists(b => b == null))
            {
                buttons = new List<InteractTrigger>();
                foreach (var trigger in FindObjectsOfType<InteractTrigger>())
                {
                    for (int i = 0; i < trigger.onInteract.GetPersistentEventCount(); i++)
                    {
                        if (trigger.onInteract.GetPersistentTarget(i) == e)
                        {
                            buttons.Add(trigger);
                            break;
                        }
                    }
                }
                elevatorButtons[e] = buttons;
            }

            InteractTrigger best = null;
            float bestDistance = float.MaxValue;
            foreach (var button in buttons)
            {
                bool isInside = false;
                for (int i = 0; i < button.onInteract.GetPersistentEventCount(); i++)
                {
                    if (button.onInteract.GetPersistentTarget(i) == e && button.onInteract.GetPersistentMethodName(i) == "PressElevatorButton")
                        isInside = true;
                }
                if (isInside != inside)
                    continue;
                float distance = Vector3.Distance(button.transform.position, near);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = button;
                }
            }
            return best;
        }

        // The bounds reach the whole car, so they also hold a player standing in the empty shaft
        // with the car at the other level. In the car means the car's floor is at their feet too.
        private static bool InElevator(MineshaftElevatorController e, Vector3 feet) =>
            e.elevatorBounds != null && e.elevatorBounds.bounds.Contains(feet + Vector3.up * 0.5f) &&
            Mathf.Abs(e.elevatorInsidePoint.position.y - feet.y) < 1.5f;

        // The controller's own flags can't be trusted from here: elevatorIsAtBottom read false
        // with the car at the bottom, and elevatorFinishedMoving turns true 4 m before the stop.
        // The car's height and whether it is still moving can.
        private float elevatorCarY;
        private float elevatorCarStillSince;
        private float elevatorCarTrackedAt = -100f;

        private void TrackElevatorCar(MineshaftElevatorController e)
        {
            float y = e.elevatorPoint.position.y;
            // Not watched since an earlier route: the height kept from then is no reading of
            // movement (the car was left at the other level, and read as just arriving). Assume
            // still; a car that is moving shows it next frame.
            if (Time.time - elevatorCarTrackedAt > 0.5f)
                elevatorCarStillSince = Time.time - 1f;
            else if (Mathf.Abs(y - elevatorCarY) > 0.01f)
                elevatorCarStillSince = Time.time;
            elevatorCarY = y;
            elevatorCarTrackedAt = Time.time;
        }

        private bool CarStill => Time.time - elevatorCarStillSince > 0.3f;

        /// <summary>Beside the car or in it, at either level.</summary>
        private bool NearElevator() =>
            elevatorOnPath != null && elevatorOnPath.elevatorInsidePoint != null &&
            HorizontalDistance(transform.position, elevatorOnPath.elevatorInsidePoint.position) < 4.5f;

        private bool CarStoppedAt(Vector3 stop) => CarStill && Mathf.Abs(elevatorCarY - stop.y) < 1f;

        /// <summary>
        /// Where the crossing stands. goal is where to walk (or look, for a button), button the
        /// button to press now, if any.
        /// </summary>
        private ElevatorStage UpdateElevator(out Vector3 goal, out InteractTrigger button)
        {
            goal = default;
            button = null;
            MineshaftElevatorController e = elevatorOnPath;
            if (e == null || elevatorRideDone || currentTargetNode == null || currentTargetNode.index < elevatorCrossIndex - 1)
                return ElevatorStage.None;

            bool boardAtBottom = !elevatorGoingDown;
            Vector3 nearStop = (boardAtBottom ? e.elevatorBottomPoint : e.elevatorTopPoint).position;
            Vector3 farStop = (boardAtBottom ? e.elevatorTopPoint : e.elevatorBottomPoint).position;
            TrackElevatorCar(e);
            bool inside = InElevator(e, transform.position);
            if (inside)
                elevatorBoarded = true;

            if (elevatorBoarded)
            {
                if (inside)
                {
                    if (CarStoppedAt(farStop) && e.elevatorDoorOpen)
                    {
                        goal = farStop;
                        return ElevatorStage.Exit;
                    }
                    // Still at this stop with the door open: press. Once pressed the door shuts
                    // and the car waits a moment before it moves; pressing again then would send
                    // it back.
                    if (CarStoppedAt(nearStop) && e.elevatorDoorOpen)
                    {
                        button = ElevatorButton(e, inside: true, transform.position);
                        goal = button != null ? button.transform.position : e.elevatorInsidePoint.position;
                        return ElevatorStage.Press;
                    }
                    return ElevatorStage.Ride;
                }

                // Out on the far level: carry on along the second half.
                if (Mathf.Abs(transform.position.y - farStop.y) < 3f)
                {
                    elevatorRideDone = true;
                    if (elevatorCrossIndex - 1 >= 0 && elevatorCrossIndex - 1 < pathNodes.Count)
                        MarkReachedThrough(pathNodes[elevatorCrossIndex - 1], announce: false);
                    FindNextTargetNode();
                    ResetStuckTracking();
                    return ElevatorStage.None;
                }
                // Stepped back out where they got in: start again.
                elevatorBoarded = false;
            }

            if (HorizontalDistance(transform.position, nearStop) > ELEVATOR_ENGAGE_DISTANCE ||
                Mathf.Abs(transform.position.y - nearStop.y) > 3f)
                return ElevatorStage.None;

            if (CarStoppedAt(nearStop) && e.elevatorDoorOpen)
            {
                goal = e.elevatorInsidePoint.position;
                return ElevatorStage.Enter;
            }
            // Called: the game clears elevatorCalled the moment the car sets off, and the car then
            // stands still while its door shuts. elevatorFinishedMoving is false from then on.
            if (!CarStill || e.elevatorCalled || !e.elevatorFinishedMoving || CarStoppedAt(nearStop) ||
                Time.time - elevatorButtonTime < 6f)
            {
                goal = nearStop;
                return ElevatorStage.Wait;
            }
            button = ElevatorButton(e, inside: false, nearStop);
            goal = button != null ? button.transform.position : nearStop;
            return ElevatorStage.Call;
        }

        private void PressElevatorButton(InteractTrigger button)
        {
            if (button == null || Time.time - elevatorButtonTime < 6f || !CanUseTrigger(button))
                return;
            elevatorButtonTime = Time.time;
            button.Interact(playerController.thisPlayerBody);
            Debug.Log($"Pathfinder: pressed elevator button {button.transform.parent?.name} at {button.transform.position}");
        }

        /// <summary>Auto mode's side of the crossing. True while it has the controls.</summary>
        private bool UpdateAutoElevator(Transform body)
        {
            ElevatorStage stage = UpdateElevator(out Vector3 goal, out InteractTrigger button);
            if (stage == ElevatorStage.None)
                return false;

            ResetStuckTracking();
            AutoSprintInput = false;
            Vector3 to = goal - transform.position;
            to.y = 0f;
            switch (stage)
            {
                case ElevatorStage.Call:
                case ElevatorStage.Press:
                    if (to.magnitude > ELEVATOR_BUTTON_REACH)
                    {
                        CenterCamera(body);
                        TurnBodyToward(body, to.normalized);
                        AutoMoveInput = InputToward(body, to, Mathf.Clamp(to.magnitude, 0.4f, 1f));
                        return true;
                    }
                    ClearAutoInput();
                    Utilities.LookAtPoint(goal);
                    PressElevatorButton(button);
                    return true;

                case ElevatorStage.Enter:
                case ElevatorStage.Exit:
                    CenterCamera(body);
                    if (to.magnitude < 0.3f)
                    {
                        ClearAutoInput();
                        return true;
                    }
                    TurnBodyToward(body, to.normalized);
                    AutoMoveInput = InputToward(body, to, Mathf.Clamp(to.magnitude, 0.5f, 1f));
                    return true;

                default:
                    // Waiting for the car, or riding it.
                    ClearAutoInput();
                    return true;
            }
        }

        /// <summary>Manual mode: say each step once, and aim the camera at a button the player is
        /// standing in front of.</summary>
        private void UpdateManualElevator()
        {
            ElevatorStage stage = UpdateElevator(out Vector3 goal, out InteractTrigger button);
            if (stage == ElevatorStage.None)
            {
                spokenElevatorStage = ElevatorStage.None;
                return;
            }
            ResetStuckTracking();

            if ((stage == ElevatorStage.Call || stage == ElevatorStage.Press) && button != null &&
                HorizontalDistance(transform.position, goal) <= ELEVATOR_BUTTON_REACH)
                Utilities.LookAtPoint(goal);

            if (stage == spokenElevatorStage)
                return;
            spokenElevatorStage = stage;
            switch (stage)
            {
                case ElevatorStage.Call:
                    Utilities.SpeakText($"Elevator. Call button {DescribeDirection(goal)}, press interact there.");
                    break;
                case ElevatorStage.Wait:
                    Utilities.SpeakText("Elevator coming");
                    break;
                case ElevatorStage.Enter:
                    Utilities.SpeakText($"Elevator here. Step in, {DescribeDirection(goal)}.");
                    break;
                case ElevatorStage.Press:
                    Utilities.SpeakText(elevatorGoingDown ? "Press interact to go down" : "Press interact to go up");
                    break;
                case ElevatorStage.Ride:
                    Utilities.SpeakText(elevatorGoingDown ? "Going down" : "Going up");
                    break;
                case ElevatorStage.Exit:
                    Utilities.SpeakText($"Step out, {DescribeDirection(goal)}");
                    break;
            }
        }

        /// <summary>Where manual mode's ping leads during the crossing: the button, the car, the
        /// way out.</summary>
        private bool ManualElevatorGuide(out Vector3 guide)
        {
            // While the car is on its way the ping stays outside the doors: the next path node is
            // in the car, and walking toward it walks into the empty shaft.
            ElevatorStage stage = UpdateElevator(out guide, out _);
            return stage != ElevatorStage.None && stage != ElevatorStage.Ride;
        }
    }
}
