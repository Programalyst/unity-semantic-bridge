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

    public enum CoverLevel { None, Half, Full }
}
