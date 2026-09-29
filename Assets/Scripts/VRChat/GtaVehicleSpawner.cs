using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Summons a chosen vehicle to the player.
    ///
    /// Udon cannot instantiate, so every offered vehicle is pre-placed inactive and simply moved to the
    /// player when picked. That is the standard way a VRChat world does spawning, and it also caps the
    /// object count at build time rather than letting players grow it without limit.
    ///
    /// Picking the same vehicle twice moves the one instance rather than accumulating copies, so a busy
    /// world does not end up buried in abandoned cars.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class GtaVehicleSpawner : UdonSharpBehaviour
    {
        [Header("Vehicles")]
        [Tooltip("Pre-placed vehicles, parked out of sight until summoned.")]
        public GameObject[] vehicles;

        [Tooltip("Display names, in the same order as vehicles.")]
        public string[] vehicleNames;

        [Header("UI")]
        public Text[] rowLabels;
        public GameObject[] rowButtons;
        public Text pageLabel;

        [Header("Placement")]
        [Tooltip("Metres in front of the player the vehicle appears.")]
        public float spawnDistance = 5f;

        [Tooltip("Lift on arrival, so it settles onto the road rather than through it.")]
        public float spawnHeight = 1f;

        private int _page = 0;

        void Start()
        {
            // everything starts parked and hidden; summoning is what reveals a vehicle
            if (vehicles != null)
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    if (vehicles[i] != null)
                        vehicles[i].SetActive(false);
                }
            }

            RefreshPage();
        }

        private int PageSize => rowLabels != null ? rowLabels.Length : 0;

        private int PageCount
        {
            get
            {
                int size = PageSize;
                if (size <= 0 || vehicleNames == null || vehicleNames.Length == 0)
                    return 1;

                return (vehicleNames.Length + size - 1) / size;
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

        private void RefreshPage()
        {
            int size = PageSize;
            if (size <= 0)
                return;

            int total = vehicleNames != null ? vehicleNames.Length : 0;

            for (int row = 0; row < size; row++)
            {
                int index = _page * size + row;
                bool used = index < total;

                if (rowLabels[row] != null)
                    rowLabels[row].text = used ? vehicleNames[index] : string.Empty;

                if (rowButtons != null && row < rowButtons.Length && rowButtons[row] != null)
                    rowButtons[row].SetActive(used);
            }

            if (pageLabel != null)
                pageLabel.text = $"{_page + 1} / {PageCount}";
        }

        private void SpawnRow(int row)
        {
            int index = _page * PageSize + row;

            if (vehicles == null || index < 0 || index >= vehicles.Length)
                return;

            GameObject vehicle = vehicles[index];
            if (vehicle == null)
                return;

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid())
                return;

            // whoever summoned it should simulate it, or it drives on someone else's client
            if (!Networking.IsOwner(localPlayer, vehicle))
                Networking.SetOwner(localPlayer, vehicle);

            Vector3 position = localPlayer.GetPosition();
            Vector3 forward = localPlayer.GetRotation() * Vector3.forward;
            forward.y = 0f;

            if (forward.sqrMagnitude < 0.001f)
                forward = Vector3.forward;

            forward = forward.normalized;

            vehicle.SetActive(true);
            vehicle.transform.position = position + forward * spawnDistance + Vector3.up * spawnHeight;
            // face the same way as the player, so it is pointing down the road they are looking along
            vehicle.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);

            // a summoned car must not arrive carrying the momentum of wherever it last was
            var body = vehicle.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }

        // one entry point per row; Udon events take no arguments
        public void SpawnRow0() { SpawnRow(0); }
        public void SpawnRow1() { SpawnRow(1); }
        public void SpawnRow2() { SpawnRow(2); }
        public void SpawnRow3() { SpawnRow(3); }
        public void SpawnRow4() { SpawnRow(4); }
        public void SpawnRow5() { SpawnRow(5); }
        public void SpawnRow6() { SpawnRow(6); }
        public void SpawnRow7() { SpawnRow(7); }
        public void SpawnRow8() { SpawnRow(8); }
        public void SpawnRow9() { SpawnRow(9); }
    }
}
