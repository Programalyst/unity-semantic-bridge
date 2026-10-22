using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Gamenami.UnitySemanticBridge.Layout
{
    /// <summary>A band a metric's value should fall in; either end may be open.</summary>
    public sealed class MetricTarget
    {
        public double? Min;
        public double? Max;

        public MetricTarget(double? min, double? max)
        {
            Min = min;
            Max = max;
        }

        public bool Contains(double value) => (!Min.HasValue || value >= Min.Value - 1e-9) && (!Max.HasValue || value <= Max.Value + 1e-9);

        public override string ToString() =>
            Min.HasValue && Max.HasValue ? $"{LayoutMetrics.N(Min.Value)}-{LayoutMetrics.N(Max.Value)}"
            : Min.HasValue ? $">= {LayoutMetrics.N(Min.Value)}" : Max.HasValue ? $"<= {LayoutMetrics.N(Max.Value)}" : "any";
    }

    /// <summary>
    /// The game numbers the level metrics need, and the game's targets per metric code. USB's defaults are
    /// neutral; a game sets its own in its GridRules (NK: NkGridRules). Targets are a game's taste, so USB has none.
    /// </summary>
    public sealed class MetricSettings
    {
        /// <summary>Sight range in tiles (circular, between cell centres), for the squad and the enemies.</summary>
        public double VisionRange = 8;
        /// <summary>Steps a unit can move in one turn.</summary>
        public int MovePerTurn = 10;
        /// <summary>"Near" a unit: open cells within this many steps (COVER_NEAR).</summary>
        public int NearSteps = 3;
        /// <summary>Cells added around the deploy cells and spawns for the engagement region.</summary>
        public int RegionMargin = 4;
        /// <summary>A flanking route counts while it is at most this times the shortest route's length.</summary>
        public double RouteStretch = 1.5;
        /// <summary>Most routes counted per enemy.</summary>
        public int MaxRoutes = 5;
        /// <summary>Target bands by metric code (DISTANCE, COVER_NEAR, COVER_NEAR_DEPLOY, ROUTES, OPEN_GROUND, COVER_SPACING, DEAD_SPACE, SIGHTLINES).</summary>
        public Dictionary<string, MetricTarget> Targets = new Dictionary<string, MetricTarget>();
    }

    /// <summary>One measured value: a "CODE x,y message" line plus the number its target is checked against.</summary>
    public sealed class MetricLine
    {
        public string Code;
        public Cell? Cell;
        public double Value;
        public string Message;
        public MetricTarget Target;
        public bool Outside => Target != null && !Target.Contains(Value);

        public override string ToString()
        {
            var text = Cell.HasValue ? $"{Code} {Cell.Value} {Message}" : $"{Code} {Message}";
            return Target == null ? text : $"{text} [target {Target}]";
        }
    }

    /// <summary>Level metrics for one layout or scene, in LayoutReport's line style.</summary>
    public sealed class MetricsReport
    {
        public readonly List<MetricLine> Lines = new List<MetricLine>();
        public Cell RegionMin;
        public Cell RegionMax;
        public int RegionCells;
        public int RegionOpen;
        public MetricSettings Settings;

        public int OutsideCount => Lines.Count(l => l.Outside);

        public IEnumerable<MetricLine> WithCode(string code) => Lines.Where(l => l.Code == code);

        /// <summary>Header, then the lines outside their targets, then the rest; at most maxLines lines (0 = all).</summary>
        public string Format(int maxLines = LayoutReport.DefaultMaxLines)
        {
            var s = Settings;
            var builder = new StringBuilder();
            builder.Append($"LAYOUT_METRICS region {RegionMin}-{RegionMax} ({RegionCells} cells, {RegionOpen} open); ")
                .Append($"vision {LayoutMetrics.N(s.VisionRange)}, move {s.MovePerTurn}, near {s.NearSteps}");
            if (s.Targets.Count > 0) builder.Append($"; {OutsideCount} outside their targets");
            builder.Append('\n');
            int shown = 0, hidden = 0;
            foreach (var (title, group) in new[] { ("Outside targets", Lines.Where(l => l.Outside).ToList()), ("Values", Lines.Where(l => !l.Outside).ToList()) })
            {
                if (group.Count == 0) continue;
                builder.Append(title).Append(" (").Append(group.Count).Append("):\n");
                foreach (var line in group)
                {
                    if (maxLines > 0 && shown >= maxLines) { hidden++; continue; }
                    builder.Append(line).Append('\n');
                    shown++;
                }
            }
            if (hidden > 0) builder.Append("... ").Append(hidden).Append(" more lines not shown (cap ").Append(maxLines).Append(")\n");
            return builder.ToString().TrimEnd('\n');
        }
    }

    /// <summary>
    /// Level metrics (plan Phase 6): judgement, not correctness, so they never fail a report. Pure C#, on a LayoutGrid
    /// (a draft's or the game's occupancy bake of a scene) with the game's rules already in it: CanStep for paths (a
    /// diagonal is one step), CoverOn for cover, CanSee for sight (with step-out when the rules have it).
    /// Per enemy: DISTANCE, COVER_NEAR, ROUTES, OPEN_GROUND; for the squad COVER_NEAR_DEPLOY; over the engagement
    /// region DEAD_SPACE and SIGHTLINES; per enemy also COVER_SPACING.
    /// </summary>
    public static class LayoutMetrics
    {
        public static MetricsReport Measure(LayoutGrid grid, IReadOnlyList<Cell> deploy, IReadOnlyList<LayoutEnemySpawn> enemies,
            MetricSettings settings = null, (Cell min, Cell max)? region = null)
        {
            settings ??= grid.Rules.Metrics ?? new MetricSettings();
            var report = new MetricsReport { Settings = settings };
            var starts = deploy.Where(c => grid.Contains(c) && !grid.IsBlocked(c)).Distinct().ToList();
            var foes = enemies.Where(e => grid.Contains(e.Cell) && !grid.IsBlocked(e.Cell)).ToList();

            var (min, max) = region ?? Region(grid, starts.Concat(foes.Select(e => e.Cell)), settings.RegionMargin);
            min = new Cell(Math.Max(0, min.X), Math.Max(0, min.Y));
            max = new Cell(Math.Min(grid.Width - 1, max.X), Math.Min(grid.Height - 1, max.Y));
            report.RegionMin = min;
            report.RegionMax = max;
            var inRegion = new List<Cell>();
            for (var y = min.Y; y <= max.Y; y++)
                for (var x = min.X; x <= max.X; x++)
                    inRegion.Add(new Cell(x, y));
            report.RegionCells = inRegion.Count;
            report.RegionOpen = inRegion.Count(c => !grid.IsBlocked(c));

            if (starts.Count == 0)
            {
                report.Lines.Add(Line(settings, "NO_DEPLOY", null, 0, "no open deploy cell; nothing measured"));
                return report;
            }
            var fromDeploy = Distances(grid, starts, out var source);
            var squad = Centroid(starts);
            var foeCentre = foes.Count > 0 ? Centroid(foes.Select(e => e.Cell)) : squad;

            foreach (var enemy in foes)
            {
                var cell = enemy.Cell;
                if (!fromDeploy.TryGetValue(cell, out var steps))
                {
                    report.Lines.Add(Line(settings, "DISTANCE", cell, double.NaN, $"{enemy.LocalId}: unreachable from the deploy cells"));
                    continue;
                }
                var straight = starts.Min(s => Euclid(s, cell));
                report.Lines.Add(Line(settings, "DISTANCE", cell, steps / (double)settings.MovePerTurn,
                    $"{enemy.LocalId}: {steps} steps from deploy #{source[cell] + 1} ({N(steps / (double)settings.MovePerTurn)} turns), {N(straight)} tiles straight"));
            }
            foreach (var enemy in foes)
                report.Lines.Add(CoverNear(grid, settings, "COVER_NEAR", enemy.Cell, new[] { enemy.Cell }, squad, $"{enemy.LocalId}", "the squad"));
            report.Lines.Add(CoverNear(grid, settings, "COVER_NEAR_DEPLOY", starts[0], starts, foeCentre, "squad", "the enemies"));
            foreach (var enemy in foes.Where(e => fromDeploy.ContainsKey(e.Cell)))
                report.Lines.Add(Routes(grid, settings, starts, enemy));
            foreach (var enemy in foes.Where(e => fromDeploy.ContainsKey(e.Cell)))
                report.Lines.Add(OpenGround(grid, settings, starts, enemy));
            foreach (var enemy in foes.Where(e => fromDeploy.ContainsKey(e.Cell)))
                report.Lines.Add(CoverSpacing(grid, settings, starts, enemy));
            var reachable = inRegion.Where(c => fromDeploy.ContainsKey(c)).ToList();
            report.Lines.Add(DeadSpace(grid, settings, fromDeploy, foes.Where(e => fromDeploy.ContainsKey(e.Cell)).ToList(), reachable));
            report.Lines.Add(Sightlines(grid, settings, reachable));
            return report;
        }

        // ---- metrics ----

        // Open cells within NearSteps of the given cells, and how many have Half or Full cover on the side facing a point
        // (the game's rule: the dominant axis towards it, ties to x).
        private static MetricLine CoverNear(LayoutGrid grid, MetricSettings s, string code, Cell at, IEnumerable<Cell> from,
            (double x, double y) towards, string who, string whom)
        {
            var near = Distances(grid, from.ToList(), out _, s.NearSteps).Keys.ToList();
            int full = 0, half = 0;
            foreach (var cell in near)
            {
                var side = SideFacing(cell, towards);
                if (!side.HasValue) continue;
                var cover = grid.CoverOn(cell, side.Value);
                if (cover == CoverLevel.Full) full++;
                else if (cover == CoverLevel.Half) half++;
            }
            var share = near.Count == 0 ? 0 : (full + half) / (double)near.Count;
            return Line(s, code, at, share,
                $"{who}: {full + half} of {near.Count} open cells within {s.NearSteps} steps have cover facing {whom} ({full} Full, {half} Half; {Pct(share)})");
        }

        // Greedy separated routes: the shortest path, then each next shortest that shares no cell with the earlier ones
        // and does not touch them (8-neighbours), up to RouteStretch times the first. Cells within 2 of the enemy or a
        // deploy cell stay usable, since every route must start and end there.
        private static MetricLine Routes(LayoutGrid grid, MetricSettings s, List<Cell> starts, LayoutEnemySpawn enemy)
        {
            var forbidden = new HashSet<Cell>();
            var lengths = new List<int>();
            bool Protected(Cell c) => Chebyshev(c, enemy.Cell) <= 2 || starts.Any(d => Chebyshev(c, d) <= 2);
            while (lengths.Count < s.MaxRoutes)
            {
                var path = ShortestPath(grid, starts, enemy.Cell, forbidden);
                if (path == null) break;
                var length = path.Count - 1;
                if (lengths.Count > 0 && length > s.RouteStretch * lengths[0] + 1e-9) break;
                lengths.Add(length);
                foreach (var c in path)
                    for (var dx = -1; dx <= 1; dx++)
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            var n = new Cell(c.X + dx, c.Y + dy);
                            if (!Protected(n)) forbidden.Add(n);
                        }
            }
            return Line(s, "ROUTES", enemy.Cell, lengths.Count,
                $"{enemy.LocalId}: {lengths.Count} distinct route{(lengths.Count == 1 ? "" : "s")} within {N(s.RouteStretch)}x the shortest ({string.Join(", ", lengths)} steps)");
        }

        // Exposed cell: the enemy sees it within range and it has no cover facing the enemy. The smallest possible longest
        // run of exposed steps over all routes (binary search on the run), and the exposed steps on one shortest route.
        private static MetricLine OpenGround(LayoutGrid grid, MetricSettings s, List<Cell> starts, LayoutEnemySpawn enemy)
        {
            var target = enemy.Cell;
            var towards = ((double)target.X, (double)target.Y);
            var exposed = new Dictionary<Cell, bool>();
            bool Exposed(Cell c)
            {
                if (c == target) return false;
                if (exposed.TryGetValue(c, out var e)) return e;
                var side = SideFacing(c, towards);
                e = Euclid(c, target) <= s.VisionRange + 1e-9 && grid.CanSee(target, c) &&
                    (!side.HasValue || grid.CoverOn(c, side.Value) == CoverLevel.None);
                exposed[c] = e;
                return e;
            }
            var shortest = ShortestPath(grid, starts, target, null);
            var onShortest = shortest.Count(Exposed);
            int lo = 0, hi = shortest.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (Reaches(grid, starts, target, mid, Exposed)) hi = mid;
                else lo = mid + 1;
            }
            var note = lo > s.MovePerTurn ? $" (more than one move of {s.MovePerTurn})" : "";
            return Line(s, "OPEN_GROUND", target, lo,
                $"{enemy.LocalId}: longest exposed run {lo} steps on the best route{note}; shortest route {shortest.Count - 1} steps, {onShortest} exposed");
        }

        // Cover along the way, seen or not: the smallest possible longest run of steps without cover facing the enemy
        // over all routes (a cell with Half or Full cover on its side towards the enemy breaks the run).
        private static MetricLine CoverSpacing(LayoutGrid grid, MetricSettings s, List<Cell> starts, LayoutEnemySpawn enemy)
        {
            var target = enemy.Cell;
            var towards = ((double)target.X, (double)target.Y);
            bool Bare(Cell c)
            {
                if (c == target) return false;
                var side = SideFacing(c, towards);
                return !side.HasValue || grid.CoverOn(c, side.Value) == CoverLevel.None;
            }
            var shortest = ShortestPath(grid, starts, target, null);
            int lo = 0, hi = shortest.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (Reaches(grid, starts, target, mid, Bare)) hi = mid;
                else lo = mid + 1;
            }
            return Line(s, "COVER_SPACING", target, lo,
                $"{enemy.LocalId}: at most {lo} steps between cover facing it on the best route ({N(lo / (double)s.MovePerTurn)} moves)");
        }

        // Region cells off every reasonable route: a reachable cell is dead when, for every enemy, going through it is
        // more than RouteStretch times that enemy's shortest route. Dead ends and empty corners score; corridors don't.
        private static MetricLine DeadSpace(LayoutGrid grid, MetricSettings s, Dictionary<Cell, int> fromDeploy,
            List<LayoutEnemySpawn> enemies, List<Cell> reachable)
        {
            if (enemies.Count == 0 || reachable.Count == 0)
                return Line(s, "DEAD_SPACE", null, 0, "no reachable enemy; not measured");
            var toEnemy = enemies.Select(e => Distances(grid, new List<Cell> { e.Cell }, out _)).ToList();
            var dead = reachable.Count(c => enemies.Select((e, i) => (e, i)).All(t =>
                !toEnemy[t.i].TryGetValue(c, out var back) || fromDeploy[c] + back > s.RouteStretch * fromDeploy[t.e.Cell] + 1e-9));
            var share = dead / (double)reachable.Count;
            return Line(s, "DEAD_SPACE", null, share,
                $"{dead} of {reachable.Count} reachable region cells ({Pct(share)}) are off every route within {N(s.RouteStretch)}x an enemy's shortest");
        }

        // Pairs of open, reachable region cells within twice the vision range: the share that see each other per
        // distance band of 3 tiles, and the median count of cells each cell sees within the vision range.
        private static MetricLine Sightlines(LayoutGrid grid, MetricSettings s, List<Cell> cells)
        {
            var reach = 2 * s.VisionRange;
            var bands = Math.Max(1, (int)Math.Ceiling(reach / 3));
            var seen = new int[bands];
            var total = new int[bands];
            var exposure = new int[cells.Count];
            for (var i = 0; i < cells.Count; i++)
                for (var j = i + 1; j < cells.Count; j++)
                {
                    var d = Euclid(cells[i], cells[j]);
                    if (d > reach + 1e-9) continue;
                    var band = Math.Min(bands - 1, (int)((Math.Ceiling(d - 1e-9) - 1) / 3));
                    total[band]++;
                    // A sightline either way (sight is one-way with step-out).
                    if (!grid.CanSee(cells[i], cells[j]) && !grid.CanSee(cells[j], cells[i])) continue;
                    seen[band]++;
                    if (d <= s.VisionRange + 1e-9) { exposure[i]++; exposure[j]++; }
                }
            var parts = new List<string>();
            for (var b = 0; b < bands; b++)
            {
                if (total[b] == 0) continue;
                var label = b == bands - 1 ? $"{3 * b + 1}+" : $"{3 * b + 1}-{3 * b + 3}";
                parts.Add($"{label} {Pct(seen[b] / (double)total[b])}");
            }
            var sorted = exposure.OrderBy(v => v).ToList();
            var median = sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
            return Line(s, "SIGHTLINES", null, median,
                $"visible pairs by distance {string.Join(", ", parts)}; median exposure {N(median)} cells within {N(s.VisionRange)} tiles ({cells.Count} cells)");
        }

        // ---- search ----

        /// <summary>Steps from the nearest start over CanStep (a diagonal is one step), up to maxSteps; source = index of that start.</summary>
        public static Dictionary<Cell, int> Distances(LayoutGrid grid, List<Cell> starts, out Dictionary<Cell, int> source, int maxSteps = int.MaxValue)
        {
            var dist = new Dictionary<Cell, int>();
            source = new Dictionary<Cell, int>();
            var queue = new Queue<Cell>();
            for (var i = 0; i < starts.Count; i++)
            {
                if (dist.ContainsKey(starts[i])) continue;
                dist[starts[i]] = 0;
                source[starts[i]] = i;
                queue.Enqueue(starts[i]);
            }
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                var d = dist[cell];
                if (d >= maxSteps) continue;
                foreach (var next in grid.Neighbours(cell))
                {
                    if (dist.ContainsKey(next)) continue;
                    dist[next] = d + 1;
                    source[next] = source[cell];
                    queue.Enqueue(next);
                }
            }
            return dist;
        }

        // A shortest path (cells, starts first) from any start to the target avoiding forbidden cells, or null.
        private static List<Cell> ShortestPath(LayoutGrid grid, List<Cell> starts, Cell target, HashSet<Cell> forbidden)
        {
            var parent = new Dictionary<Cell, Cell>();
            var seen = new HashSet<Cell>();
            var queue = new Queue<Cell>();
            foreach (var start in starts)
                if (seen.Add(start)) queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                if (cell == target)
                {
                    var path = new List<Cell> { cell };
                    while (parent.TryGetValue(cell, out var p)) path.Add(cell = p);
                    path.Reverse();
                    return path;
                }
                foreach (var next in grid.Neighbours(cell))
                {
                    if ((forbidden != null && forbidden.Contains(next)) || !seen.Add(next)) continue;
                    parent[next] = cell;
                    queue.Enqueue(next);
                }
            }
            return null;
        }

        // Whether the target can be reached with no run of more than maxRun consecutive exposed cells.
        private static bool Reaches(LayoutGrid grid, List<Cell> starts, Cell target, int maxRun, Func<Cell, bool> exposed)
        {
            var best = new Dictionary<Cell, int>(); // smallest current run seen at a cell
            var queue = new Queue<(Cell cell, int run)>();
            foreach (var start in starts.Distinct())
            {
                var run = exposed(start) ? 1 : 0;
                if (run > maxRun) continue;
                best[start] = run;
                queue.Enqueue((start, run));
            }
            while (queue.Count > 0)
            {
                var (cell, run) = queue.Dequeue();
                if (cell == target) return true;
                if (best[cell] < run) continue;
                foreach (var next in grid.Neighbours(cell))
                {
                    var r = exposed(next) ? run + 1 : 0;
                    if (r > maxRun || (best.TryGetValue(next, out var b) && b <= r)) continue;
                    best[next] = r;
                    queue.Enqueue((next, r));
                }
            }
            return false;
        }

        // ---- helpers ----

        private static (Cell, Cell) Region(LayoutGrid grid, IEnumerable<Cell> cells, int margin)
        {
            var list = cells.ToList();
            if (list.Count == 0) return (new Cell(0, 0), new Cell(grid.Width - 1, grid.Height - 1));
            return (new Cell(list.Min(c => c.X) - margin, list.Min(c => c.Y) - margin), new Cell(list.Max(c => c.X) + margin, list.Max(c => c.Y) + margin));
        }

        // The side of a cell facing a point by the dominant axis, ties to x (the game's cover rule); null on the point.
        private static GridSide? SideFacing(Cell cell, (double x, double y) point)
        {
            double dx = point.x - cell.X, dy = point.y - cell.Y;
            if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return null;
            return Math.Abs(dx) >= Math.Abs(dy) ? dx > 0 ? GridSide.PlusX : GridSide.MinusX : dy > 0 ? GridSide.PlusY : GridSide.MinusY;
        }

        private static (double, double) Centroid(IEnumerable<Cell> cells)
        {
            var list = cells.ToList();
            return (list.Average(c => c.X), list.Average(c => c.Y));
        }

        private static double Euclid(Cell a, Cell b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        private static int Chebyshev(Cell a, Cell b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        private static MetricLine Line(MetricSettings s, string code, Cell? cell, double value, string message) =>
            new MetricLine { Code = code, Cell = cell, Value = value, Message = message, Target = s.Targets.TryGetValue(code, out var t) ? t : null };

        private static string Pct(double share) => $"{Math.Round(share * 100):0}%";

        /// <summary>Numbers in reports: up to two decimals, invariant culture.</summary>
        public static string N(double value) => double.IsNaN(value) ? "-" : value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
