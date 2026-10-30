using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AurifyClient
{
    internal static class Professions
    {
        private const long MaxTime = 4102444800000;
        private const int MaxRecords = 16000, MaxSkills = 10000, MaxTotalSkills = 250000;
        private static readonly Dictionary<long, string> Names = new Dictionary<long, string> {
            {129,"First Aid"}, {164,"Blacksmithing"}, {165,"Leatherworking"}, {171,"Alchemy"},
            {182,"Herbalism"}, {185,"Cooking"}, {186,"Mining"}, {197,"Tailoring"},
            {202,"Engineering"}, {333,"Enchanting"}, {356,"Fishing"}, {393,"Skinning"},
            {755,"Jewelcrafting"}, {773,"Inscription"}
        };
        private static readonly CompareInfo Collation = CultureInfo.GetCultureInfo("en-US").CompareInfo;
        private sealed class Context
        {
            internal Dictionary<string, object> Node;
            internal string Realm, Faction;
            internal HashSet<string> Roster;
        }
        private static FormatException Invalid(string field) { return new FormatException("Invalid profession snapshot: " + field); }
        private static Dictionary<string, object> Table(object value, string field, bool optional = false)
        {
            if (value == null && optional) return Json.New();
            var result = value as Dictionary<string, object>;
            if (result == null) throw Invalid(field);
            return result;
        }
        private static IEnumerable<KeyValuePair<string, object>> Entries(Dictionary<string, object> value, string field, int limit)
        {
            if (value.Count > limit) throw Invalid(field + " size");
            return LuaData.Entries(value);
        }
        private static bool Numeric(object value)
        {
            return value is double || value is float || value is decimal || value is int || value is long || value is uint || value is ulong || value is short || value is ushort || value is byte || value is sbyte;
        }
        private static bool Zero(object value) { return Numeric(value) && Convert.ToDouble(value, CultureInfo.InvariantCulture) == 0; }
        private static long Integer(object value, string field, long min, long max)
        {
            if (!Numeric(value)) throw Invalid(field);
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (Double.IsNaN(number) || Double.IsInfinity(number) || Math.Floor(number) != number || number < min || number > max) throw Invalid(field);
            return (long)number;
        }
        private static bool White(char c)
        {
            // ECMAScript whitespace (not the extra C1 whitespace recognized by .NET Trim).
            return c == '\uFEFF' || c == '\u0009' || c == '\u000A' || c == '\u000B' || c == '\u000C' || c == '\u000D' || c == '\u2028' || c == '\u2029' || Char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
        }
        private static string Text(object value, string field, int max)
        {
            string text = value as string;
            if (String.IsNullOrEmpty(text) || text.Length > max || White(text[0]) || White(text[text.Length - 1])) throw Invalid(field);
            for (int i = 0; i < text.Length; i++)
            {
                if (Char.IsLowSurrogate(text[i]) || Char.IsHighSurrogate(text[i]) && (i + 1 == text.Length || !Char.IsLowSurrogate(text[i + 1]))) throw Invalid(field);
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(text, i);
                if (category == UnicodeCategory.Control || category == UnicodeCategory.Format) throw Invalid(field);
                if (Char.IsHighSurrogate(text[i])) i++;
            }
            return text;
        }
        private static string Realm(object value)
        {
            string result = Text(value, "realm", 96);
            foreach (char c in result) if (White(c)) throw Invalid("realm");
            return result;
        }
        private static Dictionary<string, object> Identity(object value, string nodeRealm, string faction)
        {
            string name = Text(value, "character", 256);
            int dash = name.IndexOf('-');
            string shortName = dash < 0 ? name : name.Substring(0, dash);
            Text(shortName, "character name", 128);
            if (String.Equals(shortName, "unknown", StringComparison.OrdinalIgnoreCase) || String.Equals(shortName, "unknown object", StringComparison.OrdinalIgnoreCase)) return null;
            string realm = dash < 0 ? nodeRealm : Realm(name.Substring(dash + 1));
            var result = Json.New();
            result["realm"] = realm; result["faction"] = faction; result["character"] = shortName + "-" + realm;
            return result;
        }
        private static string IdentityKey(Dictionary<string, object> value) { return (string)value["faction"] + "\0" + (string)value["character"]; }
        private static string ScopeKey(Dictionary<string, object> value)
        {
            object id = Json.Get(value, "professionId");
            return IdentityKey(value) + "\0" + (id == null ? "all" : Convert.ToString(id, CultureInfo.InvariantCulture));
        }
        private static long? RawId(string key)
        {
            long id;
            if (!Regex.IsMatch(key, "^[1-9][0-9]*$") || !Int64.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out id) || !Names.ContainsKey(id)) return null;
            return id;
        }
        private static long Id(object value)
        {
            long id = Integer(value, "professionId", 1, Int32.MaxValue);
            if (!Names.ContainsKey(id)) throw Invalid("professionId");
            return id;
        }
        private static long? GameTime(object value, string field)
        {
            if (value == null || Zero(value)) return null;
            return Integer(value, field, 1, MaxTime / 1000) * 1000;
        }
        private static long? Newest(long? a, long? b) { return !a.HasValue ? b : !b.HasValue ? a : Math.Max(a.Value, b.Value); }
        private static long? Time(Dictionary<string, object> value, string key)
        { object result = Json.Get(value, key); return result == null ? (long?)null : Convert.ToInt64(result, CultureInfo.InvariantCulture); }
        private static long? ProfessionTime(object value)
        {
            if (value == null || Numeric(value)) return GameTime(value, "skillTimes");
            long? result = null;
            foreach (var pair in Entries(Table(value, "skillTimes"), "skillTimes", MaxSkills)) result = Newest(result, GameTime(pair.Value, "skillTimes"));
            return result;
        }
        private static Dictionary<string, object> Sequence(object value, string field, int limit)
        {
            var result = Table(value, field);
            if (result.Count > limit) throw Invalid(field + " size");
            for (int i = 1; i <= result.Count; i++) if (!result.ContainsKey(i.ToString(CultureInfo.InvariantCulture))) throw Invalid(field + " sequence");
            return result;
        }
        private static object CharacterValue(Dictionary<string, object> values, string rawName, Dictionary<string, object> character, string nodeRealm)
        {
            object result;
            if (values.TryGetValue(rawName, out result) || values.TryGetValue((string)character["character"], out result)) return result;
            if ((string)character["realm"] == nodeRealm)
            {
                string canonical = (string)character["character"];
                if (values.TryGetValue(canonical.Substring(0, canonical.Length - nodeRealm.Length - 1), out result)) return result;
            }
            return null;
        }
        private static void AddReset(Dictionary<string, Dictionary<string, object>> resets, Dictionary<string, object> character, long? id, long? at)
        {
            if (!at.HasValue) return;
            var reset = new Dictionary<string, object>(character, StringComparer.Ordinal);
            reset["professionId"] = id; reset["at"] = at;
            string key = ScopeKey(reset);
            Dictionary<string, object> previous;
            if (!resets.TryGetValue(key, out previous) && resets.Count >= MaxRecords) throw Invalid("resets size");
            if (previous == null || Time(previous, "at").Value < at.Value) resets[key] = reset;
        }
        private static bool Blocked(Dictionary<string, object> record, Dictionary<string, Dictionary<string, object>> resets)
        {
            Dictionary<string, object> reset;
            long at = resets.TryGetValue(IdentityKey(record) + "\0all", out reset) ? Time(reset, "at").Value : 0;
            if (resets.TryGetValue(ScopeKey(record), out reset)) at = Math.Max(at, Time(reset, "at").Value);
            long? updated = Time(record, "updatedAt");
            return at > 0 && (!updated.HasValue || updated.Value <= at);
        }
        private static Dictionary<string, object> Metadata(Dictionary<string, object> cache, long id, long? ownItemId)
        {
            var cached = Table(Json.Get(cache, id.ToString(CultureInfo.InvariantCulture)), "skill cache", true);
            string name = "Recipe #" + id.ToString(CultureInfo.InvariantCulture);
            object rawName = Json.Get(cached, "name");
            if (rawName != null && !(rawName is string && (string)rawName == "")) name = Text(rawName, "skill name", 240);
            long? itemId = ownItemId;
            object cachedId = Json.Get(cached, "itemId");
            if (!itemId.HasValue && cachedId != null && !Zero(cachedId)) itemId = Integer(cachedId, "itemId", 1, Int32.MaxValue);
            string link = Json.Get(cached, "itemLink") as string;
            if (!itemId.HasValue && link != null && link.Length <= 2048)
            {
                Match match = Regex.Match(link, "\\|Hitem:([1-9][0-9]*):");
                if (match.Success)
                {
                    double number;
                    if (!Double.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number)) throw Invalid("itemId");
                    itemId = Integer(number, "itemId", 1, Int32.MaxValue);
                }
            }
            var result = Json.New(); result["id"] = id; result["name"] = name; result["itemId"] = itemId;
            return result;
        }
        private static int Compare(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            int result = Collation.Compare((string)a["character"], (string)b["character"], CompareOptions.None);
            if (result == 0) result = Collation.Compare((string)a["faction"], (string)b["faction"], CompareOptions.None);
            if (result == 0) result = Convert.ToInt64(a["professionId"], CultureInfo.InvariantCulture).CompareTo(Convert.ToInt64(b["professionId"], CultureInfo.InvariantCulture));
            return result;
        }
        private static Dictionary<string, object> Source(Dictionary<string, object> source)
        {
            if (source == null) throw Invalid("source");
            string id = Json.Get(source, "sourceId") as string;
            if (id == null || !Regex.IsMatch(id, "\\A[a-fA-F0-9]{64}\\z")) throw Invalid("sourceId");
            if (!Object.Equals(Json.Get(source, "client"), "forever") || Integer(Json.Get(source, "interface"), "client/interface", 16001, 16001) != 16001) throw Invalid("client/interface");
            string version = Text(Json.Get(source, "addonVersion"), "addonVersion", 32);
            if (!AddonVersions.IsSupported("professions", version)) throw Invalid("addonVersion");
            var result = Json.New(); result["sourceId"] = id.ToLowerInvariant(); result["client"] = "forever"; result["addonVersion"] = version; result["interface"] = 16001;
            result["observedAt"] = Integer(Json.Get(source, "observedAt"), "observedAt", 946684800000, MaxTime);
            return result;
        }

        public static Dictionary<string, object> Normalize(Dictionary<string, object> saved, Dictionary<string, object> source, string guildName)
        {
            var projectedSource = Source(source);
            string guild = Text(guildName, "guildName", 128);
            var globals = Table(saved, "SavedVariables");
            var settings = Table(Json.Get(globals, "PM_Settings"), "PM_Settings");
            Integer(Json.Get(settings, "storeVersion"), "storeVersion", 9, 16);
            var data = Table(Json.Get(globals, "PM_Data"), "PM_Data");
            var cache = Table(Json.Get(globals, "PM_Skills"), "PM_Skills", true);
            var contexts = new List<Context>();
            var resets = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            foreach (var pair in Entries(data, "PM_Data", 64))
            {
                int dash = pair.Key.Length - 2;
                if (dash < 0 || pair.Key[dash] != '-' || (pair.Key[dash + 1] != 'A' && pair.Key[dash + 1] != 'H')) throw Invalid("realm-faction node");
                string realm = Realm(pair.Key.Substring(0, dash)), faction = pair.Key.Substring(dash + 1);
                var node = Table(pair.Value, "realm-faction node");
                var guilds = Table(Json.Get(node, "guilds"), "guilds", true);
                if (!guilds.ContainsKey(guild)) continue;
                var membership = Table(guilds[guild], "guild");
                var roster = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in Entries(Table(Json.Get(membership, "members"), "members", true), "members", 1000))
                {
                    if (!(member.Value is bool) || !(bool)member.Value) throw Invalid("guild membership");
                    var character = Identity(member.Key, realm, faction);
                    if (character != null) roster.Add((string)character["character"]);
                }
                if (roster.Count == 0) continue;
                contexts.Add(new Context { Node = node, Realm = realm, Faction = faction, Roster = roster });
                foreach (var entry in Entries(Table(Json.Get(node, "dataResets"), "dataResets", true), "dataResets", MaxRecords))
                {
                    var character = Identity(entry.Key, realm, faction);
                    if (character == null || !roster.Contains((string)character["character"])) continue;
                    if (Numeric(entry.Value)) { AddReset(resets, character, null, GameTime(entry.Value, "reset time")); continue; }
                    var reset = Table(entry.Value, "reset");
                    AddReset(resets, character, null, GameTime(Json.Get(reset, "all"), "reset time"));
                    foreach (var scope in Entries(Table(Json.Get(reset, "professions"), "reset professions", true), "reset professions", 64))
                    {
                        long? id = RawId(scope.Key);
                        if (id.HasValue) AddReset(resets, character, id, GameTime(scope.Value, "reset time"));
                    }
                }
                if (Json.Get(node, "sentDataResets") != null)
                {
                    var sequence = Sequence(node["sentDataResets"], "sentDataResets", MaxRecords);
                    for (int i = 1; i <= sequence.Count; i++)
                    {
                        var reset = Table(sequence[i.ToString(CultureInfo.InvariantCulture)], "sent reset");
                        var character = Identity(Json.Get(reset, "name"), realm, faction);
                        if (character == null || !roster.Contains((string)character["character"])) continue;
                        object rawId = Json.Get(reset, "professionId");
                        long? id = Zero(rawId) ? (long?)null : Id(rawId);
                        AddReset(resets, character, id, GameTime(Json.Get(reset, "time"), "reset time"));
                    }
                }
            }
            var records = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            int totalSkills = 0;
            foreach (var context in contexts)
            {
                var times = Table(Json.Get(context.Node, "skillTimes"), "skillTimes", true);
                var levels = Table(Json.Get(context.Node, "ownLevels"), "ownLevels", true);
                foreach (string kind in new[] { "guildmates", "own" })
                {
                    foreach (var pair in Entries(Table(Json.Get(context.Node, kind), kind, true), kind, MaxRecords))
                    {
                        var character = Identity(pair.Key, context.Realm, context.Faction);
                        if (character == null || !context.Roster.Contains((string)character["character"])) continue;
                        var professions = Table(pair.Value, "character professions");
                        var characterTimes = Table(CharacterValue(times, pair.Key, character, context.Realm), "character skillTimes", true);
                        var characterLevels = kind == "own" ? Table(CharacterValue(levels, pair.Key, character, context.Realm), "character levels", true) : Json.New();
                        foreach (var entry in Entries(professions, "character professions", 64))
                        {
                            long? rawProfession = RawId(entry.Key);
                            if (!rawProfession.HasValue) continue;
                            long id = rawProfession.Value;
                            var sequence = Sequence(entry.Value, "skills", MaxSkills);
                            totalSkills += sequence.Count;
                            if (totalSkills > MaxTotalSkills) throw Invalid("total skills size");
                            var skills = new Dictionary<long, Dictionary<string, object>>();
                            long? addedAt = null;
                            for (int i = 1; i <= sequence.Count; i++)
                            {
                                object rawSkill = sequence[i.ToString(CultureInfo.InvariantCulture)];
                                var skill = kind == "own" ? Table(rawSkill, "own skill") : null;
                                long skillId = Integer(skill == null ? rawSkill : Json.Get(skill, "skillId"), "skillId", 1, Int32.MaxValue);
                                if (skillId >= 9000000) continue;
                                long? itemId = null;
                                if (skill != null)
                                {
                                    object rawItemId = Json.Get(skill, "itemId");
                                    if (rawItemId != null && !Zero(rawItemId)) itemId = Integer(rawItemId, "itemId", 1, Int32.MaxValue);
                                    addedAt = Newest(addedAt, GameTime(Json.Get(skill, "added"), "skill added"));
                                }
                                skills[skillId] = Metadata(cache, skillId, itemId);
                            }
                            object rawLevel = Json.Get(characterLevels, entry.Key);
                            var record = new Dictionary<string, object>(character, StringComparer.Ordinal);
                            record["guild"] = guild; record["professionId"] = id; record["professionName"] = Names[id];
                            record["level"] = rawLevel == null ? (object)null : Integer(rawLevel, "level", 0, 1000);
                            record["skills"] = new List<Dictionary<string, object>>(skills.Values);
                            record["updatedAt"] = Newest(ProfessionTime(Json.Get(characterTimes, entry.Key)), addedAt);
                            if (Blocked(record, resets)) continue;
                            string key = ScopeKey(record);
                            Dictionary<string, object> previous;
                            if (!records.TryGetValue(key, out previous))
                            {
                                if (records.Count >= MaxRecords) throw Invalid("records size");
                                records[key] = record;
                            }
                            else
                            {
                                var merged = new Dictionary<long, Dictionary<string, object>>();
                                foreach (var skill in (List<Dictionary<string, object>>)previous["skills"]) merged[(long)skill["id"]] = skill;
                                foreach (var skill in skills.Values)
                                {
                                    Dictionary<string, object> old;
                                    if (skill["itemId"] == null && merged.TryGetValue((long)skill["id"], out old)) skill["itemId"] = old["itemId"];
                                    merged[(long)skill["id"]] = skill;
                                }
                                if (merged.Count > MaxSkills) throw Invalid("skills size");
                                previous["skills"] = new List<Dictionary<string, object>>(merged.Values);
                                if (record["level"] != null && (previous["level"] == null || (Time(record, "updatedAt") ?? 0) >= (Time(previous, "updatedAt") ?? 0))) previous["level"] = record["level"];
                                previous["updatedAt"] = Newest(Time(previous, "updatedAt"), Time(record, "updatedAt"));
                            }
                        }
                    }
                }
            }
            var orderedRecords = new List<Dictionary<string, object>>(records.Values);
            foreach (var record in orderedRecords) ((List<Dictionary<string, object>>)record["skills"]).Sort((a, b) => ((long)a["id"]).CompareTo((long)b["id"]));
            var orderedResets = new List<Dictionary<string, object>>(resets.Values);
            orderedRecords.Sort(Compare); orderedResets.Sort(Compare);
            var payload = Json.New(); payload["schemaVersion"] = 1; payload["kind"] = "professions"; payload["source"] = projectedSource; payload["records"] = orderedRecords; payload["resets"] = orderedResets;
            return payload;
        }
    }
}
