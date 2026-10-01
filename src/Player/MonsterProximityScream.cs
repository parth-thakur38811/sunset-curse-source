using UnityEngine;
using SunsetCurse.AI;
using SunsetCurse.Core;
using SunsetCurse.Audio;

namespace SunsetCurse.Player
{
    /// <summary>
    /// Plays a scream/scare sting when the monster gets close to the player. Uses hysteresis so it
    /// fires once on approach (not every frame): after a scream it won't fire again until the
    /// player has gotten back beyond <see cref="rearmRange"/>, and never more often than
    /// <see cref="minRepeatDelay"/>. Only triggers at night by default (when she actually hunts).
    ///
    /// Goes through the AudioManager's SFX bus so the Settings volume controls it.
    ///
    /// SETUP: add this to the player object and assign the Scream Clip. It auto-finds the single
    /// MonsterAI in the scene. Needs an AudioManager in the scene.
    /// </summary>
    public class MonsterProximityScream : MonoBehaviour
    {
        [SerializeField] private AudioClip screamClip;
        [Range(0f, 1f)] [SerializeField] private float volume = 1f;

        [Header("Trigger distances")]
        [Tooltip("She screams when she comes within this distance of the player.")]
        [SerializeField] private float screamRange = 14f;
        [Tooltip("The player must get at least this far away again before she can scream once more.")]
        [SerializeField] private float rearmRange = 24f;
        [Tooltip("Never scream more often than this many seconds, even once re-armed.")]
        [SerializeField] private float minRepeatDelay = 8f;

        [Tooltip("Only scream at night (when the monster is active).")]
        [SerializeField] private bool nightOnly = true;

        private bool armed = true;
        private float nextAllowedTime;

        private void Update()
        {
            // Daytime: she's sheltering — keep it armed for the coming night and do nothing.
            if (nightOnly && GameClock.Instance != null &&
                GameClock.Instance.CurrentPhase != GameClock.Phase.Night)
            {
                armed = true;
                return;
            }

            // NEAREST monster each frame (main stalker OR the compound watcher) — whichever
            // creeps up on you screams.
            if (!MonsterRegistry.TryGetNearest(transform.position, out Vector3 monsterPos, out float dist))
                return;

            if (armed && dist <= screamRange && Time.time >= nextAllowedTime)
            {
                // Directional (3D) from the monster — so you hear a DIRECTION, not a guarantee, and
                // it blends with the FalseCues red herrings.
                if (screamClip != null && AudioManager.Instance != null)
                    AudioManager.Instance.PlaySfx3D(screamClip, monsterPos, volume, 60f);
                armed = false;
                nextAllowedTime = Time.time + minRepeatDelay;
            }
            else if (!armed && dist >= rearmRange)
            {
                armed = true;   // she backed off — ready to scare again on the next approach
            }
        }
    }
}
