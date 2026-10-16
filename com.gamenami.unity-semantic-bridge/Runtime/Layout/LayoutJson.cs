using System;
using System.Collections.Generic;
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
    ///  "decor":[["road","...",0,5,0]]}
    /// </code>
    /// Each piece is [localId, prefabGuid, x, y, rot]. Parsing is strict: unknown keys,
    /// wrong arity, duplicate ids and out-of-range rotations are errors, so agent typos
    /// fail loudly instead of compiling a different level.
    /// </summary>
    public static class LayoutJson
    {
        private static readonly Regex GuidPattern = new Regex("^[0-9a-f]{32}$");
        private static readonly HashSet<string> KnownKeys = new HashSet<string> { "v", "size", "pieces", "deploy", "decor" };

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
                    throw new LayoutFormatException($"Unknown key '{property.Name}'. Expected v, size, pieces, deploy, decor.");

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
                var guid = ReadString(entry[1], path + " prefabGuid");
                if (!GuidPattern.IsMatch(guid))
                    throw new LayoutFormatException($"{path}: prefabGuid '{guid}' is not a 32-character lowercase hex asset GUID.");
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
