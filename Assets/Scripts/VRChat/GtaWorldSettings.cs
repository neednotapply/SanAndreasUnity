using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Constrains player scale and movement so the world's proportions hold up.
    ///
    /// Vehicle interiors, doorways and seating positions are all sized to GTA's human proportions. VRChat
    /// avatars range from a few centimetres to several metres, so without clamping eye height a player can
    /// be too tall to fit in a car or too short to see over the dashboard. Locking the range keeps the
    /// world consistent for everyone.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GtaWorldSettings : UdonSharpBehaviour
    {
        [Header("Player scale")]
        [Tooltip("Constrain avatar eye height so players fit vehicles and see correctly.")]
        public bool constrainEyeHeight = true;

        [Tooltip("Shortest allowed eye height, metres.")]
        public float minEyeHeight = 1.5f;

        [Tooltip("Tallest allowed eye height, metres.")]
        public float maxEyeHeight = 1.9f;

        [Tooltip("Force everyone to this exact height rather than allowing a range.")]
        public bool forceExactHeight = false;

        [Tooltip("Eye height used when forcing an exact height, metres.")]
        public float exactEyeHeight = 1.7f;

        [Header("Movement")]
        public bool overrideMovement = true;
        public float walkSpeed = 2f;
        public float runSpeed = 4f;
        public float strafeSpeed = 2f;
        public float jumpImpulse = 3f;

        void Start()
        {
            ApplyToLocalPlayer();
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            // only the local player's own settings can be changed
            if (player != null && player.isLocal)
                ApplyToLocalPlayer();
        }

        private void ApplyToLocalPlayer()
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null || !player.IsValid())
                return;

            if (constrainEyeHeight)
            {
                if (forceExactHeight)
                {
                    // pin both ends of the range, then set the height itself
                    player.SetAvatarEyeHeightMinimumByMeters(exactEyeHeight);
                    player.SetAvatarEyeHeightMaximumByMeters(exactEyeHeight);
                    player.SetAvatarEyeHeightByMeters(exactEyeHeight);
                }
                else
                {
                    player.SetAvatarEyeHeightMinimumByMeters(minEyeHeight);
                    player.SetAvatarEyeHeightMaximumByMeters(maxEyeHeight);
                }
            }

            if (overrideMovement)
            {
                player.SetWalkSpeed(walkSpeed);
                player.SetRunSpeed(runSpeed);
                player.SetStrafeSpeed(strafeSpeed);
                player.SetJumpImpulse(jumpImpulse);
            }
        }
    }
}
