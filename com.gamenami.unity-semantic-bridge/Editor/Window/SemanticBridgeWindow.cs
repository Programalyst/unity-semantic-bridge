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
            DrawPortSettings();
        }

        private int? _listenerPortEdit, _eventPortEdit;
        private string _portError;

        private void DrawPortSettings()
        {
            var settings = BridgeSettings.instance;
            EditorGUILayout.BeginHorizontal();
            _listenerPortEdit = EditorGUILayout.IntField("Listener port", _listenerPortEdit ?? settings.ListenerPort);
            _eventPortEdit = EditorGUILayout.IntField("Event port", _eventPortEdit ?? settings.EventPort);
            var changed = _listenerPortEdit != settings.ListenerPort || _eventPortEdit != settings.EventPort;
            using (new EditorGUI.DisabledScope(!changed))
            {
                if (GUILayout.Button("Apply", GUILayout.Width(60)))
                {
                    _portError = EditorBridge.SetPorts(_listenerPortEdit.Value, _eventPortEdit.Value);
                    if (_portError == null) _listenerPortEdit = _eventPortEdit = null;
                    GUI.FocusControl(null);
                }
            }
            EditorGUILayout.EndHorizontal();

            if (_portError != null)
                EditorGUILayout.HelpBox(_portError, MessageType.Error);
            if (EditorBridge.LastStartError != null && !EditorBridge.IsConnected)
                EditorGUILayout.HelpBox(EditorBridge.LastStartError, MessageType.Error);
            if (settings.ListenerPort != BridgeSettings.DefaultListenerPort || settings.EventPort != BridgeSettings.DefaultEventPort)
                EditorGUILayout.HelpBox("Start this project's MCP server with: " +
                    $"--unity-port {settings.ListenerPort} --event-port {settings.EventPort}", MessageType.None);
        }
        
        private void DrawEditorContent()
        {
            var isConnected = EditorBridge.IsConnected;
            if (isConnected) 
                EditorGUILayout.HelpBox($"HTTP bridge active at http://127.0.0.1:{EditorBridge.ListeningPort}/rpc — ready to receive JSON-RPC commands.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("Start the HTTP listener to receive commands from MCP server", MessageType.Info);
            
            DrawTools();
            EditorGUILayout.Space(10);
            DrawLogArea("MCP Activity Log", _agentHistory);
        }

        private string _scanRoots = "Assets/Synty";

        private void DrawTools()
        {
            GUILayout.Label("Bridge Tools", EditorStyles.boldLabel);
            _scanRoots = EditorGUILayout.TextField("Scan Roots (comma-separated)", _scanRoots);
            if (GUILayout.Button("Generate Asset Catalog"))
            {
                var roots = ParseScanRoots(_scanRoots);
                var start = AssetCatalogRunner.Start(roots, true, result =>
                {
                    if (result != null)
                        AddAgentMessage($"[human] generate_asset_catalog -> {result["itemCount"]} items, {result["thumbnailCount"]} thumbs: {result["jsonPath"]}");
                    else
                        AddAgentMessage("[human] generate_asset_catalog cancelled or failed — see Console.");
                });
                AddAgentMessage($"[human] generate_asset_catalog started: {start}");
            }
        }

        private static string[] ParseScanRoots(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null; // null = defaults
            var roots = text.Split(new[] { ',', ';', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < roots.Length; i++)
                roots[i] = roots[i].Trim().TrimEnd('/');
            return roots.Length > 0 ? roots : null;
        }
        
        private void DrawLogArea(string areaTitle, IEnumerable<string> logs)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(areaTitle, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Copy Log", GUILayout.Width(90)))
                GUIUtility.systemCopyBuffer = string.Join("\n", _agentHistory);
            EditorGUILayout.EndHorizontal();
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
