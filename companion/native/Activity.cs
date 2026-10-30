using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AurifyClient
{
    internal static class Activity
    {
        private const long MinTime = 946684800000L;
        private const long MaxTime = 4102444800000L;
        private static readonly Regex SourceId = new Regex(@"\A[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant);
        private static readonly Regex SessionId = new Regex(@"\A[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant);
        private static readonly Regex PlayerGuid = new Regex(@"\A(?:Player-[0-9]+-[0-9a-f]+|0x0000[0-9a-f]{12})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ZeroGuid = new Regex(@"\A0x0+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static void Invalid(string field)
        {
            throw new PortalException("Invalid activity snapshot: " + field, 400, "invalid_activity_snapshot");
        }

        private static Dictionary<string, object> Table(object value, string field)
        {
            var result = value as Dictionary<string, object>;
            if (result == null) Invalid(field);
            return result;
        }

        private static bool Numeric(object value)
        {
            if (value == null) return false;
            TypeCode code = Type.GetTypeCode(value.GetType());
            return code >= TypeCode.SByte && code <= TypeCode.Decimal;
        }

        private static long Integer(object value, string field, long min, long max)
        {
            double number = Numeric(value) ? Json.Num(value, double.NaN) : double.NaN;
            if (double.IsNaN(number) || double.IsInfinity(number) || number != Math.Floor(number) || number < min || number > max)
                Invalid(field);
            return (long)number;
        }

        private static string Text(object value, string field, int max)
        {
            string text = value as string;
            if (text == null || text.Length == 0 || text.Length > max) Invalid(field);
            // ECMAScript trim includes the BOM but excludes NEL. The category
            // checks below also reject format/control characters throughout.
            if (char.IsWhiteSpace(text, 0) || char.IsWhiteSpace(text, text.Length - 1)) Invalid(field);
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (char.IsHighSurrogate(ch))
                {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) Invalid(field);
                }
                else if (char.IsLowSurrogate(ch)) Invalid(field);
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(text, i);
                if (category == UnicodeCategory.Control || category == UnicodeCategory.Format) Invalid(field);
                if (char.IsHighSurrogate(ch)) i++;
            }
            return text;
        }

        private static Dictionary<string, object> ProjectSource(Dictionary<string, object> value)
        {
            Table(value, "source");
            string id = Json.Get(value, "sourceId") as string;
            if (id == null || !SourceId.IsMatch(id)) Invalid("sourceId");
            if ((Json.Get(value, "client") as string) != "forever" || Integer(Json.Get(value, "interface"), "client/interface", 16001, 16001) != 16001)
                Invalid("client/interface");
            string addonVersion = Json.Get(value, "addonVersion") as string;
            if (addonVersion != "1.0.0" && addonVersion != "1.0.1" && addonVersion != "1.0.2" && addonVersion != "1.0.3" && addonVersion != "1.0.4" && addonVersion != "1.0.5") Invalid("addonVersion");
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sourceId", id.ToLowerInvariant() }, { "client", "forever" },
                { "addonVersion", addonVersion }, { "interface", 16001 },
                { "observedAt", Integer(Json.Get(value, "observedAt"), "observedAt", MinTime, MaxTime) }
            };
        }

        private static Dictionary<string, object> ProjectSession(object value, long observedAt)
        {
            var session = Table(value, "session");
            string id = Text(Json.Get(session, "id"), "session id", 96);
            if (!SessionId.IsMatch(id)) Invalid("session id");
            string kind = Json.Get(session, "kind") as string;
            if (kind != "party" && kind != "raid") Invalid("session kind");
            long startedAt = Integer(Json.Get(session, "startedAt"), "startedAt", MinTime, MaxTime);
            object end = Json.Get(session, "endedAt");
            long? endedAt = end == null ? (long?)null : Integer(end, "endedAt", startedAt, MaxTime);
            if (startedAt > observedAt + 300000L || (endedAt.HasValue && endedAt.Value > observedAt + 300000L))
                Invalid("future session time");
            string guid = Text(Json.Get(session, "playerGuid"), "playerGuid", 96);
            if (!PlayerGuid.IsMatch(guid) || ZeroGuid.IsMatch(guid)) Invalid("playerGuid");
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "id", id }, { "kind", kind },
                { "instanceId", Integer(Json.Get(session, "instanceId"), "instanceId", 0, 2147483647L) },
                { "difficultyId", Integer(Json.Get(session, "difficultyId"), "difficultyId", 0, 2147483647L) },
                { "name", Text(Json.Get(session, "name"), "name", 160) },
                { "startedAt", startedAt }, { "endedAt", endedAt }, { "playerGuid", guid },
                { "realm", Text(Json.Get(session, "realm"), "realm", 96) }
            };
        }

        internal static Dictionary<string, object> Normalize(Dictionary<string, object> saved, Dictionary<string, object> source)
        {
            var projectedSource = ProjectSource(source);
            var globals = Table(saved, "SavedVariables");
            var database = Table(Json.Get(globals, "AurifyDB"), "AurifyDB");
            Integer(Json.Get(database, "version"), "AurifyDB version", 1, 1);
            var sequence = Table(Json.Get(database, "sessions"), "AurifyDB sessions");
            if (sequence.Count > 128) Invalid("sessions size");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sessions = new List<Dictionary<string, object>>(sequence.Count);
            for (int i = 1; i <= sequence.Count; i++)
            {
                object input;
                if (!sequence.TryGetValue(i.ToString(CultureInfo.InvariantCulture), out input)) Invalid("sessions sequence");
                var session = ProjectSession(input, Json.Int(projectedSource["observedAt"]));
                if (!seen.Add((string)session["id"])) Invalid("duplicate session id");
                sessions.Add(session);
            }
            sessions.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b) {
                int result = Json.Int(a["startedAt"]).CompareTo(Json.Int(b["startedAt"]));
                return result != 0 ? result : StringComparer.Ordinal.Compare((string)a["id"], (string)b["id"]);
            });
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "schemaVersion", 1 }, { "kind", "activity" }, { "source", projectedSource }, { "sessions", sessions }
            };
        }
    }
}
