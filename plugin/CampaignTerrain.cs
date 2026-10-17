using UnityEngine;

namespace URAH_MapKit
{
    /// <summary>The europe campaign terrain that map plugins reshape.</summary>
    public static class CampaignTerrain
    {
        // world units; heightmap 4097^2, alphamap 2048^2
        public const float SizeX = 3750f;
        public const float SizeZ = 2625f;

        /// <summary>The campaign's Terrain component, or null outside the world view.</summary>
        public static Terrain Find()
        {
            foreach (Terrain t in Terrain.activeTerrains)
            {
                if (t != null && t.terrainData != null && t.terrainData.size.x > 3000f)
                {
                    return t;
                }
            }
            return null;
        }
    }
}
