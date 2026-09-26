using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Gamenami.UnitySemanticBridge.Editor
{
    /// <summary>Reads imported assets only: no instantiation, baking, reimport or selection changes.</summary>
    public static class ModelInspectionFunctions
    {
        public static string InspectModelAsset(JObject message)
        {
            var path = message["path"]?.ToString().Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(path) ||
                !(path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)) ||
                path.Split('/').Any(p => p == ".." || p == "." || p.Length == 0))
                return "Error: Provide a project-relative model path under Assets/ or Packages/ (for example Assets/Models/Ship.fbx), in the connected Unity project.";
            if (AssetDatabase.IsValidFolder(path))
                return "Error: Path is a folder. Supply an imported model file such as an FBX or OBJ.";
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null)
                return $"Error: No imported asset at '{path}'. Check the path and connected project; import the file in Unity first.";
            if (!(importer is ModelImporter modelImporter))
                return $"Error: '{path}' uses {importer.GetType().Name}, not ModelImporter. Supply the source FBX/OBJ model, not a prefab, material or standalone mesh asset.";
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null)
                return $"Error: '{path}' has no imported GameObject hierarchy. Check its import errors in the Unity Console.";
            var details = message["include_details"]?.Value<bool>() ?? false;
            return Describe(path, modelImporter, root, details);
        }

        private static string Describe(string path, ModelImporter importer, GameObject root, bool details)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Model: {path} (GUID {AssetDatabase.AssetPathToGUID(path)})");
            sb.AppendLine("Spaces: imported Unity units, not raw source-file coordinates. Mesh bounds = mesh-local AABB.");
            sb.AppendLine("Placement bounds = mesh AABB transformed through every local TRS, INCLUDING the imported root, into an identity parent frame (not scene/world bounds). Conservative, not tight vertex bounds; skinned meshes are undeformed, not animated bounds.");
            sb.AppendLine($"Importer: globalScale={F(importer.globalScale)}, useFileScale={importer.useFileScale}, fileScale={F(importer.fileScale)}, useFileUnits={importer.useFileUnits}, bakeAxisConversion={importer.bakeAxisConversion}");
            sb.AppendLine("Axis target: Unity left-handed, Y-up, Z-forward. bakeAxisConversion=True bakes conversion into geometry/animation; False uses the imported root transform. Original source axis metadata is not exposed here; do not apply importer scale again to these bounds.");
            if (details)
                sb.AppendLine($"Importer detail: isReadable={importer.isReadable}, preserveHierarchy={importer.preserveHierarchy}, optimizeGameObjects={importer.optimizeGameObjects}, materialImportMode={importer.materialImportMode}, materialLocation={importer.materialLocation}");

            var subassets = AssetDatabase.LoadAllAssetsAtPath(path);
            var meshes = subassets.OfType<Mesh>().OrderBy(m => m.name, StringComparer.Ordinal).ThenBy(LocalId).ToArray();
            sb.AppendLine($"Meshes ({meshes.Length}):");
            foreach (var mesh in meshes)
            {
                sb.AppendLine($"  {Reference(mesh, path)} vertices={mesh.vertexCount} submeshes={mesh.subMeshCount}; mesh-local {BoundsText(mesh.bounds)}");
                if (details)
                    for (var i = 0; i < mesh.subMeshCount; i++)
                        sb.AppendLine($"    submesh[{i}]: topology={mesh.GetTopology(i)} indices={mesh.GetIndexCount(i)}");
            }

            sb.AppendLine("Imported hierarchy (local position, quaternion xyzw, scale relative to parent; [n] = sibling index; optimized imports may omit source nodes):");
            Bounds? aggregate = null;
            var nodes = new Stack<(Transform node, string path, Matrix4x4 parent)>();
            nodes.Push((root.transform, root.name + "[0]", Matrix4x4.identity));
            while (nodes.Count > 0)
            {
                var entry = nodes.Pop();
                var t = entry.node;
                var matrix = entry.parent * Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale);
                var q = t.localRotation;
                sb.AppendLine($"  {entry.path}: p={Vector(t.localPosition)} q=({F(q.x)},{F(q.y)},{F(q.z)},{F(q.w)}) s={Vector(t.localScale)}");
                foreach (var filter in t.GetComponents<MeshFilter>())
                    AppendPlacement(sb, filter.sharedMesh, matrix, path, ref aggregate);
                foreach (var renderer in t.GetComponents<Renderer>())
                {
                    if (renderer is SkinnedMeshRenderer skinned)
                        AppendPlacement(sb, skinned.sharedMesh, matrix, path, ref aggregate);
                    var slots = renderer.sharedMaterials;
                    sb.AppendLine($"    {renderer.GetType().Name} material slots ({slots.Length}): " +
                        string.Join("; ", slots.Select((m, i) => $"[{i}] {Reference(m, path)}")));
                }
                for (var i = t.childCount - 1; i >= 0; i--)
                {
                    var child = t.GetChild(i);
                    nodes.Push((child, entry.path + "/" + child.name + $"[{i}]", matrix));
                }
            }
            sb.AppendLine(aggregate.HasValue ? $"Combined placement AABB: {BoundsText(aggregate.Value)}" : "Combined placement AABB: none (no assigned meshes).");
            var embeddedMaterials = subassets.OfType<Material>().OrderBy(m => m.name, StringComparer.Ordinal).ThenBy(LocalId).ToArray();
            sb.AppendLine($"Embedded materials ({embeddedMaterials.Length}, including unassigned):");
            foreach (var material in embeddedMaterials) sb.AppendLine("  " + Reference(material, path));
            if (details)
            {
                sb.AppendLine("Other embedded subassets:");
                foreach (var asset in subassets.Where(a => a != null && AssetDatabase.IsSubAsset(a) && !(a is Mesh) && !(a is Material))
                    .OrderBy(a => a.name, StringComparer.Ordinal).ThenBy(LocalId))
                    sb.AppendLine($"  {asset.GetType().Name}: {Reference(asset, path)}");
            }
            var dependencies = AssetDatabase.GetDependencies(path, details).Where(p => p != path)
                .Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
            sb.AppendLine($"External file dependencies ({dependencies.Length}, {(details ? "recursive" : "direct")}; outgoing, NOT incoming references; embedded subassets listed above):");
            foreach (var dependency in dependencies)
                sb.AppendLine($"  {dependency} (GUID {AssetDatabase.AssetPathToGUID(dependency)})");
            return sb.ToString();
        }

        private static void AppendPlacement(StringBuilder sb, Mesh mesh, Matrix4x4 matrix, string path, ref Bounds? aggregate)
        {
            if (mesh == null) { sb.AppendLine("    mesh: null/unassigned"); return; }
            var bounds = TransformBounds(mesh.bounds, matrix);
            sb.AppendLine($"    mesh {Reference(mesh, path)}; placement {BoundsText(bounds)}");
            if (aggregate.HasValue)
            {
                var combined = aggregate.Value;
                combined.Encapsulate(bounds);
                aggregate = combined;
            }
            else aggregate = bounds;
        }

        // Transform the box, not its size vector: handles rotations, negative/nonuniform scale and shear.
        internal static Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
        {
            var center = matrix.MultiplyPoint3x4(bounds.center);
            var e = bounds.extents;
            var x = matrix.MultiplyVector(new Vector3(e.x, 0, 0));
            var y = matrix.MultiplyVector(new Vector3(0, e.y, 0));
            var z = matrix.MultiplyVector(new Vector3(0, 0, e.z));
            var extents = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y), Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
            return new Bounds(center, extents * 2);
        }

        private static long LocalId(Object asset) => AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out _, out long id) ? id : 0;
        private static string Reference(Object asset, string modelPath)
        {
            if (asset == null) return "null/unassigned";
            var path = AssetDatabase.GetAssetPath(asset);
            string location;
            if (path == modelPath)
                location = AssetDatabase.IsSubAsset(asset) ? "embedded subasset" : "model main asset";
            else if (string.IsNullOrEmpty(path))
                location = "non-asset";
            else if (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal))
                location = "external " + (AssetDatabase.IsSubAsset(asset) ? "subasset " : "asset ") + path;
            else
                location = "built-in resource " + path;
            var id = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out var guid, out long fileId)
                ? $"GUID {guid}, fileID {fileId}" : "persistent ID unavailable";
            return $"{asset.name} ({location}; {id})";
        }
        private static string F(float value) => value.ToString("G7", CultureInfo.InvariantCulture);
        private static string Vector(Vector3 v) => $"({F(v.x)},{F(v.y)},{F(v.z)})";
        private static string BoundsText(Bounds b) => $"bounds center={Vector(b.center)} size={Vector(b.size)}";
    }
}
