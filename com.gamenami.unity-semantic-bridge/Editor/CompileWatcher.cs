using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;

namespace Gamenami.UnitySemanticBridge.Editor
{
    // SessionState preserves the latest result across domain reload, not Editor restart.
    [InitializeOnLoad]
    public static class CompileWatcher
    {
        const string StatusKey = "MCP_CompileStatus";
        const string ErrorsKey = "MCP_CompileErrors";
        const string TokenKey = "MCP_CompileToken";
        static readonly List<string> PendingErrors = new List<string>();

        static CompileWatcher()
        {
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += _ => FinalizeStatus();
            // A reload can interrupt the idle callback (e.g. an imported DLL).
            if (SessionState.GetString(StatusKey, "unknown") == "pending")
                CompleteRefresh();
        }

        public static bool IsBusy => EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying ||
            SessionState.GetString(StatusKey, "unknown") is "pending" or "compiling";

        static void OnCompilationStarted(object context)
        {
            PendingErrors.Clear();
            SessionState.SetString(StatusKey, "compiling");
            // Keep previous diagnostics until a new compilation supplies its result.
        }

        static void OnAssemblyCompiled(string assembly, CompilerMessage[] messages)
        {
            foreach (var m in messages)
                if (m.type == CompilerMessageType.Error)
                    PendingErrors.Add($"{m.file}:{m.line} {m.message}");
        }

        static void FinalizeStatus()
        {
            SessionState.SetString(ErrorsKey, string.Join("\n", PendingErrors));
            SessionState.SetString(StatusKey, PendingErrors.Count == 0 ? "success" : "failed");
            PendingErrors.Clear();
        }

        // Shared by writes and standalone refreshes. Never reuse a previous SUCCESS.
        public static string BeginWrite()
        {
            var token = Guid.NewGuid().ToString();
            SessionState.SetString(TokenKey, token);
            SessionState.SetString(StatusKey, "pending");
            return token;
        }

        public static void FailRefresh(Exception exception)
        {
            var previous = SessionState.GetString(ErrorsKey, "");
            SessionState.SetString(ErrorsKey, $"Asset refresh failed: {exception.Message}" +
                (string.IsNullOrEmpty(previous) ? "" : "\n" + previous));
            SessionState.SetString(StatusKey, "failed");
        }

        public static void CompleteRefresh()
        {
            // Let Unity process compilation scheduled by Refresh before settling a no-op.
            // No waiting on the Editor thread, no timer that guesses compilation duration.
            var token = SessionState.GetString(TokenKey, "");
            EditorApplication.delayCall += () => SettleRefresh(token);
        }

        static void SettleRefresh(string token)
        {
            if (token != SessionState.GetString(TokenKey, "") ||
                SessionState.GetString(StatusKey, "unknown") != "pending") return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += () => SettleRefresh(token);
                return;
            }
            // No new compiler result: don't certify scripts using an older SUCCESS,
            // and don't erase a previous failure just because Refresh did no work.
            SessionState.SetString(StatusKey, string.IsNullOrEmpty(SessionState.GetString(ErrorsKey, ""))
                ? "no_compilation" : "failed");
        }

        public static (string status, string errors) Poll()
        {
            var status = SessionState.GetString(StatusKey, "unknown");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) status = "compiling";
            return (status, SessionState.GetString(ErrorsKey, ""));
        }
    }
}
