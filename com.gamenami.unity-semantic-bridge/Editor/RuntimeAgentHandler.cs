using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public static class RuntimeAgentHandler
    {
        public static void HandleRequest(List<string> agentActions, SemanticScene sceneData, byte[] image)
        {
            var payload = new {
                agentActions,
                sceneJson = sceneData,
                b64Image = Convert.ToBase64String(image)
            };
            
            EditorBridge.SendToAgent(payload, "gameplay_response");
        }

        public static void HandleFunctionCall(dynamic call)
        {
            string funcName = call.name;
            var args = call.args;
            var intent = call.args.Intent != null ? (string)call.args.Intent : "No Intent";

            // Wrapping in delayCall ensures the click happens safely on the main thread during the next editor update
            EditorApplication.delayCall += () =>
            {
                switch (funcName)
                {
                    case "click_screen_position":
                    {
                        // Gemini sends 0-1 Viewport coordinates
                        var vx = (float)args.screenX;
                        var vy = (float)args.screenY;
                        AgentCommandRelay.ExecuteScreenClick(ConvertToScreenPosition(vx, vy));
                        break;
                    }
                    case "click_ui_button":
                        AgentCommandRelay.ExecuteButtonClick(args.ButtonName.ToString());
                        break;
                }
                AgentCommandRelay.CommandReceived(intent); // allow GameplayAgent to act again
            };
        }

        private static Vector2 ConvertToScreenPosition(float normalizedX, float normalizedY)
        {
            // 1. Flip Y back (Unity Screen/Viewport Y is bottom-up, LLM Y is top-down)
            var correctedY = 1f - normalizedY;

            // 2. Convert 0-1 Viewport to Actual Pixels
            var pixelPosition = new Vector2(
                normalizedX * Screen.width,
                correctedY * Screen.height
            );

            //Debug.Log($"Viewport: {normalizedX},{normalizedY} -> Pixels: {pixelPosition}");
            
            return pixelPosition;
        }
    }
}