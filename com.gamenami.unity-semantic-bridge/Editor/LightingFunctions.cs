using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class LightingFunctions
    {
        public static string GetLightsAffectingObject(JObject mcpMessage)
        {
            var id = (int)mcpMessage["instanceID"];

            var go = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (go == null) return "Error: GameObject not found.";

            var targetPos = go.transform.position;
            var allLights = Object.FindObjectsOfType<Light>();

            var sb = new StringBuilder();
            sb.AppendLine($"--- Lights Affecting: {go.name} ---");
            sb.AppendLine($"Target Position: {targetPos}");
            sb.AppendLine($"Total lights in scene: {allLights.Length}");
            sb.AppendLine();

            int inRangeCount = 0;

            foreach (var light in allLights)
            {
                float distance = Vector3.Distance(targetPos, light.transform.position);
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
                sb.AppendLine($"  Distance to Target:   {distance:F3}");
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
            
            var sb = new StringBuilder();
            sb.AppendLine("--- URP Pipeline Settings ---");
            sb.AppendLine($"Asset Name:               {pipelineAsset.name}");
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
