using System;
using Logic;

namespace URAH_MapKit
{
    /// <summary>
    /// The 16-unit high level pathfinding grid, mirroring the high grid generation in
    /// RuntimePathDataBuilder.GenerateHighPFData. Used after a map plugin changed the low level nodes
    /// (an island raised out of the sea, a whole new terrain).
    /// </summary>
    public static class HighGrid
    {
        /// <summary>
        /// Recomputes cell offsets and edge weights of the high level grid over the given node rect.
        /// Open ocean edges keep their baked weight, or get the vanilla ones (170 straight, 234 diagonal)
        /// with <paramref name="setOcean"/> when the whole grid is regenerated.
        /// </summary>
        public static void Patch(Logic.PathFinding pf, int x0, int x1, int z0, int z1, bool setOcean = false)
        {
            PathData data = pf.data;
            if (data.highPFGrid == null || data.highPFGridWeights == null)
            {
                Plugin.Log.LogWarning("No high level pathfinding grid, skipped");
                return;
            }
            float tile = pf.settings.grid_tile_size;
            int gx0 = Math.Max((int)(x0 / tile) - 1, 0);
            int gx1 = Math.Min((int)(x1 / tile) + 1, data.highPFGrid_width - 1);
            int gz0 = Math.Max((int)(z0 / tile) - 1, 0);
            int gz1 = Math.Min((int)(z1 / tile) + 1, data.highPFGrid_height - 1);

            // cell offsets: keep the tile center unless it is blocked land, then use the nearest passable node
            int moved = 0;
            for (int gy = gz0; gy <= gz1; gy++)
            {
                for (int gx = gx0; gx <= gx1; gx++)
                {
                    PathData.HighGridNode hn = data.GetHighGridNode(gx, gy);
                    byte offset = CellOffset(data, gx, gy, tile);
                    if (offset != hn.cell_offset)
                    {
                        hn.cell_offset = offset;
                        data.SetHighNode(gx, gy, hn);
                        moved++;
                    }
                }
            }

            int updated = 0, blocked = 0, detoured = 0;
            var groundWatch = new System.Diagnostics.Stopwatch();
            var detourWatch = new System.Diagnostics.Stopwatch();
            for (int gy = gz0; gy <= gz1; gy++)
            {
                for (int gx = gx0; gx <= gx1; gx++)
                {
                    // dirs 0..3 are stored on this node; 4..7 belong to the neighbors' 0..3
                    for (int dir = 0; dir < 4; dir++)
                    {
                        Coord d = data.GridNeighborOffset[dir];
                        int nx = gx + d.x;
                        int ny = gy + d.y;
                        if (!data.HighCoordsInBounds(nx, ny))
                        {
                            continue;
                        }
                        Point a = pf.GridCoordToWorld(new Coord(gx, gy));
                        Point b = pf.GridCoordToWorld(new Coord(nx, ny));
                        int seg = Segment(data, a, b);
                        if (seg == SegOcean)
                        {
                            if (setOcean)
                            {
                                data.SetHighNodeWeight(gx, gy, dir, d.x != 0 && d.y != 0 ? OceanDiagonal : OceanStraight);
                            }
                            continue; // otherwise untouched open sea keeps its baked weight
                        }
                        ushort weight = ushort.MaxValue;
                        if (seg == SegPassable)
                        {
                            groundWatch.Start();
                            weight = (ushort)Math.Min(Logic.PathFinding.CalcGroundWeight(pf, a, b, 0f, 10f), ushort.MaxValue - 1);
                            groundWatch.Stop();
                        }
                        else
                        {
                            // the game's generator falls back to a short low-level path here
                            detourWatch.Start();
                            uint cost = LocalPathCost(data, a, b, tile * 1.5f);
                            detourWatch.Stop();
                            if (cost < ushort.MaxValue)
                            {
                                weight = (ushort)cost;
                                detoured++;
                            }
                            else
                            {
                                blocked++;
                            }
                        }
                        data.SetHighNodeWeight(gx, gy, dir, weight);
                        updated++;
                    }
                }
            }
            Plugin.Log.LogInfo($"High grid: tiles [{gx0}..{gx1}]x[{gz0}..{gz1}], {moved} cell offsets moved, " +
                $"{updated} edges recomputed ({detoured} via detour, {blocked} blocked); " +
                $"ground weights {groundWatch.ElapsedMilliseconds} ms, detours {detourWatch.ElapsedMilliseconds} ms");
        }

        /// <summary>
        /// Dijkstra over low-level nodes in a window around a-b, with the per-step costs of
        /// CalcGroundWeight (7 * node weight, water at base weight). MaxValue if unreachable.
        /// </summary>
        // reused between calls (the grid generation runs on one thread)
        private static uint[] costBuffer = new uint[0];
        private static int[] weightBuffer = new int[0];
        private static int[] jumpBuffer = new int[0];
        private static ulong[] heap = new ulong[1024];

