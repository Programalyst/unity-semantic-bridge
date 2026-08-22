using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class HierarchyFunctions
    {
        public static string CreateGameObject(JObject mcpMessage)
        {
            var name = mcpMessage["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                throw new BridgeToolException("create_gameobject: 'name' is required.");

            var parentId = mcpMessage["parentInstanceId"]?.ToObject<int?>();
            var localPos = ParseVector3(mcpMessage["localPosition"]);
            var localScale = ParseVector3(mcpMessage["localScale"]);
            var localRotToken = mcpMessage["localRotation"];

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, $"Create GameObject '{name}'");

            // Parent handling — false = keep local transform (worldPositionStays false)
            if (parentId.HasValue)
            {
                var parentGo = EditorUtility.InstanceIDToObject(parentId.Value) as GameObject;
                if (parentGo == null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    throw new BridgeToolException($"create_gameobject: parentInstanceId {parentId.Value} not found.");
                }
                Undo.SetTransformParent(go.transform, parentGo.transform, "Reparent " + name);
            }

            // Apply local TRS after parenting so values are local as requested
            if (localPos.HasValue)
            {
                Undo.RecordObject(go.transform, "Set localPosition");
                go.transform.localPosition = localPos.Value;
            }
            if (localScale.HasValue)
            {
                Undo.RecordObject(go.transform, "Set localScale");
                go.transform.localScale = localScale.Value;
            }
            if (localRotToken != null && localRotToken.Type != JTokenType.Null)
            {
                Undo.RecordObject(go.transform, "Set localRotation");
                go.transform.localRotation = ParseQuaternion(localRotToken);
            }

            // Ensure dirty for save
            EditorUtility.SetDirty(go);
            var path = GetPath(go.transform);
            var result = new JObject
            {
                ["instanceId"] = go.GetInstanceID(),
                ["path"] = path,
                ["name"] = go.name
            };
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static string DuplicateGameObject(JObject mcpMessage)
        {
            var id = mcpMessage["instanceId"]?.ToObject<int>() ?? 0;
            if (id == 0) throw new BridgeToolException("duplicate_gameobject: 'instanceId' is required.");
            var newName = mcpMessage["newName"]?.ToString();

            var source = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (source == null)
                throw new BridgeToolException($"duplicate_gameobject: GameObject instanceId {id} not found.");

            // Instantiate keeps hierarchy (children) — use PrefabUtility? Simple Instantiate works for scene objects.
            var clone = UnityEngine.Object.Instantiate(source);
            // Instantiate may append "(Clone)" — fix name
            if (!string.IsNullOrWhiteSpace(newName))
                clone.name = newName;
            else
                clone.name = source.name; // strip (Clone) suffix

            Undo.RegisterCreatedObjectUndo(clone, $"Duplicate '{source.name}'");
            EditorUtility.SetDirty(clone);

            var result = new JObject
            {
                ["instanceId"] = clone.GetInstanceID(),
                ["path"] = GetPath(clone.transform),
                ["name"] = clone.name
            };
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static string SetParent(JObject mcpMessage)
        {
            var id = mcpMessage["instanceId"]?.ToObject<int>() ?? 0;
            if (id == 0) throw new BridgeToolException("set_parent: 'instanceId' is required.");
            var parentId = mcpMessage["parentInstanceId"]?.ToObject<int?>();
            // JSON null vs missing: JToken null means unparent
            if (mcpMessage["parentInstanceId"] != null && mcpMessage["parentInstanceId"].Type == JTokenType.Null)
                parentId = null;

            var keepWorld = mcpMessage["keepWorldPosition"]?.ToObject<bool>() ?? false;

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null)
                throw new BridgeToolException($"set_parent: GameObject instanceId {id} not found.");

            Transform newParent = null;
            if (parentId.HasValue)
            {
                var parentGo = EditorUtility.InstanceIDToObject(parentId.Value) as GameObject;
                if (parentGo == null)
                    throw new BridgeToolException($"set_parent: parentInstanceId {parentId.Value} not found.");
                newParent = parentGo.transform;
            }

            // Use Undo.SetTransformParent when keepWorldPosition is false (default local stays),
            // otherwise use RegisterCompleteObjectUndo + SetParent with worldPositionStays.
            // Unity's Undo.SetTransformParent has no worldPositionStays overload in 2022.3.
            if (keepWorld)
            {
                Undo.RegisterCompleteObjectUndo(go.transform, $"Set Parent of '{go.name}'");
                go.transform.SetParent(newParent, true);
            }
            else
            {
                Undo.SetTransformParent(go.transform, newParent, $"Set Parent of '{go.name}'");
            }
            EditorUtility.SetDirty(go);
            return $"Set parent of '{go.name}' ({id}) to {(newParent != null ? $"'{newParent.name}' ({parentId.Value})" : "null (root)")} keepWorldPosition={keepWorld}";
        }

        public static string DeleteGameObject(JObject mcpMessage)
        {
            var id = mcpMessage["instanceId"]?.ToObject<int>() ?? 0;
            if (id == 0) throw new BridgeToolException("delete_gameobject: 'instanceId' is required.");
            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null)
                throw new BridgeToolException($"delete_gameobject: GameObject instanceId {id} not found.");
            var name = go.name;
            Undo.DestroyObjectImmediate(go);
            return $"Deleted GameObject '{name}' ({id}).";
        }

        public static string CopyComponent(JObject mcpMessage)
        {
            var srcId = mcpMessage["sourceInstanceId"]?.ToObject<int>() ?? 0;
            var tgtId = mcpMessage["targetInstanceId"]?.ToObject<int>() ?? 0;
            var srcCompName = mcpMessage["sourceComponent"]?.ToString();
            var srcIndex = mcpMessage["sourceComponentIndex"]?.ToObject<int>() ?? 0;

            if (srcId == 0 || tgtId == 0 || string.IsNullOrWhiteSpace(srcCompName))
                throw new BridgeToolException("copy_component: sourceInstanceId, targetInstanceId and sourceComponent are required.");

            var srcGo = EditorUtility.InstanceIDToObject(srcId) as GameObject;
            var tgtGo = EditorUtility.InstanceIDToObject(tgtId) as GameObject;
            if (srcGo == null) throw new BridgeToolException($"copy_component: source GameObject {srcId} not found.");
            if (tgtGo == null) throw new BridgeToolException($"copy_component: target GameObject {tgtId} not found.");

            if (!TryResolveComponentType(srcCompName, out var type))
                throw new BridgeToolException($"copy_component: Type '{srcCompName}' not found.");

            var srcMatches = srcGo.GetComponents<Component>().Where(c => c != null && c.GetType() == type).ToArray();
            // Fallback to IsAssignableFrom / Name match if exact type not found (e.g. Rig / constraints)
            if (srcMatches.Length == 0)
                srcMatches = srcGo.GetComponents<Component>().Where(c => c != null && c.GetType().Name == type.Name).ToArray();
            if (srcMatches.Length == 0)
                srcMatches = srcGo.GetComponents<Component>().Where(c => c != null && c.GetType().Name == srcCompName).ToArray();
            if (srcMatches.Length == 0)
                throw new BridgeToolException($"copy_component: component '{srcCompName}' not found on '{srcGo.name}'. Available: {string.Join(", ", srcGo.GetComponents<Component>().Where(c=>c!=null).Select(c=>c.GetType().Name))}");
            if (srcIndex >= srcMatches.Length)
                throw new BridgeToolException($"copy_component: sourceComponentIndex {srcIndex} out of range ({srcMatches.Length} found).");

            var srcComp = srcMatches[srcIndex];

            // Add new component to target
            var newComp = Undo.AddComponent(tgtGo, type);
            if (newComp == null)
                throw new BridgeToolException($"copy_component: Failed to add '{type.FullName}' to '{tgtGo.name}'.");

            // Deep copy serialized data (handles m_Data structs for constraints)
            EditorUtility.CopySerialized(srcComp, newComp);

            // Target index among same type
            var tgtMatches = tgtGo.GetComponents<Component>().Where(c => c != null && c.GetType() == type).ToArray();
            int targetIndex = Array.IndexOf(tgtMatches, newComp);
            if (targetIndex < 0) targetIndex = tgtMatches.Length - 1;

            EditorUtility.SetDirty(tgtGo);
            var result = new JObject
            {
                ["targetComponentIndex"] = targetIndex,
                ["targetInstanceId"] = newComp.GetInstanceID(),
                ["targetComponent"] = type.FullName
            };
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static bool TryResolveComponentType(string componentType, out Type type)
        {
            type = null;
            if (string.IsNullOrWhiteSpace(componentType)) return false;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(componentType);
                if (type != null) break;
            }
            if (type == null)
            {
                type = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(t => t.Name == componentType && typeof(Component).IsAssignableFrom(t));
            }
            return type != null && typeof(Component).IsAssignableFrom(type);
        }

        private static Vector3? ParseVector3(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token is JObject o)
            {
                return new Vector3(
                    o["x"]?.ToObject<float>() ?? 0f,
                    o["y"]?.ToObject<float>() ?? 0f,
                    o["z"]?.ToObject<float>() ?? 0f);
            }
            return null;
        }

        private static Quaternion ParseQuaternion(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return Quaternion.identity;
            var o = token as JObject;
            if (o == null) return Quaternion.identity;
            // Support {x,y,z,w} quaternion
            if (o["w"] != null)
            {
                return new Quaternion(
                    o["x"]?.ToObject<float>() ?? 0f,
                    o["y"]?.ToObject<float>() ?? 0f,
                    o["z"]?.ToObject<float>() ?? 0f,
                    o["w"]?.ToObject<float>() ?? 1f);
            }
            // Support {euler:{x,y,z}} or {x,y,z} as euler
            JToken eulerToken = o["euler"] ?? o["eulerAngles"];
            if (eulerToken != null)
            {
                var e = eulerToken as JObject;
                var v = new Vector3(e["x"]?.ToObject<float>() ?? 0f, e["y"]?.ToObject<float>() ?? 0f, e["z"]?.ToObject<float>() ?? 0f);
                return Quaternion.Euler(v);
            }
            // fallback: treat x,y,z as euler
            if (o["x"] != null && o["y"] != null && o["z"] != null && o["w"] == null)
            {
                var v = new Vector3(o["x"].ToObject<float>(), o["y"].ToObject<float>(), o["z"].ToObject<float>());
                return Quaternion.Euler(v);
            }
            return Quaternion.identity;
        }

        private static string GetPath(Transform t)
        {
            var parts = new System.Collections.Generic.List<string>();
            var cur = t;
            while (cur != null)
            {
                parts.Add(cur.name);
                cur = cur.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
