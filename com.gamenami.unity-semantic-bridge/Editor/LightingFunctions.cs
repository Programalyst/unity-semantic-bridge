using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class LightingFunctions
    {
        public static string GetLightsAffectingObject(JObject mcpMessage)
        {
            var id = (int)mcpMessage["instanceID"];

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return "Error: GameObject not found.";

            // Use closest point on bounds rather than pivot, so large/flat objects (e.g. terrain)
            // don't report misleading distances based on an arbitrary transform origin.
            Bounds? bounds = null;
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                bounds = renderer.bounds;
            }
            else
            {
                var collider = go.GetComponent<Collider>();
                if (collider != null) bounds = collider.bounds;
            }

            var pivotPos = go.transform.position;
            var allLights = Object.FindObjectsOfType<Light>();

            var sb = new StringBuilder();
            sb.AppendLine($"--- Lights Affecting: {go.name} ---");
            sb.AppendLine($"Target Pivot Position: {pivotPos}");
            if (bounds.HasValue)
                sb.AppendLine($"Target Bounds: center={bounds.Value.center}, size={bounds.Value.size}");
            else
                sb.AppendLine("Target Bounds: N/A (no Renderer or Collider found — falling back to pivot position)");
            sb.AppendLine($"Total lights in scene: {allLights.Length}");
            sb.AppendLine();

            int inRangeCount = 0;

            foreach (var light in allLights)
            {
                // Distance to nearest surface point if we have bounds, otherwise fall back to pivot
                Vector3 closestPoint = bounds.HasValue ? bounds.Value.ClosestPoint(light.transform.position) : pivotPos;
                float distance = Vector3.Distance(closestPoint, light.transform.position);
                bool inRange = light.type == LightType.Directional || distance <= light.range;
                if (inRange) inRangeCount++;

                sb.AppendLine($"Light: {light.name}");
                sb.AppendLine($"  Type:                 {light.type}");
                sb.AppendLine($"  Intensity:            {light.intensity}");
                sb.AppendLine($"  Range:                {(light.type == LightType.Directional ? "N/A (Directional)" : light.range.ToString())}");
                sb.AppendLine($"  Position:             {light.transform.position}");
                sb.AppendLine($"  Lightmapping Mode:    {light.lightmapBakeType}");
                sb.AppendLine($"  Culling Mask:         {light.cullingMask} ({LayerMaskToNames(light.cullingMask)})");
                sb.AppendLine($"  Rendering Layer Mask: {light.renderingLayerMask}");
                sb.AppendLine($"  Distance to Nearest Point: {distance:F3}");
                sb.AppendLine($"  In Range:             {inRange}");
                sb.AppendLine();
            }

            sb.AppendLine($"Lights in range of '{go.name}': {inRangeCount} / {allLights.Length}");

            return sb.ToString();
        }

        private static string LayerMaskToNames(int cullingMask)
        {
            var names = new List<string>();
            for (int i = 0; i < 32; i++)
            {
                if ((cullingMask & (1 << i)) != 0)
                {
                    var layerName = LayerMask.LayerToName(i);
                    if (!string.IsNullOrEmpty(layerName))
                        names.Add(layerName);
                }
            }
            return names.Count > 0 ? string.Join(", ", names) : "None";
        }
        
        public static string GetUrpPipelineSettings()
        {
            var pipelineAsset = GraphicsSettings.currentRenderPipeline;
            if (pipelineAsset == null)
                return "Error: No render pipeline asset assigned in Graphics Settings.";

            var assetType = pipelineAsset.GetType();
            var typeName = assetType.FullName;

            if (typeName != "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset")
                return $"Error: Current pipeline is '{typeName}', not URP.";

            // Helper to safely read a property via reflection
            string Get(string propName)
            {
                var val = assetType.GetProperty(propName)?.GetValue(pipelineAsset);
                return val?.ToString() ?? "N/A";
            }

            // Rendering Path (Forward / Forward+ / Deferred) lives on the active renderer
            // data asset, not on the pipeline asset itself — needs a second reflection hop.
            string GetRenderingPath()
            {
                try
                {
                    var rendererDataListField = assetType.GetField("m_RendererDataList",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    var defaultIndexField = assetType.GetField("m_DefaultRendererIndex",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    if (rendererDataListField == null) return "N/A (field not found - check URP version)";

                    // Treat as untyped Array/object[] instead of ScriptableRendererData[] to avoid needing a URP assembly reference
                    var rendererDataListObj = rendererDataListField.GetValue(pipelineAsset) as System.Array;
                    int defaultIndex = defaultIndexField != null ? (int)defaultIndexField.GetValue(pipelineAsset) : 0;

                    if (rendererDataListObj == null || rendererDataListObj.Length <= defaultIndex)
                        return "N/A (no active renderer data)";

                    var rendererData = rendererDataListObj.GetValue(defaultIndex);
                    if (rendererData == null) return "N/A (renderer data is null)";
                    
                    var rendererDataType = rendererData.GetType();

                    // Try public property first, fall back to private field (varies by URP version)
                    var modeProp = rendererDataType.GetProperty("renderingModeRequested",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var modeVal = modeProp?.GetValue(rendererData);

                    if (modeVal == null)
                    {
                        var modeField = rendererDataType.GetField("m_RenderingMode",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        modeVal = modeField?.GetValue(rendererData);
                    }

                    return modeVal?.ToString() ?? "N/A (rendering mode field not found - check URP version)";
                }
                catch (Exception e)
                {
                    return $"N/A (error: {e.Message})";
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("--- URP Pipeline Settings ---");
            sb.AppendLine($"Asset Name:               {pipelineAsset.name}");
            sb.AppendLine($"Rendering Path:           {GetRenderingPath()}");
            sb.AppendLine($"HDR Enabled:              {Get("supportsHDR")}");
            sb.AppendLine($"MSAA Sample Count:        {Get("msaaSampleCount")}");
            sb.AppendLine($"Shadow Distance:          {Get("shadowDistance")}");
            sb.AppendLine($"Cascade Count:            {Get("shadowCascadeCount")}");
            sb.AppendLine($"Main Light Mode:          {Get("mainLightRenderingMode")}");
            sb.AppendLine($"Additional Lights Mode:   {Get("additionalLightsRenderingMode")}");
            sb.AppendLine($"Per-Object Light Limit:   {Get("maxAdditionalLightsCount")}");
            sb.AppendLine($"Supports Terrain Holes:   {Get("supportsTerrainHoles")}");

            return sb.ToString();
        }
    }
}
