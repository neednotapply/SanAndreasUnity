using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// AI driver for a traffic vehicle.
    ///
    /// This does not move the vehicle. It decides where to go along GTA's road graph and writes
    /// throttle/steer/brake into <see cref="GtaVehicleController"/> - exactly the inputs a player uses.
    /// That separation is what allows a player to take over a car the AI was driving: the AI simply stops
    /// writing inputs and the physics is unchanged.
    ///
    /// Vehicles follow VEHICLE path nodes rather than the nav mesh, which is what keeps them on roads.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class GtaTrafficVehicle : UdonSharpBehaviour
    {
        [Header("References")]
        public GtaPathNetworkData pathNetwork;
        public GtaVehicleController controller;
        public GtaTrafficLightController trafficLights;

        [Tooltip("Driver of this vehicle. A car with nobody at the wheel does not drive itself.")]
        public GtaVehicleDriver vehicleDriver;

        [Header("Traffic lights")]
        [Tooltip("How far before a red light the vehicle begins stopping.")]
        public float lightStopDistance = 12f;

        [Header("Driving")]
        [Tooltip("How close to a node counts as reaching it.")]
        public float arriveDistance = 6f;

        [Tooltip("GTA drives on the right. Lane offset as a fraction of the node's path width.")]
        public float laneOffsetFraction = 0.25f;

        [Tooltip("Steering response. Higher turns in more sharply toward the racing line.")]
        public float steerGain = 2.5f;

        [Header("Obstacles")]
        [Tooltip("How far ahead to look for something to stop for.")]
        public float lookAheadDistance = 12f;
        public LayerMask obstacleMask = ~0;

        [Header("Cruise")]
        [Tooltip("Fraction of the vehicle's top speed that traffic actually drives at. Cars flat out " +
            "cannot take corners on the node graph.")]
        [Range(0.1f, 1f)] public float cruiseSpeedFraction = 0.45f;

        [Header("Stuck recovery")]
        [Tooltip("Below this speed while trying to drive counts as stuck.")]
        public float stuckSpeedThreshold = 0.6f;

        [Tooltip("Seconds stuck before attempting to reverse out.")]
        public float stuckTimeBeforeReverse = 2.5f;

        [Tooltip("Seconds spent reversing before trying to rejoin the road.")]
        public float reverseDuration = 1.5f;

        [Header("Overtaking")]
        [Tooltip("Seconds blocked before trying to pull around whatever is in the way. Normal queues clear " +
            "well inside this, so only genuinely stopped obstacles - an abandoned car - get overtaken.")]
        public float blockedTimeBeforeOvertake = 3f;

        [Tooltip("How hard to steer out while passing a stopped obstacle.")]
        public float overtakeSteer = 0.55f;

        [Tooltip("Seconds spent committed to a pass once started.")]
        public float overtakeDuration = 2.5f;

        private float _blockedTimer = 0f;
        private float _overtakeTimer = 0f;

        private float _stuckTimer = 0f;
        private float _reverseTimer = 0f;

        [UdonSynced] private Vector3 _syncedPosition;
        [UdonSynced] private float _syncedYaw;

        private int _currentNode = -1;
        private int _previousNode = -1;
        private Vector3 _targetPoint;
        private bool _hasTarget = false;

        private Vector3 _remotePosition;
        private float _remoteYaw;
        private bool _hasRemoteSample = false;

        private const float SyncIntervalSeconds = 0.2f;
        private float _timeSinceSync = 0f;

        void Start()
        {
            _remotePosition = transform.position;
            _remoteYaw = transform.eulerAngles.y;
        }

        /// <summary>
        /// Re-acquire the road whenever the pool drops this vehicle somewhere new. Start() only runs once,
        /// so without this a recycled vehicle would steer toward wherever it was last driving.
        /// </summary>
        void OnEnable()
        {
            _hasTarget = false;
            _currentNode = -1;
            _previousNode = -1;
            // a recycled vehicle must not inherit the previous one's recovery state
            _stuckTimer = 0f;
            _reverseTimer = 0f;

            if (controller != null)
                controller.ClearInputs();

            AcquireStartNode();
        }

        void Update()
        {
            if (Networking.IsOwner(gameObject))
                UpdateAsOwner();
            else
                UpdateAsRemote();
        }

        private void UpdateAsOwner()
        {
            if (controller == null || pathNetwork == null)
                return;

            // a player in the driving seat overrides the AI entirely
            if (controller.isPlayerDriven)
                return;

            // A car whose driver has been pulled out must not keep steering itself down the road. It rolls
            // to a stop and stays put until the driver climbs back in.
            if (vehicleDriver != null && !vehicleDriver.HasDriver)
            {
                controller.throttleInput = 0f;
                controller.brakeInput = 1f;
                controller.steerInput = 0f;
                return;
            }

            if (!_hasTarget)
            {
                AcquireStartNode();
                if (!_hasTarget)
                {
                    controller.ClearInputs();
                    return;
                }
            }

            DriveTowardTarget();

            _timeSinceSync += Time.deltaTime;
            if (_timeSinceSync >= SyncIntervalSeconds)
            {
                _timeSinceSync = 0f;
                _syncedPosition = transform.position;
                _syncedYaw = transform.eulerAngles.y;
                RequestSerialization();
            }
        }

        private void UpdateAsRemote()
        {
            if (!_hasRemoteSample)
                return;

            transform.position = Vector3.Lerp(transform.position, _remotePosition, Time.deltaTime * 8f);
            float yaw = Mathf.LerpAngle(transform.eulerAngles.y, _remoteYaw, Time.deltaTime * 8f);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        public override void OnDeserialization()
        {
            _remotePosition = _syncedPosition;
            _remoteYaw = _syncedYaw;
            _hasRemoteSample = true;
        }

        private void DriveTowardTarget()
        {
            // A collision can leave a car facing a wall with its route on the far side of it. Without a
            // recovery it sits there forever holding the throttle, and everything behind it queues up.
            if (UpdateStuckRecovery())
                return;

            Vector3 toTarget = _targetPoint - transform.position;
            toTarget.y = 0f;

            float distance = toTarget.magnitude;

            if (distance <= arriveDistance)
            {
                AdvanceToNextNode();
                return;
            }

            Vector3 desired = toTarget / distance;

            // steering is the signed angle to the target, so the car turns toward the road rather than
            // snapping onto it
            float angle = Vector3.SignedAngle(transform.forward, desired, Vector3.up);
            float steer = Mathf.Clamp(angle / 45f * steerGain, -1f, 1f);

            controller.steerInput = steer;

            bool blocked = IsBlockedAhead();
            float turnSharpness = Mathf.Abs(angle) / 90f;

            // hold at a red light, but only once close enough that stopping makes sense - braking for a
            // light two junctions away would gridlock the whole street
            if (!blocked && distance <= lightStopDistance && IsStoppedByLight())
            {
                controller.throttleInput = 0f;
                controller.brakeInput = 1f;
                return;
            }

            // Track how long the way has been blocked. A queue at a junction clears in a second or two;
            // an abandoned car never does, and sitting behind it forever backs up the whole street.
            if (blocked)
                _blockedTimer += Time.deltaTime;
            else
                _blockedTimer = 0f;

            if (_overtakeTimer > 0f)
                _overtakeTimer -= Time.deltaTime;
            else if (blocked && _blockedTimer >= blockedTimeBeforeOvertake)
                _overtakeTimer = overtakeDuration;

            bool overtaking = _overtakeTimer > 0f;

            if (overtaking)
            {
                // commit to pulling out and easing past, rather than nudging and re-blocking each frame
                controller.steerInput = Mathf.Clamp(steer + overtakeSteer, -1f, 1f);
                controller.throttleInput = 0.4f;
                controller.brakeInput = 0f;
            }
            else if (blocked)
            {
                // something in the way - stop rather than push through it
                controller.throttleInput = 0f;
                controller.brakeInput = 1f;
            }
            else if (turnSharpness > 0.6f)
            {
                // ease off for tight corners, otherwise the car understeers wide across the junction
                controller.throttleInput = 0.35f;
                controller.brakeInput = 0f;
            }
            else
            {
                controller.throttleInput = 1f - (turnSharpness * 0.5f);
                controller.brakeInput = 0f;
            }

            // Traffic must not drive flat out - a car at top speed cannot make the turns the node graph
            // asks of it, which is what sent them off the road and through fences.
            if (controller.SpeedFraction > cruiseSpeedFraction && controller.throttleInput > 0f)
                controller.throttleInput = 0f;
        }

        /// <summary>
        /// Detects a vehicle that is trying to drive but going nowhere - wedged against a wall, another
        /// car, or facing the wrong way after a shunt - and backs it out, then re-acquires the road.
        /// Returns true while recovery is in progress and normal driving should be skipped.
        /// </summary>
        private bool UpdateStuckRecovery()
        {
            float dt = Time.deltaTime;

            if (_reverseTimer > 0f)
            {
                _reverseTimer -= dt;

                controller.throttleInput = -0.6f;
                controller.brakeInput = 0f;
                // steer hard while backing up so the car ends up pointing somewhere new
                controller.steerInput = 1f;

                if (_reverseTimer <= 0f)
                {
                    _stuckTimer = 0f;
                    // the old route is probably unreachable from here - find the road again
                    AcquireStartNode();
                }

                return true;
            }

            bool tryingToMove = controller.throttleInput > 0.05f;
            bool barelyMoving = Mathf.Abs(controller.CurrentSpeed) < stuckSpeedThreshold;

            if (tryingToMove && barelyMoving)
            {
                _stuckTimer += dt;

                if (_stuckTimer >= stuckTimeBeforeReverse)
                {
                    _reverseTimer = reverseDuration;
                    return true;
                }
            }
            else
            {
                _stuckTimer = 0f;
            }

            return false;
        }

        private void AcquireStartNode()
        {
            if (pathNetwork == null)
                return;

            _currentNode = pathNetwork.FindNearestNode(transform.position, false, 1);
            _previousNode = -1;

            if (_currentNode < 0)
            {
                _hasTarget = false;
                return;
            }

            AdvanceToNextNode();
        }

        private void AdvanceToNextNode()
        {
            if (pathNetwork == null || _currentNode < 0)
            {
                _hasTarget = false;
                return;
            }

            int next = PickNextRoadNode(_currentNode, _previousNode);

            if (next < 0)
            {
                next = _previousNode;
                if (next < 0)
                {
                    _hasTarget = false;
                    return;
                }
            }

            _previousNode = _currentNode;
            _currentNode = next;
            _targetPoint = ComputeLanePoint(_previousNode, _currentNode);
            _hasTarget = true;
        }

        /// <summary>
        /// Prefers carrying straight on. Without this vehicles U-turn at every junction and all pick the
        /// same route through it.
        /// </summary>
        private int PickNextRoadNode(int fromNode, int cameFromNode)
        {
            int linkCount = pathNetwork.GetLinkCount(fromNode);
            if (linkCount <= 0)
                return -1;

            Vector3 fromPosition = pathNetwork.GetNodePosition(fromNode);

            Vector3 incoming = transform.forward;
            if (cameFromNode >= 0)
            {
                Vector3 delta = fromPosition - pathNetwork.GetNodePosition(cameFromNode);
                delta.y = 0f;
                if (delta.sqrMagnitude > 0.0001f)
                    incoming = delta.normalized;
            }

            int bestNode = -1;
            float bestScore = 0f;

            for (int i = 0; i < linkCount; i++)
            {
                int candidate = pathNetwork.GetLinkedNode(fromNode, i);

                if (candidate < 0 || candidate == cameFromNode)
                    continue;
                if (pathNetwork.nodeIsPedNode[candidate])
                    continue;
                if (pathNetwork.nodeIsWater[candidate])
                    continue;

                Vector3 outgoing = pathNetwork.GetNodePosition(candidate) - fromPosition;
                outgoing.y = 0f;
                if (outgoing.sqrMagnitude < 0.0001f)
                    continue;

                float score = Vector3.Dot(incoming, outgoing.normalized) + Random.Range(0f, 0.35f);

                if (bestNode < 0 || score > bestScore)
                {
                    bestNode = candidate;
                    bestScore = score;
                }
            }

            return bestNode;
        }

        /// <summary>
        /// Offsets the target into the right-hand lane using the node's own width, so opposing traffic
        /// doesn't drive down the centre line of the road.
        /// </summary>
        private Vector3 ComputeLanePoint(int fromNode, int toNode)
        {
            Vector3 target = pathNetwork.GetNodePosition(toNode);

            Vector3 direction = target - pathNetwork.GetNodePosition(fromNode);
            direction.y = 0f;

            if (direction.sqrMagnitude < 0.0001f)
                return target;

            direction = direction.normalized;
            Vector3 right = new Vector3(direction.z, 0f, -direction.x);

            float width = pathNetwork.nodePathWidth[toNode];
            return target + right * (width * laneOffsetFraction);
        }

        /// <summary>
        /// True when the node being approached is governed by a light phase that isn't currently green.
        /// </summary>
        private bool IsStoppedByLight()
        {
            if (trafficLights == null || pathNetwork == null)
                return false;

            if (_currentNode < 0)
                return false;

            int[] directions = pathNetwork.nodeTrafficLightDirection;
            if (directions == null || _currentNode >= directions.Length)
                return false;

            return !trafficLights.CanProceed(directions[_currentNode]);
        }

        private bool IsBlockedAhead()
        {
            Vector3 origin = transform.position + Vector3.up * 0.7f;

            RaycastHit hit;
            if (Physics.Raycast(origin, transform.forward, out hit, lookAheadDistance, obstacleMask))
            {
                if (!hit.transform.IsChildOf(transform))
                    return true;
            }

            return false;
        }
    }
}
