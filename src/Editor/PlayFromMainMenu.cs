using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SunsetCurse.EditorTools
{
    /// <summary>
    /// EDITOR-ONLY convenience: pressing Play from ANY open scene starts the game at the MainMenu.
    ///
    /// Why: the NetworkManager (which spawns the player — even in single-player, via a local NGO
    /// host) lives in the MainMenu scene and is carried into SampleScene when you start a game.
    /// Pressing Play while SampleScene is open skips the menu, so there is no NetworkManager, no
    /// player, and SceneNetworkBootstrap warns "No NetworkManager.Singleton found".
    ///
    /// With this ON you can keep SampleScene open for editing, press Play, and it boots through the
    /// menu as a real game would; stopping Play returns you to SampleScene untouched.
    /// Toggle: Tools ▸ Sunset Curse ▸ Always Play From MainMenu (remembered per machine).
    /// </summary>
    [InitializeOnLoad]
    public static class PlayFromMainMenu
    {
        private const string MenuPath = "Tools/Sunset Curse/Always Play From MainMenu";
        private const string PrefKey = "SunsetCurse.PlayFromMainMenu";
        private const string MainMenuPath = "Assets/_Project/Scenes/MainMenu.unity";

        static PlayFromMainMenu()
        {
            // playModeStartScene isn't saved — re-apply after every script reload. delayCall waits
            // until the AssetDatabase is ready to load the scene asset.
            EditorApplication.delayCall += Apply;
        }

        private static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefKey, true);
            set => EditorPrefs.SetBool(PrefKey, value);
        }

        private static void Apply()
        {
            SceneAsset menu = Enabled ? AssetDatabase.LoadAssetAtPath<SceneAsset>(MainMenuPath) : null;
            if (Enabled && menu == null)
                Debug.LogWarning($"[PlayFromMainMenu] '{MainMenuPath}' not found — Play starts in the open scene.");
            EditorSceneManager.playModeStartScene = menu;
        }

        [MenuItem(MenuPath, priority = 0)]
        private static void Toggle()
        {
            Enabled = !Enabled;
            Apply();
            Debug.Log(Enabled
                ? "[PlayFromMainMenu] ON — Play always starts at the MainMenu (player spawns normally)."
                : "[PlayFromMainMenu] OFF — Play starts in whichever scene is open (no player in SampleScene).");
        }

        [MenuItem(MenuPath, true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }
    }
}
