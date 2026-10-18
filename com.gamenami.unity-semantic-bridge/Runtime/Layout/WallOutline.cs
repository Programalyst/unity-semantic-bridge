using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Gamenami.UnitySemanticBridge.Layout
{
    /// <summary>What sits on one side of a cell (walls on cell sides, plan Phase W).</summary>
    public enum SideKind
    {
        Open,
        /// <summary>Blocks movement; cover from the outline's WallCover.</summary>
        Wall,
        /// <summary>Blocks movement, sight stays physics; cover from the outline's WindowCover.</summary>
        Window,
        /// <summary>Movement crosses it; no cover.</summary>
        Door,
    }

    /// <summary>One non-open side of an outline cell, in the outline's own frame.</summary>
    public readonly struct OutlineSide : IEquatable<OutlineSide>
    {
        public readonly Cell Cell;
        public readonly GridSide Side;
        public readonly SideKind Kind;

        public OutlineSide(Cell cell, GridSide side, SideKind kind)
        {
            Cell = cell;
            Side = side;
            Kind = kind;
        }

        public bool Equals(OutlineSide other) => Cell.Equals(other.Cell) && Side == other.Side && Kind == other.Kind;
        public override bool Equals(object obj) => obj is OutlineSide other && Equals(other);
        public override int GetHashCode() => (Cell.GetHashCode() * 397) ^ ((int)Side * 31) ^ (int)Kind;
        public override string ToString() => $"{Cell} {GridSides.Name(Side)} {Kind}";
    }

    /// <summary>Where units cross a door side: metres along the side from its midpoint (towards +x or +y).</summary>
    public readonly struct DoorPoint
    {
        public readonly Cell Cell;
        public readonly GridSide Side;
        public readonly double Offset;

        public DoorPoint(Cell cell, GridSide side, double offset)
        {
            Cell = cell;
            Side = side;
            Offset = offset;
        }
    }

    /// <summary>
    /// A building's walls on cell sides: a W x H frame of cells (its footprint, which stays walkable)
    /// and what sits on each side, at layout rotation 0. Written in the overrides file as an ASCII
    /// picture, top row = +y, 2H + 1 lines of 2W + 1 characters:
    /// even lines are horizontal sides ('+' at even columns, a side character at odd ones), odd
    /// lines are cell rows (a side character at even columns, the cell at odd ones, a space).
    /// Side characters: '-' or '|' wall, 'w' window, 'D' door, ' ' open. Pure C#.
    /// </summary>
    public sealed class WallOutline
    {
        public const double MaxDoorOffsetFraction = 0.45;

        public int Width { get; }
        public int Height { get; }
        /// <summary>Non-open sides, each once: outer sides from their frame cell, interior sides from the cell on their -x/-y side.</summary>
        public IReadOnlyList<OutlineSide> Sides { get; }
        public CoverLevel WallCover { get; }
        public CoverLevel WindowCover { get; }
        public IReadOnlyList<DoorPoint> DoorPoints { get; }
        /// <summary>Wall sides whose collision mesh lets sight through; apply adds a sight box.</summary>
        public IReadOnlyList<(Cell cell, GridSide side)> SightFill { get; }
        /// <summary>Cutaway height above the floor (meters); null for the game's default.</summary>
        public double? CutHeight { get; }
        /// <summary>Roofed cells outside the frame (e.g. a porch row), as inclusive rectangles in frame coordinates.</summary>
        public IReadOnlyList<(Cell min, Cell max)> Roofed { get; }

        public WallOutline(int width, int height, IEnumerable<OutlineSide> sides, CoverLevel wallCover = CoverLevel.Full,
            CoverLevel windowCover = CoverLevel.Half, IEnumerable<DoorPoint> doorPoints = null,
            IEnumerable<(Cell, GridSide)> sightFill = null, double? cutHeight = null, IEnumerable<(Cell, Cell)> roofed = null)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "An outline needs at least one cell.");
            Width = width;
            Height = height;
            var canonical = new Dictionary<(Cell, GridSide), OutlineSide>();
            foreach (var side in sides ?? Enumerable.Empty<OutlineSide>())
            {
                if (!Contains(side.Cell)) throw new FormatException($"side {side} is outside the {width}x{height} frame");
                if (side.Kind == SideKind.Open) continue;
                var key = Canonical(side.Cell, side.Side);
                if (canonical.TryGetValue(key, out var existing) && existing.Kind != side.Kind)
                    throw new FormatException($"side {side} conflicts with {existing}");
                canonical[key] = new OutlineSide(key.Item1, key.Item2, side.Kind);
            }
            Sides = canonical.Values.OrderBy(s => s.Cell.Y).ThenBy(s => s.Cell.X).ThenBy(s => (int)s.Side).ToList();
            WallCover = wallCover;
            WindowCover = windowCover;
            DoorPoints = (doorPoints ?? Enumerable.Empty<DoorPoint>()).ToList();
            foreach (var point in DoorPoints)
            {
                if (KindAt(point.Cell, point.Side) != SideKind.Door)
                    throw new FormatException($"door point on {point.Cell} {GridSides.Name(point.Side)}, which is not a door");
            }
            SightFill = (sightFill ?? Enumerable.Empty<(Cell, GridSide)>()).ToList();
            foreach (var (cell, side) in SightFill)
            {
                var kind = KindAt(cell, side);
                if (kind != SideKind.Wall && kind != SideKind.Window)
                    throw new FormatException($"sight fill on {cell} {GridSides.Name(side)}, which is not a wall or window");
            }
            CutHeight = cutHeight;
            Roofed = (roofed ?? Enumerable.Empty<(Cell, Cell)>()).ToList();
        }

        public bool Contains(Cell cell) => cell.X >= 0 && cell.Y >= 0 && cell.X < Width && cell.Y < Height;

        /// <summary>What sits on a side of a frame cell (outer sides included); Open when nothing is listed.</summary>
        public SideKind KindAt(Cell cell, GridSide side)
        {
            var key = Canonical(cell, side);
            foreach (var s in Sides)
                if (s.Cell.Equals(key.Item1) && s.Side == key.Item2) return s.Kind;
            return SideKind.Open;
        }

        public CoverLevel CoverFor(SideKind kind) =>
            kind == SideKind.Wall ? WallCover : kind == SideKind.Window ? WindowCover : CoverLevel.None;

        // Interior sides are stored from the cell on their -x/-y side; outer sides from their frame cell.
        private (Cell, GridSide) Canonical(Cell cell, GridSide side)
        {
            var neighbour = cell + GridSides.Step(side);
            if ((side == GridSide.MinusX || side == GridSide.MinusY) && Contains(neighbour))
                return (neighbour, side == GridSide.MinusX ? GridSide.PlusX : GridSide.PlusY);
            return (cell, side);
        }

        // ---- Rotation and placement ----

        /// <summary>The outline after clockwise layout quarter turns (odd turns swap width and height).</summary>
        public WallOutline Rotate(int quarterTurns)
        {
            var turns = ((quarterTurns % 4) + 4) % 4;
            if (turns == 0) return this;
            Cell Map(Cell c)
            {
                var x = c.X; var y = c.Y; int w = Width, h = Height;
                for (var i = 0; i < turns; i++)
                {
                    // Clockwise seen from above: +y becomes +x, so (x, y) -> (y, w - 1 - x) in a frame h wide.
                    var nx = y; var ny = w - 1 - x;
                    x = nx; y = ny;
                    var t = w; w = h; h = t;
                }
                return new Cell(x, y);
            }
            var odd = turns % 2 == 1;
            var sides = Sides.Select(s => new OutlineSide(Map(s.Cell), GridSides.Rotate(s.Side, turns), s.Kind));
            // A door point's offset runs towards +x or +y; a turn that maps +x to -y or +y to -x flips its sign.
            DoorPoint MapPoint(DoorPoint p)
            {
                var side = GridSides.Rotate(p.Side, turns);
                var alongBefore = p.Side == GridSide.PlusX || p.Side == GridSide.MinusX ? GridSide.PlusY : GridSide.PlusX;
                var alongAfter = GridSides.Rotate(alongBefore, turns);
                var sign = alongAfter == GridSide.PlusX || alongAfter == GridSide.PlusY ? 1 : -1;
                return new DoorPoint(Map(p.Cell), side, p.Offset * sign);
            }
            (Cell, Cell) MapRect((Cell min, Cell max) r)
            {
                var a = MapAny(r.min); var b = MapAny(r.max);
                return (new Cell(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Cell(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
            }
            // Roofed rectangles may lie outside the frame; the same affine map applies.
            Cell MapAny(Cell c)
            {
                var x = c.X; var y = c.Y; int w = Width, h = Height;
                for (var i = 0; i < turns; i++)
                {
                    var nx = y; var ny = w - 1 - x;
                    x = nx; y = ny;
                    var t = w; w = h; h = t;
                }
                return new Cell(x, y);
            }
            return new WallOutline(odd ? Height : Width, odd ? Width : Height, sides, WallCover, WindowCover,
                DoorPoints.Select(MapPoint), SightFill.Select(f => (Map(f.cell), GridSides.Rotate(f.side, turns))),
                CutHeight, Roofed.Select(MapRect));
        }

        /// <summary>The non-open sides on the board for a piece anchored (min corner) at <paramref name="anchor"/>.</summary>
        public IEnumerable<OutlineSide> Placed(Cell anchor, int rotation) =>
            Rotate(rotation).Sides.Select(s => new OutlineSide(anchor + s.Cell, s.Side, s.Kind));

        // ---- ASCII ----

        public static WallOutline FromLines(IReadOnlyList<string> lines, CoverLevel wallCover = CoverLevel.Full,
            CoverLevel windowCover = CoverLevel.Half, IEnumerable<DoorPoint> doorPoints = null,
            IEnumerable<(Cell, GridSide)> sightFill = null, double? cutHeight = null, IEnumerable<(Cell, Cell)> roofed = null)
        {
            if (lines == null || lines.Count < 3 || lines.Count % 2 == 0)
                throw new FormatException("an outline needs an odd number of lines (2H + 1), at least 3");
            var columns = lines[0].Length;
            if (columns < 3 || columns % 2 == 0) throw new FormatException("outline lines need an odd length (2W + 1), at least 3");
            if (lines.Any(l => l.Length != columns)) throw new FormatException($"every outline line must be {columns} characters");
            var width = (columns - 1) / 2;
            var height = (lines.Count - 1) / 2;
            var sides = new List<OutlineSide>();
            for (var row = 0; row < lines.Count; row++)
            {
                var line = lines[row];
                for (var col = 0; col < columns; col++)
                {
                    var c = line[col];
                    var evenRow = row % 2 == 0;
                    var evenCol = col % 2 == 0;
                    if (evenRow && evenCol)
                    {
                        if (c != '+') throw new FormatException($"line {row + 1} column {col + 1}: expected '+' at a corner, got '{c}'");
                        continue;
                    }
                    if (!evenRow && !evenCol)
                    {
                        if (c != ' ' && c != '.') throw new FormatException($"line {row + 1} column {col + 1}: a cell is ' ', got '{c}'");
                        continue;
                    }
                    var kind = ParseSide(c, evenRow, row, col);
                    if (kind == SideKind.Open) continue;
                    // Row 0 is the top edge (y = H); odd rows are cell rows from y = H - 1 down.
                    if (evenRow)
                    {
                        var x = (col - 1) / 2;
                        var boundaryY = height - row / 2; // cells below have y = boundaryY - 1
                        sides.Add(boundaryY == 0
                            ? new OutlineSide(new Cell(x, 0), GridSide.MinusY, kind)
                            : new OutlineSide(new Cell(x, boundaryY - 1), GridSide.PlusY, kind));
                    }
                    else
                    {
                        var y = height - 1 - (row - 1) / 2;
                        var boundaryX = col / 2; // cells to the right have x = boundaryX
                        sides.Add(boundaryX == width
                            ? new OutlineSide(new Cell(width - 1, y), GridSide.PlusX, kind)
                            : new OutlineSide(new Cell(boundaryX, y), GridSide.MinusX, kind));
                    }
                }
            }
            return new WallOutline(width, height, sides, wallCover, windowCover, doorPoints, sightFill, cutHeight, roofed);
        }

        private static SideKind ParseSide(char c, bool horizontal, int row, int col)
        {
            switch (c)
            {
                case ' ': return SideKind.Open;
                case 'w': return SideKind.Window;
                case 'D': return SideKind.Door;
                case '-' when horizontal: return SideKind.Wall;
                case '|' when !horizontal: return SideKind.Wall;
                default:
                    throw new FormatException($"line {row + 1} column {col + 1}: unknown side '{c}' ({(horizontal ? "'-'" : "'|'")} wall, 'w' window, 'D' door, ' ' open)");
            }
        }

        public static char SideChar(SideKind kind, bool horizontal) => kind switch
        {
            SideKind.Wall => horizontal ? '-' : '|',
            SideKind.Window => 'w',
            SideKind.Door => 'D',
            _ => ' ',
        };

        public string[] ToLines()
        {
            var lines = new string[2 * Height + 1];
            for (var row = 0; row < lines.Length; row++)
            {
                var sb = new StringBuilder();
                for (var col = 0; col < 2 * Width + 1; col++)
                {
                    var evenRow = row % 2 == 0;
                    var evenCol = col % 2 == 0;
                    if (evenRow && evenCol) sb.Append('+');
                    else if (!evenRow && !evenCol) sb.Append(' ');
                    else if (evenRow)
                    {
                        var x = (col - 1) / 2;
                        var boundaryY = Height - row / 2;
                        var kind = boundaryY == 0 ? KindAt(new Cell(x, 0), GridSide.MinusY) : KindAt(new Cell(x, boundaryY - 1), GridSide.PlusY);
                        sb.Append(SideChar(kind, true));
                    }
                    else
                    {
                        var y = Height - 1 - (row - 1) / 2;
                        var boundaryX = col / 2;
                        var kind = boundaryX == Width ? KindAt(new Cell(Width - 1, y), GridSide.PlusX) : KindAt(new Cell(boundaryX, y), GridSide.MinusX);
                        sb.Append(SideChar(kind, false));
                    }
                }
                lines[row] = sb.ToString();
            }
            return lines;
        }

        // ---- JSON (overrides entry "walls" and the card) ----

        private static readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal)
        {
            "outline", "wallCover", "windowCover", "doorPoints", "sightFill", "cutHeight", "roofed",
        };

        /// <summary>
        /// Reads {"outline": [lines], "wallCover"?, "windowCover"?, "doorPoints"?: [[x, y, side, offset]],
        /// "sightFill"?: [[x, y, side]], "cutHeight"?, "roofed"?: [[x0, y0, x1, y1]]}. Strict.
        /// </summary>
        public static WallOutline FromJson(JToken token)
        {
            if (!(token is JObject json)) throw new FormatException("walls must be an object with an \"outline\"");
            foreach (var property in json.Properties())
                if (!Keys.Contains(property.Name))
                    throw new FormatException($"walls has unknown key '{property.Name}' ({string.Join(", ", Keys)})");
            if (!(json["outline"] is JArray outline)) throw new FormatException("walls needs an \"outline\" array of strings");
            var lines = outline.Select(t => t.Type == JTokenType.String ? (string)t : throw new FormatException("outline lines must be strings")).ToList();
            var wallCover = json["wallCover"] == null ? CoverLevel.Full : LevelCards.ParseCover((string)json["wallCover"]);
            var windowCover = json["windowCover"] == null ? CoverLevel.Half : LevelCards.ParseCover((string)json["windowCover"]);
            var doorPoints = new List<DoorPoint>();
            if (json["doorPoints"] is JArray points)
                foreach (var p in points)
                {
                    if (!(p is JArray a) || a.Count != 4) throw new FormatException("each door point is [x, y, side, offset meters]");
                    var offset = (double)a[3];
                    if (double.IsNaN(offset) || double.IsInfinity(offset)) throw new FormatException("door point offset must be finite");
                    doorPoints.Add(new DoorPoint(new Cell((int)a[0], (int)a[1]), GridSides.Parse((string)a[2]), offset));
                }
            else if (json["doorPoints"] != null) throw new FormatException("doorPoints must be an array");
            var fill = new List<(Cell, GridSide)>();
            if (json["sightFill"] is JArray fills)
                foreach (var f in fills)
                {
                    if (!(f is JArray a) || a.Count != 3) throw new FormatException("each sight fill is [x, y, side]");
                    fill.Add((new Cell((int)a[0], (int)a[1]), GridSides.Parse((string)a[2])));
                }
            else if (json["sightFill"] != null) throw new FormatException("sightFill must be an array");
            double? cut = null;
            if (json["cutHeight"] != null)
            {
                cut = (double)json["cutHeight"];
                if (!(cut > 0)) throw new FormatException("cutHeight must be positive");
            }
            var roofed = new List<(Cell, Cell)>();
            if (json["roofed"] is JArray rects)
                foreach (var r in rects)
                {
                    if (!(r is JArray a) || a.Count != 4) throw new FormatException("each roofed rectangle is [x0, y0, x1, y1]");
                    int x0 = (int)a[0], y0 = (int)a[1], x1 = (int)a[2], y1 = (int)a[3];
                    if (x1 < x0 || y1 < y0) throw new FormatException("roofed rectangles go from min to max");
                    roofed.Add((new Cell(x0, y0), new Cell(x1, y1)));
                }
            else if (json["roofed"] != null) throw new FormatException("roofed must be an array");
            return FromLines(lines, wallCover, windowCover, doorPoints, fill, cut, roofed);
        }

        public JObject ToJson()
        {
            var json = new JObject { ["outline"] = new JArray(ToLines()) };
            if (WallCover != CoverLevel.Full) json["wallCover"] = WallCover.ToString();
            if (WindowCover != CoverLevel.Half) json["windowCover"] = WindowCover.ToString();
            if (DoorPoints.Count > 0)
                json["doorPoints"] = new JArray(DoorPoints.Select(p => new JArray(p.Cell.X, p.Cell.Y, GridSides.Name(p.Side), Math.Round(p.Offset, 3))));
            if (SightFill.Count > 0)
                json["sightFill"] = new JArray(SightFill.Select(f => new JArray(f.cell.X, f.cell.Y, GridSides.Name(f.side))));
            if (CutHeight.HasValue) json["cutHeight"] = Math.Round(CutHeight.Value, 3);
            if (Roofed.Count > 0)
                json["roofed"] = new JArray(Roofed.Select(r => new JArray(r.min.X, r.min.Y, r.max.X, r.max.Y)));
            return json;
        }

        /// <summary>Door point offsets beyond this fraction of the cell size leave the side (and the two cells).</summary>
        public IEnumerable<string> CheckDoorPoints(double cellSize)
        {
            foreach (var p in DoorPoints)
                if (Math.Abs(p.Offset) > cellSize * MaxDoorOffsetFraction)
                    yield return string.Format(CultureInfo.InvariantCulture, "door point on {0} {1} is {2:0.00} m from the side's midpoint (at most {3:0.00})",
                        p.Cell, GridSides.Name(p.Side), p.Offset, cellSize * MaxDoorOffsetFraction);
        }
    }
}
