using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;

namespace Gamenami.UnitySemanticBridge.Editor
{
// ---- Persistent compile-result watcher. Registered once, survives domain reload. ----
    [InitializeOnLoad]
    public static class CompileWatcher
    {
        const string StatusKey = "MCP_CompileStatus"; // "compiling" | "success" | "failed"
        const string ErrorsKey = "MCP_CompileErrors";
        const string TokenKey = "MCP_CompileToken"; // correlates a write with its result

        static CompileWatcher()
        {
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            CompilationPipeline.compilationStarted += _ => SessionState.SetString(StatusKey, "compiling");
            CompilationPipeline.compilationFinished += _ => FinalizeStatus();
        }

        static readonly List<string> PendingErrors = new List<string>();

        static void OnAssemblyCompiled(string assembly, CompilerMessage[] messages)
        {
            foreach (var m in messages)
                if (m.type == CompilerMessageType.Error)
                    PendingErrors.Add($"{m.file}:{m.line} {m.message}");
        }

        static void FinalizeStatus()
        {
            var errors = string.Join("\n", PendingErrors);
            SessionState.SetString(ErrorsKey, errors);
            SessionState.SetString(StatusKey, PendingErrors.Count == 0 ? "success" : "failed");
            PendingErrors.Clear();
        }

        public static string BeginWrite()
        {
            var token = Guid.NewGuid().ToString();
            SessionState.SetString(TokenKey, token);
            SessionState.SetString(StatusKey, "pending"); // set before Refresh() triggers "compiling"
            SessionState.EraseString(ErrorsKey);
            return token;
        }

        public static (string status, string errors) Poll()
        {
            return (SessionState.GetString(StatusKey, "unknown"),
                SessionState.GetString(ErrorsKey, ""));
        }
    }
}