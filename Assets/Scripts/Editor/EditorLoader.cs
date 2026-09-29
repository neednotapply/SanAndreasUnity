using SanAndreasUnity.Behaviours;
using UGameCore.Utilities;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    public class EditorLoader
    {
        // Startup.unity (build index 0) is the only scene containing the GameManager prefab
        // (Loader, AudioManager, and every other manager singleton). Main.unity/Demo.unity
        // expect GameManager to already exist via DontDestroyOnLoad from that scene, so playing
        // them directly makes e.g. AudioManager.InitFromLoader() NRE on a missing singleton.
        //
        // These are the ONLY scenes that need that bootstrap. Scenes built from exported assets
        // (the VRChat scenes) must not be redirected - forcing them through Startup would launch the
        // GTA importer instead of the scene you pressed Play on.
        private const string StartupScenePath = "Assets/Scenes/Startup.unity";
        private const string GameManagerPrefabPath = "Assets/Prefabs/GameManager.prefab";

        private static readonly string[] ScenesNeedingGameManagerBootstrap =
        {
            "Assets/Scenes/Startup.unity",
            "Assets/Scenes/Main.unity",
            "Assets/Scenes/Demo.unity",
        };

        [InitializeOnLoadMethod]
        static void Init()
        {
            EditorApplication.update -= EditorUpdate;
            EditorApplication.update += EditorUpdate;

            Loader.onLoadingFinished -= OnLoadingFinished;
            Loader.onLoadingFinished += OnLoadingFinished;

            // the active scene can change at any time, so re-evaluate rather than deciding once on load
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChangedInEditMode;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChangedInEditMode;

            UpdatePlayModeStartScene();
        }

        static void OnActiveSceneChangedInEditMode(
            UnityEngine.SceneManagement.Scene previous, UnityEngine.SceneManagement.Scene current)
        {
            UpdatePlayModeStartScene();
        }

        /// <summary>
        /// Redirect Play to the startup scene only when the open scene is one of the importer-driven
        /// scenes. Anything else (notably the exported VRChat scenes) plays as-is.
        /// </summary>
        static void UpdatePlayModeStartScene()
        {
            string activeScenePath = EditorSceneManager.GetActiveScene().path;

            bool needsBootstrap = false;
            foreach (string path in ScenesNeedingGameManagerBootstrap)
            {
                if (string.Equals(activeScenePath, path, System.StringComparison.OrdinalIgnoreCase))
                {
                    needsBootstrap = true;
                    break;
                }
            }

            if (!needsBootstrap)
            {
                EditorSceneManager.playModeStartScene = null;
                return;
            }

            var startupScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(StartupScenePath);
            if (startupScene != null)
                EditorSceneManager.playModeStartScene = startupScene;
        }

        static void EditorUpdate()
        {
            if (!F.IsAppInEditMode)
                return;

            if (Loader.IsLoading)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Loading game data", Loader.LoadingStatus, Loader.GetProgressPerc()))
                {
                    Loader.StopLoading();
                    EditorUtility.ClearProgressBar();
                    return;
                }
            }
        }

        static void OnLoadingFinished()
        {
            EditorUtility.ClearProgressBar();

            if (!F.IsAppInEditMode)
                return;

            if (F.IsInHeadlessMode)
                return;

            if (Loader.HasLoaded)
                EditorUtility.DisplayDialog("", "Successfully loaded game data.", "Ok");
            else
                EditorUtility.DisplayDialog("", "Error in loading game data. Check console for more information.", "Ok");
        }

        [MenuItem(EditorCore.MenuName + "/" + "Load game data")]
        static void MenuItemLoadGameData()
        {
            if (!F.IsAppInEditMode)
            {
                EditorUtility.DisplayDialog("", "This can only be used in edit mode.", "Ok");
                return;
            }

            if (Loader.HasLoaded)
            {
                EditorUtility.DisplayDialog("", "Game data is already loaded.", "Ok");
                return;
            }

            if (null == Loader.Singleton)
            {
                InstantiateGameManager();
            }

            Loader.StartLoading();
        }

        // Loader lives on the same GameManager prefab as AudioManager and every other manager
        // singleton, so a bare Loader-only GameObject leaves those managers missing.
        static void InstantiateGameManager()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GameManagerPrefabPath);
            if (prefab != null)
            {
                PrefabUtility.InstantiatePrefab(prefab);
                return;
            }

            Debug.LogError($"Could not find GameManager prefab at {GameManagerPrefabPath} - " +
                "falling back to a bare Loader object. Some managers (e.g. AudioManager) will be missing.");
            new GameObject("Loader", typeof(Loader));
        }

        [MenuItem(EditorCore.MenuName + "/" + "Change path to GTA")]
        static void MenuItemChangePath()
        {
            if (!F.IsAppInEditMode)
            {
                EditorUtility.DisplayDialog("", "Exit play mode first.", "Ok");
                return;
            }

            string selectedFolder = EditorUtility.OpenFolderPanel("Select GTA installation folder", Config.GamePath ?? "", "");
            if (string.IsNullOrWhiteSpace(selectedFolder))
            {
                return;
            }

            if (!Loader.IsGamePathCorrect(selectedFolder, out string errorMessage))
            {
                EditorUtility.DisplayDialog("", "Selected folder is not valid:\r\n\r\n" + errorMessage, "Ok");
                return;
            }

            Config.SetString(Config.const_game_dir, selectedFolder);
            Config.SaveUserConfig();

            EditorUtility.DisplayDialog("", "Successfully changed path.", "Ok");
        }
    }
}
