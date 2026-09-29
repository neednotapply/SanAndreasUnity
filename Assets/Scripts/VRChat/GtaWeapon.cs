using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// A weapon the player can pick up and fire.
    ///
    /// Built on VRCPickup rather than a custom inventory: picking a gun up off the ground and holding it is
    /// the interaction VRChat players already know, and it works the same in VR and on desktop without a
    /// separate control path for each.
    ///
    /// Firing is a raycast. Udon cannot instantiate projectiles, and for hitscan weapons - which is nearly
    /// all of GTA's - a ray is what the original does anyway.
    ///
    /// Statistics come from the exported weapon.dat, so a Desert Eagle and a Tec-9 differ by data rather
    /// than by having their own behaviours.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GtaWeapon : UdonSharpBehaviour
    {
        [Header("Statistics (from weapon.dat)")]
        public string modelName = "";

        [Tooltip("Damage per hit.")]
        public int damage = 25;

        [Tooltip("Maximum range in metres.")]
        public float range = 35f;

        [Tooltip("Rounds per clip. Zero means melee - no shooting.")]
        public int clipSize = 17;

        [Tooltip("Accuracy multiplier from the data file; lower spreads shots wider.")]
        public float accuracy = 1f;

        [Tooltip("False for melee weapons.")]
        public bool isGun = true;

        [Header("Feel")]
        [Tooltip("Seconds between shots.")]
        public float fireInterval = 0.18f;

        [Tooltip("Seconds to reload once the clip is empty.")]
        public float reloadSeconds = 1.6f;

        [Header("References")]
        [Tooltip("Muzzle point. The shot is cast from here, along its forward axis.")]
        public Transform muzzle;

        [Tooltip("Optional muzzle flash, shown briefly when firing.")]
        public GameObject muzzleFlash;

        public AudioSource fireSound;

        [Tooltip("Layers a shot can hit. Peds and world geometry.")]
        public LayerMask hitMask = ~0;

        private int _roundsLeft = 0;
        private float _cooldown = 0f;
        private bool _reloading = false;
        private bool _triggerHeld = false;
        private bool _held = false;

        void Start()
        {
            _roundsLeft = clipSize;

            if (muzzleFlash != null)
                muzzleFlash.SetActive(false);
        }

        public override void OnPickup()
        {
            _held = true;
        }

        public override void OnDrop()
        {
            _held = false;
            _triggerHeld = false;
        }

        // VRCPickup raises these while the object is held: trigger on a VR controller, left click on desktop
        public override void OnPickupUseDown()
        {
            _triggerHeld = true;
            TryFire();
        }

        public override void OnPickupUseUp()
        {
            _triggerHeld = false;
        }

        void Update()
        {
            if (_cooldown > 0f)
                _cooldown -= Time.deltaTime;

            // automatics keep firing while the trigger is down; the fire interval paces them
            if (_held && _triggerHeld && !_reloading && _cooldown <= 0f)
                TryFire();
        }

        private void TryFire()
        {
            if (!isGun || _reloading || _cooldown > 0f)
                return;

            if (_roundsLeft <= 0)
            {
                BeginReload();
                return;
            }

            _roundsLeft--;
            _cooldown = fireInterval;

            Fire();

            if (_roundsLeft <= 0)
                BeginReload();
        }

        private void Fire()
        {
            Transform origin = muzzle != null ? muzzle : transform;

            // accuracy from the data file drives the spread cone: a value of 1 is on the nose, lower is
            // looser. Without this every weapon is a laser and they all feel identical.
            float spread = Mathf.Max(0f, (1f - Mathf.Clamp(accuracy, 0.1f, 2f)) * 0.06f);

            Vector3 direction = origin.forward
                + origin.right * Random.Range(-spread, spread)
                + origin.up * Random.Range(-spread, spread);

            direction = direction.normalized;

            if (fireSound != null)
                fireSound.Play();

            if (muzzleFlash != null)
            {
                muzzleFlash.SetActive(true);
                SendCustomEventDelayedSeconds(nameof(HideMuzzleFlash), 0.05f);
            }

            RaycastHit hit;
            if (!Physics.Raycast(origin.position, direction, out hit, range, hitMask))
                return;

            // a ped hit takes damage and reacts; anything else just stops the bullet
            var ped = hit.collider.GetComponentInParent<GtaPedAI>();
            if (ped != null)
                ped.TakeDamage(damage);
        }

        public void HideMuzzleFlash()
        {
            if (muzzleFlash != null)
                muzzleFlash.SetActive(false);
        }

        private void BeginReload()
        {
            if (_reloading || clipSize <= 0)
                return;

            _reloading = true;
            SendCustomEventDelayedSeconds(nameof(FinishReload), reloadSeconds);
        }

        public void FinishReload()
        {
            _roundsLeft = clipSize;
            _reloading = false;
        }

        /// <summary> Rounds left in the clip, for a HUD to read. </summary>
        public int RoundsLeft => _roundsLeft;

        /// <summary> True while reloading. </summary>
        public bool IsReloading => _reloading;
    }
}
