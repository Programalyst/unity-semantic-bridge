using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Gamenami.UnitySemanticBridge.Editor
{
    internal static class UiProperties
    {
        static readonly Dictionary<string, string> Allowed = new Dictionary<string, string>
        {
            ["Canvas"] = "renderMode worldCamera planeDistance sortingOrder overrideSorting pixelPerfect targetDisplay enabled",
            ["CanvasScaler"] = "uiScaleMode scaleFactor referencePixelsPerUnit referenceResolution screenMatchMode matchWidthOrHeight physicalUnit fallbackScreenDPI defaultSpriteDPI dynamicPixelsPerUnit enabled",
            ["GraphicRaycaster"] = "ignoreReversedGraphics blockingObjects blockingMask enabled",
            ["Image"] = "color sprite type preserveAspect fillCenter fillMethod fillAmount fillClockwise fillOrigin raycastTarget enabled",
            ["Button"] = "interactable navigation transition colors targetGraphic enabled",
            ["TextMeshProUGUI"] = "text font fontSize color alignment fontStyle enableWordWrapping overflowMode raycastTarget enabled",
            ["HorizontalLayoutGroup"] = "spacing padding childAlignment childControlWidth childControlHeight childForceExpandWidth childForceExpandHeight childScaleWidth childScaleHeight reverseArrangement enabled",
            ["VerticalLayoutGroup"] = "spacing padding childAlignment childControlWidth childControlHeight childForceExpandWidth childForceExpandHeight childScaleWidth childScaleHeight reverseArrangement enabled",
            ["GridLayoutGroup"] = "cellSize spacing padding childAlignment startCorner startAxis constraint constraintCount enabled",
            ["LayoutElement"] = "ignoreLayout minWidth minHeight preferredWidth preferredHeight flexibleWidth flexibleHeight layoutPriority enabled",
            ["ContentSizeFitter"] = "horizontalFit verticalFit enabled",
            ["CanvasGroup"] = "alpha interactable blocksRaycasts ignoreParentGroups enabled",
        };

        internal static Type ComponentType(string name)
        {
            var type = AuthoringReferences.TypeNamed(name);
            if (!Allowed.ContainsKey(type.Name)) throw new ArgumentException($"'{name}' is not a supported uGUI authoring component.");
            return type;
        }

        internal static void Validate(Type type, JObject values, Action<JToken, Type> reference)
        {
            if (values == null) return;
            if (!Allowed.TryGetValue(type.Name, out var names)) throw new ArgumentException($"Cannot configure {type.Name}; use set_field_values for other components.");
            foreach (var p in values.Properties())
            {
                if (!names.Split(' ').Contains(p.Name)) throw new ArgumentException($"Unsupported {type.Name} property '{p.Name}'. Supported: {names}.");
                var property = type.GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite) throw new ArgumentException($"{type.Name}.{p.Name} is not supported by this Unity/package version.");
                ConvertValue(p.Value, property.PropertyType, (token, expected) => { reference(token, expected); return null; });
            }
        }

        internal static void Apply(Component target, JObject values, IReadOnlyDictionary<string, GameObject> locals)
        {
            if (values == null) return;
            Undo.RegisterCompleteObjectUndo(target, "Author uGUI");
            foreach (var p in values.Properties())
            {
                var property = target.GetType().GetProperty(p.Name);
                if (property.PropertyType == typeof(Navigation) || property.PropertyType == typeof(ColorBlock))
                {
                    var current = property.GetValue(target);
                    foreach (var part in ((JObject)p.Value).Properties())
                    {
                        var nested = property.PropertyType.GetProperty(part.Name);
                        nested.SetValue(current, ConvertValue(part.Value, nested.PropertyType, (token, expected) => AuthoringReferences.Resolve(token, expected, locals)));
                    }
                    property.SetValue(target, current);
                }
                else property.SetValue(target, ConvertValue(p.Value, property.PropertyType, (token, expected) => AuthoringReferences.Resolve(token, expected, locals)));
            }
            UiAuthoring.Touch(target);
        }

        internal static object ConvertValue(JToken value, Type type, Func<JToken, Type, Object> reference)
        {
            if (typeof(Object).IsAssignableFrom(type)) return reference(value, type);
            if (value == null || value.Type == JTokenType.Null) throw new ArgumentException($"Null is not valid for {type.Name}.");
            if (type == typeof(string)) { if (value.Type != JTokenType.String) throw new ArgumentException("Expected a string."); return value.Value<string>(); }
            if (type == typeof(bool)) { if (value.Type != JTokenType.Boolean) throw new ArgumentException("Expected a boolean."); return value.Value<bool>(); }
            if (type == typeof(float)) return Number(value);
            if (type == typeof(int)) { if (value.Type != JTokenType.Integer) throw new ArgumentException("Expected an integer."); return value.Value<int>(); }
            if (type == typeof(LayerMask)) return (LayerMask)(int)ConvertValue(value, typeof(int), reference);
            if (type.IsEnum)
            {
                if (value.Type != JTokenType.String || !Enum.IsDefined(type, value.Value<string>()))
                    throw new ArgumentException($"Expected a {type.Name} name: {string.Join(", ", Enum.GetNames(type))}.");
                return Enum.Parse(type, value.Value<string>());
            }
            if (!(value is JObject o)) throw new ArgumentException($"Expected an object for {type.Name}.");
            if (type == typeof(Vector2)) { Keys(o, "x y", true); return new Vector2(Number(o["x"]), Number(o["y"])); }
            if (type == typeof(Vector3)) { Keys(o, "x y z", true); return new Vector3(Number(o["x"]), Number(o["y"]), Number(o["z"])); }
            if (type == typeof(Color)) { Keys(o, "r g b a"); return new Color(Number(o["r"]), Number(o["g"]), Number(o["b"]), o["a"] == null ? 1 : Number(o["a"])); }
            if (type == typeof(RectOffset))
            {
                Keys(o, "left right top bottom", true);
                return new RectOffset((int)ConvertValue(o["left"], typeof(int), reference), (int)ConvertValue(o["right"], typeof(int), reference),
                    (int)ConvertValue(o["top"], typeof(int), reference), (int)ConvertValue(o["bottom"], typeof(int), reference));
            }
            if (type == typeof(Navigation) || type == typeof(ColorBlock))
            {
                Keys(o, type == typeof(Navigation) ? "mode wrapAround selectOnUp selectOnDown selectOnLeft selectOnRight" :
                    "normalColor highlightedColor pressedColor selectedColor disabledColor colorMultiplier fadeDuration");
                object result = type == typeof(Navigation) ? (object)Navigation.defaultNavigation : ColorBlock.defaultColorBlock;
                foreach (var p in o.Properties())
                {
                    var property = type.GetProperty(p.Name);
                    property.SetValue(result, ConvertValue(p.Value, property.PropertyType, reference));
                }
                return result;
            }
            throw new ArgumentException($"Unsupported authored value type {type.FullName}.");
        }

        internal static float Number(JToken t)
        {
            if (t == null || (t.Type != JTokenType.Float && t.Type != JTokenType.Integer)) throw new ArgumentException("Expected a finite number.");
            var n = t.Value<float>(); if (float.IsNaN(n) || float.IsInfinity(n)) throw new ArgumentException("Expected a finite number.");
            return n;
        }
        internal static void Keys(JObject o, string names, bool required = false)
        {
            var keys = names.Split(' ');
            foreach (var p in o.Properties()) if (!keys.Contains(p.Name)) throw new ArgumentException($"Unknown property '{p.Name}'. Expected: {names}.");
            if (required) foreach (var key in keys) if (o[key] == null) throw new ArgumentException($"Missing '{key}'.");
        }
    }
}
