using System;
using System.Linq;
using Gamenami.UnitySemanticBridge.Layout;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // Floor heights (plan H1): a building's interior cells, the step rule on LayoutGrid, STEP_TOO_HIGH and the
    // floorHeight card field. Pure: no scene. The applier's floor boxes are in LayoutApplierCardTests.
    public class FloorHeightTests
    {
        private static readonly GridRules Rules = new GridRules
        {
            CellSize = 2f, DiagonalMoves = true, NoCornerCutting = true, SideWalls = true, MaxStepHeight = 0.5f,
        };

        private static LayoutGrid RaisedCentre(float rise, GridRules rules = null)
        {
            var grid = new LayoutGrid(3, 3, rules ?? Rules);
            var floors = new float[3, 3];
            floors[1, 1] = rise;
            grid.SetFloors(floors);
            return grid;
        }

        [Test]
        public void InteriorIsWhatWallsWindowsAndDoorsClose()
        {
            var house = WallOutline.FromLines(new[] { "+-+-+D+", "|     |", "+ + + +", "w     w", "+ + + +", "|     |", "+-+-+-+" });
            Assert.AreEqual(9, house.InteriorCells().Count, "a closed frame is all interior, door and windows included");

            // (1,1) is open to the outside on +x and leads to (1,0) and (0,0); only (0,1) is walled in.
            var open = WallOutline.FromLines(new[] { "+-+-+", "| |  ", "+-+ +", "|   |", "+-+-+" });
            CollectionAssert.AreEqual(new[] { new Cell(0, 1) }, open.InteriorCells());

            var turned = house.Rotate(1).InteriorCells();
            Assert.AreEqual(9, turned.Count);
            CollectionAssert.AreEqual(turned.OrderBy(c => c.Y).ThenBy(c => c.X), turned, "rows from y = 0, each from x = 0");
        }

        [Test]
        public void StepsAboveTheLimitCloseMovementNotSight()
        {
            var high = RaisedCentre(0.6f);
            var low = RaisedCentre(0.5f);
            foreach (var cell in high.AllCells().Where(c => !c.Equals(new Cell(1, 1))))
            {
                Assert.IsFalse(high.CanStep(cell, new Cell(1, 1)), $"0.6 m up from {cell}");
                Assert.IsFalse(high.CanStep(new Cell(1, 1), cell), $"0.6 m down to {cell}");
                Assert.IsTrue(low.CanStep(cell, new Cell(1, 1)), $"0.5 m up from {cell}");
            }
            Assert.IsTrue(high.IsStepClosed(new Cell(0, 1), GridSide.PlusX));
            Assert.AreEqual(0.6f, high.StepAcross(new Cell(1, 0), GridSide.PlusY), 1e-6f);
            Assert.IsFalse(high.CanStep(new Cell(0, 0), new Cell(1, 0)) && high.CanStep(new Cell(1, 0), new Cell(2, 1)),
                "the corner next to a closed side stays sealed for diagonals");
            Assert.IsTrue(high.CanStep(new Cell(0, 0), new Cell(1, 0)), "steps away from the raised cell stay open");
            Assert.IsTrue(high.CanSee(new Cell(0, 1), new Cell(2, 1)), "sight ignores floor steps");

            var noRule = RaisedCentre(3f, new GridRules { CellSize = 2f });
            Assert.IsTrue(noRule.CanStep(new Cell(0, 1), new Cell(1, 1)), "MaxStepHeight 0: no step rule");
            Assert.IsTrue(new LayoutGrid(3, 3, Rules).CanStep(new Cell(0, 1), new Cell(1, 1)), "no floors: no step rule");
        }

        [Test]
        public void DiagonalRiseCountsEvenWhenEachSideIsLow()
        {
            // 0, 0.5, 0.5, 1.0 round a corner: every side is 0.5 m, the diagonal climbs 1 m.
            var grid = new LayoutGrid(2, 2, Rules);
            grid.SetFloors(new[,] { { 0f, 0.5f }, { 0.5f, 1f } });
            Assert.IsTrue(grid.CanStep(new Cell(0, 0), new Cell(1, 0)));
            Assert.IsFalse(grid.CanStep(new Cell(0, 0), new Cell(1, 1)));
        }

        [Test]
        public void StepTooHighReportsOpenSidesOnly()
        {
            var grid = RaisedCentre(0.6f);
            var issues = LayoutValidator.Steps(grid);
            Assert.AreEqual(4, issues.Count, string.Join("\n", issues));
            Assert.IsTrue(issues.All(i => i.Code == "STEP_TOO_HIGH" && i.Severity == IssueSeverity.Error));
            StringAssert.StartsWith("STEP_TOO_HIGH 0,1 floor 0.00 m, 1,1 next to it 0.60 m: a 0.60 m step", issues.First(i => i.Cell.Equals(new Cell(0, 1))).ToString());

            grid.SetSide(new Cell(1, 1), GridSide.MinusX, SideKind.Wall, CoverLevel.Full, "wall");
            grid.Block(new Cell(1, 2), "crate", CoverLevel.Half);
            Assert.AreEqual(2, LayoutValidator.Steps(grid).Count, "a wall or a blocked cell is no step");
            Assert.IsEmpty(LayoutValidator.Steps(RaisedCentre(0.5f)));
            Assert.IsEmpty(LayoutValidator.Steps(new LayoutGrid(3, 3, Rules)), "no floors, no check");
        }

        [Test]
        public void ForGridAndValidateUseTheFloors()
        {
            var grid = RaisedCentre(0.6f);
            var report = LayoutValidator.ForGrid(grid, new[] { new Cell(0, 0) }, new LayoutEnemySpawn[0]);
            Assert.AreEqual(4, report.WithCode("STEP_TOO_HIGH").Count());
            Assert.AreEqual(1, report.WithCode("POCKET").Count(), "the raised cell is a pocket");

            var document = LayoutJson.Parse("{\"v\":1,\"size\":[3,3],\"pieces\":[],\"deploy\":[[0,0]],\"decor\":[]}");
            var floors = new float[3, 3];
            floors[1, 1] = 0.6f;
            var validated = LayoutValidator.Validate(document, _ => null, Rules, floors);
            Assert.AreEqual(4, validated.WithCode("STEP_TOO_HIGH").Count());
            Assert.IsTrue(validated.Grid.HasFloors);
            Assert.Throws<ArgumentException>(() => grid.SetFloors(new float[2, 3]));
        }

        [Test]
        public void FloorHeightOverrideReachesWalledBuildingCards()
        {
            const string guid = "1b69bd820a5d18d439809c81ed2b9279";
            var overrides = LevelCatalogOverrides.Parse("{\"v\":1,\"pieces\":{\"" + guid + "\":{\"role\":\"building\",\"floorHeight\":0.12," +
                                                        "\"walls\":{\"outline\":[\"+-+\",\"| |\",\"+D+\"]}}}}");
            var entry = overrides.Resolve(guid, "Pack/House.prefab");
            Assert.AreEqual(0.12, entry.FloorHeight);

            var card = new LevelCard { Guid = guid, Id = "Pack/House.prefab", Name = "SM_Bld_House", VisualBounds = Box3.FromMinMax(new Vec3(-1, 0, -1), new Vec3(1, 3, 1)) };
            LevelCards.Classify(card, Vec3.One, entry);
            Assert.AreEqual(0.12, card.FloorHeight);
            var json = LevelCards.ToJson(card);
            Assert.AreEqual(0.12, (double)json["floorHeight"]);
            Assert.AreEqual(0.12, LevelCards.FromJson(guid, json).FloorHeight);

            var solid = LevelCatalogOverrides.Parse("{\"v\":1,\"pieces\":{\"" + guid + "\":{\"role\":\"building\",\"floorHeight\":0.12}}}");
            LevelCards.Classify(card, Vec3.One, solid.Resolve(guid, card.Id));
            Assert.IsNull(card.FloorHeight, "a building without walls has no floor box");
            Assert.IsNull(LevelCards.ToJson(card)["floorHeight"], "and its card keeps its shape");

            var error = Assert.Throws<FormatException>(() => LevelCatalogOverrides.Parse("{\"v\":1,\"pieces\":{\"" + guid + "\":{\"floorHeight\":\"high\"}}}"));
            StringAssert.Contains("floorHeight must be a number of meters", error.Message);
        }
    }
}
