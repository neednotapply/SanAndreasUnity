using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Gives street lamps their glow back.
    ///
    /// GTA does not model lamp light as geometry. Each model carries a 2dfx section listing light positions,
    /// colours and corona sizes, and the game builds the visible lamp from that at runtime. Nothing in the
    /// export reads 2dfx, so every street lamp in the world is currently a dark pole - which is also why the
    /// traffic lights have no lamps.
    ///
    /// The glow is added to the exported PREFAB rather than to placements, so all thousands of instances
    /// across the map inherit it for free and it streams with the cell it belongs to. There is no runtime
    /// cost at all: no behaviour, no Update, just a small emissive quad.
    ///
    /// The lamps stay lit during the day. Toggling them would mean tracking thousands of objects from Udon,
    /// which is far more expensive than the mild oddity of a lamp that glows at noon.
    /// </summary>
    public static class StreetLightExporter
    {
        private const string MapObjectsFolder = "Assets/ExportedAssets/Prefabs/MapObjects";
        private const string LampMaterialFolder = "Assets/ExportedAssets/Materials";

        /// <summary>
        /// Name fragments that identify a lamp. 2dfx lights appear on many models - lit windows, signs,
        /// vending machines - and lighting every one of them would be a different and much larger change.
        /// Street lighting is what the world is actually missing at night.
        ///
        /// Traffic lights are included so that any lamps left by an earlier run get cleaned up, but they are
        /// never given a glow - see <see cref="IsExcludedFromLighting"/>.
        /// </summary>
        private static readonly string[] LampNameFragments =
        {
            "streetlamp", "lamppost", "lampost", "lamp_", "_lamp", "trafficlight",
        };

        /// <summary>
        /// Models whose 2dfx lights must NOT be turned into permanent glows.
        ///
        /// A traffic light carries a light for every lamp - red, amber and green, on each face. Lighting
        /// them all at once is not a working traffic light, it is a broken one, and it is precisely the
        /// fault this was meant to fix. Showing only one colour would need per-instance control, and there
        /// are thousands of these across the streamed map: far too many for Udon to drive individually.
        ///
        /// So they stay dark. An unlit signal reads as scenery; a signal showing all three at once reads as
        /// a bug. The AI already obeys the light cycle through GtaTrafficLightController - it is only the
        /// visible lamp that is missing.
        /// </summary>
        private static bool IsExcludedFromLighting(string modelName)
        {
            return modelName.ToLowerInvariant().Contains("trafficlight");
        }

        public static void Export()
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { MapObjectsFolder });

            var candidates = new List<GameObject>();

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string name = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

                foreach (string fragment in LampNameFragments)
                {
                    if (name.Contains(fragment))
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        if (prefab != null)
                            candidates.Add(prefab);

                        break;
                    }
                }
            }

            Debug.Log($"Street light candidates: {candidates.Count} models");

            int lit = 0;
            int totalLights = 0;
            int noEffects = 0;

            foreach (var prefab in candidates)
            {
                int added = AddLightsToPrefab(prefab);

                if (added > 0)
                {
                    lit++;
                    totalLights += added;
                }
                else
                {
                    noEffects++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"Street lights: {lit} models given {totalLights} lamps " +
                $"({noEffects} candidates had no 2dfx lights)");
        }

        private static int AddLightsToPrefab(GameObject prefab)
        {
            Importing.Conversion.Geometry.GeometryParts geometryParts;

            try
            {
                geometryParts = Importing.Conversion.Geometry.Load(prefab.name, new string[0]);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"Could not load geometry for '{prefab.name}': {e.Message}");
                return 0;
            }

            if (null == geometryParts)
                return 0;

            // gather this model's 2dfx lights - position and colour are what matter here
            var lights = new List<Importing.RenderWareStream.TwoDEffect.Light>();

            foreach (var geometry in geometryParts.Geometry)
            {
                var effects = geometry.RwGeometry != null ? geometry.RwGeometry.TwoDEffect : null;
                if (effects == null || effects.Lights == null)
                    continue;

                lights.AddRange(effects.Lights);
            }

            if (lights.Count == 0)
                return 0;

            string path = AssetDatabase.GetAssetPath(prefab);
            var contents = PrefabUtility.LoadPrefabContents(path);

            int added = 0;

            try
            {
                // clear any lamps from a previous run, so re-exporting replaces rather than accumulates
                var stale = new List<GameObject>();
                foreach (Transform child in contents.transform)
                {
                    // "Lamp" also catches objects from the first version of this exporter
                    if (child.name.StartsWith("LampGlow") || child.name.StartsWith("Lamp"))
                        stale.Add(child.gameObject);
                }

                foreach (var go in stale)
                    Object.DestroyImmediate(go);

                // Centre of this model's own lights, used to push each glow out through its lens.
                //
                // The model's overall centre is the wrong reference: on a traffic light it sits halfway
                // down the arm, so offsetting from it shoves the lamps further along the arm rather than
                // out through the faces they belong to. The lights' own centroid is the middle of the
                // signal head, and pushing away from that moves each lamp out of the housing it is in.
                Vector3 lightCentroid = Vector3.zero;
                foreach (var l in lights)
                    lightCentroid += l.Position;

                lightCentroid /= lights.Count;

                // stripped above, but never re-lit
                if (IsExcludedFromLighting(prefab.name))
                {
                    if (stale.Count > 0)
                        PrefabUtility.SaveAsPrefabAsset(contents, path);

                    Debug.Log($"  {prefab.name}: excluded from lighting ({lights.Count} 2dfx lights ignored)");
                    return 0;
                }

                foreach (var light in lights)
                {
                    var glow = new GameObject($"LampGlow{added}");
                    glow.transform.SetParent(contents.transform, false);

                    // A 2dfx light sits at the lamp lens, which is usually recessed inside its housing -
                    // fine for GTA, which draws coronas as always-on-top billboards, but a physical quad
                    // placed there is simply hidden inside the traffic light. Nudging it outward from the
                    // model's centre brings it clear of the housing without making lamps visible through
                    // walls, which is what an always-on-top material would do.
                    Vector3 outward = light.Position - lightCentroid;
                    // vertical stacking (red above amber above green) is not a direction to push in
                    outward.y = 0f;

                    Vector3 offset = outward.sqrMagnitude > 0.0001f
                        ? outward.normalized * 0.18f
                        : Vector3.zero;

                    glow.transform.localPosition = light.Position + offset;

                    Debug.Log($"  {prefab.name}: 2dfx light at {light.Position} " +
                        $"corona {light.CoronaSize:0.00} colour {light.Color}");

                    // Corona sizes in the data are generous - they describe a screen-space flare, not a
                    // physical object - so they are scaled well down. A lamp that reads as a small bright
                    // point is right; one sized straight from the data covers the whole lamp head.
                    float size = Mathf.Clamp(light.CoronaSize * 0.3f, 0.18f, 0.8f);

                    var material = GetLampMaterial(light.Color);

                    // Two quads crossed at a right angle. A single quad is invisible edge-on and reads as a
                    // flat disc face-on; crossing them costs four triangles and looks like a glow from any
                    // direction, which is the standard trick for cheap point lights.
                    AddGlowQuad(glow.transform, material, size, 0f);
                    AddGlowQuad(glow.transform, material, size, 90f);

                    added++;
                }

                if (added > 0)
                    PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            return added;
        }

        /// <summary> One plane of a crossed-quad glow. </summary>
        private static void AddGlowQuad(Transform parent, Material material, float size, float yaw)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = $"Glow_{yaw:0}";
            quad.transform.SetParent(parent, false);
            quad.transform.localPosition = Vector3.zero;
            quad.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            quad.transform.localScale = Vector3.one * size;

            // a glow must never block a shot, a step or a vehicle
            var collider = quad.GetComponent<Collider>();
            if (collider != null)
                Object.DestroyImmediate(collider);

            var renderer = quad.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        private static readonly Dictionary<string, Material> s_lampMaterials =
            new Dictionary<string, Material>();

        /// <summary>
        /// One material per distinct lamp colour, shared across every lamp using it.
        ///
        /// 2dfx colours repeat heavily - most street lighting is the same warm white - so caching by colour
        /// keeps this to a handful of materials rather than one per lamp, which matters for batching.
        /// </summary>
        private static Texture2D s_glowTexture;
        private static bool s_reportedShader;

        /// <summary>
        /// A soft round glow, generated once and saved beside the lamp materials.
        ///
        /// The falloff is squared rather than linear: a linear ramp still reads as a disc with a visible
        /// rim, while squaring concentrates the brightness in the middle and fades the edge out to nothing,
        /// which is what a light source at distance actually looks like.
        /// </summary>
        private static Texture2D GetGlowTexture()
        {
            if (s_glowTexture != null)
                return s_glowTexture;

            string path = $"{LampMaterialFolder}/LampGlow.png";

            s_glowTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (s_glowTexture != null)
                return s_glowTexture;

            const int Size = 64;

            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            var pixels = new Color32[Size * Size];
            float half = Size * 0.5f;

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;

                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - d);
                    a *= a;

                    byte v = (byte)Mathf.RoundToInt(a * 255f);

                    // white with the falloff in alpha, so one texture serves every lamp colour
                    pixels[y * Size + x] = new Color32(255, 255, 255, v);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();

            System.IO.Directory.CreateDirectory(LampMaterialFolder);
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (importer != null)
            {
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
            }

            s_glowTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            return s_glowTexture;
        }

        private static Material GetLampMaterial(Color color)
        {
            // quantise, so near-identical colours share one material instead of creating dozens
            int r = Mathf.RoundToInt(color.r * 8f);
            int g = Mathf.RoundToInt(color.g * 8f);
            int b = Mathf.RoundToInt(color.b * 8f);

            string key = $"{r}_{g}_{b}";

            if (s_lampMaterials.TryGetValue(key, out Material cached))
                return cached;

            string path = $"{LampMaterialFolder}/Lamp_{key}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);

            // An existing material from an earlier export is upgraded rather than reused.
            //
            // Returning it untouched is what let the old opaque-square lamps survive a rebuild: the
            // material already existed, so the code that builds a proper glow never ran. A material with
            // no texture on it is one of the old ones.
            if (material != null
                && (material.shader == null || material.shader.name == "Unlit/Color"))
            {
                AssetDatabase.DeleteAsset(path);
                material = null;
            }

            if (material == null)
            {
                // A glow needs a soft edge and additive blending.
                //
                // This was Unlit/Color on a quad, which is an opaque flat square of colour - so every
                // street lamp showed as a solid orange tile stuck to the lamp head rather than as light.
                // Additive blending with a radial falloff gives the opposite: brightest at the centre,
                // gone by the edges, and never occluding what is behind it.
                var shader = Shader.Find("Legacy Shaders/Particles/Additive");

                if (shader == null)
                    shader = Shader.Find("Particles/Additive");

                if (shader == null)
                    shader = Shader.Find("Unlit/Transparent");

                if (shader == null)
                {
                    // Nothing usable resolved. A Material built on a null shader silently swallows every
                    // property set on it, which is how the lamps kept coming out as plain squares even
                    // after the texture existed - so say so rather than producing one.
                    Debug.LogError("No additive shader available for lamp glows - lamps will look wrong. "
                        + "Tried: Legacy Shaders/Particles/Additive, Particles/Additive, Unlit/Transparent");
                    shader = Shader.Find("Sprites/Default");
                }

                if (!s_reportedShader)
                {
                    s_reportedShader = true;
                    Debug.Log($"Lamp glow shader: {(shader != null ? shader.name : "NONE")}");
                }

                material = new Material(shader);

                var tint = new Color(r / 8f, g / 8f, b / 8f, 1f);

                // the legacy particle shaders tint through _TintColor and halve it, so it is doubled back
                if (material.HasProperty("_TintColor"))
                    material.SetColor("_TintColor", tint);
                else
                    material.color = tint;

                var glowTex = GetGlowTexture();
                if (glowTex == null)
                    Debug.LogError("Lamp glow texture failed to load - lamps will be untextured squares");

                material.mainTexture = glowTex;

                System.IO.Directory.CreateDirectory(LampMaterialFolder);
                AssetDatabase.CreateAsset(material, path);

                // Verified by reading the saved asset back, not by trusting the assignment. Twice now a
                // lamp material has looked correct in memory and come out of the exporter untextured.
                var check = AssetDatabase.LoadAssetAtPath<Material>(path);
                Debug.Log($"Lamp material {System.IO.Path.GetFileName(path)}: " +
                    $"shader={(check != null && check.shader != null ? check.shader.name : "NULL")} " +
                    $"mainTexture={(check != null && check.mainTexture != null ? check.mainTexture.name : "NULL")} " +
                    $"(assigned {(glowTex != null ? glowTex.name : "NULL")})");
            }

            s_lampMaterials[key] = material;
            return material;
        }
    }
}
