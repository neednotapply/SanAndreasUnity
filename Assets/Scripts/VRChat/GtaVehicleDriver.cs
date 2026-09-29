using UdonSharp;
using UnityEngine;
using UnityEngine.AI;
using VRC.SDKBase;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Owns the life of a vehicle's AI driver: seated, thrown out, walking back, seated again.
    ///
    /// A car whose driver has been dragged out should not keep driving itself, and the driver should not
    /// simply evaporate. Both read as broken. Instead the ejected driver waits for the thief to leave, walks
    /// back to their door and gets in, and the car only resumes once someone is actually at the wheel.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class GtaVehicleDriver : UdonSharpBehaviour
    {
        [Header("References")]
        public GtaVehicleController controller;

        [Tooltip("The ped that drives this vehicle.")]
        public GameObject driverPed;

        public Animator driverAnimator;

        [Tooltip("Agent used only while the driver is out of the car; disabled while seated.")]
        public NavMeshAgent driverAgent;

        [Tooltip("Where the driver sits, in vehicle space.")]
        public Transform seatPoint;

        [Header("Timing")]
        [Tooltip("Seconds the jacked animation plays before the driver picks themselves up.")]
        public float ejectDuration = 1.6f;

        [Tooltip("How close the driver must get to the seat before climbing back in.")]
        public float reseatDistance = 2.5f;

        [Tooltip("Given up on after this long trying to walk back.")]
        public float returnTimeout = 45f;

        // 0 seated and driving, 1 sprawled on the road, 2 walking back, 3 gone for good
        private int _state = 0;
        private float _returnTimer = 0f;

        /// <summary> True when someone is actually at the wheel. </summary>
        public bool HasDriver => _state == 0 && driverPed != null && driverPed.activeInHierarchy;

        /// <summary>
        /// Hauls the driver out. Called when a player takes the vehicle.
        /// </summary>
        public void Eject()
        {
            if (_state != 0 || driverPed == null)
                return;

            _state = 1;
            _returnTimer = 0f;

            if (driverAnimator != null)
                driverAnimator.SetTrigger("Jacked");

            // detach, or the ejected driver is dragged along by the car they were pulled out of
            driverPed.transform.SetParent(null, true);

            SendCustomEventDelayedSeconds(nameof(BeginReturn), ejectDuration);
        }

        /// <summary> Driver picks themselves up and starts heading back. </summary>
        public void BeginReturn()
        {
            if (_state != 1 || driverPed == null)
                return;

            _state = 2;

            if (driverAgent != null)
            {
                // the agent has to be warped onto the mesh; it was disabled and parented to a moving car,
                // so its internal position is meaningless by now
                NavMeshHit navHit;
                if (NavMesh.SamplePosition(driverPed.transform.position, out navHit, 8f, NavMesh.AllAreas))
                {
                    driverAgent.enabled = true;
                    driverAgent.Warp(navHit.position);
                }
                else
                {
                    // stranded off the mesh entirely - nothing sensible left to do
                    _state = 3;
                    driverPed.SetActive(false);
                }
            }
        }

        void Update()
        {
            if (_state != 2)
                return;

            if (!Networking.IsOwner(gameObject))
                return;

            if (driverPed == null || seatPoint == null)
                return;

            _returnTimer += Time.deltaTime;
            if (_returnTimer > returnTimeout)
            {
                // gave up; leave the car abandoned rather than teleporting a driver into it
                _state = 3;
                driverPed.SetActive(false);
                return;
            }

            // don't climb into a seat someone else is sitting in
            if (controller != null && controller.isPlayerDriven)
                return;

            Vector3 seatWorld = seatPoint.position;

            if (driverAgent != null && driverAgent.enabled && driverAgent.isOnNavMesh)
                driverAgent.SetDestination(seatWorld);

            if (Vector3.Distance(driverPed.transform.position, seatWorld) <= reseatDistance)
                Reseat();
        }

        /// <summary> Driver climbs back in and the car is drivable again. </summary>
        private void Reseat()
        {
            // The agent must be off before the ped is re-parented.
            //
            // A live agent keeps writing the transform every frame, so a ped parented into the seat is
            // immediately dragged back out and keeps playing its walking or stumbling motion in the driver's
            // seat - which reads as the driver falling over rather than sitting down.
            if (driverAgent != null)
            {
                if (driverAgent.enabled && driverAgent.isOnNavMesh)
                    driverAgent.ResetPath();

                driverAgent.enabled = false;
            }

            driverPed.transform.SetParent(seatPoint.parent, false);
            driverPed.transform.localPosition = seatPoint.localPosition;
            driverPed.transform.localRotation = Quaternion.identity;

            // Driven by a trigger rather than Rebind/Play, which are not dependably available in Udon.
            // The generated controller has a Jacked -> Seated transition on this trigger.
            //
            // Speed is zeroed as well: the seated pose lives in a blend tree driven by it, so a driver who
            // arrives still carrying walking speed sits there playing a walk cycle.
            if (driverAnimator != null)
            {
                driverAnimator.SetFloat("Speed", 0f);
                driverAnimator.SetTrigger("Reseat");
            }

            _state = 0;
        }
    }
}
