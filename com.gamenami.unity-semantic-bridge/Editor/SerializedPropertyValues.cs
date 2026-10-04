using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    // Structured inspection and strict validation around the existing property writer.
    // Never calls ApplyModifiedProperties; callers own Undo and application.
    internal static class SerializedPropertyValues
    {
        static JObject Vector(params float[] v)
        {
            var o = new JObject(); var keys = new[] { "x", "y", "z", "w" };
            for (int i = 0; i < v.Length; i++) o[keys[i]] = (double)v[i];
            return o;
        }
        internal static JToken Read(SerializedProperty p, SemanticBatch.Budget b, int depth = 0)
        {
            b.Value(depth);
            if (p.isArray && p.propertyType != SerializedPropertyType.String)
            {
                if (p.arraySize > b.maxCollection) throw new SemanticBatch.LimitException("collectionExpansion");
                var array = new JArray();
                for (int i = 0; i < p.arraySize; i++) array.Add(Read(p.GetArrayElementAtIndex(i), b, depth + 1));
                return array;
            }
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: case SerializedPropertyType.LayerMask: return new JValue(p.longValue);
                case SerializedPropertyType.Float: return new JValue(p.doubleValue);
                case SerializedPropertyType.Boolean: return new JValue(p.boolValue);
                case SerializedPropertyType.String:
                    if (p.stringValue.Length > 4096) throw new SemanticBatch.LimitException("stringLength");
                    return new JValue(p.stringValue);
                case SerializedPropertyType.Enum: return new JValue(p.enumValueIndex);
                case SerializedPropertyType.Vector2: var v2 = p.vector2Value; return Vector(v2.x, v2.y);
                case SerializedPropertyType.Vector3: var v3 = p.vector3Value; return Vector(v3.x, v3.y, v3.z);
                case SerializedPropertyType.Vector4: var v4 = p.vector4Value; return Vector(v4.x, v4.y, v4.z, v4.w);
                case SerializedPropertyType.Quaternion: var q = p.quaternionValue; return Vector(q.x, q.y, q.z, q.w);
                case SerializedPropertyType.Color: var c = p.colorValue; return new JObject { ["r"] = (double)c.r, ["g"] = (double)c.g, ["b"] = (double)c.b, ["a"] = (double)c.a };
                case SerializedPropertyType.Vector2Int: var i2 = p.vector2IntValue; return new JObject { ["x"] = i2.x, ["y"] = i2.y };
                case SerializedPropertyType.Vector3Int: var i3 = p.vector3IntValue; return new JObject { ["x"] = i3.x, ["y"] = i3.y, ["z"] = i3.z };
                case SerializedPropertyType.ObjectReference:
                    var obj = p.objectReferenceValue;
                    if (obj == null) return JValue.CreateNull();
                    var id = AuthoringReferences.Identity(obj);
                    // Value identities are descriptive, not reusable mutation references.
                    id.Remove("instanceId"); return id;
                case SerializedPropertyType.Rect:
                    var r = p.rectValue; return new JObject { ["x"] = (double)r.x, ["y"] = (double)r.y, ["width"] = (double)r.width, ["height"] = (double)r.height };
                case SerializedPropertyType.Bounds:
                    var bounds = p.boundsValue; return new JObject { ["center"] = Vector(bounds.center.x, bounds.center.y, bounds.center.z), ["size"] = Vector(bounds.size.x, bounds.size.y, bounds.size.z) };
                case SerializedPropertyType.Generic:
                    var result = new JObject(); var it = p.Copy(); var end = p.GetEndProperty();
                    bool next = it.Next(true); int children = 0;
                    while (next && !SerializedProperty.EqualContents(it, end) && it.depth > p.depth)
                    {
                        if (++children > b.maxCollection) throw new SemanticBatch.LimitException("collectionExpansion");
                        result[it.name] = Read(it, b, depth + 1); next = it.Next(false);
                    }
                    return result;
                default: throw new ArgumentException($"Structured inspection does not support {p.propertyType} at {p.propertyPath}.");
            }
        }
        internal static bool Equal(JToken actual, JToken expected)
        {
            // Numeric JSON int/float spellings compare by exact numeric value. No epsilon.
            if (actual == null || expected == null) return actual == expected;
            bool Number(JToken v) => v.Type == JTokenType.Integer || v.Type == JTokenType.Float;
            if (Number(actual) && Number(expected))
            {
                if (actual.Type == JTokenType.Integer && expected.Type == JTokenType.Integer) return actual.Value<long>() == expected.Value<long>();
                return actual.Value<double>() == expected.Value<double>();
            }
            if (actual is JObject a && expected is JObject e)
                return a.Count == e.Count && a.Properties().All(p => e[p.Name] != null && Equal(p.Value, e[p.Name]));
            if (actual is JArray aa && expected is JArray ea)
                return aa.Count == ea.Count && aa.Where((t, i) => !Equal(t, ea[i])).Count() == 0;
            return JToken.DeepEquals(actual, expected);
        }
        static void Require(bool ok, SerializedProperty p, string expected)
        {
            if (!ok) throw new ArgumentException($"{p.propertyPath}: expected {expected}.");
        }
        static void Numeric(JToken value, SerializedProperty p, bool integer = false)
        {
            Require(value != null && (value.Type == JTokenType.Integer || (!integer && value.Type == JTokenType.Float)), p, integer ? "integer" : "finite number");
            double n = value.Value<double>();
            Require(!double.IsNaN(n) && !double.IsInfinity(n) && (integer ? n >= int.MinValue && n <= int.MaxValue : Math.Abs(n) <= float.MaxValue), p, integer ? "32-bit integer" : "finite float");
        }
        static void Axes(JToken value, SerializedProperty p, string keys, bool integer = false)
        {
            var obj = value as JObject; Require(obj != null, p, "numeric object");
            SemanticBatch.Keys(obj, keys);
            foreach (var key in keys.Split(' ')) Numeric(obj[key], p, integer);
        }
        internal static JToken Validate(SerializedProperty p, JToken value, SemanticBatch.Budget b, int depth = 0)
        {
            b.Value(depth);
            Require(p.editable, p, "editable property");
            if (p.isArray && p.propertyType != SerializedPropertyType.String)
            {
                var a = value as JArray; Require(a != null, p, "array");
                if (a.Count > b.maxCollection) throw new SemanticBatch.LimitException("collectionExpansion");
                // Resize only the uncommitted serialized buffer, allowing validation of new elements.
                p.arraySize = a.Count; var normalized = new JArray();
                for (int i = 0; i < a.Count; i++) normalized.Add(Validate(p.GetArrayElementAtIndex(i), a[i], b, depth + 1));
                return normalized;
            }
            if (p.propertyType == SerializedPropertyType.Generic)
            {
                var obj = value as JObject; Require(obj != null, p, "object");
                if (obj.Count > b.maxCollection) throw new SemanticBatch.LimitException("collectionExpansion");
                var normalized = new JObject();
                foreach (var kv in obj)
                {
                    var child = p.FindPropertyRelative(kv.Key) ?? throw new ArgumentException("Property not found: " + p.propertyPath + "." + kv.Key);
                    normalized[kv.Key] = Validate(child, kv.Value, b, depth + 1);
                }
                return normalized;
            }
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: case SerializedPropertyType.LayerMask: Numeric(value, p, true); break;
                case SerializedPropertyType.Float: Numeric(value, p); break;
                case SerializedPropertyType.Boolean: Require(value?.Type == JTokenType.Boolean, p, "Boolean"); break;
                case SerializedPropertyType.String:
                    Require(value?.Type == JTokenType.String, p, "string");
                    if (value.Value<string>().Length > 4096) throw new SemanticBatch.LimitException("stringLength");
                    break;
                case SerializedPropertyType.Enum:
                    if (value?.Type == JTokenType.String) Require(Array.IndexOf(p.enumNames, value.Value<string>()) >= 0, p, "known enum name");
                    else { Numeric(value, p, true); Require(value.Value<int>() >= 0 && value.Value<int>() < p.enumNames.Length, p, "valid enum index"); }
                    break;
                case SerializedPropertyType.Vector2: Axes(value, p, "x y"); break;
                case SerializedPropertyType.Vector3: Axes(value, p, "x y z"); break;
                case SerializedPropertyType.Vector2Int: Axes(value, p, "x y", true); break;
                case SerializedPropertyType.Vector3Int: Axes(value, p, "x y z", true); break;
                case SerializedPropertyType.Quaternion:
                    if (value is JObject quat && quat["euler"] != null) { SemanticBatch.Keys(quat, "euler"); Axes(quat["euler"], p, "x y z"); }
                    else Axes(value, p, (value as JObject)?["w"] == null ? "x y z" : "x y z w");
                    break;
                case SerializedPropertyType.Color:
                    var color = value as JObject; Require(color != null, p, "color object"); SemanticBatch.Keys(color, "r g b a");
                    foreach (var k in new[] { "r", "g", "b" }) Numeric(color[k], p);
                    if (color["a"] != null) Numeric(color["a"], p);
                    break;
                case SerializedPropertyType.ObjectReference:
                    var resolved = SemanticBatch.ResolveValueReference(value);
                    if (resolved == null) return JValue.CreateNull();
                    Type expected;
                    try { expected = AuthoringReferences.FieldType(p.serializedObject.targetObject.GetType(), p.propertyPath); }
                    catch (ArgumentException)
                    {
                        var nativeType = p.type;
                        if (!nativeType.StartsWith("PPtr<", StringComparison.Ordinal) || !nativeType.EndsWith(">", StringComparison.Ordinal))
                            throw new ArgumentException("Cannot establish native object-reference type at " + p.propertyPath);
                        var typeName = nativeType.Substring(5, nativeType.Length - 6).TrimStart('$');
                        expected = typeName == "Object" ? typeof(UnityEngine.Object) : AuthoringReferences.TypeNamed(typeName);
                    }
                    Require(expected.IsInstanceOfType(resolved), p, "compatible object reference");
                    var referenceGo = resolved as GameObject ?? (resolved as Component)?.gameObject;
                    var targetGo = (p.serializedObject.targetObject as Component)?.gameObject;
                    if (referenceGo != null && !EditorUtility.IsPersistent(referenceGo) && targetGo != null && referenceGo.scene != targetGo.scene)
                        throw new ArgumentException("Scene object references must stay in the target scene/Prefab Stage.");
                    // Native fields have no reflected FieldInfo: verify Unity accepts the value
                    // in the discarded serialized buffer, rather than treating null as success.
                    p.objectReferenceValue = resolved;
                    Require(p.objectReferenceValue == resolved, p, "compatible native object reference");
                    return new JValue(resolved.GetInstanceID());
                default: throw new ArgumentException($"Existing field setter does not support {p.propertyType} at {p.propertyPath}.");
            }
            return value.DeepClone();
        }
    }
}
