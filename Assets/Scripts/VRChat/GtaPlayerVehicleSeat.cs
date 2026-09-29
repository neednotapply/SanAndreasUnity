using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;
// VRCStation exists in both VRC.SDK3.Components and VRC.SDKBase; alias the SDK3 one explicitly, as the
// SDK's own editor scripts do, otherwise the reference is ambiguous
using VRCStation = VRC.SDK3.Components.VRCStation;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Lets a player steal and drive a vehicle.
    ///
    /// The player is seated in a VRCStation and their movement input is redirected into the same
    /// throttle/steer/brake fields the traffic AI writes. Because both drive
    /// <see cref="GtaVehicleController"/> through identical inputs, a stolen car behaves exactly as it did
    /// under AI control - there is no separate player-vehicle physics path to diverge.
    ///
    /// Ownership transfers to the driver, so their client simulates the vehicle they are driving.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class GtaPlayerVehicleSeat : UdonSharpBehaviour
    {
        [Header("References")]
        public GtaVehicleController controller;
        public GtaTrafficVehicle aiDriver;
        public VRCStation station;

        [Header("Occupants")]
        [Tooltip("The posed ped in the driver's seat. Thrown out when a player takes the wheel.")]
        public GameObject driverOccupant;

        [Tooltip("The posed passenger, if this vehicle has one. Bails out alongside the driver.")]
        public GameObject passengerOccupant;

        public Animator driverAnimator;
        public Animator passengerAnimator;

        [Tooltip("Owns the driver's eject/return cycle, so a jacked car does not drive itself away.")]
        public GtaVehicleDriver vehicleDriver;

        [Tooltip("Told when the player gets in or out, so the radio knows to play.")]
        public GtaRadio radio;

        [Tooltip("Body of a parked car, left kinematic until someone takes it.")]
        public Rigidbody parkedBody;

        [Tooltip("Receives input for whichever seat the player is in. Shared by every vehicle.")]
        public GtaVehicleInput input;

        [Tooltip("Seconds the jacked animation is given to play before the occupant is hidden.")]
        public float ejectDuration = 1.6f;

        [Header("Driving feel")]
        [Tooltip("Brake when reversing input is held while moving forward, rather than instantly reversing.")]
        public bool brakeBeforeReverse = true;

        [Header("Diagnostics")]
        [Tooltip("Logs each step of the enter/drive chain so a failure can be located precisely.")]
        public bool debugLogging = false;

        private bool _isDriving = false;
        private float _throttle = 0f;
        private float _steer = 0f;
        private bool _loggedFirstInput = false;

        /// <summary> True while the local player is at the wheel. </summary>
        public bool IsDriving => _isDriving;

        /// <summary>
        /// Entering the vehicle. VRChat raises Interact when the player uses the object's collider.
        /// </summary>
        public override void Interact()
        {
            if (debugLogging)
                Debug.Log($"[VehicleSeat] Interact on {gameObject.name} " +
                    $"(station={(station != null)}, controller={(controller != null)})");

            if (station == null || controller == null)
            {
                if (debugLogging)
                    Debug.LogWarning("[VehicleSeat] Missing station or controller - cannot seat player");
                return;
            }

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid())
                return;

            // the driver must own the vehicle, or their inputs would be simulated on someone else's client
            if (!Networking.IsOwner(localPlayer, gameObject))
                Networking.SetOwner(localPlayer, gameObject);

            if (controller.gameObject != null && !Networking.IsOwner(localPlayer, controller.gameObject))
                Networking.SetOwner(localPlayer, controller.gameObject);

            station.UseStation(localPlayer);
        }

        public override void OnStationEntered(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal)
                return;

            _isDriving = true;

            // claim input, so keypresses reach this car and no other
            if (input != null)
                input.SetActiveSeat(this);
            _throttle = 0f;
            _steer = 0f;
            _loggedFirstInput = false;

            if (radio != null)
                radio.OnEnteredVehicle();

            // A parked car sits kinematic until it is taken.
            //
            // Hundreds of them are placed at the coordinates the game parks them at, and a few of those sit
            // slightly inside a kerb or a wall. Leaving them all dynamic means every one settles - or
            // launches - the moment its cell loads. Waking only the one being stolen avoids that entirely.
            if (parkedBody != null)
                parkedBody.isKinematic = false;

            if (debugLogging)
                Debug.Log($"[VehicleSeat] OnStationEntered on {gameObject.name} - now driving. " +
                    $"Owner={Networking.IsOwner(gameObject)}");

            if (controller != null)
            {
                // stand the AI down; it checks this flag and stops writing inputs
                controller.isPlayerDriven = true;
                controller.ClearInputs();
            }

            EjectOccupants();
        }

        /// <summary>
        /// Throws whoever was in the car out of it.
        ///
        /// The occupant plays the game's own jacked animation, is unparented so they don't ride along in a
        /// car they are supposed to have been pulled out of, and is hidden once the clip has played. A
        /// driver who simply vanished the instant the player sat down would read as a bug rather than a
        /// carjacking.
        /// </summary>
        private void EjectOccupants()
        {
            bool ejected = false;

            // the driver is handled by GtaVehicleDriver, which also walks them back afterwards
            if (vehicleDriver != null)
                vehicleDriver.Eject();
            else if (driverOccupant != null && driverOccupant.activeSelf)
            {
                if (driverAnimator != null)
                    driverAnimator.SetTrigger("Jacked");

                driverOccupant.transform.SetParent(null, true);
                ejected = true;
            }

            if (passengerOccupant != null && passengerOccupant.activeSelf)
            {
                if (passengerAnimator != null)
                    passengerAnimator.SetTrigger("Jacked");

                passengerOccupant.transform.SetParent(null, true);
                ejected = true;
            }

            if (ejected)
                SendCustomEventDelayedSeconds(nameof(HideEjectedOccupants), ejectDuration);
        }

        /// <summary> Hides ejected occupants once their animation has had time to play. </summary>
        public void HideEjectedOccupants()
        {
            // only the passenger is hidden; the driver walks back to the car instead of vanishing
            if (passengerOccupant != null)
                passengerOccupant.SetActive(false);
        }

        public override void OnStationExited(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal)
                return;

            _isDriving = false;

            if (input != null)
                input.ClearActiveSeat(this);
            _throttle = 0f;
            _steer = 0f;

            if (radio != null)
                radio.OnExitedVehicle();

            if (controller != null)
            {
                controller.ClearInputs();
                // hand the car back to the AI, which re-acquires the road on its next update
                controller.isPlayerDriven = false;
            }
        }

        // Input arrives from GtaVehicleInput rather than being handled here.
        //
        // VRChat sends input events to every behaviour that implements them, so a handler on each seat
        // meant every parked car in the world processed every keypress. These are plain methods now, and
        // only the seat the player is actually in is ever called.

        public void SetThrottle(float value)
        {
            if (_isDriving)
                _throttle = value;
        }

        public void SetSteer(float value)
        {
            if (_isDriving)
                _steer = value;
        }

        public void SetHandbrake(bool held)
        {
            if (_isDriving && controller != null)
                controller.brakeInput = held ? 1f : 0f;
        }

        /// <summary> Gets the player out of the car. </summary>
        public void LeaveVehicle()
        {
            if (!_isDriving || station == null)
                return;

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid())
                return;

            station.ExitStation(localPlayer);
        }

        void Update()
        {
            if (!_isDriving || controller == null)
                return;

            controller.steerInput = _steer;

            if (brakeBeforeReverse && _throttle < -0.01f && controller.CurrentSpeed > 1f)
            {
                // pulling back while still rolling forward brakes first, as in the game, instead of
                // slamming straight into reverse
                controller.throttleInput = 0f;
                controller.brakeInput = -_throttle;
            }
            else
            {
                controller.throttleInput = _throttle;

                // don't fight an explicitly held handbrake
                if (controller.brakeInput > 0f && Mathf.Abs(_throttle) > 0.01f)
                    controller.brakeInput = 0f;
            }
        }
    }
}
