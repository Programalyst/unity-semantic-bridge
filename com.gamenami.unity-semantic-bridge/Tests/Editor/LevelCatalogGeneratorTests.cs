using System;
using System.Collections.Generic;
using System.IO;
using Gamenami.UnitySemanticBridge.Layout;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // Level cards measured on real prefabs (LoadPrefabContents), in a scratch folder.
    public class LevelCatalogGeneratorTests
    {
        private string folder;
        private string catalogDir;

        [SetUp]
        public void SetUp()
        {
            folder = "Assets/__LevelCatalog_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            catalogDir = Path.Combine(Path.GetTempPath(), "usb-level-catalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(catalogDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (folder != null) AssetDatabase.DeleteAsset(folder);
            if (catalogDir != null && Directory.Exists(catalogDir)) Directory.Delete(catalogDir, true);
        }

        // A Wall SF-like prefab: root scale (1.65, 2, 1.5) turned 90°, one cube child 2 x 1.5 x 0.5 m
        // standing on the pivot, with its box collider.
        private string MakeWall(string name, Vector3 childEuler = default)
        {
            var root = new GameObject(name);
            root.transform.localScale = new Vector3(1.65f, 2f, 1.5f);
            root.transform.localRotation = Quaternion.Euler(0, 90, 0);
            root.transform.position = new Vector3(5, 1, 7); // placement never counts
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(root.transform, false);
            cube.transform.localPosition = new Vector3(0, 0.75f, 0);
            cube.transform.localRotation = Quaternion.Euler(childEuler);
            cube.transform.localScale = new Vector3(2f, 1.5f, 0.5f);
            return Save(root);
        }

        private string Save(GameObject root)
        {
            var path = $"{folder}/{root.name}.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            return path;
        }

        [Test]
        public void MeasuresInRootSpaceAtScaleOne()
        {
            var path = MakeWall("SM_Test_Wall_01");
            var card = LevelCatalogGenerator.BuildCard(path, null, out var excluded);
            Assert.IsFalse(excluded);
            Assert.IsNotNull(card);
            Assert.AreEqual(AssetDatabase.AssetPathToGUID(path), card.Guid);
            Assert.IsTrue(card.VisualBounds.Size.ApproximatelyEquals(new Vec3(2, 1.5, 0.5)), card.VisualBounds.ToString());
            Assert.IsTrue(card.VisualBounds.Center.ApproximatelyEquals(new Vec3(0, 0.75, 0)), card.VisualBounds.ToString());
            Assert.IsTrue(card.ColliderBounds.Value.Size.ApproximatelyEquals(new Vec3(2, 1.5, 0.5)));
            Assert.IsTrue(card.RootRotation.ApproximatelyEquals(new Vec3(0, 90, 0)), card.RootRotation.ToString());
            Assert.IsTrue(card.PlacementScale.ApproximatelyEquals(new Vec3(1.65, 2, 1.5)), "defaults to the root's own scale");
            Assert.AreEqual("prefab", card.ScaleSource);
            Assert.IsFalse(card.Skewed);
            Assert.AreEqual(LevelRole.CoverFull, card.Role, "Wall, 3 m tall at placement scale");
            Assert.AreEqual(3.0, card.Height, 1e-4);

            var footprint = LevelCards.Footprint(card, 2.0);
            Assert.AreEqual((1, 2), (footprint.Width, footprint.Depth));
            Assert.AreEqual(4 - 3.3, footprint.FitErrorZ, 1e-4);
        }

        [Test]
        public void FlagsSkewedChildrenButNotQuarterTurns()
        {
            var quarter = LevelCatalogGenerator.BuildCard(MakeWall("SM_Test_Wall_Quarter", new Vector3(-90, 180, 0)), null, out _);
            Assert.IsFalse(quarter.Skewed);
            var skewed = LevelCatalogGenerator.BuildCard(MakeWall("SM_Test_Wall_Skewed", new Vector3(0, 30, 0)), null, out _);
            Assert.IsTrue(skewed.Skewed);
            Assert.IsTrue(LevelCards.Footprint(skewed, 2.0).Approximate);
        }

        [Test]
        public void ChildScalesCountAndCollidersAreOptional()
        {
            var root = new GameObject("SM_Prop_Crate_01");
            var child = new GameObject("Mesh");
            child.transform.SetParent(root.transform, false);
            child.transform.localScale = new Vector3(2, 2, 2);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.DestroyImmediate(cube.GetComponent<BoxCollider>());
            cube.transform.SetParent(child.transform, false);
            cube.transform.localPosition = new Vector3(0, 0.25f, 0);
            cube.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
            var card = LevelCatalogGenerator.BuildCard(Save(root), null, out _);
            Assert.IsNull(card.ColliderBounds);
            Assert.IsTrue(card.VisualBounds.Size.ApproximatelyEquals(new Vec3(1, 1, 1)), card.VisualBounds.ToString());
            Assert.AreEqual(LevelRole.CoverHalf, card.Role);
            Assert.AreEqual("geometry", card.RoleSource);
        }

        [Test]
        public void SkipsCharactersAndEmptyPrefabs()
        {
            var character = new GameObject("SM_Chr_Raider_01");
            var body = new GameObject("Body");
            body.transform.SetParent(character.transform, false);
            body.AddComponent<SkinnedMeshRenderer>();
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(character.transform, false);
            Assert.IsNull(LevelCatalogGenerator.BuildCard(Save(character), null, out _));
            Assert.IsNull(LevelCatalogGenerator.BuildCard(Save(new GameObject("FX_Empty")), null, out _));
        }

        [Test]
        public void OverridesApplyByGuidAndCanExclude()
        {
            var path = MakeWall("SM_Test_Wall_02");
            var guid = AssetDatabase.AssetPathToGUID(path);
            var overrides = LevelCatalogOverrides.Parse(
                "{\"pieces\":{\"" + guid + "\":{\"role\":\"cover_half\",\"placementScale\":[1.65,0.7,1.5]}}}");
            var card = LevelCatalogGenerator.BuildCard(path, overrides, out _);
            Assert.AreEqual(LevelRole.CoverHalf, card.Role);
            Assert.AreEqual("override", card.ScaleSource);
            Assert.AreEqual(1.05, card.Height, 1e-4);

            var exclude = LevelCatalogOverrides.Parse("{\"pieces\":{\"" + guid + "\":{\"exclude\":true}}}");
            Assert.IsNull(LevelCatalogGenerator.BuildCard(path, exclude, out var excluded));
            Assert.IsTrue(excluded);
        }

        [Test]
        public void WritesCatalogKeyedByGuidAndDropsExcluded()
        {
            var a = MakeWall("SM_Test_Wall_A");
            var b = MakeWall("SM_Test_Wall_B");
            var records = new List<JObject>
            {
                LevelCatalogGenerator.BuildRecord(a, null, out _),
                LevelCatalogGenerator.BuildRecord(b, null, out _),
            };
            var result = LevelCatalogGenerator.MergeAndWrite(catalogDir, records, null);
            Assert.AreEqual(2, (int)result["itemCount"]);
            Assert.AreEqual(2, (int)result["roles"]["cover_full"]);
            var text = File.ReadAllText(Path.Combine(catalogDir, LevelCatalogGenerator.CatalogFileName));
            var cards = LevelCards.ParseCatalog(text);
            var guidA = AssetDatabase.AssetPathToGUID(a);
            Assert.IsTrue(cards.ContainsKey(guidA));
            StringAssert.DoesNotContain("assetPath", text);
            Assert.AreEqual((1, 2), (LevelCards.Footprint(cards[guidA], 2.0).Width, LevelCards.Footprint(cards[guidA], 2.0).Depth));

            // A re-run upserts and removes excluded GUIDs.
            var again = LevelCatalogGenerator.MergeAndWrite(catalogDir, new List<JObject>(), new[] { guidA });
            Assert.AreEqual(1, (int)again["itemCount"]);
            Assert.IsFalse(LevelCards.ParseCatalog(File.ReadAllText(Path.Combine(catalogDir, LevelCatalogGenerator.CatalogFileName))).ContainsKey(guidA));
        }

        [TestCase("Assets/Synty/PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Barrier_01.prefab", "PolygonApocalypseWasteland", "PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Barrier_01.prefab")]
        [TestCase("Assets/3rd Party/PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Barrier_01.prefab", "PolygonApocalypseWasteland", "PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Barrier_01.prefab")]
        [TestCase("Assets/Prefabs/Structures/Wall SF/Wall SF.prefab", "Prefabs", "Prefabs/Structures/Wall SF/Wall SF.prefab")]
        [TestCase("Assets/Wall.prefab", "Assets", "Wall.prefab")]
        public void IdsDoNotDependOnTheScanRoot(string path, string pack, string id)
        {
            Assert.AreEqual(pack, LevelCatalogGenerator.PackAndId(path, out var actual));
            Assert.AreEqual(id, actual);
        }

        [Test]
        public void EulerMatchesUnityQuaternions()
        {
            var random = new System.Random(7);
            for (var i = 0; i < 50; i++)
            {
                var euler = new Vector3(random.Next(0, 360), random.Next(0, 360), random.Next(0, 360));
                var point = new Vector3((float)random.NextDouble() * 4 - 2, (float)random.NextDouble() * 4 - 2, (float)random.NextDouble() * 4 - 2);
                var expected = Quaternion.Euler(euler) * point;
                var actual = LevelCards.Rotate(new Vec3(euler.x, euler.y, euler.z), new Vec3(point.x, point.y, point.z));
                Assert.IsTrue(actual.ApproximatelyEquals(new Vec3(expected.x, expected.y, expected.z), 1e-4), $"{euler} {point}: {actual} vs {expected}");
            }
        }
    }
}
