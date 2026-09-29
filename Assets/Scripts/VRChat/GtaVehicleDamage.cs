using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Panel damage, smoke, fire and explosion for one vehicle.
    ///
    /// Every GTA vehicle model ships two versions of each panel it can lose - bonnet_ok and bonnet_dam,
    /// door_lf_ok and door_lf_dam, and so on - and the game swaps between them as the car takes hits. The
    /// exporter already brings both across with the damaged one switched off, so all that was missing was
    /// something to do the swapping.
    ///
    /// Which panel takes a hit is decided by where the impact landed rather than at random, so hitting a
    /// wall head-on crumples the front and being rear-ended crumples the back.
    ///
    /// Thresholds follow the original: smoke from a third of health, fire from a tenth, then it goes up.
    /// Health is 1000 as it is there, so hits tuned against the game's numbers behave the same way here.
    ///
    /// State is owner-only and synced manually. Damage is not worth a continuous sync - it changes on
    /// impact and not otherwise - and every client swapping panels on its own would give the same car a
    /// different set of dents for each player watching it.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class GtaVehicleDamage : UdonSharpBehaviour
    {
        [Header("Panels")]
        [Tooltip("Undamaged panels. Paired by index with damagedPanels.")]
        public GameObject[] okPanels;

        [Tooltip("Damaged versions, shown as their pair is hidden.")]
        public GameObject[] damagedPanels;

        [Tooltip("Centre of each panel in local space, used to find which one an impact is nearest.")]
        public Vector3[] panelCentres;

        [Header("Condition")]
        [Tooltip("As in the game, where a car starts at 1000.")]
        public float maxHealth = 1000f;

        [Tooltip("Smoke from this fraction of health down. The game uses a third.")]
        [Range(0f, 1f)] public float smokeFraction = 0.33f;

        [Tooltip("Fire from this fraction down. The game uses a tenth.")]
        [Range(0f, 1f)] public float fireFraction = 0.1f;

        [Header("Effects")]
        [Tooltip("Shown once the engine is smoking. Pre-placed - Udon cannot create objects.")]
        public GameObject smoke;

        [Tooltip("Shown once the engine is on fire.")]
        public GameObject fire;

        [Tooltip("Shown briefly when the vehicle explodes.")]
        public GameObject explosion;

        public AudioSource explosionSound;

        [Header("Impact")]
        [Tooltip("Collisions slower than this leave no mark, so kerbs and scrapes do not wreck a car.")]
        public float minimumImpactSpeed = 4f;

        [Tooltip("Damage per metre per second of impact speed.")]
        public float damagePerSpeed = 12f;

        [UdonSynced] private float _health = 1000f;
        [UdonSynced] private int _damagedMask = 0;

        private bool _exploded = false;

        void Start()
        {
            _health = maxHealth;
            ApplyState();
        }

        void OnCollisionEnter(Collision collision)
        {
            // Only the owner decides damage.
            //
            // Every client sees the same collision, so without this the car would take its damage several
            // times over - once per player watching - and end up wrecked by a single tap.
            if (!Networking.IsOwner(gameObject))
                return;

            if (_exploded)
                return;

            float speed = collision.relativeVelocity.magnitude;
            if (speed < minimumImpactSpeed)
                return;

            Vector3 point = collision.contacts.Length > 0
                ? collision.contacts[0].point
                : collision.transform.position;

            TakeDamage((speed - minimumImpactSpeed) * damagePerSpeed, point);
        }

        /// <summary>
        /// Applies damage and crumples whichever panel is nearest where it landed.
        /// </summary>
        public void TakeDamage(float amount, Vector3 worldPoint)
        {
            if (_exploded || amount <= 0f)
                return;

            _health -= amount;

            int nearest = NearestPanel(worldPoint);
            if (nearest >= 0)
                _damagedMask = _damagedMask | (1 << nearest);

            if (_health <= 0f)
                Explode();

            ApplyState();
            RequestSerialization();
        }

        public override void OnDeserialization()
        {
            ApplyState();
        }

        /// <summary> Index of the panel closest to a world point, or -1. </summary>
        private int NearestPanel(Vector3 worldPoint)
        {
            if (panelCentres == null || panelCentres.Length == 0)
                return -1;

            Vector3 local = transform.InverseTransformPoint(worldPoint);

            int best = -1;
            float bestDistance = 0f;

            for (int i = 0; i < panelCentres.Length; i++)
            {
                // a panel already crumpled cannot crumple further, so damage moves to the next nearest
                if ((_damagedMask & (1 << i)) != 0)
                    continue;

                float d = Vector3.SqrMagnitude(panelCentres[i] - local);

                if (best < 0 || d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }

            return best;
        }

        /// <summary> Shows the panels, smoke and fire that match the current state. </summary>
        private void ApplyState()
        {
            if (okPanels != null && damagedPanels != null)
            {
                int count = Mathf.Min(okPanels.Length, damagedPanels.Length);

                for (int i = 0; i < count; i++)
                {
                    bool wrecked = (_damagedMask & (1 << i)) != 0;

                    if (okPanels[i] != null && okPanels[i].activeSelf == wrecked)
                        okPanels[i].SetActive(!wrecked);

                    if (damagedPanels[i] != null && damagedPanels[i].activeSelf != wrecked)
                        damagedPanels[i].SetActive(wrecked);
                }
            }

            float fraction = maxHealth > 0f ? _health / maxHealth : 1f;

            bool smoking = fraction <= smokeFraction && fraction > fireFraction;
            bool burning = fraction <= fireFraction;

            if (smoke != null && smoke.activeSelf != smoking)
                smoke.SetActive(smoking);

            if (fire != null && fire.activeSelf != burning)
                fire.SetActive(burning);
        }

        /// <summary>
        /// Wrecks the vehicle.
        ///
        /// Udon cannot spawn anything, so the explosion is a pre-placed effect that is switched on rather
        /// than an object created at the moment it is needed.
        /// </summary>
        public void Explode()
        {
            if (_exploded)
                return;

            _exploded = true;
            _health = 0f;

            // everything that can crumple, does
            if (okPanels != null)
                _damagedMask = (1 << okPanels.Length) - 1;

            if (explosion != null)
                explosion.SetActive(true);

            if (explosionSound != null)
                explosionSound.Play();

            ApplyState();
        }

        public float Health => _health;
        public bool IsWrecked => _exploded;
    }
}
