using System;
using System.Collections.Generic;
using System.IO;
using Logic;
using UnityEngine;
using UnityEngine.UI;

namespace URAH_MapKit
{
    /// <summary>
    /// Draws a mod's patches (bigmap_patches.txt + PNGs from URAH_Iceland's tools/paint_bigmap.py or URAH_HRE's tools/paint_hre_bigmap.py) over the political
    /// view's painted map BigMap_WV. The texture is compressed and not readable, so it is copied on
    /// the GPU, patched with Graphics.DrawTexture, read back and compressed again, then swapped into
    /// every material that uses it. Without patches in the active mods the original is put back.
    /// </summary>
    internal static class BigMapPatch
    {
        private const string TextureName = "BigMap_WV";
        private const string MainTex = "_MainTex";

        private static Texture original;
        private static Texture2D patched;
        private static string patchedFrom;

        private static string lastState;

        /// <summary>Swap the patched (mod active) or original map into every material and UI image using it.</summary>
        public static void Apply(string mapName)
        {
            if (string.IsNullOrEmpty(mapName))
            {
                mapName = "europe"; // the title screen shows europe before a map is loaded
            }
            List<Material> materials = FindMaterials();
            List<RawImage> images = FindImages();
            if (original == null)
            {
                return;
            }
            string list = DT.GetMapFileFullPath(mapName + "/bigmap_patches.txt");
            bool modded = list != Game.maps_path + mapName + "/bigmap_patches.txt" && File.Exists(list);
            Texture use = original;
            if (modded)
            {
                if (patched == null || patchedFrom != list)
                {
                    Build(list);
                }
                use = patched != null ? (Texture)patched : original;
            }
            int swapped = 0;
            foreach (Material m in materials)
            {
                if (m.GetTexture(MainTex) != use)
                {
                    m.SetTexture(MainTex, use);
                    swapped++;
                }
            }
            foreach (RawImage img in images)
            {
                if (img.texture != use)
                {
                    img.texture = use;
                    swapped++;
                }
            }
            string state = $"{materials.Count}/{images.Count}/{(use == original ? "original" : "patched")}";
            if (swapped > 0 || state != lastState)
            {
                lastState = state;
                Plugin.Log.LogInfo($"{TextureName}: {materials.Count} material(s), {images.Count} UI image(s) use the " +
                    $"{(use == original ? "original" : "patched")} map ({swapped} swapped)");
            }
        }

        private static bool IsMap(Texture t)
        {
            if (t == null)
            {
                return false;
            }
            if (t == patched)
            {
                return true;
            }
            if (t.name == TextureName)
            {
                original = t;
                return true;
            }
            return false;
        }

        private static List<Material> FindMaterials()
        {
            var found = new List<Material>();
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m != null && m.HasProperty(MainTex) && IsMap(m.GetTexture(MainTex)))
                {
                    found.Add(m);
                }
            }
            return found;
        }

        private static List<RawImage> FindImages()
        {
            var found = new List<RawImage>();
            foreach (RawImage img in Resources.FindObjectsOfTypeAll<RawImage>())
            {
                if (img != null && IsMap(img.texture))
                {
                    found.Add(img);
                }
            }
            return found;
        }

        private static void Build(string listPath)
        {
            if (original == null)
            {
                return;
            }
            int w = original.width, h = original.height;
            var patches = new List<(Texture2D tex, int x, int y)>();
            string dir = System.IO.Path.GetDirectoryName(listPath);
            foreach (string line in File.ReadAllLines(listPath))
            {
                string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3)
                {
                    continue;
                }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(File.ReadAllBytes(System.IO.Path.Combine(dir, parts[0]))))
                {
                    Plugin.Log.LogError($"Cannot load {parts[0]}");
                    continue;
                }
                patches.Add((tex, int.Parse(parts[1]), int.Parse(parts[2])));
            }

            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            try
            {
                Graphics.Blit(original, rt);
                RenderTexture.active = rt;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, w, h, 0); // y down: (0, 0) is the top-left (north-west) corner
                foreach (var p in patches)
                {
                    Graphics.DrawTexture(new Rect(p.x, p.y, p.tex.width, p.tex.height), p.tex);
                }
                GL.PopMatrix();

                var result = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = TextureName + "_patched" };
                result.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                result.Apply(false);
                result.Compress(true);
                result.Apply(false, true);
                result.wrapMode = original.wrapMode;
                result.filterMode = original.filterMode;
                result.anisoLevel = original.anisoLevel;
                if (patched != null)
                {
                    UnityEngine.Object.Destroy(patched);
                }
                patched = result;
                patchedFrom = listPath;
                Plugin.Log.LogInfo($"{TextureName} patched with {patches.Count} patch(es) from {listPath} ({result.format})");
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                foreach (var p in patches)
                {
                    UnityEngine.Object.Destroy(p.tex);
                }
            }
        }
    }
}
