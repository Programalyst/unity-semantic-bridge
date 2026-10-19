using System;
using System.IO;
using System.Linq;
using Gamenami.UnitySemanticBridge.Editor.Layout;
using Gamenami.UnitySemanticBridge.Layout;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // query_level_catalog: reviewed pieces (overrides file + prefabs, NK's case) and a generated
    // level-catalog.json (Asset Preview's), filters, paging and the source header.
    public class LevelCatalogQueryTests
    {
        private string folder;
        private string catalogDir;

        [SetUp]
        public void SetUp()
        {
            folder = "Assets/__CatalogQuery_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            catalogDir = Path.Combine(Path.GetTempPath(), "usb-catalog-query-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(catalogDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (folder != null) AssetDatabase.DeleteAsset(folder);
            if (catalogDir != null && Directory.Exists(catalogDir)) Directory.Delete(catalogDir, true);
        }

        private string MakeBox(string name, Vector3 size)
        {
            var root = new GameObject(name);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(root.transform, false);
            cube.transform.localPosition = new Vector3(0, size.y * 0.5f, 0);
            cube.transform.localScale = size;
            var path = $"{folder}/{name}.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return path;
        }

        private static string[] Rows(string reply) =>
            reply.Split('\n').SkipWhile(l => !l.StartsWith("guid\t")).Skip(1).TakeWhile(l => !l.StartsWith("Problems")).ToArray();

        private void WriteOverrides(string pieces) =>
            File.WriteAllText(Path.Combine(catalogDir, LevelCatalogOverrides.FileName), "{\"v\":1,\"pieces\":{" + pieces + "}}");

        [Test]
        public void ReviewedPiecesAreListedByGuidOrIdWithProblems()
        {
            var sandbags = MakeBox("SM_Test_Sandbags", new Vector3(3.9f, 1f, 0.8f));
            var wall = MakeBox("SM_Test_Wall", new Vector3(1.9f, 2.5f, 0.4f));
            MakeBox("SM_Test_Unreviewed", Vector3.one);
            LevelCatalogGenerator.PackAndId(wall, out var wallId);
            WriteOverrides($"\"{AssetDatabase.AssetPathToGUID(sandbags)}\":{{\"role\":\"cover_half\"}}," +
                           $"\"{wallId}\":{{\"role\":\"cover_full\",\"placementScale\":[1,1.2,1]}}," +
                           "\"0123456789abcdef0123456789abcdef\":{\"role\":\"decor\"}");

            var reply = LevelCatalogQuery.Run(new JObject { ["cell_size"] = 2.0 }, catalogDir);
            StringAssert.StartsWith($"Source: reviewed pieces, the per-piece entries of {Path.Combine(catalogDir, LevelCatalogOverrides.FileName)} (3 entries)", reply);
            StringAssert.Contains("2 of 2 match (rows 1-2)", reply);
            var rows = Rows(reply);
            Assert.AreEqual(2, rows.Length, reply);
            var sandbagRow = rows.Single(r => r.Contains("SM_Test_Sandbags")).Split('\t');
            Assert.AreEqual(AssetDatabase.AssetPathToGUID(sandbags), sandbagRow[0]);
            CollectionAssert.AreEqual(new[] { "cover_half", "Half", "2x1", "1", "1" }, sandbagRow.Skip(1).Take(5));
            Assert.AreEqual(sandbags, sandbagRow[7], "reviewed rows carry the prefab path");
            var wallRow = rows.Single(r => r.Contains("SM_Test_Wall")).Split('\t');
            CollectionAssert.AreEqual(new[] { "cover_full", "Full", "1x1", "3", "1,1.2,1" }, wallRow.Skip(1).Take(5));
            StringAssert.Contains("MISSING 0123456789abcdef0123456789abcdef: no prefab in this project", reply);
            StringAssert.DoesNotContain("Unreviewed", reply);
        }

        [Test]
        public void FiltersByRoleCoverFootprintAndNameAndPages()
        {
            var sandbags = MakeBox("SM_Test_Sandbags", new Vector3(3.9f, 1f, 0.8f));
            var crate = MakeBox("SM_Test_Crate", new Vector3(1.5f, 1f, 1.5f));
            var wall = MakeBox("SM_Test_Wall", new Vector3(1.9f, 2.5f, 0.4f));
            WriteOverrides($"\"{AssetDatabase.AssetPathToGUID(sandbags)}\":{{\"role\":\"cover_half\"}}," +
                           $"\"{AssetDatabase.AssetPathToGUID(crate)}\":{{\"role\":\"cover_half\"}}," +
                           $"\"{AssetDatabase.AssetPathToGUID(wall)}\":{{\"role\":\"cover_full\"}}");
            string[] Ids(JObject args)
            {
                args["cell_size"] = 2.0;
                return Rows(LevelCatalogQuery.Run(args, catalogDir)).Select(r => Path.GetFileNameWithoutExtension(r.Split('\t')[6])).ToArray();
            }

            CollectionAssert.AreEqual(new[] { "SM_Test_Crate", "SM_Test_Sandbags" }, Ids(new JObject { ["role"] = new JArray("cover_half") }));
            CollectionAssert.AreEqual(new[] { "SM_Test_Wall" }, Ids(new JObject { ["cover"] = "Full" }));
            CollectionAssert.AreEqual(new[] { "SM_Test_Crate", "SM_Test_Wall" }, Ids(new JObject { ["max_cells"] = new JArray(1, 1) }));
            CollectionAssert.AreEqual(new[] { "SM_Test_Sandbags" }, Ids(new JObject { ["min_cells"] = new JArray(1, 2) }), "either orientation");
            CollectionAssert.AreEqual(new[] { "SM_Test_Sandbags" }, Ids(new JObject { ["name"] = "sandbag" }));

            var page = LevelCatalogQuery.Run(new JObject { ["cell_size"] = 2.0, ["limit"] = 2, ["offset"] = 1 }, catalogDir);
            StringAssert.Contains("3 of 3 match (rows 2-3)", page);
            Assert.AreEqual(2, Rows(page).Length);
            StringAssert.Contains("More: offset=", LevelCatalogQuery.Run(new JObject { ["cell_size"] = 2.0, ["limit"] = 1 }, catalogDir));
            Assert.Throws<BridgeToolException>(() => LevelCatalogQuery.Run(new JObject { ["role"] = "wall" }, catalogDir));
            Assert.Throws<BridgeToolException>(() => LevelCatalogQuery.Run(new JObject { ["max_cells"] = new JArray(1) }, catalogDir));
        }

        [Test]
        public void ReadsAGeneratedCatalogFirstAndSaysWhenThereIsNone()
        {
            StringAssert.StartsWith("No level catalog here", LevelCatalogQuery.Run(new JObject(), catalogDir));

            var path = MakeBox("SM_Test_Sandbags", new Vector3(3.9f, 1f, 0.8f));
            var card = LevelCatalogGenerator.BuildCard(path, LevelCatalogOverrides.Empty, out _);
            var catalog = new JObject { ["levelCatalogVersion"] = 1, ["cards"] = new JObject { [card.Guid] = LevelCards.ToJson(card) } };
            File.WriteAllText(Path.Combine(catalogDir, LevelCatalogGenerator.CatalogFileName), catalog.ToString());
            WriteOverrides("");

            var reply = LevelCatalogQuery.Run(new JObject { ["cell_size"] = 2.0 }, catalogDir);
            StringAssert.StartsWith($"Source: level catalog {Path.Combine(catalogDir, LevelCatalogGenerator.CatalogFileName)} (1 cards).", reply);
            var row = Rows(reply).Single().Split('\t');
            Assert.AreEqual(card.Guid, row[0]);
            Assert.AreEqual(7, row.Length, "catalog rows have no prefab path column");
            StringAssert.StartsWith("Source: reviewed pieces", LevelCatalogQuery.Run(new JObject { ["source"] = "reviewed", ["cell_size"] = 2.0 }, catalogDir));
        }
    }
}
