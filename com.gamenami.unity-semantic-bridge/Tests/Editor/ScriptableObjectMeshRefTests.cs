using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // Scratch ScriptableObject so the Mesh-ref tests run in any project.
    public class MeshRefProbeSo : ScriptableObject
    {
        public Mesh mesh;
    }

    /// <summary>
    /// Regression tests for the Mesh fileID bug: setting {fileID, guid} on a
    /// Mesh field must round-trip (previously the bare-guid branch won and the
    /// ref zeroed to fileID: 0), and an unknown fileID must throw, not zero.
    /// Run via Window > General > Test Runner > EditMode.
    /// </summary>
    public class ScriptableObjectMeshRefTests
    {
        private const string ScratchDir = "Assets/__BridgeTests__";
        private const string RoundTripPath = ScratchDir + "/MeshRefProbe.asset";
        private const string UnknownFileIdPath = ScratchDir + "/MeshRefProbeBad.asset";

        [TearDown]
        public void DeleteScratchAssets()
        {
            if (AssetDatabase.LoadAssetAtPath<Object>(RoundTripPath) != null)
                AssetDatabase.DeleteAsset(RoundTripPath);
            if (AssetDatabase.LoadAssetAtPath<Object>(UnknownFileIdPath) != null)
                AssetDatabase.DeleteAsset(UnknownFileIdPath);
        }

        // Resolves a real (guid, fileID) Mesh pair from the host project.
        private static bool TryFindMesh(out string guid, out long fileId)
        {
            guid = null;
            fileId = 0;
            foreach (var g in AssetDatabase.FindAssets("t:Mesh"))
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (o is Mesh && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out _, out var lid))
                    {
                        guid = g;
                        fileId = lid;
                        return true;
                    }
                }
            }
            return false;
        }

        [Test]
        public void MeshFileIdRoundTrips()
        {
            if (!TryFindMesh(out var guid, out var fileId))
                Assert.Inconclusive("No Mesh asset found in this project.");

            var msg = new JObject
            {
                ["type"] = typeof(MeshRefProbeSo).FullName,
                ["path"] = RoundTripPath,
                ["confirm"] = true,
                ["fields"] = new JObject
                {
                    ["mesh"] = new JObject { ["fileID"] = fileId, ["guid"] = guid },
                },
            };
            var result = ScriptableObjectFunctions.CreateScriptableObject(msg);
            Assert.IsFalse(result.StartsWith("Error"), $"Create failed: {result}");

            var so = AssetDatabase.LoadAssetAtPath<MeshRefProbeSo>(RoundTripPath);
            Assert.IsNotNull(so);
            Assert.IsNotNull(so.mesh, "Mesh ref was zeroed (fileID: 0).");
            Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(so.mesh, out var actualGuid, out var actualFileId));
            Assert.AreEqual(guid, actualGuid);
            Assert.AreEqual(fileId, actualFileId);
        }

        [Test]
        public void UnknownMeshFileIdThrows()
        {
            if (!TryFindMesh(out var guid, out _))
                Assert.Inconclusive("No Mesh asset found in this project.");

            var msg = new JObject
            {
                ["type"] = typeof(MeshRefProbeSo).FullName,
                ["path"] = UnknownFileIdPath,
                ["confirm"] = true,
                ["fields"] = new JObject
                {
                    // Valid guid, fileID that exists nowhere in the file.
                    ["mesh"] = new JObject { ["fileID"] = 9191953123456789L, ["guid"] = guid },
                },
            };
            var result = ScriptableObjectFunctions.CreateScriptableObject(msg);
            StringAssert.StartsWith("Error:", result);
            StringAssert.Contains("not found", result);
            // Must not leave a zeroed asset behind.
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Object>(UnknownFileIdPath));
        }
    }
}
