using System.IO;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Renders exported vehicles to PNGs so they can simply be looked at.
    ///
    /// Bounds-based checks proved untrustworthy here - a known-good vehicle measured as badly as a visibly
    /// broken one, so the numbers could not distinguish them. A picture answers the actual question, which
    /// is whether the car looks like a car.
    /// </summary>
    public static class GtaVehicleRenderCheck
    {
        private const int Size = 512;

        public static void Run()
        {
            string outDir = GetArg("vehicleShotDir", Path.Combine(Path.GetTempPath(), "vehicleshots"));
            Directory.CreateDirectory(outDir);

            string list = GetArg("vehicleShotNames", "admiral,blade,blistac,elegy,moonbeam,bus");

            var camGo = new GameObject("ShotCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.25f, 0.28f, 0.32f);

            var lightGo = new GameObject("ShotLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightGo.transform.rotation = Quaternion.Euler(40f, 150f, 0f);

            // The key light points away from the camera, so the side being photographed sat in shadow and
            // dark trucks came out solid black - useless for judging textures. Flat ambient plus a fill
            // light from the camera's side lets the surface colour show.
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.62f, 0.62f, 0.62f);

            var fillGo = new GameObject("ShotFill");
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.intensity = 0.9f;
            fillGo.transform.rotation = Quaternion.Euler(25f, -100f, 0f);

            foreach (string name in list.Split(','))
            {
                string trimmed = name.Trim();
                if (trimmed.Length == 0)
                    continue;

                RenderOne(trimmed, cam, outDir);
            }

            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(lightGo);
            Object.DestroyImmediate(fillGo);

            Debug.Log($"=== vehicle shots written to {outDir} ===");
            EditorApplication.Exit(0);
        }

        private static void RenderOne(string modelName, Camera cam, string outDir)
        {
            // folder is configurable so this works for map objects as well as vehicles
            string folder = GetArg("vehicleShotFolder", "Assets/ExportedAssets/Prefabs/Vehicles");

            string[] guids = AssetDatabase.FindAssets(modelName + " t:Prefab", new[] { folder });

            GameObject prefab = null;
            foreach (string guid in guids)
            {
                var candidate = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (candidate != null && candidate.name.ToLowerInvariant() == modelName.ToLowerInvariant())
                {
                    prefab = candidate;
                    break;
                }
            }

            if (null == prefab)
            {
                Debug.LogWarning($"  no prefab named '{modelName}'");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.transform.position = Vector3.zero;
            instance.transform.rotation = Quaternion.identity;

            // Test the double-transform theory: if every part's mesh is already in model space, then the
            // frame offsets are being applied on top of coordinates that already include them, and zeroing
            // those offsets should assemble the car correctly.
            // Candidate fix: vehicle part meshes are in model space, but each part hangs under a *_dummy
            // frame that already carries that offset, so every part is displaced by its own offset. Rebasing
            // the vertices into the frame's space makes the hierarchy correct without flattening it, which
            // keeps the wheel dummies usable.
            if (GetArg("vehicleShotRebaseMeshes", "0") == "1")
            {
                foreach (var mf in instance.GetComponentsInChildren<MeshFilter>())
                {
                    if (null == mf.sharedMesh)
                        continue;

                    Vector3 offset = instance.transform.InverseTransformPoint(mf.transform.position);
                    if (offset.sqrMagnitude < 0.000001f)
                        continue;

                    var copy = Object.Instantiate(mf.sharedMesh);
                    var verts = copy.vertices;
                    for (int i = 0; i < verts.Length; i++)
                        verts[i] -= offset;

                    copy.vertices = verts;
                    copy.RecalculateBounds();
                    mf.sharedMesh = copy;
                }
            }

            if (GetArg("vehicleShotZeroFrames", "0") == "1")
            {
                foreach (var t in instance.GetComponentsInChildren<Transform>())
                {
                    if (t != instance.transform)
                        t.localPosition = Vector3.zero;
                }
            }

            // side-on by default: wheels, door lines and any displaced panel are all visible from here
            string[] camParts = GetArg("vehicleShotCamera", "8,1.6,0").Split(',');
            cam.transform.position = new Vector3(
                float.Parse(camParts[0]), float.Parse(camParts[1]), float.Parse(camParts[2]));
            // aim point is configurable: a traffic light's signal head is metres from its pivot, so
            // framing the origin shows only the bottom of the pole
            string[] targetParts = GetArg("vehicleShotTarget", "0,0.2,0").Split(',');
            Vector3 target = new Vector3(
                float.Parse(targetParts[0]), float.Parse(targetParts[1]), float.Parse(targetParts[2]));

            cam.transform.LookAt(target);

            int visible = 0;
            foreach (var r in instance.GetComponentsInChildren<MeshRenderer>())
            {
                if (r.gameObject.activeInHierarchy && r.enabled)
                    visible++;
            }

            Debug.Log($"  {modelName}: {visible} visible mesh parts");

            var rt = new RenderTexture(Size, Size, 24);
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            var shot = new Texture2D(Size, Size, TextureFormat.RGB24, false);
            shot.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            shot.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;

            File.WriteAllBytes(Path.Combine(outDir, modelName + ".png"), shot.EncodeToPNG());
            Debug.Log($"  rendered {modelName}");

            Object.DestroyImmediate(shot);
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(instance);
        }

        private static string GetArg(string name, string fallback)
        {
            string search = "-" + name + ":";
            foreach (string arg in System.Environment.GetCommandLineArgs())
            {
                if (arg.StartsWith(search))
                    return arg.Substring(search.Length);
            }

            return fallback;
        }
    }
}
