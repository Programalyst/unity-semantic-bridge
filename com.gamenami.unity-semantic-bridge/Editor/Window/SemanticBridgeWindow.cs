using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement; // Don't use Editor scene management so it works during runtime

namespace Gamenami.UnitySemanticBridge.Editor
{
    public class SemanticBridgeWindow : EditorWindow 
    {
        public static SemanticBridgeWindow Instance { get; private set; }
        
        private int _tabSelection = 0;
        private readonly string[] _tabLabels = { "Editor Mode", "Gameplay Mode" };
        
        private SemanticSceneConfigSo _playModeConfig;
        
        private Vector2 _logScroll;
        private readonly List<string> _agentHistory = new List<string>();
        
        [MenuItem("Tools/Unity Semantic Bridge")]
        public static void ShowWindow() 
        {
            var window = GetWindow<SemanticBridgeWindow>("Unity Semantic Bridge");
            window.minSize = new Vector2(600, 500); 
        }
        
        private void OnEnable()
        {
            Instance = this; // Register this window instance
            BridgeRelay.OnAgentMessage -= AddAgentMessage;
            BridgeRelay.OnAgentMessage += AddAgentMessage;
            LoadConfigs();
        }
        
        private void OnDisable()
        {
            if (Instance == this) Instance = null; // Unregister
        }
        
        private void LoadConfigs()
        {
            var guids = AssetDatabase.FindAssets("t:SemanticSceneConfigSo");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<SemanticSceneConfigSo>(path);
                if (path.Contains("PlayMode")) _playModeConfig = asset;
            }
        }
        
        private void OnGUI() 
        {
            DrawConnectionHeader();
            
            // Tab selection
            _tabSelection = GUILayout.Toolbar(_tabSelection, _tabLabels, GUILayout.Height(30));

            EditorGUILayout.Space(10);
            
            switch (_tabSelection)
            {
                case 0:
                    DrawEditorTab();
                    break;
                case 1:
                    DrawGameplayTab();
                    break;
            }
        }
        
        private void DrawConnectionHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            var isConnected = EditorBridge.IsConnected;
            var statusStyle = new GUIStyle(EditorStyles.label) { 
                normal = { textColor = isConnected ? Color.green : Color.gray },
                fontStyle = FontStyle.Bold 
            };
            
            GUILayout.Label(isConnected ? "● Connected" : "○ Offline", statusStyle);
            GUILayout.FlexibleSpace();

