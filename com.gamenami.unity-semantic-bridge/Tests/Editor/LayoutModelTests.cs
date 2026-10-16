using System.Collections.Generic;
using System.Linq;
using Gamenami.UnitySemanticBridge.Layout;
using NUnit.Framework;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // Pure layout model: schema, parser and grid rules. No scene, no UnityEngine types.
    public class LayoutModelTests
    {
        private const string WallGuid = "215a18edfee7f4f2eaaa7fe488cea3ab";
        private const string LowWallGuid = "76918e2623518495e8239f5f325e4169";
        private const string ProfileGuid = "42e57b125c17a4bdab86d7879003f0e8";
        private const string VisualGuid = "b0388b2498f234e91a73a49eec10add1";

        private static readonly GridRules NoCornerCutting = new GridRules { CellSize = 2f, DiagonalMoves = true, NoCornerCutting = true };

        private const string Sample =
            "{\"v\":1,\"size\":[12,10],\n" +
            " \"pieces\":[[\"wallF\",\"" + WallGuid + "\",3,4,1],[\"low\",\"" + LowWallGuid + "\",6,2,0]],\n" +
            " \"deploy\":[[1,1],[2,1]],\n" +
            " \"enemies\":[[\"raider1\",\"" + ProfileGuid + "\",\"" + VisualGuid + "\",10,8,180],[\"raider2\",\"" + ProfileGuid + "\",\"" + VisualGuid + "\",11,7,22.5]],\n" +
            " \"decor\":[[\"road\",\"" + LowWallGuid + "\",0,5,2]]}";

        [Test]
        public void ParsesCompactForm()
        {
            var doc = LayoutJson.Parse(Sample);
            Assert.AreEqual(12, doc.Width);
            Assert.AreEqual(10, doc.Height);
            Assert.AreEqual(2, doc.Pieces.Count);
            Assert.AreEqual("wallF", doc.Pieces[0].LocalId);
            Assert.AreEqual(WallGuid, doc.Pieces[0].PrefabGuid);
            Assert.AreEqual(new Cell(3, 4), doc.Pieces[0].Anchor);
            Assert.AreEqual(1, doc.Pieces[0].Rotation);
            CollectionAssert.AreEqual(new[] { new Cell(1, 1), new Cell(2, 1) }, doc.Deploy);
            Assert.AreEqual(2, doc.Decor.Single().Rotation);
            Assert.AreEqual(2, doc.Enemies.Count);
            var raider = doc.Enemies[0];
            Assert.AreEqual("raider1", raider.LocalId);
            Assert.AreEqual(ProfileGuid, raider.ProfileGuid);
            Assert.AreEqual(VisualGuid, raider.VisualGuid);
            Assert.AreEqual(new Cell(10, 8), raider.Cell);
            Assert.AreEqual(180f, raider.Facing);
            Assert.AreEqual(22.5f, doc.Enemies[1].Facing);
        }

        [Test]
        public void SerializesEnemiesCompactly()
        {
            var text = LayoutJson.Serialize(LayoutJson.Parse(Sample));
            StringAssert.Contains(
                " \"enemies\":[\n  [\"raider1\",\"" + ProfileGuid + "\",\"" + VisualGuid + "\",10,8,180],\n" +
                "  [\"raider2\",\"" + ProfileGuid + "\",\"" + VisualGuid + "\",11,7,22.5]],\n", text);
        }

        [Test]
        public void SerializeRoundTrips()
        {
            var doc = LayoutJson.Parse(Sample);
            var text = LayoutJson.Serialize(doc);
            CollectionAssert.IsEmpty(LayoutDocument.Diff(doc, LayoutJson.Parse(text)));
            Assert.AreEqual(text, LayoutJson.Serialize(LayoutJson.Parse(text)), "Serialization must be stable.");
        }

        [Test]
        public void OptionalSectionsMayBeOmitted()
        {
            var doc = LayoutJson.Parse("{\"v\":1,\"size\":[4,4]}");
            Assert.IsEmpty(doc.Pieces);
            Assert.IsEmpty(doc.Deploy);
            Assert.IsEmpty(doc.Decor);
            Assert.IsEmpty(doc.Enemies);
        }

        [TestCase("", "empty")]
        [TestCase("{", "not valid JSON")]
        [TestCase("{\"v\":2,\"size\":[4,4]}", "Unsupported layout version 2")]
        [TestCase("{\"v\":1,\"size\":[0,4]}", "size must be positive")]
        [TestCase("{\"v\":1,\"size\":[4]}", "size must be [x, y]")]
        [TestCase("{\"v\":1,\"size\":[4,4],\"peices\":[]}", "Unknown key 'peices'")]
        [TestCase("{\"v\":1,\"size\":[4,4],\"pieces\":[[\"a\",\"" + WallGuid + "\",1,1]]}", "pieces[0] must be [localId, prefabGuid, x, y, rot]")]
        [TestCase("{\"v\":1,\"size\":[4,4],\"pieces\":[[\"a\",\"" + WallGuid + "\",1,1,4]]}", "rot must be 0-3")]
        [TestCase("{\"v\":1,\"size\":[4,4],\"pieces\":[[\"a\",\"" + WallGuid + "\",1.5,1,0]]}", "pieces[0] x must be an integer")]
        [TestCase("{\"v\":1,\"size\":[4,4],\"pieces\":[[\"a\",\"Wall\",1,1,0]]}", "not a 32-character lowercase hex asset GUID")]
        [TestCase("{\"v\":1,\"size\":[4,4],\"pieces\":[[\"a\",\"" + WallGuid + "\",1,1,0]],\"decor\":[[\"a\",\"" + WallGuid + "\",2,2,0]]}", "decor[0]: duplicate localId 'a'")]
        [TestCase("{\"v\":1,\"size\":[4,4],\"enemies\":[[\"e\",\"" + ProfileGuid + "\",\"" + VisualGuid + "\",1,1]]}", "enemies[0] must be [localId, profileGuid, visualGuid, x, y, facing]")]
        [TestCase("{\"v\":1,\"size\":[4,4],\"enemies\":[[\"e\",\"Raider\",\"" + VisualGuid + "\",1,1,0]]}", "enemies[0] profileGuid 'Raider' is not a 32-character")]
        [TestCase("{\"v\":1,\"size\":[4,4],\"enemies\":[[\"e\",\"" + ProfileGuid + "\",\"" + VisualGuid + "\",1,1,\"north\"]]}", "enemies[0] facing must be a number of degrees")]
        [TestCase("{\"v\":1,\"size\":[4,4],\"pieces\":[[\"a\",\"" + WallGuid + "\",1,1,0]],\"enemies\":[[\"a\",\"" + ProfileGuid + "\",\"" + VisualGuid + "\",2,2,0]]}", "enemies[0]: duplicate localId 'a'")]
        public void RejectsMalformedLayouts(string json, string expected)
        {
            var error = Assert.Throws<LayoutFormatException>(() => LayoutJson.Parse(json));
            StringAssert.Contains(expected, error.Message);
        }

        [Test]
        public void RotationSwapsFootprintAndAnchorsAtMinCorner()
        {
            var spec = new PieceSpec(2, 1, CoverLevel.Full);
            var cells = LayoutGrid.Footprint(new LayoutPiece("w", WallGuid, new Cell(3, 4), 1), spec).ToList();
            CollectionAssert.AreEquivalent(new[] { new Cell(3, 4), new Cell(3, 5) }, cells);
            cells = LayoutGrid.Footprint(new LayoutPiece("w", WallGuid, new Cell(3, 4), 2), spec).ToList();
            CollectionAssert.AreEquivalent(new[] { new Cell(3, 4), new Cell(4, 4) }, cells);
        }

        [Test]
        public void BuildDerivesCellsAndReportsConflicts()
        {
            var doc = new LayoutDocument(5, 5);
            doc.Pieces.Add(new LayoutPiece("a", WallGuid, new Cell(1, 1), 0));
            doc.Pieces.Add(new LayoutPiece("b", WallGuid, new Cell(2, 1), 0));   // overlaps a at 2,1
            doc.Pieces.Add(new LayoutPiece("c", WallGuid, new Cell(4, 4), 0));   // leaves the board at 5,4
            doc.Pieces.Add(new LayoutPiece("d", "0123456789abcdef0123456789abcdef", new Cell(0, 0), 0));
            doc.Deploy.Add(new Cell(1, 1));
            doc.Deploy.Add(new Cell(9, 9));
            var issues = new List<LayoutIssue>();
            var grid = LayoutGrid.Build(doc, p => p.PrefabGuid == WallGuid ? new PieceSpec(2, 1, CoverLevel.Half) : (PieceSpec?)null,
                NoCornerCutting, issues);

            Assert.AreEqual("a", grid[new Cell(2, 1)].Occupant, "The first piece keeps an overlapped cell.");
            Assert.AreEqual(CoverLevel.Half, grid[new Cell(1, 1)].Cover);
            Assert.IsTrue(grid.IsBlocked(new Cell(3, 1)));
            Assert.IsTrue(grid.IsBlocked(new Cell(4, 4)));
            Assert.AreEqual(4, grid.BlockedCount);
            CollectionAssert.AreEquivalent(new[]
            {
                "OVERLAP 2,1 b overlaps a",
                "OUT_OF_BOUNDS 5,4 c (2,1 at 4,4)",
                "UNKNOWN_PIECE 0,0 d: no footprint for prefab 0123456789abcdef0123456789abcdef",
                "DEPLOY_BLOCKED 1,1 deploy cell is under a",
                "DEPLOY_OUT_OF_BOUNDS 9,9 deploy cell is off the board",
            }, issues.Select(i => i.ToString()));
        }

        [Test]
        public void DiagonalMayNotCutPastABlockedOrthogonalNeighbour()
        {
            var grid = new LayoutGrid(4, 4, NoCornerCutting);
            grid.Block(new Cell(1, 0), "w", CoverLevel.Full);
            Assert.IsFalse(grid.CanStep(new Cell(0, 0), new Cell(1, 1)), "x-side neighbour blocked");
            Assert.IsFalse(grid.CanStep(new Cell(1, 1), new Cell(0, 0)), "reverse direction, y-side neighbour blocked");
            Assert.IsTrue(grid.CanStep(new Cell(0, 0), new Cell(0, 1)));
            Assert.IsTrue(grid.CanStep(new Cell(0, 1), new Cell(1, 2)), "diagonal away from the wall");
            Assert.IsFalse(grid.CanStep(new Cell(0, 0), new Cell(1, 0)), "into a blocker");
            Assert.IsFalse(grid.CanStep(new Cell(0, 0), new Cell(-1, 0)), "off the board");
            Assert.IsFalse(grid.CanStep(new Cell(0, 0), new Cell(2, 0)), "not adjacent");

            var cornerCutting = new LayoutGrid(4, 4, new GridRules { NoCornerCutting = false });
            cornerCutting.Block(new Cell(1, 0), "w", CoverLevel.Full);
            Assert.IsTrue(cornerCutting.CanStep(new Cell(0, 0), new Cell(1, 1)));

            var fourWay = new LayoutGrid(4, 4, new GridRules { DiagonalMoves = false });
            Assert.IsFalse(fourWay.CanStep(new Cell(0, 0), new Cell(1, 1)));
            CollectionAssert.AreEquivalent(new[] { new Cell(1, 0), new Cell(0, 1) }, fourWay.Neighbours(new Cell(0, 0)));
        }

        [Test]
        public void DiagonalWallSeals()
        {
            // A wall drawn diagonally from 0,3 to 3,0 must separate the corners under the corner rule.
            var grid = new LayoutGrid(4, 4, NoCornerCutting);
            for (var i = 0; i < 4; i++) grid.Block(new Cell(i, 3 - i), "w" + i, CoverLevel.Full);
            var reached = new HashSet<Cell> { new Cell(0, 0) };
            var frontier = new Queue<Cell>(reached);
            while (frontier.Count > 0)
                foreach (var next in grid.Neighbours(frontier.Dequeue()))
                    if (reached.Add(next)) frontier.Enqueue(next);
            Assert.IsFalse(reached.Contains(new Cell(3, 3)));
            CollectionAssert.AreEquivalent(new[] { new Cell(0, 0), new Cell(1, 0), new Cell(0, 1), new Cell(1, 1), new Cell(2, 0), new Cell(0, 2) }, reached);
        }

        [Test]
        public void CoverComesFromOrthogonalNeighboursOnly()
        {
            var grid = new LayoutGrid(3, 3, NoCornerCutting);
            grid.Block(new Cell(1, 2), "full", CoverLevel.Full);
            grid.Block(new Cell(2, 1), "half", CoverLevel.Half);
            grid.Block(new Cell(0, 0), "diagonal", CoverLevel.Full);
            var sides = grid.CoverSides(new Cell(1, 1));
            Assert.AreEqual(CoverLevel.Full, sides.forward);
            Assert.AreEqual(CoverLevel.None, sides.back);
            Assert.AreEqual(CoverLevel.None, sides.left);
            Assert.AreEqual(CoverLevel.Half, sides.right);
            Assert.AreEqual(CoverLevel.None, grid.CoverSides(new Cell(2, 2)).forward, "Off the board gives no cover.");
        }

        [Test]
        public void DiffsReportCellsAndPieces()
        {
            var expected = new LayoutGrid(3, 3, NoCornerCutting);
            expected.Block(new Cell(0, 0), "a", CoverLevel.Full);
            expected.Block(new Cell(1, 0), "b", CoverLevel.Half);
            var actual = new LayoutGrid(3, 3, NoCornerCutting);
            actual.Block(new Cell(1, 0), "b", CoverLevel.Full);
            actual.Block(new Cell(2, 2), "x", CoverLevel.Half);
            CollectionAssert.AreEqual(new[]
            {
                "MISSING_BLOCK 0,0 expected a",
                "COVER 1,0 expected Half from b, got Full from b",
                "EXTRA_BLOCK 2,2 x",
            }, expected.DiffOccupancy(actual));

            var a = LayoutJson.Parse(Sample);
            var b = LayoutJson.Parse(Sample);
            b.Pieces[0].Rotation = 3;
            b.Pieces.RemoveAt(1);
            b.Decor[0].LocalId = "street";
            b.Enemies[0].LocalId = "enemy1";   // levels store no ids; renaming is not a change
            b.Enemies[1].Facing = 90f;
            b.Enemies.Add(new LayoutEnemySpawn("raider3", ProfileGuid, VisualGuid, new Cell(0, 9), 0f));
            CollectionAssert.AreEqual(new[]
            {
                $"PIECE_CHANGED 3,4 wallF: expected {WallGuid} 3,4 rot 1, got {WallGuid} 3,4 rot 3",
                "PIECE_MISSING 6,2 low",
                "DECOR_MISSING 0,5 road",
                "DECOR_EXTRA 0,5 street",
                $"ENEMY_CHANGED 11,7 #2: expected raider2 {ProfileGuid} {VisualGuid} 11,7 facing 22.5, got raider2 {ProfileGuid} {VisualGuid} 11,7 facing 90",
                "ENEMY_EXTRA 0,9 #3 raider3",
            }, LayoutDocument.Diff(a, b));
        }

        [Test]
        public void AsciiMapPutsHighestRowFirst()
        {
            var grid = new LayoutGrid(3, 2, NoCornerCutting);
            grid.Block(new Cell(0, 0), "a", CoverLevel.Full);
            grid.Block(new Cell(2, 1), "b", CoverLevel.Half);
            Assert.AreEqual("A.h\n#..\n", grid.ToAscii(new[] { new Cell(0, 1) }));
            Assert.AreEqual("-Eh\n!..\n", grid.ToAscii(new[] { new Cell(0, 0) }, new[] { new Cell(1, 1) }, new[] { new Cell(0, 1) }));
        }
    }
}
