using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Gamenami.UnitySemanticBridge.Editor
{
    internal static class UiLayout
    {
        internal static void Validate(JObject rect)
        {
            if (rect == null) return;
            UiProperties.Keys(rect, "anchorMin anchorMax pivot anchoredPosition sizeDelta offsetMin offsetMax localEulerAngles localScale");
            if ((rect["offsetMin"] != null || rect["offsetMax"] != null) && (rect["anchoredPosition"] != null || rect["sizeDelta"] != null))
                throw new ArgumentException("Use offsets OR anchoredPosition/sizeDelta in one operation, not both. Anchors and pivot apply first.");
            foreach (var p in rect.Properties()) UiProperties.ConvertValue(p.Value,
                p.Name == "localEulerAngles" || p.Name == "localScale" ? typeof(Vector3) : typeof(Vector2), null);
        }

        internal static List<string> Drivers(RectTransform rect)
        {
            var result = new List<string>();
            if (rect.drivenByObject != null) result.Add($"Unity driver: {rect.drivenByObject.name} ({rect.drivenByObject.GetType().Name})");
            var ignore = rect.GetComponent<LayoutElement>();
            var parent = rect.parent != null ? rect.parent.GetComponent<LayoutGroup>() : null;
            if (parent != null && parent.isActiveAndEnabled && !(ignore != null && ignore.isActiveAndEnabled && ignore.ignoreLayout))
                result.Add($"Parent {parent.GetType().Name}: child anchors/position; sizes according to childControlWidth/Height (Grid controls both). Use LayoutElement preferred/min sizes.");
            var fitter = rect.GetComponent<ContentSizeFitter>();
            if (fitter != null && fitter.isActiveAndEnabled && (fitter.horizontalFit != ContentSizeFitter.FitMode.Unconstrained || fitter.verticalFit != ContentSizeFitter.FitMode.Unconstrained))
                result.Add($"ContentSizeFitter: horizontal={fitter.horizontalFit}, vertical={fitter.verticalFit} controls size.");
            var aspect = rect.GetComponent<AspectRatioFitter>();
            if (aspect != null && aspect.isActiveAndEnabled && aspect.aspectMode != AspectRatioFitter.AspectMode.None) result.Add("AspectRatioFitter controls size/anchors.");
            var canvas = rect.GetComponent<Canvas>();
            if (canvas != null && canvas.isRootCanvas && canvas.renderMode != RenderMode.WorldSpace) result.Add("Root screen-space Canvas: screen/scaler controls its RectTransform.");
            return result;
        }

        internal static void CheckDriven(RectTransform rect, JObject values, bool allow, List<string> warnings)
        {
            if (values == null || values.Count == 0) return;
            var drivers = Drivers(rect);
            if (drivers.Count == 0) return;
            var explanation = $"'{rect.name}' has layout drivers: {string.Join("; ", drivers)}. Requested RectTransform values may be overwritten. Configure the driver instead, or set allowDriven=true to acknowledge.";
            if (!allow) throw new ArgumentException(explanation);
            warnings.Add(explanation);
        }

        internal static void Apply(RectTransform rect, JObject values, bool allow, List<string> warnings)
        {
            if (values == null) return;
            CheckDriven(rect, values, allow, warnings);
            Undo.RegisterCompleteObjectUndo(rect, "Author uGUI layout");
            // Deterministic ordering independent of JSON key order.
            foreach (var key in new[] { "anchorMin", "anchorMax", "pivot", "anchoredPosition", "sizeDelta", "offsetMin", "offsetMax", "localEulerAngles", "localScale" })
            {
                if (values[key] == null) continue;
                var property = typeof(RectTransform).GetProperty(key);
                property.SetValue(rect, UiProperties.ConvertValue(values[key], property.PropertyType, null));
            }
            UiAuthoring.Touch(rect);
        }
    }
}
