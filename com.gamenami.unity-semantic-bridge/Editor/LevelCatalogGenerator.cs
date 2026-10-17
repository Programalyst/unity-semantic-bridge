using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gamenami.UnitySemanticBridge.Editor.Layout;
using Gamenami.UnitySemanticBridge.Layout;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor
{
    /// <summary>
    /// Level catalog cards (asset intelligence Phase 4a): one card per level-relevant prefab,
    /// measured on PrefabUtility.LoadPrefabContents (never in an open scene), written to
    /// <Project>/AssetCatalog/level-catalog.json keyed by GUID. Runs through
    /// AssetCatalogRunner (catalog "level"), which batches it and pumps thumbnails into
    /// AssetCatalog/level-thumbs/. Card fields and the overrides file: docs/level-catalog.md.
    /// </summary>
    public static class LevelCatalogGenerator
    {
        public const string CatalogFileName = "level-catalog.json";
        public const string ThumbsDirName = "level-thumbs";
        internal const int LevelCatalogVersion = 1;

        // Asset catalog categories that never belong in a level.
        private static readonly HashSet<string> NonLevelCategories = new HashSet<string>(StringComparer.Ordinal)
        {
            "hair", "facegear", "outfit", "weapon",
        };

        internal static LevelCatalogOverrides LoadOverrides(string catalogDir)
        {
            var path = Path.Combine(catalogDir, LevelCatalogOverrides.FileName);
            // A bad overrides file stops the run: silently ignoring it would publish unreviewed roles and scales.
            return File.Exists(path) ? LevelCatalogOverrides.Parse(File.ReadAllText(path)) : LevelCatalogOverrides.Empty;
        }

        internal static List<string> CollectPrefabPaths(string[] roots) =>
            AssetCatalogGenerator.CollectAssetPaths(roots)
                .Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                .ToList();

        /// <summary>
        /// The run record for one prefab: the card JSON plus internal "guid"/"assetPath"/"type"
        /// fields (stripped on write), or null when the prefab is not a level piece. Excluded
        /// pieces return null and set <paramref name="excludedGuid"/> so a re-run drops them.
        /// </summary>
        public static JObject BuildRecord(string path, LevelCatalogOverrides overrides, out string excludedGuid)
        {
            excludedGuid = null;
            var card = BuildCard(path, overrides, out var excluded);
            if (excluded) excludedGuid = AssetDatabase.AssetPathToGUID(path);
            if (card == null) return null;
            var record = LevelCards.ToJson(card);
            record["guid"] = card.Guid;
            record["assetPath"] = path;
            record["type"] = "prefab";
            return record;
        }

        /// <summary>
        /// Pack and catalog id from the asset path alone, so a card's id does not depend on the
        /// scan root: leading "Assets/", "3rd Party/" and "Synty/" are dropped and the next folder
        /// is the pack ("PolygonApocalypseWasteland/Prefabs/Props/SM_Prop_Barrier_01.prefab"), the
        /// same id a default asset catalog scan gives, in Asset Preview (Assets/Synty/...) and in
        /// a game that imported the pack under Assets/3rd Party/. Override globs match this id.
        /// </summary>
        public static string PackAndId(string path, out string id)
        {
            var rest = path.Replace("\\", "/");
            foreach (var container in new[] { "Assets/", "3rd Party/", "Synty/" })
                if (rest.StartsWith(container, StringComparison.Ordinal))
                    rest = rest.Substring(container.Length);
            id = rest;
            var slash = rest.IndexOf('/');
            return slash > 0 ? rest.Substring(0, slash) : "Assets";
        }

        /// <summary>Measures and classifies one prefab; null when it is not a level piece.</summary>
        public static LevelCard BuildCard(string path, LevelCatalogOverrides overrides, out bool excluded)
        {
            excluded = false;
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) return null;
            var pack = PackAndId(path, out var id);
            var name = Path.GetFileNameWithoutExtension(path);
            var entry = (overrides ?? LevelCatalogOverrides.Empty).Resolve(guid, id);
            if (entry?.Exclude == true)
            {
                excluded = true;
                return null;
            }
            // Characters, attachments and weapons are not level pieces unless an override says so.
            if (entry?.Role == null && NonLevelCategories.Contains(AssetCatalogGenerator.InferCategory(path, name)))
                return null;

            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var rootTransform = contents.transform;
                var rootRotation = rootTransform.localEulerAngles;
                var rootScale = rootTransform.localScale;
                // Measure in root space at scale 1: the root's own scale and placement don't count.
                rootTransform.localPosition = Vector3.zero;
                rootTransform.localRotation = Quaternion.identity;
                rootTransform.localScale = Vector3.one;

                if (entry?.Role == null && contents.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 0)
                    return null; // rigged characters and creatures
                var measured = Measure(contents);
                if (!measured.visual.HasValue) return null; // nothing visible (FX, empty helpers)

                var card = new LevelCard
                {
                    Guid = guid,
                    Id = id,
                    Name = name,
                    Pack = pack,
                    VisualBounds = measured.visual.Value,
                    ColliderBounds = measured.colliders,
                    Skewed = measured.skewed,
                    RootRotation = ToVec(NormalizeEuler(rootRotation)),
                };
                LevelCards.Classify(card, ToVec(rootScale), entry);
                return card;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // MCP entry point (method "describe_level_cards"): measures the given prefab paths and
        // returns their cards with footprints for a cell size, writing nothing. For spot checks.
        public static string DescribeLevelCards(JObject mcpMessage)
        {
            var paths = (mcpMessage["paths"] as JArray)?.ToObject<string[]>();
            if (paths == null || paths.Length == 0)
                throw new BridgeToolException("describe_level_cards needs 'paths' (project-relative .prefab paths).");
            var cellSize = mcpMessage["cell_size"]?.ToObject<double>() ?? mcpMessage["cellSize"]?.ToObject<double>() ?? 2.0;
            var catalogDir = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), AssetCatalogGenerator.CatalogDirName);
            var overrides = LoadOverrides(catalogDir);
            var result = new JObject();
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                {
                    result[path] = "no asset at this path";
                    continue;
                }
                var card = BuildCard(path, overrides, out var excluded);
                if (card == null)
                {
                    result[path] = excluded ? $"excluded by {LevelCatalogOverrides.FileName}" : "not a level piece";
                    continue;
                }
                var json = LevelCards.ToJson(card);
                json["guid"] = card.Guid;
                var footprint = LevelCards.Footprint(card, cellSize);
                json["footprint"] = new JObject
                {
                    ["cellSize"] = cellSize,
                    ["cells"] = new JArray(footprint.Width, footprint.Depth),
                    ["meters"] = new JArray(Math.Round(footprint.Size.X, 3), Math.Round(footprint.Size.Y, 3), Math.Round(footprint.Size.Z, 3)),
                    ["fitError"] = new JArray(Math.Round(footprint.FitErrorX, 3), Math.Round(footprint.FitErrorZ, 3)),
                    ["height"] = Math.Round(footprint.Height, 3),
                    ["approximate"] = footprint.Approximate,
                };
                result[path] = json;
            }
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static (Box3? visual, Box3? colliders, bool skewed) Measure(GameObject root)
        {
            var toRoot = root.transform.worldToLocalMatrix;
            var visual = new List<Vector3>();
            var colliders = new List<Vector3>();
            var skewed = false;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled || renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer) continue;
                var mesh = renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
                if (mesh == null) continue;
                visual.AddRange(PieceGeometry.BoxCorners(toRoot * renderer.transform.localToWorldMatrix, mesh.bounds.center, mesh.bounds.size));
                skewed |= IsSkewed(renderer.transform, root.transform);
            }
            foreach (var collider in root.GetComponentsInChildren<Collider>(false))
            {
                if (!collider.enabled || collider.isTrigger) continue;
                foreach (var corner in PieceGeometry.ColliderCorners(collider))
                    colliders.Add(toRoot.MultiplyPoint3x4(corner));
                skewed |= IsSkewed(collider.transform, root.transform);
            }
            return (BoxOf(visual), BoxOf(colliders), skewed);
        }

        // True when a child's rotation relative to the root is not made of 90° steps.
        private static bool IsSkewed(Transform child, Transform root)
        {
            var relative = Matrix4x4.Rotate(Quaternion.Inverse(root.rotation) * child.rotation);
            for (var column = 0; column < 3; column++)
            {
                var axis = (Vector3)relative.GetColumn(column);
                var largest = Mathf.Max(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z));
                if (largest < 0.9999f) return true; // more than ~0.8° off an axis
            }
            return false;
        }

        private static Box3? BoxOf(List<Vector3> points)
        {
            if (points.Count == 0) return null;
            var min = points[0];
            var max = points[0];
            foreach (var p in points)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            return Box3.FromMinMax(ToVec(min), ToVec(max));
        }

        private static Vector3 NormalizeEuler(Vector3 euler)
        {
            float Wrap(float angle)
            {
                var a = Mathf.Repeat(angle, 360f);
                var rounded = Mathf.Round(a * 1000f) / 1000f;
                return rounded >= 360f ? 0f : rounded;
            }
            return new Vector3(Wrap(euler.x), Wrap(euler.y), Wrap(euler.z));
        }

        private static Vec3 ToVec(Vector3 v) => new Vec3(v.x, v.y, v.z);

        /// <summary>
        /// Upserts scanned cards by GUID into level-catalog.json, drops excluded GUIDs, and
        /// writes the file sorted by id. Returns {jsonPath, itemCount, thumbnailCount}.
        /// </summary>
        public static JObject MergeAndWrite(string catalogDir, List<JObject> scanned, ICollection<string> excludedGuids)
        {
            var jsonPath = Path.Combine(catalogDir, CatalogFileName);
            var byGuid = new Dictionary<string, JObject>(StringComparer.Ordinal);
            if (File.Exists(jsonPath))
            {
                try
                {
                    var existing = JObject.Parse(File.ReadAllText(jsonPath));
                    if (existing["levelCatalogVersion"]?.ToObject<int>() == LevelCatalogVersion && existing["cards"] is JObject cards)
                    {
                        foreach (var property in cards.Properties())
                        {
                            if (property.Value is JObject card)
                                byGuid[property.Name] = card;
                        }
                    }
                    else
                    {
                        Debug.Log("[Bridge] Level catalog version changed; rebuilding from scratch.");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Bridge] Existing level catalog unreadable, rebuilding: {e.Message}");
                }
            }
            foreach (var guid in excludedGuids ?? Array.Empty<string>())
                byGuid.Remove(guid);
            foreach (var record in scanned)
            {
                var guid = record["guid"].ToString();
                record.Remove("guid");
                record.Remove("assetPath");
                record.Remove("type");
                byGuid[guid] = record;
            }
            var ordered = byGuid.OrderBy(kv => kv.Value["id"]?.ToString() ?? kv.Key, StringComparer.Ordinal).ToList();
            var cardsJson = new JObject();
            foreach (var kv in ordered)
                cardsJson[kv.Key] = kv.Value;
            var roles = new JObject();
            foreach (var group in ordered.GroupBy(kv => kv.Value["levelRole"]?.ToString() ?? "unknown").OrderBy(g => g.Key, StringComparer.Ordinal))
                roles[group.Key] = group.Count();
            var catalog = new JObject
            {
                ["levelCatalogVersion"] = LevelCatalogVersion,
                ["generatedAt"] = DateTime.UtcNow.ToString("o"),
                ["unityVersion"] = Application.unityVersion,
                ["units"] = "meters at prefab root scale 1, root space; footprints are computed on request (docs/level-catalog.md)",
                ["packs"] = new JArray(ordered.Select(kv => kv.Value["pack"]?.ToString()).Where(p => p != null).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray()),
                ["roles"] = roles,
                ["cards"] = cardsJson,
            };
            File.WriteAllText(jsonPath, catalog.ToString(Newtonsoft.Json.Formatting.Indented));
            SweepStaleThumbs(catalogDir, ordered.Select(kv => kv.Value));
            return new JObject
            {
                ["jsonPath"] = jsonPath,
                ["itemCount"] = ordered.Count,
                ["thumbnailCount"] = ordered.Count(kv => kv.Value["thumbnail"] != null),
                ["roles"] = roles,
            };
        }

        // Deletes level thumbnails no card references (only *.png inside level-thumbs/).
        private static void SweepStaleThumbs(string catalogDir, IEnumerable<JObject> cards)
        {
            var thumbsDir = Path.Combine(catalogDir, ThumbsDirName);
            if (!Directory.Exists(thumbsDir)) return;
            var live = new HashSet<string>(cards.Select(c => c["thumbnail"]?.ToString()).Where(t => !string.IsNullOrEmpty(t)).Select(Path.GetFileName),
                StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(thumbsDir, "*.png"))
            {
                if (live.Contains(Path.GetFileName(file))) continue;
                try { File.Delete(file); }
                catch (Exception e) { Debug.LogWarning($"[Bridge] Could not delete stale level thumb '{file}': {e.Message}"); }
            }
        }
    }
}
