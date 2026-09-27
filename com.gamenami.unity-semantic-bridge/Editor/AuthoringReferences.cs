using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Gamenami.UnitySemanticBridge.Editor
{
    // Shared by ordinary serialized-field editing and transactional UI authoring.
    internal static class AuthoringReferences
    {
        internal static Type TypeNamed(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A component/type name is required.");
            var types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a =>
            {
                try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
            }).Where(t => typeof(Object).IsAssignableFrom(t) && (t.FullName == name || t.Name == name)).Distinct().ToArray();
            var exact = types.FirstOrDefault(t => t.FullName == name);
            if (exact != null) return exact;
            if (types.Length != 1) throw new ArgumentException($"Type '{name}' is missing or ambiguous. Use its full namespace; install its package if missing.");
            return types[0];
        }

        internal static Object Resolve(JToken token, Type expected, IReadOnlyDictionary<string, GameObject> locals = null)
        {
            if (token == null || token.Type == JTokenType.Null || token.ToString() == "None" ||
                (token.Type == JTokenType.Integer && token.Value<int>() == 0)) return null;
            Object value = null;
            var spec = token as JObject;
            if (token.Type == JTokenType.Integer) value = EditorIdLookup.FromInstanceId(token.Value<int>());
            else if (spec != null)
            {
                UiProperties.Keys(spec, "ref instanceId globalObjectId guid path fileID component componentIndex");
                var sources = new[] { "ref", "instanceId", "globalObjectId", "guid", "path" }.Count(k => spec[k] != null);
                if (sources != 1) throw new ArgumentException("Object reference requires exactly one of ref, instanceId, globalObjectId, guid or path.");
                if (spec["ref"] != null)
                {
                    var id = spec["ref"].Value<string>();
                    if (locals == null || !locals.TryGetValue(id, out var go)) throw new ArgumentException($"Unknown request-local reference '{id}'. Create it earlier in the batch.");
                    value = go;
                }
                else if (spec["instanceId"] != null) value = EditorIdLookup.FromInstanceId(spec["instanceId"].Value<int>());
                else if (spec["globalObjectId"] != null)
                {
                    if (!GlobalObjectId.TryParse(spec["globalObjectId"].Value<string>(), out var id)) throw new ArgumentException("Invalid globalObjectId.");
                    value = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id);
                }
                else
                {
                    var path = spec["path"]?.Value<string>() ?? AssetDatabase.GUIDToAssetPath(spec["guid"].Value<string>());
                    if (string.IsNullOrEmpty(path) || !(path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)))
                        throw new ArgumentException("Asset reference must resolve to an imported Assets/ or Packages/ path.");
                    if (spec["fileID"] != null)
                    {
                        var fileId = spec["fileID"].Value<long>();
                        value = AssetDatabase.LoadAllAssetsAtPath(path).FirstOrDefault(o =>
                            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out _, out long id) && id == fileId);
                    }
                    else
                    {
                        var main = AssetDatabase.LoadMainAssetAtPath(path);
                        if (main != null && (spec["component"] != null || expected.IsInstanceOfType(main))) value = main;
                        else
                        {
                            var candidates = AssetDatabase.LoadAllAssetsAtPath(path).Where(o => expected.IsInstanceOfType(o)).ToArray();
                            if (candidates.Length > 1) throw new ArgumentException($"'{path}' contains multiple {expected.Name} subassets. Specify guid/path plus fileID explicitly.");
                            value = candidates.SingleOrDefault();
                        }
                    }
                }
                if (spec["component"] != null)
                {
                    var go = value as GameObject ?? (value as Component)?.gameObject;
                    if (go == null) throw new ArgumentException("Component selector needs a GameObject or Component reference.");
                    var type = TypeNamed(spec["component"].Value<string>());
                    if (!typeof(Component).IsAssignableFrom(type)) throw new ArgumentException("Selected type is not a Component.");
                    var components = go.GetComponents(type);
                    var index = spec["componentIndex"]?.Value<int>() ?? 0;
                    if (index < 0 || index >= components.Length) throw new ArgumentException($"Component '{type.Name}' index {index} not found on '{go.name}'.");
                    value = components[index];
                }
            }
            if (value == null) throw new ArgumentException($"Object reference {token} not found. Refresh instance IDs after reload; check GUID/path/fileID.");
            if (!expected.IsInstanceOfType(value)) throw new ArgumentException($"Expected {expected.FullName}, received {value.GetType().FullName}. Select the component explicitly with 'component'.");
            return value;
        }

        internal static Type FieldType(Type owner, string propertyPath)
        {
            var type = owner;
            foreach (var part in propertyPath.Replace(".Array.data[", "[").Split('.'))
            {
                var name = part.Split('[')[0];
                FieldInfo field = null;
                for (var t = type; t != null && field == null; t = t.BaseType)
                    field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field == null) throw new ArgumentException($"Cannot determine serialized type of '{propertyPath}' on {owner.Name}.");
                type = field.FieldType;
                if (part.Contains("[")) type = ElementType(type);
            }
            return type;
        }

        internal static Type ElementType(Type type)
        {
            if (type.IsArray) return type.GetElementType();
            if (type.IsGenericType && typeof(IList).IsAssignableFrom(type)) return type.GetGenericArguments()[0];
            throw new ArgumentException($"{type.Name} is not an array or List<T>.");
        }

        internal static JObject Identity(Object obj)
        {
            if (obj == null) return null;
            return new JObject { ["instanceId"] = obj.GetInstanceID(), ["name"] = obj.name,
                ["type"] = obj.GetType().FullName, ["globalObjectId"] = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString() };
        }
    }
}
