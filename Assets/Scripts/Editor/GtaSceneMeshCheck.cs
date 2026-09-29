using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Opens the built test scene and counts how many renderers actually have a mesh.
    ///
    /// A log line saying "45,059 placements" does not show the city is visible: an earlier export
    /// reported success while every map object pointed at deleted meshes and drew nothing.
    /// </summary>
    public static class GtaSceneMeshCheck
    {
        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/VRChatTest.unity", OpenSceneMode.Single);

            int filters = 0, nullMesh = 0, renderersNoMaterial = 0;
            Bounds bounds = new Bounds();
            bool first = true;

            var nullByName = new System.Collections.Generic.Dictionary<string, int>();
            var nullByTopParent = new System.Collections.Generic.Dictionary<string, int>();

            foreach (var mf in Object.FindObjectsOfType<MeshFilter>(true))
            {
                filters++;
                if (mf.sharedMesh == null)
                {
                    nullMesh++;
                    string n = mf.name;
                    nullByName[n] = nullByName.TryGetValue(n, out int a) ? a + 1 : 1;

                    // which top-level scene object it hangs under: world cells, vehicles, weapons...
                    Transform t = mf.transform;
                    while (t.parent != null) t = t.parent;
                    string top = t.name;
                    if (top.StartsWith("Cell_")) top = "Cell_*";
                    nullByTopParent[top] = nullByTopParent.TryGetValue(top, out int b) ? b + 1 : 1;
                    continue;
                }

                var r = mf.GetComponent<MeshRenderer>();
                if (r != null && (r.sharedMaterials.Length == 0 || r.sharedMaterial == null))
                    renderersNoMaterial++;

                if (r != null)
                {
                    if (first) { bounds = r.bounds; first = false; }
                    else bounds.Encapsulate(r.bounds);
                }
            }

            Debug.Log($"MESHCHECK meshFilters={filters} nullMesh={nullMesh} " +
                $"resolved={filters - nullMesh} noMaterial={renderersNoMaterial}");
            var names = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(nullByName);
            names.Sort((x, y) => y.Value.CompareTo(x.Value));
            for (int i = 0; i < names.Count && i < 25; i++)
                Debug.Log($"MESHCHECK nullName {names[i].Key} x{names[i].Value}");

            var tops = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(nullByTopParent);
            tops.Sort((x, y) => y.Value.CompareTo(x.Value));
            for (int i = 0; i < tops.Count && i < 12; i++)
                Debug.Log($"MESHCHECK nullUnder {tops[i].Key} x{tops[i].Value}");

            Debug.Log($"MESHCHECK renderer bounds min={bounds.min} max={bounds.max}");

            EditorApplication.Exit(nullMesh > filters / 10 ? 1 : 0);
        }
    }
}
