using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Logic;
using URAH;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace URAH_MapKit
{
    /// <summary>
    /// Makes the campaign map moddable for data mods and map plugins: realm id map (realms.bin), map csv
    /// tables replaced instead of merged, and the political view's paper map, ocean and border field
    /// following a mod's map. Map plugins (URAH_HRE, URAH_Iceland) depend on it for HighGrid and
    /// CampaignTerrain; a mod lists urah.koh2.mapkit in its bepinex_plugins.txt next to its own plugin.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "urah.koh2.mapkit";
        public const string Name = "URAH_MapKit";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;

        private static Plugin instance;

        private ModLink link;
        private Texture2D patchedRealmsData;
        private Texture2D patchedTitleRealmsData;
        private short[,] patchedRealmsDataMap;
        private short[,] patchedTitleRealmsDataMap;
        private bool anyRealmsDataRewritten;
        private int bigMapFrame;
        private bool bigMapFailed;

        private void Awake()
        {
            Log = Logger;
            instance = this;
            // patches only while a mod asks for it; the polling in Update stays on, it puts the
            // textures back to vanilla after a mod is switched off
            link = new ModLink(Guid, typeof(Plugin).Assembly, Log);
            link.Changed += active =>
            {
                if (!active)
                {
                    // set by the LoadRealmIDMap patch, which is gone now
                    RealmMapPatches.ModdedRealmMap = null;
                }
            };
            SceneManager.sceneLoaded += (scene, mode) => Log.LogInfo($"Scene loaded: '{scene.name}' ({mode})");
            Log.LogInfo($"{Name} {Version} loaded");
        }

        private void Update()
        {
            SyncAllRealmsData();
            // political map, minimap and title screen pick the texture up at various times; check every 2 s
            if (++bigMapFrame % 120 == 0)
            {
                PatchPoliticalMap();
            }
        }

        /// <summary>Bring realmsdata, paper map, political ocean and SDF in line with the loaded map now.</summary>
        public static void ApplyMapPatches()
        {
            if (instance != null)
            {
                instance.SyncAllRealmsData();
                instance.PatchPoliticalMap();
            }
        }

        private void SyncAllRealmsData()
        {
            // WorldMap/TitleMap.LoadTextures are too small to patch reliably (inlined), so poll.
            // Once a texture was rewritten it stays in memory, so keep it in sync with whatever
            // realm map is loaded, also when a mod is switched off again.
            if (RealmMapPatches.ModdedRealmMap != null || anyRealmsDataRewritten)
            {
                short[,] map = GameLogic.Get(create: false)?.realm_id_map;
                SyncRealmsData(WorldMap.Get()?.RealmsDataTexture, map, ref patchedRealmsData, ref patchedRealmsDataMap);
                SyncRealmsData(TitleMap.Get()?.RealmsDataTexture, map, ref patchedTitleRealmsData, ref patchedTitleRealmsDataMap);
            }
        }

        private void PatchPoliticalMap()
        {
            if (bigMapFailed)
            {
                return;
            }
            try
            {
                BigMapPatch.Apply(GameLogic.Get(create: false)?.map_name);
                PoliticalOceanPatch.Apply(GameLogic.Get(create: false)?.map_name);
                SdfPatch.Apply(GameLogic.Get(create: false)?.map_name);
            }
            catch (Exception e)
            {
                Log.LogError($"Patching the political map failed: {e}");
                bigMapFailed = true;
            }
        }

        /// <summary>
        /// The new-campaign screen draws right after TitleMap.LoadGame; waiting for the 2 s poll left
        /// vanilla's paper map, ocean and borders on screen until it came round.
        /// </summary>
        [HarmonyPatch(typeof(TitleMap), nameof(TitleMap.LoadGame))]
        private static class TitleMapLoadGamePatch
        {
            private static void Postfix() => ApplyMapPatches();
        }

        private void SyncRealmsData(Texture2D tex, short[,] map, ref Texture2D doneTex, ref short[,] doneMap)
        {
            if (tex == null || map == null || (tex == doneTex && map == doneMap))
            {
                return;
            }
            doneTex = tex;
            doneMap = map;
            try
            {
                RealmMapPatches.RewriteRealmsData(tex, map);
                anyRealmsDataRewritten = true;
            }
            catch (Exception e)
            {
                Log.LogError($"Rewriting {tex.name} failed: {e}");
            }
        }
    }
}
