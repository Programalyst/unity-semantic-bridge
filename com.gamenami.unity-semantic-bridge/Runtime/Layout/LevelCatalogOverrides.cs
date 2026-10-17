using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Gamenami.UnitySemanticBridge.Layout
{
    /// <summary>Overridden card fields; null fields keep the detected value.</summary>
    public sealed class LevelOverride
    {
        public LevelRole? Role;
        public CoverLevel? Cover;
        public Vec3? PlacementScale;
        public int? Storeys;
        /// <summary>Pivot height above the floor when placed (meters), for pieces modelled below their pivot.</summary>
        public double? Lift;
        /// <summary>Horizontal size in meters the piece claims on the grid (rotation 0), instead of its bounds.</summary>
        public FootprintSize? Footprint;
        /// <summary>Leave the prefab out of the level catalog.</summary>
        public bool? Exclude;
        public string Note;

        /// <summary>Copies the fields <paramref name="other"/> sets over this one's.</summary>
        public void Apply(LevelOverride other)
        {
            if (other == null) return;
            Role = other.Role ?? Role;
            Cover = other.Cover ?? Cover;
            PlacementScale = other.PlacementScale ?? PlacementScale;
            Storeys = other.Storeys ?? Storeys;
            Lift = other.Lift ?? Lift;
            Footprint = other.Footprint ?? Footprint;
            Exclude = other.Exclude ?? Exclude;
            Note = other.Note ?? Note;
        }
    }

    /// <summary>
    /// The level catalog's overrides file (AssetCatalog/LevelCatalogOverrides.json): ordered
    /// glob rules over catalog ids for whole families, then per-piece exceptions keyed by
    /// prefab GUID (or catalog id) that win over the rules. Strict: unknown keys are errors.
    /// </summary>
    public sealed class LevelCatalogOverrides
    {
        public const string FileName = "LevelCatalogOverrides.json";

        private readonly List<(string pattern, Regex regex, LevelOverride entry)> _rules = new List<(string, Regex, LevelOverride)>();
        private readonly Dictionary<string, LevelOverride> _pieces = new Dictionary<string, LevelOverride>(StringComparer.Ordinal);

        public int RuleCount => _rules.Count;
        public int PieceCount => _pieces.Count;

        public static readonly LevelCatalogOverrides Empty = new LevelCatalogOverrides();

        private static readonly HashSet<string> EntryKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "role", "cover", "placementScale", "storeys", "lift", "footprint", "exclude", "note",
        };

        public static LevelCatalogOverrides Parse(string text)
        {
            JObject root;
            try { root = JObject.Parse(text); }
            catch (Exception e) { throw new FormatException($"{FileName}: {e.Message}"); }
            var result = new LevelCatalogOverrides();
            foreach (var property in root.Properties())
            {
                switch (property.Name)
                {
                    case "v":
                        if ((int)property.Value != 1) throw new FormatException($"{FileName}: unsupported version {property.Value}.");
                        break;
                    case "rules":
                        if (!(property.Value is JArray rules)) throw new FormatException($"{FileName}: 'rules' must be an array.");
                        foreach (var token in rules)
                        {
                            if (!(token is JObject rule) || !(rule["match"] is JValue match) || match.Type != JTokenType.String)
                                throw new FormatException($"{FileName}: each rule needs a \"match\" glob.");
                            var pattern = (string)match;
                            result._rules.Add((pattern, GlobToRegex(pattern), ParseEntry(rule, $"rule '{pattern}'", "match")));
                        }
                        break;
                    case "pieces":
                        if (!(property.Value is JObject pieces)) throw new FormatException($"{FileName}: 'pieces' must be an object keyed by GUID or catalog id.");
                        foreach (var piece in pieces.Properties())
                        {
                            if (!(piece.Value is JObject entry)) throw new FormatException($"{FileName}: piece '{piece.Name}' must be an object.");
                            result._pieces[piece.Name] = ParseEntry(entry, $"piece '{piece.Name}'", null);
                        }
                        break;
                    default:
                        throw new FormatException($"{FileName}: unknown key '{property.Name}' (v, rules, pieces).");
                }
            }
            return result;
        }

        /// <summary>
        /// The merged override for a prefab: matching rules in file order (later ones win per
        /// field), then the piece entry by catalog id, then by GUID. Null when nothing matches.
        /// </summary>
        public LevelOverride Resolve(string guid, string id)
        {
            LevelOverride merged = null;
            foreach (var (_, regex, entry) in _rules)
            {
                if (id == null || !regex.IsMatch(id)) continue;
                (merged ??= new LevelOverride()).Apply(entry);
            }
            if (id != null && _pieces.TryGetValue(id, out var byId)) (merged ??= new LevelOverride()).Apply(byId);
            if (guid != null && _pieces.TryGetValue(guid, out var byGuid)) (merged ??= new LevelOverride()).Apply(byGuid);
            return merged;
        }

        private static LevelOverride ParseEntry(JObject json, string where, string extraKey)
        {
            var entry = new LevelOverride();
            foreach (var property in json.Properties())
            {
                if (property.Name == extraKey) continue;
                if (!EntryKeys.Contains(property.Name))
                    throw new FormatException($"{FileName}: {where} has unknown key '{property.Name}' (role, cover, placementScale, storeys, lift, footprint, exclude, note).");
                try
                {
                    switch (property.Name)
                    {
                        case "role": entry.Role = LevelCards.ParseRole((string)property.Value); break;
                        case "cover": entry.Cover = LevelCards.ParseCover((string)property.Value); break;
                        case "placementScale":
                            var scale = LevelCards.ParseVec(property.Value);
                            if (scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0) throw new FormatException("scale must be positive");
                            entry.PlacementScale = scale;
                            break;
                        case "storeys":
                            var storeys = (int)property.Value;
                            if (storeys < 1) throw new FormatException("storeys must be at least 1");
                            entry.Storeys = storeys;
                            break;
                        case "lift":
                            if (property.Value.Type != JTokenType.Float && property.Value.Type != JTokenType.Integer)
                                throw new FormatException("lift must be a number of meters");
                            var lift = (double)property.Value;
                            if (double.IsNaN(lift) || double.IsInfinity(lift)) throw new FormatException("lift must be finite");
                            entry.Lift = lift;
                            break;
                        case "footprint": entry.Footprint = LevelCards.ParseFootprint(property.Value); break;
                        case "exclude": entry.Exclude = (bool)property.Value; break;
                        case "note": entry.Note = (string)property.Value; break;
                    }
                }
                catch (Exception e) when (e is FormatException || e is ArgumentException || e is InvalidCastException)
                {
                    throw new FormatException($"{FileName}: {where} '{property.Name}': {e.Message}");
                }
            }
            return entry;
        }

        /// <summary>"*" matches within one path segment, "**" across segments, "?" one character.</summary>
        public static Regex GlobToRegex(string glob)
        {
            var sb = new StringBuilder("^");
            for (var i = 0; i < glob.Length; i++)
            {
                var c = glob[i];
                if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                }
                else if (c == '*') sb.Append("[^/]*");
                else if (c == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
        }
    }
}
