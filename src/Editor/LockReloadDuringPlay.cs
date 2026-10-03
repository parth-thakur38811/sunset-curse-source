using UnityEditor;

namespace SunsetCurse.EditorTools
{
    /// <summary>
    /// EDITOR-ONLY safety net: scripts that change while you're in Play mode are recompiled AFTER
    /// you press Stop — never in the middle of a game.
    ///
    /// Why: by default Unity reloads every script mid-game ("Recompile And Continue Playing"). That
    /// wipes all static state (Inventory.Instance, PlayerInventory.Local, event subscriptions…) and
    /// Netcode does not survive it. Session 25 hit exactly this: after one mid-Play reload,
    /// harvesting silently did nothing, AtmosphereFX threw every frame, and the leaked network
    /// socket made every LATER Play fail to start the host ("port 7777 already in use") until
    /// Unity was restarted.
    ///
    /// Nothing to set up — this runs automatically in the editor and has no effect on builds.
    /// </summary>
    [InitializeOnLoad]
    public static class LockReloadDuringPlay
    {
        // Lock/Unlock calls must stay paired — Unity counts them.
        private static bool locked;

        static LockReloadDuringPlay()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            // EnteredPlayMode comes AFTER Unity's own "reload for play mode", so that still happens.
            if (state == PlayModeStateChange.EnteredPlayMode && !locked)
            {
                EditorApplication.LockReloadAssemblies();
                locked = true;
            }
            else if (state == PlayModeStateChange.ExitingPlayMode && locked)
            {
                EditorApplication.UnlockReloadAssemblies();   // any waiting recompile runs now
                locked = false;
            }
        }
    }
}
