using System.Collections.Generic;
using UdonSharp;
using UdonSharp.Compiler;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Creates a <see cref="UdonSharpProgramAsset"/> for each of our UdonSharpBehaviour scripts, and
    /// compiles them.
    ///
    /// This matters because UdonSharp only compiles behaviours that have a program asset. A script with no
    /// program asset is still compiled by Unity as ordinary C#, so it looks fine - while Udon-specific
    /// restrictions (no generics, no static fields, non-exposed API calls) go completely unchecked.
    /// Generating the assets is what turns "valid C#" into "actually valid Udon".
    /// </summary>
    public static class UdonProgramAssetGenerator
    {
        private const string UdonScriptsFolder = "Assets/Scripts/VRChat";
        private const string ProgramAssetsFolder = "Assets/Scripts/VRChat/UdonPrograms";

        [MenuItem(EditorCore.MenuName + "/" + "Generate Udon program assets")]
        public static void GenerateAndCompile()
        {
            int created = GenerateProgramAssets();

            Debug.Log($"Udon program assets: {created} created. Compiling...");

            UdonSharpCompilerV1.CompileSync();

            Debug.Log("Udon compile finished.");
        }

        public static int GenerateProgramAssets()
        {
            if (!AssetDatabase.IsValidFolder(ProgramAssetsFolder))
                AssetDatabase.CreateFolder(UdonScriptsFolder, "UdonPrograms");

            // map of scripts that already have a program asset, so re-running is a no-op
            var existingScripts = new HashSet<MonoScript>();
            foreach (var programAsset in UdonSharpProgramAsset.GetAllUdonSharpPrograms())
            {
                if (programAsset != null && programAsset.sourceCsScript != null)
                    existingScripts.Add(programAsset.sourceCsScript);
            }

            string[] scriptGuids = AssetDatabase.FindAssets("t:MonoScript", new[] { UdonScriptsFolder });
            int created = 0;

            foreach (string guid in scriptGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(path);

                if (null == monoScript)
                    continue;

                System.Type scriptClass = monoScript.GetClass();

                if (null == scriptClass || !scriptClass.IsSubclassOf(typeof(UdonSharpBehaviour)))
                    continue;

                if (existingScripts.Contains(monoScript))
                    continue;

                var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
                programAsset.sourceCsScript = monoScript;

                string assetPath = $"{ProgramAssetsFolder}/{scriptClass.Name}.asset";
                AssetDatabase.CreateAsset(programAsset, assetPath);
                created++;

                Debug.Log($"Created Udon program asset for {scriptClass.Name} at {assetPath}");
            }

            if (created > 0)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            return created;
        }
    }
}
