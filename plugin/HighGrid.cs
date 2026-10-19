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
        /// An edge weighs the low level cost between its two waypoints, like vanilla's baked grid (the
        /// runtime generator's CalcGroundWeight is ~6x that on open land and pulled paths to every
        /// obstacle, river crossing and the sea). Open ocean edges keep their baked weight, or get the
        /// vanilla ones (170 straight, 234 diagonal) with <paramref name="setOcean"/> when the whole grid
        /// is regenerated.
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

            // cell offsets: the tile center unless an obstacle is near, then the most open node (F2)
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ushort[] clearance = Clearance(data);
            int moved = 0;
            for (int gy = gz0; gy <= gz1; gy++)
            {
                for (int gx = gx0; gx <= gx1; gx++)
                {
                    PathData.HighGridNode hn = data.GetHighGridNode(gx, gy);
                    byte offset = CellOffset(data, clearance, gx, gy, (int)tile);
                    if (offset != hn.cell_offset)
                    {
                        hn.cell_offset = offset;
                        data.SetHighNode(gx, gy, hn);
                        moved++;
                    }
                }
            }

            // edges: open sea keeps vanilla's weights, every other edge weighs what the low level search pays
            // between the two waypoints, the scale of vanilla's baked grid (docs/hre/PATHFINDING_PLAN.md, F1)
            var cost = new EdgeCost(pf);
            int updated = 0, blocked = 0;
            object counts = new object();
            System.Threading.Tasks.Parallel.For(gz0, gz1 + 1, () => new EdgeSearch(data, cost, (int)(tile * 1.5f)), (gy, _, search) =>
            {
                int rowUpdated = 0, rowBlocked = 0;
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
                        if (Segment(data, a, b) == SegOcean)
                        {
                            if (setOcean)
                            {
                                data.SetHighNodeWeight(gx, gy, dir, d.x != 0 && d.y != 0 ? OceanDiagonal : OceanStraight);
                            }
                            continue; // otherwise untouched open sea keeps its baked weight
                        }
                        uint c = search.Run((int)a.x, (int)a.y, (int)b.x, (int)b.y);
                        ushort weight = c < ushort.MaxValue ? (ushort)c : ushort.MaxValue;
                        if (weight == ushort.MaxValue)
                        {
                            rowBlocked++;
                        }
                        data.SetHighNodeWeight(gx, gy, dir, weight);
                        rowUpdated++;
                    }
                }
                lock (counts)
                {
                    updated += rowUpdated;
                    blocked += rowBlocked;
                }
                return search;
            }, _ => { });
            Plugin.Log.LogInfo($"High grid: tiles [{gx0}..{gx1}]x[{gz0}..{gz1}], {moved} waypoints moved, " +
                $"{updated} edges priced by the low level search ({blocked} blocked), {watch.ElapsedMilliseconds} ms");
        }

        /// <summary>The step costs of Logic.PathFinding.Open for a campaign army (max_radius 0).</summary>
        private sealed class EdgeCost
        {
            public readonly int BaseWeight;
            public readonly int EnterWater;
            public readonly int River;
            public readonly int Estimate;

            public EdgeCost(Logic.PathFinding pf)
            {
                BaseWeight = pf.settings.base_weight;
                EnterWater = pf.settings.enter_water_weight_mod;
                River = (int)pf.settings.river_avoidance;
                Estimate = pf.settings.estimate_weight;
            }

            /// <summary>
            /// Cost of the step prev -> node in direction dir (12: river jump), -1 where Open refuses it. No
            /// cheap water entry: an edge is priced for an army that stays on land. Rules: water costs the
            /// base weight, times the water entry factor when coming from land, and so does land reached
            /// from water; land of weight 0 is only entered from other weight 0 land, at 10; a river jump
            /// costs 5 river avoidance; diagonals (dir 0, 2, 5, 7) cost 7/5 of a straight step.
            /// </summary>
            public int Step(in PathData.Node prev, in PathData.Node node, int dir)
            {
                bool zeroLand = !node.ocean && node.weight == 0;
                if (zeroLand && (prev.ocean || prev.weight > 0))
                    return -1;
                if (dir == 12)
                    return 5 * River;
                int terrain;
                if (node.ocean != prev.ocean)
                    terrain = BaseWeight * EnterWater;
                else if (node.ocean)
                    terrain = BaseWeight;
                else
                    terrain = zeroLand ? 10 : node.weight;
                bool diagonal = dir == 0 || dir == 2 || dir == 5 || dir == 7;
                return (diagonal ? 7 : 5) * terrain;
            }
        }

        /// <summary>
        /// A* over the low level nodes in a window around a-b with the game's step costs and estimate
        /// (PathFinding.Estimate), ties broken towards the lower estimate. One per thread; the buffers
        /// cover the window only.
        /// </summary>
        private sealed class EdgeSearch
        {
            private static readonly int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
            private static readonly int[] dy = { -1, -1, -1, 0, 0, 1, 1, 1 };

            private readonly PathData data;
            private readonly EdgeCost cost;
            private readonly int margin;
            private uint[] g = new uint[0];
            private uint[] h = new uint[0];
            private int[] parent = new int[0];
            private int[] seen = new int[0];
            private int version;
            private ulong[] heap = new ulong[1024];
            private int count;

            public EdgeSearch(PathData data, EdgeCost cost, int margin)
            {
                this.data = data;
                this.cost = cost;
                this.margin = margin;
            }

            /// <summary>Low level cost from (ax, ay) to (bx, by), uint.MaxValue if not connected in the window.</summary>
            public uint Run(int ax, int ay, int bx, int by)
            {
                int x0 = Math.Max(Math.Min(ax, bx) - margin, 1);
                int x1 = Math.Min(Math.Max(ax, bx) + margin, data.width - 2);
                int y0 = Math.Max(Math.Min(ay, by) - margin, 1);
                int y1 = Math.Min(Math.Max(ay, by) + margin, data.height - 2);
                int w = x1 - x0 + 1;
                int n = w * (y1 - y0 + 1);
                if (ax < x0 || ax > x1 || ay < y0 || ay > y1 || bx < x0 || bx > x1 || by < y0 || by > y1)
                {
                    return uint.MaxValue;
                }
                if (g.Length < n)
                {
                    g = new uint[n];
                    h = new uint[n];
                    parent = new int[n];
                    seen = new int[n];
                    version = 0;
                }
                if (++version == int.MaxValue)
                {
                    Array.Clear(seen, 0, seen.Length);
                    version = 1;
                }
                PathData.Node[] nodes = data.nodes;
                int width = data.width;
                int start = (ay - y0) * w + (ax - x0);
                int goal = (by - y0) * w + (bx - x0);
                count = 0;
                seen[start] = version;
                g[start] = 0;
                parent[start] = -1;
                h[start] = Estimate(ax, ay, bx, by);
                Push(h[start], h[start], start);
                while (count > 0)
                {
                    int cur = Pop(out uint f);
                    if (f != g[cur] + h[cur])
                    {
                        continue; // stale
                    }
                    if (cur == goal)
                    {
                        return g[cur];
                    }
                    int cx = cur % w + x0, cy = cur / w + y0;
                    PathData.Node prev = nodes[cy * width + cx];
                    // river jump, not straight back over the jump that led here (Step: prev.dir != 12)
                    if (prev.river_offset != 0)
                    {
                        Coord o = Logic.PathFinding.GetRiverOffset(prev.river_offset);
                        int jx = cx + o.x, jy = cy + o.y;
                        if (jx >= x0 && jx <= x1 && jy >= y0 && jy <= y1)
                        {
                            int j = (jy - y0) * w + (jx - x0);
                            if (j != parent[cur] && nodes[jy * width + jx].river_offset != 0)
                            {
                                Relax(cur, j, jx, jy, cost.Step(prev, nodes[jy * width + jx], 12), bx, by);
                            }
                        }
                    }
                    for (int d = 0; d < 8; d++)
                    {
                        int nx = cx + dx[d], ny = cy + dy[d];
                        if (nx < x0 || nx > x1 || ny < y0 || ny > y1)
                        {
                            continue;
                        }
                        Relax(cur, (ny - y0) * w + (nx - x0), nx, ny, cost.Step(prev, nodes[ny * width + nx], d), bx, by);
                    }
                }
                return uint.MaxValue;
            }

            private void Relax(int cur, int next, int nx, int ny, int step, int bx, int by)
            {
                if (step < 0)
                {
                    return;
                }
                uint ng = g[cur] + (uint)step;
                if (seen[next] == version)
                {
                    if (ng >= g[next])
                    {
                        return;
                    }
                }
                else
                {
                    seen[next] = version;
                    h[next] = Estimate(nx, ny, bx, by);
                }
                g[next] = ng;
                parent[next] = cur;
                Push(ng + h[next], h[next], next);
            }

            private uint Estimate(int x, int y, int bx, int by)
            {
                int ex = Math.Abs(x - bx), ey = Math.Abs(y - by);
                int max = Math.Max(ex, ey), min = Math.Min(ex, ey);
                return (uint)(cost.Estimate * ((max - min) * 5 + min * 7));
            }

            /// <summary>Binary min-heap of (f &lt;&lt; 40 | h &lt;&lt; 20 | window index); f below 2^24, h and index below 2^20.</summary>
            private void Push(uint f, uint est, int idx)
            {
                if (count == heap.Length)
                {
                    Array.Resize(ref heap, heap.Length * 2);
                }
                ulong item = ((ulong)Math.Min(f, 0xFFFFFFu) << 40) | ((ulong)Math.Min(est, 0xFFFFFu) << 20) | (uint)idx;
                int i = count++;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (heap[p] <= item)
                    {
                        break;
                    }
                    heap[i] = heap[p];
                    i = p;
                }
                heap[i] = item;
            }

            private int Pop(out uint f)
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
                f = (uint)(top >> 40);
                return (int)(top & 0xFFFFF);
            }
        }

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

        /// <summary>
        /// Chamfer 3-4 distance (x3) of every node to the nearest obstacle: impassable land or a river
        /// (weight 0, not water). Stands in for the clearance vanilla's grid was baked with.
        /// </summary>
        private static ushort[] Clearance(PathData data)
        {
            int w = data.width, h = data.height;
            PathData.Node[] nodes = data.nodes;
            var c = new ushort[w * h];
            for (int i = 0; i < c.Length; i++)
            {
                c[i] = nodes[i].weight == 0 && !nodes[i].water ? (ushort)0 : ushort.MaxValue;
            }
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    int v = c[i];
                    if (x > 0) v = Math.Min(v, c[i - 1] + 3);
                    if (y > 0)
                    {
                        v = Math.Min(v, c[i - w] + 3);
                        if (x > 0) v = Math.Min(v, c[i - w - 1] + 4);
                        if (x < w - 1) v = Math.Min(v, c[i - w + 1] + 4);
                    }
                    c[i] = (ushort)Math.Min(v, ushort.MaxValue);
                }
            }
            for (int y = h - 1; y >= 0; y--)
            {
                for (int x = w - 1; x >= 0; x--)
                {
                    int i = y * w + x;
                    int v = c[i];
                    if (x < w - 1) v = Math.Min(v, c[i + 1] + 3);
                    if (y < h - 1)
                    {
                        v = Math.Min(v, c[i + w] + 3);
                        if (x < w - 1) v = Math.Min(v, c[i + w + 1] + 4);
                        if (x > 0) v = Math.Min(v, c[i + w - 1] + 4);
                    }
                    c[i] = (ushort)Math.Min(v, ushort.MaxValue);
                }
            }
            return c;
        }

        /// <summary>
        /// Like vanilla's baked waypoints: the tile center while it is open (no obstacle within 7 units)
        /// or water, else the passable land node of the tile farthest from any obstacle, the nearest to
        /// the center on ties. Keeps the waypoint off banks, cliffs and narrow spots.
        /// </summary>
        private static byte CellOffset(PathData data, ushort[] clearance, int gx, int gy, int tile)
        {
            const byte center = 0x88;
            int cx = Math.Min(gx * tile + 8, data.width - 1), cy = Math.Min(gy * tile + 8, data.height - 1);
            int ci = cy * data.width + cx;
            if (data.nodes[ci].water || clearance[ci] >= 7 * 3)
            {
                return center;
            }
            int best = -1, bestDist = 0;
            byte result = center;
            for (int oy = 0; oy < tile; oy++)
            {
                for (int ox = 0; ox < tile; ox++)
                {
                    int x = gx * tile + ox, y = gy * tile + oy;
                    if (x >= data.width || y >= data.height)
                    {
                        continue;
                    }
                    int i = y * data.width + x;
                    PathData.Node n = data.nodes[i];
                    if (n.water || n.weight == 0)
                    {
                        continue;
                    }
                    int dist = (ox - 8) * (ox - 8) + (oy - 8) * (oy - 8);
                    if (clearance[i] > best || (clearance[i] == best && dist < bestDist))
                    {
                        best = clearance[i];
                        bestDist = dist;
                        result = (byte)((ox << 4) | oy);
                    }
                }
            }
            return result;
        }
    }
}
