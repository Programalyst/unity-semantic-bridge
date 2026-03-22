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
        
        private SemanticSceneConfigSo _editorConfig;
        private SemanticSceneConfigSo _playModeConfig;
        
        private Vector2 _logScroll;
        private readonly List<string> _agentHistory = new List<string>();
        private bool _scrollToBottom;
        
        
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
        
        private void LoadConfigs()
        {
            var guids = AssetDatabase.FindAssets("t:SemanticSceneConfigSo");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<SemanticSceneConfigSo>(path);
                if (path.Contains("Editor")) _editorConfig = asset;
                else if (path.Contains("PlayMode")) _playModeConfig = asset;
            }
        }
        
        private void OnDisable()
        {
            if (Instance == this) Instance = null; // Unregister
        }

        private void OnGUI() 
        {
            EditorGUILayout.BeginVertical(GUILayout.ExpandHeight(false));
            {
                EditorGUILayout.Space(10);
                DrawConnectionArea();
                EditorGUILayout.Space(10);
                DrawConfigArea();
                EditorGUILayout.Space(10);
            }
            EditorGUILayout.EndVertical();
            
            DrawGameAgentBox();
        }
        
        private void DrawGameAgentBox()
        {
            GUILayout.Label("Gameplay Agent", EditorStyles.boldLabel);
            // Flexbox to hold columns
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            {
                // Column 1
                EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Width(200), GUILayout.ExpandHeight(true));
                {
                    DrawAgentControls();
                }
                EditorGUILayout.EndVertical();
                
                // Column 2
                EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandHeight(true));
                {
                    // Agent action Log area
                    _logScroll = EditorGUILayout.BeginScrollView(_logScroll,
                        EditorStyles.helpBox,
                        GUILayout.ExpandHeight(true));

                    foreach (var msg in _agentHistory)
                    {
                        GUILayout.Label(msg);
                    }
                    EditorGUILayout.EndScrollView();
                    
                    if (_scrollToBottom)
                    {
                        _logScroll.y = float.MaxValue;
                        _scrollToBottom = false;
                    }
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void AddAgentMessage(string text)
        {
            _agentHistory.Add(text);
            _scrollToBottom = true; 
            Repaint(); // redraw UI
        }

        private static void DrawConnectionArea()
        {
            GUILayout.Label("USB Agent Server connection", EditorStyles.boldLabel);
    
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            {
                // Status Indicator
                var statusStyle = new GUIStyle(EditorStyles.label)
                {
                    normal =
                    {
                        textColor = EditorBridge.IsConnected ? Color.green : EditorStyles.label.normal.textColor
                    }
                };
                GUILayout.Label(EditorBridge.IsConnected ? "● Connected" : "○ Disconnected", statusStyle);

                if (!EditorBridge.IsConnected)
                {
                    if (GUILayout.Button("Connect to Server"))
                    {
                        EditorBridge.ManualConnect();
                    }
                }
                else
                {
                    if (GUILayout.Button("Disconnect"))
                    {
                        EditorBridge.ManualDisconnect();
                    }
                }
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawConfigArea()
        {
            GUILayout.Label("Semantic Scene Config", EditorStyles.boldLabel);
            
            // START SIDE-BY-SIDE COLUMNS
            EditorGUILayout.BeginHorizontal();
            
            // COLUMN 1: Editor Config
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            _editorConfig = (SemanticSceneConfigSo)EditorGUILayout.ObjectField("Editor Config Asset", _editorConfig,
                typeof(SemanticSceneConfigSo), false);
            if (_editorConfig)
            {
                DrawConfigColumn("Editor Mode Settings", _editorConfig);
                if (GUILayout.Button("Test Editor Mode JSON export")) ExportToJson(_editorConfig);
            }
            EditorGUILayout.EndVertical();
            
            // COLUMN 2: Play Mode Config
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            _playModeConfig = (SemanticSceneConfigSo)EditorGUILayout.ObjectField("PlayMode Config Asset", _playModeConfig, 
                typeof(SemanticSceneConfigSo), false);
            if (_playModeConfig)
            {
                DrawConfigColumn("Play Mode Settings", _playModeConfig);
                if (GUILayout.Button("Test Play Mode JSON export")) ExportToJson(_playModeConfig);
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        private static void DrawConfigColumn(string label, SemanticSceneConfigSo config)
        {
            GUILayout.Label(label, EditorStyles.miniBoldLabel);
            
            var layerNames = new string[32];
            for (var i = 0; i < 32; i++) layerNames[i] = LayerMask.LayerToName(i);

            // Draw fields
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
            bool connected = EditorBridge.IsConnected;
            bool playing = EditorApplication.isPlaying;
            bool agentInScene = GameplayAgent.Instance;
    
            // Determine if we are allowed to click anything
            bool canInteract = connected && playing && agentInScene;

            EditorGUI.BeginDisabledGroup(!canInteract);
            {
                EditorGUILayout.Space(5);

                // Check the actual running state from your agent
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
            if (!connected) EditorGUILayout.HelpBox("Connect to Server first.", MessageType.None);
            if (!playing) EditorGUILayout.HelpBox("Enter Play Mode to start.", MessageType.None);
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
