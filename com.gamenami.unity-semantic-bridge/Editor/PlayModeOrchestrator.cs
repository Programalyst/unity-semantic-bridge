using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    /// <summary>
    /// Generic Play Mode orchestration. No game-specific logic, no action queues.
    /// Every call resolves to a terminal result or a timeout error carrying the
    /// current state — never a bare "entering…" with unknown outcome.
    /// Domain-reload survival uses SessionState pending-op tokens, the same
    /// pattern as <see cref="CompileWatcher"/>: the in-flight HTTP response may
    /// be lost across reload, but the op resumes and Python polls
    /// <c>poll_play_state</c> until it settles.
    /// Polling uses EditorApplication.update, which also ticks in the background, and never blocks the main thread.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayModeOrchestrator
    {
        public const string KindKey = "MCP_PlayOpKind";
        public const string TokenKey = "MCP_PlayOpToken";
        public const string DeadlineKey = "MCP_PlayOpDeadlineUtcTicks";

        const double DefaultTimeoutS = 30.0;
        const double MinTimeoutS = 1.0;
        // EditorBridge caps a single request at 60s; finish first so the
        // timeout report (with state) is ours, not the transport's.
        const double MaxTimeoutS = 55.0;

        static TaskCompletionSource<string> _completion;
        static bool _pumping;

        static PlayModeOrchestrator()
        {
            // Resume a pending op that survived a domain reload (statics are
            // reset, SessionState is not). Stale deadlines are cleared.
            if (Kind() is "enter" or "exit")
            {
                if (DateTime.UtcNow.Ticks > DeadlineTicks())
                    ClearPending();
                else
                    EnsurePumping();
            }
        }

        // ------------------------------------------------------------------
        // Snapshot (no side effects)
        // ------------------------------------------------------------------

        public static string PollPlayState(JObject _)
        {
            return Snapshot().ToString(Newtonsoft.Json.Formatting.None);
        }

        static JObject Snapshot()
        {
            var transitioning = EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying;
            var pending = PendingJson();
            // interactionMode lets agents notice a throttled (unfocused-backgrounded)
            // Editor before starting a long transition: default throttling can stall
            // main-thread work, so prefer set_editor_throttling("no_throttling").
            var snapshot = new JObject
            {
                ["isPlaying"] = EditorApplication.isPlaying,
                ["transitioning"] = transitioning,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["isUpdating"] = EditorApplication.isUpdating,
                ["pendingOp"] = pending,
                ["interactionMode"] = EditorPreferencesFunctions.GetPreferences()["interactionMode"]?.Value<string>() ?? "unknown",
            };
            return snapshot;
        }

        static JToken PendingJson()
        {
            var kind = Kind();
            if (kind is not ("enter" or "exit")) return JValue.CreateNull();
            return new JObject
            {
                ["kind"] = kind,
                ["token"] = SessionState.GetString(TokenKey, ""),
                ["expiresUtc"] = new DateTime(DeadlineTicks(), DateTimeKind.Utc).ToString("o"),
            };
        }

        // ------------------------------------------------------------------
        // open_scene(path, dirty_policy)
        // ------------------------------------------------------------------

        public static string OpenScene(JObject mcpMessage)
        {
            var path = mcpMessage["path"]?.ToString()
                ?? mcpMessage["scenePath"]?.ToString();
            if (string.IsNullOrWhiteSpace(path))
                return "Error: 'path' is required (project-relative, e.g. 'Assets/Scenes/GridBattle.unity').";

            path = path.Replace("\\", "/");
            if (path.Contains(".."))
                return $"Error: path '{path}' contains traversal segments.";
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) &&
                !path.StartsWith("Packages/", StringComparison.Ordinal))
                return $"Error: path '{path}' must start with \"Assets/\" or \"Packages/\".";
            if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                return $"Error: path '{path}' must end with \".unity\".";
            var policy = (mcpMessage["dirty_policy"]?.ToString()
                ?? mcpMessage["dirtyPolicy"]?.ToString()
                ?? "fail").Trim().ToLowerInvariant();
            if (policy is not ("fail" or "save"))
                return $"Error: unknown dirty_policy '{policy}'. Expected 'fail' (default) or 'save'.";

            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                return "Error: cannot open a scene while in Play Mode or transitioning. Exit Play Mode first.";

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                return $"Error: scene asset not found at '{path}'.";

            var active = EditorSceneManager.GetActiveScene();
            var wasDirty = active.isDirty;
            if (wasDirty && policy == "fail")
                return $"Error: Active scene '{DisplayScene(active)}' has unsaved changes (dirty). " +
                       "Nothing was opened or discarded. Re-call open_scene with dirty_policy='save' to save first.";

            try
            {
                if (wasDirty)
                    EditorSceneManager.SaveOpenScenes();
                var opened = EditorSceneManager.OpenScene(path);
                var now = EditorSceneManager.GetActiveScene();
                return new JObject
                {
                    ["status"] = "ok",
                    ["path"] = opened.path,
                    ["name"] = opened.name,
                    ["wasDirty"] = wasDirty,
                    ["isDirty"] = now.isDirty,
                    ["dirtyPolicy"] = policy,
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception e)
            {
                return $"Error: failed to open scene '{path}': {e.Message}";
            }
        }

        static string DisplayScene(UnityEngine.SceneManagement.Scene scene)
            => string.IsNullOrEmpty(scene.path) ? scene.name : scene.path;

        // ------------------------------------------------------------------
        // enter_play / exit_play (deferred completion — never block main thread)
        // ------------------------------------------------------------------

        public static void EnterPlay(JObject mcpMessage, TaskCompletionSource<string> completion)
        {
            BeginOp(mcpMessage, completion, "enter", wantPlaying: true);
        }

        public static void ExitPlay(JObject mcpMessage, TaskCompletionSource<string> completion)
        {
            BeginOp(mcpMessage, completion, "exit", wantPlaying: false);
        }

        static void BeginOp(JObject mcpMessage, TaskCompletionSource<string> completion, string kind, bool wantPlaying)
        {
            if (completion == null) throw new ArgumentNullException(nameof(completion));
            var existing = Kind();
            if (existing is "enter" or "exit" && DateTime.UtcNow.Ticks <= DeadlineTicks())
            {
                // Synchronous BUSY (same prefix convention as AssetFunctions):
                // completion stays owned here, the handler just returns.
                completion.TrySetResult(
                    $"BUSY: play-mode op '{existing}' (token={SessionState.GetString(TokenKey, "")}) already in progress. " +
                    "Poll poll_play_state until pendingOp clears, then retry.");
                return;
            }
            if (existing is "enter" or "exit")
                ClearPending(); // stale deadline — never overlap

            var timeoutS = ReadTimeoutS(mcpMessage);
            // Already in the desired idle state: succeed immediately.
            var transitioning = EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying;
            var settled = wantPlaying
                ? EditorApplication.isPlaying && !transitioning && !CompileWatcher.IsBusy
                : !EditorApplication.isPlaying && !transitioning && !CompileWatcher.IsBusy;
            if (settled)
            {
                var snapshot = Snapshot();
                snapshot["status"] = "ok";
                snapshot["kind"] = kind;
                snapshot["detail"] = wantPlaying ? "Already in Play Mode." : "Already in Edit Mode.";
                completion.TrySetResult(snapshot.ToString(Newtonsoft.Json.Formatting.None));
                return;
            }

            var token = Guid.NewGuid().ToString("N");
            var deadline = DateTime.UtcNow.AddSeconds(timeoutS);
            SessionState.SetString(KindKey, kind);
            SessionState.SetString(TokenKey, token);
            SessionState.SetString(DeadlineKey, deadline.Ticks.ToString());
            _completion = completion;

            // Request synchronously: this handler already runs on the main thread
            // (message-queue drain inside EditorApplication.update). A deferred
            // delayCall request to ENTER Play Mode was observed to never engage,
            // while the same request to EXIT did; direct assignment removes that
            // ordering hazard. Transport already acknowledged nothing yet, and the
            // deferred completion (update pump + SessionState token) is unchanged.
            try
            {
                EditorApplication.isPlaying = wantPlaying;
            }
            catch (Exception e)
            {
                FailPending($"Error: failed to request {(wantPlaying ? "Play Mode" : "Edit Mode")}: {e.Message}");
                return;
            }
            EnsurePumping();
        }

        static void EnsurePumping()
        {
            if (_pumping) return;
            _pumping = true;
            EditorApplication.update += Pump;
        }

        static void Pump()
        {
            try
            {
                var kind = Kind();
                if (kind is not ("enter" or "exit"))
                {
                    StopPumping();
                    return;
                }
                var token = SessionState.GetString(TokenKey, "");
                var wantPlaying = kind == "enter";
                var transitioning = EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying;
                var done = wantPlaying
                    ? EditorApplication.isPlaying && !transitioning && !CompileWatcher.IsBusy
                    : !EditorApplication.isPlaying && !transitioning && !CompileWatcher.IsBusy;
                if (done)
                {
                    var snapshot = Snapshot();
                    snapshot["status"] = "ok";
                    snapshot["kind"] = kind;
                    snapshot["token"] = token;
                    snapshot["detail"] = wantPlaying ? "Entered Play Mode." : "Returned to Edit Mode.";
                    ClearPending();
                    StopPumping();
                    TakeCompletion()?.TrySetResult(snapshot.ToString(Newtonsoft.Json.Formatting.None));
                    return;
                }
                if (DateTime.UtcNow.Ticks > DeadlineTicks())
                {
                    var snapshot = Snapshot();
                    snapshot["status"] = "timeout";
                    snapshot["kind"] = kind;
                    snapshot["token"] = token;
                    snapshot["message"] = wantPlaying
                        ? "Timed out waiting for Play Mode. State above; poll poll_play_state before retrying. If the Editor window is unfocused, set_editor_throttling no_throttling and retry."
                        : "Timed out waiting for Edit Mode. State above; poll poll_play_state before retrying. If the Editor window is unfocused, set_editor_throttling no_throttling and retry.";
                    ClearPending();
                    StopPumping();
                    TakeCompletion()?.TrySetResult(snapshot.ToString(Newtonsoft.Json.Formatting.None));
                }
            }
            catch (Exception e)
            {
                FailPending($"Error: play-mode pump failed: {e.Message}");
            }
        }

        static void FailPending(string message)
        {
            ClearPending();
            StopPumping();
            TakeCompletion()?.TrySetResult(message);
        }

        static TaskCompletionSource<string> TakeCompletion()
        {
            var c = _completion;
            _completion = null;
            return c;
        }

        static void StopPumping()
        {
            if (!_pumping) return;
            _pumping = false;
            EditorApplication.update -= Pump;
        }

        static void ClearPending()
        {
            SessionState.SetString(KindKey, "none");
            SessionState.SetString(TokenKey, "");
            SessionState.SetString(DeadlineKey, "");
        }

        static string Kind() => SessionState.GetString(KindKey, "none");

        static long DeadlineTicks()
        {
            var raw = SessionState.GetString(DeadlineKey, "");
            return long.TryParse(raw, out var ticks) ? ticks : 0;
        }

        public static double ReadTimeoutS(JObject mcpMessage)
        {
            var raw = mcpMessage["timeout_s"]?.ToString()
                ?? mcpMessage["timeoutS"]?.ToString()
                ?? mcpMessage["timeout"]?.ToString();
            if (!double.TryParse(raw, out var seconds) || double.IsNaN(seconds) || double.IsInfinity(seconds))
                seconds = DefaultTimeoutS;
            return Math.Min(MaxTimeoutS, Math.Max(MinTimeoutS, seconds));
        }
    }
}
