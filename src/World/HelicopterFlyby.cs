using UnityEngine;

namespace SunsetCurse.World
{
    /// <summary>
    /// The victory rescue: a helicopter that sweeps across the night sky over the local player with
    /// thumping rotor audio, then peels away as the Victory screen fades in. Spawned on every peer
    /// by <see cref="RadioTowerState"/> the moment the SOS goes out (so everyone sees their own
    /// flyby), purely local + cosmetic — nothing networked.
    ///
    /// If no helicopter model is assigned it flies a simple procedural placeholder (fuselage + tail
    /// + spinning rotor) so the moment still reads while you source a real model.
    /// </summary>
    public class HelicopterFlyby : MonoBehaviour
    {
        private Vector3 startPos, endPos;
        private float duration, elapsed;
        private Transform rotor;          // placeholder main rotor (spun each frame)
        private Transform camT;           // local camera we turn to lock onto the chopper
        private float lookRamp;           // 0→1 ease as the camera swings to face it

        /// <summary>Create + launch a flyby over the local camera. Safe to call on any peer.</summary>
        public static void Spawn(GameObject prefab, AudioClip rotorClip, float volume,
                                 float altitude, float span, float duration, float scale, bool lockCamera)
        {
            var host = new GameObject("HelicopterFlyby");
            var fly = host.AddComponent<HelicopterFlyby>();
            fly.Setup(prefab, rotorClip, volume, altitude, span, duration, scale, lockCamera);
        }

        private void Setup(GameObject prefab, AudioClip rotorClip, float volume,
                           float altitude, float span, float duration, float scale, bool lockCamera)
        {
            this.duration = Mathf.Max(1f, duration);

            // Fly ACROSS the local view (left → right), high up and a little in front, so it sweeps
            // through the player's sky. Falls back to world axes if there's no camera.
            var cam = Camera.main;
            Vector3 center = cam != null ? cam.transform.position : Vector3.zero;

            Vector3 across = cam != null ? cam.transform.right : Vector3.right;
            across.y = 0f;
            across = across.sqrMagnitude > 0.001f ? across.normalized : Vector3.right;
            Vector3 forward = cam != null ? cam.transform.forward : Vector3.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.001f ? forward.normalized : Vector3.forward;

            Vector3 high = Vector3.up * altitude + forward * (span * 0.25f);   // up + a bit ahead
            startPos = center + high - across * (span * 0.5f);
            endPos   = center + high + across * (span * 0.5f);

            transform.position = startPos;
            transform.rotation = Quaternion.LookRotation((endPos - startPos).normalized, Vector3.up);

            // The visual — real model or placeholder.
            if (prefab != null)
            {
                var model = Instantiate(prefab, transform);
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.identity;
                model.transform.localScale = Vector3.one * scale;
                StripCameras(model);   // imported models sometimes ship an embedded camera/audio node
                KickAnimations(model); // play the pack's built-in rotor spin (Animator or legacy)
            }
            else
            {
                BuildPlaceholder(scale);
            }

            // Cinematic victory cam: take over the local view and turn to lock onto the chopper.
            // (`cam` was resolved above for the flight geometry.)
            if (lockCamera && cam != null)
            {
                camT = cam.transform;
                // Stop the player looking/moving and stop Cinemachine driving the camera, so we can
                // aim it ourselves (same reflection-free approach as the death cam).
                var local = SunsetCurse.Core.PlayerInventory.Local;
                if (local != null) SetComponentEnabled(local.gameObject, "FirstPersonController", false);
                SetComponentEnabled(cam.gameObject, "CinemachineBrain", false);
            }

            // Rotor thump — 3D so it swells as it passes overhead.
            if (rotorClip != null)
            {
                var a = gameObject.AddComponent<AudioSource>();
                a.clip = rotorClip;
                a.loop = true;
                a.volume = volume;
                a.spatialBlend = 1f;
                a.rolloffMode = AudioRolloffMode.Linear;
                a.minDistance = altitude * 0.5f;
                a.maxDistance = span;
                a.playOnAwake = false;
                a.Play();
            }
        }

