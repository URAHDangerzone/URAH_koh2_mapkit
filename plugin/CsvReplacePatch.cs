using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Logic;

namespace URAH_MapKit
{
    /// <summary>
    /// Map csv tables (realms.csv, kingdoms.csv, cultures.csv, papacy.csv) are merged row by row with
    /// every active mod's copy (DT.ReadMapsCsv), so a mod can add and change rows but never remove one.
    /// A mod that lists a table in &lt;mod&gt;/maps/&lt;map&gt;/replace.txt (one file name per line) has its
    /// copy used instead of the merged table; with several such mods the last active one wins.
    /// </summary>
    [HarmonyPatch(typeof(DT), nameof(DT.ReadMapsCsv))]
    internal static class CsvReplacePatch
    {
        private static void Postfix(DT dt, string relativePath, char delimiter, string valueKey, string emptyValue,
                                    ref DT.Field __result)
        {
            ModManager manager = ModManager.Get();
            if (manager == null || string.IsNullOrEmpty(relativePath))
            {
                return;
            }
            string dir = System.IO.Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? "";
            string file = System.IO.Path.GetFileName(relativePath);
            Mod winner = null;
            foreach (Mod mod in manager.GetActiveMods())
            {
                if (!mod.has_maps)
                {
                    continue;
                }
                string list = mod.maps_path + dir + "/replace.txt";
                if (File.Exists(list) && File.Exists(mod.maps_path + relativePath) &&
                    File.ReadAllLines(list).Any(l => l.Trim().Equals(file, StringComparison.OrdinalIgnoreCase)))
                {
                    winner = mod;
                }
            }
            if (winner == null)
            {
                return;
            }
            ModManager.LoadingMod = winner;
            try
            {
                DT.Field replaced = DT.LoadCSVFile(dt, winner.maps_path + relativePath, delimiter, valueKey, emptyValue);
                if (replaced != null)
                {
                    __result = replaced;
                    Plugin.Log.LogInfo($"{relativePath}: replaced by {winner.maps_path + relativePath} ({replaced.children?.Count ?? 0} rows)");
                }
            }
            finally
            {
                ModManager.LoadingMod = null;
            }
        }
    }
}
