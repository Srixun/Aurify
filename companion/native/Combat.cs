using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace AurifyClient
{
    internal sealed class CombatSynchronizer
    {
        private const int ChunkBytes = 512 * 1024;
        private const int PrefixBytes = 4096;
        private const long FileTimeEpoch = 116444736000000000L;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly Regex DatePrefix = new Regex(@"^([0-9]{1,2})/([0-9]{1,2})\s", RegexOptions.CultureInvariant);
        private static readonly Regex TimePrefix = new Regex(@"^[0-9]{1,2}/[0-9]{1,2}\s+([0-9]{1,2}:[0-9]{2}:[0-9]{2})", RegexOptions.CultureInvariant);
        private static readonly Regex TimestampPrefix = new Regex(@"^([0-9]{1,2})/([0-9]{1,2})\s+([0-9]{1,2}:[0-9]{2}:[0-9]{2})(?:\.([0-9]{3}))?\s", RegexOptions.CultureInvariant);
        private static readonly Regex PartialPrefix = new Regex(@"^[0-9]{1,2}(?:/|$)", RegexOptions.CultureInvariant);
        private readonly PortalClient client;
        private readonly Dictionary<string, object> combat;
        private readonly Action saveState;
        private readonly Action<string, string> report;
        private TimeZoneInfo timezone;
        private readonly bool useHostTimezone;
        private readonly Func<long> clock;
        private readonly int maxChunks;
        private bool clean;
        private HandleInfo cleanInfo;
        private string cleanStreamId;
        private TimeZoneInfo cleanTimezone;
        private long reconciledAt;
        private bool savePending;

        private void SaveState()
        {
            // A failed callback can leave this private working state ahead of durable state.
            // A later scan must publish it successfully before declaring the stream clean.
            savePending = true;
            saveState();
            savePending = false;
        }

        internal CombatSynchronizer(PortalClient client, Dictionary<string, object> state,
            Action saveState, Action<string, string> report)
            : this(client, state, saveState, report, TimeZoneInfo.Local, Json.Now, 16) { useHostTimezone = true; }

        // Only the native regression harness supplies a clock or timezone. These are not configuration.
        internal CombatSynchronizer(PortalClient client, Dictionary<string, object> state,
            Action saveState, Action<string, string> report, TimeZoneInfo timezone,
            Func<long> clock, int maxChunks)
        {
            if (client == null || state == null || saveState == null || timezone == null || clock == null)
                throw new ArgumentNullException("Combat synchronizer dependency");
            this.client = client;
            this.saveState = saveState;
            this.report = report ?? delegate { };
            this.timezone = timezone;
            this.clock = clock;
            this.maxChunks = Math.Max(1, Math.Min(16, maxChunks));
            combat = Json.Obj(Json.Get(state, "combat"));
            state["combat"] = combat;
            var closures = new List<object>(Json.Items(Json.Get(combat, "pendingClosures")));
            combat["pendingClosures"] = closures;
        }

        private static long Number(Dictionary<string, object> value, string key)
        {
            long number = Json.Int(Json.Get(value, key));
            return number >= 0 && number <= 9007199254740991L ? number : 0;
        }

        private static int? OffsetMinutes(Dictionary<string, object> value, string key)
        {
            object raw = Json.Get(value, key);
            if (raw == null) return null;
            double number = Json.Num(raw, Double.NaN);
            return !Double.IsNaN(number) && number >= -840 && number <= 840 && number == Math.Truncate(number)
                ? (int?)number : null;
        }

        private Dictionary<string, object> Current { get { return Json.Get(combat, "current") as Dictionary<string, object>; } }
        private List<object> Closures { get { return (List<object>)combat["pendingClosures"]; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct HandleInfo
        {
            public uint Attributes;
            public uint CreationLow, CreationHigh;
            public uint AccessLow, AccessHigh;
            public uint WriteLow, WriteHigh;
            public uint Volume;
            public uint SizeHigh, SizeLow;
            public uint Links;
            public uint IndexHigh, IndexLow;
            public ulong Creation { get { return ((ulong)CreationHigh << 32) | CreationLow; } }
            public ulong Modified { get { return ((ulong)WriteHigh << 32) | WriteLow; } }
            public ulong Index { get { return ((ulong)IndexHigh << 32) | IndexLow; } }
            public long Size { get { return checked((long)(((ulong)SizeHigh << 32) | SizeLow)); } }
            public string Key { get { return "win32:" + Volume.ToString(Invariant) + ":" + Index.ToString(Invariant) + ":" + Creation.ToString(Invariant); } }
            public double BirthMs
            {
                get
                {
                    long ticks = checked((long)Creation) - FileTimeEpoch;
                    // Node's Stats builds milliseconds from seconds and the fractional nanoseconds separately.
                    long seconds = ticks / 10000000L;
                    long remainder = ticks % 10000000L;
                    if (remainder < 0) { seconds--; remainder += 10000000L; }
                    return seconds * 1000.0 + remainder / 10000.0;
                }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo information);

        private sealed class Inspection
        {
            internal HandleInfo Info;
            internal byte[] Prefix;
        }

        private static HandleInfo Information(FileStream file)
        {
            HandleInfo info;
            if (!GetFileInformationByHandle(file.SafeFileHandle, out info))
                throw new IOException("Cannot inspect the combat log handle.", new Win32Exception(Marshal.GetLastWin32Error()));
            if ((info.Attributes & (0x10U | 0x400U)) != 0)
                throw new IOException("Combat log must be a regular file, not a directory or reparse point.");
            return info;
        }

        private static bool Same(HandleInfo left, HandleInfo right)
        {
            return left.Volume == right.Volume && left.Index == right.Index && left.Creation == right.Creation &&
                left.Modified == right.Modified && left.Size == right.Size;
        }

        private static FileStream Open(string path)
        {
            Files.NoLinks(path);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        }

        private static bool ReadExactly(FileStream file, byte[] bytes)
        {
            int position = 0;
            while (position < bytes.Length)
            {
                int count = file.Read(bytes, position, bytes.Length - position);
                if (count == 0) return false;
                position += count;
            }
            return true;
        }

        private static bool PathStillMatches(string path, HandleInfo expected)
        {
            // Reopen the name as well as checking the original handle: rename/replacement can leave that handle alive.
            using (FileStream named = Open(path)) return Same(expected, Information(named));
        }

        private static Inspection Inspect(string path)
        {
            try
            {
                using (FileStream file = Open(path))
                {
                    HandleInfo before = Information(file);
                    byte[] prefix = new byte[(int)Math.Min(PrefixBytes, before.Size)];
                    if (!ReadExactly(file, prefix)) return null;
                    HandleInfo after = Information(file);
                    if (!Same(before, after) || !PathStillMatches(path, after)) return null;
                    return new Inspection { Info = after, Prefix = prefix };
                }
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static bool EqualBytes(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static byte[] ReadChunk(string path, Inspection inspection, long offset, int length)
        {
            try
            {
                using (FileStream file = Open(path))
                {
                    HandleInfo before = Information(file);
                    if (!Same(before, inspection.Info) || offset < 0 || length > before.Size - offset) return null;
                    byte[] prefix = new byte[inspection.Prefix.Length];
                    if (!ReadExactly(file, prefix) || !EqualBytes(prefix, inspection.Prefix)) return null;
                    if (!Same(before, Information(file)) || !PathStillMatches(path, before)) return null;
                    file.Position = offset;
                    byte[] bytes = new byte[length];
                    if (!ReadExactly(file, bytes)) return null;
                    // An in-place rewrite, truncation, append or replacement invalidates this snapshot; retry next scan.
                    if (!Same(before, Information(file)) || !PathStillMatches(path, before)) return null;
                    file.Position = 0;
                    if (!ReadExactly(file, prefix) || !EqualBytes(prefix, inspection.Prefix) || !Same(before, Information(file))) return null;
                    return bytes;
                }
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static string PrefixHash(byte[] prefix, int length)
        {
            using (SHA256 hash = SHA256.Create())
            {
                byte[] digest = hash.ComputeHash(prefix, 0, length);
                return BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
            }
        }

        private static bool PrefixMatches(Dictionary<string, object> current, Inspection inspection)
        {
            long expected = Number(current, "prefixBytes");
            return expected == 0 || (expected <= inspection.Prefix.Length &&
                PrefixHash(inspection.Prefix, (int)expected) == Json.Str(Json.Get(current, "prefixHash")));
        }

        private static bool GenerationMatches(Dictionary<string, object> current, Inspection inspection)
        {
            string key = Json.Str(Json.Get(current, "fileKey"), "");
            if (key == inspection.Info.Key) return true;
            if (key.StartsWith("win32:", StringComparison.Ordinal)) return false;
            string[] parts = key.Split(':');
            if (parts.Length != 3) return false;
            double volume, index, birth;
            if (!Double.TryParse(parts[0], NumberStyles.Float, Invariant, out volume) ||
                !Double.TryParse(parts[1], NumberStyles.Float, Invariant, out index) ||
                !Double.TryParse(parts[2], NumberStyles.Float, Invariant, out birth)) return false;
            // Legacy game.mjs statIdentity truncates birthtimeMs before generationKey serializes it.
            // Preserve that exact representation for migration; native keys retain every FILETIME/index bit.
            return volume == inspection.Info.Volume && index == (double)inspection.Info.Index &&
                birth == Math.Truncate(inspection.Info.BirthMs);
        }

        private DateTime Local(long milliseconds)
        {
            return TimeZoneInfo.ConvertTimeFromUtc(Epoch.AddMilliseconds(milliseconds), timezone);
        }

        private int ZoneAt(long milliseconds)
        {
            return -(int)timezone.GetUtcOffset(Epoch.AddMilliseconds(milliseconds)).TotalMinutes;
        }

        private static string DateText(DateTime value) { return value.ToString("yyyy-MM-dd", Invariant); }

        private DateTime ResolveCivil(DateTime civil, out int offset)
        {
            civil = DateTime.SpecifyKind(civil, DateTimeKind.Unspecified);
            if (timezone.IsInvalidTime(civil))
            {
                // ECMAScript Date advances a nonexistent spring-forward time by the gap.
                TimeSpan before = timezone.GetUtcOffset(civil.AddDays(-1));
                TimeSpan after = timezone.GetUtcOffset(civil.AddDays(1));
                civil = civil.Add(after - before);
            }
            TimeSpan selected = timezone.GetUtcOffset(civil);
            if (timezone.IsAmbiguousTime(civil))
            {
                TimeSpan[] choices = timezone.GetAmbiguousTimeOffsets(civil);
                selected = choices[0] > choices[1] ? choices[0] : choices[1];
            }
            offset = -(int)selected.TotalMinutes;
            return civil;
        }

        private long CivilMilliseconds(DateTime civil) { return (civil.Ticks - Epoch.Ticks) / TimeSpan.TicksPerMillisecond; }

        private string InferDate(string prefix, long timestamp)
        {
            DateTime reference = Local(timestamp);
            Match match = DatePrefix.Match(prefix);
            if (!match.Success) return DateText(reference);
            int month = Int32.Parse(match.Groups[1].Value, Invariant);
            int day = Int32.Parse(match.Groups[2].Value, Invariant);
            if (month < 1 || month > 12 || day < 1 || day > 31) return DateText(reference);
            DateTime best = reference;
            double distance = Double.PositiveInfinity;
            for (int year = Math.Max(1, reference.Year - 1); year <= Math.Min(9999, reference.Year + 1); year++)
            {
                if (day > DateTime.DaysInMonth(year, month)) continue;
                int zone;
                DateTime candidate = ResolveCivil(new DateTime(year, month, day), out zone);
                double difference = Math.Abs((double)CivilMilliseconds(candidate) + zone * 60000.0 - timestamp);
                if (difference < distance) { best = candidate; distance = difference; }
            }
            return DateText(best);
        }

        private Dictionary<string, object> MakeStream(Inspection inspection)
        {
            long timestamp = inspection.Info.BirthMs > 0 ? (long)inspection.Info.BirthMs :
                (checked((long)inspection.Info.Modified) - FileTimeEpoch) / 10000L;
            if (timestamp == 0) timestamp = clock();
            string prefix = Encoding.UTF8.GetString(inspection.Prefix);
            string date = InferDate(prefix, timestamp);
            Match match = TimePrefix.Match(prefix);
            DateTime source;
            int sourceZone;
            string time = match.Success ? match.Groups[1].Value.PadLeft(8, '0') : "12:00:00";
            if (DateTime.TryParseExact(date + "T" + time, "yyyy-MM-dd'T'HH:mm:ss", Invariant, DateTimeStyles.None, out source))
                ResolveCivil(source, out sourceZone);
            else sourceZone = ZoneAt(timestamp);
            var stream = Json.New();
            stream["streamId"] = Guid.NewGuid().ToString();
            stream["fileKey"] = inspection.Info.Key;
            stream["fileDate"] = date;
            stream["timezoneOffsetMinutes"] = sourceZone;
            stream["hostTimezoneOffsetMinutes"] = ZoneAt(clock());
            stream["baseOffset"] = 0L;
            stream["offset"] = 0L;
            stream["lastSize"] = inspection.Info.Size;
            stream["prefixBytes"] = inspection.Prefix.Length;
            stream["prefixHash"] = Json.Hash(inspection.Prefix);
            stream["reportId"] = null;
            return stream;
        }

        private sealed class ScanBoundary
        {
            internal int Length;
            internal string FileDate;
            internal int? Zone;
            internal Dictionary<string, object> Cursor;
        }

        private static Dictionary<string, object> NewCursor(long offset, bool lineStart, object lastCivil)
        {
            var cursor = Json.New();
            cursor["offset"] = offset;
            cursor["lineStart"] = lineStart;
            cursor["lastCivilTime"] = lastCivil;
            return cursor;
        }

        private ScanBoundary FindBoundary(byte[] bytes, int length, Dictionary<string, object> current)
        {
            long streamOffset = Number(current, "offset");
            var prior = Json.Obj(Json.Get(current, "timezoneCursor"));
            var cursor = NewCursor(streamOffset, Json.Bool(Json.Get(prior, "lineStart"), streamOffset == 0), Json.Get(prior, "lastCivilTime"));
            var result = new ScanBoundary { Length = length, Cursor = cursor };
            DateTime referenceCivil;
            if (!DateTime.TryParseExact(Json.Str(Json.Get(current, "fileDate")) + "T12:00:00", "yyyy-MM-dd'T'HH:mm:ss", Invariant, DateTimeStyles.None, out referenceCivil))
                throw new IOException("The saved combat stream date is invalid.");
            int referenceZone;
            referenceCivil = ResolveCivil(referenceCivil, out referenceZone);
            long reference = CivilMilliseconds(referenceCivil) + referenceZone * 60000L;
            int? currentZone = OffsetMinutes(current, "timezoneOffsetMinutes");
            for (int offset = 0; offset < length; )
            {
                if (Json.Bool(cursor["lineStart"]) && currentZone.HasValue)
                {
                    int available = Math.Min(40, bytes.Length - offset);
                    // ASCII is only for timestamp detection. The upload always uses the original byte array.
                    string prefix = Encoding.ASCII.GetString(bytes, offset, available);
                    Match match = TimestampPrefix.Match(prefix);
                    if (!match.Success && available < 40 && prefix.IndexOf('\n') < 0 && PartialPrefix.IsMatch(prefix))
                    {
                        result.Length = offset;
                        return result;
                    }
                    if (match.Success)
                    {
                        string date = InferDate(prefix, reference);
                        string timestamp = date + "T" + match.Groups[3].Value.PadLeft(8, '0') + "." +
                            (match.Groups[4].Success ? match.Groups[4].Value : "000");
                        DateTime civil;
                        if (DateTime.TryParseExact(timestamp, "yyyy-MM-dd'T'HH:mm:ss.fff", Invariant, DateTimeStyles.None, out civil))
                        {
                            int zone;
                            civil = ResolveCivil(civil, out zone);
                            long civilTime = CivilMilliseconds(civil);
                            long utc = civilTime + zone * 60000L;
                            int laterZone = ZoneAt(utc + 86400000L);
                            long overlap = (laterZone - zone) * 60000L;
                            if (overlap > 0 && CivilMilliseconds(Local(utc + overlap)) == civilTime)
                            {
                                object previousValue = cursor["lastCivilTime"];
                                long previous = Json.Int(previousValue);
                                bool backward = false;
                                if (previousValue != null && previous - civilTime > overlap / 2 && previous - civilTime <= overlap)
                                {
                                    backward = CivilMilliseconds(Local(previous + zone * 60000L)) == previous &&
                                        CivilMilliseconds(Local(previous + laterZone * 60000L)) == previous;
                                }
                                if (currentZone.Value == laterZone || backward) zone = laterZone;
                            }
                            if (zone != currentZone.Value)
                            {
                                result.Length = offset;
                                result.FileDate = date;
                                result.Zone = zone;
                                return result;
                            }
                            cursor["lastCivilTime"] = civilTime;
                        }
                    }
                }
                int newline = Array.IndexOf<byte>(bytes, 10, offset, length - offset);
                int end = newline < 0 ? length : newline + 1;
                cursor["lineStart"] = bytes[end - 1] == 10;
                cursor["offset"] = streamOffset + end;
                offset = end;
            }
            return result;
        }

        private static void ReconcileCursor(Dictionary<string, object> current, long offset)
        {
            var pending = Json.Get(current, "pendingTimezoneCursor") as Dictionary<string, object>;
            var cursor = Json.Get(current, "timezoneCursor") as Dictionary<string, object>;
            if (pending != null && Number(pending, "offset") == offset) current["timezoneCursor"] = pending;
            else if (cursor == null || Number(cursor, "offset") != offset)
                current["timezoneCursor"] = NewCursor(offset, offset == 0, null);
            current.Remove("pendingTimezoneCursor");
        }

        private async Task ClosePendingAsync(CancellationToken ct)
        {
            while (Closures.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var pending = Json.Obj(Closures[0]);
                string id = Json.Str(Json.Get(pending, "streamId"));
                CombatReply status = await client.CombatStatusAsync(id, ct).ConfigureAwait(false);
                if (status != null)
                {
                    CombatReply reply = await client.UploadCombatAsync(id, status.Offset, new byte[0], 0, clock(),
                        Json.Str(Json.Get(pending, "fileDate")), OffsetMinutes(pending, "timezoneOffsetMinutes"), true, ct).ConfigureAwait(false);
                    if (reply == null || reply.Offset != status.Offset)
                        throw new PortalException("Portal returned an invalid close acknowledgement");
                }
                Closures.RemoveAt(0);
                SaveState();
            }
        }

        private async Task RotateAsync(Inspection inspection, long baseOffset, string fileDate, int? zone, CancellationToken ct)
        {
            var current = Current;
            if (current != null)
            {
                string id = Json.Str(Json.Get(current, "streamId"));
                bool queued = false;
                foreach (object entry in Closures)
                    if (Json.Str(Json.Get(Json.Obj(entry), "streamId")) == id) { queued = true; break; }
                if (!queued)
                {
                    // Keep the same bound without silently dropping an unacknowledged close.
                    if (Closures.Count >= 32) await ClosePendingAsync(ct).ConfigureAwait(false);
                    var pending = Json.New();
                    pending["streamId"] = id;
                    pending["fileDate"] = Json.Get(current, "fileDate");
                    pending["timezoneOffsetMinutes"] = Json.Get(current, "timezoneOffsetMinutes");
                    Closures.Add(pending);
                }
            }
            var next = MakeStream(inspection);
            next["baseOffset"] = baseOffset;
            if (fileDate != null) next["fileDate"] = fileDate;
            if (zone.HasValue) next["timezoneOffsetMinutes"] = zone.Value;
            combat["current"] = next;
            SaveState();
            report("combat-rotated", null);
        }

        internal async Task ScanAsync(string filePath, CancellationToken ct)
        {
            bool wasClean = clean;
            clean = false;
            ct.ThrowIfCancellationRequested();
            if (useHostTimezone)
            {
                // Windows timezone changes must be visible without restarting the tray application.
                TimeZoneInfo.ClearCachedData();
                timezone = TimeZoneInfo.Local;
            }
            Inspection inspection = Inspect(filePath);
            if (inspection == null) { await ClosePendingAsync(ct).ConfigureAwait(false); return; }
            var current = Current;
            if (current == null)
            {
                current = MakeStream(inspection);
                combat["current"] = current;
                SaveState();
            }
            else if (!GenerationMatches(current, inspection) || inspection.Info.Size < Number(current, "lastSize") ||
                Number(current, "baseOffset") > inspection.Info.Size || Number(current, "offset") > inspection.Info.Size - Number(current, "baseOffset") ||
                !PrefixMatches(current, inspection))
            {
                await RotateAsync(inspection, 0, null, null, ct).ConfigureAwait(false);
                current = Current;
            }
            else
            {
                bool changed = false;
                if (Json.Str(Json.Get(current, "fileKey")) != inspection.Info.Key)
                {
                    // This is an identity upgrade, not a new stream: preserve IDs, offsets and lost-ACK cursors.
                    current["fileKey"] = inspection.Info.Key;
                    changed = true;
                }
                if (Number(current, "prefixBytes") == 0 && inspection.Prefix.Length > 0)
                {
                    current["prefixBytes"] = inspection.Prefix.Length;
                    current["prefixHash"] = Json.Hash(inspection.Prefix);
                    changed = true;
                }
                if (changed) SaveState();
            }
            long now = clock();
            int hostZone = ZoneAt(now);
            int? savedHostZone = OffsetMinutes(current, "hostTimezoneOffsetMinutes");
            if (wasClean && !savePending && Closures.Count == 0 &&
                Json.Get(current, "pendingTimezoneCursor") == null &&
                Json.Str(current["streamId"]) == cleanStreamId && Same(inspection.Info, cleanInfo) &&
                Number(current, "baseOffset") + Number(current, "offset") == inspection.Info.Size &&
                savedHostZone.HasValue && savedHostZone.Value == hostZone &&
                cleanTimezone != null && cleanTimezone.Id == timezone.Id && cleanTimezone.HasSameRules(timezone) &&
                now >= reconciledAt && now - reconciledAt < 60000)
            {
                ct.ThrowIfCancellationRequested();
                clean = true;
                return;
            }
            await ClosePendingAsync(ct).ConfigureAwait(false);
            string reconciledStreamId = Json.Str(current["streamId"]);
            CombatReply status = await client.CombatStatusAsync(Json.Str(current["streamId"]), ct).ConfigureAwait(false);
            reconciledAt = clock();
            bool reconciled = true;
            long expected = status == null ? 0 : status.Offset;
            if (expected < 0) throw new PortalException("Portal returned an invalid combat offset");
            if (expected > inspection.Info.Size - Number(current, "baseOffset"))
            {
                await RotateAsync(inspection, 0, null, null, ct).ConfigureAwait(false);
                current = Current;
                status = null;
                expected = 0;
            }
            if (Number(current, "offset") != expected || Json.Str(Json.Get(current, "reportId")) != (status == null ? null : status.ReportId) ||
                Json.Get(current, "pendingTimezoneCursor") != null)
            {
                ReconcileCursor(current, expected);
                current["offset"] = expected;
                current["reportId"] = status == null ? null : status.ReportId;
                SaveState();
            }
            savedHostZone = OffsetMinutes(current, "hostTimezoneOffsetMinutes");
            if (savedHostZone.HasValue && savedHostZone.Value != hostZone)
            {
                await RotateAsync(inspection, Number(current, "baseOffset") + Number(current, "offset"), DateText(Local(clock())), hostZone, ct).ConfigureAwait(false);
                current = Current;
                await ClosePendingAsync(ct).ConfigureAwait(false);
            }
            else if (!savedHostZone.HasValue)
            {
                current["hostTimezoneOffsetMinutes"] = hostZone;
                SaveState();
            }
            int chunks = 0, attempts = 0, rotations = 0;
            while (Number(current, "baseOffset") + Number(current, "offset") < inspection.Info.Size && attempts < maxChunks)
            {
                ct.ThrowIfCancellationRequested();
                long offset = Number(current, "offset");
                long absolute = Number(current, "baseOffset") + offset;
                int length = (int)Math.Min(ChunkBytes, inspection.Info.Size - absolute);
                byte[] bytes = ReadChunk(filePath, inspection, absolute, (int)Math.Min(length + 40L, inspection.Info.Size - absolute));
                if (bytes == null) return;
                ScanBoundary boundary = FindBoundary(bytes, length, current);
                if (boundary.Zone.HasValue && boundary.Length == 0)
                {
                    if (++rotations > 32) throw new IOException("Combat timezone changes exceeded the scan limit.");
                    await RotateAsync(inspection, absolute, boundary.FileDate, boundary.Zone, ct).ConfigureAwait(false);
                    current = Current;
                    await ClosePendingAsync(ct).ConfigureAwait(false);
                    continue;
                }
                if (boundary.Length == 0) return;
                current["pendingTimezoneCursor"] = boundary.Cursor;
                SaveState();
                attempts++;
                long? conflictingOffset = null;
                try
                {
                    CombatReply reply = await client.UploadCombatAsync(Json.Str(current["streamId"]), offset, bytes, boundary.Length, clock(),
                        Json.Str(current["fileDate"]), OffsetMinutes(current, "timezoneOffsetMinutes"), false, ct).ConfigureAwait(false);
                    long next = offset + boundary.Length;
                    if (reply == null || reply.Offset != next) throw new PortalException("Portal returned an invalid combat acknowledgement");
                    ReconcileCursor(current, next);
                    current["offset"] = next;
                    current["reportId"] = reply.ReportId;
                    SaveState();
                    chunks++;
                }
                catch (PortalException error)
                {
                    if (error.Status != 409 || !error.ExpectedOffset.HasValue || error.ExpectedOffset.Value < 0 || error.ExpectedOffset.Value == Number(current, "offset")) throw;
                    reconciled = false;
                    conflictingOffset = error.ExpectedOffset;
                }
                if (conflictingOffset.HasValue)
                {
                    if (conflictingOffset.Value > inspection.Info.Size - Number(current, "baseOffset"))
                    {
                        await RotateAsync(inspection, 0, null, null, ct).ConfigureAwait(false);
                        return;
                    }
                    ReconcileCursor(current, conflictingOffset.Value);
                    current["offset"] = conflictingOffset.Value;
                    SaveState();
                }
            }
            if (Number(current, "lastSize") != inspection.Info.Size)
            {
                current["lastSize"] = inspection.Info.Size;
                SaveState();
            }
            if (chunks > 0) report("combat-uploaded", chunks.ToString(Invariant));
            if (savePending) SaveState();
            ct.ThrowIfCancellationRequested();
            if (reconciled && Json.Str(current["streamId"]) == reconciledStreamId && Closures.Count == 0 &&
                Json.Get(current, "pendingTimezoneCursor") == null &&
                Number(current, "baseOffset") + Number(current, "offset") == inspection.Info.Size)
            {
                cleanInfo = inspection.Info;
                cleanStreamId = Json.Str(current["streamId"]);
                cleanTimezone = timezone;
                clean = true;
            }
        }
    }
}
