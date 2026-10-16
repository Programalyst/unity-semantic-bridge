using System.Collections.Generic;
using System.Linq;

namespace Gamenami.UnitySemanticBridge.Layout
{
    /// <summary>
    /// One placed prefab: anchored at the min corner of its (rotated) footprint,
    /// rotated by quarter turns (0-3, clockwise seen from above, i.e. +Y yaw) on top of
    /// the prefab root's own rotation, so rotation 0 places the prefab as authored.
    /// </summary>
    public sealed class LayoutPiece
    {
        public string LocalId;
        public string PrefabGuid;
        public Cell Anchor;
        public int Rotation;

        public LayoutPiece(string localId, string prefabGuid, Cell anchor, int rotation)
        {
            LocalId = localId;
            PrefabGuid = prefabGuid;
            Anchor = anchor;
            Rotation = rotation;
        }

        public override string ToString() => $"{LocalId} {PrefabGuid} {Anchor} rot {Rotation}";
    }

    /// <summary>
    /// The agent's draft/interchange form of a level (see LayoutJson for the compact JSON).
    /// Pieces block movement; decor never blocks.
    /// </summary>
    public sealed class LayoutDocument
    {
        public const int CurrentVersion = 1;

        public int Version = CurrentVersion;
        public int Width;
        public int Height;
        public List<LayoutPiece> Pieces = new List<LayoutPiece>();
        public List<Cell> Deploy = new List<Cell>();
        public List<LayoutPiece> Decor = new List<LayoutPiece>();

        public LayoutDocument(int width, int height)
        {
            Width = width;
            Height = height;
        }

        /// <summary>
        /// Differences between two layouts as report lines; empty when equal. Pieces are
        /// matched by local id and compared ignoring list order; deploy order matters
        /// because squad members fill it in order.
        /// </summary>
        public static List<string> Diff(LayoutDocument expected, LayoutDocument actual)
        {
            var lines = new List<string>();
            if (expected.Width != actual.Width || expected.Height != actual.Height)
                lines.Add($"SIZE expected {expected.Width}x{expected.Height}, got {actual.Width}x{actual.Height}");
            DiffPieces("PIECE", expected.Pieces, actual.Pieces, lines);
            DiffPieces("DECOR", expected.Decor, actual.Decor, lines);
            if (!expected.Deploy.SequenceEqual(actual.Deploy))
                lines.Add($"DEPLOY expected [{string.Join(" ", expected.Deploy)}], got [{string.Join(" ", actual.Deploy)}]");
            return lines;
        }

        private static void DiffPieces(string code, List<LayoutPiece> expected, List<LayoutPiece> actual, List<string> lines)
        {
            var actualById = new Dictionary<string, LayoutPiece>();
            foreach (var piece in actual) actualById[piece.LocalId] = piece;
            foreach (var piece in expected)
            {
                if (!actualById.TryGetValue(piece.LocalId, out var other))
                {
                    lines.Add($"{code}_MISSING {piece.Anchor} {piece.LocalId}");
                    continue;
                }
                actualById.Remove(piece.LocalId);
                if (other.PrefabGuid != piece.PrefabGuid || other.Anchor != piece.Anchor || other.Rotation != piece.Rotation)
                    lines.Add($"{code}_CHANGED {piece.Anchor} {piece.LocalId}: expected {piece.PrefabGuid} {piece.Anchor} rot {piece.Rotation}, got {other.PrefabGuid} {other.Anchor} rot {other.Rotation}");
            }
            foreach (var extra in actualById.Values)
                lines.Add($"{code}_EXTRA {extra.Anchor} {extra.LocalId}");
        }
    }
}
