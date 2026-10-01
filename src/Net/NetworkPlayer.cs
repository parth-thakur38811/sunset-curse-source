using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SunsetCurse.Net
{
    /// <summary>
    /// Sits on the networked Player prefab. NGO spawns one instance of this prefab
    /// per connected client; only the OWNING client should drive their own character
    /// (input, camera, audio listener). Everyone else's player is just a remote-
    /// controlled puppet from this client's point of view — its position is synced
    /// over the network by a `NetworkTransform`, but it should NOT respond to local
    /// input or try to render through its own camera.
    ///
    /// WHY THIS MATTERS (beginner gotcha): if every spawned Player prefab kept its
    /// AudioListener / Cinemachine vcam / PlayerInput / FPS controller enabled, you
    /// would see a chaotic scene where pressing W moves both your character AND every
    /// other player's character locally, you'd hear from multiple ears at once, and
    /// the camera would fight over which body to follow. Owner-gating fixes all of it.
    ///
    /// SETUP IN UNITY (once, when you make the player prefab):
    ///   1. In SampleScene, select the existing PlayerCapsule (Starter Assets first-person).
    ///   2. Drag it into `Assets/_Project/Prefabs/` to create a prefab.
    ///   3. Open the prefab and add:
    ///        • `NetworkObject` (required by NGO for any networked prefab)
    ///        • `NetworkTransform` (NGO ▸ Network Transform — syncs position/rotation
    ///          to remote clients). Tick "In Local Space" if your controller drives
    ///          the transform locally; otherwise leave the default.
    ///        • this `NetworkPlayer` script.
    ///   4. In this script's Inspector, drag the local-only stuff into the lists:
    ///        OWNER-ONLY BEHAVIOURS  →  FirstPersonController, PlayerInput,
    ///                                  CinemachineVirtualCamera, AudioListener,
    ///                                  PlayerInteractor, Flashlight, FootstepAudio,
    ///                                  MonsterProximityScream, PlayerStats UI hooks, ...
    ///        OWNER-ONLY GAMEOBJECTS → any local-only child objects (HUD canvas,
    ///                                 PlayerHUD root, etc.)
    ///   5. Drag the prefab into NetworkManager ▸ "Player Prefab".
    ///   6. DELETE the scene-placed PlayerCapsule from SampleScene — NGO will spawn
    ///      one per client when they connect. (Keep the scene Main Camera + CinemachineBrain;
    ///      those are local-view things, not per-player.)
    /// </summary>
    public class NetworkPlayer : NetworkBehaviour
    {
        [Tooltip("Components that should only be ACTIVE on the local (owning) player. " +
                 "Typical entries: FirstPersonController, PlayerInput, Cinemachine vcam, " +
                 "AudioListener, PlayerInteractor, Flashlight, FootstepAudio, MonsterProximityScream.")]
        [SerializeField] private Behaviour[] ownerOnlyBehaviours;

        [Tooltip("GameObjects that should only be ACTIVE on the local (owning) player. " +
                 "Use this for things like the local HUD root.")]
        [SerializeField] private GameObject[] ownerOnlyGameObjects;

        [Tooltip("Renderers that should be HIDDEN for the local owner (you don't see your own body " +
                 "in first-person, but OTHER players see your character normally). Drop the " +
                 "character's SkinnedMeshRenderer(s) here.")]
        [SerializeField] private Renderer[] ownerHiddenRenderers;

        [Tooltip("Transform that the scene's Cinemachine camera should follow when THIS player is " +
                 "the local owner. Usually 'PlayerCameraRoot' — the empty child at head height that " +
                 "the StarterAssets first-person controller rotates.")]
        [SerializeField] private Transform cameraFollowTarget;

        public override void OnNetworkSpawn()
        {
            ApplyOwnership();
            // Re-run local-player setup whenever a new scene loads — needed for multiplayer where
            // the player NetworkObject is spawned in MainMenu (no Cinemachine vcam there) and then
            // carried over by NGO's scene management into SampleScene (where the vcam DOES exist).
            // OnNetworkSpawn doesn't fire again after a scene transition, so sceneLoaded is the hook.
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        public override void OnNetworkDespawn()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!IsOwner) return;
            ApplyLocalPlayerSceneSetup(scene.name);
        }

        // In builds, the OS cursor un-locks any time the window loses focus (alt-tab, taskbar click,
        // even just a stray click on the title bar). The Editor doesn't replicate this, which is why
        // builds look "broken" but Play mode looks fine. Fix: re-apply the lock whenever focus
        // returns, UNLESS we're in the menu or paused (where the cursor must stay free for the UI).
        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus || !IsOwner) return;
            if (SceneManager.GetActiveScene().name == "MainMenu") return;
            if (Time.timeScale == 0f) return; // paused (single-player) — keep cursor free for the pause menu
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void ApplyOwnership()
        {
            bool isLocal = IsOwner;
            // In MULTIPLAYER, the player spawns in the MainMenu scene first (when host clicks HOST
            // in the lobby) and is carried into SampleScene by the NGO scene-load. If we activate
            // owner-only behaviours WHILE IN THE MENU, the player's PlayerInput fights with the
            // menu's InputSystemUIInputModule and gets into a broken state — movement never works
            // after the scene transition. So: while we're still in MainMenu, REMOTE players get
            // their stuff disabled (as always) AND the local owner gets it disabled too. The
            // owner-only stuff is then enabled by ApplyLocalPlayerSceneSetup once we're in the
            // gameplay scene.
            string activeScene = SceneManager.GetActiveScene().name;
            bool inMenu = activeScene == "MainMenu";
            bool activateOwnerOnly = isLocal && !inMenu;

            EnableOwnerOnlyComponents(activateOwnerOnly);

            // Friendly name in the Hierarchy so you can tell who's who while debugging.
            gameObject.name = isLocal
                ? $"Player [LOCAL — Id {OwnerClientId}]"
                : $"Player [remote — Id {OwnerClientId}]";

            if (isLocal) ApplyLocalPlayerSceneSetup(activeScene);
        }

        // Camera follow + cursor lock + enabling owner-only components for the local owner.
        // Called on initial spawn AND every scene load. Cursor stays unlocked AND the owner-only
        // components stay dormant in the MainMenu scene so the lobby UI keeps working.
        private void ApplyLocalPlayerSceneSetup(string activeSceneName)
        {
            bool inMenu = activeSceneName == "MainMenu";

            // Skip the camera attach in the menu — there's no Cinemachine vcam there by design,
            // and the noisy warning is misleading.
            if (!inMenu && cameraFollowTarget != null) AssignSceneCameraToFollowMe();

            if (!inMenu)
            {
                // Now in the gameplay scene — turn the local player ON for real.
                EnableOwnerOnlyComponents(true);
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private void EnableOwnerOnlyComponents(bool on)
        {
            if (ownerOnlyBehaviours != null)
            {
                foreach (var b in ownerOnlyBehaviours)
                    if (b != null) b.enabled = on;
            }
            if (ownerOnlyGameObjects != null)
            {
                foreach (var go in ownerOnlyGameObjects)
                    if (go != null) go.SetActive(on);
            }
            // Owner-HIDDEN renderers: the OPPOSITE of owner-only. We disable them for the local
            // owner (so the FPS camera isn't blocked by your own character mesh) and ENABLE them
            // for remote viewers (so they see your full body running around).
            if (ownerHiddenRenderers != null)
            {
                bool isLocalOwner = IsOwner;
                foreach (var r in ownerHiddenRenderers)
                    if (r != null) r.enabled = !isLocalOwner;
            }
        }

        // The StarterAssets first-person template puts a Cinemachine virtual camera (usually
        // "PlayerFollowCamera") in the scene with its Follow field pointing at the original
        // scene-placed PlayerCapsule's PlayerCameraRoot. When we prefabbed the player and
        // deleted the scene instance, that Follow reference was lost — so the camera stopped
        // tracking anything and you appeared "frozen". This rewires the scene cam to follow
        // OUR spawned camera root when we're the local player.
        //
        // We search for the base class `CinemachineVirtualCameraBase` so this works whether
        // your scene uses the new Cinemachine 3.x `CinemachineCamera` OR the legacy 2.x
        // `CinemachineVirtualCamera` (both derive from the base and both expose Follow/LookAt).
        private void AssignSceneCameraToFollowMe()
        {
            var cam = Object.FindFirstObjectByType<CinemachineVirtualCameraBase>();
            if (cam != null)
            {
                cam.Follow = cameraFollowTarget;
                cam.LookAt = cameraFollowTarget;
                Debug.Log($"[NetworkPlayer] Cinemachine '{cam.name}' now follows local player.", this);
            }
            else
            {
                Debug.LogWarning("[NetworkPlayer] No Cinemachine virtual camera found in scene — local player camera won't follow.", this);
            }
        }
    }
}
