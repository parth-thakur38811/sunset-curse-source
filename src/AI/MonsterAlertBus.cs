using System;
using System.Collections.Generic;
using UnityEngine;

namespace SunsetCurse.AI
{
    /// <summary>
    /// Server-side alert hub for ritual-related monster triggers. World scripts REPORT events
    /// here; monster AIs SUBSCRIBE — so the ritual/potion code never needs to know how many
    /// monsters exist or what type they are (main scent stalker, compound watcher, future ones).
    ///
    /// Only ever invoked on the HOST (the reporters are server-side code paths), which matches
    /// the host-authoritative AI pattern — subscribers still guard with their own IsServer checks.
    /// (Touched to force a Unity reimport — the first import raced Unity's asset refresh.)
    /// </summary>
    public static class MonsterAlerts
    {
        /// <summary>A player took ANY valuable from the compound (ritual potion, battery,
        /// transformer, handheld radio, or golden key) — a scent flare at the picker's position.
        /// Fired server-side by NightValuableSpawner / RadioItemSpawner after they validate the
        /// pickup. The main stalker treats this as "the compound has been disturbed" and is now
        /// allowed inside to chase (UPDATE #5); the watcher inspects the spot.</summary>
        public static event Action<Vector3> OnValuableTaken;

        /// <summary>The escape ritual just started at this position. Fired server-side by
        /// RitualSiteNet. Monsters hard-override whatever they're doing and head there.</summary>
        public static event Action<Vector3> OnRitualStarted;

        /// <summary>Something noisy hit the ground — an item dropped from an inventory, radio
        /// parts clattering off a corpse. Fired server-side by the drop paths; the compound
        /// watcher investigates the spot. (Deliberately NOT the stone distractor — she is immune
        /// to it by design.)</summary>
        public static event Action<Vector3, float> OnLoudNoise;

        public static void ReportValuableTaken(Vector3 pos) => OnValuableTaken?.Invoke(pos);
        public static void ReportRitualStarted(Vector3 pos) => OnRitualStarted?.Invoke(pos);
        public static void ReportLoudNoise(Vector3 pos, float loudness) => OnLoudNoise?.Invoke(pos, loudness);
    }

    /// <summary>
    /// Every active monster (MonsterAI, CompoundWatcherAI, whatever comes next) registers its
    /// transform here. Player-side proximity effects (heartbeat / scream / vignette) ask for the
    /// NEAREST monster each frame instead of hard-caching "the one MonsterAI" — so both monsters
    /// scare you, and adding a third someday needs no changes anywhere else.
    /// </summary>
    public static class MonsterRegistry
    {
        private static readonly List<Transform> monsters = new List<Transform>();

        public static void Register(Transform t)
        {
            if (t != null && !monsters.Contains(t)) monsters.Add(t);
        }

        public static void Unregister(Transform t) => monsters.Remove(t);

        /// <summary>Position of + distance to the monster nearest to <paramref name="from"/>.
        /// False when no monster is registered (e.g. main menu).</summary>
        public static bool TryGetNearest(Vector3 from, out Vector3 pos, out float dist)
        {
            pos = default;
            dist = float.MaxValue;
            bool found = false;
            for (int i = monsters.Count - 1; i >= 0; i--)
            {
                var t = monsters[i];
                if (t == null) { monsters.RemoveAt(i); continue; }   // destroyed — tidy up
                float d = Vector3.Distance(from, t.position);
                if (d < dist) { dist = d; pos = t.position; found = true; }
            }
            return found;
        }
    }
}