        private static uint LocalPathCost(PathData data, Point a, Point b, float margin)
        {
            int x0 = Math.Max((int)(Math.Min(a.x, b.x) - margin), 1);
            int x1 = Math.Min((int)(Math.Max(a.x, b.x) + margin), data.width - 2);
            int y0 = Math.Max((int)(Math.Min(a.y, b.y) - margin), 1);
            int y1 = Math.Min((int)(Math.Max(a.y, b.y) + margin), data.height - 2);
            int w = x1 - x0 + 1;
            int h = y1 - y0 + 1;
            int sx = (int)a.x - x0, sy = (int)a.y - y0, tx = (int)b.x - x0, ty = (int)b.y - y0;
            if (w <= 0 || h <= 0 || sx < 0 || sy < 0 || sx >= w || sy >= h || tx < 0 || ty < 0 || tx >= w || ty >= h)
            {
                return uint.MaxValue;
            }
            int n = w * h;
            if (costBuffer.Length < n)
            {
                costBuffer = new uint[n];
                weightBuffer = new int[n];
                jumpBuffer = new int[n];
            }
            uint[] cost = costBuffer;
            int[] weight = weightBuffer;
            int[] jump = jumpBuffer;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    cost[i] = uint.MaxValue;
                    PathData.Node node = data.GetNode(x + x0, y + y0);
                    weight[i] = node.water ? 2 : data.GetWeight(node, 0f, 0f);
                    jump[i] = -1;
                    if (node.river_offset != 0)
                    {
                        Coord o = Logic.PathFinding.GetRiverOffset(node.river_offset);
                        int jx = x + o.x, jy = y + o.y;
                        if (jx >= 0 && jy >= 0 && jx < w && jy < h)
                        {
                            jump[i] = jy * w + jx;
                        }
                    }
                }
            }
            int count = 0;
            int start = sy * w + sx, target = ty * w + tx;
            cost[start] = 0;
            Push(ref count, start, 0u);
            while (count > 0)
            {
                ulong top = Pop(ref count);
                uint c = (uint)(top >> 32);
                int idx = (int)(top & 0xFFFFFFFF);
                if (c > cost[idx])
                {
                    continue;
                }
                if (idx == target)
                {
                    return c;
                }
                int cx = idx % w, cy = idx / w;
                int j = jump[idx];
                if (j >= 0)
                {
                    // river crossing, priced like PathFinding.Open (dir 12: 5 x river_avoidance)
                    uint jc = c + RiverJumpCost;
                    if (jc < cost[j])
                    {
                        cost[j] = jc;
                        Push(ref count, j, jc);
                    }
                }
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = cy + dy;
                    if (ny < 0 || ny >= h)
                    {
                        continue;
                    }
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx;
                        if ((dx == 0 && dy == 0) || nx < 0 || nx >= w)
                        {
                            continue;
                        }
                        int ni = ny * w + nx;
                        int nodeWeight = weight[ni];
                        if (nodeWeight <= 0)
                        {
                            continue;
                        }
                        uint nc = c + (uint)((dx != 0 && dy != 0 ? 10 : 7) * nodeWeight);
                        if (nc < cost[ni])
                        {
                            cost[ni] = nc;
                            Push(ref count, ni, nc);
                        }
                    }
                }
            }
            return uint.MaxValue;
        }

        /// <summary>Binary min-heap of (cost &lt;&lt; 32 | index).</summary>
        private static void Push(ref int count, int idx, uint cost)
        {
            if (count == heap.Length)
            {
                Array.Resize(ref heap, heap.Length * 2);
            }
            ulong item = ((ulong)cost << 32) | (uint)idx;
            int i = count++;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (heap[parent] <= item)
                {
                    break;
                }
                heap[i] = heap[parent];
                i = parent;
            }
            heap[i] = item;
        }

        private static ulong Pop(ref int count)
        {
            ulong top = heap[0];
            ulong last = heap[--count];
            int i = 0;
            while (true)
            {
                int child = 2 * i + 1;
                if (child >= count)
                {
                    break;
                }
                if (child + 1 < count && heap[child + 1] < heap[child])
                {
                    child++;
                }
                if (last <= heap[child])
                {
                    break;
                }
                heap[i] = heap[child];
                i = child;
            }
            heap[i] = last;
            return top;
        }

        private const uint RiverJumpCost = 5 * 100; // europe pathfinding.def river_avoidance = 100

        private const ushort OceanStraight = 170;
        private const ushort OceanDiagonal = 234;

        private const int SegOcean = 0;
        private const int SegPassable = 1;
        private const int SegBlocked = 2;

        /// <summary>Classify the straight line a-b: all open ocean, passable (touches land), or blocked by impassable land.</summary>
        private static int Segment(PathData data, Point a, Point b)
        {
            float len = (b - a).Length();
            int steps = Math.Max((int)Math.Ceiling(len * 2f), 1);
            bool touchesLand = false;
            for (int s = 0; s <= steps; s++)
            {
                Point p = a + (b - a) * (s / (float)steps);
                PathData.Node n = data.GetNode(p);
                if (n.ocean)
                {
                    continue;
                }
                touchesLand = true;
                if (!n.water && data.GetWeight(n, 0f, 0f) == 0)
                {
                    return SegBlocked;
                }
            }
            return touchesLand ? SegPassable : SegOcean;
        }

        private static byte CellOffset(PathData data, int gx, int gy, float tile)
        {
            const byte center = 0x88;
            Point corner = new Point(gx * tile, gy * tile);
            PathData.Node c = data.GetNode(corner + new Point(8f, 8f));
            if (c.water || data.GetWeight(c, 0f, 0f) > 0)
            {
                return center;
            }
            float best = float.MaxValue;
            byte result = center;
            for (int dy = -7; dy <= 7; dy++)
            {
                for (int dx = -7; dx <= 7; dx++)
                {
                    PathData.Node n = data.GetNode(corner + new Point(8 + dx, 8 + dy));
                    if (n.water || data.GetWeight(n, 0f, 0f) == 0)
                    {
                        continue;
                    }
                    float dist = dx * dx + dy * dy;
                    if (dist < best)
                    {
                        best = dist;
                        result = (byte)(((dx + 8) << 4) | (dy + 8));
                    }
                }
            }
            return result;
        }
    }
}
