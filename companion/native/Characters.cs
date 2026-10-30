using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AurifyClient
{
    internal static class Characters
    {
        private const long MinTime = 946684800000L;
        private const long MaxTime = 4102444800000L;
        private static readonly Regex SourceId = new Regex(@"\A[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant);
        private static readonly Regex PlayerGuid = new Regex(@"\A(?:Player-[0-9]+-[0-9a-f]+|0x0000[0-9a-f]{12})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ZeroGuid = new Regex(@"\A(?:0x0+|Player-[0-9]+-0+)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ItemString = new Regex(@"\Aitem:([0-9]+)(?::[-0-9]*)*\z", RegexOptions.CultureInvariant);
        private static readonly Regex IconBuild = new Regex(@"\A1\.60\.(0|[1-9][0-9]{0,2})\.[1-9][0-9]{3,5}\z", RegexOptions.CultureInvariant);
        private static readonly Regex GuildClubIdPattern = new Regex(@"\A[1-9][0-9]{0,19}\z", RegexOptions.CultureInvariant);

        private static void Invalid(string field)
        {
            throw new PortalException("Invalid character snapshot: " + field, 400, "invalid_character_snapshot");
        }

        private static Dictionary<string, object> Table(object value, string field)
        {
            var result = value as Dictionary<string, object>;
            if (result == null) Invalid(field);
            return result;
        }

        private static double Number(object value, string field, double min, double max)
        {
            TypeCode code = value == null ? TypeCode.Empty : Type.GetTypeCode(value.GetType());
            double number = code >= TypeCode.SByte && code <= TypeCode.Decimal ? Json.Num(value, double.NaN) : double.NaN;
            if (double.IsNaN(number) || double.IsInfinity(number) || number < min || number > max) Invalid(field);
            return number;
        }

        private static long Integer(object value, string field, long min, long max)
        {
            double number = Number(value, field, min, max);
            if (number != Math.Floor(number)) Invalid(field);
            return (long)number;
        }

        private static string Text(object value, string field, int max)
        {
            string text = value as string;
            if (text == null || text.Length == 0 || text.Length > max) Invalid(field);
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

        private static string OptionalText(object value, string field, int max)
        {
            return value == null ? null : Text(value, field, max);
        }

        private static string GuildClubId(object value)
        {
            string id = Text(value, "guildClubId", 20);
            ulong parsed;
            if (!GuildClubIdPattern.IsMatch(id) || !UInt64.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out parsed))
                Invalid("guildClubId");
            return id;
        }

        private static string Build(object value)
        {
            string build = Text(value, "build", 15);
            if (!IconBuild.IsMatch(build)) Invalid("build");
            return build;
        }

        private static Dictionary<string, object> Client(object value)
        {
            var row = Table(value, "gameClient");
            string product = Json.Get(row, "product") as string, key = Json.Get(row, "buildKey") as string;
            if ((product != "wowf" && product != "wow_classic_beta") || key == null || !Regex.IsMatch(key, @"\A[0-9a-f]{32}\z")) Invalid("gameClient");
            return new Dictionary<string, object> { { "build", Build(Json.Get(row, "build")) }, { "product", product }, { "buildKey", key } };
        }

        private static Dictionary<string, object> Icon(object value)
        {
            var row = Table(value, "character icon");
            var result = new Dictionary<string, object> {
                { "build", Build(Json.Get(row, "build")) },
                { "fileDataId", Integer(Json.Get(row, "fileDataId"), "fileDataId", 1, 2147483647L) }
            };
            foreach (string field in new[] { "left", "right", "top", "bottom" })
                result[field] = Number(Json.Get(row, field), "icon bounds", 0, 1);
            if ((double)result["left"] >= (double)result["right"] || (double)result["top"] >= (double)result["bottom"]) Invalid("icon bounds");
            return result;
        }

        private static string TooltipText(object value)
        {
            if ((value as string) == "") return "";
            string text = Text(value, "tooltip text", 512);
            if (Regex.IsMatch(text, @"[|<>]|://|www\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) Invalid("tooltip text");
            return text;
        }

        private static Dictionary<string, object> Tooltip(object value)
        {
            var sequence = Table(Json.Get(Table(value, "tooltip"), "lines"), "tooltip lines");
            if (sequence.Count < 1 || sequence.Count > 64) Invalid("tooltip lines");
            var lines = new List<Dictionary<string, object>>(sequence.Count);
            int total = 0;
            for (int i = 1; i <= sequence.Count; i++)
            {
                var row = Table(Json.Get(sequence, i.ToString(CultureInfo.InvariantCulture)), "tooltip line");
                string left = TooltipText(Json.Get(row, "leftText"));
                string right = row.ContainsKey("rightText") ? TooltipText(row["rightText"]) : null;
                if (left.Length == 0 && String.IsNullOrEmpty(right)) Invalid("tooltip line");
                total += left.Length + (right == null ? 0 : right.Length);
                if (total > 8192) Invalid("tooltip size");
                var line = new Dictionary<string, object> { { "leftText", left } };
                if (right != null) line["rightText"] = right;
                foreach (string field in new[] { "leftColor", "rightColor" })
                {
                    if (!row.ContainsKey(field)) continue;
                    string color = row[field] as string;
                    if (color == null || !Regex.IsMatch(color, @"\A[0-9a-f]{6}\z")) Invalid("tooltip color");
                    line[field] = color;
                }
                lines.Add(line);
            }
            return new Dictionary<string, object> { { "lines", lines } };
        }

        private static Dictionary<string, object> Appearance(object value, Dictionary<string, object> character)
        {
            var row = Table(value, "appearance");
            long capturedAt = Integer(Json.Get(row, "capturedAt"), "appearance capturedAt", MinTime, MaxTime);
            long raceId = Integer(Json.Get(row, "raceId"), "appearance raceId", 1, 2147483647L);
            long bodyType = Integer(Json.Get(row, "bodyType"), "appearance bodyType", 0, 1);
            if (!character.ContainsKey("raceId") || !character.ContainsKey("bodyType") ||
                raceId != Json.Int(character["raceId"]) || bodyType != Json.Int(character["bodyType"]) ||
                capturedAt > Json.Int(character["capturedAt"]) + 300000L) Invalid("appearance identity/time");
            var sequence = Table(Json.Get(row, "choices"), "appearance choices");
            if (sequence.Count < 1 || sequence.Count > 128) Invalid("appearance choices");
            var choices = new List<Dictionary<string, object>>(sequence.Count);
            var seen = new HashSet<long>();
            for (int i = 1; i <= sequence.Count; i++)
            {
                var choice = Table(Json.Get(sequence, i.ToString(CultureInfo.InvariantCulture)), "appearance choice");
                long option = Integer(Json.Get(choice, "optionId"), "optionId", 1, 2147483647L);
                if (!seen.Add(option)) Invalid("duplicate appearance option");
                choices.Add(new Dictionary<string, object> { { "optionId", option },
                    { "choiceId", Integer(Json.Get(choice, "choiceId"), "choiceId", 1, 2147483647L) } });
            }
            choices.Sort((a, b) => Json.Int(a["optionId"]).CompareTo(Json.Int(b["optionId"])));
            var result = new Dictionary<string, object> { { "build", Build(Json.Get(row, "build")) },
                { "capturedAt", capturedAt }, { "raceId", raceId }, { "bodyType", bodyType }, { "choices", choices } };
            if (row.ContainsKey("modelId")) result["modelId"] = Integer(row["modelId"], "appearance modelId", 1, 2147483647L);
            return result;
        }

        private static Dictionary<string, object> ProjectSource(Dictionary<string, object> value)
        {
            Table(value, "source");
            string id = Json.Get(value, "sourceId") as string;
            if (id == null || !SourceId.IsMatch(id)) Invalid("sourceId");
            if ((Json.Get(value, "client") as string) != "forever") Invalid("client/interface");
            Integer(Json.Get(value, "interface"), "client/interface", 16001, 16001);
            string addonVersion = Json.Get(value, "addonVersion") as string;
            if (addonVersion != "1.0.0" && addonVersion != "1.0.1" && addonVersion != "1.0.2" && addonVersion != "1.0.3" && addonVersion != "1.0.4" && addonVersion != "1.0.5") Invalid("addonVersion");
            var result = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sourceId", id.ToLowerInvariant() }, { "client", "forever" },
                { "addonVersion", addonVersion }, { "interface", 16001 },
                { "observedAt", Integer(Json.Get(value, "observedAt"), "observedAt", MinTime, MaxTime) }
            };
            if (value.ContainsKey("gameClient")) result["gameClient"] = Client(value["gameClient"]);
            return result;
        }

        private static List<Dictionary<string, object>> Equipment(object value)
        {
            var sequence = Table(value, "equipment");
            if (sequence.Count > 19) Invalid("equipment size");
            var equipment = new List<Dictionary<string, object>>(sequence.Count);
            var seen = new HashSet<long>();
            for (int i = 1; i <= sequence.Count; i++)
            {
                object input;
                if (!sequence.TryGetValue(i.ToString(CultureInfo.InvariantCulture), out input)) Invalid("equipment sequence");
                var row = Table(input, "equipment row");
                long slot = Integer(Json.Get(row, "slot"), "slot", 1, 19);
                if (!seen.Add(slot)) Invalid("duplicate equipment slot");
                long itemId = Integer(Json.Get(row, "itemId"), "itemId", 0, 2147483647L);
                string itemString = OptionalText(Json.Get(row, "itemString"), "itemString", 2048);
                string name = OptionalText(Json.Get(row, "name"), "item name", 240);
                object level = Json.Get(row, "itemLevel"), quality = Json.Get(row, "quality");
                double? itemLevel = level == null ? (double?)null : Number(level, "itemLevel", 0, 100000);
                long? itemQuality = quality == null ? (long?)null : Integer(quality, "quality", 0, 7);
                bool hasIconId = row.ContainsKey("iconFileDataId"), hasIconBuild = row.ContainsKey("iconBuild");
                if (hasIconId != hasIconBuild) Invalid("icon metadata pair");
                long? iconFileDataId = hasIconId ? (long?)Integer(Json.Get(row, "iconFileDataId"), "iconFileDataId", 1, 2147483647L) : null;
                string iconBuild = hasIconBuild ? Text(Json.Get(row, "iconBuild"), "iconBuild", 15) : null;
                if (iconBuild != null && !IconBuild.IsMatch(iconBuild)) Invalid("iconBuild");
                if (itemId == 0)
                {
                    if (itemString != null || name != null || itemLevel.HasValue || itemQuality.HasValue || hasIconId || row.ContainsKey("tooltip")) Invalid("empty equipment slot");
                }
                else
                {
                    if (itemString == null) Invalid("occupied equipment slot");
                    Match match = ItemString.Match(itemString);
                    double parsedId;
                    if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out parsedId) || parsedId != itemId)
                        Invalid("itemString/itemId");
                }
                var projected = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "slot", slot }, { "itemId", itemId }, { "itemString", itemString },
                    { "name", name }, { "itemLevel", itemLevel }, { "quality", itemQuality }
                };
                if (hasIconId)
                {
                    projected["iconFileDataId"] = iconFileDataId.Value;
                    projected["iconBuild"] = iconBuild;
                }
                if (row.ContainsKey("tooltip")) projected["tooltip"] = Tooltip(row["tooltip"]);
                equipment.Add(projected);
            }
            equipment.Sort((a, b) => Json.Int(a["slot"]).CompareTo(Json.Int(b["slot"])));
            return equipment;
        }

        private static Dictionary<string, object> ProjectCharacter(object value, long observedAt)
        {
            var character = Table(value, "character");
            string guid = Text(Json.Get(character, "guid"), "guid", 96);
            if (!PlayerGuid.IsMatch(guid) || ZeroGuid.IsMatch(guid)) Invalid("guid");
            long capturedAt = Integer(Json.Get(character, "capturedAt"), "capturedAt", MinTime, MaxTime);
            if (capturedAt > observedAt + 300000L) Invalid("future capture time");
            object level = Json.Get(character, "level");
            var projected = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "guid", guid }, { "name", Text(Json.Get(character, "name"), "name", 80) },
                { "realm", Text(Json.Get(character, "realm"), "realm", 96) },
                { "class", OptionalText(Json.Get(character, "class"), "class", 60) },
                { "level", level == null ? (long?)null : Integer(level, "level", 1, 1000) },
                { "capturedAt", capturedAt }, { "equipment", Equipment(Json.Get(character, "equipment")) }
            };
            ProjectFact(character, projected, "spec");
            ProjectFact(character, projected, "guildRank");
            object guildClubId;
            if (character.TryGetValue("guildClubId", out guildClubId))
                projected["guildClubId"] = guildClubId == null || (guildClubId as string) == "" ? null : GuildClubId(guildClubId);
            object realmMode;
            if (character.TryGetValue("realmMode", out realmMode))
            {
                if (!(realmMode is string) || ((string)realmMode != "pve" && (string)realmMode != "pvp")) Invalid("realmMode");
                projected["realmMode"] = realmMode;
            }
            foreach (string field in new[] { "raceId", "classId" })
                if (character.ContainsKey(field)) projected[field] = Integer(character[field], field, 1, 2147483647L);
            if (character.ContainsKey("race")) projected["race"] = Text(character["race"], "race", 80);
            if (character.ContainsKey("bodyType")) projected["bodyType"] = Integer(character["bodyType"], "bodyType", 0, 1);
            foreach (string field in new[] { "raceIcon", "classIcon" })
                if (character.ContainsKey(field)) projected[field] = Icon(character[field]);
            if (character.ContainsKey("appearance")) projected["appearance"] = Appearance(character["appearance"], projected);
            return projected;
        }

        private static void ProjectFact(Dictionary<string, object> character, Dictionary<string, object> projected, string field)
        {
            object value;
            if (character.TryGetValue(field, out value))
                projected[field] = value == null || (value as string) == "" ? null : Text(value, field, 80);
        }

        internal static Dictionary<string, object> Normalize(Dictionary<string, object> saved, Dictionary<string, object> source)
        {
            var globals = Table(saved, "SavedVariables");
            var database = Table(Json.Get(globals, "AurifyDB"), "AurifyDB");
            Integer(Json.Get(database, "version"), "AurifyDB version", 1, 1);
            if (!database.ContainsKey("characterSchemaVersion")) return null;
            Integer(Json.Get(database, "characterSchemaVersion"), "characterSchemaVersion", 1, 1);
            var projectedSource = ProjectSource(source);
            var sequence = Table(Json.Get(database, "characters"), "AurifyDB characters");
            if (sequence.Count > 50) Invalid("characters size");
            var ordered = new SortedDictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            for (int i = 1; i <= sequence.Count; i++)
            {
                object input;
                if (!sequence.TryGetValue(i.ToString(CultureInfo.InvariantCulture), out input)) Invalid("characters sequence");
                var character = ProjectCharacter(input, Json.Int(projectedSource["observedAt"]));
                string key = ((string)character["guid"]).ToLowerInvariant() + "\n" + (string)character["realm"];
                if (ordered.ContainsKey(key)) Invalid("duplicate character identity");
                ordered.Add(key, character);
            }
            var characters = new List<Dictionary<string, object>>(ordered.Values);
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "schemaVersion", 1 }, { "kind", "characters" }, { "source", projectedSource }, { "characters", characters }
            };
        }
    }
}
