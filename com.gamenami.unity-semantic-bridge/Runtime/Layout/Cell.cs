using System;

namespace Gamenami.UnitySemanticBridge.Layout
{
    /// <summary>
    /// A board cell. Pure value type (no UnityEngine) so the layout model and its
    /// rules run outside the Editor and in NUnit without a scene.
    /// </summary>
    public readonly struct Cell : IEquatable<Cell>
    {
        public readonly int X;
        public readonly int Y;

        public Cell(int x, int y)
        {
            X = x;
            Y = y;
        }

        public static Cell operator +(Cell a, Cell b) => new Cell(a.X + b.X, a.Y + b.Y);
        public static bool operator ==(Cell a, Cell b) => a.Equals(b);
        public static bool operator !=(Cell a, Cell b) => !a.Equals(b);

        public bool Equals(Cell other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is Cell other && Equals(other);
        public override int GetHashCode() => unchecked(X * 397 ^ Y);

        // Reports address cells as "x,y".
        public override string ToString() => $"{X},{Y}";
    }

    /// <summary>
    /// A cell on a grid level (plan H2): level 0 is the board, upper levels (a roof) exist only where a building
    /// declares them. Reports address it as "x,y" on level 0 and "x,y@level" above.
    /// </summary>
    public readonly struct LevelCell : IEquatable<LevelCell>
    {
        public readonly Cell Cell;
        public readonly int Level;

        public LevelCell(Cell cell, int level = 0)
        {
            Cell = cell;
            Level = level;
        }

        public LevelCell(int x, int y, int level = 0) : this(new Cell(x, y), level) { }

        public bool Equals(LevelCell other) => Cell.Equals(other.Cell) && Level == other.Level;
        public override bool Equals(object obj) => obj is LevelCell other && Equals(other);
        public override int GetHashCode() => unchecked(Cell.GetHashCode() * 31 + Level);
        public override string ToString() => Level == 0 ? Cell.ToString() : $"{Cell}@{Level}";
    }

    public enum CoverLevel { None, Half, Full }
}
