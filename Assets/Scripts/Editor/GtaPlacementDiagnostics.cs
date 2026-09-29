using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Reports the measurements that ped and seat placement depend on.
    ///
    /// Placement has been tuned by guesswork more than once. These numbers come from the imported assets
    /// themselves - bind poses and frame positions - so the offsets can be set from data instead of from
    /// repeated visual comparison.
    /// </summary>
    public static class GtaPlacementDiagnostics
    {
        public static void Run()
        {
            MeasurePeds();
            MeasureSeats();
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// Distance from a ped's pivot down to the lowest point of its mesh, in the bind pose.
        ///
        /// This is what NavMeshAgent.baseOffset has to be: the agent puts the transform on the nav mesh,
        /// so anything hanging below the pivot ends up underground.
        /// </summary>
        private static void MeasurePeds()
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/ExportedAssets/Prefabs/Peds" });
            Debug.Log($"=== PED MEASUREMENTS ({guids.Length} prefabs, sampling 12) ===");

            // GTA skeletons are Z-up, so the bind pose has the ped lying on its back - measuring it gives
            // a 0.4m-tall "human". Only the animated pose stands upright, so the clip has to be sampled
            // before anything is measured.
            var poseClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(
                "Assets/ExportedAssets/Animations/ped/WALK_civi.anim")
                ?? AssetDatabase.LoadAssetAtPath<AnimationClip>(
                    "Assets/ExportedAssets/Animations/ped/IDLE_stance.anim");

            if (null == poseClip)
            {
                Debug.LogError("  no reference pose clip found - cannot measure standing height");
                return;
            }

            Debug.Log($"  sampling pose from '{poseClip.name}'");

            int sampled = 0;
            float total = 0f;

            foreach (string guid in guids)
            {
                if (sampled >= 12)
                    break;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (null == prefab)
                    continue;

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.transform.position = Vector3.zero;
                instance.transform.rotation = Quaternion.identity;

                // put the skeleton into the pose it will actually be displayed in
                poseClip.SampleAnimation(instance, 0f);

                bool any = false;
                float lowest = 0f;
                float highest = 0f;

                foreach (var skinned in instance.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    if (null == skinned.sharedMesh)
                        continue;

                    var baked = new Mesh();
                    skinned.BakeMesh(baked);

                    foreach (Vector3 vertex in baked.vertices)
                    {
                        float y = instance.transform.InverseTransformPoint(
                            skinned.transform.TransformPoint(vertex)).y;

                        if (!any || y < lowest) lowest = y;
                        if (!any || y > highest) highest = y;
                        any = true;
                    }

                    Object.DestroyImmediate(baked);
                }

                if (any)
                {
                    total += -lowest;
                    sampled++;
                    Debug.Log($"  {prefab.name}: pivot-to-feet = {-lowest:F3}m, standing height = {highest - lowest:F2}m");
                }

                Object.DestroyImmediate(instance);
            }

            if (sampled > 0)
                Debug.Log($"  AVERAGE pivot-to-feet = {total / sampled:F3}m  (current baseOffset is 0.53)");
        }

        /// <summary>
        /// Where the driver's seat frame sits relative to the vehicle, and how that compares to the body.
        /// A seat above the roof line means the player is placed on top of the car rather than inside it.
        /// </summary>
        private static void MeasureSeats()
        {
            // Pivot and geometry disagree on the broken models, so print both: where the frame sits, and
            // where the mesh actually lands. A wheel whose pivot is correct but whose bounds are metres away
            // means the mesh carries an offset the clone did not account for.
            string[] names = { "blade", "blistac", "elegy", "admiral" };

            foreach (string name in names)
            {
                string[] guids = AssetDatabase.FindAssets(
                    name + " t:Prefab", new[] { "Assets/ExportedAssets/Prefabs/Vehicles" });

                if (guids.Length == 0)
                    continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    AssetDatabase.GUIDToAssetPath(guids[0]));

                if (null == prefab || prefab.name.ToLowerInvariant() != name)
                    continue;

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.transform.position = Vector3.zero;
                instance.transform.rotation = Quaternion.identity;

                Debug.Log($"=== '{prefab.name}' PIVOT vs GEOMETRY ===");

                foreach (var r in instance.GetComponentsInChildren<MeshRenderer>())
                {
                    if (!r.gameObject.activeInHierarchy)
                        continue;

                    Vector3 pivot = r.transform.position;
                    Vector3 centre = r.bounds.center;
                    float drift = Vector3.Distance(pivot, centre);

                    string flag = drift > 1.5f ? "  <== DISPLACED" : "";

                    Debug.Log($"    {r.name,-18} pivot=({pivot.x:F2},{pivot.y:F2},{pivot.z:F2}) " +
                        $"meshCentre=({centre.x:F2},{centre.y:F2},{centre.z:F2}) " +
                        $"drift={drift:F2} size=({r.bounds.size.x:F1},{r.bounds.size.y:F1},{r.bounds.size.z:F1})" +
                        flag);
                }

                Object.DestroyImmediate(instance);
            }
        }
    }
}
