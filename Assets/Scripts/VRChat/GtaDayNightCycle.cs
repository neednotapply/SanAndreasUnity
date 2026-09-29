using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Drives a day/night cycle using San Andreas' own authored lighting.
    ///
    /// The colours are not computed - they come from timecyc.dat, which gives eight keyframes through the
    /// day for each weather type. That authoring is why a San Andreas dusk looks the way it does, and an
    /// arithmetic approximation of it (which is what this behaviour used to do) never quite lands.
    ///
    /// The sun colour, sky gradient and fog are taken straight from the data. Ambient light is derived from
    /// the sky instead of from timecyc's own ambient columns, which do not translate to Unity - see Apply().
    ///
    /// Time of day is derived from VRChat's server clock rather than synced as a variable, so every client
    /// computes the same sky from the same source with no ownership, no drift and no network traffic. A
    /// player joining an hour later sees the same evening as everyone else.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GtaDayNightCycle : UdonSharpBehaviour
    {
        [Header("References")]
        public Light sun;

        [Header("Cycle")]
        [Tooltip("Real seconds for one full in-game day. 1200 is a 20 minute day, as most worlds use.")]
        public float dayLengthSeconds = 1200f;

        [Tooltip("Hour of day shown when the world starts, 0-24.")]
        public float startHour = 9f;

        [Tooltip("Compass direction the sun travels along, degrees.")]
        public float sunYaw = 45f;

        [Header("Timecyc data")]
        [Tooltip("Hour each keyframe applies to, ascending.")]
        public int[] keyframeHours;

        [Tooltip("Sky colour overhead per keyframe. Ambient light is derived from this.")]
        public Color[] skyColors;

        [Tooltip("Sun colour per keyframe.")]
        public Color[] sunColors;

        [Tooltip("Sky colour at the horizon per keyframe - also used as the fog colour.")]
        public Color[] horizonColors;

        [Tooltip("Fog start distance per keyframe.")]
        public float[] fogStarts;

        [Header("Ambient")]
        [Tooltip("Scales the sky-derived ambient light. GTA's own ambient columns cannot be used directly - " +
            "see the note in Apply().")]
        public float ambientScale = 0.7f;

        [Tooltip("Floor for ambient light, so night is dark rather than unnavigable.")]
        public Color minimumAmbient = new Color(0.06f, 0.07f, 0.10f);

        [Header("Light")]
        [Tooltip("Scales the sun's brightness; timecyc gives colour, not intensity.")]
        public float sunIntensityScale = 1.1f;

        [Tooltip("Sun brightness once it is below the horizon.")]
        public float nightIntensity = 0.08f;

        [Header("Fog")]
        [Tooltip("Fog gives the world its distance haze. Off if the scene should stay crisp.")]
        public bool controlFog = true;

        [Tooltip("Seconds between updates. The sky does not need to move every frame.")]
        public float updateInterval = 0.25f;

        private float _timeSinceUpdate = 0f;

        /// <summary> Current hour of day, 0-24. </summary>
        public float CurrentHour => ComputeHour();

        /// <summary>
        /// Day of the week, 0 = Monday through 6 = Sunday.
        ///
        /// Derived from the same clock as the hour, so it costs nothing to sync and every client agrees.
        /// A week passes in seven in-game days - at the default twenty-minute day that is a little over two
        /// hours of real time, which is long enough to notice and short enough that a visitor might see
        /// both a weekday and a weekend.
        /// </summary>
        public int CurrentDayOfWeek => ComputeDayOfWeek();

        /// <summary> True on Saturday or Sunday. popcycle keeps a separate population for these. </summary>
        public bool IsWeekend
        {
            get
            {
                int day = ComputeDayOfWeek();
                return day == 5 || day == 6;
            }
        }

        /// <summary> 0 on a weekday, 1 at the weekend - the index popcycle uses. </summary>
        public int DayKind => IsWeekend ? 1 : 0;

        void Start()
        {
            Apply();
        }

        void Update()
        {
            _timeSinceUpdate += Time.deltaTime;
            if (_timeSinceUpdate < updateInterval)
                return;

            _timeSinceUpdate = 0f;
            Apply();
        }

        /// <summary>
        /// Server time, made safe to do arithmetic on.
        ///
        /// Networking.GetServerTimeInSeconds() does not always return a sane value: in ClientSim, and on a
        /// real client during the moments before the network is up, it can come back large and negative.
        /// Dividing that down into weeks produced a number far outside Int32, and the cast to a day index
        /// threw OverflowException - which halts the behaviour outright.
        ///
        /// That was not a theoretical risk. Reading the day of the week from another behaviour compiles to
        /// a SendCustomEvent into this one, so when the district gating started asking what day it was,
        /// the throw took the clock down and the traffic and pedestrian pools with it.
        /// </summary>
        private double SafeServerTime()
        {
            double serverTime = Networking.GetServerTimeInSeconds();

            // NaN fails every comparison, so this catches it as well as the out-of-range cases
            if (!(serverTime > 0.0) || serverTime > 1e12)
                return 0.0;

            return serverTime;
        }

        private int ComputeDayOfWeek()
        {
            if (dayLengthSeconds <= 1f)
                return 0;

            double serverTime = SafeServerTime();
            double days = serverTime / dayLengthSeconds;

            // Done in doubles rather than with an integer modulo: Udon does not expose the remainder
            // operator for long, and server time is large enough that it would want to be a long. Taking
            // the fractional part of the week and scaling it back up avoids the operator entirely, and
            // Floor keeps it correct for negative times instead of truncating toward zero.
            double weeks = days / 7.0;
            double dayOfWeek = (weeks - System.Math.Floor(weeks)) * 7.0;

            // Guarded before the cast, not after. Convert.ToInt32 throws on anything out of range or not
            // a number, so clamping the result would be too late - the exception happens first.
            if (!(dayOfWeek >= 0.0) || dayOfWeek > 7.0)
                return 0;

            int day = (int)dayOfWeek;

            if (day < 0)
                day = 0;
            if (day > 6)
                day = 6;

            return day;
        }

        private float ComputeHour()
        {
            if (dayLengthSeconds <= 1f)
                return startHour;

            // server time is shared by every client, so this needs no syncing of its own
            double serverTime = SafeServerTime();
            double days = serverTime / dayLengthSeconds;
            double fraction = days - System.Math.Floor(days);

            float hour = startHour + (float)fraction * 24f;
            if (hour >= 24f)
                hour -= 24f;

            return hour;
        }

        /// <summary>
        /// Finds where the current hour sits between two keyframes.
        ///
        /// The keyframes are unevenly spaced - clustered around dawn and dusk, where the light changes
        /// fastest, and sparse through the middle of the day. They also wrap: the last keyframe (10PM) runs
        /// into the first (midnight) across the end of the day.
        /// </summary>
        private void FindKeyframes(float hour, out int from, out int to, out float t)
        {
            from = 0;
            to = 0;
            t = 0f;

            int count = keyframeHours != null ? keyframeHours.Length : 0;
            if (count == 0)
                return;

            if (count == 1)
                return;

            for (int i = 0; i < count; i++)
            {
                int startHourOfFrame = keyframeHours[i];
                int endHourOfFrame = i + 1 < count ? keyframeHours[i + 1] : keyframeHours[0] + 24;

                if (hour >= startHourOfFrame && hour < endHourOfFrame)
                {
                    from = i;
                    to = (i + 1) % count;

                    float span = endHourOfFrame - startHourOfFrame;
                    t = span > 0.001f ? (hour - startHourOfFrame) / span : 0f;
                    return;
                }
            }

            // before the first keyframe: we are in the wrap from the last one
            from = count - 1;
            to = 0;

            float wrapStart = keyframeHours[count - 1];
            float wrapEnd = keyframeHours[0] + 24f;
            float wrapSpan = wrapEnd - wrapStart;

            t = wrapSpan > 0.001f ? ((hour + 24f) - wrapStart) / wrapSpan : 0f;
        }

        private void Apply()
        {
            if (sun == null)
                return;

            float hour = ComputeHour();

            // 6am sunrise, 6pm sunset: the sun is at the horizon at both, overhead at noon
            float sunAngle = (hour - 6f) / 12f * 180f;
            float elevation = Mathf.Sin(sunAngle * Mathf.Deg2Rad);
            bool isDay = elevation > 0f;

            // below the horizon, flip the light overhead so the moon lights the world from above rather
            // than shining up through the ground
            sun.transform.rotation = isDay
                ? Quaternion.Euler(sunAngle, sunYaw, 0f)
                : Quaternion.Euler(sunAngle + 180f, sunYaw, 0f);

            int from, to;
            float t;
            FindKeyframes(hour, out from, out to, out t);

            // Ambient is derived from the sky rather than taken from timecyc's ambient columns.
            //
            // Measured from the data: the "Amb" column sits between 5 and 22 across the entire day, and
            // "Amb_Obj" is a nearly constant 194-220. Neither is a day/night curve - GTA's renderer used
            // them differently from the way Unity uses RenderSettings.ambientLight, and feeding either one
            // in gives a world that is either permanently black or permanently bright.
            //
            // The sky gradient IS a real curve (dark teal at midnight, blue at midday), so lighting the
            // world from the sky both tracks the time of day and matches how Unity's own gradient ambient
            // works.
            if (skyColors != null && horizonColors != null &&
                skyColors.Length > to && horizonColors.Length > to && to >= 0)
            {
                Color sky = Color.Lerp(skyColors[from], skyColors[to], t);
                Color horizon = Color.Lerp(horizonColors[from], horizonColors[to], t);

                Color ambient = Color.Lerp(horizon, sky, 0.5f) * ambientScale;

                RenderSettings.ambientLight = new Color(
                    Mathf.Max(ambient.r, minimumAmbient.r),
                    Mathf.Max(ambient.g, minimumAmbient.g),
                    Mathf.Max(ambient.b, minimumAmbient.b));
            }

            if (sunColors != null && sunColors.Length > to && to >= 0)
                sun.color = Color.Lerp(sunColors[from], sunColors[to], t);

            sun.intensity = isDay
                ? Mathf.Lerp(nightIntensity, sunIntensityScale, Mathf.Clamp01(elevation))
                : nightIntensity;

            if (controlFog && horizonColors != null && horizonColors.Length > to && to >= 0)
            {
                RenderSettings.fog = true;
                RenderSettings.fogColor = Color.Lerp(horizonColors[from], horizonColors[to], t);
                RenderSettings.fogMode = FogMode.Linear;

                if (fogStarts != null && fogStarts.Length > to)
                {
                    float start = Mathf.Lerp(fogStarts[from], fogStarts[to], t);
                    RenderSettings.fogStartDistance = start;
                    // the game's far clip is where fog becomes total; keep a wide band so the city does not
                    // vanish abruptly at the fog line
                    RenderSettings.fogEndDistance = start + 700f;
                }
            }
        }
    }
}
