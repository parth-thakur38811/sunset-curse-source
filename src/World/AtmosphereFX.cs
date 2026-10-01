using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// Local atmosphere around the camera: low drifting GROUND MIST and floating DUST MOTES.
    ///
    /// The trick that makes it spooky: both use a LIT particle material (URP Particles/Simple Lit).
    /// At night the world is near-black, so the mist and dust are almost invisible — until light
    /// hits them. Sweep your flashlight and the beam turns into a solid shaft through the fog, with
    /// specks of dust glittering in it; the blood moon stains the mist red; by day it's a faint
    /// grey haze hugging the ground.
    ///
    /// Purely LOCAL and cosmetic: each peer builds its own particles around its own camera
    /// (spectators included). Nothing is networked.
    ///
    /// SETUP: lives on the "Directional Light" object next to DayNightLighting (which supplies the
    /// darkness value). Assign the GroundMist + DustMotes materials (Assets/_Project/Art/FX).
    /// Either material left empty = that layer is skipped.
    /// </summary>
    public class AtmosphereFX : MonoBehaviour
    {
        [Header("Ground mist")]
        [Tooltip("URP Particles/Simple Lit material (Assets/_Project/Art/FX/GroundMist).")]
        [SerializeField] private Material mistMaterial;
        [SerializeField] private int mistParticles = 70;
        [Tooltip("Mist fills a disc of this radius (m) around the camera and follows you.")]
        [SerializeField] private float mistRadius = 38f;
        [Tooltip("Billboard size range (m).")]
        [SerializeField] private Vector2 mistSize = new Vector2(7f, 14f);
        [Tooltip("Height range of the mist centres above the ground (m).")]
        [SerializeField] private Vector2 mistHeight = new Vector2(0.3f, 1.8f);
        [SerializeField] private Vector2 mistLifetime = new Vector2(18f, 30f);
        [Tooltip("Slow sideways drift (m/s).")]
        [SerializeField] private float mistDrift = 0.35f;
        [Tooltip("Mist albedo — the scene's light decides how bright it actually looks.")]
        [SerializeField] private Color mistColor = new Color(0.55f, 0.58f, 0.6f);
        [Range(0f, 1f)] [SerializeField] private float dayMistAlpha = 0.05f;
        [Range(0f, 1f)] [SerializeField] private float nightMistAlpha = 0.11f;
        [Tooltip("Mist opacity multiplier while it rains.")]
        [SerializeField] private float rainMistBoost = 1.5f;
        [Range(0f, 1f)]
        [Tooltip("Mist opacity multiplier under a roof (so rooms don't fill with fog).")]
        [SerializeField] private float indoorMistFactor = 0.25f;

        [Header("Dust motes (only visible where light falls)")]
        [Tooltip("URP Particles/Simple Lit material (Assets/_Project/Art/FX/DustMotes).")]
        [SerializeField] private Material dustMaterial;
        [SerializeField] private int dustParticles = 220;
        [Tooltip("Dust floats in a sphere of this radius (m) around the camera.")]
        [SerializeField] private float dustRadius = 9f;
        [SerializeField] private Vector2 dustSize = new Vector2(0.012f, 0.03f);
        [SerializeField] private Color dustColor = new Color(0.92f, 0.9f, 0.84f);
        [Range(0f, 1f)] [SerializeField] private float nightDustAlpha = 0.85f;
        [Range(0f, 1f)]
        [Tooltip("0 = no dust by day (sunlit specks read as screen dirt).")]
        [SerializeField] private float dayDustAlpha = 0f;

        private ParticleSystem mist, dust;
        private Material mistMat, dustMat;   // runtime COPIES — the assets on disk are never edited
        private ParticleSystem.Particle[] mistBuf, dustBuf;
        private float groundY;
        private float indoor01, indoorTarget;
        private float nextProbe;
        private float rain01;

        private void Start()
        {
            if (mistMaterial != null)
            {
                mistMat = new Material(mistMaterial);
                mist = BuildSystem("GroundMist", mistMat, mistParticles, mistLifetime, mistSize,
                                   maxScreenSize: 4f, sortByDistance: true);
                var vel = mist.velocityOverLifetime;
                vel.enabled = true;
                vel.space = ParticleSystemSimulationSpace.World;
                vel.x = new ParticleSystem.MinMaxCurve(-mistDrift, mistDrift);
                vel.y = new ParticleSystem.MinMaxCurve(0f, 0f);
                vel.z = new ParticleSystem.MinMaxCurve(-mistDrift, mistDrift);
                var rot = mist.rotationOverLifetime;
                rot.enabled = true;
                rot.z = new ParticleSystem.MinMaxCurve(-0.08f, 0.08f);   // slow swirl (radians/s)
                mistBuf = new ParticleSystem.Particle[mistParticles];
            }

            if (dustMaterial != null)
            {
                dustMat = new Material(dustMaterial);
                dust = BuildSystem("DustMotes", dustMat, dustParticles, new Vector2(8f, 14f), dustSize,
                                   maxScreenSize: 0.05f, sortByDistance: false);
                var noise = dust.noise;
                noise.enabled = true;
                noise.strength = 0.12f;
                noise.frequency = 0.25f;
                noise.scrollSpeed = 0.1f;
                noise.quality = ParticleSystemNoiseQuality.Medium;
                dustBuf = new ParticleSystem.Particle[dustParticles];
            }
        }

        // One LOCAL particle system, world-space so particles stay put while you walk through them.
        private ParticleSystem BuildSystem(string name, Material mat, int max, Vector2 life, Vector2 size,
                                           float maxScreenSize, bool sortByDistance)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = true;
            main.duration = life.y;
            main.prewarm = true;   // the field is already there on the first frame
            main.maxParticles = max;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life.x, life.y);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(size.x, size.y);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = Color.white;   // colour + opacity live on the material (set per frame)
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0f;
            main.playOnAwake = false;

            var emission = ps.emission;
            emission.rateOverTime = max / Mathf.Max(0.1f, (life.x + life.y) * 0.5f);

            var shape = ps.shape;
            shape.enabled = false;   // particles are placed by hand in PlaceAroundCamera

            // Fade in at birth, fade out at death — no particle ever pops.
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f),
                              new GradientAlphaKey(1f, 0.8f), new GradientAlphaKey(0f, 1f) });
            col.color = g;

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sortMode = sortByDistance ? ParticleSystemSortMode.Distance : ParticleSystemSortMode.None;
            r.maxParticleSize = maxScreenSize;   // default 0.5 would SHRINK a close mist bank to half the screen
            r.minParticleSize = 0f;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            ps.Play();
            return ps;
        }

        private void LateUpdate()
        {
            var cam = Camera.main;   // the LOCAL view (player or spectator)
            if (cam == null) return;
            Vector3 eye = cam.transform.position;

            // Cheap probes, 4×/s: where's the ground, and is there a roof overhead?
            if (Time.time >= nextProbe)
            {
                nextProbe = Time.time + 0.25f;
                groundY = Physics.Raycast(eye + Vector3.up * 0.2f, Vector3.down, out var hit, 60f,
                                          Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                          ? hit.point.y : eye.y - 2.4f;
                bool roofed = Physics.Raycast(eye, Vector3.up, 12f, Physics.DefaultRaycastLayers,
                                              QueryTriggerInteraction.Ignore);
                indoorTarget = roofed ? 1f : 0f;
            }
            indoor01 = Mathf.MoveTowards(indoor01, indoorTarget, Time.deltaTime / 1.5f);   // no snapping under eaves/branches

            float dark = DayNightLighting.Darkness01;
            rain01 = Mathf.MoveTowards(rain01, Weather.IsRaining ? 1f : 0f, Time.deltaTime / 6f);

            if (mist != null)
            {
                mist.transform.position = new Vector3(eye.x, groundY, eye.z);
                float a = Mathf.Lerp(dayMistAlpha, nightMistAlpha, dark)
                        * Mathf.Lerp(1f, rainMistBoost, rain01)
                        * Mathf.Lerp(1f, indoorMistFactor, indoor01);
                SetMatColor(mistMat, mistColor, a);
                Recycle(mist, mistBuf, new Vector3(eye.x, groundY, eye.z), mistRadius, flat: true);
            }

            if (dust != null)
            {
                dust.transform.position = eye;
                SetMatColor(dustMat, dustColor, Mathf.Lerp(dayDustAlpha, nightDustAlpha, dark));
                Recycle(dust, dustBuf, eye, dustRadius, flat: false);
            }
        }

        private static void SetMatColor(Material m, Color c, float alpha)
        {
            c.a = Mathf.Clamp01(alpha);
            m.SetColor("_BaseColor", c);
        }

        // A tiny base velocity marks a particle we've already placed. Particles are born (and
        // prewarmed) at the emitter origin with ZERO base velocity, so "velocity == 0" = not placed
        // yet — robust against frame hitches, unlike a "born in the last 0.05s" timing test.
        // (Drift/noise come from the velocity/noise MODULES, which don't touch this base value.)
        private static readonly Vector3 PlacedMarker = new Vector3(0f, 0.0001f, 0f);

        // Keep the field centred on the camera: new particles spawn around it, and any particle the
        // player has walked away from is moved to the OPPOSITE side (ahead of you) with a fresh life
        // — so it fades back in there instead of popping. Density stays constant even at a sprint.
        private void Recycle(ParticleSystem ps, ParticleSystem.Particle[] buf, Vector3 center, float radius, bool flat)
        {
            int n = ps.GetParticles(buf);
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                var p = buf[i];
                bool fresh = p.velocity == Vector3.zero;   // never placed: still sitting at the emitter origin
                Vector3 d = p.position - center;
                if (flat) d.y = 0f;
                if (!fresh && d.sqrMagnitude <= radius * radius) continue;

                Vector3 pos;
                if (fresh)
                {
                    // Random spot in the disc / sphere around the camera.
                    pos = flat ? center + Flat(Random.insideUnitCircle * radius)
                               : center + Random.insideUnitSphere * radius;
                }
                else
                {
                    // Left behind → re-seed on the far side, slightly inside the edge.
                    Vector3 dir = d.sqrMagnitude > 0.001f ? d.normalized : Random.onUnitSphere;
                    pos = center - dir * radius * Random.Range(0.6f, 0.95f);
                    if (flat) pos += Flat(Random.insideUnitCircle * radius * 0.25f);
                    p.remainingLifetime = p.startLifetime;
                }
                if (flat) pos.y = center.y + Random.Range(mistHeight.x, mistHeight.y);
                p.position = pos;
                p.velocity = PlacedMarker;
                buf[i] = p;
                changed = true;
            }
            if (changed) ps.SetParticles(buf, n);
        }

        private static Vector3 Flat(Vector2 v) => new Vector3(v.x, 0f, v.y);

        private void OnDestroy()
        {
            if (mistMat != null) Destroy(mistMat);
            if (dustMat != null) Destroy(dustMat);
        }
    }
}
