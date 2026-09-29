using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Prints the lighting the day/night cycle will produce, hour by hour.
    ///
    /// The exported timecyc values were interpreted by hand - which column means what, how the eight
    /// keyframes map onto hours, and how they wrap past midnight. A mistake in any of that still compiles
    /// and still runs; it just produces a sky that is subtly or completely wrong. Printing the curve is the
    /// cheapest way to see whether it behaves like a day: dark and blue at night, bright and warm at noon,
    /// warm and dim at dusk.
    ///
    /// This mirrors the arithmetic in GtaDayNightCycle deliberately. If the two ever disagree, this is
    /// checking the wrong thing.
    /// </summary>
    public static class TimeCycleCheck
    {
        public static void Run()
        {
            var data = AssetDatabase.LoadAssetAtPath<Export.GtaTimeCycleData>(
                "Assets/ExportedAssets/TimeCycle.asset");

            if (null == data || data.WeatherCount == 0)
            {
                Debug.LogError("No time cycle data to check");
                EditorApplication.Exit(1);
                return;
            }

            int weather = 0;
            for (int i = 0; i < data.weatherNames.Length; i++)
            {
                if (data.weatherNames[i].StartsWith("EXTRASUNNY_LA", System.StringComparison.OrdinalIgnoreCase))
                {
                    weather = i;
                    break;
                }
            }

            int count = Export.GtaTimeCycleData.KeyframeCount;
            int baseIndex = weather * count;

            Debug.Log($"=== TIME CYCLE CURVE: {data.weatherNames[weather]} ===");
            Debug.Log($"keyframe hours: {string.Join(", ", data.keyframeHours)}");

            for (int hour = 0; hour < 24; hour += 2)
            {
                FindKeyframes(data.keyframeHours, hour, out int from, out int to, out float t);

                Color sky = Color.Lerp(data.skyTop[baseIndex + from], data.skyTop[baseIndex + to], t);
                Color horizon = Color.Lerp(data.skyBottom[baseIndex + from], data.skyBottom[baseIndex + to], t);
                Color sun = Color.Lerp(data.sunCore[baseIndex + from], data.sunCore[baseIndex + to], t);

                // same derivation the behaviour uses
                Color ambient = Color.Lerp(horizon, sky, 0.5f) * 0.7f;

                float sunAngle = (hour - 6f) / 12f * 180f;
                float elevation = Mathf.Sin(sunAngle * Mathf.Deg2Rad);

                Debug.Log($"  {hour:00}:00  kf {from}->{to} t={t:0.00}  " +
                    $"sky({sky.r:0.00},{sky.g:0.00},{sky.b:0.00})  " +
                    $"ambient({ambient.r:0.00},{ambient.g:0.00},{ambient.b:0.00})  " +
                    $"sun({sun.r:0.00},{sun.g:0.00},{sun.b:0.00})  " +
                    $"elevation {elevation:0.00}");
            }

            EditorApplication.Exit(0);
        }

        private static void FindKeyframes(int[] hours, float hour, out int from, out int to, out float t)
        {
            from = 0;
            to = 0;
            t = 0f;

            int count = hours.Length;

            for (int i = 0; i < count; i++)
            {
                int start = hours[i];
                int end = i + 1 < count ? hours[i + 1] : hours[0] + 24;

                if (hour >= start && hour < end)
                {
                    from = i;
                    to = (i + 1) % count;

                    float span = end - start;
                    t = span > 0.001f ? (hour - start) / span : 0f;
                    return;
                }
            }

            from = count - 1;
            to = 0;

            float wrapStart = hours[count - 1];
            float wrapSpan = (hours[0] + 24f) - wrapStart;

            t = wrapSpan > 0.001f ? ((hour + 24f) - wrapStart) / wrapSpan : 0f;
        }
    }
}
