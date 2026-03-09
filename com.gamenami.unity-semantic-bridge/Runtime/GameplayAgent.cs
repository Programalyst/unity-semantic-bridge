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
        [SerializeField] private bool _awaitingResponse; // needed to make agent wait to send next frame capture
        [SerializeField] private bool _isRunning;
        public bool IsRunning => _isRunning;

        [Header("Actions")]
        [SerializeField] private List<string> agentActions = new List<string>();
        
        private const float AGENT_INTERVAL = 3.0f; // Give Gemini time to "Think"
        private float _cooldown = 0f;
        
        private void OnEnable()
        {
            // Clear history on start
            agentActions = new List<string>();
            // Listen for the "Action Done" signal from the Bridge
            AgentCommandRelay.OnCommandReceived += HandleActionComplete;
        }

        private void OnDisable()
        {
            AgentCommandRelay.OnCommandReceived -= HandleActionComplete;
        }
        
        public void StartAgentLoop()
        {
            _isRunning = true;
            _awaitingResponse = false;
            _cooldown = AGENT_INTERVAL; // Allow action immediately
            Debug.Log("<color=cyan>[Agent]</color> Loop Started.");
        }

        public void StopAgentLoop()
        {
            _isRunning = false;
        }
        
        private void HandleActionComplete(string intent)
        {
            var entry = $"Step {agentActions.Count + 1}: {intent}";
            agentActions.Add(entry);
            _awaitingResponse = false; 
        
            // Notify the UI Window to show the log
            BridgeRelay.OnNotifyAgentLogWindow?.Invoke(entry);
        }

        private void Update()
        {
            if (!_isRunning || _awaitingResponse || !BridgeRelay.IsServerConnected()) return;

            _cooldown += Time.deltaTime;
            if (!(_cooldown >= AGENT_INTERVAL)) return;
            
            _cooldown = 0f;
            if (AgentStateRelay.CanAgentAct() && !AgentStateRelay.IsProcessing())
            {
                _awaitingResponse = true;
                CaptureAndSend();
            }
        }

        private void CaptureAndSend()
        {
            var sceneData = SemanticSceneGenerator.Generate(configAsset);
            ScreenshotTool.Instance.GetScreenshotBytes(imageBytes => 
            {
                // Send directly through the bridge
                BridgeRelay.Send(agentActions, sceneData, imageBytes);
            });
        }
    }
}