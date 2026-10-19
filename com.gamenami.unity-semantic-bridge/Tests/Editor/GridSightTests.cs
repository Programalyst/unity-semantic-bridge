using Gamenami.UnitySemanticBridge.Layout;
using NUnit.Framework;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // Grid sight (plan Phase S, D13): a supercover line between cell centres, blocked by Full-cover cells and walls,
    // with blocked corners. NK's Validate Layout Round Trip checks the same rule against the game's GridMap.CanSee.
    public class GridSightTests
    {
        private static readonly GridRules Rules = new GridRules { CellSize = 2f, DiagonalMoves = true, NoCornerCutting = true, SideWalls = true, GridSight = true };

        private static LayoutGrid Board() => new LayoutGrid(9, 9, Rules);

        [Test]
        public void FullCoverCellsBlockAndHalfCoverDoesNot()
        {
            var grid = Board();
            Assert.IsTrue(grid.CanSee(new Cell(0, 4), new Cell(8, 4)));
            Assert.IsTrue(grid.CanSee(new Cell(4, 4), new Cell(4, 4)), "a cell sees itself");
            grid.Block(new Cell(4, 4), "wall", CoverLevel.Full);
            Assert.IsFalse(grid.CanSee(new Cell(0, 4), new Cell(8, 4)));
            Assert.IsFalse(grid.CanSee(new Cell(8, 4), new Cell(0, 4)));
            Assert.IsTrue(grid.CanSee(new Cell(0, 4), new Cell(4, 4)), "the blocker's own cell is seen");
            Assert.IsTrue(grid.CanSee(new Cell(4, 4), new Cell(0, 4)), "and sees out");
            Assert.IsFalse(grid.CanSee(new Cell(3, 3), new Cell(5, 5)), "a blocker on the diagonal");

            grid = Board();
            grid.Block(new Cell(4, 4), "sandbags", CoverLevel.Half);
            grid.Block(new Cell(4, 5), "tyres", CoverLevel.None);
            Assert.IsTrue(grid.CanSee(new Cell(0, 4), new Cell(8, 4)), "Half cover does not block sight");
            Assert.IsTrue(grid.CanSee(new Cell(0, 5), new Cell(8, 5)), "nor does a movement-only blocker");
        }

        [Test]
        public void CornersAreBlockedIfEitherWayRoundIs()
        {
            var grid = Board();
            grid.Block(new Cell(4, 3), "crate", CoverLevel.Full);
            Assert.IsFalse(grid.CanSee(new Cell(3, 3), new Cell(4, 4)), "a blocker beside an exact corner");
            Assert.IsFalse(grid.CanSee(new Cell(4, 4), new Cell(3, 3)));
            Assert.IsTrue(grid.CanSee(new Cell(3, 4), new Cell(5, 5)), "a line passing near it");

            grid = Board();
            grid.SetSide(new Cell(3, 4), GridSide.PlusX, SideKind.Wall, CoverLevel.Full, "w");
            Assert.IsFalse(grid.CanSee(new Cell(2, 4), new Cell(6, 4)), "a Full wall blocks a straight line");
            Assert.IsFalse(grid.CanSee(new Cell(3, 3), new Cell(4, 4)), "and both diagonals at either of its ends");
            Assert.IsFalse(grid.CanSee(new Cell(3, 4), new Cell(4, 5)));
            Assert.IsFalse(grid.CanSee(new Cell(4, 3), new Cell(3, 4)));
        }

        [Test]
        public void WindowsAndDoorsLetSightThroughAndWallsDoNot()
        {
            var grid = Board();
            // The house outline at 2,2: door on +y of 4,4, windows (Half) on -x of 2,3 and +x of 4,3.
            var outline = WallOutline.FromLines(new[] { "+-+-+D+", "|     |", "+ + + +", "w     w", "+ + + +", "|     |", "+-+-+-+" });
            foreach (var side in outline.Placed(new Cell(2, 2), 0))
                grid.SetSide(side.Cell, side.Side, side.Kind, outline.CoverFor(side.Kind), "house");
            Assert.IsTrue(grid.CanSee(new Cell(3, 3), new Cell(0, 3)), "out of a window");
            Assert.IsTrue(grid.CanSee(new Cell(8, 3), new Cell(3, 3)), "in through the other window");
            Assert.IsTrue(grid.CanSee(new Cell(4, 3), new Cell(4, 8)), "out of the door");
            Assert.IsFalse(grid.CanSee(new Cell(3, 3), new Cell(3, 0)), "not through the back wall");
            Assert.IsFalse(grid.CanSee(new Cell(3, 3), new Cell(3, 8)), "not through the front wall beside the door");
            Assert.IsFalse(grid.CanSee(new Cell(4, 4), new Cell(6, 6)), "not diagonally past the door frame's corner");
        }

        [Test]
        public void SightIsSymmetric()
        {
            var random = new System.Random(19);
            var grid = new LayoutGrid(12, 10, Rules);
            for (var i = 0; i < 30; i++)
                grid.Block(new Cell(random.Next(12), random.Next(10)), "b", random.NextDouble() < .6 ? CoverLevel.Full : CoverLevel.Half);
            for (var i = 0; i < 25; i++)
                grid.SetSide(new Cell(random.Next(12), random.Next(10)), random.NextDouble() < .5 ? GridSide.PlusX : GridSide.PlusY,
                    random.NextDouble() < .7 ? SideKind.Wall : SideKind.Window, random.NextDouble() < .7 ? CoverLevel.Full : CoverLevel.Half, "w");
            for (var i = 0; i < 400; i++)
            {
                var a = new Cell(random.Next(12), random.Next(10));
                var b = new Cell(random.Next(12), random.Next(10));
                Assert.AreEqual(grid.CanSee(a, b), grid.CanSee(b, a), $"{a} <-> {b}");
            }
        }
    }
}
