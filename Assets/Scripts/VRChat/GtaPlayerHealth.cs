using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Health for the local player, with GTA's respawn-on-death behaviour.
    ///
    /// VRChat has no player health of its own, so this owns it. Everything here is deliberately local: each
    /// client tracks its own player's health and nothing is synced. That keeps a fight responsive, avoids
    /// ownership fights over a value that only one person cares about, and means a player cannot have their
    /// health changed by someone else's lag.
    ///
    /// Hostile peds are detected by scanning the pool rather than having peds push damage outward. A ped is
    /// simulated by whoever owns it, which may not be the player it is attacking - so letting the victim's
    /// own client decide it has been hit is both simpler and harder to spoof.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GtaPlayerHealth : UdonSharpBehaviour
    {
        [Header("References")]
        [Tooltip("Pooled NPCs, scanned for hostiles near the player.")]
        public GameObject[] pooledNpcs;

        [Tooltip("Where the player reappears after dying.")]
        public Transform respawnPoint;

        [Header("Health")]
        public int maxHealth = 100;

        [Tooltip("Damage a hostile ped deals per hit.")]
        public int pedMeleeDamage = 8;

        [Tooltip("How close a hostile ped must be to land a hit.")]
        public float pedMeleeRange = 1.8f;

        [Tooltip("Seconds between ped hits, so contact does not drain health every frame.")]
        public float meleeInterval = 1.2f;

        [Tooltip("Seconds of invulnerability after respawning, so you are not killed as you appear.")]
        public float respawnGrace = 3f;

        [Header("Regeneration")]
        [Tooltip("Health regained per second once out of combat. Zero disables it.")]
        public float regenPerSecond = 2f;

        [Tooltip("Seconds without damage before health starts coming back.")]
        public float regenDelay = 8f;

        private int _health = 100;
        private float _meleeCooldown = 0f;
        private float _timeSinceDamage = 999f;
        private float _graceRemaining = 0f;
        private float _scanTimer = 0f;

        /// <summary> Current health. </summary>
        public int Health => _health;

        /// <summary> Health as a 0-1 fraction, for a bar to read. </summary>
        public float HealthFraction => maxHealth > 0 ? Mathf.Clamp01(_health / (float)maxHealth) : 0f;

        public bool IsDead => _health <= 0;

        void Start()
        {
            _health = maxHealth;
            _graceRemaining = respawnGrace;
        }

        void Update()
        {
            float dt = Time.deltaTime;

            if (_graceRemaining > 0f)
                _graceRemaining -= dt;

            if (_meleeCooldown > 0f)
                _meleeCooldown -= dt;

            _timeSinceDamage += dt;

            // out of combat for long enough - start healing, as the game does
            if (regenPerSecond > 0f && _timeSinceDamage >= regenDelay && _health < maxHealth && _health > 0)
            {
                _health = Mathf.Min(maxHealth, _health + Mathf.CeilToInt(regenPerSecond * dt));
            }

            // scanning the pool is cheap but pointless every frame
            _scanTimer += dt;
            if (_scanTimer >= 0.25f)
            {
                _scanTimer = 0f;
                ScanForHostiles();
            }
        }

        private void ScanForHostiles()
        {
            if (pooledNpcs == null || _health <= 0 || _graceRemaining > 0f || _meleeCooldown > 0f)
                return;

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid())
                return;

            Vector3 position = localPlayer.GetPosition();

            for (int i = 0; i < pooledNpcs.Length; i++)
            {
                GameObject npc = pooledNpcs[i];
                if (npc == null || !npc.activeInHierarchy)
                    continue;

                var ai = npc.GetComponent<GtaPedAI>();
                if (ai == null || ai.IsDead || ai.isCivilian)
                    continue;

                if (Vector3.Distance(npc.transform.position, position) > pedMeleeRange)
                    continue;

                TakeDamage(pedMeleeDamage);
                _meleeCooldown = meleeInterval;
                return;
            }
        }

        /// <summary> Applies damage to the local player. </summary>
        public void TakeDamage(int amount)
        {
            if (amount <= 0 || _health <= 0 || _graceRemaining > 0f)
                return;

            _health -= amount;
            _timeSinceDamage = 0f;

            if (_health <= 0)
            {
                _health = 0;
                Die();
            }
        }

        public void Heal(int amount)
        {
            if (amount <= 0 || _health <= 0)
                return;

            _health = Mathf.Min(maxHealth, _health + amount);
        }

        private void Die()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid())
                return;

            if (respawnPoint != null)
                localPlayer.TeleportTo(respawnPoint.position, respawnPoint.rotation);
            else
                localPlayer.Respawn();

            SendCustomEventDelayedSeconds(nameof(FinishRespawn), 0.5f);
        }

        public void FinishRespawn()
        {
            _health = maxHealth;
            _timeSinceDamage = 0f;
            _graceRemaining = respawnGrace;
        }
    }
}
