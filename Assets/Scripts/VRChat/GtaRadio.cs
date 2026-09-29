using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components.Video;
using VRC.SDK3.Video.Components.AVPro;
using VRC.SDKBase;
using VRC.Udon.Common;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// The car radio, following the game's own logic.
    ///
    /// In San Andreas a station does not wait for you. Its playhead runs in real time whether or not anyone
    /// is tuned in, so switching away for two minutes and coming back drops you two minutes further along,
    /// mid-song. That behaviour is the whole character of the radio, and it falls out of deriving the
    /// position from a clock rather than remembering where playback stopped:
    ///
    ///     position = (serverTime + stationOffset) mod duration
    ///
    /// Because it is derived, a station is always in the right place - for a listener who just tuned in, for
    /// one who never left, and for a player who joined the instance an hour late. Nothing is synced and
    /// nothing can drift apart, for the same reason the day/night cycle needs no syncing.
    ///
    /// Station choice is deliberately local. Each client plays whatever that player is tuned to, so two
    /// people in different cars hear different stations - which is both correct and the only way to have
    /// more than one station audible in a world at once.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GtaRadio : UdonSharpBehaviour
    {
        [Header("Stations")]
        [Tooltip("Station names, in tuning order.")]
        public string[] stationNames;

        [Tooltip("One recording per station. These are baked at build time - Udon cannot build a VRCUrl.")]
        public VRCUrl[] stationUrls;

        [Tooltip("Seconds added to each station's playhead so they are not all at the same point.")]
        public float[] stationOffsets;

        [Header("Playback")]
        public VRCAVProVideoPlayer videoPlayer;

        [Tooltip("Where the radio comes out. Put this on the vehicle for a radio you can hear from outside.")]
        public AudioSource output;

        [Tooltip("Short burst played while a station loads, standing in for tuning noise. Optional.")]
        public AudioSource tuningNoise;

        [Header("Behaviour")]
        [Tooltip("Radio only plays while the player is in a vehicle, as in the game.")]
        public bool onlyInVehicle = true;

        /// <summary>
        /// Set by whichever seat the player occupies.
        ///
        /// The radio used to scan every seat in the world each frame to decide whether to play. That was
        /// tolerable at fifty-five seats and is not at eight hundred, so the seat reports in instead.
        /// </summary>
        private bool _inVehicle = false;

        [Tooltip("Seconds of drift tolerated before the playhead is corrected.")]
        public float driftTolerance = 2.5f;

        [Tooltip("Seconds between drift checks.")]
        public float driftCheckInterval = 15f;

        [Header("Now playing (optional)")]
        [Tooltip("Station each track belongs to.")]
        public int[] trackStations;

        [Tooltip("Seconds into the recording where each track starts.")]
        public float[] trackStarts;

        public string[] trackTitles;

        [Tooltip("Displays station and track. Optional.")]
        public Text nowPlayingText;

        /// <summary> Station currently tuned, or -1 for off. </summary>
        private int _station = -1;

        private bool _ready = false;
        private float _driftTimer = 0f;
        private float _duration = 0f;

        public int CurrentStation => _station;

        public string CurrentStationName =>
            _station >= 0 && stationNames != null && _station < stationNames.Length
                ? stationNames[_station]
                : "RADIO OFF";

        void Start()
        {
            if (videoPlayer != null)
            {
                // the recordings are continuous stations, so they run round rather than stopping
                videoPlayer.Loop = true;
            }

            UpdateNowPlaying();
        }

        // ---- tuning ---------------------------------------------------------------------------------

        /// <summary> Next station, wrapping past the last one into "off". </summary>
        public void NextStation()
        {
            int count = stationUrls != null ? stationUrls.Length : 0;
            if (count == 0)
                return;

            int next = _station + 1;

            // one step past the last station is silence, as cycling past the last station in the game is
            if (next >= count)
                next = -1;

            Tune(next);
        }

        public void PreviousStation()
        {
            int count = stationUrls != null ? stationUrls.Length : 0;
            if (count == 0)
                return;

            int previous = _station - 1;

            if (previous < -1)
                previous = count - 1;

            Tune(previous);
        }

        public void RadioOff()
        {
            Tune(-1);
        }

        /// <summary> Tunes to a station index, or -1 for off. </summary>
        public void Tune(int station)
        {
            if (station == _station)
                return;

            _station = station;
            _ready = false;
            _duration = 0f;

            if (videoPlayer == null)
                return;

            if (_station < 0 || stationUrls == null || _station >= stationUrls.Length)
            {
                videoPlayer.Stop();
                UpdateNowPlaying();
                return;
            }

            // A station takes a moment to resolve and buffer - the one place this cannot match the game,
            // where switching is instant. The tuning burst covers it, which is also what the game plays
            // between stations.
            if (tuningNoise != null)
                tuningNoise.Play();

            videoPlayer.PlayURL(stationUrls[_station]);

            UpdateNowPlaying();
        }

        /// <summary>
        /// Cycles stations while driving.
        ///
        /// Bound to grab because the other inputs are taken: movement drives, jump leaves the car and use is
        /// the handbrake. Grab does nothing at the wheel, which makes it the one key free to be the radio.
        /// </summary>
        public override void InputGrab(bool value, UdonInputEventArgs args)
        {
            if (!value || !ShouldBeAudible())
                return;

            NextStation();
        }

        /// <summary> Called by a seat when the local player sits down. </summary>
        public void OnEnteredVehicle()
        {
            _inVehicle = true;
        }

        /// <summary> Called by a seat when the local player gets out. </summary>
        public void OnExitedVehicle()
        {
            _inVehicle = false;
        }

        // ---- the playhead ---------------------------------------------------------------------------

        /// <summary>
        /// Where the tuned station should be right now.
        ///
        /// Derived from the network clock, never from playback, which is what makes a station carry on
        /// while nobody is listening.
        /// </summary>
        private float ComputePosition()
        {
            if (_duration <= 1f)
                return 0f;

            double serverTime = Networking.GetServerTimeInSeconds();

            float offset = stationOffsets != null && _station >= 0 && _station < stationOffsets.Length
                ? stationOffsets[_station]
                : 0f;

            double elapsed = serverTime + offset;

            // wrap into the recording, keeping it positive for negative server times
            double laps = elapsed / _duration;
            double position = (laps - System.Math.Floor(laps)) * _duration;

            return (float)position;
        }

        public override void OnVideoReady()
        {
            if (videoPlayer == null)
                return;

            _duration = videoPlayer.GetDuration();
            _ready = true;

            // join the station where it already is, rather than at the beginning
            videoPlayer.SetTime(ComputePosition());

            if (tuningNoise != null)
                tuningNoise.Stop();

            UpdateNowPlaying();
        }

        public override void OnVideoError(VideoError videoError)
        {
            // A station whose recording will not load is simply dead air. Skipping to the next one would
            // hide the problem and could loop through every broken station in a row.
            _ready = false;

            if (tuningNoise != null)
                tuningNoise.Stop();

            if (nowPlayingText != null)
                nowPlayingText.text = CurrentStationName + "  (unavailable)";
        }

        void Update()
        {
            bool shouldPlay = _station >= 0 && ShouldBeAudible();

            if (output != null)
                output.mute = !shouldPlay;

            if (!_ready || videoPlayer == null)
                return;

            _driftTimer += Time.deltaTime;
            if (_driftTimer < driftCheckInterval)
                return;

            _driftTimer = 0f;

            // Correct the playhead if it has wandered. Buffering and loop points both nudge it, and over a
            // long session that would accumulate into listeners being minutes apart.
            float expected = ComputePosition();
            float actual = videoPlayer.GetTime();

            if (Mathf.Abs(actual - expected) > driftTolerance)
                videoPlayer.SetTime(expected);

            UpdateNowPlaying();
        }

        /// <summary> Whether the radio should be heard at all right now. </summary>
        private bool ShouldBeAudible()
        {
            return !onlyInVehicle || _inVehicle;
        }

        // ---- now playing ----------------------------------------------------------------------------

        /// <summary>
        /// Names the track the station is currently on, from the timecodes in the recording.
        /// </summary>
        private void UpdateNowPlaying()
        {
            if (nowPlayingText == null)
                return;

            if (_station < 0)
            {
                nowPlayingText.text = "RADIO OFF";
                return;
            }

            string title = FindCurrentTrack();

            nowPlayingText.text = title.Length > 0
                ? CurrentStationName + "  -  " + title
                : CurrentStationName;
        }

        private string FindCurrentTrack()
        {
            if (trackStations == null || trackStarts == null || trackTitles == null || !_ready)
                return string.Empty;

            float position = ComputePosition();

            float bestStart = -1f;
            int best = -1;

            for (int i = 0; i < trackStations.Length; i++)
            {
                if (trackStations[i] != _station)
                    continue;

                if (i >= trackStarts.Length || i >= trackTitles.Length)
                    continue;

                // the current track is the latest one that has already started
                if (trackStarts[i] <= position && trackStarts[i] > bestStart)
                {
                    bestStart = trackStarts[i];
                    best = i;
                }
            }

            return best >= 0 ? trackTitles[best] : string.Empty;
        }
    }
}
