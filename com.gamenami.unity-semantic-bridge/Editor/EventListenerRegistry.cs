using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Gamenami.UnitySemanticBridge.Editor
{
    /// <summary>
    /// Extra event endpoints registered by MCP servers that could not bind this project's
    /// event port (several agent sessions on one Editor). Unity sends every event to the
    /// configured event port plus each live registration. Registrations expire unless
    /// renewed, so a server that exits drops out on its own. Thread-safe: events are sent
    /// from any thread.
    /// </summary>
    public static class EventListenerRegistry
    {
        public const int MaxListeners = 16;
        public const double MinTtlSeconds = 10;
        public const double MaxTtlSeconds = 600;

        private static readonly object Lock = new object();
        private static readonly Dictionary<string, DateTime> Expiry = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>Returns the normalized URL, or null with an error for anything but a loopback /rpc endpoint.</summary>
        public static string Normalize(string url, out string error)
        {
            error = null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
                error = "url must be an absolute http URL.";
            else if (uri.Host != "127.0.0.1" && uri.Host != "localhost")
                error = "url must point at 127.0.0.1 or localhost.";
            else if (uri.AbsolutePath != "/rpc")
                error = "url path must be /rpc.";
            else if (uri.IsDefaultPort || uri.Port < 1024)
                error = "url must name a port of 1024 or above.";
            return error == null ? $"http://127.0.0.1:{uri.Port}/rpc" : null;
        }

        /// <summary>Adds or renews a listener. Returns null on success, otherwise an error message.</summary>
        public static string Register(string url, double ttlSeconds, DateTime nowUtc)
        {
            var normalized = Normalize(url, out var error);
            if (normalized == null) return error;
            var ttl = Math.Min(Math.Max(ttlSeconds, MinTtlSeconds), MaxTtlSeconds);
            lock (Lock)
            {
                PruneExpired(nowUtc);
                if (!Expiry.ContainsKey(normalized) && Expiry.Count >= MaxListeners)
                    return $"Too many event listeners ({MaxListeners}); retry after unused ones expire.";
                Expiry[normalized] = nowUtc.AddSeconds(ttl);
            }
            return null;
        }

        public static void Remove(string url)
        {
            lock (Lock) Expiry.Remove(url);
        }

        public static List<string> Live(DateTime nowUtc)
        {
            lock (Lock)
            {
                PruneExpired(nowUtc);
                return new List<string>(Expiry.Keys);
            }
        }

        public static void Clear()
        {
            lock (Lock) Expiry.Clear();
        }

        // One "url ticks" pair per line, for SessionState across domain reloads.
        public static string Serialize()
        {
            var sb = new StringBuilder();
            lock (Lock)
                foreach (var pair in Expiry)
                    sb.Append(pair.Key).Append(' ').Append(pair.Value.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        public static void Load(string serialized, DateTime nowUtc)
        {
            lock (Lock)
            {
                Expiry.Clear();
                if (string.IsNullOrEmpty(serialized)) return;
                foreach (var line in serialized.Split('\n'))
                {
                    var parts = line.Split(' ');
                    if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)) continue;
                    if (Normalize(parts[0], out _) != parts[0] || Expiry.Count >= MaxListeners) continue;
                    var expires = new DateTime(ticks, DateTimeKind.Utc);
                    if (expires > nowUtc) Expiry[parts[0]] = expires;
                }
            }
        }

        private static void PruneExpired(DateTime nowUtc)
        {
            List<string> expired = null;
            foreach (var pair in Expiry)
                if (pair.Value <= nowUtc) (expired ??= new List<string>()).Add(pair.Key);
            if (expired != null)
                foreach (var url in expired) Expiry.Remove(url);
        }
    }
}
