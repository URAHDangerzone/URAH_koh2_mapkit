using System.IO;
using Logic;
using UnityEngine;

namespace URAH_MapKit
{
    /// <summary>
    /// The world view's border distance field MapData.SDF_Tex (global shader texture _SDF) starts as the
    /// scene's baked sdf_ex: red = distance to the nearest realm border (coasts included), green =
    /// kingdom borders. The game only rebuilds green at runtime, so red keeps vanilla's borders: the ocean
    /// (material "Ocean WV DX11", own _SDF slot pointing at sdf_ex) glows along vanilla's coasts, and
    /// WorldMap.IsFarFromLand (red > 0.4) answers for vanilla's coasts. With a mod's sdf_r.bin
    /// (URAH_HRE's tools/import_hre.py, uint8 1024², row 0 = south) the red channel is rewritten and the ocean
    /// materials get the runtime texture.
    /// </summary>
    internal static class SdfPatch
    {
        private static Texture2D patchedTex;
        private static byte[] red;
        private static string loadedFrom;

        public static void Apply(string mapName)
        {
            MapData map = MapData.GetPoliticalOnly();
            Texture2D tex = map?.SDF_Tex;
            if (tex == null || string.IsNullOrEmpty(mapName))
            {
                return;
            }
            string path = DT.GetMapFileFullPath(mapName + "/sdf_r.bin");
            if (path == Game.maps_path + mapName + "/sdf_r.bin" || !File.Exists(path))
            {
                return; // vanilla: nothing to do (a switched-off mod needs a restart for this one)
            }
            if (loadedFrom != path)
            {
                red = File.ReadAllBytes(path);
                loadedFrom = path;
                patchedTex = null;
            }
            if (tex != patchedTex)
            {
                int n = (int)System.Math.Sqrt(red.Length);
                if (tex.width != n || tex.height != n || !tex.isReadable)
                {
                    Plugin.Log.LogWarning($"SDF texture is {tex.width}x{tex.height} (readable {tex.isReadable}), sdf_r.bin {n}²; not patched");
                    patchedTex = tex;
                    return;
                }
                Color32[] px = tex.GetPixels32();
                for (int i = 0; i < px.Length; i++)
                {
                    px[i].r = red[i];
                }
                tex.SetPixels32(px);
                tex.Apply(false);
                patchedTex = tex;
                Shader.SetGlobalTexture("_SDF", tex);
                // the game writes green with Graphics.CopyTexture (GPU only), so the CPU copy uploaded
                // here still has the baked vanilla kingdom borders; rebuild green from the GPU texture
                // (the compute shader keeps red), else the title screen shows vanilla's borders
                map.RegenerateSDF();
                Plugin.Log.LogInfo($"SDF realm border channel rewritten ({tex.name} {n}x{n}), kingdom channel rebuild queued");
            }
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m != null && m.HasProperty("_SDF") && m.shader != null && m.shader.name.StartsWith("CalmWater") &&
                    m.GetTexture("_SDF") != tex)
                {
                    m.SetTexture("_SDF", tex);
                    Plugin.Log.LogInfo($"Ocean material '{m.name}' uses the patched SDF");
                }
            }
        }
    }
}
