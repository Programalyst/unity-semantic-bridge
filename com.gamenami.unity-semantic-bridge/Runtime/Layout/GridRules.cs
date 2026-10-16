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

        public PieceSpec(int width, int depth, CoverLevel cover)
        {
            Width = width;
            Depth = depth;
            Cover = cover;
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
