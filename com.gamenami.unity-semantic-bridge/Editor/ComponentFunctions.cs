using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class ComponentFunctions
    {
        public static string GetComponentCode(JObject mcpMessage) 
        {
            var componentName = mcpMessage["componentName"]?.ToString();
            
            // Find the script asset by name
            var guids = AssetDatabase.FindAssets($"{componentName} t:MonoScript");
            if (guids.Length == 0) return $"Source code for {componentName} not found.";

            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
            try 
            {
                var fullPath = System.IO.Path.GetFullPath(path);
                return System.IO.File.ReadAllText(fullPath);
            } 
            catch (Exception e) 
            {
                return $"Error reading file: {e.Message}";
            }
        }
        
        public static string GetComponentInspectorValues(JObject mcpMessage)
        {
            var id = (int)mcpMessage["instanceId"];
            var compName = mcpMessage["componentName"]?.ToString();

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return "Error: GameObject not found.";

            var comp = go.GetComponent(compName);
            if (comp == null) return $"Error: Component '{compName}' not found on {go.name}.";

            var sb = new StringBuilder();
            sb.AppendLine($"--- Inspector: {go.name} > {compName} ---");
            ComponentInspector.AppendComponentProperties(sb, comp);
            return sb.ToString();
        }
        
        public static string AddComponent(JObject mcpMessage)
        {
            var id = (int)mcpMessage["instanceId"];
            var componentType = mcpMessage["componentType"]?.ToString();
            var allowDuplicate = mcpMessage["allowDuplicate"]?.ToObject<bool>() ?? false;

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return "Error: GameObject not found.";

            // Resolve the type across all loaded assemblies
            Type type = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (componentType != null) type = assembly.GetType(componentType);
                if (type != null) break;
            }
            // Fallback: simple name match restricted to Component subclasses
            if (type == null)
            {
                type = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(t => t.Name == componentType && typeof(Component).IsAssignableFrom(t));
            }
            if (type == null) return $"Error: Type '{componentType}' not found in any loaded assembly.";

            if (!allowDuplicate)
            {
                var existing = go.GetComponent(type);
                if (existing != null)
                    return $"Skipped: '{componentType}' already exists on '{go.name}' (instanceId: {existing.GetInstanceID()}). Set allowDuplicate=true to override.";
            }

            var added = Undo.AddComponent(go, type);
            if (added == null) return $"Error: AddComponent failed for '{componentType}'. Ensure it is a valid non-abstract Component subclass.";

            EditorUtility.SetDirty(go);

            return $"Added '{type.FullName}' to '{go.name}'. New component instanceId: {added.GetInstanceID()}.";
        }

        public static string SetFieldValues(JObject mcpMessage)
        {
            var id = (int)mcpMessage["instanceId"];
            var componentName = mcpMessage["componentName"]?.ToString();
            var fields = mcpMessage["fields"] as JObject;
            var componentIndex = mcpMessage["componentIndex"]?.ToObject<int>() ?? 0;

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return "Error: GameObject not found.";

            var matches = go.GetComponents<Component>()
                .Where(c => c != null && c.GetType().Name == componentName)
                .ToArray();

            if (matches.Length == 0) return $"Error: Component '{componentName}' not found on '{go.name}'.";
            if (componentIndex >= matches.Length) return $"Error: componentIndex {componentIndex} out of range — found {matches.Length} '{componentName}' component(s).";

            var target = matches[componentIndex];
            var so = new SerializedObject(target);
            Undo.RecordObject(target, $"Set fields on {componentName}");

            var sb = new StringBuilder();
            sb.AppendLine($"--- SetFieldValue: {go.name} > {componentName} ---");

            foreach (var kvp in fields)
            {
                var prop = so.FindProperty(kvp.Key);
                if (prop == null)
                {
                    sb.AppendLine($"{kvp.Key}: ERROR — property not found.");
                    continue;
                }

                try
                {
                    ApplyPropertyValue(prop, kvp.Value);
                    sb.AppendLine($"{kvp.Key}: OK");
                }
                catch (Exception e)
                {
                    sb.AppendLine($"{kvp.Key}: ERROR — {e.Message}");
                }
            }

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(go);

            return sb.ToString();
        }

        private static void ApplyPropertyValue(SerializedProperty prop, JToken value)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    prop.intValue = value.ToObject<int>(); 
                    break;
                case SerializedPropertyType.Float:
                    prop.floatValue = value.ToObject<float>(); 
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = value.ToObject<bool>(); 
                    break;
                case SerializedPropertyType.String:
                    prop.stringValue = value.ToObject<string>(); 
                    break;
                case SerializedPropertyType.Enum:
                    prop.enumValueIndex = value.Type == JTokenType.String
                        ? Array.IndexOf(prop.enumNames, value.ToObject<string>())
                        : value.ToObject<int>(); 
                    break;
                case SerializedPropertyType.Vector2:
                    var j2 = (JObject)value;
                    prop.vector2Value = new Vector2(j2["x"].ToObject<float>(), j2["y"].ToObject<float>()); 
                    break;
                case SerializedPropertyType.Vector3:
                    var j3 = (JObject)value;
                    prop.vector3Value = new Vector3(j3["x"].ToObject<float>(), j3["y"].ToObject<float>(), j3["z"].ToObject<float>()); 
                    break;
                case SerializedPropertyType.Quaternion:
                    var jq = (JObject)value;
                    prop.quaternionValue = new Quaternion(jq["x"].ToObject<float>(), jq["y"].ToObject<float>(), jq["z"].ToObject<float>(), jq["w"].ToObject<float>()); 
                    break;
                case SerializedPropertyType.Color:
                    var jc = (JObject)value;
                    prop.colorValue = new Color(jc["r"].ToObject<float>(), jc["g"].ToObject<float>(), jc["b"].ToObject<float>(), jc["a"]?.ToObject<float>() ?? 1f); 
                    break;
                case SerializedPropertyType.ObjectReference:
                    prop.objectReferenceValue = EditorUtility.InstanceIDToObject(value.ToObject<int>()); 
                    break;
                case SerializedPropertyType.LayerMask:
                    prop.intValue = value.ToObject<int>(); 
                    break;
                default:
                    throw new NotSupportedException($"SerializedPropertyType '{prop.propertyType}' is not supported.");
            }
        }
    }
}
