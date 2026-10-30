using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AurifyClient
{
    internal static class Overlord
    {
        private const long MinTime = 1072915200000L;
        private const long MaxTime = 4102444800000L;
        private const long ClockSkew = 300000L;
        private const long Week = 604800000L;
        private const long EpochTolerance = 129600000L;
        private const long MaxScore = 2147483647L;
        private static readonly string[] Pools = { "global", "pve", "rpg", "hc" };
        private static readonly string[] BucketKeys = { "global", "pve", "rpg", "hc", "na", "us", "eu", "fr", "de" };
        private static readonly HashSet<string> Classes = new HashSet<string>(new[] {
            "", "UNKNOWN", "WARRIOR", "PALADIN", "HUNTER", "ROGUE", "PRIEST", "SHAMAN", "MAGE",
            "WARLOCK", "DRUID", "DEATHKNIGHT", "MONK", "DEMONHUNTER", "EVOKER"
        }, StringComparer.Ordinal);
        private static readonly HashSet<string> ZoneStatuses = new HashSet<string>(new[] { "locked", "available", "in_progress", "captured" }, StringComparer.Ordinal);
        private static readonly HashSet<string> SiteStatuses = new HashSet<string>(new[] { "neutral", "in_progress", "held" }, StringComparer.Ordinal);
        private static readonly HashSet<string> UnknownNames = new HashSet<string>(new[] { "unknown", "inconnu", "unbekannt", "desconocido", "desconhecido" }, StringComparer.Ordinal);
        private static readonly Regex SourceId = new Regex(@"\A[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant);
        private static readonly Regex Spaces = new Regex(" +", RegexOptions.CultureInvariant);
        private static readonly CompareInfo English = CultureInfo.GetCultureInfo("en-US").CompareInfo;
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private sealed class PlayerIdentity
        {
            internal string Name;
            internal string Key;
        }

        private sealed class Bucket
        {
            internal Dictionary<string, object> Value;
            internal long Start;
        }

        private sealed class Preview
        {
            internal bool Snapshot;
            internal Dictionary<string, object> Value;
            internal Dictionary<string, object> Campaign;
            internal Dictionary<string, object> Bucket;
            internal long? At;
        }

        private static void Invalid()
        {
            throw new PortalException("Invalid Overlord snapshot", 400, "INVALID_OVERLORD_SNAPSHOT");
        }

        private static bool IsRecord(object value) { return value is Dictionary<string, object>; }
        private static object Get(Dictionary<string, object> value, string key) { return Json.Get(value, key); }
        private static Dictionary<string, object> Record(object value) { return Json.Obj(value); }

        private static bool Numeric(object value)
        {
            if (value == null) return false;
            TypeCode code = Type.GetTypeCode(value.GetType());
            return code >= TypeCode.SByte && code <= TypeCode.Decimal;
        }

        private static bool Text(object value, int max, bool empty = true)
        {
            string text = value as string;
            if (text == null || text.Length > max || (!empty && text.Length == 0)) return false;
            foreach (char ch in text) if (ch <= '\u001f' || ch == '\u007f') return false;
            return true;
        }

        private static bool Count(object value, long max = MaxScore)
        {
            if (!Numeric(value)) return false;
            double number = Json.Num(value, double.NaN);
            return !double.IsNaN(number) && number >= 0 && number <= max && number == Math.Floor(number);
        }

        private static bool Duration(object value)
        {
            if (!Numeric(value)) return false;
            double number = Json.Num(value, double.NaN);
            return !double.IsNaN(number) && number >= 0 && number <= 604800;
        }

        private static bool Time(object value, long maximum)
        {
            if (!Numeric(value)) return false;
            double number = Json.Num(value, double.NaN);
            return !double.IsNaN(number) && number >= MinTime && number <= maximum && number == Math.Floor(number);
        }

        private static long? GameTime(object value, long maximum)
        {
            if (!Numeric(value)) return null;
            double number = Math.Floor(Json.Num(value, double.NaN) * 1000);
            return Time(number, maximum) ? (long?)((long)number) : null;
        }

        private static bool TrimSpace(char ch)
        {
            return ch == '\t' || ch == '\v' || ch == '\f' || ch == '\r' || ch == '\n' || ch == '\u2028' || ch == '\u2029' || ch == '\ufeff' || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.SpaceSeparator;
        }

        private static string Trim(string value)
        {
            int start = 0, end = value.Length;
            while (start < end && TrimSpace(value[start])) start++;
            while (end > start && TrimSpace(value[end - 1])) end--;
            return start == 0 && end == value.Length ? value : value.Substring(start, end - start);
        }

        private static string Pool(object value)
        {
            string text = value as string;
            if (text == null) return null;
            text = Trim(text).ToLowerInvariant();
            if (text == "na" || text == "us" || text == "eu" || text == "fr" || text == "de") return "global";
            return Array.IndexOf(Pools, text) >= 0 ? text : null;
        }

        private static bool Matches(long? a, long? b)
        {
            return a.HasValue && b.HasValue && Math.Abs(a.Value - b.Value) <= EpochTolerance;
        }

        private static string CampaignId(long start) { return UnixEpoch.AddMilliseconds(start).ToString("yyyyMMdd", CultureInfo.InvariantCulture); }
        private static string DisplayText(object value, int max) { return Text(value, max) ? (string)value : ""; }
        private static long Score(object value) { return Count(value) ? Json.Int(value) : 0; }
        private static string Faction(object value)
        {
            string text = value as string;
            return text == "Alliance" || text == "Horde" ? text : "";
        }
        private static string PlayerClass(object value)
        {
            string text = value as string;
            return text != null && Classes.Contains(text) ? text : "";
        }
        private static object Level(object value) { return Count(value, 1000) && Json.Int(value) > 0 ? (object)Json.Int(value) : null; }
        private static bool True(object value) { return value is bool && (bool)value; }

        private static int LocaleCompare(string a, string b)
        {
            int comparison = English.Compare(a, b, CompareOptions.IgnoreCase);
            if (comparison != 0) return comparison;
            // JavaScript's default English collation orders lowercase first.
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                if (a[i] == b[i]) continue;
                bool lowerA = char.IsLower(a[i]), lowerB = char.IsLower(b[i]);
                if (lowerA != lowerB) return lowerA ? -1 : 1;
            }
            return English.Compare(a, b, CompareOptions.None);
        }

        private static bool IndexKey(string key, out uint index)
        {
            return uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index != uint.MaxValue && index.ToString(CultureInfo.InvariantCulture) == key;
        }

        private static IEnumerable<KeyValuePair<string, object>> Entries(object value, int limit)
        {
            var map = value as Dictionary<string, object>;
            if (map == null) yield break;
            if (map.Count > limit) Invalid();
            // ECMAScript enumerates integer property names before insertion-order names.
            var indices = new List<KeyValuePair<uint, string>>();
            foreach (string key in map.Keys)
            {
                uint index;
                if (IndexKey(key, out index)) indices.Add(new KeyValuePair<uint, string>(index, key));
            }
            indices.Sort(delegate(KeyValuePair<uint, string> a, KeyValuePair<uint, string> b) { return a.Key.CompareTo(b.Key); });
            foreach (var index in indices) yield return new KeyValuePair<string, object>(index.Value, map[index.Value]);
            foreach (var entry in map)
            {
                uint index;
                if (!IndexKey(entry.Key, out index)) yield return entry;
            }
        }

        private static IEnumerable<object> Rows(object value, int limit)
        {
            var map = value as Dictionary<string, object>;
            if (map != null)
            {
                object row;
                for (int i = 1; map.TryGetValue(i.ToString(CultureInfo.InvariantCulture), out row); i++)
                {
                    if (i > limit) Invalid();
                    yield return row;
                }
                yield break;
            }
            var list = value as IList;
            if (list == null) yield break;
            for (int i = 0; i < list.Count; i++)
            {
                if (i >= limit) Invalid();
                yield return list[i];
            }
        }

        private static Dictionary<string, object> SourceProjection(Dictionary<string, object> source)
        {
            if (source == null) Invalid();
            string id = Get(source, "sourceId") as string;
            string version = Get(source, "addonVersion") as string;
            if (id == null || !SourceId.IsMatch(id) || (Get(source, "client") as string) != "forever" ||
                !Count(Get(source, "interface"), 16001) || Json.Int(Get(source, "interface")) != 16001 ||
                !Text(version, 32, false) || !AddonVersions.IsSupported("overlord", version) ||
                !Time(Get(source, "observedAt"), Math.Min(MaxTime, Json.Now() + ClockSkew))) Invalid();
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sourceId", id.ToLowerInvariant() }, { "client", "forever" }, { "addonVersion", version },
                { "interface", 16001 }, { "observedAt", Json.Int(Get(source, "observedAt")) }
            };
        }

        private static bool NamePart(string part)
        {
            if (part.Length == 0 || Encoding.UTF8.GetByteCount(part) > 24) return false;
            for (int i = 0; i < part.Length; i++)
            {
                if (part[i] == '\'') continue;
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(part, i);
                if (category != UnicodeCategory.UppercaseLetter && category != UnicodeCategory.LowercaseLetter &&
                    category != UnicodeCategory.TitlecaseLetter && category != UnicodeCategory.ModifierLetter &&
                    category != UnicodeCategory.OtherLetter) return false;
                if (char.IsHighSurrogate(part[i])) i++;
            }
            return true;
        }

        private static PlayerIdentity Identity(object value)
        {
            if (!Text(value, 160, false)) return null;
            string name = Trim((string)value);
            int pipe = name.IndexOf('|');
            if (pipe >= 0) name = name.Substring(0, pipe);
            name = Trim(name).Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u2013', '-').Replace('\u2014', '-').Replace('\u00ad', '-');
            if (Encoding.UTF8.GetByteCount(name) > 80 || name.IndexOf(':') >= 0) return null;
            int dash = name.IndexOf('-');
            string basis = Spaces.Replace(Trim(dash < 0 ? name : name.Substring(0, dash)), " ");
            string[] parts = basis.Split(' ');
            if (parts.Length != 2 || !NamePart(parts[0]) || !NamePart(parts[1]) || UnknownNames.Contains(parts[0].ToLowerInvariant())) return null;
            return new PlayerIdentity { Name = name, Key = basis.ToLowerInvariant() };
        }

        private static bool WrongPool(Dictionary<string, object> metadata, string campaignPool)
        {
            return metadata.ContainsKey("pool") && !object.Equals(Get(metadata, "pool"), "") && Pool(Get(metadata, "pool")) != campaignPool;
        }

        private static Dictionary<string, object> Player(PlayerIdentity identity, string guild, string faction, string playerClass, object level)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "name", identity.Name }, { "guild", guild }, { "faction", faction }, { "class", playerClass },
                { "level", level }, { "kills", 0L }, { "captures", 0L }
            };
        }

        private static List<Dictionary<string, object>> ScorePlayers(Dictionary<string, object> bucket, string campaignPool)
        {
            var players = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            var info = Record(Get(bucket, "playerInfo"));
            foreach (string metric in new[] { "kills", "captures" })
            {
                foreach (var entry in Entries(Get(bucket, metric == "kills" ? "kills" : "captureCount"), 50000))
                {
                    var identity = Identity(entry.Key);
                    if (identity == null || !Count(entry.Value)) continue;
                    var metadata = Record(Get(info, entry.Key));
                    if (WrongPool(metadata, campaignPool)) continue;
                    Dictionary<string, object> player;
                    if (!players.TryGetValue(identity.Key, out player))
                    {
                        if (players.Count >= 50000) Invalid();
                        player = Player(identity, DisplayText(Get(metadata, "guild"), 128), Faction(Get(metadata, "faction")), PlayerClass(Get(metadata, "class")), Level(Get(metadata, "level")));
                        players.Add(identity.Key, player);
                    }
                    // Aliases are max-merged, never summed; zone previews are not capture totals.
                    player[metric] = Math.Max(Json.Int(player[metric]), Json.Int(entry.Value));
                }
            }
            var result = new List<Dictionary<string, object>>(players.Values);
            result.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b) {
                int order = Json.Int(b["kills"]).CompareTo(Json.Int(a["kills"]));
                if (order == 0) order = Json.Int(b["captures"]).CompareTo(Json.Int(a["captures"]));
                return order != 0 ? order : LocaleCompare((string)a["name"], (string)b["name"]);
            });
            if (result.Count > 6500) result.RemoveRange(6500, result.Count - 6500);
            return result;
        }

        private static void AddCachePlayer(Dictionary<string, Dictionary<string, object>> players, object input, string metric, object value, string rowFaction, Dictionary<string, object> metadata, Dictionary<string, object> info, string campaignPool)
        {
            if (!IsRecord(input) || !Count(value)) return;
            var row = Record(input);
            var identity = Identity(Get(row, "name"));
            if (identity == null) return;
            string rawName = (string)Get(row, "name");
            var meta = Record(Get(metadata, rawName));
            var details = Record(Get(info, rawName));
            if (WrongPool(details, campaignPool)) return;
            Dictionary<string, object> player;
            if (!players.TryGetValue(identity.Key, out player))
            {
                string faction = Faction(Get(meta, "2"));
                string playerClass = PlayerClass(Get(meta, "1"));
                player = Player(identity, DisplayText(Get(details, "guild"), 128), faction.Length > 0 ? faction : Faction(rowFaction),
                    playerClass.Length > 0 ? playerClass : PlayerClass(Get(row, "class")), Level(Get(details, "level")));
                players.Add(identity.Key, player);
            }
            player[metric] = Math.Max(Json.Int(player[metric]), Json.Int(value));
        }

        private static List<Dictionary<string, object>> CachePlayers(Dictionary<string, object> cache, string campaignPool, Dictionary<string, object> bucket)
        {
            var players = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            var metadata = Record(Get(cache, "meta"));
            var info = Record(Get(Record(bucket), "playerInfo"));
            foreach (object row in Rows(Get(cache, "sortedKills"), 5000))
                AddCachePlayer(players, row, "kills", Get(Record(row), "kills"), "", metadata, info, campaignPool);
            var byFaction = Record(Get(cache, "byFaction"));
            foreach (string side in new[] { "Alliance", "Horde" })
                foreach (object row in Rows(Get(byFaction, side), 500))
                    if ((Get(Record(row), "faction") as string) == side)
                        AddCachePlayer(players, row, "captures", Get(Record(row), "count"), side, metadata, info, campaignPool);
            return new List<Dictionary<string, object>>(players.Values);
        }

        private static List<Dictionary<string, object>> CacheGuilds(Dictionary<string, object> cache)
        {
            var result = new List<Dictionary<string, object>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object input in Rows(Get(cache, "sortedGuildRating"), 100))
            {
                if (!IsRecord(input)) continue;
                var row = Record(input);
                if (!Text(Get(row, "guild"), 128, false) || !Count(Get(row, "kills")) || !Count(Get(row, "points")) ||
                    !Count(Get(row, "zones")) || !Count(Get(row, "outposts")) || !Count(Get(row, "keeps"))) continue;
                string name = (string)Get(row, "guild");
                if (!seen.Add(name.ToLowerInvariant())) continue;
                result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "name", name }, { "faction", Faction(Get(row, "faction")) }, { "kills", Json.Int(Get(row, "kills")) },
                    { "points", Json.Int(Get(row, "points")) }, { "zones", Json.Int(Get(row, "zones")) },
                    { "outposts", Json.Int(Get(row, "outposts")) }, { "keeps", Json.Int(Get(row, "keeps")) }
                });
            }
            return result;
        }

        private static string WorldPool(Dictionary<string, object> database)
        {
            var votes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in Entries(Get(database, "outposts"), 256))
            {
                string stamp = Pool(Get(Record(entry.Value), "pool"));
                if (stamp == null) continue;
                int count;
                votes.TryGetValue(stamp, out count);
                votes[stamp] = count + 1;
            }
            string winner = null;
            int best = 0;
            foreach (var vote in votes)
                if (vote.Value > best || (vote.Value == best && LocaleCompare(vote.Key, winner) < 0))
                {
                    winner = vote.Key;
                    best = vote.Value;
                }
            return winner ?? Pool(Get(database, "lastSessionPool"));
        }

        private static long? Timely(object value, long maximum, long campaignStart)
        {
            long? at = GameTime(value, maximum);
            return at.HasValue && at.Value >= campaignStart - EpochTolerance ? at : null;
        }

        private static void WorldProjection(Dictionary<string, object> world, Dictionary<string, object> campaign, long maximum)
        {
            long start = Json.Int(campaign["campaignStart"]);
            string campaignPool = (string)campaign["pool"];
            var zones = new List<Dictionary<string, object>>();
            foreach (var entry in Entries(Get(world, "zones"), 512))
            {
                if (!IsRecord(entry.Value) || !Text(entry.Key, 128, false)) continue;
                var row = Record(entry.Value);
                string status = Get(row, "status") as string;
                if (status == null || !ZoneStatuses.Contains(status)) continue;
                long? capturedAt = GameTime(Get(row, "capturedTime"), maximum);
                if (capturedAt.HasValue && capturedAt.Value < start - EpochTolerance) continue;
                zones.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "id", entry.Key }, { "status", status }, { "owner", Faction(Get(row, "owner")) },
                    { "updatedAt", True(Get(row, "_loginSyncUnconfirmed")) ? null : Timely(Get(row, "updatedAt"), maximum, start) },
                    { "capturedAt", capturedAt }, { "kills", Score(Get(row, "killsCurrent")) },
                    { "holdSeconds", Duration(Get(row, "holdTimeElapsed")) ? Json.Num(Get(row, "holdTimeElapsed")) : 0 },
                    { "requiredSeconds", Duration(Get(row, "holdTimeRequired")) ? Json.Num(Get(row, "holdTimeRequired")) : 0 }
                });
            }
            var sites = new List<Dictionary<string, object>>();
            foreach (var entry in Entries(Get(world, "outposts"), 256))
            {
                if (!IsRecord(entry.Value) || !Text(entry.Key, 128, false)) continue;
                var row = Record(entry.Value);
                string status = Get(row, "status") as string;
                if (status == null || !SiteStatuses.Contains(status) || WrongPool(row, campaignPool)) continue;
                long? claimedAt = GameTime(Get(row, "claimedAt"), maximum);
                if (claimedAt.HasValue && claimedAt.Value < start - EpochTolerance) continue;
                sites.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "id", entry.Key }, { "status", status }, { "ownerGuild", DisplayText(Get(row, "ownerGuild"), 128) },
                    { "ownerFaction", Faction(Get(row, "ownerFaction")) }, { "claimedAt", claimedAt },
                    { "expiresAt", GameTime(Get(row, "expiresAt"), maximum + Week) },
                    { "updatedAt", True(Get(row, "_loginSyncUnconfirmed")) ? null : Timely(Get(row, "updatedAt"), maximum, start) },
                    { "contested", True(Get(row, "isContested")) }
                });
            }
            campaign["zones"] = zones;
            campaign["sites"] = sites;
        }

        private static Dictionary<string, object> Campaign(Dictionary<string, Dictionary<string, object>> campaigns, string pool, long start)
        {
            string key = pool + ":" + start.ToString(CultureInfo.InvariantCulture);
            Dictionary<string, object> campaign;
            if (!campaigns.TryGetValue(key, out campaign))
            {
                if (campaigns.Count >= 16) Invalid();
                campaign = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "pool", pool }, { "campaignStart", start }, { "campaignId", CampaignId(start) },
                    { "snapshotAt", null }, { "cached", true },
                    { "players", new List<Dictionary<string, object>>() }, { "guilds", new List<Dictionary<string, object>>() },
                    { "zones", new List<Dictionary<string, object>>() }, { "sites", new List<Dictionary<string, object>>() }
                };
                campaigns.Add(key, campaign);
            }
            return campaign;
        }

        private static void ValidateDate(object value, long maximum, long earliest)
        {
            if (value != null && (!Time(value, Math.Min(MaxTime, maximum)) || Json.Int(value) < earliest)) Invalid();
        }

        private static void ValidateProjected(List<Dictionary<string, object>> campaigns, long maximum)
        {
            // All fields and uniqueness are constructed above, but final server
            // chronology and combined row bounds can still reject a raw projection.
            int total = campaigns.Count;
            foreach (var campaign in campaigns)
            {
                long start = Json.Int(campaign["campaignStart"]);
                ValidateDate(campaign["snapshotAt"], maximum, start - EpochTolerance);
                if (campaign["snapshotAt"] != null && Json.Int(campaign["snapshotAt"]) > start + Week + EpochTolerance) Invalid();
                foreach (string field in new[] { "players", "guilds", "zones", "sites" })
                {
                    var rows = (List<Dictionary<string, object>>)campaign[field];
                    int limit = field == "players" ? 6500 : field == "guilds" ? 5000 : field == "zones" ? 512 : 256;
                    if (rows.Count > limit) Invalid();
                    total += rows.Count;
                    if (total > 40000) Invalid();
                    if (field == "zones" || field == "sites")
                        foreach (var row in rows)
                        {
                            ValidateDate(row["updatedAt"], maximum, start - EpochTolerance);
                            if (field == "zones") ValidateDate(row["capturedAt"], maximum, start - EpochTolerance);
                            else
                            {
                                ValidateDate(row["claimedAt"], maximum, start - EpochTolerance);
                                ValidateDate(row["expiresAt"], maximum + Week, start - EpochTolerance);
                                if (row["claimedAt"] != null && row["expiresAt"] != null && Json.Int(row["expiresAt"]) < Json.Int(row["claimedAt"])) Invalid();
                            }
                        }
                }
            }
        }

        internal static Dictionary<string, object> Normalize(Dictionary<string, object> saved, Dictionary<string, object> source)
        {
            if (saved == null || !IsRecord(Get(saved, "OverlordDB"))) Invalid();
            var projectedSource = SourceProjection(source);
            long maximum = Json.Int(projectedSource["observedAt"]) + ClockSkew;
            var database = Record(Get(saved, "OverlordDB"));
            var campaigns = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            var buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            var rawBuckets = Record(Get(database, "leaderboardsByPool"));
            foreach (string key in BucketKeys)
            {
                var bucket = Record(Get(rawBuckets, key));
                string campaignPool = Pool(key);
                long? start = GameTime(Get(bucket, "campaignStart"), maximum);
                if (!start.HasValue || buckets.ContainsKey(campaignPool)) continue;
                buckets.Add(campaignPool, new Bucket { Value = bucket, Start = start.Value });
            }
            string aliasPool = Pool(Get(database, database.ContainsKey("leaderboardAliasPool") ? "leaderboardAliasPool" : "lastSessionPool"));
            var root = Record(Get(database, "leaderboard"));
            long? rootStart = GameTime(Get(root, "campaignStart"), maximum);
            if (aliasPool != null && rootStart.HasValue && !buckets.ContainsKey(aliasPool))
                buckets.Add(aliasPool, new Bucket { Value = root, Start = rootStart.Value });
            foreach (var bucket in buckets) Campaign(campaigns, bucket.Key, bucket.Value.Start);

            var previews = new List<Preview>();
            foreach (string kind in new[] { "snapshot", "display" })
            {
                object input = Get(database, kind == "snapshot" ? "leaderboardSnapshot" : "leaderboardDisplayCache");
                if (!IsRecord(input)) continue;
                var value = Record(input);
                string campaignPool = Pool(Get(value, "pool"));
                long? start = GameTime(Get(value, "campaignStart"), maximum);
                long? scoreEpoch = GameTime(Get(value, "scoreBucketEpoch"), maximum);
                if (campaignPool == null || !start.HasValue || !Matches(start, scoreEpoch)) continue;
                if (kind == "display" && (!Count(Get(value, "version"), 2) || Json.Int(Get(value, "version")) != 2 ||
                    !Count(Get(value, "killLimit"), 5000) || Json.Int(Get(value, "killLimit")) != 5000)) continue;
                Bucket existing;
                bool sameBucket = buckets.TryGetValue(campaignPool, out existing) && Matches(start, existing.Start);
                long? attested = GameTime(Get(database, "leaderboardScoreBucketEpoch"), maximum);
                if (sameBucket && campaignPool == aliasPool && attested.HasValue && !Matches(start, attested)) continue;
                var campaign = Campaign(campaigns, campaignPool, sameBucket ? existing.Start : start.Value);
                long? rawAt = GameTime(Get(value, "at"), maximum);
                long? at = rawAt.HasValue && rawAt.Value >= start.Value - EpochTolerance && rawAt.Value <= start.Value + Week + EpochTolerance ? rawAt : null;
                previews.Add(new Preview { Snapshot = kind == "snapshot", Value = value, Campaign = campaign, At = at, Bucket = sameBucket ? existing.Value : null });
            }
            var hasSnapshot = new HashSet<Dictionary<string, object>>();
            var hasPreview = new HashSet<Dictionary<string, object>>();
            foreach (var preview in previews)
            {
                hasPreview.Add(preview.Campaign);
                if (!preview.Snapshot) continue;
                preview.Campaign["players"] = ScorePlayers(preview.Value, (string)preview.Campaign["pool"]);
                preview.Campaign["snapshotAt"] = preview.At;
                hasSnapshot.Add(preview.Campaign);
            }
            foreach (var preview in previews)
            {
                if (preview.Snapshot) continue;
                preview.Campaign["guilds"] = CacheGuilds(preview.Value);
                if (!hasSnapshot.Contains(preview.Campaign))
                {
                    preview.Campaign["players"] = CachePlayers(preview.Value, (string)preview.Campaign["pool"], preview.Bucket);
                    preview.Campaign["snapshotAt"] = preview.At;
                }
                else if (!preview.At.HasValue || preview.Campaign["snapshotAt"] == null) preview.Campaign["snapshotAt"] = null;
                else preview.Campaign["snapshotAt"] = Math.Min(preview.At.Value, Json.Int(preview.Campaign["snapshotAt"]));
            }
            foreach (var bucket in buckets)
            {
                var campaign = Campaign(campaigns, bucket.Key, bucket.Value.Start);
                if (!hasPreview.Contains(campaign)) campaign["players"] = ScorePlayers(bucket.Value.Value, bucket.Key);
            }
            foreach (var entry in Entries(Get(database, "worldsByPool"), 16))
            {
                string campaignPool = Pool(entry.Key);
                var world = Record(entry.Value);
                long? start = GameTime(Get(world, "epoch"), maximum);
                if (campaignPool == null || !start.HasValue) continue;
                WorldProjection(world, Campaign(campaigns, campaignPool, start.Value), maximum);
            }
            string activePool = WorldPool(database);
            long? worldStart = GameTime(Get(database, "lastResetTimestamp"), maximum);
            if (activePool != null && worldStart.HasValue) WorldProjection(database, Campaign(campaigns, activePool, worldStart.Value), maximum);
            var result = new List<Dictionary<string, object>>(campaigns.Values);
            result.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b) {
                int order = Json.Int(b["campaignStart"]).CompareTo(Json.Int(a["campaignStart"]));
                return order != 0 ? order : LocaleCompare((string)a["pool"], (string)b["pool"]);
            });
            ValidateProjected(result, maximum);
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "schemaVersion", 1 }, { "kind", "overlord" }, { "source", projectedSource }, { "campaigns", result }
            };
        }
    }
}
