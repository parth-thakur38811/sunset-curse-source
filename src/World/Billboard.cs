using UnityEngine;

namespace SunsetCurse.World
{
    /// <summary>
    /// Makes a flat 2D sprite always face the camera, so a sprite used as a world object (like a
    /// pickup) looks correct from any angle instead of turning edge-on and disappearing.
    ///
    /// SETUP: put this on the object that has the Sprite Renderer.
    /// </summary>
    public class Billboard : MonoBehaviour
    {
        [Tooltip("Keep it standing upright (only spin around Y) instead of fully tilting to the camera.")]
        [SerializeField] private bool upright = true;

        private Transform cam;

        private void LateUpdate()
        {
            if (cam == null)
            {
                if (Camera.main == null) return;
                cam = Camera.main.transform;
            }

            if (upright)
            {
                Vector3 dir = transform.position - cam.position;
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.0001f) transform.forward = dir.normalized;
            }
            else
            {
                transform.forward = cam.forward;
            }
        }
    }
}
