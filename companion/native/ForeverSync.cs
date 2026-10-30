using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace AurifyClient
{
    internal sealed class ForeverSynchronizer
    {
        private const int RawLimit = 4 * 1024 * 1024;
        private const int GzipLimit = RawLimit + 65536;
        private const int HeaderLimit = 16384;
        private const int PrefixLimit = 4096;
        private const int QueueCountLimit = 4096;
        private const long QueueLimit = 128L * 1024 * 1024;
        private const long IdleMilliseconds = 15L * 60 * 1000;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("AURIFYF1");
        private static readonly Regex DigestPattern = new Regex("^[a-f0-9]{64}\\z", RegexOptions.CultureInvariant);
        private static readonly Regex ReportPattern = new Regex("^[1-9][0-9]{0,19}\\z", RegexOptions.CultureInvariant);
        private static readonly Regex FilePattern = new Regex("^([1-9][0-9]{0,19})-([1-9][0-9]{0,15})-([a-f0-9]{64})\\.segment\\z", RegexOptions.CultureInvariant);
        private static readonly Regex TemporaryPattern = new Regex("^\\.(.+)\\.[a-f0-9]{32}\\.tmp\\z", RegexOptions.CultureInvariant);
        private static readonly Regex IdentityPattern = new Regex("^win32:[0-9]{1,10}:[0-9]{1,20}:[0-9]{1,20}\\z", RegexOptions.CultureInvariant);
        private static readonly Regex ZonePattern = new Regex("^[A-Za-z0-9_+.-]+(?:/[A-Za-z0-9_+.-]+)*\\z", RegexOptions.CultureInvariant);
        private readonly string statePath;
        private readonly string outboxPath;
        private readonly ForeverClient client;
        private readonly Action<string> status;
        private readonly Func<DateTime> utcNow;
        private readonly List<Segment> queue = new List<Segment>();
        private State state;
        private long queueBytes;
        private long observedConnectionGeneration = -1;
        private bool failedStorage;
        private bool needsStatus;

        private sealed class Source
        {
            internal string PathHash, Identity, PrefixHash, AnchorHash;
            internal int PrefixBytes, AnchorBytes;
            internal long Offset, ObservedLength;
            internal bool Drained;
            internal Source Copy() { return (Source)MemberwiseClone(); }
        }

        private sealed class State
        {
            internal string OwnerHash, ReportId, LastReportId, Zone, Visibility;
            internal Source Source;
            internal long NextSeq = 1, AckSeq, LastDataMs, HeartbeatMs;
            internal int Day;
            internal bool Finalizing;
            internal State Copy()
            {
                State result = (State)MemberwiseClone();
                result.Source = Source == null ? null : Source.Copy();
                return result;
            }
        }

        private sealed class Segment
        {
            internal string Name, OwnerHash, ReportId;
            internal long Seq, StartOffset, LastDataMs;
            internal int RawLength, GzipLength, FileLength;
            internal Source After;
        }

        private sealed class Batch
        {
            internal Source Before, After;
            internal byte[] Bytes;
            internal int Count, Day;
            internal bool Replaced;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HandleInfo
        {
            public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
            internal ulong Creation { get { return ((ulong)CreationHigh << 32) | CreationLow; } }
            internal ulong Modified { get { return ((ulong)WriteHigh << 32) | WriteLow; } }
            internal long Length { get { return checked((long)(((ulong)SizeHigh << 32) | SizeLow)); } }
            internal string Identity
            {
                get { return "win32:" + Volume.ToString(Invariant) + ":" + (((ulong)IndexHigh << 32) | IndexLow).ToString(Invariant) + ":" + Creation.ToString(Invariant); }
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out HandleInfo information);

        internal ForeverSynchronizer(string root, ForeverClient client, Action<string> status)
            : this(root, client, status, delegate { return DateTime.UtcNow; }) { }

        // Only the native regression harness supplies a clock; never a user setting.
        internal ForeverSynchronizer(string root, ForeverClient client, Action<string> status, Func<DateTime> utcNow)
        {
            if (client == null || utcNow == null) throw new ArgumentNullException("ForeverLogs dependency");
            this.client = client;
            this.status = status ?? delegate { };
            this.utcNow = utcNow;
            root = NativeGamePaths.LocalPath(root);
            statePath = Path.Combine(root, "config.forever.state.json");
            outboxPath = Path.Combine(root, "forever-outbox");
            Files.NoLinks(statePath);
            bool saved = Exists(statePath);
            state = saved ? ParseState(ParseObject(Files.ReadBytes(statePath, HeaderLimit))) : new State();
            Files.PrivateDirectory(outboxPath);
            Recover(saved);
            needsStatus = state.ReportId != null;
        }

        internal string ReportUrl
        {
            get
            {
                string id = state.ReportId ?? state.LastReportId;
                return id == null ? null : "https://foreverlogs.gg/reports/" + id;
            }
        }

        internal async Task ScanAsync(string logPath, bool gameRunning, string visibility, string timezone, CancellationToken ct)
        {
            if (!Ready()) return;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!BindAccount()) return;
                if (visibility != "private" && visibility != "public") throw new InvalidDataException("ForeverLogs visibility must be private or public.");
                if (!ValidZone(timezone)) throw new InvalidDataException("ForeverLogs needs a valid IANA time zone.");
                // Resume/lost-ACK liveness must be resolved before adding bytes to the saved report.
                if (state.ReportId != null && needsStatus && !await DrainAsync(ct).ConfigureAwait(false)) return;
                if (state.Finalizing && !await FinishAsync(ct).ConfigureAwait(false)) return;
                bool backpressure = false;
                if (!String.IsNullOrEmpty(logPath))
                {
                    string path = NativeGamePaths.LocalPath(logPath);
                    for (int chunk = 0; chunk < 16; chunk++)
                    {
                        ct.ThrowIfCancellationRequested();
                        Batch batch = ReadBatch(path, state.Source, state.Day);
                        if (batch == null) break;
                        if (batch.Replaced)
                        {
                            // A new name/generation is not permission to forget an uncaptured old tail.
                            if (state.Source != null && state.Source.Offset != state.Source.ObservedLength)
                            {
                                await DrainAsync(ct).ConfigureAwait(false);
                                status("ForeverLogs is paused: restore the previous log with its unread tail. Pending data is preserved.");
                                return;
                            }
                            if (!await FinishAsync(ct).ConfigureAwait(false)) return;
                            State replacement = state.Copy();
                            replacement.Source = batch.Before;
                            replacement.LastDataMs = Now();
                            Save(replacement);
                            continue;
                        }
                        if (state.Source == null)
                        {
                            State initial = state.Copy();
                            initial.Source = batch.Before;
                            initial.LastDataMs = Now();
                            Save(initial);
                        }
                        if (state.ReportId != null && (state.Zone != timezone || (batch.Count != 0 && batch.Day != 0 && state.Day != 0 && batch.Day != state.Day)))
                        {
                            if (!await FinishAsync(ct).ConfigureAwait(false)) return;
                            continue;
                        }
                        if (batch.Count == 0)
                        {
                            Observe(batch.After);
                            break;
                        }
                        if (state.ReportId == null)
                        {
                            string id = await client.StartAsync("Aurify combat log", visibility, timezone, ct).ConfigureAwait(false);
                            if (!ValidReport(id)) throw new InvalidDataException("ForeverLogs returned an invalid report identity.");
                            State started = state.Copy();
                            started.ReportId = started.LastReportId = id;
                            started.Zone = timezone;
                            started.Visibility = visibility;
                            started.Day = batch.Day;
                            started.NextSeq = 1;
                            started.AckSeq = 0;
                            started.LastDataMs = Now();
                            started.HeartbeatMs = 0;
                            Save(started);
                            needsStatus = false;
                        }
                        else if (state.Day == 0 && batch.Day != 0)
                        {
                            State dated = state.Copy();
                            dated.Day = batch.Day;
                            Save(dated);
                        }
                        if (!Enqueue(batch)) { backpressure = true; break; }
                        if (batch.After.Drained) break;
                    }
                }
                if (!await DrainAsync(ct).ConfigureAwait(false)) return;
                if (state.ReportId != null)
                {
                    long now = Now();
                    if (state.Source != null && state.Source.Drained && (!gameRunning || now - state.LastDataMs >= IdleMilliseconds))
                    {
                        if (!await FinishAsync(ct).ConfigureAwait(false)) return;
                        status("ForeverLogs report finalized. New complete log lines will resume from the saved cursor.");
                        return;
                    }
                    if (state.HeartbeatMs == 0 || now - state.HeartbeatMs >= 30000)
                    {
                        State heartbeat = state.Copy();
                        heartbeat.HeartbeatMs = now;
                        Save(heartbeat); // Rate-limit attempts, including failed heartbeats, across restarts.
                        await client.HeartbeatAsync(state.ReportId, ct).ConfigureAwait(false);
                    }
                }
                status(backpressure ? "ForeverLogs outbox is full; the log cursor is paused without dropping data." :
                    state.ReportId == null ? "ForeverLogs is connected; waiting for complete combat log lines." : "ForeverLogs is live; all queued segments are acknowledged.");
            }
            catch (InvalidDataException) { status("ForeverLogs saved data is invalid. Preserve the state and outbox for recovery; they have not been reset."); }
            catch (OperationCanceledException) { needsStatus = state.ReportId != null; throw; }
            catch (ForeverException error) { RemoteFailure(error); }
            catch (Exception) { status(failedStorage ? "ForeverLogs local storage failed. Pending data is preserved; restart after repairing storage." : "ForeverLogs is paused because its log or saved data could not be read safely. Pending data is preserved."); }
        }

        internal async Task FlushAsync(CancellationToken ct)
        {
            if (!Ready()) return;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!BindAccount()) return;
                if (!await DrainAsync(ct).ConfigureAwait(false)) return;
                if (state.ReportId == null) return;
                if (state.Finalizing || (state.Source != null && state.Source.Drained))
                {
                    if (await FinishAsync(ct).ConfigureAwait(false)) status("ForeverLogs report finalized.");
                }
                else status("ForeverLogs has an unread log tail; its report and pending data are preserved.");
            }
            catch (OperationCanceledException) { needsStatus = state.ReportId != null; throw; }
            catch (ForeverException error) { RemoteFailure(error); }
            catch (Exception) { status("ForeverLogs could not finish safely. Its report and pending data are preserved."); }
        }

        private bool Ready()
        {
            if (failedStorage) { status("ForeverLogs local storage failed. Pending data is preserved; restart after repairing storage."); return false; }
            if (!client.IsConnected) { status("Connect ForeverLogs to resume its preserved upload queue."); return false; }
            return true;
        }

        private bool BindAccount()
        {
            string account = client.AccountId;
            if (String.IsNullOrEmpty(account) || account.Length > 256) throw new InvalidDataException("ForeverLogs account identity is unavailable.");
            string owner = Json.Hash(Utf8.GetBytes(account));
            if (state.OwnerHash != null && state.OwnerHash != owner)
            {
                status("ForeverLogs is paused: reconnect the original account to resume its saved log and upload queue.");
                return false;
            }
            if (state.OwnerHash == null)
            {
                State bound = state.Copy();
                bound.OwnerHash = owner;
                Save(bound);
            }
            // Host need not scan while disconnected: a reconnect invalidates cached report liveness.
            long generation = client.ConnectionGeneration;
            if (generation != observedConnectionGeneration)
            {
                observedConnectionGeneration = generation;
                needsStatus = state.ReportId != null;
            }
            return true;
        }

        private void RemoteFailure(ForeverException error)
        {
            needsStatus = state.ReportId != null;
            if (error.Code == "log_line_too_long")
                status("ForeverLogs is paused: the log contains a line larger than 4 MiB. Restore a valid combat log; the cursor and pending data are preserved.");
            else if (error.Status == 401 || error.Status == 403)
                status("ForeverLogs authorization failed. Reconnect the original account; pending data is preserved.");
            else if (error.Status == 404 || error.Status == 410)
                status("ForeverLogs cannot resolve its saved report. Pending data is preserved; the report requires recovery, not automatic replay.");
            else status("ForeverLogs is unavailable. Its upload queue is preserved and will retry independently.");
        }

        private long Now()
        {
            long value = (utcNow().ToUniversalTime().Ticks - 621355968000000000L) / TimeSpan.TicksPerMillisecond;
            if (value <= 0 || value > Json.MaxInteger) throw new InvalidDataException("Invalid ForeverLogs clock.");
            return value;
        }

        private async Task<bool> DrainAsync(CancellationToken ct)
        {
            if (state.ReportId == null) return true;
            if (needsStatus)
            {
                Dictionary<string, object> remote = await client.StatusAsync(state.ReportId, ct).ConfigureAwait(false);
                if (Json.Str(Json.Get(Json.Obj(Json.Get(remote, "session")), "status")) != "live")
                {
                    status("ForeverLogs saved report is no longer live. Pending data is preserved; automatic replay into another report is blocked.");
                    return false;
                }
                needsStatus = false;
            }
            while (queue.Count != 0)
            {
                ct.ThrowIfCancellationRequested();
                Segment segment = queue[0];
                if (segment.Seq > state.AckSeq)
                {
                    if (segment.ReportId != state.ReportId || segment.Seq != state.AckSeq + 1) throw Corrupt();
                    byte[] gzip;
                    Segment disk = ReadSegment(segment.Name, out gzip);
                    if (!SameSegment(segment, disk)) throw Corrupt();
                    // The client recognizes only 2xx or out_of_order with expectedSeq > this seq as ACK.
                    // Retrying this exact file after a lost ACK never creates a different payload for the seq.
                    await client.SegmentAsync(state.ReportId, segment.Seq, gzip, ct).ConfigureAwait(false);
                    State acknowledged = state.Copy();
                    acknowledged.AckSeq = segment.Seq;
                    Save(acknowledged);
                }
                DeleteSegment(segment); // ACK checkpoint precedes deletion, never the reverse.
                queue.RemoveAt(0);
            }
            return true;
        }

        private async Task<bool> FinishAsync(CancellationToken ct)
        {
            if (state.ReportId == null) return true;
            if (!await DrainAsync(ct).ConfigureAwait(false)) return false;
            if (state.AckSeq != state.NextSeq - 1) throw Corrupt();
            if (!state.Finalizing)
            {
                State closing = state.Copy();
                closing.Finalizing = true;
                Save(closing);
            }
            // If this ACK is lost and status is subsequently non-live, retain the closure instead of
            // inventing a server state or silently reopening/replaying the report.
            await client.FinalizeAsync(state.ReportId, ct).ConfigureAwait(false);
            State closed = state.Copy();
            closed.ReportId = closed.Zone = closed.Visibility = null;
            closed.NextSeq = 1;
            closed.AckSeq = 0;
            closed.Day = 0;
            closed.Finalizing = false;
            closed.HeartbeatMs = 0;
            Save(closed);
            needsStatus = false;
            return true;
        }

        private void Observe(Source source)
        {
            if (SameSource(state.Source, source)) return;
            State observed = state.Copy();
            if (state.Source.ObservedLength != source.ObservedLength) observed.LastDataMs = Now();
            observed.Source = source;
            Save(observed);
        }

        private bool Enqueue(Batch batch)
        {
            if (queue.Count >= QueueCountLimit || state.NextSeq >= Json.MaxInteger) return false;
            byte[] gzip;
            using (MemoryStream target = new MemoryStream())
            {
                using (GZipStream compressor = new GZipStream(target, CompressionMode.Compress, true)) compressor.Write(batch.Bytes, 0, batch.Count);
                gzip = target.ToArray();
            }
            if (gzip.Length > GzipLimit) throw Corrupt();
            Segment segment = new Segment {
                OwnerHash = state.OwnerHash, ReportId = state.ReportId, Seq = state.NextSeq,
                StartOffset = state.Source.Offset, After = batch.After, RawLength = batch.Count,
                GzipLength = gzip.Length, LastDataMs = Now()
            };
            byte[] header = Utf8.GetBytes(Json.Stringify(SegmentJson(segment)));
            if (header.Length > HeaderLimit) throw Corrupt();
            byte[] bytes = new byte[Magic.Length + 4 + header.Length + gzip.Length];
            Buffer.BlockCopy(Magic, 0, bytes, 0, Magic.Length);
            int length = header.Length;
            for (int i = 0; i < 4; i++) bytes[Magic.Length + i] = (byte)(length >> (8 * i));
            Buffer.BlockCopy(header, 0, bytes, Magic.Length + 4, header.Length);
            Buffer.BlockCopy(gzip, 0, bytes, Magic.Length + 4 + header.Length, gzip.Length);
            segment.FileLength = bytes.Length;
            segment.Name = segment.ReportId + "-" + segment.Seq.ToString(Invariant) + "-" + Json.Hash(bytes) + ".segment";
            if (queueBytes > QueueLimit - bytes.Length) return false;
            try
            {
                Files.AtomicWrite(SegmentPath(segment.Name), bytes);
                // A self-describing journal recovers this write if the following checkpoint is interrupted.
                State queued = state.Copy();
                queued.Source = batch.After;
                queued.LastDataMs = segment.LastDataMs;
                queued.NextSeq++;
                Save(queued);
                queue.Add(segment);
                queueBytes += segment.FileLength;
            }
            catch { failedStorage = true; throw; }
            return true;
        }

        private void Recover(bool saved)
        {
            using (NativeGamePaths.Guard guard = NativeGamePaths.LockDirectory(outboxPath))
            {
                string[] paths = Directory.GetFileSystemEntries(outboxPath);
                if (paths.Length > QueueCountLimit + 1) throw Corrupt();
                foreach (string path in paths)
                {
                    Match temporary = TemporaryPattern.Match(Path.GetFileName(path));
                    if (temporary.Success && FilePattern.IsMatch(temporary.Groups[1].Value))
                        RecoverTemporary(path, temporary.Groups[1].Value);
                }
                // Re-enumerate because a complete interrupted write may now be its durable target.
                paths = Directory.GetFileSystemEntries(outboxPath);
                if (paths.Length > QueueCountLimit + 1) throw Corrupt();
                if (!saved && paths.Length != 0) throw Corrupt();
                foreach (string path in paths)
                {
                    string name = Path.GetFileName(path);
                    byte[] gzip;
                    Segment segment = ReadSegment(name, out gzip);
                    if (segment.OwnerHash != state.OwnerHash || segment.ReportId != state.ReportId) throw Corrupt();
                    queueBytes = checked(queueBytes + segment.FileLength);
                    if (queueBytes > QueueLimit) throw Corrupt();
                    queue.Add(segment);
                }
            }
            queue.Sort(delegate(Segment left, Segment right) { return left.Seq.CompareTo(right.Seq); });
            long previous = 0;
            foreach (Segment segment in queue)
            {
                if (segment.Seq == previous) throw Corrupt();
                previous = segment.Seq;
            }
            // Complete journals and their chain must validate before any checkpoint or ACK cleanup.
            State recovered = state.Copy();
            Source prior = null;
            long expected = state.AckSeq + 1;
            foreach (Segment segment in queue)
            {
                if (segment.Seq <= state.AckSeq) continue;
                if (segment.Seq != expected++) throw Corrupt();
                if (segment.Seq > state.NextSeq) throw Corrupt();
                if (prior != null && !Follows(prior, segment)) throw Corrupt();
                prior = segment.After;
                if (segment.Seq == state.NextSeq)
                {
                    if (state.Finalizing || !Follows(state.Source, segment)) throw Corrupt();
                    recovered.Source = segment.After;
                    recovered.NextSeq++;
                    recovered.LastDataMs = segment.LastDataMs;
                }
                else if (segment.Seq == state.NextSeq - 1 && !SameCursor(segment.After, state.Source)) throw Corrupt();
            }
            if (expected != recovered.NextSeq) throw Corrupt();
            if (recovered.NextSeq != state.NextSeq) Save(recovered);
            while (queue.Count > 0 && queue[0].Seq <= state.AckSeq)
            {
                DeleteSegment(queue[0]);
                queue.RemoveAt(0);
            }
        }

        private void RecoverTemporary(string path, string logicalName)
        {
            // Read through a no-link regular-file handle before deciding whether this exact
            // AtomicWrite temporary is an incomplete discard or the only durable segment copy.
            byte[] bytes = NativeGamePaths.ReadFile(path, Magic.Length + 4 + HeaderLimit + GzipLimit);
            Segment temporary;
            byte[] gzip;
            if (!TryParseSegment(logicalName, bytes, out temporary, out gzip))
            {
                Files.Delete(path);
                return;
            }
            string target = SegmentPath(logicalName);
            if (Exists(target))
            {
                Segment durable = ReadSegment(logicalName, out gzip);
                if (!SameSegment(temporary, durable)) throw Corrupt();
            }
            else
            {
                // The state still names the pre-enqueue cursor when publication was interrupted.
                // Never promote a valid but unrelated or already-checkpointed temporary.
                if (temporary.OwnerHash != state.OwnerHash || temporary.ReportId != state.ReportId ||
                    temporary.Seq != state.NextSeq || state.Finalizing || !Follows(state.Source, temporary))
                    throw Corrupt();
                Files.AtomicWrite(target, bytes);
                Segment durable = ReadSegment(logicalName, out gzip);
                if (!SameSegment(temporary, durable)) throw Corrupt();
            }
            Files.Delete(path);
        }

        private static bool Follows(Source before, Segment next)
        {
            return before != null && next.StartOffset == before.Offset && before.Identity == next.After.Identity && before.PathHash == next.After.PathHash;
        }

        private static bool SameCursor(Source left, Source right)
        {
            return left != null && right != null && left.PathHash == right.PathHash && left.Identity == right.Identity && left.Offset == right.Offset && left.AnchorBytes == right.AnchorBytes && left.AnchorHash == right.AnchorHash;
        }

        private static bool SameSource(Source left, Source right)
        {
            return SameCursor(left, right) && left.PrefixBytes == right.PrefixBytes && left.PrefixHash == right.PrefixHash && left.ObservedLength == right.ObservedLength && left.Drained == right.Drained;
        }

        private static bool SameSegment(Segment left, Segment right)
        {
            return left.Name == right.Name && left.OwnerHash == right.OwnerHash && left.ReportId == right.ReportId && left.Seq == right.Seq && left.StartOffset == right.StartOffset && left.RawLength == right.RawLength && left.GzipLength == right.GzipLength && left.LastDataMs == right.LastDataMs && SameSource(left.After, right.After);
        }

        private void DeleteSegment(Segment segment)
        {
            try { Files.Delete(SegmentPath(segment.Name)); queueBytes -= segment.FileLength; }
            catch { failedStorage = true; throw; }
        }

        private string SegmentPath(string name)
        {
            if (String.IsNullOrEmpty(name) || !FilePattern.IsMatch(name)) throw Corrupt();
            return Path.Combine(outboxPath, name);
        }

        private Segment ReadSegment(string name, out byte[] gzip)
        {
            byte[] bytes = NativeGamePaths.ReadFile(SegmentPath(name), Magic.Length + 4 + HeaderLimit + GzipLimit);
            return ParseSegment(name, bytes, out gzip);
        }

        private static bool TryParseSegment(string name, byte[] bytes, out Segment result, out byte[] gzip)
        {
            try
            {
                result = ParseSegment(name, bytes, out gzip);
                return true;
            }
            catch (InvalidDataException) { }
            catch (IOException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (FormatException) { }
            catch (OverflowException) { }
            result = null;
            gzip = null;
            return false;
        }

        private static Segment ParseSegment(string name, byte[] bytes, out byte[] gzip)
        {
            Match match = FilePattern.Match(name);
            if (!match.Success) throw Corrupt();
            if (bytes.Length < Magic.Length + 4 || Json.Hash(bytes) != match.Groups[3].Value) throw Corrupt();
            for (int i = 0; i < Magic.Length; i++) if (bytes[i] != Magic[i]) throw Corrupt();
            int headerLength = 0;
            for (int i = 0; i < 4; i++) headerLength |= bytes[Magic.Length + i] << (8 * i);
            if (headerLength <= 0 || headerLength > HeaderLimit || headerLength > bytes.Length - Magic.Length - 4) throw Corrupt();
            Dictionary<string, object> header = ParseObject(Utf8.GetString(bytes, Magic.Length + 4, headerLength));
            Keys(header, "version", "ownerHash", "reportId", "seq", "startOffset", "rawLength", "gzipLength", "lastDataMs", "source");
            if (Integer(header, "version", 1, 1) != 1) throw Corrupt();
            Segment result = new Segment {
                Name = name, FileLength = bytes.Length, OwnerHash = Text(header, "ownerHash"), ReportId = Text(header, "reportId"),
                Seq = Integer(header, "seq", 1, Json.MaxInteger - 1), StartOffset = Integer(header, "startOffset", 0, Json.MaxInteger),
                RawLength = (int)Integer(header, "rawLength", 1, RawLimit), GzipLength = (int)Integer(header, "gzipLength", 1, GzipLimit),
                LastDataMs = Integer(header, "lastDataMs", 1, Json.MaxInteger)
            };
            result.After = ParseSource(Object(header, "source"));
            if (!ValidDigest(result.OwnerHash) || !ValidReport(result.ReportId) || result.ReportId != match.Groups[1].Value || result.Seq.ToString(Invariant) != match.Groups[2].Value || result.StartOffset > Json.MaxInteger - result.RawLength || result.After.Offset != result.StartOffset + result.RawLength) throw Corrupt();
            int payload = Magic.Length + 4 + headerLength;
            if (bytes.Length - payload != result.GzipLength) throw Corrupt();
            gzip = new byte[result.GzipLength];
            Buffer.BlockCopy(bytes, payload, gzip, 0, gzip.Length);
            ValidateGzip(gzip, result.RawLength);
            return result;
        }

        private static void ValidateGzip(byte[] bytes, int expected)
        {
            using (MemoryStream input = new MemoryStream(bytes, false))
            using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                byte[] buffer = new byte[32768];
                char[] characters = new char[32770];
                Decoder decoder = Utf8.GetDecoder();
                int total = 0, last = -1, count;
                while ((count = gzip.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (count > expected - total) throw Corrupt();
                    decoder.GetChars(buffer, 0, count, characters, 0, false);
                    total += count;
                    last = buffer[count - 1];
                }
                decoder.GetChars(buffer, 0, 0, characters, 0, true);
                if (total != expected || last != 10) throw Corrupt();
            }
        }

        private void Save(State value)
        {
            try
            {
                Dictionary<string, object> json = StateJson(value);
                ParseState(json);
                byte[] bytes = Utf8.GetBytes(Json.Stringify(json));
                if (bytes.Length > HeaderLimit) throw Corrupt();
                Files.AtomicWrite(statePath, bytes);
                state = value;
            }
            catch { failedStorage = true; throw; }
        }

        private static Dictionary<string, object> SourceJson(Source source)
        {
            return new Dictionary<string, object> {
                { "pathHash", source.PathHash }, { "identity", source.Identity }, { "offset", source.Offset },
                { "observedLength", source.ObservedLength }, { "prefixBytes", source.PrefixBytes }, { "prefixHash", source.PrefixHash },
                { "anchorBytes", source.AnchorBytes }, { "anchorHash", source.AnchorHash }, { "drained", source.Drained }
            };
        }

        private static Dictionary<string, object> StateJson(State value)
        {
            return new Dictionary<string, object> {
                { "version", 1 }, { "ownerHash", value.OwnerHash }, { "source", value.Source == null ? null : SourceJson(value.Source) },
                { "reportId", value.ReportId }, { "lastReportId", value.LastReportId }, { "timezone", value.Zone }, { "visibility", value.Visibility },
                { "nextSeq", value.NextSeq }, { "ackSeq", value.AckSeq }, { "day", value.Day }, { "finalizing", value.Finalizing },
                { "lastDataMs", value.LastDataMs }, { "heartbeatMs", value.HeartbeatMs }
            };
        }

        private static Dictionary<string, object> SegmentJson(Segment value)
        {
            return new Dictionary<string, object> {
                { "version", 1 }, { "ownerHash", value.OwnerHash }, { "reportId", value.ReportId }, { "seq", value.Seq },
                { "startOffset", value.StartOffset }, { "rawLength", value.RawLength }, { "gzipLength", value.GzipLength },
                { "lastDataMs", value.LastDataMs }, { "source", SourceJson(value.After) }
            };
        }

        private static State ParseState(Dictionary<string, object> json)
        {
            Keys(json, "version", "ownerHash", "source", "reportId", "lastReportId", "timezone", "visibility", "nextSeq", "ackSeq", "day", "finalizing", "lastDataMs", "heartbeatMs");
            Integer(json, "version", 1, 1);
            State value = new State {
                OwnerHash = NullableText(json, "ownerHash"), ReportId = NullableText(json, "reportId"), LastReportId = NullableText(json, "lastReportId"),
                Zone = NullableText(json, "timezone"), Visibility = NullableText(json, "visibility"),
                NextSeq = Integer(json, "nextSeq", 1, Json.MaxInteger), AckSeq = Integer(json, "ackSeq", 0, Json.MaxInteger - 1),
                Day = (int)Integer(json, "day", 0, 1231), Finalizing = Boolean(json, "finalizing"),
                LastDataMs = Integer(json, "lastDataMs", 0, Json.MaxInteger), HeartbeatMs = Integer(json, "heartbeatMs", 0, Json.MaxInteger)
            };
            if (Json.Get(json, "source") != null) value.Source = ParseSource(Object(json, "source"));
            if (value.OwnerHash != null && !ValidDigest(value.OwnerHash)) throw Corrupt();
            if (value.LastReportId != null && !ValidReport(value.LastReportId)) throw Corrupt();
            if (value.LastReportId != null && (value.OwnerHash == null || value.Source == null)) throw Corrupt();
            if (value.Source != null && (value.OwnerHash == null || value.LastDataMs == 0)) throw Corrupt();
            if (value.Day != 0 && !ValidDay(value.Day)) throw Corrupt();
            if (value.ReportId == null)
            {
                if (value.NextSeq != 1 || value.AckSeq != 0 || value.Finalizing || value.Day != 0 || value.Zone != null || value.Visibility != null || value.HeartbeatMs != 0) throw Corrupt();
            }
            else if (!ValidReport(value.ReportId) || value.ReportId != value.LastReportId || value.Source == null || value.OwnerHash == null || !ValidZone(value.Zone) || (value.Visibility != "private" && value.Visibility != "public") || value.AckSeq >= value.NextSeq || (value.Finalizing && value.AckSeq != value.NextSeq - 1)) throw Corrupt();
            return value;
        }

        private static Source ParseSource(Dictionary<string, object> json)
        {
            Keys(json, "pathHash", "identity", "offset", "observedLength", "prefixBytes", "prefixHash", "anchorBytes", "anchorHash", "drained");
            Source value = new Source {
                PathHash = Text(json, "pathHash"), Identity = Text(json, "identity"), PrefixHash = Text(json, "prefixHash"), AnchorHash = Text(json, "anchorHash"),
                Offset = Integer(json, "offset", 0, Json.MaxInteger), ObservedLength = Integer(json, "observedLength", 0, Json.MaxInteger),
                PrefixBytes = (int)Integer(json, "prefixBytes", 0, PrefixLimit), AnchorBytes = (int)Integer(json, "anchorBytes", 0, PrefixLimit), Drained = Boolean(json, "drained")
            };
            if (!ValidDigest(value.PathHash) || value.Identity.Length > 80 || !IdentityPattern.IsMatch(value.Identity) || !ValidDigest(value.PrefixHash) || !ValidDigest(value.AnchorHash) || value.Offset > value.ObservedLength || value.PrefixBytes > value.ObservedLength || value.AnchorBytes != Math.Min(PrefixLimit, value.Offset) || (value.Drained && value.ObservedLength - value.Offset > RawLimit)) throw Corrupt();
            return value;
        }

        private static Dictionary<string, object> ParseObject(byte[] bytes) { return ParseObject(Utf8.GetString(bytes)); }
        private static Dictionary<string, object> ParseObject(string text)
        {
            Dictionary<string, object> value = Json.Parse(text) as Dictionary<string, object>;
            if (value == null) throw Corrupt();
            return value;
        }
        private static Dictionary<string, object> Object(Dictionary<string, object> value, string key)
        {
            Dictionary<string, object> result = Json.Get(value, key) as Dictionary<string, object>;
            if (result == null) throw Corrupt();
            return result;
        }
        private static void Keys(Dictionary<string, object> value, params string[] keys)
        {
            if (value.Count != keys.Length) throw Corrupt();
            foreach (string key in keys) if (!value.ContainsKey(key)) throw Corrupt();
        }
        private static string Text(Dictionary<string, object> value, string key)
        {
            string result = Json.Get(value, key) as string;
            if (result == null || result.Length > 256) throw Corrupt();
            return result;
        }
        private static string NullableText(Dictionary<string, object> value, string key) { return Json.Get(value, key) == null ? null : Text(value, key); }
        private static bool Boolean(Dictionary<string, object> value, string key)
        {
            object result = Json.Get(value, key);
            if (!(result is bool)) throw Corrupt();
            return (bool)result;
        }
        private static long Integer(Dictionary<string, object> value, string key, long min, long max)
        {
            object result = Json.Get(value, key);
            if (!Json.Integer(result, min, max)) throw Corrupt();
            return Json.Int(result);
        }
        private static bool ValidDigest(string value) { return value != null && value.Length == 64 && DigestPattern.IsMatch(value); }
        private static bool ValidReport(string value) { return value != null && value.Length <= 20 && ReportPattern.IsMatch(value); }
        private static bool ValidZone(string value) { return value != null && value.Length <= 128 && ZonePattern.IsMatch(value) && value != "." && value != ".."; }
        private static bool ValidDay(int value) { return value / 100 >= 1 && value / 100 <= 12 && value % 100 >= 1 && value % 100 <= DateTime.DaysInMonth(2000, value / 100); }
        private static InvalidDataException Corrupt() { return new InvalidDataException("ForeverLogs saved state or outbox is invalid; it has not been reset."); }
        private static bool Exists(string path)
        {
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private static HandleInfo Information(FileStream file)
        {
            HandleInfo info;
            if (!GetFileInformationByHandle(file.SafeFileHandle, out info)) throw new IOException("ForeverLogs cannot inspect the log handle.");
            if ((info.Attributes & (0x10U | 0x400U)) != 0 || info.Links != 1 || info.Length > Json.MaxInteger) throw new IOException("ForeverLogs requires a safe regular combat log.");
            return info;
        }
        private static bool SameFile(HandleInfo left, HandleInfo right)
        {
            return left.Identity == right.Identity && left.Length == right.Length && left.Modified == right.Modified;
        }
        private static FileStream OpenLog(string path)
        {
            // Share normal append writers, but not deletion; the directory guard pins every ancestor.
            SafeFileHandle handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error == 2 || error == 3) return null;
                throw new IOException("ForeverLogs cannot open the combat log safely.");
            }
            try { return new FileStream(handle, FileAccess.Read, 8192, false); }
            catch { handle.Dispose(); throw; }
        }
        private static byte[] ReadAt(FileStream file, long offset, int length)
        {
            file.Position = offset;
            byte[] bytes = new byte[length];
            int position = 0;
            while (position < length)
            {
                int count = file.Read(bytes, position, length - position);
                if (count == 0) return null;
                position += count;
            }
            return bytes;
        }
        private static string HashAt(FileStream file, long offset, int length)
        {
            byte[] bytes = ReadAt(file, offset, length);
            return bytes == null ? null : Json.Hash(bytes);
        }

        private static Batch ReadBatch(string path, Source current, int currentDay)
        {
            using (NativeGamePaths.Guard guard = NativeGamePaths.LockDirectory(Path.GetDirectoryName(path)))
            using (FileStream file = OpenLog(path))
            {
                if (file == null) return null;
                HandleInfo before = Information(file);
                string pathHash = Json.Hash(Utf8.GetBytes(path.ToUpperInvariant()));
                bool changed = current != null && (current.PathHash != pathHash || current.Identity != before.Identity || before.Length < current.ObservedLength || before.Length < current.Offset);
                if (current != null && !changed)
                {
                    changed = HashAt(file, 0, current.PrefixBytes) != current.PrefixHash || HashAt(file, current.Offset - current.AnchorBytes, current.AnchorBytes) != current.AnchorHash;
                }
                int prefixBytes = (int)Math.Min(PrefixLimit, before.Length);
                string prefixHash = HashAt(file, 0, prefixBytes);
                if (prefixHash == null) return null;
                Source source = current == null || changed ? new Source {
                    PathHash = pathHash, Identity = before.Identity, Offset = 0, ObservedLength = before.Length,
                    PrefixBytes = prefixBytes, PrefixHash = prefixHash, AnchorBytes = 0, AnchorHash = Json.Hash(new byte[0]), Drained = before.Length == 0
                } : current.Copy();
                if (changed)
                {
                    if (!SameFile(before, Information(file))) return null;
                    return new Batch { Before = source, Replaced = true };
                }
                int length = (int)Math.Min(RawLimit, before.Length - source.Offset);
                byte[] raw = ReadAt(file, source.Offset, length);
                if (raw == null) return null;
                int complete = 0;
                for (int i = raw.Length - 1; i >= 0; i--) if (raw[i] == 10) { complete = i + 1; break; }
                if (complete == 0 && raw.Length == RawLimit) throw new ForeverException("ForeverLogs log line exceeds the segment limit.", 0, "log_line_too_long");
                int selected = complete;
                int day = 0;
                int line = 0;
                for (int i = 0; i < complete; i++)
                {
                    if (raw[i] != 10) continue;
                    int candidate = ReadDay(raw, line, i - line);
                    if (candidate != 0)
                    {
                        if (day == 0) day = candidate;
                        else if (candidate != day) { selected = line; break; }
                    }
                    line = i + 1;
                }
                if (day == 0) day = currentDay;
                if (selected != 0) Utf8.GetCharCount(raw, 0, selected); // Validate without decoding/re-encoding the wire bytes.
                Source after = source.Copy();
                after.Offset += selected;
                after.ObservedLength = before.Length;
                after.PrefixBytes = prefixBytes;
                after.PrefixHash = prefixHash;
                after.AnchorBytes = (int)Math.Min(PrefixLimit, after.Offset);
                after.AnchorHash = HashAt(file, after.Offset - after.AnchorBytes, after.AnchorBytes);
                after.Drained = length == before.Length - source.Offset && selected == complete;
                if (after.AnchorHash == null || !SameFile(before, Information(file)) || HashAt(file, 0, prefixBytes) != prefixHash || !SameFile(before, Information(file))) return null;
                return new Batch { Before = source, After = after, Bytes = raw, Count = selected, Day = day };
            }
        }

        private static int ReadDay(byte[] bytes, int start, int length)
        {
            int end = start + length;
            if (length >= 3 && bytes[start] == 239 && bytes[start + 1] == 187 && bytes[start + 2] == 191) start += 3;
            int month = 0, day = 0, digits = 0;
            while (start < end && bytes[start] >= 48 && bytes[start] <= 57 && digits < 2) { month = month * 10 + bytes[start++] - 48; digits++; }
            if (digits == 0 || start >= end || bytes[start++] != 47) return 0;
            digits = 0;
            while (start < end && bytes[start] >= 48 && bytes[start] <= 57 && digits < 2) { day = day * 10 + bytes[start++] - 48; digits++; }
            int value = month * 100 + day;
            return digits != 0 && start < end && (bytes[start] == 32 || bytes[start] == 9) && ValidDay(value) ? value : 0;
        }
    }
}
