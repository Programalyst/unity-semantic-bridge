using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    public class SemanticBridgeWindow : EditorWindow 
    {
        public static SemanticBridgeWindow Instance { get; private set; }
        
        private Vector2 _logScroll;
        private readonly List<string> _agentHistory = new List<string>();
        
        [MenuItem("Tools/Unity Semantic Bridge")]
        public static void ShowWindow() 
        {
            var window = GetWindow<SemanticBridgeWindow>("Unity Semantic Bridge");
            window.minSize = new Vector2(600, 400); 
        }
        
        private void OnEnable()
        {
            Instance = this;
            BridgeRelay.OnAgentMessage -= AddAgentMessage;
            BridgeRelay.OnAgentMessage += AddAgentMessage;
        }
        
        private void OnDisable()
        {
            if (Instance == this) Instance = null;
        }
        
        private void OnGUI() 
        {
            DrawConnectionHeader();
            EditorGUILayout.Space(10);
            DrawEditorContent();
        }
        
        private void DrawConnectionHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            var isConnected = EditorBridge.IsConnected;
            var statusStyle = new GUIStyle(EditorStyles.label) { 
                normal = { textColor = isConnected ? Color.green : Color.gray },
                fontStyle = FontStyle.Bold 
            };
            
            GUILayout.Label(isConnected ? $"● Listening on :{EditorBridge.ListeningPort}" : "○ Offline", statusStyle);
            GUILayout.FlexibleSpace();

            if (!isConnected)
            {
                if (GUILayout.Button("Start HTTP Listener")) EditorBridge.ManualConnect();
            }
            else
            {
                if (GUILayout.Button("Stop Listener")) EditorBridge.ManualDisconnect();
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawEditorContent()
        {
            var isConnected = EditorBridge.IsConnected;
            if (isConnected) 
                EditorGUILayout.HelpBox($"HTTP bridge active at http://127.0.0.1:{EditorBridge.ListeningPort}/mcp — ready to receive MCP commands.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("Start the HTTP listener to receive MCP commands.", MessageType.Info);
            
            DrawLogArea("MCP Activity Log", _agentHistory);
        }
        
        private void DrawLogArea(string areaTitle, IEnumerable<string> logs)
        {
            GUILayout.Label(areaTitle, EditorStyles.boldLabel);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, EditorStyles.helpBox);
            foreach (var log in logs)
            {
                GUILayout.Label(log, EditorStyles.wordWrappedLabel);
            }
            EditorGUILayout.EndScrollView();
        }
        
        private void AddAgentMessage(string text)
        {
            _agentHistory.Add(text);
            Repaint();
        }
    }
}
