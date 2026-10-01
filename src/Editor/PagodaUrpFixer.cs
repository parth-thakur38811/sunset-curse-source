using UnityEditor;
using UnityEngine;

namespace SunsetCurse.EditorTools
{
    /// <summary>
    /// One-click fix for the pink JTS_PagodaArchitecture (TanukiDigital) temple materials.
    ///
    /// The pack ships its own built-in custom shader ("TanukiDigital/Standard_Packed_PBR"), which
    /// URP can't render (→ magenta) and the Render Pipeline Converter ignores (it only knows the
    /// standard Unity shaders). This converts those materials to URP/Lit and re-maps the textures:
    ///   _MainTex (RGB albedo, A smoothness) → Base Map (+ smoothness from albedo alpha)
    ///   _NTex    (RGB normal)               → Normal Map
    /// (The packed _MaskTex is dropped — set Metallic per-material if a copper piece needs it.)
    ///
    /// RUN IT: menu  Tools ▸ Sunset Curse ▸ Fix Pagoda materials → URP Lit  (after importing).
    /// Also works on just the materials you've selected, if any.
    /// </summary>
    public static class PagodaUrpFixer
    {
        [MenuItem("Tools/Sunset Curse/Fix Pagoda materials → URP Lit")]
        public static void Fix()
        {
            var urp = Shader.Find("Universal Render Pipeline/Lit");
            if (urp == null) { Debug.LogError("[PagodaUrpFixer] URP/Lit shader not found — is URP installed?"); return; }

            // Operate on the current selection if there are materials selected; otherwise the whole project.
            string[] guids;
            var selected = Selection.GetFiltered<Material>(SelectionMode.Assets);
            if (selected != null && selected.Length > 0)
                guids = System.Array.ConvertAll(selected, m => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(m)));
            else
                guids = AssetDatabase.FindAssets("t:Material");

            int count = 0;
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null) continue;
                if (!mat.shader.name.StartsWith("TanukiDigital")) continue;   // only the temple's custom shaders

                Texture albedo = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
                Texture normal = mat.HasProperty("_NTex") ? mat.GetTexture("_NTex") : null;
                Color tint = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;

                mat.shader = urp;

                if (albedo != null) { mat.SetTexture("_BaseMap", albedo); mat.SetTexture("_MainTex", albedo); }
                mat.SetColor("_BaseColor", tint);

                if (normal != null)
                {
                    mat.SetTexture("_BumpMap", normal);
                    mat.EnableKeyword("_NORMALMAP");
                    mat.SetFloat("_BumpScale", 1f);
                }

                // This pack stores smoothness in the albedo's alpha channel.
                if (mat.HasProperty("_SmoothnessTextureChannel")) mat.SetFloat("_SmoothnessTextureChannel", 1f); // albedo alpha
                mat.SetFloat("_Smoothness", 0.4f);
                mat.SetFloat("_Metallic", 0f);

                EditorUtility.SetDirty(mat);
                count++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[PagodaUrpFixer] Converted {count} TanukiDigital material(s) to URP/Lit.");
        }
    }
}
