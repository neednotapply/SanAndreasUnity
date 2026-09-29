using System.IO;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Renders the exported water surface from directly above.
    ///
    /// Water is hard to judge from numbers - a face count says nothing about whether the sea is the right
    /// shape. Seen from above it either reads as the San Andreas coastline or it does not.
    /// </summary>
    public static class GtaWaterRenderCheck
    {
        private const int Size = 900;

        public static void Run()
        {
            string outDir = GetArg("waterShotDir", Path.Combine(Path.GetTempPath(), "watershot"));
            Directory.CreateDirectory(outDir);

            string[] guids = AssetDatabase.FindAssets("t:Mesh", new[] { "Assets/ExportedAssets/Water" });
            if (guids.Length == 0)
            {
                Debug.LogError("No water meshes found");
                EditorApplication.Exit(1);
                return;
            }

            var root = new GameObject("WaterPreview");
            var bounds = new Bounds();
            bool first = true;

            foreach (string guid in guids)
            {
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GUIDToAssetPath(guid));
                if (null == mesh)
                    continue;

                var go = new GameObject(mesh.name);
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;

                var renderer = go.AddComponent<MeshRenderer>();
                var material = new Material(Shader.Find("Unlit/Color"));
                material.color = new Color(0.25f, 0.55f, 0.8f);
                renderer.sharedMaterial = material;

                if (first)
                {
                    bounds = renderer.bounds;
                    first = false;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            Debug.Log($"Water bounds: centre {bounds.center}, size {bounds.size}");

            var camGo = new GameObject("Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.15f, 0.14f, 0.12f);
            cam.orthographic = true;
            // frame the whole surface, with a little margin so the coastline is not clipped
            cam.orthographicSize = Mathf.Max(bounds.size.x, bounds.size.z) * 0.55f;
            cam.transform.position = new Vector3(bounds.center.x, bounds.max.y + 500f, bounds.center.z);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            cam.farClipPlane = 5000f;

            var rt = new RenderTexture(Size, Size, 24);
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            var shot = new Texture2D(Size, Size, TextureFormat.RGB24, false);
            shot.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            shot.Apply();
            RenderTexture.active = null;

            File.WriteAllBytes(Path.Combine(outDir, "water_topdown.png"), shot.EncodeToPNG());
            Debug.Log($"=== water shot written to {outDir} ===");

            EditorApplication.Exit(0);
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
