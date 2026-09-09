using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    /// <summary>
    /// Frame-pumped driver for AssetCatalogGenerator. Records stream in small
    /// per-frame batches, then thumbnails are collected as AssetPreview finishes
    /// rendering them (previews need pumped frames — never Thread.Sleep the
    /// main thread waiting for them). Progress bar + cancel throughout.
    /// One run at a time; a second Start while active returns an error record.
    /// </summary>
    public static class AssetCatalogRunner
    {
        private const int RecordsPerTick = 25;
        private const int PreviewsPerTick = 20;
        private const int MaxPreviewAttempts = 300;

        private class Run
        {
            public string runId;
            public List<string> roots;
            public List<string> paths;
            public int recordIndex;
            public List<JObject> records = new List<JObject>();
            public Dictionary<string, JObject> overrides;
            public string catalogDir;
            public string thumbsDir;
            public bool wantThumbnails;
            public Action<JObject> onComplete;
            // thumb bookkeeping: id -> attempts, plus resolved record lookup
            public List<JObject> pendingThumbs = new List<JObject>();
            public Dictionary<string, int> attempts = new Dictionary<string, int>(StringComparer.Ordinal);
            public string error;
            public bool cancelled;
            public bool done;
            public JObject result;
        }

        private static Run _active;

        public static JObject Start(string[] scanRoots, bool wantThumbnails, Action<JObject> onComplete)
        {
            if (_active != null && !_active.done)
                return new JObject { ["state"] = "error", ["error"] = $"Catalog run {_active.runId} already in progress ({_active.recordIndex}/{_active.paths.Count} records)." };
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var catalogDir = Path.Combine(projectRoot, AssetCatalogGenerator.CatalogDirName);
            Directory.CreateDirectory(catalogDir);
            var thumbsDir = Path.Combine(catalogDir, AssetCatalogGenerator.ThumbsDirName);
            if (wantThumbnails)
                Directory.CreateDirectory(thumbsDir);
            var roots = new List<string>(AssetCatalogGenerator.ResolveRoots(scanRoots));
            var run = new Run
            {
                runId = Guid.NewGuid().ToString("N"),
                roots = roots,
                paths = AssetCatalogGenerator.CollectAssetPaths(roots.ToArray()),
                overrides = AssetCatalogGenerator.LoadOverrides(catalogDir),
                catalogDir = catalogDir,
                thumbsDir = thumbsDir,
                wantThumbnails = wantThumbnails,
                onComplete = onComplete,
            };
            _active = run;
            EditorApplication.update += Pump;
            return new JObject { ["runId"] = run.runId, ["state"] = "running", ["total"] = run.paths.Count };
        }

        public static JObject Poll(string runId)
        {
            var run = _active;
            if (run == null || (runId != null && !string.Equals(run.runId, runId, StringComparison.Ordinal)))
                return new JObject { ["state"] = "error", ["error"] = "Unknown catalog run id." };
            var status = new JObject
            {
                ["runId"] = run.runId,
                ["state"] = run.error != null ? "error" : run.cancelled ? "cancelled" : run.done ? "done" : "running",
                ["processed"] = run.recordIndex,
                ["total"] = run.paths.Count,
                ["pendingThumbs"] = run.pendingThumbs.Count,
            };
            if (run.error != null)
                status["error"] = run.error;
            if (run.done && run.result != null)
            {
                status["jsonPath"] = run.result["jsonPath"];
                status["itemCount"] = run.result["itemCount"];
                status["thumbnailCount"] = run.result["thumbnailCount"];
            }
            return status;
        }

        private static void Pump()
        {
            var run = _active;
            if (run == null)
            {
                EditorApplication.update -= Pump;
                return;
            }
            try
            {
                if (run.recordIndex < run.paths.Count)
                    PumpRecords(run);
                else if (run.pendingThumbs.Count > 0)
                    PumpThumbs(run);
                else
                    Finish(run);
            }
            catch (Exception e)
            {
                run.error = e.Message;
                Abort(run, $"Catalog run failed: {e.Message}");
            }
        }

        private static void PumpRecords(Run run)
        {
            var n = Math.Min(RecordsPerTick, run.paths.Count - run.recordIndex);
            for (var i = 0; i < n; i++)
            {
                var path = run.paths[run.recordIndex++];
                var root = RootFor(run.roots, path);
                var record = AssetCatalogGenerator.BuildRecord(root, path, run.overrides, out _);
                if (record == null) continue;
                if (run.wantThumbnails)
                    ReuseOrQueueThumb(run, record);
                run.records.Add(record);
            }
            if (EditorUtility.DisplayCancelableProgressBar("Asset Catalog",
                    $"Scanning records {run.recordIndex}/{run.paths.Count}", (float)run.recordIndex / run.paths.Count))
            {
                run.cancelled = true;
                Abort(run, "Catalog run cancelled during record scan.");
            }
        }

        private static void ReuseOrQueueThumb(Run run, JObject record)
        {
            var id = record["id"].ToString();
            var fileName = AssetCatalogGenerator.ThumbFileName(id);
            if (File.Exists(Path.Combine(run.thumbsDir, fileName)))
            {
                record["thumbnail"] = $"{AssetCatalogGenerator.ThumbsDirName}/{fileName}";
                return;
            }
            var migrated = AssetCatalogGenerator.MigrateLegacyThumb(run.thumbsDir, record["pack"].ToString(), fileName);
            if (migrated != null)
            {
                record["thumbnail"] = migrated;
                return;
            }
            run.pendingThumbs.Add(record);
            run.attempts[id] = 0;
        }

        private static void PumpThumbs(Run run)
        {
            var collected = 0;
            for (var i = run.pendingThumbs.Count - 1; i >= 0 && collected < PreviewsPerTick; i--)
            {
                var record = run.pendingThumbs[i];
                var id = record["id"].ToString();
                var attempts = run.attempts[id] + 1;
                run.attempts[id] = attempts;
                var target = PreviewTarget(record);
                Texture2D tex = target != null ? AssetPreview.GetAssetPreview(target) : null;
                if (tex != null)
                {
                    try
                    {
                        var png = tex.EncodeToPNG();
                        if (png != null && png.Length > 0)
                        {
                            var fileName = AssetCatalogGenerator.ThumbFileName(id);
                            File.WriteAllBytes(Path.Combine(run.thumbsDir, fileName), png);
                            record["thumbnail"] = $"{AssetCatalogGenerator.ThumbsDirName}/{fileName}";
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[Bridge] Thumbnail encode failed for '{id}': {e.Message}");
                    }
                    run.pendingThumbs.RemoveAt(i);
                    run.attempts.Remove(id);
                    collected++;
                }
                else if (attempts >= MaxPreviewAttempts)
                {
                    Debug.LogWarning($"[Bridge] No preview for '{id}' after {MaxPreviewAttempts} frames.");
                    run.pendingThumbs.RemoveAt(i);
                    run.attempts.Remove(id);
                }
            }
            var doneCount = run.records.Count - run.pendingThumbs.Count;
            if (EditorUtility.DisplayCancelableProgressBar("Asset Catalog",
                    $"Capturing thumbnails ({doneCount}/{run.records.Count} resolved, {run.pendingThumbs.Count} pending)",
                    run.records.Count > 0 ? (float)doneCount / run.records.Count : 1f))
            {
                run.cancelled = true;
                Abort(run, "Catalog run cancelled during thumbnail capture.");
            }
        }

        // assetPath is stored on the record at build time and stripped before writing.
        // Materials preview from the Material object, fbx/prefabs from the GameObject.
        private static UnityEngine.Object PreviewTarget(JObject record)
        {
            var assetPath = record["assetPath"]?.ToString();
            if (string.IsNullOrEmpty(assetPath)) return null;
            if (string.Equals(record["type"]?.ToString(), "material", StringComparison.Ordinal))
                return AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            return AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        }

        private static void Finish(Run run)
        {
            EditorApplication.update -= Pump;
            EditorUtility.ClearProgressBar();
            foreach (var record in run.records)
                record.Remove("assetPath");
            run.result = AssetCatalogGenerator.MergeAndWrite(run.catalogDir, run.records);
            run.done = true;
            try { run.onComplete?.Invoke(run.result); }
            catch (Exception e) { Debug.LogWarning($"[Bridge] Catalog onComplete failed: {e.Message}"); }
        }

        private static void Abort(Run run, string message)
        {
            EditorApplication.update -= Pump;
            EditorUtility.ClearProgressBar();
            Debug.LogWarning($"[Bridge] {message}");
            run.done = true;
            try { run.onComplete?.Invoke(null); }
            catch (Exception e) { Debug.LogWarning($"[Bridge] Catalog onComplete failed: {e.Message}"); }
        }

        private static string RootFor(List<string> roots, string path)
        {
            foreach (var root in roots)
            {
                if (path.StartsWith(root + "/", StringComparison.Ordinal))
                    return root;
            }
            return "Assets";
        }
    }
}