            if (!isConnected)
            {
                if (GUILayout.Button("Connect to USB MCP Server")) EditorBridge.ManualConnect();
            }
            else
            {
                if (GUILayout.Button("Disconnect")) EditorBridge.ManualDisconnect();
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawEditorTab()
        {
            var isConnected = EditorBridge.IsConnected;
            if (isConnected) 
                EditorGUILayout.HelpBox("Ready to receive MCP commands.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("Connect to MCP server to start receiving MCP commands.", MessageType.Info);
            
            DrawLogArea("MCP Activity Log", _agentHistory);
        }
        
        private void DrawGameplayTab()
        {
            DrawConfigArea();
            // Re-use your existing Gameplay Agent UI logic
            EditorGUILayout.BeginHorizontal();
            {
                // Column 1: Controls
                EditorGUILayout.BeginVertical(GUILayout.Width(200));
                DrawAgentControls();
                EditorGUILayout.EndVertical();

                // Column 2: Live Intent Log
                EditorGUILayout.BeginVertical();
                DrawLogArea("Agent Intent Log", _agentHistory);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
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
            Repaint(); // redraw UI
        }
        
        private void DrawConfigArea()
        {
            GUILayout.Label("Semantic Scene Generation Config", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            _playModeConfig = (SemanticSceneConfigSo)EditorGUILayout.ObjectField("PlayMode Config Asset", _playModeConfig, 
                typeof(SemanticSceneConfigSo), false);
            if (_playModeConfig)
            {
                DrawConfigColumn("Play Mode Settings", _playModeConfig);
                if (GUILayout.Button("Test Play Mode JSON export")) ExportToJson(_playModeConfig);
            }
            EditorGUILayout.EndVertical();
        }

        private static void DrawConfigColumn(string label, SemanticSceneConfigSo config)
        {
            GUILayout.Label(label, EditorStyles.miniBoldLabel);
            
            var layerNames = new string[32];
            for (var i = 0; i < 32; i++) layerNames[i] = LayerMask.LayerToName(i);
            
            EditorGUI.BeginChangeCheck();
    
            config.maxDepth = EditorGUILayout.IntField("Max Hierarchy Depth", config.maxDepth);
            config.includeComponents = EditorGUILayout.Toggle("Include Components", config.includeComponents);
            config.includeTransforms = EditorGUILayout.Toggle("Include Transforms", config.includeTransforms);
            config.excludeLayers = EditorGUILayout.MaskField("Exclude Layers", config.excludeLayers, layerNames);
            config.includeLayerStats = EditorGUILayout.Toggle("Include Statistics", config.includeLayerStats);

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(config);
                // AssetDatabase.SaveAssets() is heavy, usually SetDirty is enough until Unity auto-saves
            }
        }

        private static void DrawAgentControls()
        {
            bool isConnected = EditorBridge.IsConnected;
            bool isPlayMode = EditorApplication.isPlaying;
            bool agentInScene = GameplayAgent.Instance;
    
            // Determine if we are allowed to click anything
            bool canInteract = isConnected && isPlayMode && agentInScene;

            EditorGUI.BeginDisabledGroup(!canInteract);
            {
                EditorGUILayout.Space(5);

                // Check the actual running state from Gameplay agent
                bool isRunning = agentInScene && GameplayAgent.Instance.IsRunning; 

                if (!isRunning)
                {
                    GUI.backgroundColor = Color.cyan; // Subtle highlight for the start button
                    if (GUILayout.Button("🚀 Start Agent Loop", GUILayout.Height(30)))
                    {
                        GameplayAgent.Instance.StartAgentLoop();
                    }
                }
                else
                {
                    GUI.backgroundColor = new Color(1f, 0.4f, 0.4f); // Subtle red for stop
                    if (GUILayout.Button("🛑 Stop Agent Loop", GUILayout.Height(30)))
                    {
                        GameplayAgent.Instance.StopAgentLoop();
                    }
                }
                GUI.backgroundColor = Color.white; // Reset color for other UI elements
                EditorGUILayout.Space(5);
            }
            EditorGUI.EndDisabledGroup();
            
            // Contextual Feedback
            if (!isConnected) EditorGUILayout.HelpBox("Connect to Server first.", MessageType.None);
            if (!isPlayMode) EditorGUILayout.HelpBox("Enter Play Mode to start.", MessageType.None);
            if (!agentInScene) EditorGUILayout.HelpBox("Add GameplayAgent to scene.", MessageType.Warning);
        }

        private static void ExportToJson(SemanticSceneConfigSo config) 
        {
            var activeScene = SceneManager.GetActiveScene();
            var sceneName = string.IsNullOrEmpty(activeScene.name) ? "UntitledScene" : activeScene.name;
            var sceneData = SemanticSceneGenerator.Generate(config);
            var sceneJson = JsonConvert.SerializeObject(sceneData, new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            });
            
            Debug.Log("Scene Exported (Max Depth: " + config.maxDepth + ")");
            
            // Save to a file
            var path = EditorUtility.SaveFilePanel(
                "Save Semantic Scene", 
                "", 
                $"{sceneName}.json", 
                "json"
            );
                
            if (string.IsNullOrEmpty(path)) return;
            System.IO.File.WriteAllText(path, sceneJson);
            AssetDatabase.Refresh();
        }
    }
}
