using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static partial class UiAuthoring
    {
        internal sealed class Plan
        {
            internal readonly Dictionary<string, List<Type>> locals = new Dictionary<string, List<Type>>();
            internal readonly Dictionary<int, List<Type>> added = new Dictionary<int, List<Type>>();
            internal readonly List<Action<Dictionary<string, GameObject>, List<string>>> actions = new List<Action<Dictionary<string, GameObject>, List<string>>>();
            internal readonly HashSet<GameObject> touched = new HashSet<GameObject>();
            internal Scene scene;
            internal bool hasScene;

            internal void Use(GameObject go)
            {
                EnsureEditable(go);
                if (hasScene && scene != go.scene) throw new ArgumentException("One UI batch must stay in one scene or one Prefab Stage.");
                scene = go.scene; hasScene = true; touched.Add(go);
            }

            internal Type ReferenceType(JToken token, Type expected)
            {
                if (token == null || token.Type == JTokenType.Null) return expected;
                var spec = token as JObject;
                if (spec?["ref"] != null)
                {
                    UiProperties.Keys(spec, "ref component componentIndex");
                    var id = spec["ref"].Value<string>();
                    if (!locals.TryGetValue(id, out var components)) throw new ArgumentException($"Unknown local id '{id}'; create it earlier in the batch.");
                    var type = typeof(GameObject);
                    if (spec["component"] != null)
                    {
                        type = AuthoringReferences.TypeNamed(spec["component"].Value<string>());
                        var index = spec["componentIndex"]?.Value<int>() ?? 0;
                        if (index < 0 || components.Count(t => type.IsAssignableFrom(t)) <= index) throw new ArgumentException($"Local '{id}' has no {type.Name} at index {index}.");
                    }
                    if (!expected.IsAssignableFrom(type)) throw new ArgumentException($"Local '{id}' is {type.Name}, expected {expected.Name}; use a component selector.");
                    return type;
                }
                // A component added earlier to an existing object may not exist until execution.
                if (spec?["component"] != null)
                {
                    var baseSpec = (JObject)spec.DeepClone(); baseSpec.Remove("component"); baseSpec.Remove("componentIndex");
                    var obj = AuthoringReferences.Resolve(baseSpec, typeof(Object));
                    var go = obj as GameObject ?? (obj as Component)?.gameObject;
                    var type = AuthoringReferences.TypeNamed(spec["component"].Value<string>());
                    if (go != null && added.TryGetValue(go.GetInstanceID(), out var list))
                    {
                        var index = spec["componentIndex"]?.Value<int>() ?? 0;
                        if (index >= 0 && index < go.GetComponents(type).Length + list.Count(t => type.IsAssignableFrom(t)))
                        {
                            if (!expected.IsAssignableFrom(type)) throw new ArgumentException($"Expected {expected.Name}, got {type.Name}.");
                            Use(go); return type;
                        }
                    }
                }
                var value = AuthoringReferences.Resolve(token, expected);
                if (value is GameObject g && !EditorUtility.IsPersistent(g)) Use(g);
                if (value is Component c && !EditorUtility.IsPersistent(c)) Use(c.gameObject);
                return value != null ? value.GetType() : expected;
            }

            internal void Required(JToken token, Type type)
            {
                if (token == null || token.Type == JTokenType.Null) throw new ArgumentException($"A {type.Name} reference is required.");
                ReferenceType(token, type);
            }
        }

        internal static void EnsureEditable(GameObject go)
        {
            if (go == null || EditorUtility.IsPersistent(go) || !go.scene.IsValid() || !go.scene.isLoaded || (go.hideFlags & HideFlags.NotEditable) != 0)
                throw new ArgumentException("Target must be an editable, loaded scene object or object in the current Prefab Stage; asset objects cannot be edited directly.");
            var stage = PrefabStageUtility.GetPrefabStage(go);
            var current = PrefabStageUtility.GetCurrentPrefabStage();
            if (current != null && stage != current) throw new ArgumentException("While Prefab Mode is open, target only that Prefab Stage.");
            if (stage != null)
            {
                CheckWritablePath(stage.assetPath, ".prefab");
                var auto = stage.GetType().GetProperty("autoSave", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (auto == null || (bool)auto.GetValue(stage)) throw new ArgumentException("Turn off Auto Save in Prefab Mode before UI authoring; saving must be explicit.");
            }
        }

        internal static void CheckWritablePath(string path, string extension)
        {
            var parts = (path ?? "").Replace('\\', '/').Split('/');
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || parts.Contains("..") ||
                parts.Any(p => new[] { "packages", "synty", "thirdparty", "third party", "3rd party", "vendor", "plugins" }.Contains(p.ToLowerInvariant())))
                throw new ArgumentException("Save/edit path must be a project-owned Assets/ path, outside Packages, Synty, ThirdParty, Vendor or Plugins folders. Copy vendor content into your own folder first.");
            if (!AssetDatabase.IsOpenForEdit(path)) throw new ArgumentException($"'{path}' is read-only or not open for edit. Check it out first.");
        }

        internal static void Touch(Object obj)
        {
            EditorUtility.SetDirty(obj);
            if (PrefabUtility.IsPartOfPrefabInstance(obj)) PrefabUtility.RecordPrefabInstancePropertyModifications(obj);
            var go = obj as GameObject ?? (obj as Component)?.gameObject;
            if (go != null) EditorSceneManager.MarkSceneDirty(go.scene);
        }

        static Type Kind(string kind)
        {
            switch (kind)
            {
                case "Canvas": return typeof(Canvas);
                case "RectTransform": return typeof(RectTransform);
                case "Panel": case "Image": return typeof(Image);
                case "Button": return typeof(Button);
                case "TextMeshProUGUI":
                    try { return UiProperties.ComponentType("TMPro.TextMeshProUGUI"); }
                    catch { throw new ArgumentException("TextMeshProUGUI is unavailable. Install TextMeshPro (com.unity.textmeshpro on Unity 2022.3; included in Unity 6 uGUI), then import TMP Essential Resources."); }
                default: throw new ArgumentException("kind must be Canvas, RectTransform, Panel, Image, Button or TextMeshProUGUI.");
            }
        }

        static GameObject Go(JToken token, Dictionary<string, GameObject> locals) => (GameObject)AuthoringReferences.Resolve(token, typeof(GameObject), locals);

        public static string Batch(JObject message)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return "Error: UI authoring requires an idle Editor in Edit Mode.";
            int group = -1;
            try
            {
                UiProperties.Keys(message, "method operations name");
                if (!(message["operations"] is JArray ops) || ops.Count == 0 || ops.Count > 200) throw new ArgumentException("operations must contain 1–200 operations.");
                var plan = new Plan();
                for (var i = 0; i < ops.Count; i++)
                {
                    try { Prepare(plan, ops[i] as JObject ?? throw new ArgumentException("Operation must be an object.")); }
                    catch (Exception e) { throw new ArgumentException($"Operation {i}: {e.Message}", e); }
                }
                if (!plan.hasScene)
                {
                    var stage = PrefabStageUtility.GetCurrentPrefabStage();
                    if (stage != null) throw new ArgumentException("In Prefab Mode specify a parent inside the prefab for newly created UI.");
                    plan.scene = SceneManager.GetActiveScene();
                    if (!plan.scene.IsValid() || !plan.scene.isLoaded) throw new ArgumentException("Open a scene first.");
                }
                Undo.IncrementCurrentGroup(); group = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(message["name"]?.Value<string>() ?? "Author uGUI");
                // Layout rebuild can move existing siblings; include their rects in the same Undo.
                var rects = plan.touched.SelectMany(g => (g.GetComponentInParent<Canvas>()?.rootCanvas.gameObject ?? g).GetComponentsInChildren<RectTransform>(true)).Distinct().Cast<Object>().ToArray();
                if (rects.Length > 0) Undo.RegisterCompleteObjectUndo(rects, "Author uGUI layout");
                var locals = new Dictionary<string, GameObject>(); var warnings = new List<string>();
                foreach (var action in plan.actions) action(locals, warnings);
                Canvas.ForceUpdateCanvases();
                Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group);
                var token = RememberUndo(group, plan.scene);
                Undo.IncrementCurrentGroup();
                return new JObject { ["status"] = "ok", ["undoToken"] = token, ["saved"] = false,
                    ["objects"] = new JObject(locals.Select(kv => new JProperty(kv.Key, DescribeCreated(kv.Value)))),
                    ["warnings"] = new JArray(warnings.Distinct()) }.ToString();
            }
            catch (Exception e)
            {
                if (group >= 0)
                {
                    try { Undo.FlushUndoRecordObjects(); Undo.RevertAllDownToGroup(group); }
                    catch (Exception rollback) { return $"Error: {e.Message}. Rollback failed: {rollback.Message}. Inspect the scene before continuing."; }
                }
                return $"Error: {e.Message}" + (group >= 0 ? " Batch rolled back; scene may remain dirty. Nothing was saved." : " No changes made.");
            }
        }

        static JObject DescribeCreated(GameObject go)
        {
            var result = AuthoringReferences.Identity(go);
            result["components"] = new JArray(go.GetComponents<Component>().Where(c => c != null).Select(AuthoringReferences.Identity));
            return result;
        }

        static void Prepare(Plan plan, JObject op)
        {
            var verb = op["op"]?.Value<string>();
            switch (verb)
            {
                case "create": PrepareCreate(plan, op); break;
                case "add_component": PrepareAdd(plan, op); break;
                case "configure": PrepareConfigure(plan, op); break;
                case "set_rect":
                    UiProperties.Keys(op, "op target rect allowDriven"); plan.Required(op["target"], typeof(GameObject));
                    UiLayout.Validate(op["rect"] as JObject ?? throw new ArgumentException("rect object is required."));
                    if (!(op["target"] is JObject target && target["ref"] != null))
                    {
                        var go = Go(op["target"], null); EnsureEditable(go);
                        var rt = go.GetComponent<RectTransform>(); if (rt == null) throw new ArgumentException("Target needs a RectTransform.");
                        UiLayout.CheckDriven(rt, (JObject)op["rect"], op["allowDriven"]?.Value<bool>() ?? false, new List<string>());
                    }
                    plan.actions.Add((locals, warnings) => UiLayout.Apply(Go(op["target"], locals).GetComponent<RectTransform>() ?? throw new ArgumentException("Target needs RectTransform."),
                        (JObject)op["rect"], op["allowDriven"]?.Value<bool>() ?? false, warnings));
                    break;
                default: if (!PrepareWiring(plan, op)) throw new ArgumentException($"Unknown UI operation '{verb}'."); break;
            }
        }

        static void PrepareCreate(Plan plan, JObject op)
        {
            UiProperties.Keys(op, "op id kind name parent properties rect allowDriven");
            var id = op["id"]?.Value<string>(); var name = op["name"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(id) || plan.locals.ContainsKey(id) || string.IsNullOrWhiteSpace(name)) throw new ArgumentException("create requires unique id and nonempty name.");
            var type = Kind(op["kind"]?.Value<string>());
            var parent = op["parent"];
            if (parent != null && parent.Type != JTokenType.Null)
            {
                plan.Required(parent, typeof(GameObject));
                if (!(parent is JObject p && p["ref"] != null))
                {
                    var go = Go(parent, null); plan.Use(go);
                    if (!(go.transform is RectTransform)) throw new ArgumentException("UI parent must have RectTransform (including Canvas).");
                }
            }
            else if (type != typeof(Canvas)) throw new ArgumentException("Non-Canvas UI needs an explicit parent RectTransform.");
            var properties = op["properties"] as JObject;
            if (op["properties"] != null && properties == null) throw new ArgumentException("properties must be an object.");
            if (type == typeof(Canvas))
            {
                if (properties?["renderMode"] == null) throw new ArgumentException("Canvas requires explicit properties.renderMode.");
                if (properties["renderMode"].Value<string>() != "ScreenSpaceOverlay" && properties["worldCamera"] == null) throw new ArgumentException("Camera/world-space Canvas requires an explicit worldCamera reference.");
                if (properties["renderMode"].Value<string>() != "ScreenSpaceOverlay") plan.Required(properties["worldCamera"], typeof(Camera));
            }
            if (type.Name == "TextMeshProUGUI")
            {
                if (Resources.Load("TMP Settings") == null) throw new ArgumentException("TMP Essential Resources missing. Import them via Window > TextMeshPro > Import TMP Essential Resources before creating text.");
                var fontType = AuthoringReferences.TypeNamed("TMPro.TMP_FontAsset");
                plan.Required(properties?["font"], fontType);
                var font = AuthoringReferences.Resolve(properties["font"], fontType);
                var material = fontType.GetProperty("material")?.GetValue(font) as Material;
                if (material == null || material.shader == null) throw new ArgumentException("The explicit TMP font has no usable material/shader. Import its font atlas/material resources.");
            }
            if (properties != null) UiProperties.Validate(type, properties, (t, expected) => plan.ReferenceType(t, expected));
            UiLayout.Validate(op["rect"] as JObject);
            var components = new List<Type> { typeof(RectTransform) };
            if (type != typeof(RectTransform)) components.Add(type);
            if (type == typeof(Canvas)) components.AddRange(new[] { typeof(CanvasScaler), typeof(GraphicRaycaster) });
            if (type == typeof(Button)) components.Add(typeof(Image));
            plan.locals.Add(id, components);
            var scene = plan;
            plan.actions.Add((locals, warnings) =>
            {
                var go = new GameObject(name, typeof(RectTransform));
                Undo.RegisterCreatedObjectUndo(go, "Create uGUI"); go.SetActive(false);
                SceneManager.MoveGameObjectToScene(go, scene.scene);
                var parentGo = parent == null || parent.Type == JTokenType.Null ? null : Go(parent, locals);
                if (parentGo != null) Undo.SetTransformParent(go.transform, parentGo.transform, "Parent uGUI");
                go.layer = parentGo != null ? parentGo.layer : 5;
                go.transform.localPosition = Vector3.zero; go.transform.localRotation = Quaternion.identity; go.transform.localScale = Vector3.one;
                locals.Add(id, go);
                Component primary = go.transform;
                if (type == typeof(Canvas))
                {
                    primary = Undo.AddComponent<Canvas>(go);
                    Undo.AddComponent<CanvasScaler>(go); Undo.AddComponent<GraphicRaycaster>(go);
                }
                else if (type == typeof(Button))
                {
                    var image = Undo.AddComponent<Image>(go); var button = Undo.AddComponent<Button>(go); button.targetGraphic = image; primary = button;
                }
                else if (type != typeof(RectTransform)) primary = Undo.AddComponent(go, type);
                if (primary is Graphic graphic && type.Name == "TextMeshProUGUI") graphic.raycastTarget = false;
                UiProperties.Apply(primary, properties, locals);
                UiLayout.Apply((RectTransform)go.transform, op["rect"] as JObject, op["allowDriven"]?.Value<bool>() ?? false, warnings);
                go.SetActive(true); Touch(go);
            });
        }

        static void PrepareAdd(Plan plan, JObject op)
        {
            UiProperties.Keys(op, "op target component properties"); plan.Required(op["target"], typeof(GameObject));
            var type = UiProperties.ComponentType(op["component"]?.Value<string>());
            if (type == typeof(Canvas) || type.Name == "TextMeshProUGUI" || type == typeof(Button) || type == typeof(Image)) throw new ArgumentException("Use create for visual elements; add_component supports layout, CanvasGroup, scaler and raycaster components.");
            UiProperties.Validate(type, op["properties"] as JObject, (t, e) => plan.ReferenceType(t, e));
            if (op["target"] is JObject spec && spec["ref"] != null)
            {
                var list = plan.locals[spec["ref"].Value<string>()];
                if (list.Contains(type) || (typeof(LayoutGroup).IsAssignableFrom(type) && list.Any(t => typeof(LayoutGroup).IsAssignableFrom(t)))) throw new ArgumentException("Component/layout group already exists.");
                list.Add(type);
            }
            else
            {
                var go = Go(op["target"], null); plan.Use(go);
                if (go.GetComponent<RectTransform>() == null) throw new ArgumentException("Target requires RectTransform.");
                if (!plan.added.TryGetValue(go.GetInstanceID(), out var list)) plan.added[go.GetInstanceID()] = list = new List<Type>();
                if (go.GetComponent(type) != null || list.Contains(type) || (typeof(LayoutGroup).IsAssignableFrom(type) && (go.GetComponent<LayoutGroup>() != null || list.Any(t => typeof(LayoutGroup).IsAssignableFrom(t))))) throw new ArgumentException("Component/layout group already exists; use configure.");
                list.Add(type);
            }
            plan.actions.Add((locals, warnings) =>
            {
                var go = Go(op["target"], locals); var c = Undo.AddComponent(go, type);
                if (c == null) throw new ArgumentException($"Could not add {type.Name}.");
                UiProperties.Apply(c, op["properties"] as JObject, locals); Touch(go);
            });
        }

        static void PrepareConfigure(Plan plan, JObject op)
        {
            UiProperties.Keys(op, "op target properties"); plan.Required(op["target"], typeof(Component));
            var type = plan.ReferenceType(op["target"], typeof(Component));
            var values = op["properties"] as JObject ?? throw new ArgumentException("properties object is required.");
            UiProperties.Validate(type, values, (t, e) => plan.ReferenceType(t, e));
            plan.actions.Add((locals, warnings) =>
            {
                var c = (Component)AuthoringReferences.Resolve(op["target"], typeof(Component), locals); EnsureEditable(c.gameObject);
                UiProperties.Apply(c, values, locals);
            });
        }

        // Extended in the wiring and transaction-control implementation.
        static bool PrepareWiring(Plan plan, JObject op) => false;
        static string RememberUndo(int group, Scene scene) => group.ToString();
    }
}
