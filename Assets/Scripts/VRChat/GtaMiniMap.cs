using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// The radar: a window onto San Andreas' map that follows and turns with the player.
    ///
    /// The whole state is one 1536x1536 texture assembled from the game's 144 radar tiles. Rather than
    /// scrolling a UV rectangle over it, the map is a full-size rectangle that is slid and rotated under a
    /// circular mask, so whatever ends up over the centre is what shows. That is what a UV rectangle could
    /// not do: a rect has no rotation, so a north-up map was the only thing it could express.
    ///
    /// Turning the map rather than the arrow matches the game, where the radar always points the way you
    /// are driving. The arrow stays pointing up the screen and never moves.
    ///
    /// It costs a rotation and a vector per frame, with no cameras, no render textures and no allocation.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GtaMiniMap : UdonSharpBehaviour
    {
        [Header("References")]
        [Tooltip("Shows the map texture. Its rectangle is what slides and turns.")]
        public RawImage mapImage;

        [Tooltip("The map's rectangle, sized here so the whole world spans it.")]
        public RectTransform mapRect;

        [Tooltip("Player marker. Stays put, pointing up the screen.")]
        public RectTransform playerArrow;

        [Tooltip("Names the district the player is in. Optional.")]
        public Text districtLabel;

        public GtaZoneDisplay zoneDisplay;

        [Header("World")]
        [Tooltip("Width of the map in world units. San Andreas is 6000 across, centred on the origin.")]
        public float worldSize = 6000f;

        [Header("View")]
        [Tooltip("How much of the world the radar shows, in world units. Smaller is more zoomed in.")]
        public float viewSize = 500f;

        [Tooltip("Width of the radar on screen, in canvas units.")]
        public float radarPixels = 150f;

        [Tooltip("Turn the map with the player. Off gives a north-up radar.")]
        public bool rotateWithPlayer = true;

        void Start()
        {
            Resize();
            Apply();
        }

        /// <summary>
        /// Updated every frame, in LateUpdate.
        ///
        /// The panel it sits on is moved to follow the head, so the radar has to be refreshed after that
        /// has happened or the map lags a frame behind its own frame. Updating on a timer was worse still:
        /// the panel moved smoothly while the map jumped ten times a second, which reads as the radar
        /// bouncing around rather than sitting on the HUD.
        /// </summary>
        void LateUpdate()
        {
            Apply();
        }

        /// <summary>
        /// Sizes the map rectangle so the requested slice of the world fills the radar.
        ///
        /// Only needed when the zoom changes, which is rare, so it is not part of the per-frame work.
        /// </summary>
        private void Resize()
        {
            if (mapRect == null || viewSize < 1f)
                return;

            float span = radarPixels * (worldSize / viewSize);
            mapRect.sizeDelta = new Vector2(span, span);
        }

        private void Apply()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid())
                return;

            Vector3 position = localPlayer.GetPosition();

            if (mapRect != null && worldSize > 1f && viewSize > 1f)
            {
                // world units to radar units
                float scale = (radarPixels / viewSize);

                // Where the player is on the map, measured from its centre. Negated because the map has to
                // move the opposite way to the player for the player to stay in the middle of it.
                Vector2 offset = new Vector2(-position.x * scale, -position.z * scale);

                float heading = rotateWithPlayer ? localPlayer.GetRotation().eulerAngles.y : 0f;

                // The map turns the opposite way to the player, so the direction they face is up the
                // screen. The offset is turned with it, or the map would rotate about the world origin
                // instead of about the player.
                Quaternion turn = Quaternion.Euler(0f, 0f, heading);

                mapRect.localRotation = turn;
                mapRect.anchoredPosition = turn * offset;
            }

            // The arrow no longer rotates - the map does - but it is kept upright explicitly so a rebuild
            // with rotateWithPlayer off does not leave it stuck at whatever angle it last held.
            if (playerArrow != null)
            {
                float heading = rotateWithPlayer ? 0f : -localPlayer.GetRotation().eulerAngles.y;
                playerArrow.localRotation = Quaternion.Euler(0f, 0f, heading);
            }

            if (districtLabel != null && zoneDisplay != null)
                districtLabel.text = zoneDisplay.CurrentZone;
        }

        /// <summary> Zooms the radar in or out, for a UI button to call. </summary>
        public void ZoomIn()
        {
            viewSize = Mathf.Max(150f, viewSize * 0.6f);
            Resize();
            Apply();
        }

        public void ZoomOut()
        {
            viewSize = Mathf.Min(worldSize, viewSize / 0.6f);
            Resize();
            Apply();
        }
    }
}
