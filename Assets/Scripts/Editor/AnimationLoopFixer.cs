using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Marks exported animation clips as looping.
    ///
    /// The importer produces clips with no loop flag, so a Mecanim state plays the clip once and then
    /// holds its final pose forever. Locomotion clips are around a second long, so a walking ped appears
    /// permanently frozen mid-stride even though the state machine is switching between idle/walk/run
    /// correctly - the pose changes on a state change, then stops moving again.
    ///
    /// Idempotent: clips already marked looping are skipped, so re-running is cheap.
    /// </summary>
    public static class AnimationLoopFixer
    {
        private const string AnimationsFolder = "Assets/ExportedAssets/Animations";

        [MenuItem(EditorCore.MenuName + "/" + "Fix animation looping")]
        public static void FixFromMenu()
        {
            int changed = SetClipsLooping();
            EditorUtility.DisplayDialog("", $"Marked {changed} animation clips as looping.", "Ok");
        }

        /// <summary> Returns the number of clips changed. </summary>
        public static int SetClipsLooping()
        {
            if (!AssetDatabase.IsValidFolder(AnimationsFolder))
            {
                Debug.LogWarning($"No exported animations at {AnimationsFolder}");
                return 0;
            }

            string[] guids = AssetDatabase.FindAssets("t:AnimationClip", new[] { AnimationsFolder });
            int changed = 0;

            try
            {
                AssetDatabase.StartAssetEditing();

                foreach (string guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                    if (null == clip)
                        continue;

                    var settings = AnimationUtility.GetAnimationClipSettings(clip);
                    if (settings.loopTime)
                        continue; // already looping

                    settings.loopTime = true;
                    AnimationUtility.SetAnimationClipSettings(clip, settings);
                    EditorUtility.SetDirty(clip);
                    changed++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            if (changed > 0)
                AssetDatabase.SaveAssets();

            Debug.Log($"Animation looping: {changed} of {guids.Length} clips updated");
            return changed;
        }
    }
}