        private void Update()
        {
            elapsed += Time.deltaTime;
            float k = Mathf.Clamp01(elapsed / duration);
            // Ease in/out so it enters and leaves smoothly.
            float eased = Mathf.SmoothStep(0f, 1f, k);
            transform.position = Vector3.Lerp(startPos, endPos, eased);

            // Face travel with a slight forward nose-down + bank into the sweep.
            Vector3 dir = (endPos - startPos).normalized;
            transform.rotation = Quaternion.LookRotation(dir, Vector3.up) * Quaternion.Euler(8f, 0f, -12f);

            if (rotor != null) rotor.Rotate(0f, 1600f * Time.deltaTime, 0f, Space.Self);

            // Turn the camera to lock onto the chopper: ease in over ~1s, then track it as it sweeps.
            if (camT != null)
            {
                Vector3 to = transform.position - camT.position;
                if (to.sqrMagnitude > 0.01f)
                {
                    Quaternion target = Quaternion.LookRotation(to.normalized, Vector3.up);
                    lookRamp = Mathf.Min(1f, lookRamp + Time.deltaTime / 1.0f);
                    camT.rotation = Quaternion.Slerp(camT.rotation, target, lookRamp);
                }
            }

            if (k >= 1f) Destroy(gameObject);   // gone beyond the far side
        }

        // ───────────────────────── procedural placeholder ─────────────────────────

        private void BuildPlaceholder(float scale)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            Color body = new Color(0.06f, 0.06f, 0.07f);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", body); else mat.color = body;

            // Fuselage.
            var fuselage = MakeBox("Fuselage", new Vector3(1.4f, 1.3f, 3.4f), Vector3.zero, mat);
            // Tail boom.
            MakeBox("Tail", new Vector3(0.4f, 0.4f, 3.0f), new Vector3(0f, 0.2f, -3.0f), mat);
            // Tail fin.
            MakeBox("TailFin", new Vector3(0.15f, 1.0f, 0.6f), new Vector3(0f, 0.7f, -4.3f), mat);
            // Main rotor — a thin cross that spins.
            rotor = new GameObject("Rotor").transform;
            rotor.SetParent(transform, false);
            rotor.localPosition = new Vector3(0f, 0.95f, 0f);
            MakeBox("Blade1", new Vector3(6.5f, 0.06f, 0.35f), Vector3.zero, mat, rotor);
            MakeBox("Blade2", new Vector3(0.35f, 0.06f, 6.5f), Vector3.zero, mat, rotor);

            // A red blinking beacon so it reads at night.
            var beacon = new GameObject("Beacon").AddComponent<Light>();
            beacon.transform.SetParent(fuselage.transform, false);
            beacon.transform.localPosition = new Vector3(0f, -0.8f, 0f);
            beacon.type = LightType.Point;
            beacon.color = new Color(1f, 0.15f, 0.1f);
            beacon.intensity = 4f;
            beacon.range = 8f;

            transform.localScale = Vector3.one * scale;
        }

        private Transform MakeBox(string name, Vector3 size, Vector3 localPos, Material mat, Transform parent = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Destroy(go.GetComponent<Collider>());   // cosmetic only — no physics
            go.transform.SetParent(parent != null ? parent : transform, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go.transform;
        }

        private static void StripCameras(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<Camera>(true)) Destroy(c);
            foreach (var a in go.GetComponentsInChildren<AudioListener>(true)) Destroy(a);
        }

        /// <summary>Make the pack's built-in rotor animation actually spin. An Animator plays its
        /// default state on its own once enabled; a legacy <see cref="Animation"/> component needs
        /// a nudge (playAutomatically + Play). Covers both animation systems the pack might use.</summary>
        private static void KickAnimations(GameObject go)
        {
            foreach (var anim in go.GetComponentsInChildren<Animator>(true))
            {
                anim.enabled = true;
                if (anim.runtimeAnimatorController != null) anim.Play(0, 0, 0f);
            }
            foreach (var legacy in go.GetComponentsInChildren<Animation>(true))
            {
                legacy.playAutomatically = true;
                legacy.Play();
            }
        }

        private static void SetComponentEnabled(GameObject root, string typeName, bool enabled)
        {
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && mb.GetType().Name == typeName) mb.enabled = enabled;
        }
    }
}
