using System;
using System.Collections.Generic;
using System.Text;

namespace Gamenami.UnitySemanticBridge.Layout
{
    public struct CellState
    {
        public bool Blocked;
        public CoverLevel Cover;
        /// <summary>Local id (or scene name, for a bake) of the blocking piece.</summary>
        public string Occupant;
    }

    /// <summary>What sits on one side of a cell; stored on both cells it separates.</summary>
    public struct SideState
    {
        public SideKind Kind;
        public CoverLevel Cover;
        /// <summary>Local id (or scene name) of the walled piece.</summary>
        public string Owner;

        public bool Blocks => Kind == SideKind.Wall || Kind == SideKind.Window;
    }

    /// <summary>
    /// Derived per-cell state of a board: at most one blocking occupant per cell, which
    /// matches a one-obstacle-per-tile game rule. Pure C#; neighbours and cover follow
    /// the supplied GridRules.
    /// </summary>
    public sealed class LayoutGrid
    {
        private static readonly Cell[] Orthogonal = { new Cell(0, 1), new Cell(0, -1), new Cell(-1, 0), new Cell(1, 0) };
        private static readonly Cell[] Diagonal = { new Cell(1, 1), new Cell(1, -1), new Cell(-1, 1), new Cell(-1, -1) };

        private readonly CellState[,] _cells;
        // Per cell and side, in Orthogonal order: +y, -y, -x, +x.
        private readonly SideState[,,] _sides;
        // Walled pieces claim their frame without blocking it, so two buildings can't share cells.
        private readonly string[,] _claims;
        // World Y of each cell's floor (plan H1); null when the grid has none.
        private float[,] _floors;

        public int Width { get; }
        public int Height { get; }
        public GridRules Rules { get; }

        public LayoutGrid(int width, int height, GridRules rules)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Board size must be positive.");
            Width = width;
            Height = height;
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _cells = new CellState[width, height];
            _sides = new SideState[width, height, 4];
            _claims = new string[width, height];
        }

        /// <summary>True once any side has a wall, window or door.</summary>
        public bool HasWalls { get; private set; }

        private static int SideIndex(GridSide side) => side switch
        {
            GridSide.PlusY => 0,
            GridSide.MinusY => 1,
            GridSide.MinusX => 2,
            _ => 3,
        };

        private static readonly GridSide[] SideOrder = { GridSide.PlusY, GridSide.MinusY, GridSide.MinusX, GridSide.PlusX };

        private static GridSide Opposite(GridSide side) => GridSides.Rotate(side, 2);

        /// <summary>What sits on a side of a cell; Open for a cell off the board.</summary>
        public SideState SideAt(Cell cell, GridSide side) => Contains(cell) ? _sides[cell.X, cell.Y, SideIndex(side)] : default;

        public bool IsWalled(Cell cell, GridSide side) => SideAt(cell, side).Blocks;

        /// <summary>Sets a side on the cell and the matching side of its neighbour (either may be off the board).</summary>
        public void SetSide(Cell cell, GridSide side, SideKind kind, CoverLevel cover, string owner)
        {
            var state = kind == SideKind.Open ? default : new SideState { Kind = kind, Cover = cover, Owner = owner };
            var neighbour = cell + GridSides.Step(side);
            if (!Contains(cell) && !Contains(neighbour)) throw new ArgumentOutOfRangeException(nameof(cell), cell, "Side is outside the board.");
            if (Contains(cell)) _sides[cell.X, cell.Y, SideIndex(side)] = state;
            if (Contains(neighbour)) _sides[neighbour.X, neighbour.Y, SideIndex(Opposite(side))] = state;
            if (kind != SideKind.Open) HasWalls = true;
        }

        /// <summary>True once floor heights are set (SetFloors).</summary>
        public bool HasFloors => _floors != null;

