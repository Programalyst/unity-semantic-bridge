using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class UiInspection
    {
        static JObject V(Vector2 v) => new JObject { ["x"] = v.x, ["y"] = v.y };
        static JObject V3(Vector3 v) => new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
        static GameObject Target(JObject message)
        {
            var go = EditorIdLookup.FromInstanceId(message["instanceId"]?.Value<int>() ?? 0) as GameObject;
            if (go == null || !(go.transform is RectTransform)) throw new ArgumentException("instanceId must identify a loaded GameObject with RectTransform.");
            return go;
        }
        static Camera ViewCamera(JObject message)
        {
            if (message["cameraInstanceId"] == null || message["cameraInstanceId"].Type == JTokenType.Null) return null;
            var obj = EditorIdLookup.FromInstanceId(message["cameraInstanceId"].Value<int>());
            var camera = obj as Camera ?? (obj as GameObject)?.GetComponent<Camera>();
            return camera != null ? camera : throw new ArgumentException("cameraInstanceId must identify a Camera or its GameObject.");
        }

        public static string Inspect(JObject message)
        {
            try
            {
                var root = Target(message); var view = ViewCamera(message);
                var max = message["maxNodes"]?.Value<int>() ?? 100;
                if (max < 1 || max > 1000) throw new ArgumentException("maxNodes must be 1–1000.");
                // Resolve calculated layout, but never instantiate, change selection or save.
                Canvas.ForceUpdateCanvases();
                var all = root.GetComponentsInChildren<RectTransform>(true);
                var nodes = new JArray();
                foreach (var rect in all.Take(max))
                {
                    var node = Describe(rect, view);
                    node["parent"] = AuthoringReferences.Identity(rect.parent?.gameObject);
                    node["components"] = new JArray(rect.GetComponents<Component>().Where(c => c != null).Select(AuthoringReferences.Identity));
                    node["layoutDrivers"] = new JArray(UiLayout.Drivers(rect));
                    var button = rect.GetComponent<Button>();
                    if (button != null)
                    {
                        var calls = new JArray();
                        for (var i = 0; i < button.onClick.GetPersistentEventCount(); i++) calls.Add(new JObject
                        { ["target"] = AuthoringReferences.Identity(button.onClick.GetPersistentTarget(i)), ["method"] = button.onClick.GetPersistentMethodName(i), ["state"] = button.onClick.GetPersistentListenerState(i).ToString() });
                        node["buttonOnClick"] = calls;
                    }
                    if (message["includeReferences"]?.Value<bool>() ?? false)
                    {
                        var refs = new JArray(); bool truncated = false;
                        foreach (var component in rect.GetComponents<Component>().Where(c => c != null))
                        {
                            using (var serialized = new SerializedObject(component))
                            {
                                var p = serialized.GetIterator();
                                while (p.NextVisible(true))
                                {
                                    if (p.propertyType != SerializedPropertyType.ObjectReference || p.name == "m_Script") continue;
                                    if (refs.Count == 100) { truncated = true; break; }
                                    refs.Add(new JObject { ["componentInstanceId"] = component.GetInstanceID(), ["field"] = p.propertyPath,
                                        ["value"] = AuthoringReferences.Identity(p.objectReferenceValue) });
                                }
                            }
                            if (truncated) break;
                        }
                        node["serializedReferences"] = refs; node["referencesTruncated"] = truncated;
                    }
                    nodes.Add(node);
                }
                return new JObject { ["nodes"] = nodes, ["totalNodes"] = all.Length, ["truncated"] = all.Length > max,
                    ["space"] = "RectTransform authored values are parent-local; screen AABBs are projected pixels, origin bottom-left.",
                    ["note"] = "Visibility is an estimate, not an occlusion/shader test. Overlapping screen rectangles do not prove input blocking; use raycast_ui. No layout values or assets are authored by inspection." }.ToString();
            }
            catch (Exception e) { return "Error: " + e.Message; }
        }

        internal static JObject Describe(RectTransform rect, Camera view)
        {
            var go = rect.gameObject; var canvas = go.GetComponentInParent<Canvas>();
            var rootCanvas = canvas != null ? canvas.rootCanvas : null;
            var overlay = rootCanvas != null && rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay;
            var camera = overlay ? null : view != null ? view : rootCanvas != null ? rootCanvas.worldCamera : null;
            var fallback = !overlay && camera == null;
            if (fallback) camera = Camera.main;
            var graphic = go.GetComponent<Graphic>(); var selectable = go.GetComponent<Selectable>();
            var reasons = new JArray();
            if (!go.activeInHierarchy) reasons.Add("inactive hierarchy");
            if (canvas == null) reasons.Add("no Canvas");
            if (canvas != null && !canvas.isActiveAndEnabled) reasons.Add("Canvas disabled");
            if (!overlay && camera == null) reasons.Add("no viewing camera; projection/visibility cannot be established");
            if (camera != null && !camera.isActiveAndEnabled) reasons.Add("viewing camera disabled");
            var excluded = camera != null && (camera.cullingMask & (1 << go.layer)) == 0;
            if (excluded) reasons.Add("viewing camera cullingMask excludes this object's layer; this does NOT necessarily exclude GraphicRaycaster hits");
            var groups = new JArray(); var groupRaycast = true; var groupInteractable = true; var ignore = false;
            for (var t = rect.transform; t != null && !ignore; t = t.parent)
            {
                foreach (var g in t.GetComponents<CanvasGroup>().Where(g => g.isActiveAndEnabled))
                {
                    groups.Add(new JObject { ["instanceId"] = g.GetInstanceID(), ["name"] = g.name, ["alpha"] = g.alpha,
                        ["interactable"] = g.interactable, ["blocksRaycasts"] = g.blocksRaycasts, ["ignoreParentGroups"] = g.ignoreParentGroups });
                    groupRaycast &= g.blocksRaycasts; groupInteractable &= g.interactable;
                    ignore |= g.ignoreParentGroups;
                }
            }
            var alpha = graphic != null ? graphic.color.a * graphic.canvasRenderer.GetInheritedAlpha() : 1;
            if (graphic != null && !graphic.isActiveAndEnabled) reasons.Add("Graphic disabled");
            if (graphic != null && alpha <= 0.001f) reasons.Add("Graphic/CanvasGroup alpha is zero; transparent graphics can still receive raycasts");
            if (graphic != null && graphic.canvasRenderer.cull) reasons.Add("CanvasRenderer culled (for example by a mask)");
            var corners = new Vector3[4]; rect.GetWorldCorners(corners);
            var screen = new JArray(); var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity); var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            bool behind = false;
            foreach (var corner in corners)
            {
                var point = RectTransformUtility.WorldToScreenPoint(camera, corner); screen.Add(V(point));
                min = Vector2.Min(min, point); max = Vector2.Max(max, point);
                if (camera != null && camera.WorldToScreenPoint(corner).z <= 0) behind = true;
            }
            if (behind) reasons.Add("rectangle crosses/is behind camera; projected AABB is not reliable");
            var viewport = camera != null ? camera.pixelRect : new Rect(0, 0, Screen.width, Screen.height);
            if (max.x < viewport.xMin || min.x > viewport.xMax || max.y < viewport.yMin || min.y > viewport.yMax) reasons.Add("outside camera/screen viewport");
            return new JObject
            {
                ["object"] = AuthoringReferences.Identity(go), ["activeSelf"] = go.activeSelf, ["activeInHierarchy"] = go.activeInHierarchy,
                ["layer"] = go.layer, ["canvas"] = AuthoringReferences.Identity(canvas), ["renderMode"] = rootCanvas != null ? rootCanvas.renderMode.ToString() : "none",
                ["viewingCamera"] = AuthoringReferences.Identity(camera), ["usedMainCameraFallback"] = fallback && camera != null,
                ["cameraMaskExcludesObject"] = excluded,
                ["rect"] = new JObject { ["anchorMin"] = V(rect.anchorMin), ["anchorMax"] = V(rect.anchorMax), ["pivot"] = V(rect.pivot),
                    ["anchoredPosition"] = V(rect.anchoredPosition), ["sizeDelta"] = V(rect.sizeDelta), ["offsetMin"] = V(rect.offsetMin), ["offsetMax"] = V(rect.offsetMax),
                    ["localEulerAngles"] = V3(rect.localEulerAngles), ["localScale"] = V3(rect.localScale), ["calculatedSize"] = V(rect.rect.size) },
                ["screenBounds"] = new JObject { ["min"] = V(min), ["max"] = V(max), ["corners"] = screen, ["reliable"] = !behind && (overlay || camera != null) },
                ["graphicPresent"] = graphic != null, ["visibleEstimate"] = graphic != null && reasons.Count == 0,
                ["visibilityReasons"] = reasons, ["effectiveAlpha"] = alpha,
                ["raycastTarget"] = graphic != null && graphic.raycastTarget, ["canvasGroups"] = groups,
                ["raycastsAllowedByGroups"] = groupRaycast,
                ["interactable"] = selectable != null && selectable.isActiveAndEnabled && selectable.IsInteractable(),
                ["interactionAllowedByGroups"] = groupInteractable,
            };
        }

        public static string Raycast(JObject message)
        {
            try
            {
                var pos = new Vector2(UiProperties.Number(message["x"]), UiProperties.Number(message["y"]));
                var view = ViewCamera(message); var hits = new List<RaycastResult>();
                Canvas.ForceUpdateCanvases();
                var system = EventSystem.current;
                var unrendered = Resources.FindObjectsOfTypeAll<Graphic>().Count(g => g != null && g.isActiveAndEnabled &&
                    g.gameObject.scene.IsValid() && !EditorUtility.IsPersistent(g) && g.raycastTarget && g.depth == -1);
                var data = new PointerEventData(system) { position = pos, button = PointerEventData.InputButton.Left };
                if (system != null) system.RaycastAll(data, hits);
                else
                {
                    // BaseRaycaster registration is a Play Mode lifecycle detail. In Edit Mode,
                    // query loaded, enabled scene components directly without enabling them.
                    foreach (var raycaster in Resources.FindObjectsOfTypeAll<BaseRaycaster>()
                        .Where(r => r != null && r.isActiveAndEnabled && r.gameObject.scene.IsValid() && !EditorUtility.IsPersistent(r)))
                        raycaster.Raycast(data, hits);
                }
                var output = new JArray();
                foreach (var hit in hits.Take(200))
                {
                    var rect = hit.gameObject.transform as RectTransform;
                    var visibility = rect != null ? Describe(rect, view) : null;
                    var invisibleHit = visibility != null && visibility["cameraMaskExcludesObject"].Value<bool>();
                    output.Add(new JObject { ["object"] = AuthoringReferences.Identity(hit.gameObject), ["raycaster"] = AuthoringReferences.Identity(hit.module),
                        ["eventCamera"] = AuthoringReferences.Identity(hit.module != null ? hit.module.eventCamera : null),
                        ["distance"] = hit.distance, ["depth"] = hit.depth, ["sortingLayer"] = hit.sortingLayer, ["sortingOrder"] = hit.sortingOrder,
                        ["screenPosition"] = V(hit.screenPosition), ["worldPosition"] = V3(hit.worldPosition),
                        ["visibility"] = visibility,
                        ["warning"] = invisibleHit ? "Actual raycast hit despite viewing-camera culling mask. This invisible UI may block gameplay that gates input on UI hits. Disable its raycastTarget/GraphicRaycaster or use CanvasGroup.blocksRaycasts=false as appropriate." : null });
                }
                return new JObject { ["position"] = V(pos), ["eventSystem"] = AuthoringReferences.Identity(system), ["hits"] = output,
                    ["totalHits"] = hits.Count, ["truncated"] = hits.Count > 200,
                    ["unrenderedRaycastGraphics"] = unrendered,
                    ["geometryWarning"] = unrendered > 0 ? "Some active raycast Graphics have no rendered depth yet. Let the Game view render, then retry; absence of hits is not conclusive for those Graphics." : null,
                    ["ordering"] = system != null ? "EventSystem.RaycastAll priority order (all active registered raycasters)." : "No EventSystem: hits queried directly per active raycaster; not global input-priority order. Add/configure your project's input module for runtime clicks.",
                    ["note"] = "Primary-display screen pixels, origin bottom-left. These are actual raycaster results, not rectangle overlaps. Hits do not prove a gameplay script consumes/blocks input; that depends on its input policy. UI alpha/interactability and camera rendering masks are not equivalent to raycast filtering." }.ToString();
            }
            catch (Exception e) { return "Error: " + e.Message; }
        }
    }
}
