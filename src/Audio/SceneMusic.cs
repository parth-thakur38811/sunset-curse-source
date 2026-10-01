using UnityEngine;

namespace SunsetCurse.Audio
{
    /// <summary>
    /// Controls this scene's background music through the shared, persistent AudioManager.
    ///   - Assign a Music clip  → that track plays when the scene starts.
    ///   - Leave Music EMPTY    → the scene is SILENT (it stops whatever was playing, e.g. the
    ///                            menu music, when you enter the game). SFX are unaffected.
    /// Because the AudioManager is shared, loading a new scene swaps/stops the music automatically,
    /// and pressing Play directly in a scene behaves the same (handy for testing).
    ///
    /// SETUP: empty GameObject "Music" → add this → assign a clip, or leave empty to silence the
    /// scene. Needs an AudioManager in (or carried into) the scene.
    /// </summary>
    public class SceneMusic : MonoBehaviour
    {
        [Tooltip("Track to play in this scene. LEAVE EMPTY to make the scene silent (stops music).")]
        [SerializeField] private AudioClip music;
        [Range(0f, 1f)] [SerializeField] private float volume = 1f;
        [Tooltip("If this exact track is already playing (e.g. you came back to this scene), " +
                 "don't restart it from the beginning.")]
        [SerializeField] private bool dontRestartIfSame = true;

        private void Start()
        {
            if (AudioManager.Instance == null)
            {
                Debug.LogWarning("[SceneMusic] No AudioManager in the scene — nothing to control.", this);
                return;
            }

            if (music == null)
                AudioManager.Instance.StopAmbient();   // silence this scene (menu music stops here)
            else
                AudioManager.Instance.PlayAmbient(music, volume, dontRestartIfSame);
        }
    }
}
