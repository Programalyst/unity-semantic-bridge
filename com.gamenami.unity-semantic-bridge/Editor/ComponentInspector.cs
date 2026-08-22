using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class ComponentInspector
    {
        public static void AppendComponentProperties(StringBuilder sb, Component comp, string indent = "")
        {
            var so = new SerializedObject(comp);
            var prop = so.GetIterator();
            bool enterChildren = true;
            while (prop.NextVisible(enterChildren))
            {
                if (prop.name == "m_Script") { enterChildren = false; continue; }

                int relativeDepth = prop.depth - 1;
                if (relativeDepth < 0) relativeDepth = 0;
                string depthIndent = indent + new string(' ', relativeDepth * 2);

                // Handle arrays manually to avoid Unity's internal Array/size/data indirection and error spam
                if (prop.isArray)
                {
                    sb.AppendLine($"{depthIndent}{prop.displayName} ({prop.name}): Array size {prop.arraySize}");
                    for (int i = 0; i < prop.arraySize; i++)
                    {
                        var elem = prop.GetArrayElementAtIndex(i);
                        string elemIndent = depthIndent + "  ";
                        sb.AppendLine($"{elemIndent}Element {i} ({elem.name}):");
                        if (elem.propertyType == SerializedPropertyType.Generic)
                            PrintGenericChildren(sb, elem, elemIndent + "  ");
                        else
                            sb.AppendLine($"{elemIndent}  {elem.displayName} ({elem.name}): {FormatValue(elem)}");
                    }
                    enterChildren = false;
                    continue;
                }

                if (prop.propertyType == SerializedPropertyType.Generic)
                {
                    sb.AppendLine($"{depthIndent}{prop.displayName} ({prop.name}):");
                    enterChildren = true;
                }
                else
                {
                    string value = FormatValue(prop);
                    sb.AppendLine($"{depthIndent}{prop.displayName} ({prop.name}): {value}");
                    enterChildren = false;
                }
            }
        }

        private static void PrintGenericChildren(StringBuilder sb, SerializedProperty parent, string indent)
        {
            var it = parent.Copy();
            var end = it.GetEndProperty();
            // Move to first child of parent
            bool hasNext = it.NextVisible(true);
            while (hasNext && !SerializedProperty.EqualContents(it, end))
            {
                if (it.depth <= parent.depth) break;

                string childIndent = indent + new string(' ', (it.depth - parent.depth - 1) * 2);

                if (it.isArray)
                {
                    sb.AppendLine($"{childIndent}{it.displayName} ({it.name}): Array size {it.arraySize}");
                    for (int i = 0; i < it.arraySize; i++)
                    {
                        var elem = it.GetArrayElementAtIndex(i);
                        sb.AppendLine($"{childIndent}  Element {i} ({elem.name}):");
                        if (elem.propertyType == SerializedPropertyType.Generic)
                            PrintGenericChildren(sb, elem, childIndent + "    ");
                        else
                            sb.AppendLine($"{childIndent}    {elem.displayName} ({elem.name}): {FormatValue(elem)}");
                    }
                    // advance past this array's children
                    hasNext = it.NextVisible(false);
                    continue;
                }

                if (it.propertyType == SerializedPropertyType.Generic)
                {
                    sb.AppendLine($"{childIndent}{it.displayName} ({it.name}):");
                    PrintGenericChildren(sb, it, childIndent + "  ");
                    hasNext = it.NextVisible(false);
                    continue;
                }

                sb.AppendLine($"{childIndent}{it.displayName} ({it.name}): {FormatValue(it)}");
                hasNext = it.NextVisible(false);
            }
        }

        private static string FormatValue(SerializedProperty prop)
        {
            if (prop.name == "m_LocalRotation" && prop.propertyType == SerializedPropertyType.Quaternion)
            {
                var q = prop.quaternionValue;
                var e = q.eulerAngles;
                return $"quat({q.x:F3},{q.y:F3},{q.z:F3},{q.w:F3}) euler({e.x:F1},{e.y:F1},{e.z:F1})";
            }

            return prop.propertyType switch
            {
                SerializedPropertyType.Integer => prop.intValue.ToString(),
                SerializedPropertyType.Boolean => prop.boolValue.ToString(),
                SerializedPropertyType.Float => prop.floatValue.ToString(CultureInfo.InvariantCulture),
                SerializedPropertyType.String => prop.stringValue,
                SerializedPropertyType.Color => prop.colorValue.ToString(),
                SerializedPropertyType.ObjectReference => FormatObjectReference(prop),
                SerializedPropertyType.Enum => FormatEnum(prop),
                SerializedPropertyType.Vector2 => prop.vector2Value.ToString(),
                SerializedPropertyType.Vector3 => prop.vector3Value.ToString(),
                SerializedPropertyType.Vector4 => prop.vector4Value.ToString(),
                SerializedPropertyType.Rect => prop.rectValue.ToString(),
                SerializedPropertyType.ArraySize => prop.arraySize.ToString(),
                SerializedPropertyType.Character => prop.intValue.ToString(),
                SerializedPropertyType.AnimationCurve => prop.animationCurveValue != null ? $"Curve({prop.animationCurveValue.length} keys)" : "None",
                SerializedPropertyType.Bounds => prop.boundsValue.ToString(),
                SerializedPropertyType.Gradient => "Gradient",
                SerializedPropertyType.Quaternion => FormatQuaternion(prop.quaternionValue),
                SerializedPropertyType.ExposedReference => prop.exposedReferenceValue != null ? prop.exposedReferenceValue.name : "None",
                SerializedPropertyType.FixedBufferSize => prop.fixedBufferSize.ToString(),
                SerializedPropertyType.Vector2Int => prop.vector2IntValue.ToString(),
                SerializedPropertyType.Vector3Int => prop.vector3IntValue.ToString(),
                SerializedPropertyType.RectInt => prop.rectIntValue.ToString(),
                SerializedPropertyType.BoundsInt => prop.boundsIntValue.ToString(),
                SerializedPropertyType.ManagedReference => prop.managedReferenceValue != null ? prop.managedReferenceValue.ToString() : "None",
                _ => $"[{prop.propertyType}]"
            };
        }

        private static string FormatObjectReference(SerializedProperty prop)
        {
            var obj = prop.objectReferenceValue;
            if (obj == null) return "None";
            return $"{obj.name} (InstanceId:{obj.GetInstanceID()}, Type:{obj.GetType().Name})";
        }

        private static string FormatEnum(SerializedProperty prop)
        {
            if (prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumNames.Length)
                return prop.enumNames[prop.enumValueIndex];
            return prop.enumValueIndex.ToString();
        }

        private static string FormatQuaternion(Quaternion q)
        {
            var e = q.eulerAngles;
            return $"quat({q.x:F3},{q.y:F3},{q.z:F3},{q.w:F3}) euler({e.x:F1},{e.y:F1},{e.z:F1})";
        }
    }
}
