using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    /// <summary>
    /// Scans content-pack roots (default Assets/Synty) and writes a stable,
    /// idempotent asset catalog for agents: <Project>/AssetCatalog/asset-catalog.json
    /// plus PNG thumbnails under <Project>/AssetCatalog/thumbs/.
    ///
    /// Object refs are resolved via AssetDatabase (never guessed) so the
    /// records feed directly into create/update_scriptable_object fields.
    ///
    /// Large scans (1000s of fbx) run frame-pumped via AssetCatalogRunner so
    /// the Editor stays responsive — never call the record scan synchronously
    /// on the main thread. Re-runs upsert by id (no dupes) and reuse existing
    /// thumbnails, so incremental per-pack runs stay cheap.
    /// </summary>
    public static class AssetCatalogGenerator
    {
        public const string CatalogDirName = "AssetCatalog";
        public const string CatalogFileName = "asset-catalog.json";
        public const string ThumbsDirName = "thumbs";
        public const string OverrideFileName = "CatalogTagOverrides.json";

        private static readonly string[] DefaultScanRoots = { "Assets/Synty" };

        [MenuItem("Tools/Bridge/Generate Asset Catalog")]
        public static void GenerateFromMenu()
        {
            AssetCatalogRunner.Start(null, true, result =>
            {
                if (result != null)
                    Debug.Log($"[Bridge] Asset catalog: {result["itemCount"]} items, {result["thumbnailCount"]} thumbs -> {result["jsonPath"]}");
                else
                    Debug.LogWarning("[Bridge] Asset catalog run cancelled or superseded.");
            });
        }

        // MCP entry point (method "generate_asset_catalog"): starts a run, returns immediately.
        public static string GenerateAssetCatalog(JObject mcpMessage)
        {
            var rootsToken = mcpMessage["scan_roots"] ?? mcpMessage["scanRoots"];
            string[] roots = null;
            if (rootsToken != null && rootsToken.Type == JTokenType.Array)
                roots = rootsToken.ToObject<string[]>();
            var thumbsToken = mcpMessage["thumbnails"];
            var wantThumbs = thumbsToken == null || thumbsToken.ToObject<bool>();
            return AssetCatalogRunner.Start(roots, wantThumbs, null).ToString(Newtonsoft.Json.Formatting.None);
        }

        // MCP entry point (method "get_asset_catalog_status"): polls the run.
        public static string GetAssetCatalogStatus(JObject mcpMessage)
        {
            var runId = mcpMessage["run_id"]?.ToString() ?? mcpMessage["runId"]?.ToString();
            return AssetCatalogRunner.Poll(runId).ToString(Newtonsoft.Json.Formatting.None);
        }

        internal static string[] ResolveRoots(string[] scanRoots)
        {
            var roots = (scanRoots != null && scanRoots.Length > 0 ? scanRoots : DefaultScanRoots)
                .Select(r => (r ?? "").Replace("\\", "/").Trim().TrimEnd('/'))
                .Where(r => !string.IsNullOrEmpty(r) && AssetDatabase.IsValidFolder(r))
                .ToArray();
            return roots.Length > 0 ? roots : new[] { "Assets" }; // fallback when no configured root exists
        }

        internal static List<string> CollectAssetPaths(string[] roots)
        {
            var paths = new List<string>();
            foreach (var root in roots)
            {
                var folders = new[] { root };
                paths.AddRange(AssetDatabase.FindAssets("t:Model", folders)
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)));
                paths.AddRange(AssetDatabase.FindAssets("t:Prefab", folders)
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)));
                paths.AddRange(AssetDatabase.FindAssets("t:Material", folders)
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => p.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)));
            }
            paths.Sort(StringComparer.Ordinal);
            return paths;
        }

        // Generic container folders that are never a pack name.
        private static readonly HashSet<string> ContainerFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Assets", "Synty",
        };

        // Splits an asset path into pack + id. The id already starts with the
        // pack in the common case (root is a container like Assets/Synty), so it
        // is NOT pack + relative — that double-counts the pack segment.
        // When the scan root is itself a pack dir, the pack is the root's last
        // segment and relative is the path inside it.
        internal static string PackAndId(string root, string path, out string id)
        {
            var relFromRoot = path.StartsWith(root + "/", StringComparison.Ordinal) ? path.Substring(root.Length + 1) : path;
            var rootBase = root.Contains("/") ? root.Substring(root.LastIndexOf('/') + 1) : root;
            string pack, relative;
            if (!ContainerFolders.Contains(rootBase))
            {
                pack = rootBase;
                relative = relFromRoot;
            }
            else
            {
                pack = relFromRoot.Contains("/") ? relFromRoot.Substring(0, relFromRoot.IndexOf('/')) : rootBase;
                relative = relFromRoot;
            }
            id = relative.StartsWith(pack + "/", StringComparison.Ordinal) ? relative : $"{pack}/{relative}";
            return pack;
        }

        internal static JObject BuildRecord(string root, string path, Dictionary<string, JObject> overrides,
            out string thumbId)
        {
            thumbId = null;
            if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                return BuildPrefabRecord(root, path, overrides, out thumbId);
            if (path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                return BuildMaterialRecord(root, path, out thumbId);
            return BuildFbxRecord(root, path, overrides, out thumbId);
        }

        internal static JObject BuildFbxRecord(string root, string path, Dictionary<string, JObject> overrides,
            out string thumbId)
        {
            thumbId = null;
            var fbxGuid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(fbxGuid))
            {
                Debug.LogWarning($"[Bridge] Skipping '{path}': no guid.");
                return null;
            }
            var pack = PackAndId(root, path, out var id);
            var name = Path.GetFileNameWithoutExtension(path);

            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null)
            {
                Debug.LogWarning($"[Bridge] Skipping '{path}': main asset is not a GameObject.");
                return null;
            }
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(go, out var goGuid, out long goFileId))
            {
                Debug.LogWarning($"[Bridge] Skipping '{path}': cannot resolve GameObject fileID.");
                return null;
            }

            var category = InferCategory(path, name);
            var body = InferBody(name);
            if (overrides.TryGetValue(id, out var ov))
            {
                if (ov["category"] != null) category = ov["category"].ToString();
                if (ov["body"] != null) body = ov["body"].ToString();
            }

            // Note: no materials[] here by design — the fbx record is the mesh
            // source; material relations live on prefab + material records.
            thumbId = id;
            return new JObject
            {
                ["id"] = id,
                ["assetPath"] = path, // internal only — stripped before writing the catalog
                ["type"] = "fbx",
                ["name"] = name,
                ["pack"] = pack,
                ["category"] = category,
                ["fbxGuid"] = fbxGuid,
                ["gameObjectRef"] = new JObject { ["fileID"] = goFileId, ["guid"] = goGuid },
                ["meshes"] = ListSubMeshes(path, go),
                ["body"] = body,
            };
        }

        // Every Mesh sub-asset with its resolved ref. Bone counts come from the
        // SkinnedMeshRenderers using each mesh (absent = static, unskinned mesh).
        // This is what SO wiring consumes — no more guessing mesh fileIDs.
        private static JArray ListSubMeshes(string path, GameObject go)
        {
            var boneCounts = new Dictionary<long, int>();
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(smr.sharedMesh, out _, out long meshFileId)) continue;
                var bones = smr.bones?.Length ?? 0;
                if (!boneCounts.TryGetValue(meshFileId, out var prev) || prev < bones)
                    boneCounts[meshFileId] = bones;
            }
            var meshes = new List<JObject>();
            var seen = new HashSet<long>();
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (!(o is Mesh mesh)) continue;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out var meshGuid, out long meshFileId)) continue;
                if (!seen.Add(meshFileId)) continue;
                var entry = new JObject { ["name"] = mesh.name, ["fileID"] = meshFileId, ["guid"] = meshGuid };
                if (boneCounts.TryGetValue(meshFileId, out var bones))
                    entry["bones"] = bones;
                meshes.Add(entry);
            }
            meshes.Sort((a, b) => StringComparer.Ordinal.Compare(a["name"].ToString(), b["name"].ToString()));
            return new JArray(meshes.ToArray());
        }

        // A prefab record resolves the full composition: which meshes with which
        // materials and bone counts — the outfit answer in one lookup.
        internal static JObject BuildPrefabRecord(string root, string path, Dictionary<string, JObject> overrides,
            out string thumbId)
        {
            thumbId = null;
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) return null;
            var pack = PackAndId(root, path, out var id);
            var name = Path.GetFileNameWithoutExtension(path);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) return null;

            var category = InferCategory(path, name);
            var body = InferBody(name);
            if (overrides.TryGetValue(id, out var ov))
            {
                if (ov["category"] != null) category = ov["category"].ToString();
                if (ov["body"] != null) body = ov["body"].ToString();
            }

            var skinned = new List<JObject>();
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var entry = new JObject { ["node"] = smr.gameObject.name };
                if (smr.sharedMesh != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(smr.sharedMesh, out var meshGuid, out long meshFileId))
                    entry["mesh"] = MeshRef(smr.sharedMesh.name, meshFileId, meshGuid);
                entry["materials"] = ListMaterialRefs(smr.sharedMaterials);
                entry["bones"] = smr.bones?.Length ?? 0;
                skinned.Add(entry);
            }
            var statics = new List<JObject>();
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var renderer = mf.GetComponent<MeshRenderer>();
                statics.Add(new JObject
                {
                    ["node"] = mf.gameObject.name,
                    ["mesh"] = MeshRefByObject(mf.sharedMesh),
                    ["materials"] = ListMaterialRefs(renderer != null ? renderer.sharedMaterials : null),
                });
            }

            var record = new JObject
            {
                ["id"] = id,
                ["assetPath"] = path, // internal only — stripped before writing the catalog
                ["type"] = "prefab",
                ["name"] = name,
                ["pack"] = pack,
                ["category"] = category,
                ["guid"] = guid,
                ["skinnedMeshes"] = new JArray(skinned.ToArray()),
                ["staticMeshes"] = new JArray(statics.ToArray()),
                ["body"] = body,
            };
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(go, out var goGuid, out long goFileId))
                record["gameObjectRef"] = new JObject { ["fileID"] = goFileId, ["guid"] = goGuid };
            thumbId = id;
            return record;
        }

        // A material record gives pack-wide color variants their own identity:
        // name + guid + albedo, independent of any single prefab's selection.
        internal static JObject BuildMaterialRecord(string root, string path, out string thumbId)
        {
            thumbId = null;
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) return null;
            var pack = PackAndId(root, path, out var id);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) return null;
            var record = new JObject
            {
                ["id"] = id,
                ["assetPath"] = path, // internal only — stripped before writing the catalog
                ["type"] = "material",
                ["name"] = mat.name,
                ["pack"] = pack,
                ["category"] = "material",
                ["guid"] = guid,
            };
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mat, out _, out long matFileId))
                record["fileID"] = matFileId;
            if (mat.HasProperty("_BaseColor"))
                record["albedo"] = "#" + ColorUtility.ToHtmlStringRGB(mat.GetColor("_BaseColor"));
            thumbId = id;
            return record;
        }

        private static JObject MeshRef(string name, long fileId, string guid)
        {
            return new JObject { ["name"] = name, ["fileID"] = fileId, ["guid"] = guid };
        }

        private static JObject MeshRefByObject(Mesh mesh)
        {
            if (mesh != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out var guid, out long fileId))
                return MeshRef(mesh.name, fileId, guid);
            return null;
        }

        private static JArray ListMaterialRefs(Material[] materials)
        {
            var list = new List<JObject>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (materials == null) return new JArray();
            foreach (var mat in materials)
            {
                if (mat == null) continue;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mat, out var matGuid, out long _)) continue;
                if (!seen.Add(matGuid)) continue;
                list.Add(new JObject { ["name"] = mat.name, ["guid"] = matGuid });
            }
            return new JArray(list.ToArray());
        }

        internal static string ThumbFileName(string id) => id.Replace("/", "__") + ".png";

        internal static string InferCategory(string path, string name)
        {
            if (path.IndexOf("Attach_Hair", StringComparison.OrdinalIgnoreCase) >= 0) return "hair";
            if (path.IndexOf("Attach_Mask", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("Faceplate", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("Helmet", StringComparison.OrdinalIgnoreCase) >= 0) return "facegear";
            if (path.IndexOf("Characters", StringComparison.OrdinalIgnoreCase) >= 0) return "outfit";
            if (path.IndexOf("Models/Weapons", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Wep", StringComparison.OrdinalIgnoreCase) >= 0) return "weapon";
            return "prop";
        }

        internal static string InferBody(string name)
        {
            // "Female" contains "Male" — check female first.
            if (name.IndexOf("Female", StringComparison.OrdinalIgnoreCase) >= 0) return "female01";
            if (name.IndexOf("Male", StringComparison.OrdinalIgnoreCase) >= 0) return "male01";
            return "unknown";
        }

        // Optional overlay: { "overrides": { "<id>": {"category": "...", "body": "..."} } }
        internal static Dictionary<string, JObject> LoadOverrides(string catalogDir)
        {
            var result = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var ovPath = Path.Combine(catalogDir, OverrideFileName);
            if (!File.Exists(ovPath)) return result;
            try
            {
                var root = JObject.Parse(File.ReadAllText(ovPath));
                var map = root["overrides"] as JObject;
                if (map == null) return result;
                foreach (var kvp in map.Properties())
                {
                    if (kvp.Value is JObject jo)
                        result[kvp.Name] = jo;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bridge] Ignoring bad {OverrideFileName}: {e.Message}");
            }
            return result;
        }

        // Schema/id version — bump when the record shape or id format changes so old
        // catalogs rebuild instead of merging incompatible ids.
        internal const int CatalogVersion = 4; // NB: materials[] removal is shape-compatible, no rebuild needed

        // Merges scanned records into the existing catalog by id (upsert — no dupes).
        internal static JObject MergeAndWrite(string catalogDir, List<JObject> scanned)
        {
            var jsonPath = Path.Combine(catalogDir, CatalogFileName);
            var byId = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var packs = new SortedSet<string>(StringComparer.Ordinal);
            string generatedAt = DateTime.UtcNow.ToString("o");
            if (File.Exists(jsonPath))
            {
                try
                {
                    var existing = JObject.Parse(File.ReadAllText(jsonPath));
                    if (existing["catalogVersion"]?.ToObject<int>() == CatalogVersion)
                    {
                        foreach (var item in existing["items"] as JArray ?? new JArray())
                        {
                            if (item is JObject jo && jo["id"] != null)
                                byId[jo["id"].ToString()] = jo;
                        }
                    }
                    else
                    {
                        Debug.Log("[Bridge] Catalog version changed — rebuilding from scratch.");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Bridge] Existing catalog unreadable, rebuilding: {e.Message}");
                }
            }
            foreach (var record in scanned)
                byId[record["id"].ToString()] = record;
            var items = byId.Values.OrderBy(r => r["id"].ToString(), StringComparer.Ordinal).ToList();
            foreach (var item in items)
                packs.Add(item["pack"].ToString());
            var thumbCount = items.Count(i => i["thumbnail"] != null);
            var catalog = new JObject
            {
                ["catalogVersion"] = CatalogVersion,
                ["generatedAt"] = generatedAt,
                ["unityVersion"] = Application.unityVersion,
                ["packs"] = new JArray(packs.ToArray()),
                ["items"] = new JArray(items.ToArray()),
            };
            File.WriteAllText(jsonPath, catalog.ToString(Newtonsoft.Json.Formatting.Indented));
            SweepStaleThumbs(catalogDir, items);
            return new JObject
            {
                ["jsonPath"] = jsonPath,
                ["itemCount"] = items.Count,
                ["thumbnailCount"] = thumbCount,
            };
        }

        // Deletes generated PNGs no longer referenced by the catalog (e.g. after
        // an id-format change). Only touches *.png inside the managed thumbs dir.
        private static void SweepStaleThumbs(string catalogDir, List<JObject> items)
        {
            var thumbsDir = Path.Combine(catalogDir, ThumbsDirName);
            if (!Directory.Exists(thumbsDir)) return;
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var thumb = item["thumbnail"]?.ToString();
                if (!string.IsNullOrEmpty(thumb))
                    live.Add(Path.GetFileName(thumb));
            }
            foreach (var file in Directory.GetFiles(thumbsDir, "*.png"))
            {
                if (!live.Contains(Path.GetFileName(file)))
                {
                    try { File.Delete(file); }
                    catch (Exception e) { Debug.LogWarning($"[Bridge] Could not delete stale thumb '{file}': {e.Message}"); }
                }
            }
        }

        // One-time migration for thumbs captured under the v1 doubled-pack ids
        // (<pack>__<pack>__...). Returns the migrated file name or null.
        internal static string MigrateLegacyThumb(string thumbsDir, string pack, string fileName)
        {
            var legacy = Path.Combine(thumbsDir, $"{pack}__{fileName}");
            if (!File.Exists(legacy)) return null;
            var target = Path.Combine(thumbsDir, fileName);
            try
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(legacy, target);
                return $"{ThumbsDirName}/{fileName}";
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bridge] Thumb migration failed for '{fileName}': {e.Message}");
                return null;
            }
        }
    }
}
