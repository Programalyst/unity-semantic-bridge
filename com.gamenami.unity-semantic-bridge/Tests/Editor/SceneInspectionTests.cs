using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // get_scene_hierarchy subtree queries (incl. rigs) and inspect_gameobject's component filter.
    public class SceneInspectionTests
    {
        GameObject character;

        [SetUp]
        public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // Character > Body (skinned) > BodyChild, and Character > Armature > Hips > Spine > Chest.
            character = new GameObject("Character");
            var body = new GameObject("Body", typeof(SkinnedMeshRenderer));
            body.transform.SetParent(character.transform);
            new GameObject("BodyChild").transform.SetParent(body.transform);
            var parent = character.transform;
            foreach (var bone in new[] { "Armature", "Hips", "Spine", "Chest" })
            {
                var go = new GameObject(bone);
                go.transform.SetParent(parent);
                parent = go.transform;
            }
            parent.gameObject.AddComponent<BoxCollider>();
        }

        [TearDown]
        public void Cleanup()
        {
            if (character != null) Object.DestroyImmediate(character);
        }

        static string Call(string method, JObject request)
        {
            request["method"] = method;
            var completion = new TaskCompletionSource<string>();
            McpMessageHandler.HandleMcpMessage(request, completion);
            Assert.IsTrue(completion.Task.IsCompleted);
            return completion.Task.Result;
        }

        string[] Paths(int depth, bool? pruneSkinned)
        {
            var request = new JObject
            {
                ["depth"] = depth, ["onlyMainCamVisible"] = false, ["rootInstanceId"] = character.GetInstanceID(),
            };
            if (pruneSkinned.HasValue) request["pruneSkinned"] = pruneSkinned.Value;
            var result = JObject.Parse(Call("get_scene_hierarchy", request));
            return result["nodes"].Select(n => n["path"].Value<string>()).ToArray();
        }

        [Test]
        public void RootedQueryAppliesDepthBelowTheRoot()
        {
            CollectionAssert.AreEqual(
                new[] { "Character", "Character/Armature", "Character/Armature/Hips", "Character/Armature/Hips/Spine" },
                Paths(3, null));
        }

        [Test]
        public void PruneSkinnedFalseWalksTheWholeRig()
        {
            CollectionAssert.AreEqual(
                new[]
                {
                    "Character", "Character/Body", "Character/Body/BodyChild", "Character/Armature",
                    "Character/Armature/Hips", "Character/Armature/Hips/Spine", "Character/Armature/Hips/Spine/Chest",
                },
                Paths(10, false));
        }

        [Test]
        public void InspectGameObjectFiltersToOneComponent()
        {
            var chest = character.GetComponentInChildren<BoxCollider>().gameObject;
            var all = Call("inspect_gameobject", new JObject { ["instanceId"] = chest.GetInstanceID() });
            StringAssert.Contains("[Component: Transform]", all);
            StringAssert.Contains("[Component: BoxCollider]", all);

            var one = Call("inspect_gameobject", new JObject { ["instanceId"] = chest.GetInstanceID(), ["componentName"] = "BoxCollider" });
            StringAssert.StartsWith("--- Inspector: Chest > BoxCollider ---", one);
            StringAssert.DoesNotContain("Transform", one.Split('\n')[0]);
            StringAssert.Contains("m_Size", one);

            var missing = Call("inspect_gameobject", new JObject { ["instanceId"] = chest.GetInstanceID(), ["componentName"] = "Rigidbody" });
            StringAssert.StartsWith("Error:", missing);
        }
    }
}
