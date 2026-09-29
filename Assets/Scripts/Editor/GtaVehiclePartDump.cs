using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Prints every part of a vehicle prefab: where its frame sits, where its mesh actually is, and
    /// whether it is switched on.
    ///
    /// Renders answer "does it look wrong"; this answers "which part is wrong and by how much", which is
    /// what repeated guesswork about the vehicle geometry has been missing.
    /// </summary>
    public static class GtaVehiclePartDump
    {
        public static void Run()
        {
            string names = GetArg("partDumpNames", "admiral");

            foreach (string name in names.Split(','))
            {
                string[] found = AssetDatabase.FindAssets($"{name.Trim()} t:Prefab",
                    new[] { "Assets/ExportedAssets/Prefabs/Vehicles" });

                string path = found
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .FirstOrDefault(p => System.IO.Path.GetFileNameWithoutExtension(p)
                        .Equals(name.Trim(), System.StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrEmpty(path))
                {
                    Debug.Log($"PARTDUMP {name}: prefab not found");
                    continue;
                }

                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(go);

                Debug.Log($"PARTDUMP === {name} ===");

                foreach (var mf in instance.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh == null)
                        continue;

                    Vector3 framePos = mf.transform.localPosition;
                    Vector3 meshCentre = mf.sharedMesh.bounds.center;
                    Vector3 world = mf.transform.TransformPoint(meshCentre);

                    Debug.Log($"PARTDUMP {mf.name,-18} active={(mf.gameObject.activeInHierarchy ? 1 : 0)} " +
                        $"frame=({framePos.x,7:F2},{framePos.y,6:F2},{framePos.z,7:F2}) " +
                        $"meshC=({meshCentre.x,7:F2},{meshCentre.y,6:F2},{meshCentre.z,7:F2}) " +
                        $"world=({world.x,7:F2},{world.y,6:F2},{world.z,7:F2}) " +
                        $"size={mf.sharedMesh.bounds.size.magnitude,6:F2}");

                    // materials per renderer: which texture, which paint slot, which colour
                    var rend = mf.GetComponent<MeshRenderer>();
                    if (rend != null && GetArg("partDumpMaterials", "0") == "1")
                    {
                        var mats = rend.sharedMaterials;
                        for (int m = 0; m < mats.Length; m++)
                        {
                            var mat = mats[m];
                            if (mat == null) { Debug.Log($"PARTDUMP    mat[{m}] NULL"); continue; }

                            string texName = mat.mainTexture != null ? mat.mainTexture.name : "NONE";
                            int idx = mat.HasProperty(Importing.Conversion.Geometry.CarColorIndexId)
                                ? mat.GetInt(Importing.Conversion.Geometry.CarColorIndexId) : -1;
                            Color col = mat.HasProperty("_CarColor") ? mat.GetColor("_CarColor") : Color.magenta;

                            Debug.Log($"PARTDUMP    mat[{m}] {mat.name,-16} tex={texName,-18} slot={idx} " +
                                $"carColor=({col.r:F2},{col.g:F2},{col.b:F2}) shader={mat.shader.name}");
                        }
                    }
                }

                Object.DestroyImmediate(instance);
            }

            EditorApplication.Exit(0);
        }

        private static string GetArg(string name, string fallback)
        {
            string search = "-" + name + ":";
            string arg = System.Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith(search));
            return arg == null ? fallback : arg.Substring(search.Length);
        }
    }
}
