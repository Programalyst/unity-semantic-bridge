using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class ComponentInspector
    {
        // Appends every visible serialized property of a single component.
        // Shared by GetComponentInspectorValues (one component, full detail)
        // and InspectGameObject (all components, quick overview).
        public static void AppendComponentProperties(StringBuilder sb, Component comp, string indent = "")
        {
            var so = new SerializedObject(comp);
            var prop = so.GetIterator();
            var enterChildren = true;
            while (prop.NextVisible(enterChildren))
            {
                enterChildren = false; // don't descend into nested sub-properties
                if (prop.name == "m_Script") continue;
                sb.AppendLine($"{indent}{prop.displayName} ({prop.name}): {GetPropertyValue(prop)}");
            }
        }

        private static string GetPropertyValue(SerializedProperty prop)
        {
            return prop.propertyType switch
            {
                SerializedPropertyType.Integer => prop.intValue.ToString(),
                SerializedPropertyType.Boolean => prop.boolValue.ToString(),
                SerializedPropertyType.Float => prop.floatValue.ToString(CultureInfo.InvariantCulture),
                SerializedPropertyType.String => prop.stringValue,
                SerializedPropertyType.Color => prop.colorValue.ToString(),
                SerializedPropertyType.ObjectReference => prop.objectReferenceValue != null
                    ? prop.objectReferenceValue.name
                    : "None",
                // guarded vs. the original's direct index — enumValueIndex can legally be
                // out of range for some flags/serialization edge cases
                SerializedPropertyType.Enum => (prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumNames.Length)
                    ? prop.enumNames[prop.enumValueIndex]
                    : prop.enumValueIndex.ToString(),
                SerializedPropertyType.Vector2 => prop.vector2Value.ToString(),
                SerializedPropertyType.Vector3 => prop.vector3Value.ToString(),
                SerializedPropertyType.Rect => prop.rectValue.ToString(),
                SerializedPropertyType.ArraySize => prop.arraySize.ToString(),
                _ => $"[{prop.propertyType}]"
            };
        }
    }
}