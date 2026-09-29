using UdonSharp;
using UnityEngine;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Drives one vehicle using its own handling data from GTA's handling.cfg, so a Rhino, an Infernus and
    /// a Bobcat genuinely behave differently rather than sharing one generic car model.
    ///
    /// This class is only the vehicle: it converts throttle/steer/brake inputs into motion. It does not
    /// decide where to go - traffic AI and the player both write the same three inputs, which is what lets
    /// a player take over a car the AI was driving.
    ///
    /// Unity's WheelCollider is not usable from Udon, so this is a deliberately simple mass/traction model
    /// rather than a suspension simulation: forward drive, speed-scaled steering, and lateral grip that
    /// bleeds off sideways velocity.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GtaVehicleController : UdonSharpBehaviour
    {
        [Header("Handling (populated at export from handling.cfg)")]
        [Tooltip("Vehicle mass in kg.")]
        public float vehicleMass = 1400f;

        [Tooltip("Top speed, derived from the transmission's max velocity.")]
        public float maxSpeed = 30f;

        [Tooltip("Engine acceleration - how hard it pulls away.")]
        public float engineAccel = 10f;

        [Tooltip("Braking deceleration.")]
        public float brakeDecel = 15f;

        [Tooltip("Maximum steering angle in degrees at low speed.")]
        public float steeringLock = 35f;

        [Tooltip("Grip. Higher resists sliding; lower slides more readily.")]
        public float tractionMult = 0.85f;

        [Tooltip("Aerodynamic/rolling drag.")]
        public float dragCoefficient = 0.02f;

        [Header("Inputs (written by the AI driver or the player)")]
        [Range(-1f, 1f)] public float throttleInput = 0f;
        [Range(-1f, 1f)] public float steerInput = 0f;
        [Range(0f, 1f)] public float brakeInput = 0f;

        [Header("Runtime")]
        public bool isPlayerDriven = false;

        private Rigidbody _rigidbody;
        private float _currentSpeed = 0f;

        /// <summary> Signed forward speed in m/s. Negative when reversing. </summary>
        public float CurrentSpeed => _currentSpeed;

        /// <summary> Speed as a fraction of this vehicle's top speed, for animation or audio. </summary>
        public float SpeedFraction
        {
            get
            {
                if (maxSpeed <= 0.01f)
                    return 0f;

                float f = Mathf.Abs(_currentSpeed) / maxSpeed;
                return f > 1f ? 1f : f;
            }
        }

        void Start()
        {
            _rigidbody = (Rigidbody)GetComponent(typeof(Rigidbody));

            if (_rigidbody != null)
            {
                _rigidbody.mass = vehicleMass;
                // keep the centre of mass low or the vehicle rolls at the slightest provocation
                _rigidbody.centerOfMass = new Vector3(0f, -0.4f, 0f);
            }
        }

        void FixedUpdate()
        {
            if (_rigidbody == null)
                return;

            float dt = Time.fixedDeltaTime;

            Vector3 velocity = _rigidbody.velocity;
            Vector3 forward = transform.forward;

            _currentSpeed = Vector3.Dot(velocity, forward);

            ApplyDrive(dt, forward);
            ApplySteering(dt);
            ApplyTraction(dt);
        }

        private void ApplyDrive(float dt, Vector3 forward)
        {
            float targetAccel = 0f;

            if (brakeInput > 0.01f)
            {
                // brake opposes whichever way we're actually moving
                float direction = _currentSpeed > 0f ? -1f : 1f;
                targetAccel = direction * brakeDecel * brakeInput;

                // don't let braking drag the car backwards through zero
                if (Mathf.Abs(_currentSpeed) < 0.5f)
                {
                    _rigidbody.velocity = new Vector3(0f, _rigidbody.velocity.y, 0f);
                    targetAccel = 0f;
                }
            }
            else if (Mathf.Abs(throttleInput) > 0.01f)
            {
                // engines pull much harder off the line than at top speed
                float speedFactor = 1f - SpeedFraction;
                if (speedFactor < 0.1f)
                    speedFactor = 0.1f;

                targetAccel = throttleInput * engineAccel * speedFactor;

                // refuse to exceed this vehicle's top speed
                if (throttleInput > 0f && _currentSpeed >= maxSpeed)
                    targetAccel = 0f;
                else if (throttleInput < 0f && _currentSpeed <= -maxSpeed * 0.3f)
                    targetAccel = 0f; // reverse is slower, as in the game
            }
            else
            {
                // coasting: rolling resistance
                targetAccel = -_currentSpeed * dragCoefficient * 10f;
            }

            _rigidbody.AddForce(forward * (targetAccel * vehicleMass), ForceMode.Force);
        }

        private void ApplySteering(float dt)
        {
            if (Mathf.Abs(steerInput) < 0.01f)
                return;

            // A car cannot turn while stationary, and turning authority falls off with speed - without
            // this vehicles pirouette on the spot and twitch violently at motorway speed.
            float speedFactor = Mathf.Abs(_currentSpeed) / 12f;
            if (speedFactor > 1f)
                speedFactor = 1f;

            if (speedFactor < 0.02f)
                return;

            float steerAngle = steerInput * steeringLock * speedFactor;

            // reverse steering inverts, like a real car
            if (_currentSpeed < -0.5f)
                steerAngle = -steerAngle;

            Quaternion turn = Quaternion.Euler(0f, steerAngle * dt, 0f);
            _rigidbody.MoveRotation(_rigidbody.rotation * turn);
        }

        private void ApplyTraction(float dt)
        {
            Vector3 velocity = _rigidbody.velocity;
            Vector3 right = transform.right;

            float lateralSpeed = Vector3.Dot(velocity, right);

            // bleed off sideways motion so the vehicle follows its nose instead of drifting like ice
            Vector3 correction = right * (-lateralSpeed * tractionMult);
            _rigidbody.AddForce(correction * vehicleMass, ForceMode.Force);
        }

        /// <summary> Clears all inputs - used when a driver leaves or the vehicle is recycled. </summary>
        public void ClearInputs()
        {
            throttleInput = 0f;
            steerInput = 0f;
            brakeInput = 0f;
        }
    }
}
