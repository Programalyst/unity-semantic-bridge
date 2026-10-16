using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Gamenami.UnitySemanticBridge.Layout
{
    /// <summary>
    /// A collider the game's sight test hits, reduced to the board cells it covers at sight
    /// height. Input to the consistency check; the Editor gathers these from scene physics.
    /// </summary>
    public sealed class SightCollider
    {
        public string Name;
        /// <summary>True when the collider belongs to a layout blocker (it also blocks movement).</summary>
        public bool IsObstacle;
        public List<Cell> Cells = new List<Cell>();

        public SightCollider(string name, bool isObstacle, IEnumerable<Cell> cells)
        {
            Name = name;
            IsObstacle = isObstacle;
            Cells.AddRange(cells);
        }
    }

    /// <summary>Validator output: cell-addressed issues plus what the ASCII map needs.</summary>
    public sealed class LayoutReport
    {
        public const int DefaultMaxLines = 40;

        public readonly List<LayoutIssue> Issues = new List<LayoutIssue>();
        /// <summary>Occupancy the reachability and encounter checks ran on.</summary>
        public LayoutGrid Grid;
        public readonly List<Cell> Deploy = new List<Cell>();
        public readonly List<LayoutEnemySpawn> Enemies = new List<LayoutEnemySpawn>();
        /// <summary>Open cells no deploy cell reaches.</summary>
        public readonly HashSet<Cell> Unreachable = new HashSet<Cell>();

        public int ErrorCount => Issues.Count(i => i.Severity == IssueSeverity.Error);
        public int WarningCount => Issues.Count(i => i.Severity == IssueSeverity.Warning);
        public bool HasErrors => ErrorCount > 0;

        public IEnumerable<LayoutIssue> WithCode(string code) => Issues.Where(i => i.Code == code);

        /// <summary>
        /// Errors, then warnings, one "CODE x,y message" line each, at most <paramref name="maxLines"/>
        /// issue lines (0 = all); the ASCII map is appended on request.
        /// </summary>
        public string Format(int maxLines = DefaultMaxLines, bool ascii = false)
        {
            var builder = new StringBuilder();
            builder.Append("LAYOUT_REPORT ").Append(ErrorCount).Append(" errors, ").Append(WarningCount).Append(" warnings\n");
            var shown = 0;
            var hidden = 0;
            foreach (var severity in new[] { IssueSeverity.Error, IssueSeverity.Warning })
            {
                var group = Issues.Where(i => i.Severity == severity).ToList();
                if (group.Count == 0) continue;
                builder.Append(severity == IssueSeverity.Error ? "Errors" : "Warnings").Append(" (").Append(group.Count).Append("):\n");
                foreach (var issue in group)
                {
                    if (maxLines > 0 && shown >= maxLines)
                    {
                        hidden++;
                        continue;
                    }
                    builder.Append(issue).Append('\n');
                    shown++;
                }
            }
            if (hidden > 0) builder.Append("... ").Append(hidden).Append(" more lines not shown (cap ").Append(maxLines).Append(")\n");
            if (ascii && Grid != null)
            {
                builder.Append("Map (top row = highest y; # full, h half, o blocked, . open, A deploy, E enemy, ! unit on a blocker, - unreachable):\n");
                builder.Append(Grid.ToAscii(Deploy, Enemies.Select(e => e.Cell), Unreachable));
            }
            return builder.ToString().TrimEnd('\n');
        }
    }

    /// <summary>
    /// Grid-tier layout validators. Pure C#: they read a LayoutDocument (or a LayoutGrid
    /// baked from a scene) and the game's GridRules, and report cell-addressed issues.
    /// Structural: footprints on the board, one blocker per cell. Encounter: unit cells and
    /// facing. Reachability: what the squad can walk to. Consistency compares the layout with
    /// the game's own occupancy and sight colliders, which the Editor gathers.
    /// </summary>
    public static class LayoutValidator
    {
        /// <summary>Structural, encounter and reachability checks of a draft.</summary>
        public static LayoutReport Validate(LayoutDocument document, Func<LayoutPiece, PieceSpec?> resolve, GridRules rules)
        {
            var issues = Structural(document, resolve, rules, out var grid);
            var report = ForGrid(grid, document.Deploy, document.Enemies);
            report.Issues.InsertRange(0, issues);
            return report;
        }

        /// <summary>Encounter and reachability checks on an occupancy grid (e.g. the game's own bake of a scene).</summary>
        public static LayoutReport ForGrid(LayoutGrid grid, IReadOnlyList<Cell> deploy, IReadOnlyList<LayoutEnemySpawn> enemies)
        {
            var report = new LayoutReport { Grid = grid };
            report.Deploy.AddRange(deploy);
            report.Enemies.AddRange(enemies);
            report.Issues.AddRange(Encounter(grid, deploy, enemies));
            report.Issues.AddRange(Reachability(grid, deploy, enemies, report.Unreachable));
            return report;
        }

        /// <summary>
        /// Footprints resolve and stay on the board (OUT_OF_BOUNDS, UNKNOWN_PIECE), one blocker per
        /// cell (OVERLAP), decor anchored on the board (DECOR_OUT_OF_BOUNDS). Pieces are on whole
        /// cells by construction; a bake reports scene pieces that are not (OFFGRID_*).
        /// </summary>
        public static List<LayoutIssue> Structural(LayoutDocument document, Func<LayoutPiece, PieceSpec?> resolve, GridRules rules,
            out LayoutGrid grid)
        {
            var built = new List<LayoutIssue>();
            grid = LayoutGrid.Build(document, resolve, rules, built);
            // Build also checks deploy cells; the encounter check owns those.
            var issues = built.Where(i => !i.Code.StartsWith("DEPLOY_")).ToList();
            foreach (var decor in document.Decor)
                if (!grid.Contains(decor.Anchor))
                    issues.Add(new LayoutIssue("DECOR_OUT_OF_BOUNDS", decor.Anchor,
                        $"{decor.LocalId} is anchored off the {grid.Width}x{grid.Height} board", IssueSeverity.Warning));
            return issues;
        }

        /// <summary>
        /// Every deploy cell and spawn is on the board and open, no two units share a cell, and
        /// each enemy faces 0-359 degrees (a warning when it is not one of the eight grid directions).
        /// </summary>
        public static List<LayoutIssue> Encounter(LayoutGrid grid, IReadOnlyList<Cell> deploy, IReadOnlyList<LayoutEnemySpawn> enemies)
        {
            var issues = new List<LayoutIssue>();
            var units = new Dictionary<Cell, string>();
            void CheckCell(string prefix, Cell cell, string label)
            {
                if (!grid.Contains(cell))
                    issues.Add(new LayoutIssue(prefix + "_OUT_OF_BOUNDS", cell, $"{label} is off the {grid.Width}x{grid.Height} board"));
                else if (grid.IsBlocked(cell))
                    issues.Add(new LayoutIssue(prefix + "_BLOCKED", cell, $"{label} is under {grid[cell].Occupant}"));
                if (units.TryGetValue(cell, out var other))
                    issues.Add(new LayoutIssue("CELL_SHARED", cell, $"{label} starts on the same cell as {other}"));
                else
                    units[cell] = label;
            }

            for (var i = 0; i < deploy.Count; i++)
                CheckCell("DEPLOY", deploy[i], $"deploy #{i + 1}");
            foreach (var enemy in enemies)
            {
                CheckCell("ENEMY", enemy.Cell, $"enemy {enemy.LocalId}");
                var facing = enemy.Facing;
                if (float.IsNaN(facing) || facing < 0f || facing >= 360f)
                    issues.Add(new LayoutIssue("FACING_RANGE", enemy.Cell, $"enemy {enemy.LocalId} faces {LayoutJson.FormatNumber(facing)} degrees; use 0-359"));
                else if (Math.Abs(facing / 45f - Math.Round(facing / 45f)) > 1e-4)
                    issues.Add(new LayoutIssue("FACING_OFF_AXIS", enemy.Cell,
                        $"enemy {enemy.LocalId} faces {LayoutJson.FormatNumber(facing)} degrees, between the eight grid directions", IssueSeverity.Warning));
            }
            return issues;
        }

        /// <summary>
        /// Breadth-first search from the open deploy cells with the game's step rule
        /// (LayoutGrid.CanStep: 8-way, no corner cutting). Every enemy on an open cell must be
        /// reached (ENEMY_UNREACHABLE); each region of open cells no deploy cell reaches is one
        /// POCKET warning at its lowest cell. Unreached open cells are added to <paramref name="unreachable"/>.
        /// </summary>
        public static List<LayoutIssue> Reachability(LayoutGrid grid, IReadOnlyList<Cell> deploy, IReadOnlyList<LayoutEnemySpawn> enemies,
            HashSet<Cell> unreachable = null)
        {
            var issues = new List<LayoutIssue>();
            var starts = deploy.Where(c => grid.Contains(c) && !grid.IsBlocked(c)).Distinct().ToList();
            if (starts.Count == 0)
            {
                issues.Add(new LayoutIssue("NO_DEPLOY", null, "no deploy cell is on an open board cell; reachability not checked"));
                return issues;
            }

            var reached = Flood(grid, starts);
            foreach (var enemy in enemies)
                if (grid.Contains(enemy.Cell) && !grid.IsBlocked(enemy.Cell) && !reached.Contains(enemy.Cell))
                    issues.Add(new LayoutIssue("ENEMY_UNREACHABLE", enemy.Cell, $"enemy {enemy.LocalId} cannot be reached from the deploy cells"));

            foreach (var cell in grid.AllCells())
            {
                if (grid.IsBlocked(cell) || reached.Contains(cell)) continue;
                var pocket = Flood(grid, new[] { cell });
                reached.UnionWith(pocket);
                unreachable?.UnionWith(pocket);
                var held = enemies.Where(e => pocket.Contains(e.Cell)).Select(e => e.LocalId).ToList();
                issues.Add(new LayoutIssue("POCKET", cell,
                    $"{pocket.Count} open cell{(pocket.Count == 1 ? "" : "s")} unreachable from the deploy cells" +
                    (held.Count > 0 ? $" (holds {string.Join(", ", held)})" : ""), IssueSeverity.Warning));
            }
            return issues;
        }

        /// <summary>
        /// Physics tier, after apply: the game's occupancy must equal the layout's (every
        /// blocking cell has a collider: MISSING_BLOCK, EXTRA_BLOCK, COVER), and every sight
        /// collider on the board must belong to a blocker or is reported as SIGHT_NO_OBSTACLE.
        /// </summary>
        public static List<LayoutIssue> Consistency(LayoutGrid layout, LayoutGrid physics, IEnumerable<SightCollider> sight)
        {
            var issues = layout.DiffOccupancy(physics).Select(line => LayoutIssue.FromLine(line)).ToList();
            var sightOnly = sight
                .Where(s => !s.IsObstacle && s.Cells.Count > 0)
                .Select(s => (collider: s, first: s.Cells.OrderBy(c => c.Y).ThenBy(c => c.X).First()))
                .OrderBy(s => s.first.Y).ThenBy(s => s.first.X).ThenBy(s => s.collider.Name, StringComparer.Ordinal);
            foreach (var (collider, first) in sightOnly)
                issues.Add(new LayoutIssue("SIGHT_NO_OBSTACLE", first,
                    $"{collider.Name} blocks sight over {collider.Cells.Count} cell{(collider.Cells.Count == 1 ? "" : "s")} but not movement",
                    IssueSeverity.Warning));
            return issues;
        }

        private static HashSet<Cell> Flood(LayoutGrid grid, IEnumerable<Cell> starts)
        {
            var seen = new HashSet<Cell>(starts);
            var queue = new Queue<Cell>(seen);
            while (queue.Count > 0)
                foreach (var next in grid.Neighbours(queue.Dequeue()))
                    if (seen.Add(next))
                        queue.Enqueue(next);
            return seen;
        }
    }
}
