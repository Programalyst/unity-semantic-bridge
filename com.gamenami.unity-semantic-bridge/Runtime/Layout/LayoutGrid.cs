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
        /// and open, and a diagonal does not cut past a blocked orthogonal neighbour when the
        /// rules forbid that. Only blockers count; units are not part of the layout.
        /// </summary>
        public bool CanStep(Cell from, Cell to)
        {
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1 || (dx == 0 && dy == 0)) return false;
            if (!Contains(to) || IsBlocked(to)) return false;
            if (dx == 0 || dy == 0) return true;
            if (!Rules.DiagonalMoves) return false;
            if (!Rules.NoCornerCutting) return true;
            return !IsBlocked(new Cell(from.X + dx, from.Y)) && !IsBlocked(new Cell(from.X, from.Y + dy));
        }

        public IEnumerable<Cell> Neighbours(Cell cell)
        {
            foreach (var offset in Orthogonal)
                if (CanStep(cell, cell + offset)) yield return cell + offset;
            if (!Rules.DiagonalMoves) yield break;
            foreach (var offset in Diagonal)
                if (CanStep(cell, cell + offset)) yield return cell + offset;
        }

        /// <summary>Cover a cell gets from its neighbour on one side; outside the board gives None.</summary>
        public CoverLevel CoverFrom(Cell neighbour) => IsBlocked(neighbour) ? this[neighbour].Cover : CoverLevel.None;

        /// <summary>Per-side cover: forward (+y), back (-y), left (-x), right (+x).</summary>
        public (CoverLevel forward, CoverLevel back, CoverLevel left, CoverLevel right) CoverSides(Cell cell) =>
            (CoverFrom(cell + Orthogonal[0]), CoverFrom(cell + Orthogonal[1]), CoverFrom(cell + Orthogonal[2]), CoverFrom(cell + Orthogonal[3]));

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
            return lines;
        }

        /// <summary>ASCII map, top row = highest y: '#' full, 'h' half, 'o' blocked without cover, '.' open, 'A' deploy.</summary>
        public string ToAscii(IEnumerable<Cell> deploy = null)
        {
            var deployCells = deploy == null ? new HashSet<Cell>() : new HashSet<Cell>(deploy);
            var builder = new StringBuilder();
            for (var y = Height - 1; y >= 0; y--)
            {
                for (var x = 0; x < Width; x++)
                {
                    var cell = new Cell(x, y);
                    var state = this[cell];
                    builder.Append(state.Blocked
                        ? state.Cover == CoverLevel.Full ? '#' : state.Cover == CoverLevel.Half ? 'h' : 'o'
                        : deployCells.Contains(cell) ? 'A' : '.');
                }
                builder.Append('\n');
            }
            return builder.ToString();
        }

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

        public static IEnumerable<Cell> Footprint(LayoutPiece piece, PieceSpec spec)
        {
            var size = spec.RotatedSize(piece.Rotation);
            for (var dx = 0; dx < size.X; dx++)
                for (var dy = 0; dy < size.Y; dy++)
                    yield return new Cell(piece.Anchor.X + dx, piece.Anchor.Y + dy);
        }
    }
}
