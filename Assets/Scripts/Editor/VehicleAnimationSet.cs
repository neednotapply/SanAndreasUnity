using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Finds the right in-vehicle ped animation for a vehicle's animation group.
    ///
    /// GTA layers this data rather than duplicating it. The default car set lives in the ped group
    /// (CAR_sit, CAR_sitp, CAR_getout_LHS, CAR_jackedLHS...), and each vehicle class overrides only the
    /// clips that differ: a truck overrides entry and exit for its high cab but still sits with CAR_sit,
    /// while a bike replaces the set outright - its rider has a ride pose, not a seated one.
    ///
    /// So resolution walks the vehicle's own group first and falls back to the car set, which reproduces
    /// the game's layering without needing to hard-code which classes override what. Clips are matched by
    /// suffix because the groups use their own prefixes (BIKEs_Ride, QUAD_ride, CHOPPA_ride).
    /// </summary>
    public static class VehicleAnimationSet
    {
        private const string AnimRoot = "Assets/ExportedAssets/Animations";
        private const string DefaultGroup = "ped";

        // cached per group, since a build resolves the same handful of groups hundreds of times
        private static readonly Dictionary<string, string[]> s_clipsByGroup =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        // actual on-disk folder for each group name, keyed case-insensitively
        private static Dictionary<string, string> s_groupFolders;

        /// <summary> Seated/ride pose for whoever is at the wheel. </summary>
        public static AnimationClip ResolveDriverPose(string animGroup)
        {
            // a bike's ride pose is the equivalent of sitting; "still" is the stationary variant
            return Resolve(animGroup, new[] { "_ride", "_still", "_sit" }, "CAR_sit");
        }

        /// <summary> Pose for a passenger. </summary>
        public static AnimationClip ResolvePassengerPose(string animGroup)
        {
            return Resolve(animGroup, new[] { "_passenger", "_sitp" }, "CAR_sitp")
                // bikes with no passenger clip still look better riding than sitting in midair
                ?? ResolveDriverPose(animGroup);
        }

        /// <summary> The occupant being dragged out of the vehicle. </summary>
        public static AnimationClip ResolveJackedVictim(string animGroup)
        {
            return Resolve(animGroup, new[] { "_jackedlhs", "_jacked_lhs", "_jacked", "_snatch_l" },
                "CAR_jackedLHS");
        }

        /// <summary> The occupant climbing out under their own power. </summary>
        public static AnimationClip ResolveGetOut(string animGroup)
        {
            return Resolve(animGroup, new[] { "_getout_lhs", "_getout_l", "_getoutlhs", "_outl", "_getofflhs",
                "_getoff_lhs" }, "CAR_getout_LHS");
        }

        /// <summary>
        /// Looks for a clip in the vehicle's group whose name ends with one of <paramref name="suffixes"/>,
        /// in priority order, then falls back to a named clip in the default car set.
        /// </summary>
        private static AnimationClip Resolve(string animGroup, string[] suffixes, string fallbackClipName)
        {
            if (!string.IsNullOrWhiteSpace(animGroup) &&
                !string.Equals(animGroup, "null", StringComparison.OrdinalIgnoreCase))
            {
                string[] clips = GetClipPaths(animGroup);

                foreach (string suffix in suffixes)
                {
                    string match = clips.FirstOrDefault(path =>
                        System.IO.Path.GetFileNameWithoutExtension(path)
                            .EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                        return AssetDatabase.LoadAssetAtPath<AnimationClip>(match);
                }
            }

            return AssetDatabase.LoadAssetAtPath<AnimationClip>(
                $"{AnimRoot}/{DefaultGroup}/{fallbackClipName}.anim");
        }

        private static string[] GetClipPaths(string animGroup)
        {
            if (s_clipsByGroup.TryGetValue(animGroup, out string[] cached))
                return cached;

            // vehicles.ide and the animation packages disagree on case - "KART" and "BF_injection" are
            // exported as "kart" and "bf_injection" - so the folder is matched case-insensitively rather
            // than trusting the name from the definition
            string folder = ResolveGroupFolder(animGroup);

            string[] paths = folder != null
                ? AssetDatabase.FindAssets("t:AnimationClip", new[] { folder })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .OrderBy(_ => _)
                    .ToArray()
                : Array.Empty<string>();

            if (folder == null)
                Debug.LogWarning($"No animation folder for group '{animGroup}' - falling back to the car set");

            s_clipsByGroup[animGroup] = paths;
            return paths;
        }

        private static string ResolveGroupFolder(string animGroup)
        {
            string exact = $"{AnimRoot}/{animGroup}";
            if (AssetDatabase.IsValidFolder(exact))
                return exact;

            if (s_groupFolders == null)
            {
                s_groupFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (System.IO.Directory.Exists(AnimRoot))
                {
                    foreach (string directory in System.IO.Directory.GetDirectories(AnimRoot))
                    {
                        string name = System.IO.Path.GetFileName(directory);
                        s_groupFolders[name] = $"{AnimRoot}/{name}";
                    }
                }
            }

            return s_groupFolders.TryGetValue(animGroup, out string resolved) ? resolved : null;
        }

        /// <summary> Drops cached listings, so a rebuild after re-exporting animations sees new clips. </summary>
        public static void ClearCache()
        {
            s_clipsByGroup.Clear();
            s_groupFolders = null;
        }
    }
}
