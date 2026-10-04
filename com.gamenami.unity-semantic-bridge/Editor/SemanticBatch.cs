using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Gamenami.UnitySemanticBridge.Editor
{
    // All entry points are dispatched synchronously by the existing main-thread queue.
    [InitializeOnLoad]
    public static class SemanticBatch
    {
        const int MaxOperations = 64;
        sealed class ReferenceEntry { internal Object obj; internal int sceneHandle; }
        static readonly Dictionary<string, ReferenceEntry> references = new Dictionary<string, ReferenceEntry>();
        static readonly Queue<string> referenceOrder = new Queue<string>();

        static SemanticBatch()
        {
            EditorSceneManager.sceneOpened += (_, __) => Invalidate();
            EditorSceneManager.sceneClosed += _ => Invalidate();
            EditorSceneManager.activeSceneChangedInEditMode += (_, __) => Invalidate();
            EditorSceneManager.newSceneCreated += (_, __, ___) => Invalidate();
            PrefabStage.prefabStageOpened += _ => Invalidate();
            PrefabStage.prefabStageClosing += _ => Invalidate();
            EditorApplication.playModeStateChanged += _ => Invalidate();
        }
        static void Invalidate() { references.Clear(); referenceOrder.Clear(); }

        internal sealed class Budget
        {
            internal readonly Stopwatch clock = Stopwatch.StartNew();
            internal int nodes, targets, values;
            internal readonly int maxNodes, maxTargets, maxDepth, maxCollection, maxValues, maxBytes, maxMs;
            internal readonly HashSet<string> reasons = new HashSet<string>();
            internal Budget(JObject limits)
            {
                Keys(limits, "maxNodes maxTargets maxDepth maxCollection maxValues maxResponseBytes maxMilliseconds");
                maxNodes = Limit(limits, "maxNodes", 512, 4096);
                maxTargets = Limit(limits, "maxTargets", 128, 512);
                maxDepth = Limit(limits, "maxDepth", 32, 64);
                maxCollection = Limit(limits, "maxCollection", 32, 128);
                maxValues = Limit(limits, "maxValues", 4096, 16384);
                maxBytes = Limit(limits, "maxResponseBytes", 262144, 1048576, 8192);
                maxMs = Limit(limits, "maxMilliseconds", 100, 1000);
            }
            static int Limit(JObject obj, string key, int fallback, int max, int min = 1)
            {
                if (obj?[key] == null) return fallback;
                if (obj[key].Type != JTokenType.Integer) throw new ArgumentException(key + " must be an integer.");
                int n = obj[key].Value<int>();
                if (n < min || n > max) throw new ArgumentException($"{key} must be {min}–{max}.");
                return n;
            }
            internal void CheckTime() { if (clock.ElapsedMilliseconds >= maxMs) throw new LimitException("executionTime"); }
            internal void Value(int depth)
            {
                CheckTime();
                if (depth > maxDepth) throw new LimitException("propertyDepth");
                if (++values > maxValues) throw new LimitException("expandedValues");
            }
        }
        internal sealed class LimitException : Exception
        {
            internal LimitException(string reason) : base(reason) { }
        }
        internal static void Keys(JObject obj, string allowed)
        {
            if (obj == null) return;
            var names = allowed.Split(' ');
            foreach (var p in obj.Properties()) if (!names.Contains(p.Name)) throw new ArgumentException("Unsupported key: " + p.Name);
        }
        static JObject LimitsObject(JObject message)
        {
            if (message["limits"] != null && !(message["limits"] is JObject)) throw new ArgumentException("limits must be an object.");
            if (Bytes(message) > 262144) throw new ArgumentException("Request exceeds 262144 UTF-8 bytes.");
            return message["limits"] as JObject;
        }
        static void Idle()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new ArgumentException("Wait for an idle Edit Mode Editor.");
        }
        static string RequiredString(JToken token, string label, int max = 256)
        {
            if (token?.Type != JTokenType.String || string.IsNullOrWhiteSpace(token.Value<string>()) || token.Value<string>().Length > max)
                throw new ArgumentException($"{label} must be a nonempty string of at most {max} characters.");
            return token.Value<string>();
        }
        static JArray Strings(JToken token, string label, int max = 32)
        {
            if (!(token is JArray a) || a.Count < 1 || a.Count > max) throw new ArgumentException($"{label} must contain 1–{max} strings.");
            foreach (var v in a) RequiredString(v, label);
            if (a.Select(v => v.Value<string>()).Distinct().Count() != a.Count) throw new ArgumentException(label + " contains duplicates.");
            return a;
        }
        static string Error(Exception e) => e.Message.Length <= 256 ? e.Message : e.Message.Substring(0, 256);
        static int Bytes(JToken token) => Encoding.UTF8.GetByteCount(token.ToString(Formatting.None));
        static JObject Result(string id, string status) => new JObject { ["id"] = id, ["status"] = status };
        static JObject Envelope(JArray results, Budget b) => new JObject {
            ["results"] = results, ["truncated"] = b.reasons.Count != 0,
            ["limitsReached"] = new JArray(b.reasons.OrderBy(x => x)), ["elapsedMilliseconds"] = b.clock.ElapsedMilliseconds,
            ["snapshotConsistency"] = false, ["saved"] = false
        };
        static string Render(JObject response, Budget b)
        {
            if (Bytes(response) <= b.maxBytes) return response.ToString(Formatting.None);
            b.reasons.Add("responseBytes"); response["truncated"] = true;
            response["limitsReached"] = new JArray(b.reasons.OrderBy(x => x));
            var results = (JArray)response["results"];
            foreach (JObject r in results)
            {
                if (r["error"] != null) { r.Remove("error"); response["errorDetailsOmitted"] = true; }
            }
            for (int i = results.Count - 1; i >= 0 && Bytes(response) > b.maxBytes; i--)
            {
                var r = (JObject)results[i];
                // Applied mutation reports must remain reviewable. Their space was reserved
                // in preflight; only non-applied payloads may be omitted here.
                if (r["status"].Value<string>() == "applied" || r["status"].Value<string>() == "unchanged" || r["status"].Value<string>() == "applicationUncertain") continue;
                bool removed = false;
                foreach (var key in new[] { "objects", "object", "component", "components", "properties", "changes", "target" }) removed |= r.Remove(key);
                if (removed) { r["detailOmitted"] = true; if (r["status"].Value<string>() == "ok") r["status"] = "truncated"; }
            }
            if (Bytes(response) > b.maxBytes)
            {
                foreach (JObject r in results) { r.Remove("detailOmitted"); r.Remove("reason"); }
                response["resultDetailsOmitted"] = true;
            }
            return response.ToString(Formatting.None);
        }
        static void EnsureStatusSpace(JArray keys, Budget b)
        {
            var minimum = new JArray(keys.Select(k => Result(k.Value<string>(), "truncated")));
            if (Bytes(minimum) + 2048 > b.maxBytes) throw new ArgumentException("Response budget too small to report every requested operation/path. Increase maxResponseBytes or reduce request count/ID lengths.");
        }
        static bool Fits(JArray results, JObject item, Budget b)
        {
            // Reserve room for every operation's status and envelope metadata.
            return Bytes(results) + Bytes(item) + 4096 <= b.maxBytes;
        }
        static JObject Identity(Object obj, bool live)
        {
            var result = AuthoringReferences.Identity(obj);
            if (!live) result.Remove("instanceId");
            else
            {
                var token = Guid.NewGuid().ToString("N");
                if (references.Count >= 2048) references.Remove(referenceOrder.Dequeue());
                var owner = obj as GameObject ?? (obj as Component)?.gameObject;
                references[token] = new ReferenceEntry { obj = obj, sceneHandle = owner != null ? owner.scene.handle : 0 }; referenceOrder.Enqueue(token);
                result["reference"] = token;
            }
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long fileId);
            if (!string.IsNullOrEmpty(guid)) { result["guid"] = guid; result["fileID"] = fileId; }
            var go = obj as GameObject ?? (obj as Component)?.gameObject;
            if (go != null)
            {
                result["hierarchyPath"] = HierarchyPath(go.transform);
                result["scenePath"] = go.scene.path;
            }
            return result;
        }
        static string HierarchyPath(Transform t)
        {
            var parts = new List<string>();
            // Identity text is bounded independently of traversal.
            for (int i = 0; t != null && i < 128; i++, t = t.parent)
                parts.Add(t.name + "[" + t.GetSiblingIndex() + "]");
            parts.Reverse(); return string.Join("/", parts);
        }
        static Object ResolveReference(JToken token)
        {
            string key = RequiredString(token, "reference", 64);
            if (!references.TryGetValue(key, out var entry) || entry.obj == null || EditorIdLookup.FromInstanceId(entry.obj.GetInstanceID()) != entry.obj)
                throw new ArgumentException("Missing/stale reference. Acquire a new reference after reload, scene/stage changes, deletion or cache eviction.");
            var obj = entry.obj;
            var owner = obj as GameObject ?? (obj as Component)?.gameObject;
            if (owner == null || !owner.scene.IsValid() || !owner.scene.isLoaded || owner.scene.handle != entry.sceneHandle) throw new ArgumentException("Stale reference after object moved scene or scene unloaded.");
            if (EditorUtility.IsPersistent(obj)) throw new ArgumentException("Batch Editor references cannot target asset objects.");
            return obj;
        }
        static Component Target(JObject op)
        {
            var obj = ResolveReference(op["target"]);
            if (obj is Component direct)
            {
                if (op["component"] != null || op["componentIndex"] != null) throw new ArgumentException("Component reference already identifies its component; omit selectors.");
                return direct;
            }
            var go = obj as GameObject ?? throw new ArgumentException("Target must be a GameObject or Component.");
            var type = AuthoringReferences.TypeNamed(RequiredString(op["component"], "component"));
            if (!typeof(Component).IsAssignableFrom(type)) throw new ArgumentException("Type must be a Component.");
            int index = op["componentIndex"]?.Value<int>() ?? 0;
            var matches = go.GetComponents(type);
            if (index < 0 || index >= matches.Length) throw new ArgumentException("Component index not found.");
            return matches[index];
        }

        public static string AcquireReferences(JObject message)
        {
            try
            {
                Idle(); Keys(message, "method instanceIds limits");
                var b = new Budget(LimitsObject(message));
                if (!(message["instanceIds"] is JArray ids) || ids.Count < 1 || ids.Count > MaxOperations) throw new ArgumentException("instanceIds must contain 1–64 IDs.");
                foreach (var id in ids) if (id.Type != JTokenType.Integer) throw new ArgumentException("Instance IDs must be integers.");
                var results = new JArray();
                foreach (var id in ids)
                {
                    var r = Result(id.ToString(), "ok");
                    try
                    {
                        b.CheckTime();
                        if (id.Type != JTokenType.Integer) throw new ArgumentException("Instance ID must be an integer.");
                        var obj = EditorIdLookup.FromInstanceId(id.Value<int>());
                        if (!(obj is GameObject) && !(obj is Component)) throw new ArgumentException("ID must identify a live GameObject or Component.");
                        if (EditorUtility.IsPersistent(obj)) throw new ArgumentException("Use prefab asset inspection for assets.");
                        r["object"] = Identity(obj, true);
                        if (!Fits(results, r, b)) throw new LimitException("responseBytes");
                    }
                    catch (LimitException e) { b.reasons.Add(e.Message); r = Result(id.ToString(), "truncated"); r["reason"] = e.Message; }
                    catch (Exception e) { r["status"] = "error"; r["error"] = Error(e); }
                    results.Add(r);
                }
                return Render(Envelope(results, b), b);
            }
            catch (Exception e) { return new JObject { ["status"] = "error", ["error"] = Error(e) }.ToString(Formatting.None); }
        }

        static JObject ReadProperties(Component target, JArray paths, Budget b)
        {
            var fields = new JObject();
            using (var so = new SerializedObject(target))
                foreach (var path in paths)
                {
                    var prop = so.FindProperty(path.Value<string>());
                    if (prop == null) throw new ArgumentException("Property not found: " + path);
                    fields[path.Value<string>()] = new JObject { ["type"] = prop.propertyType.ToString(), ["value"] = SerializedPropertyValues.Read(prop, b) };
                }
            return fields;
        }

        public static string InspectPrefabs(JObject message)
        {
            try
            {
                Idle(); Keys(message, "method paths component properties limits");
                var paths = Strings(message["paths"], "paths", 32);
                var properties = Strings(message["properties"], "properties");
                var type = AuthoringReferences.TypeNamed(RequiredString(message["component"], "component"));
                if (!typeof(Component).IsAssignableFrom(type)) throw new ArgumentException("Filter type must be a Component.");
                var b = new Budget(LimitsObject(message)); var results = new JArray();
                EnsureStatusSpace(paths, b);
                foreach (var pathToken in paths)
                {
                    string path = pathToken.Value<string>();
                    var r = Result(path, "ok"); var items = new JArray(); r["objects"] = items;
                    r["source"] = "resolvedImportedPrefabAsset";
                    r["assetPath"] = path;
                    try
                    {
                        b.CheckTime();
                        if (!(path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)) ||
                            !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || path.Contains('\\') || path.Split('/').Any(p => p == ".." || p == "." || p == ""))
                            throw new ArgumentException("Provide an imported project-relative Assets/ or Packages/ .prefab path.");
                        var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        if (root == null || !PrefabUtility.IsPartOfPrefabAsset(root)) throw new ArgumentException("Prefab asset not found: " + path);
                        // Imported assets resolve nested prefabs/variants without LoadPrefabContents,
                        // preview scenes, script instantiation or temporary instance IDs.
                        var stack = new Stack<(Transform transform, int depth, int nextChild)>(); stack.Push((root.transform, 0, -1));
                        while (stack.Count != 0)
                        {
                            b.CheckTime();
                            var node = stack.Pop();
                            if (node.nextChild == -1)
                            {
                                if (++b.nodes > b.maxNodes) throw new LimitException("hierarchyNodes");
                                var components = node.transform.GetComponents(type);
                                for (int i = 0; i < components.Length; i++)
                                {
                                    if (++b.targets > b.maxTargets) throw new LimitException("discoveredTargets");
                                    var item = new JObject { ["object"] = Identity(node.transform.gameObject, false),
                                        ["component"] = Identity(components[i], false), ["componentIndex"] = i,
                                        ["properties"] = ReadProperties(components[i], properties, b) };
                                    items.Add(item);
                                    if (!Fits(results, r, b)) { items.RemoveAt(items.Count - 1); throw new LimitException("responseBytes"); }
                                }
                                if (node.depth >= b.maxDepth)
                                {
                                    if (node.transform.childCount > 0) { b.reasons.Add("hierarchyDepth"); r["status"] = "truncated"; }
                                    continue;
                                }
                                node.nextChild = 0;
                            }
                            if (node.nextChild < node.transform.childCount)
                            {
                                stack.Push((node.transform, node.depth, node.nextChild + 1));
                                stack.Push((node.transform.GetChild(node.nextChild), node.depth + 1, -1));
                            }
                        }
                        if (items.Count == 0 && r["status"].Value<string>() == "ok") throw new ArgumentException("No matching components in prefab: " + type.FullName);
                    }
                    catch (LimitException e) { b.reasons.Add(e.Message); r["status"] = "truncated"; r["reason"] = e.Message; }
                    catch (Exception e) { r["status"] = "error"; r["error"] = Error(e); }
                    if (!Fits(results, r, b)) { b.reasons.Add("responseBytes"); r = Result(path, "truncated"); r["reason"] = "responseBytes"; }
                    results.Add(r);
                }
                return Render(Envelope(results, b), b);
            }
            catch (Exception e) { return new JObject { ["status"] = "error", ["error"] = Error(e) }.ToString(Formatting.None); }
        }

        sealed class Prepared : IDisposable
        {
            internal Component target;
            internal SerializedObject so;
            internal JObject before, after;
            public void Dispose() { so?.Dispose(); }
        }
        static Prepared Prepare(JObject op, Budget b, ref Scene? scene)
        {
            b.CheckTime();
            var target = Target(op); UiAuthoring.EnsureEditable(target.gameObject);
            if ((target.hideFlags & HideFlags.NotEditable) != 0) throw new ArgumentException("Component is not editable.");
            if (PrefabUtility.IsPartOfImmutablePrefab(target)) throw new ArgumentException("Immutable prefab target.");
            if (scene.HasValue && scene.Value != target.gameObject.scene) throw new ArgumentException("Mutation batch must stay in one scene or Prefab Stage.");
            scene = target.gameObject.scene;
            if (!(op["fields"] is JObject fields) || fields.Count < 1 || fields.Count > 32) throw new ArgumentException("fields must contain 1–32 properties.");
            var expected = op["expected"] as JObject;
            if (expected != null && expected.Count > 32) throw new ArgumentException("expected supports at most 32 properties.");
            if (op["expected"] != null && expected == null) throw new ArgumentException("expected must be an object of property paths to values.");
            var plan = new Prepared { target = target, so = new SerializedObject(target), before = new JObject(), after = new JObject() };
            try
            {
                foreach (var kv in fields)
                {
                    RequiredString(new JValue(kv.Key), "property path");
                    if (fields.Properties().Any(p => p.Name != kv.Key && (p.Name.StartsWith(kv.Key + ".", StringComparison.Ordinal) || kv.Key.StartsWith(p.Name + ".", StringComparison.Ordinal))))
                        throw new ArgumentException("Overlapping property paths are unsupported.");
                    var prop = plan.so.FindProperty(kv.Key) ?? throw new ArgumentException("Property not found: " + kv.Key);
                    if (!prop.editable || kv.Key == "m_Script" || kv.Key == "m_GameObject" || kv.Key == "m_ObjectHideFlags" || kv.Key == "m_Father" || kv.Key.StartsWith("m_Children", StringComparison.Ordinal) || kv.Key == "m_RootOrder" || kv.Key.StartsWith("m_CorrespondingSourceObject") || kv.Key.StartsWith("m_Prefab"))
                        throw new ArgumentException("Property is not editable by this batch: " + kv.Key);
                    plan.before[kv.Key] = SerializedPropertyValues.Read(prop, b);
                }
                if (expected != null) foreach (var kv in expected)
                {
                    var prop = plan.so.FindProperty(kv.Key) ?? throw new ArgumentException("Precondition property not found: " + kv.Key);
                    var actual = SerializedPropertyValues.Read(prop, b);
                    if (!SerializedPropertyValues.Equal(actual, kv.Value)) throw new ArgumentException("Expected-value precondition failed: " + kv.Key);
                }
                foreach (var kv in fields)
                {
                    var prop = plan.so.FindProperty(kv.Key);
                    var value = SerializedPropertyValues.Validate(prop, kv.Value, b);
                    // Stage into this uncommitted serialized buffer using the ordinary setter.
                    // Disposal without ApplyModifiedProperties makes validation side-effect free.
                    ComponentFunctions.ApplyPropertyValue(prop, value);
                    plan.after[kv.Key] = SerializedPropertyValues.Read(prop, b);
                }
                return plan;
            }
            catch { plan.Dispose(); throw; }
        }

        public static string Batch(JObject message)
        {
            int group = -1;
            try
            {
                Idle(); Keys(message, "method operations dryRun name limits");
                if (!(message["operations"] is JArray ops) || ops.Count < 1 || ops.Count > MaxOperations) throw new ArgumentException("operations must contain 1–64 operations.");
                bool mutation = false; var ids = new HashSet<string>();
                foreach (var token in ops)
                {
                    var op = token as JObject ?? throw new ArgumentException("Operation must be an object.");
                    Keys(op, "id op target component componentIndex properties fields expected");
                    if (!ids.Add(RequiredString(op["id"], "id", 64))) throw new ArgumentException("Operation IDs must be unique.");
                    string kind = RequiredString(op["op"], "op", 32);
                    if (kind != "inspect" && kind != "set_fields") throw new ArgumentException("Allowed operations: inspect, set_fields.");
                    mutation |= kind == "set_fields";
                    if (kind == "inspect" && (op["fields"] != null || op["expected"] != null)) throw new ArgumentException("inspect does not accept fields/expected.");
                    if (kind == "set_fields" && op["properties"] != null) throw new ArgumentException("set_fields does not accept properties.");
                    if (op["componentIndex"] != null && op["componentIndex"].Type != JTokenType.Integer) throw new ArgumentException("componentIndex must be an integer.");
                }
                if (mutation && ops.Any(o => o["op"].Value<string>() != "set_fields")) throw new ArgumentException("Read and mutation operations cannot be mixed in one batch.");
                if (message["dryRun"] != null && message["dryRun"].Type != JTokenType.Boolean) throw new ArgumentException("dryRun must be Boolean.");
                bool dry = message["dryRun"]?.Value<bool>() ?? false;
                string name = message["name"] == null ? "USB semantic batch" : RequiredString(message["name"], "name", 128);
                var b = new Budget(LimitsObject(message)); var results = new JArray(); Scene? scene = null;
                EnsureStatusSpace(new JArray(ops.Select(o => o["id"].DeepClone())), b);
                // Validate the whole mutation request before any Undo/dirty/apply operations.
                if (mutation)
                {
                    bool invalid = false;
                    var validatedTargets = new Component[ops.Count];
                    int validationIndex = 0;
                    foreach (JObject op in ops)
                    {
                        var r = Result(op["id"].Value<string>(), "validated");
                        try
                        {
                            using (var p = Prepare(op, b, ref scene))
                            {
                                r["changes"] = new JObject { ["before"] = p.before, ["after"] = p.after };
                                r["target"] = Identity(p.target, false);
                                r["targetInstanceId"] = p.target.GetInstanceID();
                                r["targetReference"] = op["target"].DeepClone();
                                validatedTargets[validationIndex] = p.target;
                                if (!Fits(results, r, b)) throw new LimitException("responseBytes");
                            }
                        }
                        catch (LimitException e) { invalid = true; b.reasons.Add(e.Message); r = Result(op["id"].Value<string>(), "truncated"); r["reason"] = e.Message; }
                        catch (Exception e) { invalid = true; r["status"] = "error"; r["error"] = Error(e); }
                        results.Add(r); validationIndex++;
                    }
                    if (invalid || dry)
                    {
                        if (invalid) foreach (JObject r in results) if (r["status"].Value<string>() == "validated") { r["status"] = "skipped"; r["reason"] = "batchValidationFailed"; r.Remove("changes"); }
                        var validationResponse = Envelope(results, b); validationResponse["dryRun"] = dry; validationResponse["appliedOperations"] = 0;
                        return Render(validationResponse, b);
                    }
                    // Report previews are reserved before execution, so response pressure cannot
                    // silently discard the list of writes after applying a mutation.
                    bool stopped = false; int applied = 0; bool applicationUncertain = false;
                    for (int i = 0; i < ops.Count; i++)
                    {
                        var r = (JObject)results[i];
                        if (stopped) { r["status"] = "skipped"; r["reason"] = "previousOperationFailed"; r.Remove("changes"); continue; }
                        bool applyStarted = false;
                        try
                        {
                            using (var p = Prepare((JObject)ops[i], b, ref scene))
                            {
                                if (validatedTargets[i] == null || p.target != validatedTargets[i]) throw new ArgumentException("Component identity changed since preflight; reacquire a direct component reference.");
                                // Preconditions were checked again against live state, including
                                // changes/callbacks from earlier operations. No cross-tick snapshot.
                                var changes = new JObject { ["before"] = p.before, ["after"] = p.after };
                                var candidate = (JObject)r.DeepClone(); candidate["changes"] = changes;
                                var without = new JArray(results.Where((_, index) => index != i).Select(t => t.DeepClone()));
                                if (!Fits(without, candidate, b)) throw new LimitException("responseBytes");
                                if (group < 0) { Undo.IncrementCurrentGroup(); group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName(name); }
                                Undo.RecordObject(p.target, name);
                                r["changes"] = changes;
                                applyStarted = true;
                                bool changed = p.so.ApplyModifiedProperties();
                                if (changed) applied++;
                                r["status"] = changed ? "applied" : "unchanged";
                                if (changed) UiAuthoring.Touch(p.target);
                                r["valuesReported"] = "submittedSerializedValues";
                                // Apply invokes project validation callbacks. Their additional
                                // effects are not represented as submitted property writes.
                            }
                        }
                        catch (LimitException e) { b.reasons.Add(e.Message); r["status"] = "truncated"; r["reason"] = e.Message; r.Remove("changes"); stopped = true; }
                        catch (Exception e) { r["status"] = applyStarted ? "applicationUncertain" : "error"; r["error"] = Error(e); if (!applyStarted) r.Remove("changes"); applicationUncertain |= applyStarted; stopped = true; }
                    }
                    var response = Envelope(results, b); response["dryRun"] = false; response["appliedOperations"] = applied;
                    response["partialApplication"] = stopped && (applied > 0 || applicationUncertain);
                    response["applicationUncertain"] = applicationUncertain;
                    response["undoGroup"] = group < 0 ? JValue.CreateNull() : new JValue(group);
                    try { BridgeRelay.OnAgentMessage?.Invoke($"USB semantic batch: {applied} applied operation(s), stopped={stopped}, uncertain={applicationUncertain}; nothing saved."); }
                    catch (Exception e) { response["loggingWarning"] = Error(e); }
                    return Render(response, b);
                }
                foreach (JObject op in ops)
                {
                    var r = Result(op["id"].Value<string>(), "ok");
                    try
                    {
                        b.CheckTime();
                        var obj = ResolveReference(op["target"]);
                        if (obj is GameObject go && op["component"] == null)
                        {
                            if (op["properties"] != null) throw new ArgumentException("Select a component to inspect properties.");
                            var items = new JArray(); r["components"] = items;
                            foreach (var c in go.GetComponents<Component>())
                            {
                                if (c == null) continue;
                                b.CheckTime(); if (++b.targets > b.maxTargets) throw new LimitException("discoveredTargets");
                                items.Add(Identity(c, true));
                                if (!Fits(results, r, b)) throw new LimitException("responseBytes");
                            }
                            r["object"] = Identity(go, true);
                        }
                        else { var target = Target(op); r["component"] = Identity(target, true); r["properties"] = ReadProperties(target, Strings(op["properties"], "properties"), b); }
                        r["source"] = "liveEditor";
                        if (!Fits(results, r, b)) throw new LimitException("responseBytes");
                    }
                    catch (LimitException e) { b.reasons.Add(e.Message); r = Result(op["id"].Value<string>(), "truncated"); r["reason"] = e.Message; }
                    catch (Exception e) { r["status"] = "error"; r["error"] = Error(e); }
                    results.Add(r);
                }
                return Render(Envelope(results, b), b);
            }
            catch (Exception e) { return new JObject { ["status"] = "error", ["error"] = Error(e) }.ToString(Formatting.None); }
            finally
            {
                if (group >= 0) { Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group); Undo.IncrementCurrentGroup(); }
            }
        }

        internal static Object ResolveValueReference(JToken token)
        {
            if (token?.Type == JTokenType.Null) return null;
            if (token is JObject obj && obj["reference"] != null)
            {
                Keys(obj, "reference"); return ResolveReference(obj["reference"]);
            }
            if (!(token is JObject asset) || (asset["path"] == null && asset["guid"] == null))
                throw new ArgumentException("Object values require null, {reference: token}, or an explicit asset path/GUID reference; raw instance IDs are unsupported.");
            Keys(asset, "path guid fileID component componentIndex");
            return AuthoringReferences.Resolve(asset, typeof(Object));
        }
    }
}
