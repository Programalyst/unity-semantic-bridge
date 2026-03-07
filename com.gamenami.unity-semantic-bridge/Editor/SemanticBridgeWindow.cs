using System.Collections.Generic;
using System.Text;
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
        
        private Vector2 _chatScroll;
        private string _chatInput = "";
        private readonly List<ChatMessage> _chatHistory = new List<ChatMessage>();
        private bool _scrollToBottom;
        
        private GUIStyle _userStyle;
        private GUIStyle _agentStyle;

        [System.Serializable]
        public class ChatMessage 
        {
            public string text;
            public bool isUser;
        }
        
        [MenuItem("Tools/Unity Semantic Bridge")]
        public static void ShowWindow() 
        {
            var window = GetWindow<SemanticBridgeWindow>("Unity Semantic Bridge");
            window.minSize = new Vector2(600, 500); 
        }
        
        private void OnEnable()
        {
            Instance = this; // Register this window instance

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
            InitStyles();
            
            EditorGUILayout.BeginVertical(GUILayout.ExpandHeight(false));
            {
                EditorGUILayout.Space(10);
                DrawConnectionArea();
                EditorGUILayout.Space(10);
                DrawConfigArea();
                EditorGUILayout.Space(10);
            }
            EditorGUILayout.EndVertical();
            
            DrawChatBox();
        }
        
        private void InitStyles()
        {
            _userStyle = new GUIStyle(EditorStyles.label) {
                wordWrap = true,
                richText = true,
                padding = new RectOffset(10, 10, 1, 1),
                normal = { textColor = new Color(0.4f, 1f, 1f) }
            };

            _agentStyle = new GUIStyle(EditorStyles.label) {
                wordWrap = true,
                richText = true,
                padding = new RectOffset(10, 10, 1, 1),
                normal = { textColor = Color.white }
            };
        }
        
        private void DrawChatBox()
        {
            GUILayout.Label("Agent Chat", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandHeight(true));
            {
                _chatScroll = EditorGUILayout.BeginScrollView(_chatScroll,
                    EditorStyles.helpBox,
                    GUILayout.ExpandHeight(true));
            
                foreach (var msg in _chatHistory)
                {
                    DrawMessage(msg);
                }
                EditorGUILayout.EndScrollView();
            }
            EditorGUILayout.EndVertical();

            // 2. INPUT AREA
            EditorGUILayout.BeginHorizontal();
            {
                // Handle Enter key for sending
                bool pressEnter = (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return);
            
                _chatInput = EditorGUILayout.TextField(_chatInput, GUILayout.Height(25));
                if (GUILayout.Button("Send", GUILayout.Width(60), GUILayout.Height(25)) || pressEnter)
                {
                    if (!string.IsNullOrEmpty(_chatInput))
                    {
                        SendChatMessage(_chatInput);
                        _chatInput = "";
                        GUI.FocusControl(null); // Deselect text field
                    }
                }
            }
            EditorGUILayout.EndHorizontal();
            
            if (_scrollToBottom)
            {
                _chatScroll.y = float.MaxValue;
                _scrollToBottom = false;
            }
        }

        private void DrawMessage(ChatMessage msg)
        {
            var style = msg.isUser ? _userStyle : _agentStyle;
            var prefix = msg.isUser ? "<b>You:</b> " : "<b>Agent:</b> ";
            GUILayout.Label(prefix + msg.text, style);
        }
        
        public void AddAgentMessage(string text)
        {
            _chatHistory.Add(new ChatMessage { 
                text = text, 
                isUser = false, 
            });
            
            _scrollToBottom = true; 
            Repaint(); // redraw UI
        }

        private void SendChatMessage(string text)
        {
            // Trigger the Bridge to send to Python
            if (EditorBridge.IsConnected)
            {
                // Use the Editor config for Dev Chat context
                
                SemanticScene sceneData = SemanticSceneGenerator.Generate(_editorConfig);
                var sceneJson = JsonConvert.SerializeObject(sceneData, new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                });
                var kbSize = Encoding.UTF8.GetByteCount(sceneJson) / 1024.0;
                
                var payload = new {
                    type = "chat",
                    message = text,
                    scene = JsonConvert.DeserializeObject(sceneJson)
                };
                
                EditorBridge.SendToAgent(JsonConvert.SerializeObject(payload));
                
                _chatHistory.Add(new ChatMessage { 
                    text = $"{text} (+ appended scene context {kbSize:N1} kb)", 
                    isUser = true
                });
            }
            else
            {
                Debug.LogError("No USB Agent server connection. Please connect first.");
            }
            
            // Auto-scroll to bottom
            _chatScroll.y = float.MaxValue;
        }

        private static void DrawConnectionArea()
        {
            GUILayout.Label("USB Agent Server connection", EditorStyles.boldLabel);
    
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
    
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
                    _ = EditorBridge.Connect(); // Fire and forget async
                }
            }
            else
            {
                if (GUILayout.Button("Disconnect"))
                {
                    EditorBridge.Disconnect();
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
                // Note: AssetDatabase.SaveAssets() is heavy, usually SetDirty is enough until Unity auto-saves
            }
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

        public SemanticScene GetEditorHierarchy()
        {
            return SemanticSceneGenerator.Generate(_editorConfig);
        }
    }
}