        /// <summary>
        /// Per-cell floor heights, world Y, indexed [x, y] (IGridRulesAdapter.FloorHeights); null clears them. With
        /// GridRules.MaxStepHeight above 0, a step between neighbours whose floors differ by more is closed to movement.
        /// </summary>
        public void SetFloors(float[,] floors)
        {
            if (floors != null && (floors.GetLength(0) != Width || floors.GetLength(1) != Height))
                throw new ArgumentException($"Floor heights are {floors.GetLength(0)}x{floors.GetLength(1)}; the board is {Width}x{Height}.", nameof(floors));
            _floors = floors;
        }

        /// <summary>A cell's floor height (world Y); 0 without floor heights or off the board.</summary>
        public float FloorAt(Cell cell) => _floors != null && Contains(cell) ? _floors[cell.X, cell.Y] : 0f;

        /// <summary>The floor step across a side of a cell, meters (absolute); 0 without floors or towards off the board.</summary>
        public float StepAcross(Cell cell, GridSide side)
        {
            var neighbour = cell + GridSides.Step(side);
            if (_floors == null || !Contains(cell) || !Contains(neighbour)) return 0f;
            return Math.Abs(_floors[neighbour.X, neighbour.Y] - _floors[cell.X, cell.Y]);
        }

        /// <summary>Whether the step rule (GridRules.MaxStepHeight) closes a side of a cell to movement.</summary>
        public bool IsStepClosed(Cell cell, GridSide side) =>
            Rules.MaxStepHeight > 0f && StepAcross(cell, side) > Rules.MaxStepHeight + StepTolerance;

        /// <summary>Slack on the step rule, meters, so a measured 0.5004 m step is still 0.5 m.</summary>
        public const float StepTolerance = 0.005f;

        // A side movement may not cross: a wall or window, or a step that is too high.
        private bool IsMoveClosed(Cell cell, GridSide side) => IsWalled(cell, side) || IsStepClosed(cell, side);

        /// <summary>The walled piece whose frame covers a cell, or null.</summary>
        public string ClaimedBy(Cell cell) => Contains(cell) ? _claims[cell.X, cell.Y] : null;

        public void Claim(Cell cell, string owner)
        {
            if (!Contains(cell)) throw new ArgumentOutOfRangeException(nameof(cell), cell, "Cell is outside the board.");
            _claims[cell.X, cell.Y] = owner;
        }

        public bool Contains(Cell cell) => cell.X >= 0 && cell.Y >= 0 && cell.X < Width && cell.Y < Height;

        public CellState this[Cell cell] => _cells[cell.X, cell.Y];

        public bool IsBlocked(Cell cell) => Contains(cell) && _cells[cell.X, cell.Y].Blocked;

        /// <summary>Marks a cell blocked; replaces any previous occupant (callers report overlaps).</summary>
        public void Block(Cell cell, string occupant, CoverLevel cover)
        {
            if (!Contains(cell)) throw new ArgumentOutOfRangeException(nameof(cell), cell, "Cell is outside the board.");
            _cells[cell.X, cell.Y] = new CellState { Blocked = true, Cover = cover, Occupant = occupant };
        }

