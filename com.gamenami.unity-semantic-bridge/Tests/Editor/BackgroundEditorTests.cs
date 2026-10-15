using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Gamenami.UnitySemanticBridge.Editor.Tests
{
    public class BackgroundEditorTests
    {
        [UnityTest]
        public IEnumerator EditorTickRunsOnceOnALaterUpdate()
        {
            var runs = 0;
            EditorTick.Next(() => runs++);
            Assert.AreEqual(0, runs, "must not run synchronously, only on a later update");
            for (var frame = 0; frame < 5; frame++) yield return null;
            Assert.AreEqual(1, runs);
        }

        [TestCase("Assets/Scripts/Player.cs", true)]
        [TestCase("Packages/com.example/Runtime/Example.Runtime.asmdef", true)]
        [TestCase("Assets/Plugins/Lib.DLL", true)]
        [TestCase("Assets/Scripts/Folder.asmref", true)]
        [TestCase("Assets/csc.rsp", true)]
        [TestCase("Assets/Prefabs/Player.prefab", false)]
        [TestCase("Assets/Docs/notes.cs.txt", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void CompilationInputsAreRecognized(string path, bool expected)
        {
            Assert.AreEqual(expected, ScriptImportTracker.IsCompilationInput(path));
        }
    }
}
