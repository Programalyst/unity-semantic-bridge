using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    // Lifecycle tests simulate compiler callbacks without creating C# files.
    // Explicit integration tests below additionally exercise real asset refresh.
    public class RefreshAssetsTests
    {
        static readonly string[] Keys = { "MCP_CompileStatus", "MCP_CompileErrors", "MCP_CompileToken" };
        string scratchPath;
        readonly Dictionary<string, string> saved = new Dictionary<string, string>();

        static object Invoke(string name, params object[] args) => typeof(CompileWatcher)
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);

        [SetUp]
        public void SetUp()
        {
            saved.Clear();
            if (CompileWatcher.IsBusy) Assert.Ignore("Run when the Editor is idle.");
            foreach (var key in Keys)
            {
                saved[key] = SessionState.GetString(key, "");
                SessionState.EraseString(key);
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (scratchPath != null)
            {
                AssetDatabase.DeleteAsset(scratchPath);
                scratchPath = null;
            }
            foreach (var entry in saved) SessionState.SetString(entry.Key, entry.Value);
        }

        // Opt-in integration checks. Refresh can import other pending disk edits,
        // so run only in a disposable project, never the shared gameplay Editor.
        [UnityTest, Explicit("Requires an idle disposable Unity project.")]
        public IEnumerator RealNoOpRefreshSettles()
        {
            yield return RefreshAndWait();
            Assert.AreEqual("no_compilation", CompileWatcher.Poll().status);
        }

        [UnityTest, Explicit("Imports a temporary text asset in a disposable Unity project.")]
        public IEnumerator RealNonCodeRefreshImportsTextAndSettles()
        {
            scratchPath = "Assets/__BridgeRefresh_" + Guid.NewGuid().ToString("N") + ".txt";
            File.WriteAllText(scratchPath, "refresh probe");
            yield return RefreshAndWait();
            Assert.AreEqual("no_compilation", CompileWatcher.Poll().status);
            Assert.AreEqual("refresh probe", AssetDatabase.LoadAssetAtPath<TextAsset>(scratchPath).text);
        }

        static IEnumerator RefreshAndWait()
        {
            var completion = new TaskCompletionSource<string>();
            McpMessageHandler.HandleMcpMessage(new JObject { ["method"] = "refresh_assets" }, completion);
            StringAssert.StartsWith("Refresh queued", completion.Task.Result);
            StringAssert.Contains("RELOAD_IMMINENT", completion.Task.Result);
            for (var tick = 0; tick < 1200; tick++)
            {
                yield return null;
                var status = CompileWatcher.Poll().status;
                if (status != "pending" && status != "compiling") yield break;
            }
            Assert.Fail("Refresh did not settle in 1200 Editor test ticks.");
        }

        [TestCase("no-op")]
        [TestCase("non-code import")]
        public void RefreshWithoutCompilerCallbacksSettlesWithoutCertifyingOldSuccess(string scenario)
        {
            SessionState.SetString(Keys[0], "success");
            var token = CompileWatcher.BeginWrite();
            Assert.AreEqual("pending", CompileWatcher.Poll().status, scenario);
            Invoke("SettleRefresh", token);
            Assert.AreEqual("no_compilation", CompileWatcher.Poll().status);
            StringAssert.StartsWith("NO_COMPILATION:", AssetFunctions.GetCompilationStatus(new JObject()));
        }

        [Test]
        public void CompilationFailureSurvivesNoOpAndRecoversOnSuccessfulCompilation()
        {
            CompileWatcher.BeginWrite();
            Invoke("OnCompilationStarted", new object());
            Invoke("OnAssemblyCompiled", "Probe.dll", new[] {
                new CompilerMessage { type = CompilerMessageType.Error, file = "Probe.cs", line = 7, message = "test error" }
            });
            Invoke("FinalizeStatus");
            StringAssert.Contains("Probe.cs:7 test error", CompileWatcher.Poll().errors);
            var token = CompileWatcher.BeginWrite();
            Invoke("SettleRefresh", token);
            Assert.AreEqual("failed", CompileWatcher.Poll().status);
            StringAssert.Contains("test error", CompileWatcher.Poll().errors);
            CompileWatcher.BeginWrite();
            Invoke("OnCompilationStarted", new object());
            Assert.AreEqual("compiling", CompileWatcher.Poll().status);
            Invoke("FinalizeStatus");
            Assert.AreEqual("success", CompileWatcher.Poll().status);
            Assert.AreEqual("", CompileWatcher.Poll().errors);
        }

        [Test]
        public void BusyDispatcherRejectsRefreshAndWriteWithoutOverwritingToken()
        {
            var token = CompileWatcher.BeginWrite();
            var completion = new TaskCompletionSource<string>();
            McpMessageHandler.HandleMcpMessage(new JObject { ["method"] = "refresh_assets" }, completion);
            StringAssert.StartsWith("BUSY:", completion.Task.Result);
            StringAssert.StartsWith("BUSY:", AssetFunctions.WriteScript(new JObject { ["path"] = "Assets/Unused.cs" }));
            Assert.AreEqual(token, SessionState.GetString(Keys[2], ""));
        }

        [Test]
        public void StaleIdleCallbackCannotCompleteAnotherOperationOrCompilation()
        {
            var oldToken = CompileWatcher.BeginWrite();
            CompileWatcher.BeginWrite();
            Invoke("SettleRefresh", oldToken);
            Assert.AreEqual("pending", CompileWatcher.Poll().status);
            Invoke("OnCompilationStarted", new object());
            Invoke("SettleRefresh", SessionState.GetString(Keys[2], ""));
            Assert.AreEqual("compiling", CompileWatcher.Poll().status);
            Invoke("FinalizeStatus");
            Assert.AreEqual("success", CompileWatcher.Poll().status);
        }

        [Test]
        public void RefreshExceptionIsInspectableAndUnknownIsNotSuccess()
        {
            StringAssert.StartsWith("UNKNOWN:", AssetFunctions.GetCompilationStatus(new JObject()));
            CompileWatcher.BeginWrite();
            CompileWatcher.FailRefresh(new InvalidOperationException("test import failure"));
            StringAssert.Contains("test import failure", AssetFunctions.GetCompilationStatus(new JObject()));
            Assert.IsFalse(CompileWatcher.IsBusy);
        }
    }
}
