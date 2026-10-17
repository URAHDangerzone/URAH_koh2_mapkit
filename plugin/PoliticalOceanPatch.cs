using System.IO;
using Logic;
using UnityEngine;

namespace URAH_MapKit
{
    /// <summary>
    /// The political view's ocean (shader BSG/PV_Ocean, material "Political Ocean") shades the sea from
    /// two data textures baked for vanilla's coastline: _CoastDistance ("coastline europe") and _Heights
    /// ("heights"). With a whole-map terrain those draw vanilla's coasts over the new sea, so they are
    /// replaced by pv_coast.bin / pv_heights.bin from URAH_HRE's tools/paint_hre_bigmap.py (raw 2048², row 0 = south).
    /// Without the files in an active mod the original textures are put back.
    /// </summary>
    internal static class PoliticalOceanPatch
    {
        private const string Shader = "BSG/PV_Ocean";
        private const int Res = 2048;

        private static Texture2D coast, heights;
        private static Texture originalCoast, originalHeights;
        private static string loadedFrom;

        public static void Apply(string mapName)
        {
            if (string.IsNullOrEmpty(mapName))
            {
                mapName = "europe";
            }
            string coastPath = DT.GetMapFileFullPath(mapName + "/pv_coast.bin");
            bool modded = coastPath != Game.maps_path + mapName + "/pv_coast.bin" && File.Exists(coastPath);
            if (modded && loadedFrom != coastPath)
            {
                Load(coastPath, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(coastPath), "pv_heights.bin"));
                loadedFrom = coastPath;
            }
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.shader == null || m.shader.name != Shader)
                {
                    continue;
                }
                Texture c = m.GetTexture("_CoastDistance"), h = m.GetTexture("_Heights");
                if (originalCoast == null && c != null && c != coast)
                {
                    originalCoast = c;
                }
                if (originalHeights == null && h != null && h != heights)
                {
                    originalHeights = h;
                }
                Texture wantC = modded && coast != null ? coast : originalCoast;
                Texture wantH = modded && heights != null ? heights : originalHeights;
                if (wantC != null && c != wantC)
                {
                    m.SetTexture("_CoastDistance", wantC);
                    Plugin.Log.LogInfo($"Political ocean '{m.name}': coast distance {(modded ? "replaced" : "restored")}");
                }
                if (wantH != null && h != wantH)
                {
                    m.SetTexture("_Heights", wantH);
                }
            }
        }

        private static void Load(string coastPath, string heightsPath)
        {
            coast = new Texture2D(Res, Res, TextureFormat.RGB24, false, true) { name = "coastline_hre", wrapMode = TextureWrapMode.Clamp };
            coast.LoadRawTextureData(File.ReadAllBytes(coastPath));
            coast.Apply(false, true);
            if (File.Exists(heightsPath))
            {
                heights = new Texture2D(Res, Res, TextureFormat.RGB24, false, true) { name = "heights_hre", wrapMode = TextureWrapMode.Clamp };
                heights.LoadRawTextureData(File.ReadAllBytes(heightsPath));
                heights.Apply(false, true);
            }
        }
    }
}
