using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Gamenami.UnitySemanticBridge.Tests;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    public class UiAuthoringTests
    {
        Scene scene, previous;
        string folder;
        UiAcceptanceReceiver receiver;
        const string FontPath = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";

        [SetUp]
        public void SetUp()
        {
            if (!Application.isBatchMode) Assert.Ignore("Run UI integration tests in a disposable batch-mode project; they open/replace test scenes.");
            previous = SceneManager.GetActiveScene();
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SceneManager.SetActiveScene(scene);
            folder = "Assets/__UsbUiTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            receiver = new GameObject("ExistingGameplay").AddComponent<UiAcceptanceReceiver>();
        }

        [TearDown]
        public void TearDown()
        {
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                PrefabStageUtility.GetCurrentPrefabStage().ClearDirtiness(); // Discard only this disposable fixture on failure.
                StageUtility.GoToMainStage();
            }
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        static JObject Ref(Object o, string component = null)
        {
            var r = new JObject { ["instanceId"] = o.GetInstanceID() }; if (component != null) r["component"] = component; return r;
        }
        static JObject Local(string id, string component = null)
        {
            var r = new JObject { ["ref"] = id }; if (component != null) r["component"] = component; return r;
        }
        static JObject Vec(float x, float y) => new JObject { ["x"] = x, ["y"] = y };
        static JObject Rect(float x, float y, float w, float h) => new JObject { ["anchorMin"] = Vec(.5f, .5f), ["anchorMax"] = Vec(.5f, .5f),
            ["pivot"] = Vec(.5f, .5f), ["anchoredPosition"] = Vec(x,y), ["sizeDelta"] = Vec(w,h) };
        static string Call(string method, JObject message)
        {
            message["method"] = method; var completion = new TaskCompletionSource<string>();
            McpMessageHandler.HandleMcpMessage(message, completion);
            Assert.IsTrue(completion.Task.IsCompleted); return completion.Task.GetAwaiter().GetResult();
        }
        static JObject Run(JArray ops)
        {
            var result = Call("author_ui", new JObject { ["operations"] = ops });
            Assert.IsFalse(result.StartsWith("Error:"), result); return JObject.Parse(result);
        }
        static GameObject Created(JObject result, string id) => EditorUtility.InstanceIDToObject(result["objects"][id]["instanceId"].Value<int>()) as GameObject;
        JArray Acceptance()
        {
            Assert.IsNotNull(AssetDatabase.LoadMainAssetAtPath(FontPath), "Install TMP Essential Resources in the disposable test project.");
            var ops = new JArray
            {
                new JObject { ["op"]="create", ["id"]="hud", ["kind"]="Canvas", ["name"]="MovementHUD", ["properties"]=new JObject { ["renderMode"]="ScreenSpaceOverlay" } },
                new JObject { ["op"]="create", ["id"]="panel", ["kind"]="Panel", ["name"]="MovementPanel", ["parent"]=Local("hud"), ["rect"]=Rect(0,0,520,240),
                    ["properties"]=new JObject { ["raycastTarget"]=false, ["color"]=new JObject { ["r"]=.1, ["g"]=.15, ["b"]=.2, ["a"]=.9 } } },
            };
            for (var i=0; i<4; i++) ops.Add(new JObject { ["op"]="create", ["id"]="button"+i, ["kind"]="Button", ["name"]=i<3?"Move"+i:"Weapon", ["parent"]=Local("panel"),
                ["rect"]=Rect(-180+i*120,0,100,50), ["properties"]=new JObject { ["interactable"]=true, ["navigation"]=new JObject { ["mode"]="None" } } });
            ops.Add(new JObject { ["op"]="create", ["id"]="status", ["kind"]="TextMeshProUGUI", ["name"]="Status", ["parent"]=Local("panel"), ["rect"]=Rect(0,75,450,40),
                ["properties"]=new JObject { ["text"]="Ready", ["font"]=new JObject { ["path"]=FontPath }, ["fontSize"]=24, ["color"]=new JObject { ["r"]=1,["g"]=1,["b"]=1,["a"]=1 } } });
            ops.Add(new JObject { ["op"]="set_fields", ["target"]=Ref(receiver), ["fields"]=new JObject { ["movementButtons"]=new JArray(Local("button0","Button"),Local("button1","Button"),Local("button2","Button")),
                ["weaponButton"]=Local("button3","Button"), ["statusLabel"]=Local("status","TMPro.TextMeshProUGUI") } });
            ops.Add(new JObject { ["op"]="button_on_click", ["target"]=Local("button3","Button"), ["listeners"]=new JArray(new JObject { ["target"]=Ref(receiver), ["method"]="SelectWeapon", ["mode"]="int", ["argument"]=2 }) });
            return ops;
        }

        [UnityTest]
        public IEnumerator AcceptanceCreateWireInspectUndoSaveAndReopen()
        {
            new GameObject("EventSystem").AddComponent<EventSystem>();
            var result = Run(Acceptance());
            Assert.AreEqual(3, receiver.movementButtons.Length); Assert.AreEqual("TextMeshProUGUI", receiver.statusLabel.GetType().Name);
            Assert.AreEqual("keep me", receiver.preserved); Assert.AreEqual(1,receiver.weaponButton.onClick.GetPersistentEventCount());
            Assert.AreEqual("SelectWeapon",receiver.weaponButton.onClick.GetPersistentMethodName(0));
            var hud = Created(result,"hud"); var button = receiver.weaponButton;
            var report = JObject.Parse(Call("inspect_ui",new JObject { ["instanceId"]=hud.GetInstanceID(), ["includeReferences"]=true }));
            Assert.AreEqual(7,report["totalNodes"].Value<int>());
            var node = report["nodes"].Single(n=>n["object"]["instanceId"].Value<int>()==button.gameObject.GetInstanceID());
            
            var gameView = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")); gameView.Show();
            EditorApplication.QueuePlayerLoopUpdate();
            var deadline = EditorApplication.timeSinceStartup + .5;
            while (EditorApplication.timeSinceStartup < deadline) yield return null;
            report = JObject.Parse(Call("inspect_ui",new JObject { ["instanceId"]=hud.GetInstanceID() }));
            node = report["nodes"].Single(n=>n["object"]["instanceId"].Value<int>()==button.gameObject.GetInstanceID());
            var min=node["screenBounds"]["min"]; var max=node["screenBounds"]["max"];
            var ray = JObject.Parse(Call("raycast_ui",new JObject { ["x"]=(min["x"].Value<float>()+max["x"].Value<float>())/2, ["y"]=(min["y"].Value<float>()+max["y"].Value<float>())/2 }));
            TestContext.WriteLine("Overlay raycast: "+ray);
            Assert.IsTrue(ray["hits"].Any(h=>h["object"]["instanceId"].Value<int>()==button.gameObject.GetInstanceID()), "Expected actual Button GraphicRaycaster hit.");
            StringAssert.StartsWith("UI batch undone",Call("undo_ui_batch",new JObject { ["undoToken"]=result["undoToken"] }));
            Assert.IsTrue(hud==null); Assert.IsNull(receiver.weaponButton); Assert.IsNull(receiver.statusLabel);
            result=Run(Acceptance()); hud=Created(result,"hud");
            var path=folder+"/Acceptance.unity";
            var saved=Call("save_ui_context",new JObject { ["instanceId"]=hud.GetInstanceID(), ["path"]=path });
            StringAssert.DoesNotContain("Error:",saved);
            var global=JObject.Parse(saved)["object"]["globalObjectId"].Value<string>();
            scene=EditorSceneManager.OpenScene(path,OpenSceneMode.Single); SceneManager.SetActiveScene(scene);
            receiver=scene.GetRootGameObjects().Select(g=>g.GetComponent<UiAcceptanceReceiver>()).First(c=>c!=null);
            Assert.AreEqual(3,receiver.movementButtons.Length); Assert.IsNotNull(receiver.statusLabel); Assert.AreEqual(1,receiver.weaponButton.onClick.GetPersistentEventCount());
            Assert.IsTrue(GlobalObjectId.TryParse(global,out var gid)); Assert.IsNotNull(GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid));
            var serialized=new SerializedObject(receiver.weaponButton); Assert.AreEqual(2, serialized.FindProperty("m_OnClick.m_PersistentCalls.m_Calls").GetArrayElementAtIndex(0).FindPropertyRelative("m_Arguments.m_IntArgument").intValue);
            Assert.IsFalse(scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<Component>(true)).Any(c=>c!=null && c.GetType().Assembly==typeof(UiAuthoring).Assembly));
        }

        [Test]
        public void UndoTokenAndUnrelatedEditsAreProtected()
        {
            var result=Run(new JArray(new JObject { ["op"]="create",["id"]="c",["kind"]="Canvas",["name"]="UndoCanvas",["properties"]=new JObject { ["renderMode"]="ScreenSpaceOverlay" } }));
            var go=Created(result,"c");
            StringAssert.StartsWith("UI batch undone",Call("undo_ui_batch",new JObject { ["undoToken"]=result["undoToken"] })); Assert.IsTrue(go==null);
            result=Run(new JArray(new JObject { ["op"]="create",["id"]="c",["kind"]="Canvas",["name"]="KeepCanvas",["properties"]=new JObject { ["renderMode"]="ScreenSpaceOverlay" } }));
            Undo.IncrementCurrentGroup(); Undo.RecordObject(receiver,"Other edit"); receiver.preserved="unrelated";
            StringAssert.StartsWith("Error:",Call("undo_ui_batch",new JObject { ["undoToken"]=result["undoToken"] })); Assert.IsNotNull(Created(result,"c")); Assert.AreEqual("unrelated",receiver.preserved);
        }

        [Test]
        public void InvalidReferencesTypesAndMethodsPreflightWithoutMutation()
        {
            var count=scene.rootCount;
            foreach (var fields in new[] {
                new JObject { ["weaponButton"]=Ref(receiver) },
                new JObject { ["weaponButton"]=new JObject { ["instanceId"]=int.MaxValue } },
                new JObject { ["movementButtons"]=new JArray(Ref(receiver)) } })
            {
                var result=Call("author_ui",new JObject { ["operations"]=new JArray(new JObject { ["op"]="set_fields",["target"]=Ref(receiver),["fields"]=fields }) });
                StringAssert.Contains("No changes made",result); Assert.AreEqual(count,scene.rootCount); Assert.IsNull(receiver.weaponButton);
            }
            var ops=Acceptance(); ((JObject)ops.Last)["listeners"][0]["method"]="UnsupportedReturn";
            StringAssert.Contains("No changes made",Call("author_ui",new JObject { ["operations"]=ops })); Assert.AreEqual(count,scene.rootCount);
        }

        [Test]
        public void MissingFontAndTmpResourcesAreActionable()
        {
            var ops=Acceptance(); ((JObject)ops[6])["properties"]["font"]=new JObject { ["path"]="Assets/MissingFont.asset" };
            StringAssert.Contains("No changes made",Call("author_ui",new JObject { ["operations"]=ops }));
            var result=Run(Acceptance());
            StringAssert.Contains("reference is required",Call("author_ui",new JObject { ["operations"]=new JArray(new JObject {
                ["op"]="configure",["target"]=Ref(receiver.statusLabel),["properties"]=new JObject { ["font"]=null } }) }));
            const string settings="Assets/TextMesh Pro/Resources/TMP Settings.asset";
            var moved=folder+"/TemporarilyUnavailableSettings.asset";
            Assert.AreEqual("",AssetDatabase.MoveAsset(settings,moved));
            try { StringAssert.Contains("TMP Essential Resources missing",Call("author_ui",new JObject { ["operations"]=Acceptance() })); }
            finally { Assert.AreEqual("",AssetDatabase.MoveAsset(moved,settings)); }
        }

        [Test]
        public void FailedLayoutBatchRollsBackCreatedObjectsAndExistingFields()
        {
            var count=scene.rootCount; var ops=Acceptance();
            ops.Add(new JObject { ["op"]="add_component",["target"]=Local("panel"),["component"]="VerticalLayoutGroup" });
            ops.Add(new JObject { ["op"]="set_rect",["target"]=Local("button0"),["rect"]=new JObject { ["sizeDelta"]=Vec(90,40) } });
            var result=Call("author_ui",new JObject { ["operations"]=ops });
            StringAssert.Contains("Batch rolled back",result); StringAssert.Contains("Parent VerticalLayoutGroup",result);
            Assert.AreEqual(count,scene.rootCount); Assert.IsNull(receiver.weaponButton); Assert.IsNull(receiver.statusLabel);
        }

        [Test]
        public void LayoutComponentsAndDeterministicOffsets()
        {
            var result=Run(Acceptance()); var panel=Created(result,"panel");
            var ops=new JArray(new JObject { ["op"]="set_rect",["target"]=Ref(panel),["rect"]=new JObject { ["offsetMax"]=Vec(-10,-20),["anchorMax"]=Vec(1,1),["offsetMin"]=Vec(10,20),["anchorMin"]=Vec(0,0),["pivot"]=Vec(.5f,.5f) } },
                new JObject { ["op"]="add_component",["target"]=Ref(panel),["component"]="GridLayoutGroup",["properties"]=new JObject { ["cellSize"]=Vec(100,50),["spacing"]=Vec(4,4),["constraint"]="FixedColumnCount",["constraintCount"]=2 } },
                new JObject { ["op"]="add_component",["target"]=Ref(receiver.weaponButton.gameObject),["component"]="LayoutElement",["properties"]=new JObject { ["preferredWidth"]=100,["ignoreLayout"]=true } },
                new JObject { ["op"]="add_component",["target"]=Ref(Created(result,"status")),["component"]="ContentSizeFitter",["properties"]=new JObject { ["horizontalFit"]="PreferredSize" } });
            Run(ops); var rect=(RectTransform)panel.transform; Assert.AreEqual(new Vector2(10,20),rect.offsetMin); Assert.AreEqual(new Vector2(-10,-20),rect.offsetMax);
            var error=Call("author_ui",new JObject { ["operations"]=new JArray(new JObject { ["op"]="set_rect",["target"]=Ref(panel),["rect"]=new JObject { ["offsetMin"]=Vec(0,0),["sizeDelta"]=Vec(1,1) } }) });
            StringAssert.Contains("Use offsets OR",error);
        }

        [Test]
        public void PrefabInstanceEditsRemainOverridesAndUndoCleanly()
        {
            var source=new GameObject("UiPrefab",typeof(RectTransform),typeof(Canvas),typeof(Image));
            var path=folder+"/UiPrefab.prefab"; PrefabUtility.SaveAsPrefabAsset(source,path); Object.DestroyImmediate(source);
            var instance=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path),scene); var image=instance.GetComponent<Image>();
            var before=image.color;
            var result=Run(new JArray(new JObject { ["op"]="configure",["target"]=Ref(image),["properties"]=new JObject { ["color"]=new JObject { ["r"]=1,["g"]=0,["b"]=0,["a"]=1 } } },
                new JObject { ["op"]="create",["id"]="child",["kind"]="Panel",["name"]="AddedChild",["parent"]=Ref(instance) }));
            Assert.IsTrue(PrefabUtility.IsPartOfPrefabInstance(instance)); Assert.AreEqual(Color.red,image.color);
            Assert.IsTrue(PrefabUtility.GetPropertyModifications(instance).Any(m=>m.propertyPath.StartsWith("m_Color")));
            Assert.AreEqual(before,AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<Image>().color);
            StringAssert.StartsWith("UI batch undone",Call("undo_ui_batch",new JObject { ["undoToken"]=result["undoToken"] }));
            Assert.AreEqual(before,image.color); Assert.AreEqual(0,instance.transform.childCount);
            StringAssert.Contains("asset objects cannot be edited",Call("author_ui",new JObject { ["operations"]=new JArray(new JObject { ["op"]="configure",["target"]=Ref(AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<Image>()),["properties"]=new JObject { ["raycastTarget"]=false } }) }));
        }

        [Test]
        public void PartialPropertiesAndPersistentObjectCallbacksPreserveValues()
        {
            var result=Run(Acceptance()); var button=receiver.weaponButton;
            var nav=button.navigation; nav.selectOnUp=receiver.movementButtons[0]; button.navigation=nav;
            var colors=button.colors; colors.fadeDuration=.75f; button.colors=colors;
            Run(new JArray(
                new JObject { ["op"]="configure",["target"]=Ref(button),["properties"]=new JObject {
                    ["navigation"]=new JObject { ["mode"]="Explicit" },["colors"]=new JObject { ["colorMultiplier"]=2 } } },
                new JObject { ["op"]="button_on_click",["target"]=Ref(button),["mode"]="replace",["listeners"]=new JArray(
                    new JObject { ["target"]=Ref(receiver),["method"]="SetLabel",["mode"]="object",["argumentType"]="UnityEngine.UI.Graphic",["argument"]=Ref(receiver.statusLabel),["state"]="EditorAndRuntime" },
                    new JObject { ["target"]=Ref(receiver),["method"]="SelectWeapon",["mode"]="int",["argument"]=3,["state"]="EditorAndRuntime" }) }));
            Assert.AreEqual(receiver.movementButtons[0],button.navigation.selectOnUp);
            Assert.AreEqual(.75f,button.colors.fadeDuration); Assert.AreEqual(2,button.colors.colorMultiplier);
            var label=receiver.statusLabel; receiver.statusLabel=null;
            button.onClick.Invoke(); Assert.AreEqual(label,receiver.statusLabel); Assert.AreEqual(3,receiver.weaponIndex);
            Assert.AreEqual(2,button.onClick.GetPersistentEventCount());
            Run(new JArray(new JObject { ["op"]="button_on_click",["target"]=Ref(button),["mode"]="replace",["listeners"]=new JArray() }));
            Assert.AreEqual(0,button.onClick.GetPersistentEventCount());
        }

        [Test]
        public void PersistentScalarCallbacksValidateExactSignatures()
        {
            Run(Acceptance());
            var listeners=new JArray();
            foreach (var pair in new[] { new[] { "Click", "void" }, new[] { "SetFraction", "float" }, new[] { "SetArmed", "bool" }, new[] { "SetCaption", "string" } })
            {
                var listener=new JObject { ["target"]=Ref(receiver),["method"]=pair[0],["mode"]=pair[1],["state"]="EditorAndRuntime" };
                if (pair[1]=="float") listener["argument"]=.5;
                if (pair[1]=="bool") listener["argument"]=true;
                if (pair[1]=="string") listener["argument"]="Ready";
                listeners.Add(listener);
            }
            Run(new JArray(new JObject { ["op"]="button_on_click",["target"]=Ref(receiver.weaponButton),["mode"]="replace",["listeners"]=listeners }));
            receiver.weaponButton.onClick.Invoke();
            Assert.AreEqual(0,receiver.weaponIndex); Assert.AreEqual(.5f,receiver.fraction); Assert.IsTrue(receiver.armed); Assert.AreEqual("Ready",receiver.caption);
            listeners=new JArray(new JObject { ["target"]=Ref(receiver),["method"]="SetFraction",["mode"]="int",["argument"]=2 });
            StringAssert.Contains("No changes made",Call("author_ui",new JObject { ["operations"]=new JArray(new JObject { ["op"]="button_on_click",["target"]=Ref(receiver.weaponButton),["listeners"]=listeners }) }));
            Assert.AreEqual(4,receiver.weaponButton.onClick.GetPersistentEventCount());
        }

        [Test]
        public void DrivenAcknowledgementAndUnsafeSavePathsAreExplicit()
        {
            var result=Run(Acceptance()); var panel=Created(result,"panel");
            Run(new JArray(new JObject { ["op"]="add_component",["target"]=Ref(panel),["component"]="HorizontalLayoutGroup" }));
            var op=new JObject { ["op"]="set_rect",["target"]=Ref(receiver.weaponButton.gameObject),["rect"]=new JObject { ["sizeDelta"]=Vec(20,30) } };
            StringAssert.Contains("No changes made",Call("author_ui",new JObject { ["operations"]=new JArray(op) }));
            op["allowDriven"]=true; var changed=Run(new JArray(op));
            Assert.IsTrue(changed["warnings"].Any(w=>w.Value<string>().Contains("layout drivers")));
            foreach (var path in new[] { "Packages/UI/No.unity", "Assets/Vendor/No.unity", "Assets/../No.unity" })
                StringAssert.StartsWith("Error:",Call("save_ui_context",new JObject { ["instanceId"]=panel.GetInstanceID(),["path"]=path }));
        }

        [Test]
        public void ExistingFieldEditorSharesTypedReferencesAndKeepsPrimitiveSupport()
        {
            Run(Acceptance()); var button=receiver.weaponButton;
            var result=Call("set_field_values",new JObject { ["instanceId"]=receiver.gameObject.GetInstanceID(),["componentName"]="UiAcceptanceReceiver",["fields"]=new JObject {
                ["weaponButton"]=Ref(button),["movementButtons"]=new JArray(Ref(button)),["preserved"]="updated" } });
            StringAssert.DoesNotContain("ERROR",result); Assert.AreEqual(button,receiver.weaponButton);
            Assert.AreEqual(1,receiver.movementButtons.Length); Assert.AreEqual("updated",receiver.preserved);
            result=Call("set_field_values",new JObject { ["instanceId"]=receiver.gameObject.GetInstanceID(),["componentName"]="UiAcceptanceReceiver",["fields"]=new JObject { ["weaponButton"]=Ref(receiver) } });
            StringAssert.Contains("ERROR",result); Assert.AreEqual(button,receiver.weaponButton);
            var camera=new GameObject("NativeFields").AddComponent<Camera>(); var texture=new RenderTexture(16,16,0);
            try
            {
                result=Call("set_field_values",new JObject { ["instanceId"]=camera.gameObject.GetInstanceID(),["componentName"]="Camera",["fields"]=new JObject { ["m_TargetTexture"]=Ref(texture) } });
                StringAssert.DoesNotContain("ERROR",result); Assert.AreEqual(texture,camera.targetTexture);
            }
            finally { Object.DestroyImmediate(texture); }
        }

        [Test]
        public void PrefabStageRequiresExplicitSaveAndPreservesSourceUntilSaved()
        {
            // Save the surrounding test scene before entering Prefab Mode.
            EditorSceneManager.SaveScene(scene,folder+"/StageHost.unity");
            var source=new GameObject("StageUI",typeof(RectTransform),typeof(Canvas),typeof(Image));
            var path=folder+"/StageUI.prefab"; PrefabUtility.SaveAsPrefabAsset(source,path); Object.DestroyImmediate(source);
            var stage=PrefabStageUtility.OpenPrefab(path);
            var auto=stage.GetType().GetProperty("autoSave",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
            Assert.IsNotNull(auto); var original=(bool)auto.GetValue(stage);
            try
            {
                auto.SetValue(stage,true);
                var root=stage.prefabContentsRoot; var image=root.GetComponent<Image>();
                var ops=new JArray(new JObject { ["op"]="configure",["target"]=Ref(image),["properties"]=new JObject { ["raycastTarget"]=false } });
                StringAssert.Contains("Turn off Auto Save",Call("author_ui",new JObject { ["operations"]=ops }));
                auto.SetValue(stage,false); Run(ops);
                Assert.IsFalse(image.raycastTarget); Assert.IsTrue(AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<Image>().raycastTarget);
                StringAssert.DoesNotContain("Error:",Call("save_ui_context",new JObject { ["instanceId"]=root.GetInstanceID() }));
                Assert.IsFalse(AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<Image>().raycastTarget);
                Assert.IsFalse(stage.scene.isDirty);
                // Saved asset is the source of truth; reopening must preserve the authoring.
                StageUtility.GoToMainStage(); stage=PrefabStageUtility.OpenPrefab(path);
                Assert.IsFalse(stage.prefabContentsRoot.GetComponent<Image>().raycastTarget);
            }
            finally { if (stage!=null) auto.SetValue(stage,original); StageUtility.GoToMainStage(); }
        }

        [UnityTest]
        public IEnumerator WorldSpaceMaskHiddenGraphicStillRaycasts()
        {
            new GameObject("EventSystem").AddComponent<EventSystem>();
            var camera=new GameObject("View").AddComponent<Camera>(); camera.transform.position=new Vector3(0,0,-10); camera.orthographic=true; camera.orthographicSize=5; camera.pixelRect=new Rect(0,0,640,480);
            var result=Run(new JArray(new JObject { ["op"]="create",["id"]="world",["kind"]="Canvas",["name"]="WorldUI",["properties"]=new JObject { ["renderMode"]="WorldSpace",["worldCamera"]=Ref(camera) },["rect"]=new JObject { ["sizeDelta"]=Vec(4,4) } },
                new JObject { ["op"]="create",["id"]="label",["kind"]="Image",["name"]="InvisibleInputBlocker",["parent"]=Local("world"),["rect"]=Rect(0,0,2,2) }));
            var gameView = EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")); gameView.Show();
            Canvas.ForceUpdateCanvases(); camera.Render(); EditorApplication.QueuePlayerLoopUpdate();
            var deadline = EditorApplication.timeSinceStartup + .5;
            while (EditorApplication.timeSinceStartup < deadline) yield return null;
            TestContext.WriteLine("World depth="+Created(result,"label").GetComponent<Graphic>().depth+" screen="+Screen.width+"x"+Screen.height+" pos="+Created(result,"label").transform.position);
            camera.cullingMask=0;
            var pos=camera.WorldToScreenPoint(Created(result,"label").transform.position);
            var ray=JObject.Parse(Call("raycast_ui",new JObject { ["x"]=pos.x,["y"]=pos.y,["cameraInstanceId"]=camera.GetInstanceID() }));
            TestContext.WriteLine("Hidden UI raycast: "+ray);
            var hit=ray["hits"].FirstOrDefault(h=>h["object"]["instanceId"].Value<int>()==Created(result,"label").GetInstanceID());
            Assert.IsNotNull(hit,"Expected actual world-space GraphicRaycaster hit despite camera mask.");
            Assert.IsTrue(hit["visibility"]["cameraMaskExcludesObject"].Value<bool>()); Assert.IsFalse(hit["visibility"]["visibleEstimate"].Value<bool>());
            Run(new JArray(new JObject { ["op"]="add_component",["target"]=Ref(Created(result,"world")),["component"]="CanvasGroup",["properties"]=new JObject { ["blocksRaycasts"]=false,["alpha"]=0,["interactable"]=false } }));
            ray=JObject.Parse(Call("raycast_ui",new JObject { ["x"]=pos.x,["y"]=pos.y }));
            Assert.IsFalse(ray["hits"].Any(h=>h["object"]["instanceId"].Value<int>()==Created(result,"label").GetInstanceID()));
        }
    }
}