        public IEnumerable<Cell> AllCells()
        {
            for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++)
                    yield return new Cell(x, y);
        }

        public int BlockedCount
        {
            get
            {
                var count = 0;
                foreach (var state in _cells)
                    if (state.Blocked) count++;
                return count;
            }
        }

        /// <summary>
        /// Whether a single step between adjacent cells is legal: the target is on the board
        /// and open, a straight step does not cross a walled side, and a diagonal does not cut
        /// past a blocked orthogonal neighbour (when the rules forbid that) nor pass a corner
        /// where a walled side meets. A step too high for GridRules.MaxStepHeight counts as a wall
        /// (movement only). Only blockers and walls count; units are not part of the layout.
        /// </summary>
        public bool CanStep(Cell from, Cell to)
        {
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1 || (dx == 0 && dy == 0)) return false;
            if (!Contains(to) || IsBlocked(to)) return false;
            if (dx == 0 || dy == 0) return !IsMoveClosed(from, dx > 0 ? GridSide.PlusX : dx < 0 ? GridSide.MinusX : dy > 0 ? GridSide.PlusY : GridSide.MinusY);
            if (!Rules.DiagonalMoves) return false;
            // Walls seal at their corners whatever the corner-cutting rule says.
            var xSide = dx > 0 ? GridSide.PlusX : GridSide.MinusX;
            var ySide = dy > 0 ? GridSide.PlusY : GridSide.MinusY;
            var besideX = new Cell(from.X + dx, from.Y);
            var besideY = new Cell(from.X, from.Y + dy);
            if (IsMoveClosed(from, xSide) || IsMoveClosed(from, ySide) || IsMoveClosed(besideX, ySide) || IsMoveClosed(besideY, xSide)) return false;
            // A too-high step closes its corners like a wall; the diagonal's own rise counts too.
            if (Rules.MaxStepHeight > 0f && Math.Abs(FloorAt(to) - FloorAt(from)) > Rules.MaxStepHeight + StepTolerance) return false;
            if (!Rules.NoCornerCutting) return true;
            return !IsBlocked(besideX) && !IsBlocked(besideY);
        }

        public IEnumerable<Cell> Neighbours(Cell cell)
        {
            foreach (var offset in Orthogonal)
                if (CanStep(cell, cell + offset)) yield return cell + offset;
            if (!Rules.DiagonalMoves) yield break;
            foreach (var offset in Diagonal)
                if (CanStep(cell, cell + offset)) yield return cell + offset;
        }

        // ---- Grid levels (plan H2) ----

        // Upper-level cells and their floor heights (world Y); level 0 is the board above.
        private readonly Dictionary<LevelCell, float> _upper = new Dictionary<LevelCell, float>();
        // Ladders, both directions, and as declared.
        private readonly HashSet<(LevelCell, LevelCell)> _ladders = new HashSet<(LevelCell, LevelCell)>();
        private readonly List<(LevelCell a, LevelCell b)> _declaredLadders = new List<(LevelCell, LevelCell)>();

        /// <summary>Declared upper-level cells (level 1 and up).</summary>
        public IEnumerable<LevelCell> UpperCells => _upper.Keys;

        /// <summary>Ladders as declared, one direction each.</summary>
        public IReadOnlyList<(LevelCell a, LevelCell b)> Ladders => _declaredLadders;

        /// <summary>Declares a cell on an upper level (1 and up) with its floor height; its level's edge stays closed.</summary>
        public void AddUpperCell(LevelCell cell, float floorY)
        {
            if (cell.Level < 1) throw new ArgumentOutOfRangeException(nameof(cell), cell, "Upper levels start at 1.");
            if (!Contains(cell.Cell)) throw new ArgumentOutOfRangeException(nameof(cell), cell, "Cell is outside the board.");
            _upper[cell] = floorY;
        }

        /// <summary>Whether a level cell exists: every board cell on level 0, declared cells above.</summary>
        public bool Exists(LevelCell cell) => cell.Level == 0 ? Contains(cell.Cell) : _upper.ContainsKey(cell);

        /// <summary>A level cell's floor height (world Y); level 0 reads FloorAt.</summary>
        public float FloorAt(LevelCell cell) => cell.Level == 0 ? FloorAt(cell.Cell) : _upper.TryGetValue(cell, out var y) ? y : 0f;

        /// <summary>A ladder between two existing cells on different levels; it costs GridRules.LadderTiles.</summary>
        public void AddLadder(LevelCell a, LevelCell b)
        {
            if (a.Level == b.Level) throw new ArgumentException($"A ladder joins two levels; {a} and {b} are both on level {a.Level}.");
            if (!Exists(a) || !Exists(b)) throw new ArgumentException($"Ladder {a} - {b}: both ends must exist.");
            if (!_ladders.Add((a, b))) return;
            _ladders.Add((b, a));
            _declaredLadders.Add((a, b));
        }

        public bool IsLadder(LevelCell from, LevelCell to) => _ladders.Contains((from, to));

        /// <summary>
        /// A step between level cells: a ladder only with <paramref name="canUseLadder"/> (the moving unit's rule, D8), on the
        /// board the 2D CanStep, and on an upper level a step to a declared neighbour (a diagonal needs both cells beside
        /// it declared) within the step rule.
        /// </summary>
        public bool CanStep(LevelCell from, LevelCell to, bool canUseLadder = true)
        {
            if (IsLadder(from, to)) return canUseLadder;
            if (from.Level != to.Level) return false;
            if (from.Level == 0) return CanStep(from.Cell, to.Cell);
            var dx = to.Cell.X - from.Cell.X;
            var dy = to.Cell.Y - from.Cell.Y;
            if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1 || (dx == 0 && dy == 0) || !Exists(to)) return false;
            if (dx != 0 && dy != 0)
            {
                if (!Rules.DiagonalMoves) return false;
                var besideX = new LevelCell(from.Cell.X + dx, from.Cell.Y, from.Level);
                var besideY = new LevelCell(from.Cell.X, from.Cell.Y + dy, from.Level);
                if (!Exists(besideX) || !Exists(besideY) || TooHigh(from, besideX) || TooHigh(from, besideY) ||
                    TooHigh(besideX, to) || TooHigh(besideY, to)) return false;
            }
            return !TooHigh(from, to);
        }

        private bool TooHigh(LevelCell a, LevelCell b) =>
            Rules.MaxStepHeight > 0f && Math.Abs(FloorAt(a) - FloorAt(b)) > Rules.MaxStepHeight + StepTolerance;

        /// <summary>Neighbours a unit can step to on any level, with the tiles each step costs (1, or GridRules.LadderTiles).</summary>
        public IEnumerable<(LevelCell cell, int tiles)> Neighbours(LevelCell cell, bool canUseLadder = true)
        {
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var next = new LevelCell(cell.Cell.X + dx, cell.Cell.Y + dy, cell.Level);
                    if (CanStep(cell, next, canUseLadder)) yield return (next, 1);
                }
            foreach (var (a, b) in _ladders)
                if (a.Equals(cell) && canUseLadder) yield return (b, Math.Max(1, Rules.LadderTiles));
        }

        /// <summary>
        /// Entered tiles from the nearest of <paramref name="starts"/> to every level cell they connect to, ladders counted
        /// at GridRules.LadderTiles, up to <paramref name="maxSteps"/>. Unreached cells are absent.
        /// </summary>
        public Dictionary<LevelCell, int> StepsFrom(IEnumerable<LevelCell> starts, bool canUseLadder = true, int maxSteps = int.MaxValue)
        {
            var best = new Dictionary<LevelCell, int>();
            var buckets = new List<Queue<LevelCell>> { new Queue<LevelCell>() };
            foreach (var start in starts)
                if (Exists(start) && !(start.Level == 0 && IsBlocked(start.Cell)) && !best.ContainsKey(start))
                {
                    best[start] = 0;
                    buckets[0].Enqueue(start);
                }
            for (var steps = 0; steps < buckets.Count; steps++)
                while (buckets[steps].Count > 0)
                {
                    var cell = buckets[steps].Dequeue();
                    if (best[cell] != steps || steps >= maxSteps) continue;
                    foreach (var (next, tiles) in Neighbours(cell, canUseLadder))
                    {
                        var total = steps + tiles;
                        if (total > maxSteps || (best.TryGetValue(next, out var known) && known <= total)) continue;
                        best[next] = total;
                        while (buckets.Count <= total) buckets.Add(new Queue<LevelCell>());
                        buckets[total].Enqueue(next);
                    }
                }
            return best;
        }

        /// <summary>Cover a cell gets from its neighbour on one side; outside the board gives None.</summary>
        public CoverLevel CoverFrom(Cell neighbour) => IsBlocked(neighbour) ? this[neighbour].Cover : CoverLevel.None;

        /// <summary>Whether a cell blocks grid sight: it holds a Full-cover blocker.</summary>
        public bool BlocksSight(Cell cell) => IsBlocked(cell) && this[cell].Cover == CoverLevel.Full;

        /// <summary>Whether a side blocks grid sight: a wall or window there gives Full cover (windows are usually Half).</summary>
        public bool IsSideSightBlocked(Cell cell, GridSide side)
        {
            var state = SideAt(cell, side);
            return state.Blocks && state.Cover == CoverLevel.Full;
        }

        /// <summary>
        /// Grid sight (plan Phase S, D13): HasClearLine, or with Rules.StepOut (S5) a clear line between any viewpoint
        /// of each cell (Viewpoints). Symmetric; range is the caller's. Units are not part of the layout, so only
        /// blockers and walls refuse a step-out here.
        /// </summary>
        public bool CanSee(Cell from, Cell to)
        {
            if (HasClearLine(from, to)) return true;
            if (!Rules.StepOut) return false;
            var toViews = Viewpoints(to);
            foreach (var a in Viewpoints(from))
                foreach (var b in toViews)
                    if ((a != from || b != to) && HasClearLine(a, b)) return true;
            return false;
        }

        /// <summary>
        /// A cell and its step-out cells (plan S5, mirrors NK's GridMap.GetViewpoints). For each side with Full sight
        /// cover (a Full blocker beside it, or a Full wall on that side), each sideways cell is a step-out when it is on
        /// the board, not blocked, not walled off from the cell (wall or window), and has no Full cover on that same
        /// side itself, so only the end of a cover gives one. A blocked cell has none. Without Rules.StepOut, the cell alone.
        /// </summary>
        public List<Cell> Viewpoints(Cell cell)
        {
            var result = new List<Cell> { cell };
            if (!Rules.StepOut || !Contains(cell) || IsBlocked(cell)) return result;
            foreach (var side in new[] { GridSide.PlusY, GridSide.MinusY, GridSide.MinusX, GridSide.PlusX })
            {
                if (!HasFullSightCover(cell, side)) continue;
                var sideways = side == GridSide.PlusY || side == GridSide.MinusY
                    ? new[] { GridSide.MinusX, GridSide.PlusX } : new[] { GridSide.MinusY, GridSide.PlusY };
                foreach (var across in sideways)
                {
                    var step = cell + GridSides.Step(across);
                    if (!Contains(step) || IsBlocked(step) || IsWalled(cell, across) || HasFullSightCover(step, side) ||
                        result.Contains(step)) continue;
                    result.Add(step);
                }
            }
            return result;
        }

        private bool HasFullSightCover(Cell cell, GridSide side) =>
            IsSideSightBlocked(cell, side) || BlocksSight(cell + GridSides.Step(side));

        /// <summary>
        /// The grid sight line (plan Phase S, D13): the supercover line between the two cell centres meets no Full-cover
        /// cell (start and end excepted) and crosses no Full-cover wall. Where the line passes exactly through a
        /// cell corner it is blocked if either way round the corner is. Symmetric; no step-out (CanSee adds it).
        /// </summary>
        public bool HasClearLine(Cell from, Cell to)
        {
            int dx = to.X - from.X, dy = to.Y - from.Y;
            int nx = Math.Abs(dx), ny = Math.Abs(dy);
            int sx = dx > 0 ? 1 : -1, sy = dy > 0 ? 1 : -1;
            var xSide = sx > 0 ? GridSide.PlusX : GridSide.MinusX;
            var ySide = sy > 0 ? GridSide.PlusY : GridSide.MinusY;
            int x = from.X, y = from.Y, ix = 0, iy = 0;
            while (ix < nx || iy < ny)
            {
                var cell = new Cell(x, y);
                // Which boundary comes next: x at t = (ix + 1/2) / nx, y at t = (iy + 1/2) / ny.
                var compare = ix >= nx ? 1 : iy >= ny ? -1 : ((1 + 2 * ix) * ny).CompareTo((1 + 2 * iy) * nx);
                if (compare < 0)
                {
                    if (IsSideSightBlocked(cell, xSide)) return false;
                    x += sx; ix++;
                }
                else if (compare > 0)
                {
                    if (IsSideSightBlocked(cell, ySide)) return false;
                    y += sy; iy++;
                }
                else
                {
                    var besideX = new Cell(x + sx, y);
                    var besideY = new Cell(x, y + sy);
                    if (IsSideSightBlocked(cell, xSide) || IsSideSightBlocked(cell, ySide) || BlocksSight(besideX) || BlocksSight(besideY) ||
                        IsSideSightBlocked(besideX, ySide) || IsSideSightBlocked(besideY, xSide)) return false;
                    x += sx; y += sy; ix++; iy++;
                }
                if ((ix < nx || iy < ny) && BlocksSight(new Cell(x, y))) return false;
            }
            return true;
        }

        /// <summary>Cover on one side of a cell: the better of the neighbour's blocker and a wall or window on that side.</summary>
        public CoverLevel CoverOn(Cell cell, GridSide side)
        {
            var cover = CoverFrom(cell + GridSides.Step(side));
            var wall = SideAt(cell, side);
            return wall.Blocks && wall.Cover > cover ? wall.Cover : cover;
        }

        /// <summary>Per-side cover: forward (+y), back (-y), left (-x), right (+x).</summary>
        public (CoverLevel forward, CoverLevel back, CoverLevel left, CoverLevel right) CoverSides(Cell cell) =>
            (CoverOn(cell, GridSide.PlusY), CoverOn(cell, GridSide.MinusY), CoverOn(cell, GridSide.MinusX), CoverOn(cell, GridSide.PlusX));

        /// <summary>Cells whose blocked flag or cover differs, as "CODE x,y" lines.</summary>
        public List<string> DiffOccupancy(LayoutGrid actual)
        {
            var lines = new List<string>();
            if (actual.Width != Width || actual.Height != Height)
            {
                lines.Add($"SIZE expected {Width}x{Height}, got {actual.Width}x{actual.Height}");
                return lines;
            }
            foreach (var cell in AllCells())
            {
                var expected = this[cell];
                var got = actual[cell];
                if (expected.Blocked && !got.Blocked) lines.Add($"MISSING_BLOCK {cell} expected {expected.Occupant}");
                else if (!expected.Blocked && got.Blocked) lines.Add($"EXTRA_BLOCK {cell} {got.Occupant}");
                else if (expected.Blocked && expected.Cover != got.Cover)
                    lines.Add($"COVER {cell} expected {expected.Cover} from {expected.Occupant}, got {got.Cover} from {got.Occupant}");
            }
            if (!HasWalls && !actual.HasWalls) return lines;
            // Each side once: +x and +y of every cell, plus the board's left and bottom edges.
            foreach (var cell in AllCells())
                foreach (var side in SideOrder)
                {
                    if ((side == GridSide.MinusX && cell.X != 0) || (side == GridSide.MinusY && cell.Y != 0)) continue;
                    var expected = SideAt(cell, side);
                    var got = actual.SideAt(cell, side);
                    var name = GridSides.Name(side);
                    if (expected.Kind == got.Kind && (expected.Kind == SideKind.Open || expected.Cover == got.Cover)) continue;
                    if (got.Kind == SideKind.Open)
                        lines.Add($"WALL_MISSING {cell} {name} expected {expected.Kind} {expected.Cover} from {expected.Owner}");
                    else if (expected.Kind == SideKind.Open)
                        lines.Add($"WALL_EXTRA {cell} {name} {got.Kind} {got.Cover} from {got.Owner}");
                    else if (expected.Kind != got.Kind)
                        lines.Add($"WALL_KIND {cell} {name} expected {expected.Kind} from {expected.Owner}, got {got.Kind} from {got.Owner}");
                    else
                        lines.Add($"WALL_COVER {cell} {name} expected {expected.Cover} from {expected.Owner}, got {got.Cover} from {got.Owner}");
                }
            return lines;
        }

        /// <summary>
        /// ASCII map, top row = highest y: '#' full, 'h' half, 'o' blocked without cover, '.' open,
        /// 'A' deploy, 'E' enemy, '!' a unit on a blocked cell, '-' open but unreachable.
        /// </summary>
        public string ToAscii(IEnumerable<Cell> deploy = null, IEnumerable<Cell> enemies = null, IEnumerable<Cell> unreachable = null)
        {
            var deployCells = deploy == null ? new HashSet<Cell>() : new HashSet<Cell>(deploy);
            var enemyCells = enemies == null ? new HashSet<Cell>() : new HashSet<Cell>(enemies);
            var unreachableCells = unreachable == null ? new HashSet<Cell>() : new HashSet<Cell>(unreachable);
            char CellChar(Cell cell)
            {
                var state = this[cell];
                var unit = deployCells.Contains(cell) || enemyCells.Contains(cell);
                return state.Blocked
                    ? unit ? '!' : state.Cover == CoverLevel.Full ? '#' : state.Cover == CoverLevel.Half ? 'h' : 'o'
                    : deployCells.Contains(cell) ? 'A' : enemyCells.Contains(cell) ? 'E' : unreachableCells.Contains(cell) ? '-' : '.';
            }
            var builder = new StringBuilder();
            if (!HasWalls)
            {
                for (var y = Height - 1; y >= 0; y--)
                {
                    for (var x = 0; x < Width; x++) builder.Append(CellChar(new Cell(x, y)));
                    builder.Append('\n');
                }
                return builder.ToString();
            }
            // With walls: double resolution, the outline characters between cells ('-'/'|' wall, 'w' window,
            // 'D' door), '+' where a non-open side meets a corner, spaces elsewhere.
            for (var row = 2 * Height; row >= 0; row--)
            {
                for (var col = 0; col <= 2 * Width; col++)
                {
                    bool evenRow = row % 2 == 0, evenCol = col % 2 == 0;
                    int x = col / 2, y = row / 2;
                    if (!evenRow && !evenCol) builder.Append(CellChar(new Cell(x, y)));
                    else if (evenRow && !evenCol) builder.Append(WallOutline.SideChar(HorizontalSide(x, y).Kind, true));
                    else if (!evenRow) builder.Append(WallOutline.SideChar(VerticalSide(x, y).Kind, false));
                    else
                    {
                        var corner = HorizontalSide(x - 1, y).Kind != SideKind.Open || HorizontalSide(x, y).Kind != SideKind.Open ||
                                     VerticalSide(x, y - 1).Kind != SideKind.Open || VerticalSide(x, y).Kind != SideKind.Open;
                        builder.Append(corner ? '+' : ' ');
                    }
                }
                builder.Append('\n');
            }
            return builder.ToString();
        }

        // The side below cell (x, y) (its -y side), or above the top row.
        private SideState HorizontalSide(int x, int y) =>
            y < Height ? SideAt(new Cell(x, y), GridSide.MinusY) : SideAt(new Cell(x, y - 1), GridSide.PlusY);

        // The side left of cell (x, y) (its -x side), or right of the last column.
        private SideState VerticalSide(int x, int y) =>
            x < Width ? SideAt(new Cell(x, y), GridSide.MinusX) : SideAt(new Cell(x - 1, y), GridSide.PlusX);

        /// <summary>
        /// Derives the occupancy a layout should produce. Pieces whose spec cannot be resolved,
        /// that leave the board or that overlap an earlier piece are reported; the first piece
        /// keeps an overlapped cell. Deploy cells off the board or on a blocker are reported too.
        /// </summary>
        public static LayoutGrid Build(LayoutDocument document, Func<LayoutPiece, PieceSpec?> resolve, GridRules rules,
            List<LayoutIssue> issues)
        {
            var grid = new LayoutGrid(document.Width, document.Height, rules);
            foreach (var piece in document.Pieces)
            {
                var spec = resolve(piece);
                if (!spec.HasValue)
                {
                    issues.Add(new LayoutIssue("UNKNOWN_PIECE", piece.Anchor, $"{piece.LocalId}: no footprint for prefab {piece.PrefabGuid}"));
                    continue;
                }
                if (spec.Value.Outline != null && rules.SideWalls)
                {
                    BuildWalled(grid, piece, spec.Value, issues);
                    continue;
                }
                foreach (var cell in Footprint(piece, spec.Value))
                {
                    if (!grid.Contains(cell))
                    {
                        issues.Add(new LayoutIssue("OUT_OF_BOUNDS", cell, $"{piece.LocalId} ({spec.Value.RotatedSize(piece.Rotation)} at {piece.Anchor})"));
                        continue;
                    }
                    if (grid.IsBlocked(cell))
                    {
                        issues.Add(new LayoutIssue("OVERLAP", cell, $"{piece.LocalId} overlaps {grid[cell].Occupant}"));
                        continue;
                    }
                    grid.Block(cell, piece.LocalId, spec.Value.Cover);
                }
            }
            foreach (var cell in document.Deploy)
            {
                if (!grid.Contains(cell)) issues.Add(new LayoutIssue("DEPLOY_OUT_OF_BOUNDS", cell, "deploy cell is off the board"));
                else if (grid.IsBlocked(cell)) issues.Add(new LayoutIssue("DEPLOY_BLOCKED", cell, $"deploy cell is under {grid[cell].Occupant}"));
            }
            return grid;
        }

        // A walled piece claims its frame (cells stay walkable) and puts its outline's sides on the board.
        private static void BuildWalled(LayoutGrid grid, LayoutPiece piece, PieceSpec spec, List<LayoutIssue> issues)
        {
            var outline = spec.Outline.Rotate(piece.Rotation);
            var size = spec.RotatedSize(piece.Rotation);
            if (outline.Width != size.X || outline.Height != size.Y)
            {
                issues.Add(new LayoutIssue("OUTLINE_SIZE", piece.Anchor,
                    $"{piece.LocalId}: outline is {outline.Width}x{outline.Height}, footprint is {size.X}x{size.Y}"));
                return;
            }
            foreach (var cell in Footprint(piece, spec))
            {
                if (!grid.Contains(cell))
                {
                    issues.Add(new LayoutIssue("OUT_OF_BOUNDS", cell, $"{piece.LocalId} ({size} at {piece.Anchor})"));
                    continue;
                }
                var other = grid.ClaimedBy(cell);
                if (other != null)
                {
                    issues.Add(new LayoutIssue("OVERLAP", cell, $"{piece.LocalId} overlaps {other}"));
                    continue;
                }
                grid.Claim(cell, piece.LocalId);
            }
            foreach (var side in outline.Sides)
            {
                var cell = piece.Anchor + side.Cell;
                if (grid.Contains(cell) || grid.Contains(cell + GridSides.Step(side.Side)))
                    grid.SetSide(cell, side.Side, side.Kind, outline.CoverFor(side.Kind), piece.LocalId);
            }
            // Grid levels (plan H2): each upper level's cells at its height above the board floor, and the ladders.
            for (var i = 0; i < outline.Levels.Count; i++)
                foreach (var cell in outline.Levels[i].Cells())
                    if (grid.Contains(piece.Anchor + cell))
                        grid.AddUpperCell(new LevelCell(piece.Anchor + cell, i + 1), (float)outline.Levels[i].Height);
            foreach (var ladder in outline.Ladders)
            {
                var from = new LevelCell(piece.Anchor + ladder.FromCell, ladder.FromLevel);
                var to = new LevelCell(piece.Anchor + ladder.ToCell, ladder.ToLevel);
                if (grid.Exists(from) && grid.Exists(to)) grid.AddLadder(from, to);
                else issues.Add(new LayoutIssue("LADDER_OFF_BOARD", piece.Anchor, $"{piece.LocalId}: ladder {from} - {to} has an end off the board"));
            }
        }

        public static IEnumerable<Cell> Footprint(LayoutPiece piece, PieceSpec spec)
        {
            var size = spec.RotatedSize(piece.Rotation);
            for (var dx = 0; dx < size.X; dx++)
                for (var dy = 0; dy < size.Y; dy++)
                    yield return new Cell(piece.Anchor.X + dx, piece.Anchor.Y + dy);
        }
    }
}
