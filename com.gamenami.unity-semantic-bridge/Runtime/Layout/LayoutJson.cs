using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Gamenami.UnitySemanticBridge.Layout
{
    public class LayoutFormatException : Exception
    {
        public LayoutFormatException(string message) : base(message) { }
    }

    /// <summary>
    /// Compact layout JSON (version 1):
    /// <code>
    /// {"v":1,"size":[12,12],
    ///  "pieces":[["wallF","5fe5...",3,4,1]],
    ///  "deploy":[[1,1],[2,1]],
    ///  "enemies":[["raider1","42e5...","b038...",10,11,180]],
    ///  "decor":[["road","...",0,5,0]]}
    /// </code>
    /// Each piece is [localId, prefabGuid, x, y, rot]; each enemy is [localId, profileGuid,
    /// visualGuid, x, y, facing] with facing in degrees. Parsing is strict: unknown keys,
    /// wrong arity, malformed GUIDs, duplicate ids (shared by pieces, decor and enemies) and
    /// out-of-range rotations are errors, so agent typos fail loudly instead of applying a
    /// different level. Facing is any number here; the encounter validator checks it.
    /// </summary>
    public static class LayoutJson
    {
        private static readonly Regex GuidPattern = new Regex("^[0-9a-f]{32}$");
        private static readonly HashSet<string> KnownKeys = new HashSet<string> { "v", "size", "pieces", "deploy", "enemies", "decor" };

        public static LayoutDocument Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new LayoutFormatException("Layout JSON is empty.");
            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (JsonReaderException e)
            {
                throw new LayoutFormatException($"Layout is not valid JSON: {e.Message}");
            }

            foreach (var property in root.Properties())
                if (!KnownKeys.Contains(property.Name))
                    throw new LayoutFormatException($"Unknown key '{property.Name}'. Expected v, size, pieces, deploy, enemies, decor.");

            var version = ReadInt(root["v"], "v");
            if (version != LayoutDocument.CurrentVersion)
                throw new LayoutFormatException($"Unsupported layout version {version}; expected {LayoutDocument.CurrentVersion}.");

            var size = ReadPair(root["size"], "size");
            if (size.X <= 0 || size.Y <= 0) throw new LayoutFormatException($"size must be positive (was {size}).");

            var document = new LayoutDocument(size.X, size.Y) { Version = version };
            var ids = new HashSet<string>();
            document.Pieces.AddRange(ReadPieces(root["pieces"], "pieces", ids));
            document.Decor.AddRange(ReadPieces(root["decor"], "decor", ids));
            var deploy = OptionalArray(root["deploy"], "deploy");
            for (var i = 0; i < deploy.Count; i++)
                document.Deploy.Add(ReadPair(deploy[i], $"deploy[{i}]"));
            document.Enemies.AddRange(ReadEnemies(root["enemies"], ids));
            return document;
        }

        /// <summary>One piece per line so diffs stay readable; stable for round trips.</summary>
        public static string Serialize(LayoutDocument document)
        {
            var builder = new StringBuilder();
            builder.Append("{\"v\":").Append(document.Version)
                .Append(",\"size\":[").Append(document.Width).Append(',').Append(document.Height).Append(']');
            AppendPieces(builder, "pieces", document.Pieces);
            builder.Append(",\n \"deploy\":[");
            for (var i = 0; i < document.Deploy.Count; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append('[').Append(document.Deploy[i].X).Append(',').Append(document.Deploy[i].Y).Append(']');
            }
            builder.Append(']');
            builder.Append(",\n \"enemies\":[");
            for (var i = 0; i < document.Enemies.Count; i++)
            {
                var enemy = document.Enemies[i];
                builder.Append(i > 0 ? ",\n  " : "\n  ")
                    .Append('[').Append(JsonConvert.ToString(enemy.LocalId))
                    .Append(',').Append(JsonConvert.ToString(enemy.ProfileGuid))
                    .Append(',').Append(JsonConvert.ToString(enemy.VisualGuid))
                    .Append(',').Append(enemy.Cell.X).Append(',').Append(enemy.Cell.Y)
                    .Append(',').Append(FormatNumber(enemy.Facing)).Append(']');
            }
            builder.Append(']');
            AppendPieces(builder, "decor", document.Decor);
            builder.Append("}\n");
            return builder.ToString();
        }

        private static void AppendPieces(StringBuilder builder, string key, List<LayoutPiece> pieces)
        {
            builder.Append(",\n \"").Append(key).Append("\":[");
            for (var i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i];
                builder.Append(i > 0 ? ",\n  " : "\n  ")
                    .Append('[').Append(JsonConvert.ToString(piece.LocalId))
                    .Append(',').Append(JsonConvert.ToString(piece.PrefabGuid))
                    .Append(',').Append(piece.Anchor.X).Append(',').Append(piece.Anchor.Y)
                    .Append(',').Append(piece.Rotation).Append(']');
            }
            builder.Append(']');
        }

        /// <summary>Integral values without a decimal point, others invariant with up to 3 decimals.</summary>
        public static string FormatNumber(float value) =>
            value == Math.Floor(value) && Math.Abs(value) < 1e7f
                ? ((long)value).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.###", CultureInfo.InvariantCulture);

        private static IEnumerable<LayoutEnemySpawn> ReadEnemies(JToken token, HashSet<string> ids)
        {
            var array = OptionalArray(token, "enemies");
            for (var i = 0; i < array.Count; i++)
            {
                var path = $"enemies[{i}]";
                if (!(array[i] is JArray entry) || entry.Count != 6)
                    throw new LayoutFormatException($"{path} must be [localId, profileGuid, visualGuid, x, y, facing].");
                var localId = ReadString(entry[0], path + " localId");
                if (!ids.Add(localId)) throw new LayoutFormatException($"{path}: duplicate localId '{localId}'.");
                var profile = ReadGuid(entry[1], path + " profileGuid");
                var visual = ReadGuid(entry[2], path + " visualGuid");
                var cell = new Cell(ReadInt(entry[3], path + " x"), ReadInt(entry[4], path + " y"));
                var facingToken = entry[5];
                if (facingToken.Type != JTokenType.Integer && facingToken.Type != JTokenType.Float)
                    throw new LayoutFormatException($"{path} facing must be a number of degrees.");
                yield return new LayoutEnemySpawn(localId, profile, visual, cell, facingToken.Value<float>());
            }
        }

        private static string ReadGuid(JToken token, string path)
        {
            var guid = ReadString(token, path);
            if (!GuidPattern.IsMatch(guid))
                throw new LayoutFormatException($"{path} '{guid}' is not a 32-character lowercase hex asset GUID.");
            return guid;
        }

        private static IEnumerable<LayoutPiece> ReadPieces(JToken token, string key, HashSet<string> ids)
        {
            var array = OptionalArray(token, key);
            for (var i = 0; i < array.Count; i++)
            {
                var path = $"{key}[{i}]";
                if (!(array[i] is JArray entry) || entry.Count != 5)
                    throw new LayoutFormatException($"{path} must be [localId, prefabGuid, x, y, rot].");
                var localId = ReadString(entry[0], path + " localId");
                if (!ids.Add(localId)) throw new LayoutFormatException($"{path}: duplicate localId '{localId}'.");
                var guid = ReadGuid(entry[1], path + " prefabGuid");
                var anchor = new Cell(ReadInt(entry[2], path + " x"), ReadInt(entry[3], path + " y"));
                var rotation = ReadInt(entry[4], path + " rot");
                if (rotation < 0 || rotation > 3)
                    throw new LayoutFormatException($"{path}: rot must be 0-3 quarter turns (was {rotation}).");
                yield return new LayoutPiece(localId, guid, anchor, rotation);
            }
        }

        private static JArray OptionalArray(JToken token, string key)
        {
            if (token == null || token.Type == JTokenType.Null) return new JArray();
            if (token is JArray array) return array;
            throw new LayoutFormatException($"{key} must be an array.");
        }

        private static Cell ReadPair(JToken token, string path)
        {
            if (!(token is JArray pair) || pair.Count != 2)
                throw new LayoutFormatException($"{path} must be [x, y].");
            return new Cell(ReadInt(pair[0], path + " x"), ReadInt(pair[1], path + " y"));
        }

        private static int ReadInt(JToken token, string path)
        {
            if (token == null || token.Type != JTokenType.Integer)
                throw new LayoutFormatException($"{path} must be an integer.");
            return token.Value<int>();
        }

        private static string ReadString(JToken token, string path)
        {
            if (token == null || token.Type != JTokenType.String || string.IsNullOrEmpty(token.Value<string>()))
                throw new LayoutFormatException($"{path} must be a non-empty string.");
            return token.Value<string>();
        }
    }
}
