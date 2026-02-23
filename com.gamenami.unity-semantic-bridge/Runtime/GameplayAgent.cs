using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge
{
    public class GameplayAgent : AgentSingleton<GameplayAgent>
    {
        // Get this from the Package Runtime folder
        [Header("Scene Semantic Generation Settings")]
        [SerializeField] private SemanticSceneConfigSo configAsset;
        
        [Header("Status")]
        [SerializeField] private bool awaitingResponse;
        
        [Header("Actions")]
        [SerializeField] private List<string> agentActions;
        
        private const float AGENT_INTERVAL = 1.0f; // Slower interval for LLM processing
        private float _cooldown = 0f;

        private void OnEnable()
        {
            agentActions = new List<string>();
            AgentCommandRelay.OnCommandReceived += HandleAgentCommand;
        }
        
        private void OnDisable()
        {
            AgentCommandRelay.OnCommandReceived -= HandleAgentCommand;
        }

        private void FixedUpdate()
        {
            _cooldown += Time.fixedDeltaTime;
            if (!(_cooldown >= AGENT_INTERVAL)) return;
            
            _cooldown = 0f;
            TryToAct();
        }
        
        private void HandleAgentCommand(string agentIntent)
        {
            agentActions.Add(agentIntent);
            StartCoroutine(DelayedResetWaitingResponse());
        }

        private IEnumerator DelayedResetWaitingResponse()
        {
            yield return new WaitForSeconds(AGENT_INTERVAL);
            awaitingResponse = false;
        }

        private void TryToAct()
        {
            if (awaitingResponse) return;
            
            // Use AgentStateRelay. If no game logic is linked, it defaults to 'false'
            if (!AgentStateRelay.CanAgentAct()) return;
            if (AgentStateRelay.IsProcessing()) return;
            
            CaptureAndSend();
        }

        private void CaptureAndSend()
        {
            // 1. Generate Semantic Scene Representation 
            var sceneJson = SemanticSceneGenerator.Generate(configAsset);

            // 2. Capture the Screenshot (Vision)
            ScreenshotTool.Instance.GetScreenshotBytes(imageBytes => 
            {
                // 3. Send both to Server via MPE Bridge
                BridgeRelay.Send(sceneJson, imageBytes);
                awaitingResponse = true;
                
                Debug.Log($"[Agent] Context sent. Scene JSON size: {sceneJson.Length / 1024}KB. Image size: {imageBytes.Length / 1024}KB.");
            });
        }
    }
}