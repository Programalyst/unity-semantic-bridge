using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    public class SemanticBatchTests
    {
        string folder, prefabPath;
        GameObject root;
        Scene scene;

        [SetUp]
        public void Setup()
        {
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            folder = "Assets/UsbBatchTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            root = new GameObject("Rig");
            for (int i = 0; i < 10; i++)
            {
                var child = new GameObject("Joint"); child.transform.SetParent(root.transform);
                var j = child.AddComponent<CharacterJoint>();
                j.axis = new Vector3(0.4568338f, -0.0000002948218f, 0.00000009464566f);
                j.swingAxis = Vector3.up; j.autoConfigureConnectedAnchor = false;
                j.connectedAnchor = new Vector3(0.15248686f, 0.43523058f, -0.03266224f);
            }
            prefabPath = folder + "/Rig.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        }
        [TearDown]
        public void Cleanup()
        {
            Undo.ClearAll();
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            AssetDatabase.DeleteAsset(folder);
        }
        static JObject Dispatch(string method, JObject request)
        {
            request["method"] = method;
            var completion = new TaskCompletionSource<string>();
            McpMessageHandler.HandleMcpMessage(request, completion);
            Assert.IsTrue(completion.Task.IsCompleted);
            return JObject.Parse(completion.Task.Result);
        }
        static JObject Limits(int ms = 1000) => new JObject { ["maxMilliseconds"] = ms };
        static string Reference(UnityEngine.Object obj)
        {
            var r = Dispatch("acquire_editor_references", new JObject { ["instanceIds"] = new JArray(obj.GetInstanceID()), ["limits"] = Limits() });
            return r["results"][0]["object"]["reference"].Value<string>();
        }
        static JObject InspectOp(string id, string reference, params string[] fields) => new JObject {
            ["id"] = id, ["op"] = "inspect", ["target"] = reference, ["properties"] = new JArray(fields)
        };
        static JObject EditOp(string id, string reference, JObject fields, JObject expected = null)
        {
            var o = new JObject { ["id"] = id, ["op"] = "set_fields", ["target"] = reference, ["fields"] = fields };
            if (expected != null) o["expected"] = expected;
            return o;
        }
        static JObject Run(bool dry, params JObject[] ops) => Dispatch("semantic_batch", new JObject {
            ["operations"] = new JArray(ops), ["dryRun"] = dry, ["limits"] = Limits()
        });
        JObject Prefabs(JObject limits = null, params string[] paths) => Dispatch("inspect_prefab_assets", new JObject {
            ["paths"] = new JArray(paths.Length == 0 ? new[] { prefabPath } : paths), ["component"] = "UnityEngine.CharacterJoint",
            ["properties"] = new JArray("m_Axis", "m_SwingAxis", "m_ConnectedAnchor"), ["limits"] = limits ?? Limits()
        });

        [Test]
        public void PrefabInspectionRetainsPrecisionAndIsReadOnly()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var before = File.ReadAllBytes(prefabPath); var meta = File.ReadAllBytes(prefabPath + ".meta");
            var selection = Selection.instanceIDs; bool dirty = scene.isDirty, assetDirty = EditorUtility.IsDirty(asset);
            int roots = scene.rootCount, scenes = SceneManager.sceneCount, undo = Undo.GetCurrentGroup();
            var r = Prefabs();
            Assert.IsFalse(r["truncated"].Value<bool>());
            var items = (JArray)r["results"][0]["objects"]; Assert.AreEqual(10, items.Count);
            double x = items[0]["properties"]["m_Axis"]["value"]["x"].Value<double>();
            Assert.AreEqual((double)0.4568338f, x);
            Assert.AreEqual((double)(-0.0000002948218f), items[0]["properties"]["m_Axis"]["value"]["y"].Value<double>());
            Assert.IsNull(items[0]["component"]["instanceId"]);
            Assert.IsNull(items[0]["component"]["reference"]);
            Assert.AreNotEqual(items[0]["object"]["hierarchyPath"].Value<string>(), items[1]["object"]["hierarchyPath"].Value<string>());
            CollectionAssert.AreEqual(before, File.ReadAllBytes(prefabPath)); CollectionAssert.AreEqual(meta, File.ReadAllBytes(prefabPath + ".meta"));
            CollectionAssert.AreEqual(selection, Selection.instanceIDs);
            Assert.AreEqual(dirty, scene.isDirty); Assert.AreEqual(assetDirty, EditorUtility.IsDirty(asset));
            Assert.AreEqual(roots, scene.rootCount); Assert.AreEqual(scenes, SceneManager.sceneCount); Assert.AreEqual(undo, Undo.GetCurrentGroup());
        }
        [Test]
        public void VariantsNestedPrefabsAndMultipleComponentsAreResolved()
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
            instance.GetComponentInChildren<CharacterJoint>().axis = Vector3.left;
            var extra = instance.transform.GetChild(0).gameObject.AddComponent<CharacterJoint>(); extra.axis = Vector3.up;
            string variant = folder + "/Variant.prefab"; PrefabUtility.SaveAsPrefabAsset(instance, variant);
            UnityEngine.Object.DestroyImmediate(instance);
            var container = new GameObject("Container");
            var nested = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(variant));
            nested.transform.SetParent(container.transform);
            string outer = folder + "/Outer.prefab"; PrefabUtility.SaveAsPrefabAsset(container, outer);
            UnityEngine.Object.DestroyImmediate(container);
            var r = Prefabs(null, variant, outer);
            foreach (var result in r["results"])
            {
                Assert.AreEqual("ok", result["status"].Value<string>());
                var items = (JArray)result["objects"]; Assert.AreEqual(11, items.Count);
                Assert.AreEqual(-1, items[0]["properties"]["m_Axis"]["value"]["x"].Value<double>());
                Assert.AreEqual(1, items[1]["componentIndex"].Value<int>());
            }
        }
        [Test]
        public void MissingPrefabAndPropertyDoNotStopOtherReadOperations()
        {
            var r = Prefabs(null, folder + "/Missing.prefab", prefabPath);
            Assert.AreEqual("error", r["results"][0]["status"].Value<string>()); Assert.AreEqual("ok", r["results"][1]["status"].Value<string>());
            var token = Reference(root.transform);
            r = Run(false, InspectOp("bad", token, "missing"), InspectOp("good", token, "m_LocalPosition"));
            Assert.AreEqual("error", r["results"][0]["status"].Value<string>()); Assert.AreEqual("ok", r["results"][1]["status"].Value<string>());
        }
        [Test]
        public void DryRunAndFailedPreflightHaveNoSideEffects()
        {
            var token = Reference(root.transform); var before = root.transform.localPosition;
            var good = EditOp("good", token, new JObject { ["m_LocalPosition"] = new JObject { ["x"] = 1, ["y"] = 2, ["z"] = 3 } });
            var bad = EditOp("bad", token, new JObject { ["m_LocalPosition"] = "wrong" });
            int undo = Undo.GetCurrentGroup(); bool dirty = scene.isDirty;
            Assert.AreEqual("validated", Run(true, good)["results"][0]["status"].Value<string>());
            Assert.AreEqual(before, root.transform.localPosition); Assert.AreEqual(undo, Undo.GetCurrentGroup()); Assert.AreEqual(dirty, scene.isDirty);
            var r = Run(false, good, bad);
            Assert.AreEqual("skipped", r["results"][0]["status"].Value<string>()); Assert.AreEqual("error", r["results"][1]["status"].Value<string>());
            Assert.AreEqual(before, root.transform.localPosition); Assert.AreEqual(undo, Undo.GetCurrentGroup());
        }
        [Test]
        public void UndoRedoAndPrefabOverridesUseOneGroup()
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
            var t = instance.transform; var token = Reference(t); var before = t.localPosition;
            var r = Run(false,
                EditOp("position", token, new JObject { ["m_LocalPosition"] = new JObject { ["x"] = 1, ["y"] = 2, ["z"] = 3 } }),
                EditOp("scale", token, new JObject { ["m_LocalScale"] = new JObject { ["x"] = 2, ["y"] = 2, ["z"] = 2 } }));
            Assert.AreEqual(2, r["appliedOperations"].Value<int>()); Assert.AreEqual(new Vector3(1, 2, 3), t.localPosition);
            Assert.IsTrue(PrefabUtility.HasPrefabInstanceAnyOverrides(instance, false)); Assert.IsTrue(scene.isDirty);
            Undo.PerformUndo(); Assert.AreEqual(before, t.localPosition); Assert.AreEqual(Vector3.one, t.localScale);
            Undo.PerformRedo(); Assert.AreEqual(new Vector3(1, 2, 3), t.localPosition); Assert.AreEqual(Vector3.one * 2, t.localScale);
            UnityEngine.Object.DestroyImmediate(instance);
        }
        [Test]
        public void InterveningChangesFailPreconditionsAndSkipRemainder()
        {
            var token = Reference(root.transform); var zero = new JObject { ["x"] = 0, ["y"] = 0, ["z"] = 0 };
            var expected = new JObject { ["m_LocalPosition"] = zero.DeepClone() };
            var one = new JObject { ["x"] = 1, ["y"] = 0, ["z"] = 0 };
            // Both operations validate against initial state. The second must revalidate
            // after the first edit; a human edit before execution has the same comparison path.
            var r = Run(false,
                EditOp("first", token, new JObject { ["m_LocalPosition"] = one }, expected),
                EditOp("second", token, new JObject { ["m_LocalPosition"] = zero }, expected),
                EditOp("third", token, new JObject { ["m_LocalScale"] = one }));
            Assert.IsTrue(r["partialApplication"].Value<bool>());
            CollectionAssert.AreEqual(new[] { "applied", "error", "skipped" }, r["results"].Select(x => x["status"].Value<string>()).ToArray());
            root.transform.localPosition = Vector3.right * 2;
            r = Run(false, EditOp("human", token, new JObject { ["m_LocalPosition"] = zero }, expected));
            Assert.AreEqual("error", r["results"][0]["status"].Value<string>()); Assert.AreEqual(Vector3.right * 2, root.transform.localPosition);
        }
        [Test]
        public void DeletedAndReloadInvalidatedReferencesAreRejected()
        {
            var other = new GameObject("Other"); string token = Reference(other.transform);
            UnityEngine.Object.DestroyImmediate(other);
            Assert.AreEqual("error", Run(false, InspectOp("deleted", token, "m_LocalPosition"))["results"][0]["status"].Value<string>());
            token = Reference(root.transform);
            typeof(SemanticBatch).GetMethod("Invalidate", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            Assert.AreEqual("error", Run(false, InspectOp("reload", token, "m_LocalPosition"))["results"][0]["status"].Value<string>());
        }
        [Test]
        public void ExplicitLimitsAndAllowlistAreEnforced()
        {
            var r = Prefabs(new JObject { ["maxTargets"] = 2, ["maxMilliseconds"] = 1000 });
            Assert.IsTrue(r["truncated"].Value<bool>()); Assert.AreEqual(2, ((JArray)r["results"][0]["objects"]).Count);
            Assert.AreEqual("truncated", r["results"][0]["status"].Value<string>());
            r = Prefabs(new JObject { ["maxResponseBytes"] = 8192, ["maxMilliseconds"] = 1000 });
            Assert.IsTrue(r["truncated"].Value<bool>()); Assert.LessOrEqual(System.Text.Encoding.UTF8.GetByteCount(r.ToString(Newtonsoft.Json.Formatting.None)), 8192);
            var token = Reference(root.transform); var op = InspectOp("bad", token, "m_LocalPosition"); op["op"] = "delete_gameobject";
            Assert.AreEqual("error", Run(false, op)["status"].Value<string>());
            var many = Enumerable.Range(0, 65).Select(i => InspectOp(i.ToString(), token, "m_LocalPosition")).ToArray();
            Assert.AreEqual("error", Run(false, many)["status"].Value<string>());
        }
        [Test]
        public void CollectionLimitAndSceneChangesRejectUnsafeRequests()
        {
            var token = Reference(root.transform);
            var operation = EditOp("collection", token, new JObject { ["m_LocalPosition"] = new JObject { ["x"] = 1, ["y"] = 0, ["z"] = 0 } });
            var r = Dispatch("semantic_batch", new JObject { ["operations"] = new JArray(operation), ["dryRun"] = true, ["limits"] = new JObject { ["maxValues"] = 1, ["maxMilliseconds"] = 1000 } });
            Assert.IsTrue(r["truncated"].Value<bool>()); Assert.AreEqual(Vector3.zero, root.transform.localPosition);
            EditorSceneManager.SaveScene(scene, folder + "/Scene.unity");
            var additive = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            r = Run(false, InspectOp("stale", token, "m_LocalPosition"));
            Assert.AreEqual("error", r["results"][0]["status"].Value<string>());
            EditorSceneManager.CloseScene(additive, true);
        }
        [Test]
        public void ArraysAndTypedObjectReferencesValidateWithoutApplying()
        {
            var receiver = root.AddComponent<Gamenami.UnitySemanticBridge.Tests.UiAcceptanceReceiver>();
            var buttonGo = new GameObject("Button", typeof(RectTransform), typeof(UnityEngine.UI.Button));
            buttonGo.transform.SetParent(root.transform);
            var button = buttonGo.GetComponent<UnityEngine.UI.Button>();
            var receiverToken = Reference(receiver); var buttonToken = Reference(button);
            receiver.movementButtons = new[] { button, button, button };
            var read = Dispatch("semantic_batch", new JObject { ["operations"] = new JArray(InspectOp("array", receiverToken, "movementButtons")), ["limits"] = new JObject { ["maxCollection"] = 2, ["maxMilliseconds"] = 1000 } });
            Assert.AreEqual("truncated", read["results"][0]["status"].Value<string>());
            Assert.IsTrue(read["truncated"].Value<bool>());
            var fields = new JObject { ["movementButtons"] = new JArray(new JObject { ["reference"] = buttonToken }) };
            var dry = Run(true, EditOp("array", receiverToken, fields));
            Assert.AreEqual("validated", dry["results"][0]["status"].Value<string>());
            Assert.AreEqual(3, receiver.movementButtons.Length);
            var r = Run(false, EditOp("array", receiverToken, fields));
            Assert.AreEqual("applied", r["results"][0]["status"].Value<string>());
            Assert.AreEqual(1, receiver.movementButtons.Length); Assert.AreSame(button, receiver.movementButtons[0]);
            Undo.PerformUndo(); Assert.AreEqual(3, receiver.movementButtons.Length);
            var wrong = Reference(root.transform);
            r = Run(false, EditOp("type", receiverToken, new JObject { ["weaponButton"] = new JObject { ["reference"] = wrong } }));
            Assert.AreEqual("error", r["results"][0]["status"].Value<string>()); Assert.IsNull(receiver.weaponButton);
            r = Run(false, EditOp("raw", receiverToken, new JObject { ["weaponButton"] = button.GetInstanceID() }));
            Assert.AreEqual("error", r["results"][0]["status"].Value<string>());
        }
        [Test]
        public void TimeDepthAndMixedSceneLimitsAreExplicit()
        {
            var refToken = Reference(root.transform);
            var slow = Dispatch("semantic_batch", new JObject { ["operations"] = new JArray(Enumerable.Range(0, 64).Select(i => InspectOp(i.ToString(), refToken, "m_LocalPosition"))), ["limits"] = new JObject { ["maxMilliseconds"] = 1 } });
            Assert.IsTrue(slow["truncated"].Value<bool>());
            var deep = new GameObject("Deep"); deep.transform.SetParent(root.transform.GetChild(0)); deep.AddComponent<CharacterJoint>();
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            var r = Prefabs(new JObject { ["maxDepth"] = 1, ["maxMilliseconds"] = 1000 });
            Assert.IsTrue(r["truncated"].Value<bool>());
            EditorSceneManager.SaveScene(scene, folder + "/Scene.unity");
            var otherScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var other = new GameObject("Other"); SceneManager.MoveGameObjectToScene(other, otherScene);
            var a = Reference(root.transform); var b = Reference(other.transform);
            r = Run(false, EditOp("first", a, new JObject { ["m_LocalPosition"] = new JObject { ["x"] = 1, ["y"] = 0, ["z"] = 0 } }), EditOp("other", b, new JObject { ["m_LocalPosition"] = new JObject { ["x"] = 2, ["y"] = 0, ["z"] = 0 } }));
            Assert.AreEqual("skipped", r["results"][0]["status"].Value<string>()); Assert.AreEqual("error", r["results"][1]["status"].Value<string>());
            Assert.AreEqual(Vector3.zero, root.transform.localPosition);
            EditorSceneManager.CloseScene(otherScene, true);
        }

        [Test]
        public void NativeReferenceTypesAndStructuralPropertiesAreRejected()
        {
            var joint = root.GetComponentInChildren<CharacterJoint>(); var token = Reference(joint);
            var wrong = Reference(root.transform);
            var r = Run(false, EditOp("wrong", token, new JObject { ["m_ConnectedBody"] = new JObject { ["reference"] = wrong } }));
            Assert.AreEqual("error", r["results"][0]["status"].Value<string>()); Assert.IsNull(joint.connectedBody);
            var body = root.AddComponent<Rigidbody>(); var bodyToken = Reference(body);
            r = Run(false, EditOp("right", token, new JObject { ["m_ConnectedBody"] = new JObject { ["reference"] = bodyToken } }));
            Assert.AreEqual("applied", r["results"][0]["status"].Value<string>()); Assert.AreSame(body, joint.connectedBody);
            r = Run(false, EditOp("parent", wrong, new JObject { ["m_Father"] = JValue.CreateNull() }));
            Assert.AreEqual("error", r["results"][0]["status"].Value<string>());
        }
        [Test]
        public void ErrorHeavyResponsesStayBoundedAndPreserveOperationOrder()
        {
            var token = Reference(root.transform);
            var ops = new JArray(Enumerable.Range(0, 64).Select(i => InspectOp(i.ToString("D2") + new string('x', 62), token, "missing")));
            var r = Dispatch("semantic_batch", new JObject { ["operations"] = ops, ["limits"] = new JObject { ["maxResponseBytes"] = 8192, ["maxMilliseconds"] = 1000 } });
            Assert.AreEqual(64, ((JArray)r["results"]).Count);
            CollectionAssert.AreEqual(ops.Select(o => o["id"].Value<string>()).ToArray(), r["results"].Select(o => o["id"].Value<string>()).ToArray());
            Assert.LessOrEqual(System.Text.Encoding.UTF8.GetByteCount(r.ToString(Newtonsoft.Json.Formatting.None)), 8192);
        }

        [Test]
        public void MeasuresDispatcherRoundTripsForTwoPrefabJointComparison()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var joints = root.GetComponentsInChildren<CharacterJoint>();
            for (int copy = 0; copy < 2; copy++) foreach (var joint in joints)
            {
                var completion = new TaskCompletionSource<string>();
                McpMessageHandler.HandleMcpMessage(new JObject { ["method"] = "inspect_gameobject", ["instanceId"] = joint.gameObject.GetInstanceID(), ["componentName"] = "CharacterJoint" }, completion);
                Assert.IsFalse(completion.Task.Result.StartsWith("Error:"));
            }
            long individualMs = watch.ElapsedMilliseconds;
            string second = folder + "/Rig2.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, second);
            watch.Restart(); var result = Prefabs(null, prefabPath, second); long batchMs = watch.ElapsedMilliseconds;
            Assert.AreEqual(20, result["results"].Sum(r => ((JArray)r["objects"]).Count));
            TestContext.WriteLine($"MEASURED: 20 individual dispatcher requests vs 1 prefab query; individual={individualMs}ms, prefabBatch={batchMs}ms. Excludes HTTP/MCP latency and object discovery.");
            var transforms = joints.Select(j => j.transform).ToArray();
            watch.Restart();
            foreach (var t in transforms)
            {
                var completion = new TaskCompletionSource<string>();
                McpMessageHandler.HandleMcpMessage(new JObject { ["method"] = "set_field_values", ["instanceId"] = t.gameObject.GetInstanceID(), ["componentName"] = "Transform",
                    ["fields"] = new JObject { ["m_LocalScale"] = new JObject { ["x"] = 2, ["y"] = 2, ["z"] = 2 } } }, completion);
                StringAssert.Contains("OK", completion.Task.Result);
            }
            long individualEditMs = watch.ElapsedMilliseconds;
            foreach (var t in transforms) t.localScale = Vector3.one;
            var refs = Dispatch("acquire_editor_references", new JObject { ["instanceIds"] = new JArray(transforms.Select(t => t.GetInstanceID())), ["limits"] = Limits() });
            var editsToApply = transforms.Select((t, i) => EditOp("scale" + i, refs["results"][i]["object"]["reference"].Value<string>(), new JObject { ["m_LocalScale"] = new JObject { ["x"] = 2, ["y"] = 2, ["z"] = 2 } })).ToArray();
            watch.Restart(); var edits = Run(false, editsToApply); long editBatchMs = watch.ElapsedMilliseconds;
            Assert.AreEqual(10, edits["appliedOperations"].Value<int>());
            Assert.IsTrue(transforms.All(t => t.localScale == Vector3.one * 2));
            TestContext.WriteLine($"MEASURED: 10 individual edits on 10 objects vs 1 batch request; individual={individualEditMs}ms, editBatch={editBatchMs}ms. With cold references, 1 acquisition + 1 batch = 2 requests. Excludes HTTP/MCP latency.");
        }
    }
}
