using System;
using Gamenami.UnitySemanticBridge.Layout;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // Pure level catalog cards: footprints on request, roles, overrides, JSON. No UnityEngine types.
    public class LevelCardTests
    {
        private const double Cell = 2.0;

        // NK's Wall SF (docs/level-rules.md): prefab scale (1.65, 2, 1.5), root turned 90°, box
        // collider 3.91 x 2.90 x 0.75 m at that scale, 2 cells long along grid y at rotation 0.
        private static LevelCard WallSf() => new LevelCard
        {
            Name = "Wall SF",
            Id = "Prefabs/Structures/Wall SF/Wall SF.prefab",
            Role = LevelRole.CoverFull,
            VisualBounds = new Box3(new Vec3(0, 3.79 / 2.0 / 2.0, 0), new Vec3(3.91 / 1.65, 3.79 / 2.5, 1.79 / 2.0)),
            ColliderBounds = new Box3(new Vec3(0, 1.45 / 2.0, 0), new Vec3(3.91 / 1.65, 2.90 / 2.0, 0.75 / 1.5)),
            RootRotation = new Vec3(0, 90, 0),
            PlacementScale = new Vec3(1.65, 2, 1.5),
        };

        // Wasteland SM_Prop_Barrier_01 (docs/level-rules.md): 3.26 x 1.41 x 0.79 m at scale 1, mesh collider.
        private static LevelCard Barrier01() => new LevelCard
        {
            Name = "SM_Prop_Barrier_01",
            Id = "PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Barrier_01.prefab",
            Role = LevelRole.Blocker,
            VisualBounds = new Box3(new Vec3(0, 0.705, 0), new Vec3(3.26, 1.41, 0.79)),
            ColliderBounds = new Box3(new Vec3(0, 0.705, 0), new Vec3(3.26, 1.41, 0.79)),
        };

        [Test]
        public void WallSfFootprintMatchesHandMeasurement()
        {
            var footprint = LevelCards.Footprint(WallSf(), Cell);
            Assert.AreEqual(1, footprint.Width, "across the wall (grid x at rot 0)");
            Assert.AreEqual(2, footprint.Depth, "along the wall (grid y at rot 0, root turned 90°)");
            Assert.IsTrue(footprint.Size.ApproximatelyEquals(new Vec3(0.75, 2.90, 3.91)), footprint.Size.ToString());
            Assert.AreEqual(2.90, footprint.Height, 1e-6);
            Assert.AreEqual(1.25, footprint.FitErrorX, 1e-6);
            Assert.AreEqual(0.09, footprint.FitErrorZ, 1e-6);
            Assert.IsFalse(footprint.Approximate);
            Assert.AreEqual(new Cell(2, 1), footprint.RotatedSize(1));
            Assert.AreEqual("1x2 Full", footprint.ToSpec(CoverLevel.Full).ToString());
        }

        [Test]
        public void WallSfHalfFootprintMatchesHandMeasurement()
        {
            // Wall SF (half): scale (1.65, 0.8, 1), collider 3.80 x 1.12 x 0.70 m, 2 x 1 cells.
            var card = new LevelCard
            {
                Role = LevelRole.CoverHalf,
                VisualBounds = new Box3(new Vec3(0, 0.7, 0), new Vec3(3.91 / 1.65, 1.4, 0.9)),
                ColliderBounds = new Box3(new Vec3(0, 0.7, 0), new Vec3(3.80 / 1.65, 1.12 / 0.8, 0.70)),
                RootRotation = new Vec3(0, 90, 0),
                PlacementScale = new Vec3(1.65, 0.8, 1),
            };
            var footprint = LevelCards.Footprint(card, Cell);
            Assert.AreEqual((1, 2), (footprint.Width, footprint.Depth));
            Assert.AreEqual(1.12, footprint.Height, 1e-6);
        }

        [Test]
        public void WastelandBarrierSpansTwoCellsWithSlack()
        {
            var footprint = LevelCards.Footprint(Barrier01(), Cell);
            Assert.AreEqual((2, 1), (footprint.Width, footprint.Depth));
            Assert.AreEqual(0.74, footprint.FitErrorX, 1e-6, "about 0.7 m spare along the barrier");
            Assert.AreEqual(1.21, footprint.FitErrorZ, 1e-6);
            Assert.AreEqual(1.41, footprint.Height, 1e-6);
        }

        [Test]
        public void FootprintFollowsCellSizeAndScaleException()
        {
            var card = Barrier01();
            Assert.AreEqual((4, 1), Size(LevelCards.Footprint(card, 1.0)));
            // A per-piece exception scale replaces placementScale; the card is unchanged.
            var scaled = LevelCards.Footprint(card, Cell, new Vec3(1.2, 1, 1));
            Assert.AreEqual((2, 1), Size(scaled));
            Assert.AreEqual(4 - 3.912, scaled.FitErrorX, 1e-6);
            Assert.AreEqual((3, 1), Size(LevelCards.Footprint(card, Cell, new Vec3(1.25, 1, 1))));
            Assert.IsTrue(card.PlacementScale.Equals(Vec3.One));
        }

        [Test]
        public void ToleranceAllowsFiveCentimetres()
        {
            Assert.AreEqual(2, LevelCards.CellsFor(4.05, Cell));
            Assert.AreEqual(3, LevelCards.CellsFor(4.06, Cell));
            Assert.AreEqual(1, LevelCards.CellsFor(0.01, Cell), "at least one cell");
            Assert.AreEqual(2, LevelCards.CellsFor(4.0, Cell));
        }

        [Test]
        public void FootprintUsesCollidersOnlyForBlockingRoles()
        {
            var card = Barrier01();
            card.ColliderBounds = new Box3(new Vec3(0, 0.5, 0), new Vec3(1.5, 1, 0.5));
            Assert.AreEqual((1, 1), Size(LevelCards.Footprint(card, Cell)));
            card.Role = LevelRole.Decor;
            Assert.AreEqual((2, 1), Size(LevelCards.Footprint(card, Cell)), "decor uses the visual bounds");
            card.Role = LevelRole.Blocker;
            card.ColliderBounds = null;
            Assert.AreEqual((2, 1), Size(LevelCards.Footprint(card, Cell)), "no colliders: visual bounds");
        }

        [Test]
        public void FootprintBoundsFollowHowThePieceIsPlaced()
        {
            // A cover piece placed as decor measures its renderers; a ground piece placed as a blocker its colliders.
            var card = Barrier01();
            card.ColliderBounds = new Box3(new Vec3(0, 0.5, 0), new Vec3(1.5, 1, 0.5));
            Assert.AreEqual((2, 1), Size(LevelCards.Footprint(card, Cell, blocking: false)));
            card.Role = LevelRole.Ground;
            Assert.AreEqual((1, 1), Size(LevelCards.Footprint(card, Cell, blocking: true)));
            Assert.AreEqual((2, 1), Size(LevelCards.Footprint(card, Cell)), "null follows the role");
        }

        [Test]
        public void OnlyPieceEntriesCountAsReviewed()
        {
            var overrides = LevelCatalogOverrides.Parse(@"{""v"":1,
                ""rules"":[{""match"":""**/SM_Prop_*"",""role"":""decor""}],
                ""pieces"":{""g1"":{""role"":""cover_half""},""Pack/Prefabs/SM_Prop_B.prefab"":{""lift"":0.5}}}");
            Assert.IsTrue(overrides.HasPieceEntry("g1", "Pack/Prefabs/SM_Prop_A.prefab"));
            Assert.IsTrue(overrides.HasPieceEntry("g2", "Pack/Prefabs/SM_Prop_B.prefab"), "by catalog id");
            Assert.IsFalse(overrides.HasPieceEntry("g3", "Pack/Prefabs/SM_Prop_C.prefab"), "a rule match alone is not a review");
        }

        [Test]
        public void OffCentrePivotMovesTheCentreNotTheSize()
        {
            var bounds = new Box3(new Vec3(1.5, 0.5, -0.25), new Vec3(3, 1, 0.5));
            var footprint = LevelCards.Footprint(bounds, new Vec3(0, 90, 0), Vec3.One, Cell);
            Assert.AreEqual((1, 2), Size(footprint));
            // Unity yaw +90 maps +x to -z.
            Assert.IsTrue(footprint.Center.ApproximatelyEquals(new Vec3(-0.25, 0.5, -1.5)), footprint.Center.ToString());
        }

        [Test]
        public void EulerFollowsUnityConvention()
        {
            Assert.IsTrue(LevelCards.Rotate(new Vec3(0, 90, 0), new Vec3(1, 0, 0)).ApproximatelyEquals(new Vec3(0, 0, -1)));
            Assert.IsTrue(LevelCards.Rotate(new Vec3(90, 0, 0), new Vec3(0, 1, 0)).ApproximatelyEquals(new Vec3(0, 0, 1)));
            Assert.IsTrue(LevelCards.Rotate(new Vec3(0, 0, 90), new Vec3(1, 0, 0)).ApproximatelyEquals(new Vec3(0, 1, 0)));
            // Z, then X, then Y.
            Assert.IsTrue(LevelCards.Rotate(new Vec3(90, 90, 0), new Vec3(0, 1, 0)).ApproximatelyEquals(new Vec3(1, 0, 0)));
        }

        [Test]
        public void NonQuarterRootTurnsAreApproximate()
        {
            var box = new Box3(new Vec3(0, 0.5, 0), new Vec3(2, 1, 2));
            var yaw45 = LevelCards.Footprint(box, new Vec3(0, 45, 0), Vec3.One, Cell);
            Assert.IsTrue(yaw45.Approximate);
            Assert.AreEqual(Math.Sqrt(8), yaw45.Size.X, 1e-6, "the box overstates the size");
            Assert.IsTrue(LevelCards.Footprint(box, new Vec3(270, 0, 0), Vec3.One, Cell).Approximate, "tilted root");
            Assert.IsFalse(LevelCards.Footprint(box, new Vec3(0, 180, 0), Vec3.One, Cell).Approximate);
            Assert.IsTrue(LevelCards.Footprint(box, Vec3.Zero, Vec3.One, Cell, skewed: true).Approximate);
        }

        [TestCase("SM_Env_Road_Straight_01", "Pack/Prefabs/Env/SM_Env_Road_Straight_01.prefab", 0.1, 4, 4, LevelRole.Ground, "name")]
        [TestCase("SM_Env_Kerb_01", "Pack/Prefabs/Sidewalk/SM_Env_Kerb_01.prefab", 0.3, 2, 1, LevelRole.Ground, "name")]
        [TestCase("SM_Prop_Sandbag_Wall_01", "Pack/Prefabs/Props/SM_Prop_Sandbag_Wall_01.prefab", 0.9, 2, 1, LevelRole.CoverHalf, "name")]
        [TestCase("SM_Bld_Wall_Chunk_01", "Pack/Prefabs/Buildings/SM_Bld_Wall_Chunk_01.prefab", 3.0, 4, 0.5, LevelRole.CoverFull, "name")]
        [TestCase("SM_Prop_Barrier_01", "Pack/Prefabs/Props/SM_Prop_Barrier_01.prefab", 1.41, 3.26, 0.79, LevelRole.Blocker, "name")]
        [TestCase("SM_Prop_Fence_01", "Pack/Prefabs/Props/SM_Prop_Fence_01.prefab", 2.0, 4, 0.2, LevelRole.CoverFull, "name")]
        [TestCase("SM_Bld_Motel_01", "Pack/Prefabs/Buildings/SM_Bld_Motel_01.prefab", 7.0, 20, 10, LevelRole.Building, "name")]
        [TestCase("SM_Bld_Door_05", "Pack/Prefabs/Buildings/SM_Bld_Door_05.prefab", 2.2, 1.2, 0.1, LevelRole.CoverFull, "geometry")]
        [TestCase("SM_Bld_Diner_Sign", "Pack/Prefabs/Buildings/SM_Bld_Diner_Sign.prefab", 10.5, 3, 0.3, LevelRole.Decor, "name")]
        [TestCase("SM_Prop_Greeble_Wall_01", "Pack/Prefabs/Props/SM_Prop_Greeble_Wall_01.prefab", 0.6, 1, 0.1, LevelRole.Decor, "name")]
        [TestCase("SM_Prop_Sign_Road_01", "Pack/Prefabs/Props/SM_Prop_Sign_Road_01.prefab", 0.3, 1, 0.1, LevelRole.Decor, "name")]
        [TestCase("SM_Bld_Bunker_Floor_Wood_01", "Pack/Prefabs/Buildings/SM_Bld_Bunker_Floor_Wood_01.prefab", 0.31, 3, 3, LevelRole.Ground, "geometry")]
        [TestCase("SM_Prop_Pallet_01", "Pack/Prefabs/Props/SM_Prop_Pallet_01.prefab", 0.4, 1.2, 1, LevelRole.CoverHalf, "geometry")]
        [TestCase("SM_Prop_Rug_01", "Pack/Prefabs/Props/SM_Prop_Rug_01.prefab", 0.05, 2, 1, LevelRole.Ground, "geometry")]
        [TestCase("SM_Prop_Can_01", "Pack/Prefabs/Props/SM_Prop_Can_01.prefab", 0.3, 0.1, 0.1, LevelRole.Decor, "geometry")]
        [TestCase("SM_Prop_Crate_01", "Pack/Prefabs/Props/SM_Prop_Crate_01.prefab", 1.0, 1, 1, LevelRole.CoverHalf, "geometry")]
        [TestCase("SM_Prop_Container_01", "Pack/Prefabs/Props/SM_Prop_Container_01.prefab", 2.6, 6, 2.4, LevelRole.CoverFull, "geometry")]
        [TestCase("SM_Prop_Shelf_01", "Pack/Prefabs/Props/SM_Prop_Shelf_01.prefab", 1.6, 1, 0.5, LevelRole.Unknown, "geometry")]
        public void RolesFollowNameThenGeometry(string name, string id, double height, double x, double z, LevelRole expected, string source)
        {
            var card = new LevelCard
            {
                Name = name,
                Id = id,
                VisualBounds = new Box3(new Vec3(0, height / 2, 0), new Vec3(x, height, z)),
            };
            LevelCards.Classify(card, Vec3.One, null);
            Assert.AreEqual(expected, card.Role);
            Assert.AreEqual(source, card.RoleSource);
        }

        [Test]
        public void ClassifyMeasuresAtPlacementScaleAndFillsDerivedFields()
        {
            // 1.0 m tall at scale 1, but the prefab root is scaled 2.5 in y: 2.5 m, full cover.
            var card = new LevelCard
            {
                Name = "SM_Prop_Wall_01",
                Id = "Pack/SM_Prop_Wall_01.prefab",
                VisualBounds = new Box3(new Vec3(0, 0.5, 0), new Vec3(2, 1, 0.3)),
                ColliderBounds = new Box3(new Vec3(0.1, 0.4, 0), new Vec3(1.8, 0.8, 0.2)),
            };
            LevelCards.Classify(card, new Vec3(1, 2.5, 1), null);
            Assert.AreEqual(LevelRole.CoverFull, card.Role);
            Assert.AreEqual(CoverLevel.Full, card.Cover);
            Assert.AreEqual("prefab", card.ScaleSource);
            Assert.IsTrue(card.PlacementScale.Equals(new Vec3(1, 2.5, 1)));
            Assert.AreEqual(2.0, card.Height, 1e-6, "collider top at placement scale");
            Assert.IsTrue(card.PivotOffset.ApproximatelyEquals(new Vec3(0.1, 0, 0)), "collider bottom centre at scale 1");
            Assert.IsNull(card.Storeys);

            var building = new LevelCard
            {
                Name = "SM_Bld_Motel_01",
                Id = "Pack/SM_Bld_Motel_01.prefab",
                VisualBounds = new Box3(new Vec3(0, 4, 0), new Vec3(20, 8, 10)),
            };
            LevelCards.Classify(building, Vec3.One, null);
            Assert.AreEqual(LevelRole.Building, building.Role);
            Assert.AreEqual(3, building.Storeys);
            Assert.AreEqual(CoverLevel.Full, building.Cover);
        }

        [Test]
        public void OverridesWinOverDetection()
        {
            var overrides = LevelCatalogOverrides.Parse(@"{
              ""v"": 1,
              ""rules"": [
                {""match"": ""PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Barrier_*"", ""role"": ""cover_half"", ""placementScale"": [1, 0.8, 1]},
                {""match"": ""**/SM_Prop_Barrier_04.prefab"", ""placementScale"": 1.2}
              ],
              ""pieces"": {
                ""PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Barrier_02.prefab"": {""cover"": ""Full""},
                ""0123456789abcdef0123456789abcdef"": {""role"": ""cover_full"", ""note"": ""approved""}
              }
            }");
            Assert.AreEqual(2, overrides.RuleCount);
            Assert.AreEqual(2, overrides.PieceCount);

            var card = Barrier01();
            LevelCards.Classify(card, Vec3.One, overrides.Resolve("ffff", card.Id));
            Assert.AreEqual(LevelRole.CoverHalf, card.Role);
            Assert.AreEqual("override", card.RoleSource);
            Assert.AreEqual(CoverLevel.Half, card.Cover);
            Assert.AreEqual("override", card.ScaleSource);
            Assert.AreEqual(1.41 * 0.8, card.Height, 1e-6);

            var four = overrides.Resolve(null, "PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Barrier_04.prefab");
            Assert.AreEqual(LevelRole.CoverHalf, four.Role, "earlier rule fields survive");
            Assert.IsTrue(four.PlacementScale.Value.Equals(new Vec3(1.2, 1.2, 1.2)), "later rule wins, uniform scale");

            var two = overrides.Resolve(null, "PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Barrier_02.prefab");
            Assert.AreEqual(CoverLevel.Full, two.Cover, "piece entry by id");
            Assert.AreEqual(LevelRole.CoverHalf, two.Role);

            var byGuid = overrides.Resolve("0123456789abcdef0123456789abcdef", "PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Barrier_02.prefab");
            Assert.AreEqual(LevelRole.CoverFull, byGuid.Role, "GUID entry wins over the id entry and rules");
            Assert.AreEqual(CoverLevel.Full, byGuid.Cover);
            Assert.AreEqual("approved", byGuid.Note);

            Assert.IsNull(overrides.Resolve("none", "Other/SM_Prop_Barrier_01.prefab"), "'*' stays in one folder");
        }

        [TestCase(@"{""v"":2}", "unsupported version")]
        [TestCase(@"{""extra"":1}", "unknown key 'extra'")]
        [TestCase(@"{""rules"":[{""role"":""decor""}]}", "match")]
        [TestCase(@"{""rules"":[{""match"":""*"",""role"":""wall""}]}", "Unknown level role 'wall'")]
        [TestCase(@"{""pieces"":{""a"":{""cover"":""half""}}}", "Unknown cover 'half'")]
        [TestCase(@"{""pieces"":{""a"":{""placementScale"":[1,0,1]}}}", "positive")]
        [TestCase(@"{""pieces"":{""a"":{""placementScale"":[1,1]}}}", "Expected [x, y, z]")]
        [TestCase(@"{""pieces"":{""a"":{""scale"":2}}}", "unknown key 'scale'")]
        [TestCase(@"{""pieces"":{""a"":{""storeys"":0}}}", "at least 1")]
        [TestCase(@"{""pieces"":{""a"":{""lift"":""high""}}}", "lift must be a number")]
        [TestCase(@"{""pieces"":{""a"":{""footprint"":[2,0]}}}", "footprint sides must be positive")]
        [TestCase(@"{""pieces"":{""a"":{""footprint"":[2,2,2]}}}", "Expected [x, z]")]
        [TestCase(@"{""pieces"":", "LevelCatalogOverrides.json")]
        public void OverridesAreStrict(string json, string message)
        {
            var e = Assert.Throws<FormatException>(() => LevelCatalogOverrides.Parse(json));
            StringAssert.Contains(message, e.Message);
        }

        [Test]
        public void ExcludeAndStoreysOverride()
        {
            var overrides = LevelCatalogOverrides.Parse(@"{""pieces"":{""g1"":{""exclude"":true},""g2"":{""role"":""building"",""storeys"":2}}}");
            Assert.IsTrue(overrides.Resolve("g1", "x").Exclude == true);
            var card = new LevelCard { Name = "SM_Env_Tower_01", Id = "x", VisualBounds = new Box3(new Vec3(0, 6, 0), new Vec3(4, 12, 4)) };
            LevelCards.Classify(card, Vec3.One, overrides.Resolve("g2", "x"));
            Assert.AreEqual(LevelRole.Building, card.Role);
            Assert.AreEqual(2, card.Storeys);
        }

        // A Wasteland barricade pivoted at mid-height: mesh -0.92..+0.92 m. Without a lift it sinks half into
        // the floor and reads as 0.92 m half cover; lifted 0.75 m its top is 1.67 m above the floor.
        [Test]
        public void LiftRaisesThePivotAndCountsInHeightAndRole()
        {
            LevelCard Barricade() => new LevelCard
            {
                Name = "SM_Prop_Barricade_01",
                Id = "PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Barricade_01.prefab",
                VisualBounds = new Box3(new Vec3(0, 0, 0), new Vec3(1.73, 1.84, 0.22)),
            };
            var plain = Barricade();
            LevelCards.Classify(plain, Vec3.One, null);
            Assert.AreEqual(LevelRole.CoverHalf, plain.Role, "geometry, top 0.92 m above the pivot");
            Assert.AreEqual(0, plain.Lift);

            var overrides = LevelCatalogOverrides.Parse(@"{""rules"":[{""match"":""**/SM_Prop_Barricade_*"",""lift"":0.5}],
              ""pieces"":{""g1"":{""lift"":0.75}}}");
            var lifted = Barricade();
            LevelCards.Classify(lifted, Vec3.One, overrides.Resolve("g1", lifted.Id));
            Assert.AreEqual(0.75, lifted.Lift, 1e-9, "piece entry wins over the rule");
            Assert.AreEqual(0.92 + 0.75, lifted.Height, 1e-6);
            Assert.AreEqual(LevelRole.Unknown, lifted.Role, "1.67 m: between half and full cover");
            var footprint = LevelCards.Footprint(lifted, Cell);
            Assert.AreEqual(0.92 + 0.75, footprint.Height, 1e-6, "footprint height is above the floor");
            Assert.AreEqual(1.84, footprint.Size.Y, 1e-6, "size is not changed by the lift");
            Assert.AreEqual(0.0, footprint.Center.Y, 1e-6, "centre stays relative to the pivot");

            var back = LevelCards.FromJson("g1", LevelCards.ToJson(lifted));
            Assert.AreEqual(0.75, back.Lift, 1e-9);
            var old = LevelCards.ToJson(plain);
            old.Remove("lift");
            Assert.AreEqual(0, LevelCards.FromJson("g0", old).Lift, "catalogs written before lift read as 0");
        }

        // Wasteland SM_Prop_Tyre_Pile_02 at (0.9, 1, 0.9): 2.21 x 2.31 m, which rounds to 2 x 2 cells. The owner
        // wants it to claim one cell and overhang visually, so its override claims 2 x 2 m.
        [Test]
        public void FootprintOverrideClaimsCellsAndKeepsTheMeasuredSize()
        {
            LevelCard Pile() => new LevelCard
            {
                Name = "SM_Prop_Tyre_Pile_02",
                Id = "PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Tyre_Pile_02.prefab",
                VisualBounds = new Box3(new Vec3(0.1, 0.55, 0), new Vec3(2.46, 1.15, 2.57)),
            };
            var plain = Pile();
            var overrides = LevelCatalogOverrides.Parse(@"{""pieces"":{""g"":{""placementScale"":[0.9,1,0.9]}}}");
            LevelCards.Classify(plain, Vec3.One, overrides.Resolve("g", plain.Id));
            var measured = LevelCards.Footprint(plain, Cell);
            Assert.AreEqual(2, measured.Width);
            Assert.AreEqual(2, measured.Depth);
            Assert.IsFalse(measured.Overridden);

            overrides = LevelCatalogOverrides.Parse(@"{""pieces"":{""g"":{""placementScale"":[0.9,1,0.9],""footprint"":[2,2]}}}");
            var card = Pile();
            LevelCards.Classify(card, Vec3.One, overrides.Resolve("g", card.Id));
            Assert.IsTrue(card.FootprintOverride.Value.Equals(new FootprintSize(2, 2)));
            var claimed = LevelCards.Footprint(card, Cell);
            Assert.AreEqual(1, claimed.Width);
            Assert.AreEqual(1, claimed.Depth);
            Assert.IsTrue(claimed.Overridden);
            Assert.AreEqual(2.46 * 0.9, claimed.Size.X, 1e-6, "measured size is kept");
            Assert.AreEqual(2.0 - 2.46 * 0.9, claimed.FitErrorX, 1e-6, "negative fit error: the overhang");
            Assert.AreEqual(0.09, claimed.Center.X, 1e-6, "centre stays the bounds centre");
            Assert.AreEqual(2, LevelCards.Footprint(card, 1.0).Width, "the override is in meters, so 1 m cells give 2");
            Assert.AreEqual(new Cell(1, 1), claimed.RotatedSize(1));

            var back = LevelCards.FromJson("g", LevelCards.ToJson(card));
            Assert.IsTrue(back.FootprintOverride.Value.Equals(new FootprintSize(2, 2)));
            Assert.AreEqual(JTokenType.Null, LevelCards.ToJson(plain)["footprintOverride"].Type);
            Assert.IsNull(LevelCards.FromJson("g", LevelCards.ToJson(plain)).FootprintOverride);
        }

        [Test]
        public void CardJsonRoundTrips()
        {
            var card = WallSf();
            card.Guid = "215a18edfee7f4f2eaaa7fe488cea3ab";
            card.Pack = "NK";
            card.RoleSource = "override";
            card.Cover = CoverLevel.Full;
            card.PivotOffset = new Vec3(0, 0, 0);
            card.Height = 2.9;
            card.Thumbnail = "level-thumbs/x.png";
            var json = LevelCards.ToJson(card);
            Assert.AreEqual("cover_full", (string)json["levelRole"]);
            Assert.AreEqual("[1.65,2.0,1.5]", json["placementScale"].ToString(Newtonsoft.Json.Formatting.None));
            var catalog = new JObject { ["levelCatalogVersion"] = 1, ["cards"] = new JObject { [card.Guid] = json } };
            var back = LevelCards.ParseCatalog(catalog.ToString())[card.Guid];
            Assert.AreEqual(card.Guid, back.Guid);
            Assert.AreEqual(LevelRole.CoverFull, back.Role);
            Assert.AreEqual(CoverLevel.Full, back.Cover);
            Assert.IsTrue(back.RootRotation.Equals(new Vec3(0, 90, 0)));
            Assert.AreEqual("level-thumbs/x.png", back.Thumbnail);
            // Bounds are stored to the millimetre; the footprint survives the trip.
            Assert.AreEqual(Size(LevelCards.Footprint(card, Cell)), Size(LevelCards.Footprint(back, Cell)));

            var noCollider = Barrier01();
            noCollider.ColliderBounds = null;
            var json2 = LevelCards.ToJson(noCollider);
            Assert.AreEqual(JTokenType.Null, json2["colliderBounds"].Type);
            Assert.AreEqual(JTokenType.Null, json2["cover"].Type);
            Assert.IsNull(LevelCards.FromJson("g", json2).ColliderBounds);
        }

        [Test]
        public void RoleNamesRoundTrip()
        {
            foreach (LevelRole role in Enum.GetValues(typeof(LevelRole)))
                Assert.AreEqual(role, LevelCards.ParseRole(LevelCards.RoleName(role)));
            Assert.AreEqual("cover_half", LevelCards.RoleName(LevelRole.CoverHalf));
        }

        private static (int, int) Size(CardFootprint footprint) => (footprint.Width, footprint.Depth);
    }
}
