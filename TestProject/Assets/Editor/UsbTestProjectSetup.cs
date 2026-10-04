using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// Fixture preparation belongs to this disposable host, not to the shipped package.
public static class UsbTestProjectSetup
{
    const string Font = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";
    static double deadline;

    [MenuItem("USB Tests/Prepare Fixtures")]
    public static void Prepare()
    {
        if (Path.GetFileName(Path.GetDirectoryName(Application.dataPath)) != "TestProject")
            throw new InvalidOperationException("Fixture preparation is restricted to the USB TestProject.");
        if (AssetDatabase.LoadMainAssetAtPath(Font) != null) { Finish(); return; }
        const string resources = "Packages/com.unity.textmeshpro/Package Resources/TMP Essential Resources.unitypackage";
        if (!File.Exists(resources)) throw new FileNotFoundException("Install TMP 3.0.7 before preparing UI fixtures.", resources);
        // ImportPackage completes asynchronously. The batch runner must not use -quit:
        // wait for import completion and verify the font before explicitly exiting.
        deadline = EditorApplication.timeSinceStartup + 120;
        AssetDatabase.importPackageCompleted += Imported;
        AssetDatabase.importPackageFailed += Failed;
        AssetDatabase.importPackageCancelled += Cancelled;
        EditorApplication.update += CheckDeadline;
        AssetDatabase.ImportPackage(resources, false);
    }
    static void Unsubscribe()
    {
        AssetDatabase.importPackageCompleted -= Imported;
        AssetDatabase.importPackageFailed -= Failed;
        AssetDatabase.importPackageCancelled -= Cancelled;
        EditorApplication.update -= CheckDeadline;
    }
    static void Imported(string package)
    {
        Unsubscribe(); EditorApplication.delayCall += Finish;
    }
    static void Failed(string package, string error) { Abort(error); }
    static void Cancelled(string package) { Abort("TMP resource import was cancelled."); }
    static void CheckDeadline()
    {
        if (EditorApplication.timeSinceStartup >= deadline) Abort("TMP resource import exceeded 120 seconds.");
    }
    static void Abort(string error)
    {
        Unsubscribe(); Debug.LogError(error);
        if (Application.isBatchMode) EditorApplication.Exit(1);
    }
    static void Finish()
    {
        if (AssetDatabase.LoadMainAssetAtPath(Font) == null) { Abort("TMP import completed without the required font fixture."); return; }
        const string scratch = "Assets/__BridgeTests__";
        if (!AssetDatabase.IsValidFolder(scratch)) AssetDatabase.CreateFolder("Assets", "__BridgeTests__");
        const string meshPath = scratch + "/Triangle.asset";
        if (AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) == null)
        {
            var mesh = new Mesh { name = "USB test triangle" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 }; mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, meshPath);
        }
        AssetDatabase.SaveAssets(); // Explicit fixture setup, never a tool side effect.
        Debug.Log("USB TestProject fixtures prepared and verified.");
        if (Application.isBatchMode) EditorApplication.Exit(0);
    }
}
