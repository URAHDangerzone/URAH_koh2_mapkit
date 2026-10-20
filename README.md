# URAH_MapKit

A BepInEx 5 plugin for **Knights of Honor II: Sovereign** that makes the campaign map moddable beyond
what the game's mod system covers. Map mods depend on it (URAH HRE map); on its own it does nothing.

What a data mod can then ship in `Maps/<map>/`:

- `realms.bin`: its own realm id map (the game reads it with a plain file read, no mod lookup);
  the political view's realmsdata texture is rewritten to match, `realmsdata_patch.bin` patches its
  terrain channel
- `replace.txt`: map csv tables (realms.csv, kingdoms.csv, ...) used instead of merged row by row
- `bigmap_patches.txt` + PNGs: patches painted over the political view's paper map
- `pv_coast.bin`, `pv_heights.bin`: the political view's ocean shading for a new coastline
- `sdf_r.bin`: the world view's realm border distance field for a new map

For map plugins it offers `URAH_MapKit.HighGrid` (rebuilds the high level pathfinding grid after
the low level nodes changed) and `URAH_MapKit.CampaignTerrain`.

## Use

A mod switches MapKit on by naming it in `bepinex_plugins.txt` in its root folder (one plugin GUID
per line):

```
urah.koh2.mapkit
```

The patches are applied while such a mod is active and removed when it is switched off. A plugin that
uses the API declares `[BepInDependency("urah.koh2.mapkit")]` and references `URAH_MapKit.dll`
without copying it.

## Releases

MapKit has no release of its own: it ships signed inside the map mods that use it, for example in
`URAH_HRE-plugins.zip` of [`URAH_HRE-v<version>`](https://github.com/URAHDangerzone/URAH_koh2_releases/releases?q=URAH_HRE&expanded=true) in [URAH_koh2_releases](https://github.com/URAHDangerzone/URAH_koh2_releases/releases).
Every release is signed with the URAH release key; [URAH_koh2_releases](https://github.com/URAHDangerzone/URAH_koh2_releases) explains how to check it.

## Building

```sh
git clone --recursive <this repo>      # external/URAH_shared is a submodule
cd plugin && dotnet build -c Release -p:Deploy=true
```

Compiles against your own game install with BepInEx 5 (`GameDir`, default: Steam on Linux).
