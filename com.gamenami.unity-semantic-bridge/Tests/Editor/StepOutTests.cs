using System.Collections.Generic;
using System.Linq;
using Gamenami.UnitySemanticBridge.Layout;
using NUnit.Framework;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // Step-out (plan S5, XCOM): grid sight also looks from the sideways cell where Full cover ends. NK's Validate Grid
    // Sight has the same cases on GridMap, and Validate Layout Round Trip compares LayoutGrid.CanSee with GridMap.CanSee.
    public class StepOutTests
    {
        private static readonly GridRules Rules = new GridRules
        {
            CellSize = 2f, DiagonalMoves = true, NoCornerCutting = true, SideWalls = true, GridSight = true, StepOut = true
        };

        private static LayoutGrid Board(GridRules rules = null) => new LayoutGrid(12, 12, rules ?? Rules);

        private static bool Views(LayoutGrid grid, Cell cell, params Cell[] stepOuts)
        {
            var views = grid.Viewpoints(cell);
            return views.Count == stepOuts.Length + 1 && views[0] == cell && stepOuts.All(views.Contains);
        }

        private static bool Sees(LayoutGrid grid, Cell a, Cell b) => grid.CanSee(a, b) && grid.CanSee(b, a);

        [Test]
        public void WallEndStepsOutAndMidWallDoesNot()
        {
            // The RoadNetwork repro, shifted: a unit behind the end of a three-cell Full wreck and a raider beyond it.
            var grid = Board();
            for (var x = 5; x <= 7; x++) grid.Block(new Cell(x, 4), "wreck", CoverLevel.Full);
            Assert.IsFalse(grid.HasClearLine(new Cell(5, 5), new Cell(3, 0)), "the line is blocked by the wreck");
            Assert.IsTrue(Views(grid, new Cell(5, 5), new Cell(4, 5)), "the wreck's end steps out past it");
            Assert.IsTrue(Sees(grid, new Cell(5, 5), new Cell(3, 0)), "so they see each other, both ways");
            Assert.IsTrue(Views(grid, new Cell(6, 5)), "mid-cover gives no step-out");
            Assert.IsFalse(Sees(grid, new Cell(6, 5), new Cell(4, 0)));
            Assert.IsTrue(Views(grid, new Cell(5, 4)), "a blocked cell has no step-outs");

            var off = Board(new GridRules { CellSize = 2f, DiagonalMoves = true, NoCornerCutting = true, SideWalls = true, GridSight = true });
            for (var x = 5; x <= 7; x++) off.Block(new Cell(x, 4), "wreck", CoverLevel.Full);
            Assert.IsFalse(off.CanSee(new Cell(5, 5), new Cell(3, 0)), "without StepOut the rule is the line alone");
            Assert.IsTrue(Views(off, new Cell(5, 5)));
        }

        [Test]
        public void SingleBlockerHasTwoEndsAndTheSidewaysCellMustBeOpen()
        {
            var grid = Board();
            grid.Block(new Cell(5, 5), "car", CoverLevel.Full);
            Assert.IsTrue(Views(grid, new Cell(5, 4), new Cell(4, 4), new Cell(6, 4)));
            Assert.IsTrue(Sees(grid, new Cell(5, 4), new Cell(3, 9)) && Sees(grid, new Cell(5, 4), new Cell(7, 9)));
            grid.Block(new Cell(6, 4), "sandbags", CoverLevel.Half);
            grid.SetSide(new Cell(5, 4), GridSide.MinusX, SideKind.Window, CoverLevel.Half, "w");
            Assert.IsTrue(Views(grid, new Cell(5, 4)), "a Half blocker in the sideways cell or a window between refuses the step");

            grid = Board();
            grid.Block(new Cell(0, 5), "car", CoverLevel.Full);
            Assert.IsTrue(Views(grid, new Cell(0, 4), new Cell(1, 4)), "the board edge offers no step-out");
            grid = Board();
            grid.SetSide(new Cell(5, 4), GridSide.PlusY, SideKind.Window, CoverLevel.Half, "w");
            Assert.IsTrue(Views(grid, new Cell(5, 4)), "a window is no Full cover, so no step-out");
        }

        [Test]
        public void SideWallStepsOutAtBothEnds()
        {
            var grid = Board();
            for (var x = 3; x <= 6; x++) grid.SetSide(new Cell(x, 4), GridSide.PlusY, SideKind.Wall, CoverLevel.Full, "w");
            Assert.IsTrue(Views(grid, new Cell(3, 4), new Cell(2, 4)) && Views(grid, new Cell(6, 4), new Cell(7, 4)));
            Assert.IsTrue(Views(grid, new Cell(4, 4)) && Views(grid, new Cell(5, 4)), "none mid-wall");
            Assert.IsFalse(Sees(grid, new Cell(5, 4), new Cell(5, 8)));
            Assert.IsFalse(grid.HasClearLine(new Cell(6, 4), new Cell(8, 8)));
            Assert.IsTrue(Sees(grid, new Cell(6, 4), new Cell(8, 8)) && Sees(grid, new Cell(3, 4), new Cell(1, 8)));
            Assert.IsTrue(Views(grid, new Cell(3, 5), new Cell(2, 5)) && Sees(grid, new Cell(3, 5), new Cell(1, 1)), "the far side too");
        }

        [Test]
        public void BuildingCornersAndDoorways()
        {
            // A walled room x 4..6, y 4..6 with a door on -y of (5,4).
            var grid = Board();
            for (var i = 4; i <= 6; i++)
            {
                if (i != 5) grid.SetSide(new Cell(i, 4), GridSide.MinusY, SideKind.Wall, CoverLevel.Full, "w");
                else grid.SetSide(new Cell(i, 4), GridSide.MinusY, SideKind.Door, CoverLevel.None, "w");
                grid.SetSide(new Cell(i, 6), GridSide.PlusY, SideKind.Wall, CoverLevel.Full, "w");
                grid.SetSide(new Cell(4, i), GridSide.MinusX, SideKind.Wall, CoverLevel.Full, "w");
                grid.SetSide(new Cell(6, i), GridSide.PlusX, SideKind.Wall, CoverLevel.Full, "w");
            }
            Assert.IsTrue(Views(grid, new Cell(6, 7), new Cell(7, 7)) && Views(grid, new Cell(7, 6), new Cell(7, 7)), "outside corner");
            Assert.IsFalse(grid.HasClearLine(new Cell(7, 6), new Cell(4, 8)));
            Assert.IsTrue(Sees(grid, new Cell(7, 6), new Cell(4, 8)), "round the corner along the next face");
            Assert.IsFalse(grid.HasClearLine(new Cell(6, 7), new Cell(9, 4)));
            Assert.IsTrue(Sees(grid, new Cell(6, 7), new Cell(9, 4)));
            Assert.IsFalse(Sees(grid, new Cell(7, 5), new Cell(5, 9)), "mid-face sees nothing through the wall");
            Assert.IsTrue(Views(grid, new Cell(4, 4), new Cell(5, 4)) && Views(grid, new Cell(6, 6)), "inside corners give nothing but the doorway");
            Assert.IsTrue(Views(grid, new Cell(6, 4), new Cell(5, 4)) && Sees(grid, new Cell(6, 4), new Cell(3, 0)), "inside beside the door");
            Assert.IsTrue(Views(grid, new Cell(6, 3), new Cell(5, 3), new Cell(7, 3)) && Sees(grid, new Cell(6, 3), new Cell(5, 6)),
                "outside beside the door");
            Assert.IsFalse(Sees(grid, new Cell(5, 5), new Cell(9, 5)) || Sees(grid, new Cell(5, 5), new Cell(5, 9)), "walls still block");
        }

        [Test]
        public void StepOutSightIsSymmetricAndNeverLosesALine()
        {
            var random = new System.Random(20261020);
            var grid = new LayoutGrid(12, 10, Rules);
            for (var i = 0; i < 24; i++)
                grid.Block(new Cell(random.Next(12), random.Next(10)), "b", random.NextDouble() < .6 ? CoverLevel.Full : CoverLevel.Half);
            for (var i = 0; i < 25; i++)
                grid.SetSide(new Cell(random.Next(12), random.Next(10)), random.NextDouble() < .5 ? GridSide.PlusX : GridSide.PlusY,
                    random.NextDouble() < .7 ? SideKind.Wall : SideKind.Window, random.NextDouble() < .7 ? CoverLevel.Full : CoverLevel.Half, "w");
            var gained = 0;
            for (var i = 0; i < 600; i++)
            {
                var a = new Cell(random.Next(12), random.Next(10));
                var b = new Cell(random.Next(12), random.Next(10));
                var sees = grid.CanSee(a, b);
                Assert.AreEqual(sees, grid.CanSee(b, a), $"{a} <-> {b}");
                Assert.IsTrue(sees || !grid.HasClearLine(a, b), $"{a} -> {b} keeps its clear line");
                var expected = grid.Viewpoints(a).Any(va => grid.Viewpoints(b).Any(vb => grid.HasClearLine(va, vb)));
                Assert.AreEqual(expected, sees, $"{a} -> {b} is any viewpoint pair");
                if (sees && !grid.HasClearLine(a, b)) gained++;
            }
            Assert.Greater(gained, 0, "step-out adds sight somewhere on the random board");
        }
    }
}
