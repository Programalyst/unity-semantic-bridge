using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Gamenami.UnitySemanticBridge.Layout;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gamenami.UnitySemanticBridge.Editor.Layout
{
    /// <summary>A level card with the prefab path it was built from (null for cards read from level-catalog.json).</summary>
    public sealed class CatalogEntry
    {
        public LevelCard Card;
        public string Path;
    }

    /// <summary>Filters for query_level_catalog; null fields don't filter.</summary>
    public sealed class CatalogFilter
    {
        public HashSet<LevelRole> Roles;
        public CoverLevel? Cover;
        /// <summary>Largest footprint in cells at rot 0, [w, h]; a card matches in either orientation.</summary>
        public Cell? MaxCells;
        public Cell? MinCells;
        /// <summary>Case-insensitive substring of the card id.</summary>
        public string Name;
        public double CellSize = 2.0;
        public int Limit = 50;
        public int Offset;
    }

    /// <summary>
    /// query_level_catalog: finds level pieces by role, cover, footprint and name, as compact rows.
    /// Its source is the project's generated level-catalog.json (Asset Preview), or, in a game project
    /// that keeps none (NK), the reviewed pieces: every per-piece entry in the overrides file, built into
    /// a card from its prefab the way apply and bake do (LevelCardSource). The reply names the file read.
    /// </summary>
    public static class LevelCatalogQuery
    {
        public const int MaxLimit = 500;

        // MCP entry point (method "query_level_catalog").
        public static string Query(JObject mcpMessage)
        {
            var catalogDir = System.IO.Path.Combine(System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..")),
                AssetCatalogGenerator.CatalogDirName);
            return Run(mcpMessage ?? new JObject(), catalogDir);
        }

        /// <summary>The query against the catalog folder <paramref name="catalogDir"/> (tests pass their own).</summary>
        public static string Run(JObject args, string catalogDir)
        {
            var filter = ParseFilter(args);
            var source = args["source"]?.Value<string>() ?? "auto";
            if (source != "auto" && source != "catalog" && source != "reviewed")
                throw new BridgeToolException($"source must be auto, catalog or reviewed (was '{source}').");

            var catalogPath = System.IO.Path.Combine(catalogDir, LevelCatalogGenerator.CatalogFileName);
            var overridesPath = System.IO.Path.Combine(catalogDir, LevelCatalogOverrides.FileName);
            var useCatalog = source == "catalog" || (source == "auto" && File.Exists(catalogPath));
            List<CatalogEntry> entries;
            var problems = new List<string>();
            string header;
            if (useCatalog)
            {
                if (!File.Exists(catalogPath))
                    throw new BridgeToolException($"No level catalog at {catalogPath}. Generate it with generate_asset_catalog(catalog='level'), or use source='reviewed'.");
                Dictionary<string, LevelCard> cards;
                try
                {
                    cards = LevelCards.ParseCatalog(File.ReadAllText(catalogPath));
                }
                catch (Exception e) when (e is FormatException || e is Newtonsoft.Json.JsonException || e is InvalidCastException)
                {
                    throw new BridgeToolException($"{catalogPath} is unreadable: {e.Message}");
                }
                entries = cards.Values.Select(c => new CatalogEntry { Card = c }).ToList();
                header = $"Source: level catalog {catalogPath} ({entries.Count} cards).";
            }
            else
            {
                if (!File.Exists(overridesPath))
                {
                    return $"No level catalog here: neither {catalogPath} nor {overridesPath} exists. " +
                           "Generate a catalog with generate_asset_catalog(catalog='level'), or review pieces into the overrides file (docs/level-catalog.md).";
                }
                LevelCatalogOverrides overrides;
                try
                {
                    overrides = LevelCatalogOverrides.Parse(File.ReadAllText(overridesPath));
                }
                catch (FormatException e)
                {
                    throw new BridgeToolException(e.Message);
                }
                entries = ReviewedPieces(overrides, problems);
                header = $"Source: reviewed pieces, the per-piece entries of {overridesPath} ({overrides.PieceCount} entries), with cards built from their prefabs.";
            }
            return Format(header, entries, filter, problems);
        }

        /// <summary>
        /// A card for every per-piece entry (by GUID, or by catalog id over the project's prefabs). Entries
        /// that resolve to no prefab, or to a prefab that is not a level piece, go to <paramref name="problems"/>;
        /// excluded entries are skipped.
        /// </summary>
        public static List<CatalogEntry> ReviewedPieces(LevelCatalogOverrides overrides, List<string> problems)
        {
            var entries = new List<CatalogEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, string> pathsById = null;
            foreach (var key in overrides.PieceKeys)
            {
                string path;
                if (IsGuid(key))
                {
                    path = AssetDatabase.GUIDToAssetPath(key);
                }
                else
                {
                    pathsById ??= PrefabPathsById();
                    pathsById.TryGetValue(key, out path);
                }
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                {
                    problems.Add($"MISSING {key}: no prefab in this project");
                    continue;
                }
                var card = LevelCatalogGenerator.BuildCard(path, overrides, out var excluded);
                if (excluded) continue;
                if (card == null)
                {
                    problems.Add($"NOT_A_PIECE {key}: {path} is not a level piece");
                    continue;
                }
                if (seen.Add(card.Guid)) entries.Add(new CatalogEntry { Card = card, Path = path });
            }
            return entries;
        }

        /// <summary>The header, the matching rows (TSV, sorted by id, paged) and any problems.</summary>
        public static string Format(string header, IReadOnlyList<CatalogEntry> entries, CatalogFilter filter, IReadOnlyList<string> problems)
        {
            var matches = entries.Select(e => (entry: e, footprint: LevelCards.Footprint(e.Card, filter.CellSize)))
                .Where(m => Matches(m.entry.Card, m.footprint, filter))
                .OrderBy(m => m.entry.Card.Id ?? m.entry.Card.Guid, StringComparer.Ordinal)
                .ToList();
            var page = matches.Skip(filter.Offset).Take(filter.Limit).ToList();
            var withPaths = page.Any(m => m.entry.Path != null);

            var text = new StringBuilder();
            text.AppendLine(header);
            var range = page.Count == 0 ? "no rows" : $"rows {filter.Offset + 1}-{filter.Offset + page.Count}";
            text.AppendLine($"{matches.Count} of {entries.Count} match ({range}); cells at rot 0 for a {Num(filter.CellSize)} m cell, height in meters at placementScale.");
            if (filter.Offset + page.Count < matches.Count)
                text.AppendLine($"More: offset={filter.Offset + page.Count}.");
            if (page.Count > 0)
            {
                text.AppendLine("guid\trole\tcover\tcells\theight\tscale\tid" + (withPaths ? "\tpath" : ""));
                foreach (var (entry, footprint) in page)
                {
                    var card = entry.Card;
                    var role = LevelCards.RoleName(card.Role) + (card.Walls != null ? "+walls" : "");
                    var row = string.Join("\t", card.Guid, role, card.Cover?.ToString() ?? "-",
                        $"{footprint.Width}x{footprint.Depth}", Num(footprint.Height), Scale(card.PlacementScale), card.Id);
                    if (withPaths) row += "\t" + (entry.Path ?? "-");
                    text.AppendLine(row);
                }
            }
            if (problems != null && problems.Count > 0)
            {
                text.AppendLine($"Problems ({problems.Count}):");
                foreach (var line in problems) text.AppendLine(line);
            }
            return text.ToString().TrimEnd();
        }

        public static bool Matches(LevelCard card, CardFootprint footprint, CatalogFilter filter)
        {
            if (filter.Roles != null && !filter.Roles.Contains(card.Role)) return false;
            if (filter.Cover.HasValue && card.Cover != filter.Cover) return false;
            if (!string.IsNullOrEmpty(filter.Name) && (card.Id ?? card.Name ?? "").IndexOf(filter.Name, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            int w = footprint.Width, d = footprint.Depth;
            if (filter.MaxCells is Cell max && !((w <= max.X && d <= max.Y) || (d <= max.X && w <= max.Y))) return false;
            if (filter.MinCells is Cell min && !((w >= min.X && d >= min.Y) || (d >= min.X && w >= min.Y))) return false;
            return true;
        }

        private static CatalogFilter ParseFilter(JObject args)
        {
            var filter = new CatalogFilter
            {
                CellSize = args["cell_size"]?.Value<double?>() ?? LayoutAdapters.TryFind()?.Rules?.CellSize ?? 2.0,
                Limit = args["limit"]?.Value<int?>() ?? 50,
                Offset = args["offset"]?.Value<int?>() ?? 0,
                Name = args["name"]?.Value<string>(),
            };
            if (filter.CellSize <= 0) throw new BridgeToolException("cell_size must be positive.");
            if (filter.Limit < 1 || filter.Limit > MaxLimit) throw new BridgeToolException($"limit must be 1-{MaxLimit}.");
            if (filter.Offset < 0) throw new BridgeToolException("offset must not be negative.");
            try
            {
                if (args["role"] is JArray roles && roles.Count > 0)
                    filter.Roles = new HashSet<LevelRole>(roles.Select(r => LevelCards.ParseRole((string)r)));
                else if (args["role"] is JValue role && role.Type == JTokenType.String)
                    filter.Roles = new HashSet<LevelRole> { LevelCards.ParseRole((string)role) };
                var cover = args["cover"]?.Value<string>();
                if (!string.IsNullOrEmpty(cover)) filter.Cover = LevelCards.ParseCover(cover);
            }
            catch (FormatException e)
            {
                throw new BridgeToolException(e.Message);
            }
            filter.MaxCells = ReadCells(args["max_cells"], "max_cells");
            filter.MinCells = ReadCells(args["min_cells"], "min_cells");
            return filter;
        }

        private static Cell? ReadCells(JToken token, string name)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (!(token is JArray pair) || pair.Count != 2 || pair.Any(t => t.Type != JTokenType.Integer))
                throw new BridgeToolException($"{name} must be [w, h] in whole cells.");
            var cells = new Cell((int)pair[0], (int)pair[1]);
            if (cells.X < 1 || cells.Y < 1) throw new BridgeToolException($"{name} must be at least [1, 1].");
            return cells;
        }

        private static Dictionary<string, string> PrefabPathsById()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                LevelCatalogGenerator.PackAndId(path, out var id);
                result.TryAdd(id, path);
            }
            return result;
        }

        private static bool IsGuid(string key) =>
            key.Length == 32 && key.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        private static string Num(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

        private static string Scale(Vec3 scale) =>
            scale.ApproximatelyEquals(Vec3.One) ? "1"
            : scale.X == scale.Y && scale.Y == scale.Z ? Num(scale.X)
            : $"{Num(scale.X)},{Num(scale.Y)},{Num(scale.Z)}";
    }
}
