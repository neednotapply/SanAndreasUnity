using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Streams the city in and out around players by toggling pre-built spatial cells.
    ///
    /// Udon cannot instantiate at runtime in any practical way, so every map object is placed at build
    /// time and grouped into cells; this only flips cell roots active/inactive. Toggling a few hundred
    /// parents is affordable where toggling 45,000 objects would not be.
    ///
    /// Cells are checked round-robin rather than all at once - a full scan every frame would dominate the
    /// Udon budget, and geometry a few hundred metres away does not need frame-accurate decisions.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GtaWorldStreamer : UdonSharpBehaviour
    {
        [Header("Cells (built by the editor tool)")]
        public GameObject[] cellRoots;
        public Vector3[] cellCenters;

        [Header("Street lighting")]
        [Tooltip("One object per cell holding that cell's lamp glows, so a whole cell lights at once.")]
        public GameObject[] cellLamps;

        [Tooltip("Supplies the hour. Without it the lamps stay off.")]
        public GtaDayNightCycle dayNight;

        [Tooltip("Hour the lamps come on.")]
        public float duskHour = 19f;

        [Tooltip("Hour the lamps go off.")]
        public float dawnHour = 6.5f;

        [Tooltip("Seconds between checks for dusk and dawn.")]
        public float lampCheckInterval = 10f;

        private bool _lampsLit = false;
        private float _lampTimer = 0f;

        [Header("Distances")]
        [Tooltip("Cells with a player closer than this are shown.")]
        public float loadRadius = 450f;

        [Tooltip("Cells are hidden past this. Must exceed loadRadius, or cells flicker on the boundary.")]
        public float unloadRadius = 550f;

        [Header("Budget")]
        [Tooltip("Cells evaluated per tick. Lower is cheaper but slower to react.")]
        public int cellsPerTick = 24;

        [Tooltip("Seconds between ticks.")]
        public float updateInterval = 0.25f;

        private int _scanCursor = 0;
        private float _timeSinceUpdate = 0f;

        // avoids re-calling SetActive on cells that are already in the right state
        private bool[] _cellActive;

        void Start()
        {
            if (cellRoots == null)
                return;

            _cellActive = new bool[cellRoots.Length];

            for (int i = 0; i < cellRoots.Length; i++)
            {
                if (cellRoots[i] != null)
                    cellRoots[i].SetActive(false);

                _cellActive[i] = false;
            }

            // Bring in the surrounding cells immediately. The round-robin scan needs several seconds to
            // work through every cell, and until it reaches the player's own cell there is no ground under
            // them - they spawn in mid-air and fall.
            RefreshAllCellsImmediate();
        }

        /// <summary>
        /// Evaluates every cell in one pass, ignoring the per-tick budget. Used at startup and after a
        /// respawn, where waiting for the incremental scan would drop the player through the world.
        /// </summary>
        public void RefreshAllCellsImmediate()
        {
            if (cellRoots == null || cellCenters == null || _cellActive == null)
                return;

            Vector3 viewer = GetLocalPlayerPosition();

            for (int i = 0; i < cellRoots.Length; i++)
            {
                GameObject cell = cellRoots[i];
                if (cell == null)
                    continue;

                bool shouldBeActive = Vector3.Distance(viewer, cellCenters[i]) <= loadRadius;

                if (shouldBeActive != _cellActive[i])
                {
                    cell.SetActive(shouldBeActive);
                    _cellActive[i] = shouldBeActive;
                }
            }
        }

        /// <summary>
        /// VRChat calls this when the local player respawns. Respawning teleports the player across the
        /// map, so the streamed set has to be rebuilt at once rather than caught up incrementally.
        /// </summary>
        public override void OnPlayerRespawn(VRCPlayerApi player)
        {
            if (player != null && player.isLocal)
                RefreshAllCellsImmediate();
        }

        /// <summary>
        /// Turns street lighting on after dark.
        ///
        /// Each cell's lamp glows are gathered under a single object at build time, so lighting the world is
        /// one toggle per cell rather than one per lamp. Only cells the streamer has loaded are touched -
        /// about two dozen at a time - which is what makes this affordable at all. Leaving every lamp lit
        /// permanently was the previous behaviour, and it looked wrong at noon.
        /// </summary>
        private void UpdateLamps()
        {
            if (cellLamps == null || dayNight == null)
                return;

            float hour = dayNight.CurrentHour;
            bool lit = hour >= duskHour || hour < dawnHour;

            if (lit == _lampsLit)
                return;

            _lampsLit = lit;

            for (int i = 0; i < cellLamps.Length; i++)
            {
                if (cellLamps[i] != null)
                    cellLamps[i].SetActive(lit);
            }
        }

        void Update()
        {
            _lampTimer += Time.deltaTime;
            if (_lampTimer >= lampCheckInterval)
            {
                _lampTimer = 0f;
                UpdateLamps();
            }

            if (cellRoots == null || cellCenters == null || _cellActive == null)
                return;

            _timeSinceUpdate += Time.deltaTime;
            if (_timeSinceUpdate < updateInterval)
                return;

            _timeSinceUpdate = 0f;

            int cellCount = cellRoots.Length;
            if (cellCount == 0)
                return;

            // every client streams for itself - this is presentation, not shared state, so it must not
            // be gated on ownership the way the NPC pool is
            Vector3 viewer = GetLocalPlayerPosition();

            int toScan = cellsPerTick;
            if (toScan > cellCount)
                toScan = cellCount;

            for (int i = 0; i < toScan; i++)
            {
                int index = _scanCursor;
                _scanCursor++;
                if (_scanCursor >= cellCount)
                    _scanCursor = 0;

                GameObject cell = cellRoots[index];
                if (cell == null)
                    continue;

                float distance = Vector3.Distance(viewer, cellCenters[index]);

                if (!_cellActive[index])
                {
                    if (distance <= loadRadius)
                    {
                        cell.SetActive(true);
                        _cellActive[index] = true;
                    }
                }
                else
                {
                    // hysteresis: only hide once well outside, so walking the boundary doesn't thrash
                    if (distance > unloadRadius)
                    {
                        cell.SetActive(false);
                        _cellActive[index] = false;
                    }
                }
            }
        }

        private Vector3 GetLocalPlayerPosition()
        {
            VRCPlayerApi player = Networking.LocalPlayer;

            // in the editor without a player there is no viewer, so fall back to this object
            if (player == null || !player.IsValid())
                return transform.position;

            return player.GetPosition();
        }

        /// <summary> Number of cells currently shown - useful for tuning the radii. </summary>
        public int GetActiveCellCount()
        {
            if (_cellActive == null)
                return 0;

            int count = 0;
            for (int i = 0; i < _cellActive.Length; i++)
            {
                if (_cellActive[i])
                    count++;
            }

            return count;
        }
    }
}
