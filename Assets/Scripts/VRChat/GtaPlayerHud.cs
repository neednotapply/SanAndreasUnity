using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// The heads-up display: health, ammunition and the time of day.
    ///
    /// VRChat gives a world no screen-space overlay, so the HUD is a small world-space panel carried in
    /// front of the player's head. It is positioned from head tracking data rather than parented to the
    /// camera, which is what keeps it readable in VR as well as on desktop.
    ///
    /// The panel exists once per client and is only ever driven by the local player's own state, so nothing
    /// here is synced.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GtaPlayerHud : UdonSharpBehaviour
    {
        [Header("References")]
        public GtaPlayerHealth playerHealth;
        public GtaDayNightCycle dayNight;

        [Tooltip("Panel moved in front of the player's head.")]
        public Transform panel;

        [Header("Display")]
        public Text healthText;
        public Image healthBar;
        public Text clockText;
        public Text weaponText;

        [Tooltip("World position readout. Useful for reporting exactly where something is wrong.")]
        public Text positionText;

        [Header("Placement")]
        [Tooltip("Metres in front of the head.")]
        public float distance = 0.85f;

        [Tooltip("Metres below eye level, so it sits at the bottom of vision rather than over it.")]
        public float drop = 0.28f;

        [Tooltip("How quickly the panel catches up with the head. Low values feel calmer in VR.")]
        public float followSharpness = 8f;

        private GtaWeapon _heldWeapon;
        private float _weaponScanTimer = 0f;

        void Update()
        {
            UpdateReadouts();
        }

        /// <summary>
        /// The panel is moved in LateUpdate so it settles after head tracking for the frame, and so that
        /// anything parented to it - the radar - can update against its final position rather than a stale
        /// one.
        /// </summary>
        void LateUpdate()
        {
            FollowHead();
        }

        /// <summary>
        /// Keeps the panel in front of the player without parenting it to the head, which would make it
        /// jitter with every small head movement and is uncomfortable in VR.
        /// </summary>
        private void FollowHead()
        {
            if (panel == null)
                return;

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid())
                return;

            VRCPlayerApi.TrackingData head = localPlayer.GetTrackingData(
                VRCPlayerApi.TrackingDataType.Head);

            Vector3 forward = head.rotation * Vector3.forward;
            Vector3 target = head.position + forward * distance + Vector3.down * drop;

            float t = Mathf.Clamp01(Time.deltaTime * followSharpness);

            panel.position = Vector3.Lerp(panel.position, target, t);
            panel.rotation = Quaternion.Slerp(panel.rotation, head.rotation, t);
        }

        private void UpdateReadouts()
        {
            if (playerHealth != null)
            {
                if (healthText != null)
                    healthText.text = playerHealth.Health.ToString();

                if (healthBar != null)
                    healthBar.fillAmount = playerHealth.HealthFraction;
            }

            if (clockText != null && dayNight != null)
            {
                float hour = dayNight.CurrentHour;
                int hours = (int)hour;
                int minutes = (int)((hour - hours) * 60f);
                clockText.text = $"{hours:00}:{minutes:00}";
            }

            UpdateWeaponReadout();

            if (positionText != null)
            {
                VRCPlayerApi localPlayer = Networking.LocalPlayer;
                if (localPlayer != null && localPlayer.IsValid())
                {
                    Vector3 p = localPlayer.GetPosition();
                    positionText.text = $"{p.x:0} {p.y:0} {p.z:0}";
                }
            }
        }

        /// <summary>
        /// Shows the ammunition of whatever the player is holding.
        ///
        /// Udon cannot ask "what am I holding", so the held weapon is found by checking pickups for one
        /// currently held by the local player - rescanned occasionally rather than every frame.
        /// </summary>
        private void UpdateWeaponReadout()
        {
            if (weaponText == null)
                return;

            _weaponScanTimer += Time.deltaTime;
            if (_weaponScanTimer >= 0.4f)
            {
                _weaponScanTimer = 0f;
                _heldWeapon = FindHeldWeapon();
            }

            if (_heldWeapon == null)
            {
                weaponText.text = "UNARMED";
                return;
            }

            weaponText.text = _heldWeapon.IsReloading
                ? $"{_heldWeapon.modelName}  RELOADING"
                : $"{_heldWeapon.modelName}  {_heldWeapon.RoundsLeft}/{_heldWeapon.clipSize}";
        }

        [Tooltip("Weapons that can be picked up, scanned to find the one being held.")]
        public GtaWeapon[] weapons;

        private GtaWeapon FindHeldWeapon()
        {
            if (weapons == null)
                return null;

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid())
                return null;

            Vector3 leftHand = localPlayer.GetBonePosition(HumanBodyBones.LeftHand);
            Vector3 rightHand = localPlayer.GetBonePosition(HumanBodyBones.RightHand);

            for (int i = 0; i < weapons.Length; i++)
            {
                GtaWeapon weapon = weapons[i];
                if (weapon == null || !weapon.gameObject.activeInHierarchy)
                    continue;

                Vector3 position = weapon.transform.position;

                // held weapons sit in a hand; anything on the ground is metres away from both
                if (Vector3.Distance(position, rightHand) < 0.35f ||
                    Vector3.Distance(position, leftHand) < 0.35f)
                {
                    return weapon;
                }
            }

            return null;
        }
    }
}
