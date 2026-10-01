using System.Collections;
using UnityEngine;
using SunsetCurse.Core;

namespace SunsetCurse.World
{
    /// <summary>
    /// Spawns a brief yellow point-light burst whenever ANYONE flashes their torch (Inventory
    /// broadcasts the world position). Visible to every peer because Inventory's broadcast fires
    /// the OnTorchFlashed event on each client locally.
    ///
    /// No prefab needed — the light is built procedurally and self-destroys after ~0.5s.
    ///
    /// SETUP: empty GameObject "TorchFlashFX" in SampleScene + this script. No fields to set.
    /// </summary>
    public class TorchFlashFX : MonoBehaviour
    {
        [Header("Burst")]
        [Tooltip("Peak brightness of the yellow flash.")]
        [SerializeField] private float peakIntensity = 60f;
        [Tooltip("How far the light reaches.")]
        [SerializeField] private float range = 35f;
        [Tooltip("Total burst lifetime in seconds (rise + fall).")]
        [SerializeField] private float lifetime = 0.55f;
        [SerializeField] private Color flashColor = new Color(1f, 0.95f, 0.55f);

        private void OnEnable()  => Inventory.OnTorchFlashed += Spawn;
        private void OnDisable() => Inventory.OnTorchFlashed -= Spawn;

        private void Spawn(Vector3 worldPos)
        {
            var go = new GameObject("TorchFlashBurst");
            go.transform.position = worldPos;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = flashColor;
            light.intensity = 0f;
            light.range = range;
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.ForcePixel;
            StartCoroutine(LifeRoutine(light));
        }

        private IEnumerator LifeRoutine(Light light)
        {
            float half = lifetime * 0.5f;
            float t = 0f;
            // Snap up to peak
            while (t < half * 0.3f)
            {
                t += Time.deltaTime;
                light.intensity = Mathf.Lerp(0f, peakIntensity, t / (half * 0.3f));
                yield return null;
            }
            light.intensity = peakIntensity;
            // Fade out over the remaining time
            t = 0f;
            float fall = lifetime - half * 0.3f;
            while (t < fall)
            {
                t += Time.deltaTime;
                light.intensity = Mathf.Lerp(peakIntensity, 0f, t / fall);
                yield return null;
            }
            Destroy(light.gameObject);
        }
    }
}
