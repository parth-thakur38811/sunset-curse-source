using System.Collections.Generic;
using UnityEngine;
using SunsetCurse.Core;
using SunsetCurse.AI;
using SunsetCurse.Audio;

namespace SunsetCurse.World
{
    /// <summary>
    /// World object spawned when a player throws their stone distractor. Persistent — the user
    /// asked for it to be NON-DESTRUCTIBLE: once crafted it stays for the rest of the game,
    /// bouncing between players' inventories and the world via pick-up / throw cycles.
    ///
    /// On spawn:
    ///   • Plays a 3D landing thud (audible to all peers because each spawns its own local instance).
    ///   • Tells the monster to investigate this position for 15s (Inventory broadcasts; every
    ///     peer's local MonsterAI gets the call).
    ///
    /// On interact:
    ///   • Server validates + transfers ownership to the picker via Inventory's pickup flow.
    /// </summary>
    public class StoneDistractor : Interactable
    {
        private static readonly Dictionary<int, StoneDistractor> registry = new Dictionary<int, StoneDistractor>();
        private static GameObject hostRoot;

        public int Id { get; private set; }

        /// <summary>Set by InventoryUI's Inspector field on Start, so every spawned distractor
        /// thuds with the same clip. Optional — silent if null.</summary>
        public static AudioClip LandingSfx;

        /// <summary>Optional stone texture (set from InventoryUI's Inspector slot). Applied to the
        /// procedural rock's material so it reads as an actual stone instead of a flat grey cube.</summary>
        public static Texture StoneTexture;

        /// <summary>Optional distracting-stone MODEL (set from InventoryUI's Inspector slot). If
        /// assigned, thrown distractors use this prefab instead of the procedural grey cube; the
        /// texture above is then ignored (the model brings its own look).</summary>
        public static GameObject StonePrefab;

        /// <summary>Assigned model auto-scaled so its largest side is this many metres (0 = keep
        /// the prefab's own size). Set from InventoryUI.</summary>
        public static float ModelTargetSize = 0.5f;

        private const float InvestigateDuration = 15f;

        public override string Verb => "pick up the stone distractor";

        public override void Interact(GameObject interactor)
        {
            Inventory.RequestPickupDistractor(Id);
        }

        // ───────────── Subscribe / spawn / despawn ─────────────

        public static void EnsureSubscribed()
        {
            Inventory.OnDistractorSpawned   -= HandleSpawned;
            Inventory.OnDistractorSpawned   += HandleSpawned;
            Inventory.OnDistractorDespawned -= HandleDespawned;
            Inventory.OnDistractorDespawned += HandleDespawned;
            // Late-join replay for distractors already in the world.
            Inventory.OnReady -= ReplayExisting;
            Inventory.OnReady += ReplayExisting;
            ReplayExisting();
        }

        private static void ReplayExisting() => Inventory.ReplayDistractors(HandleSpawned);

        private static void HandleSpawned(int id, Vector3 pos)
        {
            if (registry.TryGetValue(id, out var stale) && stale != null) Object.Destroy(stale.gameObject);
            registry[id] = SpawnLocal(id, pos);
        }

        private static void HandleDespawned(int id)
        {
            if (registry.TryGetValue(id, out var go) && go != null) Object.Destroy(go.gameObject);
            registry.Remove(id);
        }

        // ───────────── Procedural visual ─────────────

        private static StoneDistractor SpawnLocal(int id, Vector3 pos)
        {
            if (hostRoot == null) hostRoot = new GameObject("StoneDistractors");

            GameObject go;
            if (StonePrefab != null)
            {
                // Assigned model: instantiate + guarantee a trigger collider for the look-ray.
                go = Object.Instantiate(StonePrefab, pos + Vector3.up * 0.15f, Quaternion.identity, hostRoot.transform);
                go.name = $"StoneDistractor_{id}";
                ModelArtifacts.Strip(go);   // kill any embedded camera that would hijack the view
                if (ModelTargetSize > 0f)
                {
                    var rs = go.GetComponentsInChildren<Renderer>(true);
                    if (rs.Length > 0)
                    {
                        Bounds bb = rs[0].bounds;
                        for (int i = 1; i < rs.Length; i++) bb.Encapsulate(rs[i].bounds);
                        float md = Mathf.Max(bb.size.x, Mathf.Max(bb.size.y, bb.size.z));
                        if (md > 0.001f) go.transform.localScale *= ModelTargetSize / md;
                    }
                }
                var mcol = go.GetComponentInChildren<Collider>();
                if (mcol == null)
                {
                    var box = go.AddComponent<BoxCollider>();
                    var r = go.GetComponentInChildren<Renderer>();
                    if (r != null) { box.center = go.transform.InverseTransformPoint(r.bounds.center); box.size = r.bounds.size; }
                    mcol = box;
                }
                mcol.isTrigger = true;
            }
            else
            {
                // Procedural grey rock-ish cube. Small + raycast-able for interaction.
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"StoneDistractor_{id}";
                go.transform.SetParent(hostRoot.transform, false);
                go.transform.position = pos + Vector3.up * 0.25f;
                go.transform.localScale = new Vector3(0.4f, 0.3f, 0.4f);

                var col = go.GetComponent<BoxCollider>();
                col.isTrigger = true;   // walk-through; raycast still hits via QueryTriggerInteraction.Collide

                var mr = go.GetComponent<MeshRenderer>();
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                if (shader != null)
                {
                    var mat = new Material(shader);
                    // With a texture the tint stays near-white so the texture's own colour shows;
                    // without one, fall back to the old flat grey.
                    Color tint = StoneTexture != null ? new Color(0.9f, 0.9f, 0.9f) : new Color(0.45f, 0.45f, 0.48f);
                    mat.SetColor("_BaseColor", tint);
                    mat.SetColor("_Color", tint);
                    if (StoneTexture != null)
                    {
                        mat.SetTexture("_BaseMap", StoneTexture);   // URP/Lit
                        mat.SetTexture("_MainTex", StoneTexture);   // Standard fallback
                    }
                    mr.material = mat;
                }
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            var sd = go.GetComponent<StoneDistractor>() ?? go.AddComponent<StoneDistractor>();
            sd.Id = id;

            // Land thud — 3D so it positions naturally. Every peer plays their own local copy
            // because OnDistractorSpawned fires on each client (so the audio is positionally
            // correct on each machine without networked audio).
            if (LandingSfx != null)
            {
                AudioSource a = go.AddComponent<AudioSource>();
                a.spatialBlend = 1f;
                a.maxDistance = 80f;
                a.rolloffMode = AudioRolloffMode.Linear;
                a.volume = 0.9f;
                a.playOnAwake = false;
                a.PlayOneShot(LandingSfx);
            }

            // Tell every peer's monster to investigate this spot. Only fire from the server (in
            // MP) so the broadcast happens once total, not once per peer.
            if (Unity.Netcode.NetworkManager.Singleton == null || Unity.Netcode.NetworkManager.Singleton.IsServer)
                Inventory.TriggerMonsterInvestigate(pos, InvestigateDuration);

            return sd;
        }
    }
}
