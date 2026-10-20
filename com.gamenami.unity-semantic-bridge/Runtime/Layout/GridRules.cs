using System;
using System.Globalization;

namespace Gamenami.UnitySemanticBridge.Layout
{
    /// <summary>
    /// The movement and cover rules a game's grid uses, as plain data so the pure
    /// model can apply them. A game adapter (IGridRulesAdapter) supplies one.
    /// </summary>
    public sealed class GridRules
    {
        /// <summary>Cell edge length in meters.</summary>
        public float CellSize = 1f;
        /// <summary>8-way movement when true, 4-way otherwise.</summary>
        public bool DiagonalMoves = true;
        /// <summary>A diagonal step is illegal when either orthogonal neighbour it passes is blocked.</summary>
        public bool NoCornerCutting = true;
        /// <summary>A cell takes cover from blockers on its four orthogonal neighbours (the only rule supported so far).</summary>
        public bool OrthogonalCover = true;
        /// <summary>
        /// Walls on cell sides (plan Phase W): a walled piece (card outline) keeps its footprint walkable and puts its
        /// walls on cell sides, which refuse straight steps across them and diagonals past a corner they meet, and give
        /// cover to both cells. False builds walled pieces as solid blockers.
        /// </summary>
        public bool SideWalls;
        /// <summary>
        /// Grid sight (plan Phase S, D13): the game decides sight and line of fire with LayoutGrid.CanSee (a
        /// supercover line between cell centres, blocked by Full-cover cells and walls) rather than physics rays.
        /// </summary>
        public bool GridSight;
        /// <summary>
        /// Step-out (plan S5, XCOM): with GridSight, a cell with Full cover on a side may also look from the sideways
        /// cell where that cover ends (LayoutGrid.Viewpoints), and CanSee is true when any viewpoint pair has a clear line.
        /// </summary>
        public bool StepOut;
        /// <summary>Target heights per cover class (catalog role detection and the scale validator).</summary>
        public CoverHeightBands CoverHeights = new CoverHeightBands();
        /// <summary>Numbers and targets for the level metrics (plan Phase 6, LayoutMetrics).</summary>
        public MetricSettings Metrics = new MetricSettings();
    }

    /// <summary>
    /// The heights a game wants per cover class, meters from the floor to the top of a piece as placed
    /// (a catalog card's height, lift included). Half cover is HalfMin to HalfMax, full cover FullMin or
    /// more; nothing should sit in between. Movement-only blockers (cover None) may be any height. The
    /// defaults (half below 1.2 m, full from 2 m, no lower limit) are USB's own until a game sets its own.
    /// </summary>
    public sealed class CoverHeightBands
    {
        /// <summary>Lowest height that still counts as half cover.</summary>
        public double HalfMin;
        /// <summary>Highest half-cover height; catalog role detection calls anything lower half cover.</summary>
        public double HalfMax = 1.2;
        /// <summary>Lowest full-cover height; catalog role detection calls anything this tall or taller full cover.</summary>
        public double FullMin = 2.0;

        /// <summary>Heights are compared with this slack, meters, so a measured 1.2004 m is still 1.2 m.</summary>
        public const double Tolerance = 0.005;

        public static readonly CoverHeightBands Default = new CoverHeightBands();

        /// <summary>Whether a piece of this height fits the band of the cover it gives (None and others always fit).</summary>
        public bool Fits(CoverLevel cover, double height)
        {
            switch (cover)
            {
                case CoverLevel.Half: return height >= HalfMin - Tolerance && height <= HalfMax + Tolerance;
                case CoverLevel.Full: return height >= FullMin - Tolerance;
                default: return true;
            }
        }

        /// <summary>"0.90-1.20 m" for Half, "1.60 m or more" for Full.</summary>
        public string Describe(CoverLevel cover) => cover == CoverLevel.Half
            ? (HalfMin > 0 ? $"{Meters(HalfMin)}-{Meters(HalfMax)} m" : $"up to {Meters(HalfMax)} m")
            : cover == CoverLevel.Full ? $"{Meters(FullMin)} m or more" : "any height";

        /// <summary>The nearest height inside the band of <paramref name="cover"/>.</summary>
        public double Nearest(CoverLevel cover, double height) => cover == CoverLevel.Half
            ? Math.Min(Math.Max(height, HalfMin), HalfMax)
            : cover == CoverLevel.Full ? Math.Max(height, FullMin) : height;

        internal static string Meters(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Footprint and cover of a blocking piece at rotation 0: Width cells along +x,
    /// Depth cells along +y. Comes from measurement now, from catalog cards later.
    /// </summary>
    public readonly struct PieceSpec
    {
        public readonly int Width;
        public readonly int Depth;
        public readonly CoverLevel Cover;
        /// <summary>Walls on cell sides at rotation 0 (a walled building); null for a solid piece.</summary>
        public readonly WallOutline Outline;

        public PieceSpec(int width, int depth, CoverLevel cover, WallOutline outline = null)
        {
            Width = width;
            Depth = depth;
            Cover = cover;
            Outline = outline;
        }

        /// <summary>Footprint size on the board after quarter turns (odd turns swap the axes).</summary>
        public Cell RotatedSize(int rotation) => rotation % 2 == 0 ? new Cell(Width, Depth) : new Cell(Depth, Width);

        public override string ToString() => $"{Width}x{Depth} {Cover}";
    }

    public enum IssueSeverity
    {
        /// <summary>The level is broken or will not match the layout.</summary>
        Error,
        /// <summary>Allowed, but worth a look (e.g. a sealed pocket, scenery that blocks sight only).</summary>
        Warning,
        /// <summary>For the record only: reviewed content outside a rule (e.g. an owner-approved piece outside its height band).</summary>
        Note,
    }

    /// <summary>A cell-addressed report line: "CODE x,y message".</summary>
    public readonly struct LayoutIssue
    {
        public readonly string Code;
        public readonly Cell? Cell;
        public readonly string Message;
        public readonly IssueSeverity Severity;

        public LayoutIssue(string code, Cell? cell, string message, IssueSeverity severity = IssueSeverity.Error)
        {
            Code = code;
            Cell = cell;
            Message = message;
            Severity = severity;
        }

        /// <summary>Wraps a report line produced elsewhere ("CODE rest"); it prints unchanged.</summary>
        public static LayoutIssue FromLine(string line, IssueSeverity severity = IssueSeverity.Error)
        {
            var space = line.IndexOf(' ');
            return space < 0
                ? new LayoutIssue(line, null, "", severity)
                : new LayoutIssue(line.Substring(0, space), null, line.Substring(space + 1), severity);
        }

        public override string ToString() =>
            Cell.HasValue ? $"{Code} {Cell.Value} {Message}" : string.IsNullOrEmpty(Message) ? Code : $"{Code} {Message}";
    }
}
