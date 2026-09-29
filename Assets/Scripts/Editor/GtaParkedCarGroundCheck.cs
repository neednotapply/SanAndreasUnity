using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Measures how far parked cars sit above (+) or below (-) the road under them.
    ///
    /// For a sample of parked vehicles it drops a ray onto the world colliders and compares the height of
    /// the lowest wheel with the surface. A correctly placed car reads close to zero. Written because the
    /// sunken-vehicle bug was diagnosed from a screenshot and needs a number to prove it is fixed.
    /// </summary>
    public static class GtaParkedCarGroundCheck
    {
        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/VRChatTest.unity", OpenSceneMode.Single);

            int step = 8;
            var vehicles = new List<Transform>();

            int allVehicleRoots = 0;
            var parentNames = new Dictionary<string, int>();

            foreach (var t in Object.FindObjectsOfType<Transform>(true))
            {
                if (!PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject))
                    continue;

                GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject);
                if (source == null)
                    continue;

                string path = AssetDatabase.GetAssetPath(source);
                if (!path.Contains("/Prefabs/Vehicles/"))
                    continue;

                allVehicleRoots++;
                string pn = t.parent != null ? t.parent.name : "(none)";
                if (pn.Length > 5) pn = pn.Substring(0, 5);
                parentNames[pn] = parentNames.TryGetValue(pn, out int c) ? c + 1 : 1;

                // parked cars are parented straight into a streaming cell
                if (t.parent == null || !t.parent.name.StartsWith("Cell"))
                    continue;

                vehicles.Add(t);
            }

            Debug.Log($"GROUNDCHECK vehicle prefab instances={allVehicleRoots}, in cells={vehicles.Count}, " +
                $"parents: {string.Join(", ", parentNames.Select(kv => kv.Key + "x" + kv.Value))}");

            vehicles = vehicles.OrderBy(v => v.position.x * 7919f + v.position.z).ToList();

            var clearances = new List<float>();
            int noGround = 0, sampled = 0;

            for (int i = 0; i < vehicles.Count; i += step)
            {
                Transform vehicle = vehicles[i];
                Transform cell = vehicle.parent;

                // cells are switched off at runtime by the streamer; make this one solid for the ray
                bool wasActive = cell.gameObject.activeSelf;
                cell.gameObject.SetActive(true);
                Physics.SyncTransforms();

                float lowestWheel = float.MaxValue;
                foreach (var r in vehicle.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (r.name == "wheel")
                        lowestWheel = Mathf.Min(lowestWheel, r.bounds.min.y);
                }

                if (lowestWheel == float.MaxValue)
                {
                    cell.gameObject.SetActive(wasActive);
                    continue;
                }

                // highest surface under the car that is not the car itself
                float ground = float.NegativeInfinity;
                var origin = vehicle.position + Vector3.up * 1.4f;

                foreach (var hit in Physics.RaycastAll(origin, Vector3.down, 6f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.transform.IsChildOf(vehicle))
                        continue;
                    if (hit.point.y > ground)
                        ground = hit.point.y;
                }

                cell.gameObject.SetActive(wasActive);
                sampled++;

                if (float.IsNegativeInfinity(ground))
                {
                    noGround++;
                    continue;
                }

                float clearance = lowestWheel - ground;
                clearances.Add(clearance);

                if (clearances.Count <= 12)
                    Debug.Log($"GROUNDCHECK {vehicle.name,-12} y={vehicle.position.y:F2} wheelBottom={lowestWheel:F2} " +
                        $"ground={ground:F2} clearance={clearance:+0.00;-0.00}");
            }

            clearances.Sort();

            if (clearances.Count > 0)
            {
                float median = clearances[clearances.Count / 2];
                Debug.Log($"GROUNDCHECK vehicles={vehicles.Count} sampled={sampled} withGround={clearances.Count} noGround={noGround}");
                Debug.Log($"GROUNDCHECK clearance min={clearances[0]:F2} p10={clearances[clearances.Count / 10]:F2} " +
                    $"median={median:F2} p90={clearances[clearances.Count * 9 / 10]:F2} max={clearances[clearances.Count - 1]:F2}");
                Debug.Log($"GROUNDCHECK sunk (clearance < -0.15): {clearances.Count(c => c < -0.15f)}  " +
                    $"floating (> +0.4): {clearances.Count(c => c > 0.4f)}  ok: {clearances.Count(c => c >= -0.15f && c <= 0.4f)}");
            }
            else
            {
                Debug.Log($"GROUNDCHECK no measurements (vehicles={vehicles.Count}, noGround={noGround})");
            }

            EditorApplication.Exit(0);
        }
    }
}
