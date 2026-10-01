using UnityEngine;

namespace SunsetCurse.World
{
    /// <summary>
    /// Strips non-visual junk that imported FBX/GLB models smuggle in — chiefly an embedded
    /// CAMERA, but also AudioListeners and Lights. An embedded camera becomes active the instant
    /// the model is instantiated and HIJACKS the player's view (it locks onto the spawned object).
    /// This bit us with the golden-key item (session 11c) and again with the dropped-resource
    /// models — every peer that spawns the model gets their own view hijacked.
    ///
    /// Call this right after Instantiating ANY user-assigned world model. A static (non-
    /// MonoBehaviour) helper so it can never be accidentally AddComponent'd into a scene.
    /// </summary>
    public static class ModelArtifacts
    {
        public static void Strip(GameObject go)
        {
            if (go == null) return;
            foreach (var c in go.GetComponentsInChildren<Camera>(true))        Object.Destroy(c);
            foreach (var a in go.GetComponentsInChildren<AudioListener>(true)) Object.Destroy(a);
            foreach (var l in go.GetComponentsInChildren<Light>(true))         Object.Destroy(l);
        }
    }
}
