using System;
using System.Collections.Generic;
using System.Globalization;
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

    /// <summary>
    /// A placed piece that has a level catalog card, as the scale and plausibility checks see it: where it
    /// sits, how it is placed, and the cover it gives. Apply and bake fill these from the scene.
    /// </summary>
    public sealed class PlacedCard
    {
        public string LocalId;
        public LevelCard Card;
        /// <summary>Min-corner cell of the footprint.</summary>
        public Cell Anchor;
        /// <summary>Footprint size in cells on the board (layout rotation applied).</summary>
        public Cell Size;
        /// <summary>Layout quarter turns (0-3).</summary>
        public int Rotation;
        /// <summary>Placed as a layout blocker (pieces), not decor.</summary>
        public bool Blocking;
        /// <summary>Cover the piece gives as placed (None for decor).</summary>
        public CoverLevel Cover;
        /// <summary>The instance's scale when it differs from the card's placementScale (a scene edit); null when it has the card's.</summary>
        public Vec3? Scale;

        public IEnumerable<Cell> Cells()
        {
            for (var y = 0; y < Size.Y; y++)
                for (var x = 0; x < Size.X; x++)
                    yield return new Cell(Anchor.X + x, Anchor.Y + y);
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
        public int NoteCount => Issues.Count(i => i.Severity == IssueSeverity.Note);
        public bool HasErrors => ErrorCount > 0;

        public IEnumerable<LayoutIssue> WithCode(string code) => Issues.Where(i => i.Code == code);

        /// <summary>
        /// Errors, then warnings, then notes, one "CODE x,y message" line each, at most <paramref name="maxLines"/>
        /// issue lines (0 = all); the ASCII map is appended on request.
        /// </summary>
        public string Format(int maxLines = DefaultMaxLines, bool ascii = false)
        {
            var builder = new StringBuilder();
            builder.Append("LAYOUT_REPORT ").Append(ErrorCount).Append(" errors, ").Append(WarningCount).Append(" warnings");
            if (NoteCount > 0) builder.Append(", ").Append(NoteCount).Append(" notes");
            builder.Append('\n');
            var shown = 0;
            var hidden = 0;
            foreach (var severity in new[] { IssueSeverity.Error, IssueSeverity.Warning, IssueSeverity.Note })
            {
                var group = Issues.Where(i => i.Severity == severity).ToList();
                if (group.Count == 0) continue;
                builder.Append(severity == IssueSeverity.Error ? "Errors" : severity == IssueSeverity.Warning ? "Warnings" : "Notes").Append(" (").Append(group.Count).Append("):\n");
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
            issues.AddRange(WallStructure(document, resolve, rules, grid));
            foreach (var decor in document.Decor)
                if (!grid.Contains(decor.Anchor))
                    issues.Add(new LayoutIssue("DECOR_OUT_OF_BOUNDS", decor.Anchor,
                        $"{decor.LocalId} is anchored off the {grid.Width}x{grid.Height} board", IssueSeverity.Warning));
            return issues;
        }

        /// <summary>
        /// Walls on cell sides: a solid piece may not straddle a wall (WALL_CROSSES_PIECE: the wall would cut
        /// through it and the game could never enter half of it), and a walled building's door points stay
        /// on their side, within the two cells it separates (DOOR_POINT_RANGE).
        /// </summary>
        public static List<LayoutIssue> WallStructure(LayoutDocument document, Func<LayoutPiece, PieceSpec?> resolve, GridRules rules,
            LayoutGrid grid)
        {
            var issues = new List<LayoutIssue>();
            if (!grid.HasWalls) return issues;
            foreach (var piece in document.Pieces)
            {
                var spec = resolve(piece);
                if (!spec.HasValue) continue;
                if (spec.Value.Outline != null)
                {
                    foreach (var problem in spec.Value.Outline.CheckDoorPoints(rules.CellSize))
                        issues.Add(new LayoutIssue("DOOR_POINT_RANGE", piece.Anchor, $"{piece.LocalId}: {problem}"));
                    continue;
                }
                var cells = new HashSet<Cell>(LayoutGrid.Footprint(piece, spec.Value));
                if (CrossedWall(grid, cells) is (Cell cell, GridSide side))
                    issues.Add(new LayoutIssue("WALL_CROSSES_PIECE", cell,
                        $"{piece.LocalId} straddles the wall of {grid.SideAt(cell, side).Owner} on its {GridSides.Name(side)} side; move it to one side of the wall"));
            }
            return issues;
        }

        // The first wall or window between two cells of one footprint, or null.
        private static (Cell, GridSide)? CrossedWall(LayoutGrid grid, HashSet<Cell> cells)
        {
            foreach (var cell in cells.OrderBy(c => c.Y).ThenBy(c => c.X))
                foreach (var side in new[] { GridSide.PlusX, GridSide.PlusY })
                    if (cells.Contains(cell + GridSides.Step(side)) && grid.IsWalled(cell, side))
                        return (cell, side);
            return null;
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
        /// collider on the board must belong to a blocker or is reported as SIGHT_NO_OBSTACLE (physics sight only:
        /// with GridRules.GridSight there are no sight colliders to pass).
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

        /// <summary>
        /// Scale: a piece with a card has its card's placementScale (OFFGRID_SCALE, with its height at both
        /// scales), and a blocker giving Half or Full cover is as tall as the game's band for that cover
        /// (HEIGHT_BAND: the card's height, lift included, with a suggested Y scale). Movement-only blockers
        /// (cover None) may be any height. A piece whose role and scale are both owner-approved overrides
        /// is reviewed truth: outside its band it is a note, not an error.
        /// </summary>
        public static List<LayoutIssue> Scale(IEnumerable<PlacedCard> pieces, CoverHeightBands bands)
        {
            bands ??= CoverHeightBands.Default;
            var issues = new List<LayoutIssue>();
            foreach (var piece in pieces)
            {
                var card = piece.Card;
                if (card == null) continue;
                var top = card.Height - card.Lift;
                if (piece.Scale.HasValue && !SameScale(piece.Scale.Value, card.PlacementScale))
                {
                    var here = top * Ratio(piece.Scale.Value.Y, card.PlacementScale.Y) + card.Lift;
                    var message = $"{piece.LocalId}: scale {piece.Scale.Value} differs from its card's placementScale {card.PlacementScale}: " +
                                  $"{M(here)} m tall here, {M(card.Height)} m at the card's scale";
                    if (piece.Blocking && !bands.Fits(piece.Cover, here))
                        message += $" (outside the {CoverName(piece.Cover)} band {bands.Describe(piece.Cover)})";
                    issues.Add(new LayoutIssue("OFFGRID_SCALE", piece.Anchor,
                        message + "; a layout places it at the card's scale, so reapply, or make this scale its placementScale in the overrides file"));
                }
                if (!piece.Blocking || bands.Fits(piece.Cover, card.Height)) continue;

                var reviewed = card.RoleSource == "override" && card.ScaleSource == "override";
                var text = $"{piece.LocalId}: {M(card.Height)} m tall gives {piece.Cover} cover, outside the {CoverName(piece.Cover)} band " +
                           $"{bands.Describe(piece.Cover)}";
                var target = bands.Nearest(piece.Cover, card.Height);
                if (top > 0 && target > card.Lift)
                {
                    // Three decimals, rounded towards the band so the suggestion lands inside it.
                    var exact = card.PlacementScale.Y * (target - card.Lift) / top * 1000;
                    var suggested = (target < card.Height ? Math.Floor(exact + 1e-6) : Math.Ceiling(exact - 1e-6)) / 1000;
                    var reached = top * Ratio(suggested, card.PlacementScale.Y) + card.Lift;
                    text += $"; Y scale {suggested.ToString("0.###", CultureInfo.InvariantCulture)} (now {card.PlacementScale.Y.ToString("0.###", CultureInfo.InvariantCulture)}) makes it {M(reached)} m";
                }
                else
                {
                    text += "; lower its lift";
                }
                var other = piece.Cover == CoverLevel.Half ? CoverLevel.Full : CoverLevel.Half;
                if (bands.Fits(other, card.Height)) text += $", or give it {other} cover";
                if (reviewed) text += " (owner-approved role and scale; the height is a bounding-box top, which thin parts such as posts can overstate)";
                issues.Add(new LayoutIssue("HEIGHT_BAND", piece.Anchor, text, reviewed ? IssueSeverity.Note : IssueSeverity.Error));
            }
            return issues;
        }

        /// <summary>
        /// Plausibility, from cards and the layout only; warnings, since judgement is involved:
        /// ROLE_LAYER (a ground or decor piece placed as a blocker, or a blocking role placed as decor);
        /// GROUND_RUN (a 4-connected run of ground cells, e.g. road and sidewalk tiles, that reaches no board
        /// edge, so its free edges show the ground lift step) and GROUND_OVERLAP (two ground pieces on one cell
        /// z-fight); BUILDING_FRONT (a building's front faces the board edge or only blocked cells). A
        /// building without a known front is a BUILDING_NO_FRONT note.
        /// </summary>
        public static List<LayoutIssue> Plausibility(LayoutGrid grid, IEnumerable<PlacedCard> pieces)
        {
            var issues = new List<LayoutIssue>();
            var placed = pieces.Where(p => p.Card != null).ToList();
            foreach (var piece in placed)
            {
                var role = piece.Card.Role;
                if (piece.Blocking && (role == LevelRole.Ground || role == LevelRole.Decor))
                    issues.Add(new LayoutIssue("ROLE_LAYER", piece.Anchor,
                        $"{piece.LocalId}: a {LevelCards.RoleName(role)} piece placed as a blocker; put it in decor", IssueSeverity.Warning));
                else if (!piece.Blocking && LevelCards.IsBlocking(role))
                    issues.Add(new LayoutIssue("ROLE_LAYER", piece.Anchor,
                        $"{piece.LocalId}: a {LevelCards.RoleName(role)} piece placed as decor never blocks; put it in pieces", IssueSeverity.Warning));
            }
            issues.AddRange(GroundRuns(grid, placed.Where(p => p.Card.Role == LevelRole.Ground).ToList()));
            foreach (var building in placed.Where(p => p.Card.Role == LevelRole.Building))
            {
                // A walled building is entered through its doors, so its doors matter, not its model's front.
                if (building.Blocking && building.Card.Walls != null && grid.Rules.SideWalls && grid.HasWalls)
                    issues.AddRange(Doors(grid, building));
                else if (BuildingFront(grid, building) is LayoutIssue issue)
                    issues.Add(issue);
            }
            return issues;
        }

        private static IEnumerable<LayoutIssue> GroundRuns(LayoutGrid grid, List<PlacedCard> ground)
        {
            var owners = new Dictionary<Cell, List<string>>();
            var reported = new HashSet<(string, string)>();
            var edgeTouching = new HashSet<string>();
            foreach (var piece in ground)
                foreach (var cell in piece.Cells())
                {
                    if (!grid.Contains(cell))
                    {
                        edgeTouching.Add(piece.LocalId); // reaches past the edge
                        continue;
                    }
                    if (!owners.TryGetValue(cell, out var here)) owners[cell] = here = new List<string>();
                    foreach (var other in here)
                        if (other != piece.LocalId && reported.Add((other, piece.LocalId)))
                            yield return new LayoutIssue("GROUND_OVERLAP", cell,
                                $"{piece.LocalId} overlaps ground piece {other}: both sit at the same ground lift and z-fight", IssueSeverity.Warning);
                    if (!here.Contains(piece.LocalId)) here.Add(piece.LocalId);
                }

            var seen = new HashSet<Cell>();
            foreach (var start in owners.Keys.OrderBy(c => c.Y).ThenBy(c => c.X))
            {
                if (!seen.Add(start)) continue;
                var run = new List<Cell> { start };
                var queue = new Queue<Cell>(run);
                while (queue.Count > 0)
                {
                    var cell = queue.Dequeue();
                    foreach (var step in new[] { new Cell(1, 0), new Cell(-1, 0), new Cell(0, 1), new Cell(0, -1) })
                    {
                        var next = cell + step;
                        if (owners.ContainsKey(next) && seen.Add(next))
                        {
                            run.Add(next);
                            queue.Enqueue(next);
                        }
                    }
                }
                var ids = run.SelectMany(c => owners[c]).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToList();
                var atEdge = run.Any(c => c.X == 0 || c.Y == 0 || c.X == grid.Width - 1 || c.Y == grid.Height - 1) || ids.Any(edgeTouching.Contains);
                if (atEdge) continue;
                yield return new LayoutIssue("GROUND_RUN", run.OrderBy(c => c.Y).ThenBy(c => c.X).First(),
                    $"ground run of {run.Count} cell{(run.Count == 1 ? "" : "s")} ({string.Join(", ", ids)}) reaches no board edge, so its free edges show the ground lift step; " +
                    "extend it to the board edge", IssueSeverity.Warning);
            }
        }

        private static LayoutIssue? BuildingFront(LayoutGrid grid, PlacedCard building)
        {
            if (!building.Card.Front.HasValue)
                return new LayoutIssue("BUILDING_NO_FRONT", building.Anchor,
                    $"{building.LocalId}: no front known (its root is tilted); add \"front\" to its overrides entry to have it checked", IssueSeverity.Note);
            var side = GridSides.Rotate(building.Card.Front.Value, building.Rotation);
            var cells = FrontCells(building, side).Where(grid.Contains).ToList();
            var name = GridSides.Name(side);
            if (cells.Count == 0)
                return new LayoutIssue("BUILDING_FRONT", building.Anchor,
                    $"{building.LocalId}: its front ({name}) faces the board edge; turn it towards the board", IssueSeverity.Warning);
            if (cells.All(grid.IsBlocked))
            {
                var blockers = cells.Select(c => grid[c].Occupant).Distinct().OrderBy(id => id, StringComparer.Ordinal);
                return new LayoutIssue("BUILDING_FRONT", building.Anchor,
                    $"{building.LocalId}: its front ({name}) faces only blocked cells ({string.Join(", ", blockers)}); leave open or road cells before it",
                    IssueSeverity.Warning);
            }
            return null;
        }

        // A walled building's outer doors: each must open onto an open board cell (DOOR_OFF_BOARD,
        // DOOR_BLOCKED), and at least one must (BUILDING_SEALED: nothing can get in or out).
        private static IEnumerable<LayoutIssue> Doors(LayoutGrid grid, PlacedCard building)
        {
            var outline = building.Card.Walls.Rotate(building.Rotation);
            var doors = outline.Sides.Where(s => s.Kind == SideKind.Door && !outline.Contains(s.Cell + GridSides.Step(s.Side))).ToList();
            var usable = 0;
            foreach (var door in doors)
            {
                var inside = building.Anchor + door.Cell;
                var outside = inside + GridSides.Step(door.Side);
                var where = $"{building.LocalId}: the door on {GridSides.Name(door.Side)} of {inside}";
                if (!grid.Contains(outside))
                    yield return new LayoutIssue("DOOR_OFF_BOARD", inside, $"{where} opens off the board; turn or move the building", IssueSeverity.Warning);
                else if (grid.IsBlocked(outside))
                    yield return new LayoutIssue("DOOR_BLOCKED", inside, $"{where} opens onto {grid[outside].Occupant} at {outside}; keep that cell open", IssueSeverity.Warning);
                else
                    usable++;
            }
            if (usable == 0)
                yield return new LayoutIssue("BUILDING_SEALED", building.Anchor,
                    $"{building.LocalId}: none of its {doors.Count} door{(doors.Count == 1 ? "" : "s")} opens onto an open cell, so nobody can get in or out",
                    IssueSeverity.Warning);
        }

        // The row of cells just outside a footprint on one side.
        private static IEnumerable<Cell> FrontCells(PlacedCard piece, GridSide side)
        {
            var a = piece.Anchor;
            var s = piece.Size;
            switch (side)
            {
                case GridSide.PlusX: for (var y = 0; y < s.Y; y++) yield return new Cell(a.X + s.X, a.Y + y); break;
                case GridSide.MinusX: for (var y = 0; y < s.Y; y++) yield return new Cell(a.X - 1, a.Y + y); break;
                case GridSide.PlusY: for (var x = 0; x < s.X; x++) yield return new Cell(a.X + x, a.Y + s.Y); break;
                default: for (var x = 0; x < s.X; x++) yield return new Cell(a.X + x, a.Y - 1); break;
            }
        }

        private static bool SameScale(Vec3 a, Vec3 b) =>
            Math.Abs(a.X - b.X) <= 1e-3 * Math.Max(1, Math.Abs(b.X)) && Math.Abs(a.Y - b.Y) <= 1e-3 * Math.Max(1, Math.Abs(b.Y)) &&
            Math.Abs(a.Z - b.Z) <= 1e-3 * Math.Max(1, Math.Abs(b.Z));

        private static double Ratio(double value, double of) => of == 0 ? 1 : value / of;

        private static string CoverName(CoverLevel cover) => cover == CoverLevel.Half ? "half-cover" : cover == CoverLevel.Full ? "full-cover" : "no-cover";

        private static string M(double meters) => CoverHeightBands.Meters(meters);

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
