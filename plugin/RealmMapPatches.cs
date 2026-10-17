using System;
using System.IO;
using HarmonyLib;
using Logic;
using UnityEngine;

namespace URAH_MapKit
{
    /// <summary>
    /// Makes the realm id map moddable: Logic.Game.LoadRealmIDMap reads realms.bin with a plain
    /// File.ReadAllBytes (no mod lookup), and the political view's realmsdata texture encodes the
    /// same ids, so it is rewritten from the loaded map instead of shipping a PNG override.
    /// </summary>
    internal static class RealmMapPatches
    {
        /// <summary>Realm map of the current game came from a mod; realmsdata textures must follow it.</summary>
        internal static short[,] ModdedRealmMap;

        [HarmonyPatch(typeof(Game), nameof(Game.LoadRealmIDMap))]
        private static class LoadRealmIDMapPatch
        {
            private static bool Prefix(Game __instance, string map_name)
            {
                string path = DT.GetMapFileFullPath(map_name + "/realms.bin");
                ModdedRealmMap = null;
                if (path == Game.maps_path + map_name + "/realms.bin")
                {
                    return true; // no mod provides one, keep the original loader
                }
                try
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    int n = (int)Math.Sqrt(bytes.Length / 2);
                    __instance.realm_id_map = Serialization.ToArray<short>(bytes, n, n);
                    ModdedRealmMap = __instance.realm_id_map;
                    Plugin.Log.LogInfo($"Loaded realm id map {path} ({n}x{n})");
                    CheckAllRealms(__instance);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Loading {path} failed: {e}");
                    __instance.realm_id_map = null;
                }
                return false;
            }
        }

        /// <summary>Log whether the modded realm map agrees with the realms: pixels with unknown ids and realms without any pixel.</summary>
        private static void CheckAllRealms(Game game)
        {
            short[,] map = game.realm_id_map;
            int n = map.GetLength(0);
            var pixels = new System.Collections.Generic.Dictionary<int, int>();
            int unknown = 0;
            for (int x = 0; x < n; x++)
            {
                for (int z = 0; z < n; z++)
                {
                    short id = map[x, z];
                    if (id != 0 && game.GetRealm(id) == null)
                    {
                        unknown++;
                    }
                    pixels.TryGetValue(id, out int c);
                    pixels[id] = c + 1;
                }
            }
            int empty = 0;
            string examples = "";
            foreach (Logic.Realm realm in game.realms)
            {
                if (!pixels.ContainsKey(realm.id))
                {
                    empty++;
                    if (empty <= 5)
                    {
                        examples += " " + realm.name;
                    }
                }
            }
            Plugin.Log.LogInfo($"Realm check: {game.realms.Count} realms, {unknown} px with unknown ids, " +
                               $"{empty} realms without pixels{(empty > 0 ? " (" + examples.Trim() + ")" : "")}");
        }

        /// <summary>
        /// realmsdata: R + 256 * G = abs(realm id), B = terrain steepness on land, 255 at sea.
        /// Texture row 0 is z = 0. Sea pixels always get B = 255 (true for every vanilla sea pixel),
        /// land pixels from a mod's realmsdata_patch.bin; so switching a mod off restores vanilla.
        /// </summary>
        internal static void RewriteRealmsData(Texture2D tex, short[,] map)
        {
            if (tex == null || map == null)
            {
                return;
            }
            int n = map.GetLength(0);
            if (tex.width != n || tex.height != n)
            {
                Plugin.Log.LogWarning($"realmsdata is {tex.width}x{tex.height}, realm map {n}x{n}; not rewritten");
                return;
            }
            BluePatch patch = BluePatch.Load(GameLogic.Get(create: false)?.map_name);
            Color32[] px = tex.GetPixels32();
            int changed = 0;
            for (int z = 0; z < n; z++)
            {
                for (int x = 0; x < n; x++)
                {
                    int rid = map[x, z];
                    int id = Math.Abs(rid);
                    int i = z * n + x;
                    byte r = (byte)(id & 0xFF);
                    byte g = (byte)(id >> 8);
                    byte b = px[i].b;
                    if (rid < 0)
                    {
                        b = 255;
                    }
                    else if (patch != null)
                    {
                        byte pb = patch.At(x, z);
                        if (pb != 255)
                        {
                            b = pb;
                        }
                    }
                    if (px[i].r != r || px[i].g != g || px[i].b != b)
                    {
                        px[i].r = r;
                        px[i].g = g;
                        px[i].b = b;
                        changed++;
                    }
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false);
            Plugin.Log.LogInfo($"{tex.name} rewritten: {changed} pixels changed{(patch != null ? ", blue patch applied" : "")}");
        }

        /// <summary>realmsdata_patch.bin from URAH_Iceland's tools/build_island_realm.py: "PRB1", x0, z0, w, h, uint8[h][w] (255 = keep).</summary>
        private sealed class BluePatch
        {
            private int x0, z0, w, h;
            private byte[] data;

            public static BluePatch Load(string mapName)
            {
                if (string.IsNullOrEmpty(mapName))
                {
                    return null;
                }
                string path = DT.GetMapFileFullPath(mapName + "/realmsdata_patch.bin");
                if (path == Game.maps_path + mapName + "/realmsdata_patch.bin" || !File.Exists(path))
                {
                    return null;
                }
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    if (new string(r.ReadChars(4)) != "PRB1")
                    {
                        Plugin.Log.LogError($"{path}: bad magic");
                        return null;
                    }
                    var p = new BluePatch { x0 = r.ReadInt32(), z0 = r.ReadInt32(), w = r.ReadInt32(), h = r.ReadInt32() };
                    p.data = r.ReadBytes(p.w * p.h);
                    return p.data.Length == p.w * p.h ? p : null;
                }
            }

            public byte At(int x, int z)
            {
                int i = x - x0, j = z - z0;
                return i < 0 || j < 0 || i >= w || j >= h ? (byte)255 : data[j * w + i];
            }
        }
    }
}
