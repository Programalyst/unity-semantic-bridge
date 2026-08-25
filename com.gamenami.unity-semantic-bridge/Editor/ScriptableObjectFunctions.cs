using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class ScriptableObjectFunctions
    {
        public static string CreateScriptableObject(JObject mcpMessage)
        {
            var typeName = mcpMessage["type"]?.ToString();
            var path = mcpMessage["path"]?.ToString();
            var fields = mcpMessage["fields"] as JObject;
            var confirm = mcpMessage["confirm"]?.ToObject<bool>() ?? false;

            if (string.IsNullOrWhiteSpace(typeName))
                return "Error: 'type' is required.";
            if (string.IsNullOrWhiteSpace(path))
                return "Error: 'path' is required.";

            // Validate path
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                return "Error: path must start with \"Assets/\" and be project-relative.";
            if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return "Error: path must end with \".asset\".";

            var parentFolder = Path.GetDirectoryName(path)?.Replace("\\", "/");
            if (string.IsNullOrEmpty(parentFolder))
                parentFolder = "Assets";
            if (!AssetDatabase.IsValidFolder(parentFolder))
            {
                // Also check via Directory exists as fallback
                var fullParent = Path.GetFullPath(Path.Combine(Application.dataPath, "..", parentFolder));
                if (!Directory.Exists(fullParent))
                    return $"Error: parent folder \"{parentFolder}\" does not exist.";
            }

            // Resolve type
            if (!TryResolveScriptableObjectType(typeName, out var type))
                return $"Error: Unknown ScriptableObject type '{typeName}'. Ensure fully-qualified name (e.g. Data.AssetData.UnitVisualInfoSo) and assembly is loaded.";

            if (!typeof(ScriptableObject).IsAssignableFrom(type))
                return $"Error: Type '{typeName}' is not a ScriptableObject subclass.";

            // Check existing
            var fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                return "Error: path escapes project root.";

            var existing = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (existing != null && !confirm)
            {
                // Build preview similar to WriteScript confirm pattern
                var existingGuid = AssetDatabase.AssetPathToGUID(path);
                // Try to dump inspector preview
                string preview = "";
                try
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"Existing asset: {existing.name} (type {existing.GetType().FullName}, guid {existingGuid}, instanceId {existing.GetInstanceID()})");
                    // Append few serialized properties
                    var soTmp = new SerializedObject(existing);
                    var prop = soTmp.GetIterator();
                    bool enter = true;
                    int lines = 0;
                    while (prop.NextVisible(enter) && lines < 10)
                    {
                        enter = false;
                        if (prop.name == "m_Script") continue;
                        sb.AppendLine($"  {prop.displayName} ({prop.name}): {prop.propertyType}");
                        lines++;
                    }
                    preview = sb.ToString();
                }
                catch { preview = $"Existing guid {existingGuid}"; }

                return $"CONFIRM_REQUIRED: '{path}' already exists. {preview} Re-call with confirm:true to overwrite.";
            }

            // If confirm and exists, delete first to allow overwrite
            if (existing != null && confirm)
            {
                AssetDatabase.DeleteAsset(path);
                AssetDatabase.Refresh();
            }

            // Create instance
            ScriptableObject instance;
            try
            {
                instance = ScriptableObject.CreateInstance(type);
            }
            catch (Exception e)
            {
                return $"Error: Failed to create instance of '{typeName}': {e.Message}";
            }

            // Apply fields if any
            if (fields != null && fields.Count > 0)
            {
                var so = new SerializedObject(instance);
                foreach (var kvp in fields)
                {
                    var prop = so.FindProperty(kvp.Key);
                    if (prop == null)
                    {
                        // Try case-insensitive search via iterator
                        prop = FindPropertyCaseInsensitive(so, kvp.Key);
                        if (prop == null)
                            return $"Error: Unknown field '{kvp.Key}' for type '{typeName}'.";
                    }
                    try
                    {
                        ApplyPropertyValue(prop, kvp.Value);
                    }
                    catch (Exception e)
                    {
                        // Map enum invalid to specific message
                        if (e is ArgumentException || e.Message.Contains("enum"))
                            return $"Error: Invalid enum value '{kvp.Value}' for field '{kvp.Key}': {e.Message}";
                        return $"Error: Failed to set field '{kvp.Key}': {e.Message}";
                    }
                }
                so.ApplyModifiedProperties();
            }

            // Register undo before creating asset
            Undo.RegisterCreatedObjectUndo(instance, "Create " + type.Name);

            try
            {
                AssetDatabase.CreateAsset(instance, path);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                return $"Error: AssetDatabase.CreateAsset failed: {e.Message}";
            }

            var guid = AssetDatabase.AssetPathToGUID(path);
            var result = new JObject
            {
                ["instanceId"] = instance.GetInstanceID(),
                ["guid"] = guid,
                ["path"] = path
            };
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static SerializedProperty FindPropertyCaseInsensitive(SerializedObject so, string name)
        {
            var prop = so.GetIterator();
            bool enter = true;
            while (prop.NextVisible(enter))
            {
                enter = false;
                if (prop.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return prop;
            }
            return null;
        }

        private static bool TryResolveScriptableObjectType(string typeName, out Type type)
        {
            type = null;
            if (string.IsNullOrWhiteSpace(typeName)) return false;

            // First try exact GetType
            type = Type.GetType(typeName);
            if (type != null) return typeof(ScriptableObject).IsAssignableFrom(type);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName);
                if (type != null) break;
            }
            if (type != null) return typeof(ScriptableObject).IsAssignableFrom(type);

            // Fallback by short name across all assemblies where is ScriptableObject
            var shortName = typeName.Contains(".") ? typeName.Substring(typeName.LastIndexOf('.') + 1) : typeName;
            type = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(t => t.Name == shortName && typeof(ScriptableObject).IsAssignableFrom(t));
            if (type != null) return true;

            // Also try full name case-insensitive
            type = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(t => t.FullName != null && t.FullName.Equals(typeName, StringComparison.OrdinalIgnoreCase) && typeof(ScriptableObject).IsAssignableFrom(t));

            return type != null;
        }

        private static void ApplyPropertyValue(SerializedProperty prop, JToken value)
        {
            if (prop.isArray)
            {
                if (value.Type != JTokenType.Array)
                    throw new NotSupportedException($"Property '{prop.name}' is an array but value is {value.Type}, expected JSON array.");
                var arr = (JArray)value;
                prop.arraySize = arr.Count;
                for (int i = 0; i < arr.Count; i++)
                {
                    var element = prop.GetArrayElementAtIndex(i);
                    if (element.propertyType == SerializedPropertyType.Generic)
                        ApplyGenericValue(element, arr[i]);
                    else
                        ApplyPropertyValue(element, arr[i]);
                }
                return;
            }
            if (prop.propertyType == SerializedPropertyType.Generic)
            {
                ApplyGenericValue(prop, value);
                return;
            }

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
                    {
                        string strVal = value.ToString();
                        int idx = -1;
                        // Try case-insensitive match
                        for (int i = 0; i < prop.enumNames.Length; i++)
                        {
                            if (prop.enumNames[i].Equals(strVal, StringComparison.OrdinalIgnoreCase))
                            {
                                idx = i;
                                break;
                            }
                        }
                        if (idx == -1)
                        {
                            // Try numeric
                            if (value.Type == JTokenType.Integer)
                                idx = value.ToObject<int>();
                            else
                                throw new ArgumentException($"Invalid enum value '{strVal}'. Valid: {string.Join(", ", prop.enumNames)}");
                        }
                        if (idx < 0 || idx >= prop.enumNames.Length)
                            throw new ArgumentException($"Invalid enum value '{strVal}'. Valid: {string.Join(", ", prop.enumNames)}");
                        prop.enumValueIndex = idx;
                    }
                    break;
                case SerializedPropertyType.Vector2:
                    {
                        var j2 = (JObject)value;
                        prop.vector2Value = new Vector2(j2["x"].ToObject<float>(), j2["y"].ToObject<float>());
                    }
                    break;
                case SerializedPropertyType.Vector3:
                    {
                        var j3 = (JObject)value;
                        prop.vector3Value = new Vector3(j3["x"].ToObject<float>(), j3["y"].ToObject<float>(), j3["z"].ToObject<float>());
                    }
                    break;
                case SerializedPropertyType.Quaternion:
                    {
                        var jq = (JObject)value;
                        if (jq["w"] != null)
                            prop.quaternionValue = new Quaternion(jq["x"].ToObject<float>(), jq["y"].ToObject<float>(), jq["z"].ToObject<float>(), jq["w"].ToObject<float>());
                        else if (jq["euler"] != null)
                        {
                            var e = (JObject)jq["euler"];
                            prop.quaternionValue = Quaternion.Euler(e["x"].ToObject<float>(), e["y"].ToObject<float>(), e["z"].ToObject<float>());
                        }
                        else
                            prop.quaternionValue = Quaternion.Euler(jq["x"].ToObject<float>(), jq["y"].ToObject<float>(), jq["z"].ToObject<float>());
                    }
                    break;
                case SerializedPropertyType.Color:
                    {
                        var jc = (JObject)value;
                        prop.colorValue = new Color(jc["r"].ToObject<float>(), jc["g"].ToObject<float>(), jc["b"].ToObject<float>(), jc["a"]?.ToObject<float>() ?? 1f);
                    }
                    break;
                case SerializedPropertyType.ObjectReference:
                    {
                        if (value.Type == JTokenType.Null || (value.Type == JTokenType.String && (value.ToString() == "None" || string.IsNullOrEmpty(value.ToString()))))
                        {
                            prop.objectReferenceValue = null;
                        }
                        else if (value.Type == JTokenType.Integer)
                        {
                            prop.objectReferenceValue = EditorUtility.InstanceIDToObject(value.ToObject<int>());
                        }
                        else if (value.Type == JTokenType.String)
                        {
                            string s = value.ToString();
                            // Check if it's a GUID (32 hex chars)
                            if (s.Length == 32 && IsHexString(s))
                            {
                                string assetPath = AssetDatabase.GUIDToAssetPath(s);
                                if (!string.IsNullOrEmpty(assetPath))
                                    prop.objectReferenceValue = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                                else
                                    throw new ArgumentException($"GUID '{s}' not found.");
                            }
                            else
                            {
                                // Try as asset path
                                var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(s);
                                if (obj != null)
                                    prop.objectReferenceValue = obj;
                                else
                                    throw new ArgumentException($"Object reference string '{s}' is not a valid guid or asset path.");
                            }
                        }
                        else if (value is JObject jo)
                        {
                            if (jo["instanceId"] != null)
                                prop.objectReferenceValue = EditorUtility.InstanceIDToObject(jo["instanceId"].ToObject<int>());
                            else if (jo["guid"] != null)
                            {
                                string g = jo["guid"].ToString();
                                string ap = AssetDatabase.GUIDToAssetPath(g);
                                if (!string.IsNullOrEmpty(ap))
                                    prop.objectReferenceValue = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ap);
                                else
                                    throw new ArgumentException($"GUID '{g}' not found.");
                            }
                            else if (jo["fileID"] != null && jo["guid"] != null)
                            {
                                // For Sprite/Mesh/Material as fileID+guid — fallback to guid load
                                string g = jo["guid"].ToString();
                                string ap = AssetDatabase.GUIDToAssetPath(g);
                                if (!string.IsNullOrEmpty(ap))
                                    prop.objectReferenceValue = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ap);
                                else
                                    prop.objectReferenceValue = null;
                            }
                            else
                                throw new ArgumentException($"Unsupported object reference JObject: {jo}");
                        }
                        else
                        {
                            throw new ArgumentException($"Unsupported object reference value type {value.Type}");
                        }
                    }
                    break;
                case SerializedPropertyType.LayerMask:
                    prop.intValue = value.ToObject<int>();
                    break;
                case SerializedPropertyType.Vector2Int:
                    {
                        var j2i = (JObject)value;
                        prop.vector2IntValue = new Vector2Int(j2i["x"].ToObject<int>(), j2i["y"].ToObject<int>());
                    }
                    break;
                case SerializedPropertyType.Vector3Int:
                    {
                        var j3i = (JObject)value;
                        prop.vector3IntValue = new Vector3Int(j3i["x"].ToObject<int>(), j3i["y"].ToObject<int>(), j3i["z"].ToObject<int>());
                    }
                    break;
                default:
                    throw new NotSupportedException($"SerializedPropertyType '{prop.propertyType}' is not supported for '{prop.name}'.");
            }
        }

        private static void ApplyGenericValue(SerializedProperty prop, JToken value)
        {
            if (value is JObject obj)
            {
                foreach (var kvp in obj)
                {
                    var child = prop.FindPropertyRelative(kvp.Key);
                    if (child == null)
                        child = FindChildCaseInsensitive(prop, kvp.Key);
                    if (child == null)
                        throw new NotSupportedException($"Generic property '{prop.name}' has no child '{kvp.Key}'.");
                    ApplyPropertyValue(child, kvp.Value);
                }
            }
            else if (value is JArray arr && prop.isArray)
            {
                prop.arraySize = arr.Count;
                for (int i = 0; i < arr.Count; i++)
                {
                    var element = prop.GetArrayElementAtIndex(i);
                    ApplyPropertyValue(element, arr[i]);
                }
            }
            else
            {
                throw new NotSupportedException($"Generic property '{prop.name}' expects JObject value, got {value.Type}.");
            }
        }

        private static SerializedProperty FindChildCaseInsensitive(SerializedProperty parent, string name)
        {
            // Iterate children case-insensitive
            var it = parent.Copy();
            var end = it.GetEndProperty();
            bool enter = true;
            if (!it.NextVisible(enter)) return null;
            enter = false;
            while (!SerializedProperty.EqualContents(it, end))
            {
                if (it.depth <= parent.depth) break;
                if (it.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return it;
                if (!it.NextVisible(false)) break;
            }
            return null;
        }

        private static bool IsHexString(string s)
        {
            foreach (char c in s)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }
    }
}
