using System.IO;
using System.Linq;
using System.Text;
using Gamenami.UnitySemanticBridge.Layout;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace Gamenami.UnitySemanticBridge.Editor.Layout
{
    /// <summary>
    /// MCP entry points for layouts. Layout files and scenes are named by project path, pieces by prefab GUID
    /// (a prefab path is accepted where a GUID goes and stored as the GUID). Scenes must already be open;
    /// nothing here opens, saves, closes or discards a scene.
    /// </summary>
    public static class LayoutTools
    {
        /// <summary>
        /// bake_layout: reads a scene (the active one, or the open scene at "scene") back into layout JSON. With
        /// "path" (project-relative, under Assets/, ending .json) it also writes the layout there.
        /// </summary>
        public static string BakeLayout(JObject mcpMessage)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new BridgeToolException("Exit Play Mode before baking a layout; the bake reads the authored scene.");
            var result = LayoutBaker.Bake(ResolveScene(mcpMessage));
            var path = mcpMessage?["path"]?.Value<string>();
            if (string.IsNullOrEmpty(path)) return result.ToString();

            path = CheckLayoutPath(path);
            File.WriteAllText(path, LayoutJson.Serialize(result.Document));
            AssetDatabase.ImportAsset(path);
            return $"Wrote {path}\n{result}";
        }

        /// <summary>get_layout: a layout file's summary and canonical JSON, or its schema error.</summary>
        public static string GetLayout(JObject mcpMessage)
        {
            var path = CheckLayoutPath(mcpMessage?["path"]?.Value<string>());
            if (!File.Exists(path)) throw new BridgeToolException($"No layout file at {path}.");
            var document = Parse(File.ReadAllText(path), path);
            return $"{Summary(path, document)}\n{LayoutJson.Serialize(document).TrimEnd('\n')}";
        }

        /// <summary>
        /// set_layout: writes "layout" (JSON text or object) to "path" in canonical form, after a schema check that
        /// writes nothing on failure; prefab paths become GUIDs. With "validate" (default true) the grid tier follows.
        /// </summary>
        public static string SetLayout(JObject mcpMessage)
        {
            var path = CheckLayoutPath(mcpMessage?["path"]?.Value<string>());
            var layout = mcpMessage?["layout"];
            if (layout == null || layout.Type == JTokenType.Null)
                throw new BridgeToolException("set_layout needs 'layout' (the layout JSON).");
            var document = Parse(LayoutText(layout), "layout");
            var existed = File.Exists(path);
            File.WriteAllText(path, LayoutJson.Serialize(document));
            AssetDatabase.ImportAsset(path);

            var text = new StringBuilder($"{(existed ? "Replaced" : "Wrote")} {Summary(path, document)}");
            if (mcpMessage["validate"]?.Value<bool?>() ?? true)
            {
                text.Append('\n');
                try
                {
                    text.Append(GridReport(document, mcpMessage));
                }
                catch (BridgeToolException e)
                {
                    text.Append("Grid tier not run: ").Append(e.Message);
                }
            }
            return text.ToString();
        }

        /// <summary>
        /// validate_layout: tier "grid" checks a draft ("path" or inline "layout") without touching any open scene;
        /// tier "scene" checks an open scene (hand-made or applied) against the game's own occupancy bake.
        /// </summary>
        public static string ValidateLayout(JObject mcpMessage)
        {
            var tier = mcpMessage?["tier"]?.Value<string>() ?? "grid";
            if (tier == "grid") return GridReport(ReadDocument(mcpMessage), mcpMessage);
            if (tier != "scene") throw new BridgeToolException($"tier must be 'grid' or 'scene' (was '{tier}').");
            if (HasValue(mcpMessage, "path") || HasValue(mcpMessage, "layout"))
                throw new BridgeToolException("tier='scene' validates the scene itself; omit path and layout (use apply_layout to build a draft first).");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new BridgeToolException("Exit Play Mode before validating a scene; the check reads the authored scene.");
            var scene = ResolveScene(mcpMessage);
            var report = LayoutSceneValidator.ValidateScene(scene);
            return $"Scene tier for {SceneName(scene)} (the game's occupancy bake of the scene):\n{FormatWithMetrics(report, mcpMessage)}";
        }

        /// <summary>
        /// apply_layout: builds a draft ("path" or inline "layout") into an open scene under [Generated Layout] and
        /// writes its deploy cells and enemy spawns into the level, as one Undo group, leaving the scene unsaved.
        /// With "validate" (default true) the full report follows, physics tier included.
        /// </summary>
        public static string ApplyLayout(JObject mcpMessage)
        {
            var document = ReadDocument(mcpMessage);
            var scene = ResolveScene(mcpMessage);
            var applied = LayoutApplier.Apply(document, scene);
            var source = HasValue(mcpMessage, "path") ? mcpMessage["path"].Value<string>() : "the inline layout";
            var header = $"Applied {source} to {SceneName(scene)} (not saved; Undo reverts it).";
            if (!(mcpMessage["validate"]?.Value<bool?>() ?? true)) return $"{header}\n{applied}";

            var maxLines = MaxLines(mcpMessage);
            var text = new StringBuilder(header).Append('\n');
            text.Append($"Placed {applied.PieceCount} pieces and {applied.DecorCount} decor ({applied.CardCount} from catalog cards); {applied.Expected?.BlockedCount ?? 0} blocked cells.\n");
            text.Append($"Notes ({applied.Notes.Count}):\n");
            foreach (var note in applied.Notes.Take(maxLines > 0 ? maxLines : int.MaxValue)) text.Append(note).Append('\n');
            if (maxLines > 0 && applied.Notes.Count > maxLines) text.Append($"... {applied.Notes.Count - maxLines} more notes not shown\n");
            var report = LayoutSceneValidator.ValidateApplied(document, applied, scene);
            text.Append(FormatReport(report, mcpMessage));
            return text.ToString();
        }

        private static string GridReport(LayoutDocument document, JObject mcpMessage)
        {
            var report = LayoutPieceResolver.ValidateDraft(document);
            return "Grid tier (the draft alone; collider fit and the game's occupancy need apply_layout or tier='scene'):\n"
                   + FormatWithMetrics(report, mcpMessage);
        }

        // The report, and with "metrics" the level metrics on the same grid (plan Phase 6); "metrics_only" drops the
        // report's issue lines but keeps its header, so errors still show.
        public static string FormatWithMetrics(LayoutReport report, JObject mcpMessage)
        {
            var metricsOnly = mcpMessage?["metrics_only"]?.Value<bool?>() ?? false;
            var metrics = metricsOnly || (mcpMessage?["metrics"]?.Value<bool?>() ?? false);
            var text = metricsOnly ? report.Format(1, false).Split('\n')[0] : FormatReport(report, mcpMessage);
            if (!metrics) return text;
            var measured = LayoutMetrics.Measure(report.Grid, report.Deploy, report.Enemies, null, Region(mcpMessage));
            return text + "\n" + measured.Format(MaxLines(mcpMessage));
        }

        private static (Cell, Cell)? Region(JObject mcpMessage)
        {
            var region = mcpMessage?["region"];
            if (region == null || region.Type == JTokenType.Null) return null;
            if (!(region is JArray a) || a.Count != 4)
                throw new BridgeToolException("region must be [x0, y0, x1, y1] (cells, inclusive).");
            return (new Cell(a[0].Value<int>(), a[1].Value<int>()), new Cell(a[2].Value<int>(), a[3].Value<int>()));
        }

        private static string FormatReport(LayoutReport report, JObject mcpMessage) =>
            report.Format(MaxLines(mcpMessage), mcpMessage?["ascii"]?.Value<bool?>() ?? false);

        private static int MaxLines(JObject mcpMessage)
        {
            var maxLines = mcpMessage?["max_lines"]?.Value<int?>() ?? LayoutReport.DefaultMaxLines;
            if (maxLines < 0) throw new BridgeToolException("max_lines must be 0 (all) or more.");
            return maxLines;
        }

        /// <summary>The draft from exactly one of "path" (a layout file) and "layout" (inline JSON).</summary>
        public static LayoutDocument ReadDocument(JObject mcpMessage)
        {
            var hasPath = HasValue(mcpMessage, "path");
            var hasLayout = HasValue(mcpMessage, "layout");
            if (hasPath == hasLayout) throw new BridgeToolException("Give exactly one of 'path' (a layout file) and 'layout' (inline layout JSON).");
            if (hasLayout) return Parse(LayoutText(mcpMessage["layout"]), "layout");
            var path = CheckLayoutPath(mcpMessage["path"].Value<string>());
            if (!File.Exists(path)) throw new BridgeToolException($"No layout file at {path}.");
            return Parse(File.ReadAllText(path), path);
        }

        /// <summary>Parses layout JSON, accepting asset paths where GUIDs go; schema errors name <paramref name="source"/>.</summary>
        public static LayoutDocument Parse(string json, string source)
        {
            try
            {
                return LayoutJson.Parse(json, AssetPathToGuid);
            }
            catch (LayoutFormatException e)
            {
                throw new BridgeToolException($"{source}: {e.Message}");
            }
        }

        /// <summary>An existing asset's GUID from its project path (Assets/ or Packages/), else null.</summary>
        public static string AssetPathToGuid(string path)
        {
            if (string.IsNullOrEmpty(path) || !(path.StartsWith("Assets/") || path.StartsWith("Packages/"))) return null;
            if (AssetDatabase.LoadMainAssetAtPath(path) == null) return null;
            var guid = AssetDatabase.AssetPathToGUID(path);
            return string.IsNullOrEmpty(guid) ? null : guid;
        }

        /// <summary>The open scene at "scene" (a project path), else the active scene.</summary>
        public static Scene ResolveScene(JObject mcpMessage)
        {
            var path = mcpMessage?["scene"]?.Value<string>();
            if (string.IsNullOrEmpty(path)) return SceneManager.GetActiveScene();
            var scene = SceneManager.GetSceneByPath(path.Replace('\\', '/'));
            if (!scene.IsValid() || !scene.isLoaded)
                throw new BridgeToolException($"Scene '{path}' is not open. Open it first (open_scene); layout tools never open or close scenes.");
            return scene;
        }

        private static string SceneName(Scene scene) => string.IsNullOrEmpty(scene.path) ? $"scene '{scene.name}' (unsaved)" : scene.path;

        private static string LayoutText(JToken layout) =>
            layout.Type == JTokenType.String ? layout.Value<string>() : layout.ToString(Newtonsoft.Json.Formatting.None);

        private static bool HasValue(JObject message, string key) =>
            message?[key] != null && message[key].Type != JTokenType.Null && !(message[key].Type == JTokenType.String && message[key].Value<string>() == "");

        private static string Summary(string path, LayoutDocument document) =>
            $"{path}: {document.Width}x{document.Height}, {document.Pieces.Count} pieces, {document.Decor.Count} decor, " +
            $"{document.Deploy.Count} deploy cells, {document.Enemies.Count} enemies.";

        private static string CheckLayoutPath(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new BridgeToolException("path is required (project-relative, under Assets/, ending .json).");
            path = path.Replace('\\', '/');
            if (!path.StartsWith("Assets/") || path.Contains("..") || !path.EndsWith(".json"))
                throw new BridgeToolException($"path must be project-relative under Assets/ and end in .json (was '{path}').");
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                throw new BridgeToolException($"Folder '{directory}' does not exist.");
            return path;
        }
    }
}
