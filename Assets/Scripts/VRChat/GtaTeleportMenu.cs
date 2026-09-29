using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// A paged list of GTA locations the player can teleport to.
    ///
    /// San Andreas is several kilometres across and the world streams in around the player, so walking
    /// between districts to look at them is impractical. This is the fastest way to inspect distant parts of
    /// the map, and it doubles as the world's fast travel.
    ///
    /// Udon cannot create objects at runtime, so the buttons are pre-placed and their labels are rewritten
    /// as the page changes - the usual way to show a long list in a VRChat world.
    ///
    /// Teleporting is deliberately local: each player moves independently, so one person exploring does not
    /// drag everyone else across the map.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GtaTeleportMenu : UdonSharpBehaviour
    {
        [Header("Destination data")]
        [Tooltip("Names, in the same order as positions and headings.")]
        public string[] destinationNames;

        public Vector3[] destinationPositions;
        public float[] destinationHeadings;

        [Header("UI")]
        [Tooltip("One label per row. The number of rows sets the page size.")]
        public Text[] rowLabels;

        [Tooltip("Row buttons, matching rowLabels. Hidden when a page is not full.")]
        public GameObject[] rowButtons;

        public Text pageLabel;

        [Header("Behaviour")]
        [Tooltip("Lift applied on arrival so the player lands on the ground rather than inside it.")]
        public float arrivalHeightOffset = 1f;

        private int _page = 0;

        void Start()
        {
            RefreshPage();
        }

        private int PageSize => rowLabels != null ? rowLabels.Length : 0;

        private int PageCount
        {
            get
            {
                int size = PageSize;
                if (size <= 0 || destinationNames == null || destinationNames.Length == 0)
                    return 1;

                return (destinationNames.Length + size - 1) / size;
            }
        }

        public void NextPage()
        {
            _page++;
            if (_page >= PageCount)
                _page = 0;

            RefreshPage();
        }

        public void PreviousPage()
        {
            _page--;
            if (_page < 0)
                _page = PageCount - 1;

            RefreshPage();
        }

        /// <summary> Rewrites the visible rows for the current page. </summary>
        private void RefreshPage()
        {
            int size = PageSize;
            if (size <= 0)
                return;

            int total = destinationNames != null ? destinationNames.Length : 0;

            for (int row = 0; row < size; row++)
            {
                int index = _page * size + row;
                bool used = index < total;

                if (rowLabels[row] != null)
                    rowLabels[row].text = used ? destinationNames[index] : string.Empty;

                // hide unused rows rather than leaving dead buttons on the last page
                if (rowButtons != null && row < rowButtons.Length && rowButtons[row] != null)
                    rowButtons[row].SetActive(used);
            }

            if (pageLabel != null)
                pageLabel.text = $"{_page + 1} / {PageCount}";
        }

        /// <summary> Sends the local player to the destination shown on the given row. </summary>
        private void TeleportToRow(int row)
        {
            int index = _page * PageSize + row;

            if (destinationPositions == null || index < 0 || index >= destinationPositions.Length)
                return;

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid())
                return;

            Vector3 target = destinationPositions[index] + Vector3.up * arrivalHeightOffset;
            float heading = destinationHeadings != null && index < destinationHeadings.Length
                ? destinationHeadings[index]
                : 0f;

            localPlayer.TeleportTo(target, Quaternion.Euler(0f, heading, 0f));
        }

        // One entry point per row: Udon events carry no arguments, so a button cannot pass its own index.
        public void TeleportRow0() { TeleportToRow(0); }
        public void TeleportRow1() { TeleportToRow(1); }
        public void TeleportRow2() { TeleportToRow(2); }
        public void TeleportRow3() { TeleportToRow(3); }
        public void TeleportRow4() { TeleportToRow(4); }
        public void TeleportRow5() { TeleportToRow(5); }
        public void TeleportRow6() { TeleportToRow(6); }
        public void TeleportRow7() { TeleportToRow(7); }
        public void TeleportRow8() { TeleportToRow(8); }
        public void TeleportRow9() { TeleportToRow(9); }
    }
}
