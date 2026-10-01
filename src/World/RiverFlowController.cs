using System.Collections.Generic;
using UnityEngine;
using SunsetCurse.Audio;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// The river's brain. Replaces the old sine-wave RiverFlow. Built by Tools ▸ Sunset Curse ▸ 21.
    ///
    ///   • MESH: generates a dense strip (same 10×10 local footprint as a Unity Plane, so the
    ///     transform's scale defines the real size) whose OUTER edge columns drop below the ground
    ///     — the water surface meets the banks with no floating slab edge.
    ///   • WAVES: organic Perlin-noise vertex displacement (two octaves, drifting downstream) —
    ///     runs on top of whatever the water MATERIAL does (the Procedural Water shader adds GPU
    ///     Gerstner crests + foam of its own).
    ///   • CURRENT: a trigger volume covering the water. Anything entering gets a
    ///     <see cref="RiverBuoyancy"/> auto-added and bound — characters wade and get pushed
    ///     downstream, rigidbodies float and drift. Purely local (each peer simulates its own
    ///     local player; owner-authoritative NetworkTransform replicates the result).
    ///   • AUDIO: routes the looping water AudioSource through the SFX mixer bus.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class RiverFlowController : MonoBehaviour
    {
        [Header("Current (world-space; direction = this object's forward)")]
        [Tooltip("Downstream speed in m/s — used by RiverBuoyancy for pushing/washing.")]
        [SerializeField] private float currentSpeed = 2.2f;

        [Header("Water level")]
        [Tooltip("How deep the water is — the trigger volume reaches this far below the surface.")]
        [SerializeField] private float waterDepth = 0.45f;
        [Tooltip("Outer edge columns sink this far below the surface so banks have no visible slab edge.")]
        [SerializeField] private float edgeSkirtDrop = 0.8f;

        [Header("Perlin waves (CPU, organic ripple)")]
        [Tooltip("Peak vertical displacement in metres. 0 = flat (let the shader do all the work).")]
        [SerializeField] private float noiseAmplitude = 0.12f;
        [Tooltip("Noise feature size in metres — bigger = broader swells.")]
        [SerializeField] private float noiseScale = 6f;
        [Tooltip("How fast the noise field drifts downstream.")]
        [SerializeField] private float noiseDrift = 0.9f;
        [SerializeField] private int lengthSegments = 90;
        [SerializeField] private int widthSegments = 16;   // the widened lagoon needs cross-resolution too

        [Header("Flow audio (the looping water AudioSource on this object)")]
        [Tooltip("ON = the water loop slides along the river to the point NEAREST the listener every " +
                 "frame, so the whole ~95m river is audible along its length (a single point source at " +
                 "the centre went silent toward both ends). OFF = plain fixed source.")]
        [SerializeField] private bool flowAudioFollowsListener = true;
        [Tooltip("Volume multiplier while the listener is standing IN the water — the river is all " +
                 "around you, so it should be louder than from the bank.")]
        [SerializeField] private float inWaterVolumeBoost = 1.4f;

        /// <summary>World-space current velocity (direction × speed).</summary>
        public Vector3 Current { get; private set; }

        /// <summary>The scene's river (menu 21 builds exactly one). Null until it wakes.
        /// Used by the bucket-filling and river-ambience systems to ask "am I in the water?".</summary>
        public static RiverFlowController Instance { get; private set; }

        // Cached — nothing is looked up per frame.
        private Mesh mesh;
        private Vector3[] baseVerts;
        private Vector3[] verts;
        private bool[] isSkirt;
        private float worldPerLocalX = 1f, worldPerLocalZ = 1f;
        private Transform tr;
        private readonly Dictionary<Collider, RiverBuoyancy> touching = new Dictionary<Collider, RiverBuoyancy>();
        private AudioSource flowAudio;      // the (possibly moved) water loop
        private float flowBaseVolume;

        private void Awake()
        {
            Instance = this;
            tr = transform;
            Current = tr.forward * currentSpeed;
            worldPerLocalX = Mathf.Max(0.0001f, Mathf.Abs(tr.lossyScale.x));
            worldPerLocalZ = Mathf.Max(0.0001f, Mathf.Abs(tr.lossyScale.z));

            BuildStripMesh();
            EnsureTrigger();

            var src = GetComponent<AudioSource>();
            if (src != null && AudioManager.Instance != null && AudioManager.Instance.SfxGroup != null)
                src.outputAudioMixerGroup = AudioManager.Instance.SfxGroup;
            SetupFlowAudio(src);
        }

        // The loop lives on the river object itself, and moving IT would move the river — so the
        // settings are copied onto a child "emitter" we can slide around, and the original is muted.
        private void SetupFlowAudio(AudioSource src)
        {
            flowAudio = src;
            if (src == null) return;
            flowBaseVolume = src.volume;
            if (!flowAudioFollowsListener) return;

            var go = new GameObject("RiverFlowAudio");
            go.transform.SetParent(tr, false);
            var a = go.AddComponent<AudioSource>();
            a.clip = src.clip;
            a.outputAudioMixerGroup = src.outputAudioMixerGroup;
            a.volume = src.volume;
            a.pitch = src.pitch;
            a.loop = true;
            a.spatialBlend = src.spatialBlend;
            a.rolloffMode = src.rolloffMode;
            a.minDistance = src.minDistance;
            a.maxDistance = src.maxDistance;
            a.dopplerLevel = 0f;   // the emitter jumps along the bank as you move — no pitch wobble
            a.priority = src.priority;

            src.Stop();
            src.enabled = false;
            flowAudio = a;
            if (a.clip != null) a.Play();
        }

        /// <summary>Closest point on the water surface's footprint to <paramref name="worldPos"/>.</summary>
        public Vector3 ClosestPointOnWater(Vector3 worldPos)
        {
            Vector3 lp = tr.InverseTransformPoint(worldPos);
            lp.x = Mathf.Clamp(lp.x, -5f, 5f);
            lp.z = Mathf.Clamp(lp.z, -5f, 5f);
            lp.y = 0f;
            return tr.TransformPoint(lp);
        }

        private void LateUpdate()
        {
            if (!flowAudioFollowsListener || flowAudio == null || flowAudio.transform == tr) return;
            var cam = Camera.main;   // the local player's view (spectators included)
            if (cam == null) return;
            Vector3 ears = cam.transform.position;
            flowAudio.transform.position = ClosestPointOnWater(ears);
            // Wading? Judge by the local player's FEET (camera fallback for spectators).
            Vector3 feet = PlayerInventory.Local != null ? PlayerInventory.Local.transform.position
                                                         : ears - Vector3.up * 2f;
            flowAudio.volume = flowBaseVolume * (IsInWater(feet) ? inWaterVolumeBoost : 1f);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>Water surface height (m, world) at a position — includes the Perlin ripple so
        /// floating objects bob with the water rather than sit on a mathematical plane.</summary>
        public float SurfaceHeightAt(Vector3 worldPos)
        {
            return tr.position.y + SampleNoise(worldPos.x, worldPos.z, Time.time) * noiseAmplitude;
        }

        /// <summary>
        /// Is this world position genuinely IN the water — inside the strip's footprint, below the
        /// (rippling) surface, and at least <paramref name="minBankDistance"/> metres horizontally
        /// in from the nearest edge? Pass 0 for a plain "touching the water" test (river ambience);
        /// the bucket fill uses 2 so players must wade properly in, not dip a toe at the bank.
        /// Standing on the bridge fails the below-surface check, so it never counts as swimming.
        /// </summary>
        public bool IsInWater(Vector3 worldPos, float minBankDistance = 0f)
        {
            // The strip mesh keeps a Unity-Plane-style 10×10 LOCAL footprint (x,z ∈ [-5, 5]);
            // the transform's scale stretches it to real size, so distances convert per axis.
            Vector3 lp = tr.InverseTransformPoint(worldPos);
            if (Mathf.Abs(lp.x) > 5f || Mathf.Abs(lp.z) > 5f) return false;   // outside the water

            float bankDist = Mathf.Min((5f - Mathf.Abs(lp.x)) * worldPerLocalX,
                                       (5f - Mathf.Abs(lp.z)) * worldPerLocalZ);
            if (bankDist < minBankDistance) return false;

            return worldPos.y < SurfaceHeightAt(worldPos);   // feet actually under the surface
        }

        // ── Mesh ─────────────────────────────────────────────────────────────────────────────
        private void BuildStripMesh()
        {
            int nx = Mathf.Clamp(widthSegments, 2, 40);
            int nz = Mathf.Clamp(lengthSegments, 2, 220);

            var v   = new Vector3[(nx + 1) * (nz + 1)];
            var uv  = new Vector2[v.Length];
            var tri = new int[nx * nz * 6];
            isSkirt = new bool[v.Length];

            float skirtLocal = edgeSkirtDrop / Mathf.Max(0.0001f, Mathf.Abs(tr.lossyScale.y));

            for (int z = 0, i = 0; z <= nz; z++)
                for (int x = 0; x <= nx; x++, i++)
                {
                    bool skirt = x == 0 || x == nx;
                    v[i]  = new Vector3(x / (float)nx * 10f - 5f, skirt ? -skirtLocal : 0f, z / (float)nz * 10f - 5f);
                    uv[i] = new Vector2(x / (float)nx, z / (float)nz);
                    isSkirt[i] = skirt;
                }

            for (int z = 0, t = 0; z < nz; z++)
                for (int x = 0; x < nx; x++)
                {
                    int i = z * (nx + 1) + x;
                    tri[t++] = i;     tri[t++] = i + nx + 1; tri[t++] = i + 1;
                    tri[t++] = i + 1; tri[t++] = i + nx + 1; tri[t++] = i + nx + 2;
                }

            mesh = new Mesh { name = "RiverStrip" };
            mesh.vertices = v; mesh.uv = uv; mesh.triangles = tri;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var b = mesh.bounds; b.Expand(new Vector3(0f, 3f, 0f)); mesh.bounds = b;   // never wave-culled

            GetComponent<MeshFilter>().mesh = mesh;   // instance mesh — no shared asset touched
            baseVerts = v;
            verts = (Vector3[])v.Clone();
        }

        private float SampleNoise(float wx, float wz, float t)
        {
            float inv = 1f / Mathf.Max(0.5f, noiseScale);
            // Two octaves drifting downstream — organic, never repeats like a sine does.
            float n1 = Mathf.PerlinNoise(wx * inv + t * noiseDrift * 0.31f,
                                         wz * inv - t * noiseDrift * 0.17f);
            float n2 = Mathf.PerlinNoise(wx * inv * 2.7f - t * noiseDrift * 0.23f,
                                         wz * inv * 2.7f + t * noiseDrift * 0.41f);
            return (n1 - 0.5f) * 2f * 0.7f + (n2 - 0.5f) * 2f * 0.3f;   // −1..1
        }

        private void Update()
        {
            if (mesh == null || noiseAmplitude <= 0f) return;

            float t = Time.time;
            float yScale = Mathf.Max(0.0001f, Mathf.Abs(tr.lossyScale.y));
            for (int i = 0; i < baseVerts.Length; i++)
            {
                if (isSkirt[i]) continue;   // banks stay planted
                Vector3 p = baseVerts[i];
                float h = SampleNoise(p.x * worldPerLocalX, p.z * worldPerLocalZ, t);
                verts[i] = new Vector3(p.x, h * noiseAmplitude / yScale, p.z);
            }
            mesh.vertices = verts;
            mesh.RecalculateNormals();
        }

        // ── Trigger volume + buoyancy hand-off ───────────────────────────────────────────────
        private void EnsureTrigger()
        {
            var box = GetComponent<BoxCollider>();
            if (box == null) box = gameObject.AddComponent<BoxCollider>();
            box.isTrigger = true;
            float yScale = Mathf.Max(0.0001f, Mathf.Abs(tr.lossyScale.y));
            float h = (waterDepth + 0.6f) / yScale;                    // bed → a little above the surface
            box.size   = new Vector3(10f, h, 10f);
            box.center = new Vector3(0f, (0.5f - waterDepth) / yScale * 0.5f, 0f);
        }

        private void OnTriggerEnter(Collider other)
        {
            // Only things that can react: characters and dynamic rigidbodies.
            var host = other.attachedRigidbody != null ? other.attachedRigidbody.gameObject
                     : other.GetComponent<CharacterController>() != null ? other.gameObject : null;
            if (host == null) return;

            if (!host.TryGetComponent(out RiverBuoyancy buoy))
                buoy = host.AddComponent<RiverBuoyancy>();   // zero-wiring: the river equips you
            buoy.SetInWater(this, true);
            touching[other] = buoy;
        }

        private void OnTriggerExit(Collider other)
        {
            if (touching.TryGetValue(other, out var buoy))
            {
                if (buoy != null) buoy.SetInWater(this, false);
                touching.Remove(other);
            }
        }
    }
}
