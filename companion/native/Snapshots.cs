using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AurifyClient
{
    internal sealed class SnapshotSynchronizer : IDisposable
    {
        private const long FullReconcileMilliseconds = 60 * 1000;
        private const long WatcherRetryMilliseconds = 60 * 1000;
        private const int WatcherDebounceMilliseconds = 250;
        private const int MaximumCachedFiles = 256;
        private const int MaximumWatcherHints = 256;
        private readonly PortalClient client;
        private readonly Dictionary<string, object> checkpoints;
        private readonly Action saveState;
        private readonly byte[] sourceKey;
        private readonly string guildName;
        private readonly Action<string, string> report;
        private readonly Func<long> clock;
        private readonly Func<string, StableFile> stableRead;
        private readonly Func<string, StableFileMetadata> stableMetadata;
        private readonly Func<string, Dictionary<string, object>> parseSaved;
        private readonly bool enableWatcher;
        private readonly object sync = new object();
        private readonly SemaphoreSlim scanGate = new SemaphoreSlim(1, 1);
        private readonly Timer watcherDebounce;
        private readonly Dictionary<string, CacheEntry> cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> pendingHints = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> dirtyHints = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher watcher;
        private string watcherRoot;
        private bool watcherFaulted;
        private long nextWatcherAttempt;
        private string cacheRoot;
        private string scanContext;
        private string accountContext;
        private long hintSequence;
        private long globalHint;
        private long pendingGlobalHint;
        private long watcherErrorVersion;
        private bool hasFullReconciliation;
        private long lastFullReconciliation;
        private bool disposed;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly string[] Kinds = { "professions", "overlord", "activity", "characters" };
        private static readonly string[] FileNames = { "ProfessionMaster.lua", "Overlord.lua", "Aurify.lua", "Aurify.lua" };

        private sealed class CheckpointBatch
        {
            private readonly Dictionary<string, object> checkpoints;
            private readonly Action saveState;
            private readonly Action<string, string> report;
            private readonly Dictionary<string, object> original = new Dictionary<string, object>(StringComparer.Ordinal);
            private readonly HashSet<string> missing = new HashSet<string>(StringComparer.Ordinal);
            private readonly List<string> touched = new List<string>();
            private readonly List<string> uploaded = new List<string>();

            internal CheckpointBatch(Dictionary<string, object> checkpoints, Action saveState, Action<string, string> report)
            {
                this.checkpoints = checkpoints;
                this.saveState = saveState;
                this.report = report;
            }

            internal void Set(string key, string kind, object value, bool changed)
            {
                if (!original.ContainsKey(key) && !missing.Contains(key))
                {
                    object previous;
                    if (checkpoints.TryGetValue(key, out previous)) original.Add(key, previous);
                    else missing.Add(key);
                    touched.Add(kind);
                }
                checkpoints[key] = value;
                if (changed) uploaded.Add(kind);
            }

            internal Exception Commit()
            {
                if (touched.Count == 0) return null;
                try { saveState(); }
                catch (Exception error)
                {
                    foreach (KeyValuePair<string, object> entry in original) checkpoints[entry.Key] = entry.Value;
                    foreach (string key in missing) checkpoints.Remove(key);
                    foreach (string kind in touched) report("snapshot-error", kind);
                    Clear();
                    return error;
                }
                foreach (string kind in uploaded) report("snapshot-uploaded", kind);
                Clear();
                return null;
            }

            private void Clear()
            {
                original.Clear();
                missing.Clear();
                touched.Clear();
                uploaded.Clear();
            }
        }

        private sealed class KindCache
        {
            internal StableFileMetadata Metadata;
            internal bool HasMetadata;
            internal string Context;
            internal long CleanVersion;
        }

        private sealed class CacheEntry
        {
            internal readonly Dictionary<string, KindCache> Kinds = new Dictionary<string, KindCache>(StringComparer.Ordinal);
        }

        private sealed class CacheUpdate
        {
            internal string Path;
            internal string Kind;
            internal StableFileMetadata Metadata;
            internal bool HasMetadata;
            internal string Context;
            internal long HintVersion;
        }

        private sealed class KindWork
        {
            internal string Kind;
            internal AddonInfo Addon;
            internal string Context;
        }

        private sealed class FileWork
        {
            internal string Path;
            internal string AccountIdentifier;
            internal StableFileMetadata ObservedMetadata;
            internal bool MetadataUncertain;
            internal long HintVersion;
            internal readonly List<KindWork> Kinds = new List<KindWork>();
            internal Task<StableFile> Read;
        }

        private sealed class FilePass
        {
            private readonly StableFile stable;
            private readonly Func<string, Dictionary<string, object>> parser;
            private bool parsed;
            private Dictionary<string, object> saved;
            private Exception parseError;

            internal FilePass(StableFile stable, Func<string, Dictionary<string, object>> parser)
            {
                this.stable = stable;
                this.parser = parser;
            }

            internal Dictionary<string, object> Saved()
            {
                if (!parsed)
                {
                    parsed = true;
                    try
                    {
                        string text = StrictUtf8.GetString(stable.Bytes);
                        if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
                        saved = parser(text);
                    }
                    catch (Exception error) { parseError = error; }
                }
                if (parseError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(parseError).Throw();
                return saved;
            }
        }

        private sealed class GameClientLookup
        {
            private readonly GameInstallation installation;
            private bool resolved;
            private Dictionary<string, object> value;

            internal GameClientLookup(GameInstallation installation) { this.installation = installation; }

            internal Dictionary<string, object> Value()
            {
                if (resolved) return value;
                resolved = true;
                try
                {
                    string gameRoot;
                    value = CharacterModelSynchronizer.InstalledClient(installation, out gameRoot);
                }
                catch (ModelFailure) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (DecoderFallbackException) { }
                catch (ArgumentException) { }
                catch (System.ComponentModel.Win32Exception) { }
                catch (System.Security.SecurityException) { }
                return value;
            }
        }

        public SnapshotSynchronizer(PortalClient client, Dictionary<string, object> state, Action saveState, string sourceKey, string guildName, Action<string, string> report)
            : this(client, state, saveState, sourceKey, guildName, report, Json.Now,
                delegate(string path) { return Files.StableRead(path); }, Files.StableMetadata, LuaData.Parse, true)
        {
        }

        internal SnapshotSynchronizer(PortalClient client, Dictionary<string, object> state, Action saveState, string sourceKey,
            string guildName, Action<string, string> report, Func<long> clock, Func<string, StableFile> stableRead,
            Func<string, StableFileMetadata> stableMetadata, Func<string, Dictionary<string, object>> parseSaved, bool enableWatcher)
        {
            if (client == null || state == null || saveState == null || clock == null || stableRead == null || stableMetadata == null || parseSaved == null)
                throw new ArgumentNullException();
            this.client = client;
            this.saveState = saveState;
            this.sourceKey = Convert.FromBase64String(sourceKey);
            if (this.sourceKey.Length != 32) throw new ArgumentException("Source key must contain 32 bytes");
            this.guildName = guildName ?? "AURA";
            this.report = report ?? delegate { };
            this.clock = clock;
            this.stableRead = stableRead;
            this.stableMetadata = stableMetadata;
            this.parseSaved = parseSaved;
            this.enableWatcher = enableWatcher;
            checkpoints = Json.Get(state, "snapshots") as Dictionary<string, object>;
            if (checkpoints == null) { checkpoints = Json.New(); state["snapshots"] = checkpoints; }
            watcherDebounce = new Timer(FlushWatcherHints, null, Timeout.Infinite, Timeout.Infinite);
        }

        internal static string DeriveSourceId(string key, string accountIdentifier)
        {
            byte[] bytes = Convert.FromBase64String(key);
            if (bytes.Length != 32) throw new ArgumentException("Source key must contain 32 bytes");
            return DeriveSourceId(bytes, accountIdentifier);
        }

        private static string DeriveSourceId(byte[] key, string accountIdentifier)
        {
            if (String.IsNullOrEmpty(accountIdentifier)) throw new ArgumentException("Account identifier is required");
            using (var hmac = new HMACSHA256(key))
                return Hex(hmac.ComputeHash(Encoding.UTF8.GetBytes(accountIdentifier.Normalize(NormalizationForm.FormKC))));
        }

        private static string Hex(byte[] bytes)
        {
            var result = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return result.ToString();
        }

        public void Invalidate()
        {
            lock (sync)
            {
                if (disposed) return;
                MarkAllDirtyLocked();
            }
        }

        public async Task ScanAsync(GameInstallation installation, CancellationToken ct)
        {
            if (installation == null) throw new ArgumentNullException("installation");
            await scanGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (sync) { if (disposed) throw new ObjectDisposedException("SnapshotSynchronizer"); }
                await ScanCoreAsync(installation, ct).ConfigureAwait(false);
            }
            finally { scanGate.Release(); }
        }

        private async Task ScanCoreAsync(GameInstallation installation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            long now = clock();
            string accountRoot = Path.GetFullPath(Path.Combine(installation.GameDir, "WTF", "Account"));
            FileSystemWatcher stale = PrepareContext(accountRoot, ContextFor(installation));
            if (stale != null) stale.Dispose();
            EnsureWatcher(Path.GetDirectoryName(accountRoot), now);
            string[] accounts;
            try
            {
                Files.NoLinks(accountRoot);
                accounts = Directory.GetDirectories(accountRoot);
            }
            catch (DirectoryNotFoundException) { ObserveAccounts(""); return; }
            catch (FileNotFoundException) { ObserveAccounts(""); return; }
            catch (UnauthorizedAccessException) { Invalidate(); throw; }
            catch (IOException) { Invalidate(); throw; }
            catch (System.Security.SecurityException) { Invalidate(); throw; }
            Array.Sort(accounts, delegate(string a, string b) {
                return CultureInfo.GetCultureInfo("en-US").CompareInfo.Compare(Path.GetFileName(a), Path.GetFileName(b), CompareOptions.None);
            });
            var accountSignature = new StringBuilder();
            foreach (string account in accounts) accountSignature.Append(Path.GetFileName(account)).Append('\0');
            ObserveAccounts(accountSignature.ToString());
            bool fullReconciliation = FullReconciliationDue(now);
            long watcherVersionAtStart;
            lock (sync) watcherVersionAtStart = watcherErrorVersion;
            PortalException firstError = null;
            bool completed = false;
            bool applied = false;
            CheckpointBatch batch = new CheckpointBatch(checkpoints, saveState, report);
            var updates = new List<CacheUpdate>();
            Exception commitError = null;
            var gameClient = new GameClientLookup(installation);
            try
            {
                foreach (string account in accounts)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if ((File.GetAttributes(account) & FileAttributes.ReparsePoint) != 0)
                        {
                            MarkAccountDirty(account);
                            continue;
                        }
                        Files.NoLinks(account);
                    }
                    catch (IOException) { MarkAccountDirty(account); continue; }
                    catch (UnauthorizedAccessException) { MarkAccountDirty(account); continue; }
                    catch (System.Security.SecurityException) { MarkAccountDirty(account); continue; }
                    Dictionary<string, FileWork> works = PrepareAccount(installation, account, fullReconciliation);
                    foreach (FileWork work in works.Values)
                        if (work.Kinds.Count != 0) work.Read = Task.Run(delegate { return stableRead(work.Path); });
                    var reads = new List<Task<StableFile>>();
                    foreach (FileWork work in works.Values) if (work.Read != null) reads.Add(work.Read);
                    try { await Task.WhenAll(reads).ConfigureAwait(false); }
                    catch (Exception) { }
                    ct.ThrowIfCancellationRequested();
                    foreach (FileWork work in works.Values)
                    {
                        if (work.Read == null) continue;
                        StableFile stable = null;
                        Exception readError = null;
                        try { stable = await work.Read.ConfigureAwait(false); }
                        catch (Exception error) { readError = error; }
                        if (stable == null)
                        {
                            // Absence is intentionally not cached: a newly-created SavedVariables file
                            // must be retried even if the watcher is unavailable. Uncertain failures also
                            // retain an explicit generation so matching old metadata can never suppress them.
                            if (readError != null || work.MetadataUncertain || work.ObservedMetadata != null)
                            {
                                MarkPathDirty(work.Path);
                                foreach (KindWork kind in work.Kinds) report("snapshot-error", kind.Kind);
                            }
                            continue;
                        }
                        StableFileMetadata readMetadata = stable.Metadata ?? new StableFileMetadata {
                            Identity = stable.Identity,
                            Length = stable.Bytes == null ? 0 : stable.Bytes.LongLength,
                            LastWriteTicks = (stable.LastWriteMs + 11644473600000L) * 10000L
                        };
                        FilePass pass = new FilePass(stable, parseSaved);
                        foreach (KindWork kind in work.Kinds)
                        {
                            ct.ThrowIfCancellationRequested();
                            Exception error = await ScanFileAsync(kind, work.AccountIdentifier, stable, readMetadata, pass,
                                gameClient, batch, work, updates, ct).ConfigureAwait(false);
                            if (error == null) continue;
                            PortalException portal = error as PortalException;
                            if (portal != null && firstError == null) firstError = portal;
                        }
                    }
                }
                completed = true;
            }
            finally
            {
                commitError = batch.Commit();
                if (completed && commitError == null) applied = ApplyUpdates(updates);
            }
            bool watcherFailedDuringScan;
            lock (sync) watcherFailedDuringScan = watcherErrorVersion > watcherVersionAtStart;
            if (fullReconciliation && completed && commitError == null && applied && !watcherFailedDuringScan)
                CompleteFullReconciliation(now);
            if (firstError != null) throw firstError;
            if (commitError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(commitError).Throw();
        }

        private Dictionary<string, FileWork> PrepareAccount(GameInstallation installation, string account, bool fullReconciliation)
        {
            var works = new Dictionary<string, FileWork>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Kinds.Length; i++)
            {
                AddonInfo addon;
                string addonKind = Kinds[i] == "characters" ? "activity" : Kinds[i];
                if (installation.Addons == null || !installation.Addons.TryGetValue(addonKind, out addon) || addon == null || addon.Status != "ready")
                    continue;
                string path = Path.GetFullPath(Path.Combine(account, "SavedVariables", FileNames[i]));
                FileWork work;
                if (!works.TryGetValue(path, out work))
                {
                    work = new FileWork { Path = path, AccountIdentifier = Path.GetFileName(account) };
                    try { work.ObservedMetadata = stableMetadata(path); }
                    catch (Exception) { work.MetadataUncertain = true; }
                    work.HintVersion = HintVersion(path);
                    works.Add(path, work);
                }
                string context = BaseContext(Kinds[i], addon);
                KindCache cached = CachedKind(path, Kinds[i]);
                if (fullReconciliation || work.MetadataUncertain || cached == null ||
                    cached.CleanVersion < work.HintVersion || !String.Equals(cached.Context, context, StringComparison.Ordinal) ||
                    !SameMetadata(cached.HasMetadata, cached.Metadata, work.ObservedMetadata))
                    work.Kinds.Add(new KindWork { Kind = Kinds[i], Addon = addon, Context = context });
            }
            return works;
        }

        private async Task<Exception> ScanFileAsync(KindWork kind, string accountIdentifier, StableFile stable,
            StableFileMetadata readMetadata, FilePass pass, GameClientLookup gameClient, CheckpointBatch batch,
            FileWork work, List<CacheUpdate> updates, CancellationToken ct)
        {
            try
            {
                string sourceId = DeriveSourceId(sourceKey, accountIdentifier);
                string checkpointKey = kind.Kind + ":" + sourceId;
                var previous = Json.Obj(Json.Get(checkpoints, checkpointKey));
                // Legacy Aurify saves have no character projection and need no game-client discovery.
                if (kind.Kind == "characters")
                {
                    Dictionary<string, object> parsed = pass.Saved();
                    if (!Json.Obj(Json.Get(parsed, "AurifyDB")).ContainsKey("characterSchemaVersion") &&
                        Characters.Normalize(parsed, Json.New()) == null)
                    {
                        updates.Add(NewUpdate(work, kind, readMetadata));
                        return null;
                    }
                }
                Dictionary<string, object> identity = kind.Kind == "characters" ? gameClient.Value() : null;
                var context = Json.New();
                context["kind"] = kind.Kind;
                context["addonVersion"] = kind.Addon.Version;
                context["interface"] = kind.Addon.Interface;
                context["guildName"] = kind.Kind == "professions" ? guildName : null;
                if (kind.Kind == "characters" && identity != null) context["gameClient"] = identity;
                byte[] contextBytes = Encoding.UTF8.GetBytes(Json.Stringify(context));
                string rawHash;
                using (var hash = SHA256.Create())
                {
                    hash.TransformBlock(contextBytes, 0, contextBytes.Length, contextBytes, 0);
                    byte[] separator = { 0 };
                    hash.TransformBlock(separator, 0, 1, separator, 0);
                    hash.TransformBlock(stable.Bytes, 0, stable.Bytes.Length, stable.Bytes, 0);
                    hash.TransformFinalBlock(new byte[0], 0, 0);
                    rawHash = Hex(hash.Hash);
                }
                if (Json.Str(Json.Get(previous, "rawHash")) == rawHash)
                {
                    updates.Add(NewUpdate(work, kind, readMetadata));
                    return null;
                }
                ct.ThrowIfCancellationRequested();
                long current = clock();
                long observedAt = Math.Min(stable.LastWriteMs >= 946684800000 ? stable.LastWriteMs : current, current);
                var source = Json.New();
                source["sourceId"] = sourceId;
                source["client"] = "forever";
                source["addonVersion"] = kind.Addon.Version;
                source["interface"] = kind.Addon.Interface;
                source["observedAt"] = observedAt;
                if (kind.Kind == "characters" && identity != null) source["gameClient"] = identity;
                Dictionary<string, object> saved = pass.Saved();
                Dictionary<string, object> payload;
                if (kind.Kind == "professions") payload = Professions.Normalize(saved, source, guildName);
                else if (kind.Kind == "overlord") payload = Overlord.Normalize(saved, source);
                else if (kind.Kind == "activity") payload = Activity.Normalize(saved, source);
                else if (kind.Kind == "characters") payload = Characters.Normalize(saved, source);
                else throw new ArgumentException("Unknown snapshot kind");
                if (payload == null)
                {
                    updates.Add(NewUpdate(work, kind, readMetadata));
                    return null;
                }
                var semanticSource = new Dictionary<string, object>(Json.Obj(Json.Get(payload, "source")), StringComparer.Ordinal);
                semanticSource["observedAt"] = 0;
                var semantic = new Dictionary<string, object>(payload, StringComparer.Ordinal);
                semantic["source"] = semanticSource;
                string payloadHash = Json.Hash(Encoding.UTF8.GetBytes(Json.Stringify(semantic)));
                bool changed = Json.Str(Json.Get(previous, "payloadHash")) != payloadHash;
                if (changed) await client.UploadSnapshotAsync(kind.Kind, payload, ct).ConfigureAwait(false);
                var checkpoint = Json.New();
                checkpoint["rawHash"] = rawHash;
                checkpoint["payloadHash"] = payloadHash;
                batch.Set(checkpointKey, kind.Kind, checkpoint, changed);
                updates.Add(NewUpdate(work, kind, readMetadata));
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                MarkPathDirty(work.Path);
                report("snapshot-error", kind.Kind);
                return error;
            }
        }

        private static CacheUpdate NewUpdate(FileWork work, KindWork kind, StableFileMetadata metadata)
        {
            return new CacheUpdate {
                Path = work.Path,
                Kind = kind.Kind,
                Metadata = metadata,
                HasMetadata = true,
                Context = kind.Context,
                HintVersion = work.HintVersion
            };
        }

        private static bool SameMetadata(bool hasMetadata, StableFileMetadata cached, StableFileMetadata current)
        {
            if (!hasMetadata) return false;
            if (cached == null || current == null) return cached == null && current == null;
            return cached.Length == current.Length && cached.LastWriteTicks == current.LastWriteTicks &&
                String.Equals(cached.Identity, current.Identity, StringComparison.Ordinal) &&
                String.Equals(cached.ParentIdentity, current.ParentIdentity, StringComparison.Ordinal);
        }

        private KindCache CachedKind(string path, string kind)
        {
            lock (sync)
            {
                CacheEntry entry;
                KindCache value;
                return cache.TryGetValue(path, out entry) && entry.Kinds.TryGetValue(kind, out value) ? value : null;
            }
        }

        private bool ApplyUpdates(List<CacheUpdate> updates)
        {
            bool allApplied = true;
            lock (sync)
            {
                if (disposed) return false;
                foreach (CacheUpdate update in updates)
                {
                    if (HintVersionLocked(update.Path) > update.HintVersion)
                    {
                        allApplied = false;
                        continue;
                    }
                    CacheEntry entry;
                    if (!cache.TryGetValue(update.Path, out entry))
                    {
                        if (cache.Count >= MaximumCachedFiles)
                        {
                            allApplied = false;
                            continue;
                        }
                        entry = new CacheEntry();
                        cache.Add(update.Path, entry);
                    }
                    entry.Kinds[update.Kind] = new KindCache {
                        Metadata = update.Metadata,
                        HasMetadata = update.HasMetadata,
                        Context = update.Context,
                        CleanVersion = update.HintVersion
                    };
                }
            }
            return allApplied;
        }

        private string BaseContext(string kind, AddonInfo addon)
        {
            return kind + "\0" + (addon.Version ?? "") + "\0" + addon.Interface.ToString(CultureInfo.InvariantCulture) +
                "\0" + (kind == "professions" ? guildName : "");
        }

        private string ContextFor(GameInstallation installation)
        {
            var value = new StringBuilder(guildName).Append('\0');
            foreach (string kind in new[] { "professions", "overlord", "activity" })
            {
                AddonInfo addon;
                value.Append(kind).Append('\0');
                if (installation.Addons != null && installation.Addons.TryGetValue(kind, out addon) && addon != null)
                    value.Append(addon.Status).Append('\0').Append(addon.Version).Append('\0')
                        .Append(addon.Interface.ToString(CultureInfo.InvariantCulture));
                value.Append('\0');
            }
            return value.ToString();
        }

        private FileSystemWatcher PrepareContext(string root, string context)
        {
            FileSystemWatcher stale = null;
            lock (sync)
            {
                if (!String.Equals(cacheRoot, root, StringComparison.OrdinalIgnoreCase))
                {
                    stale = watcher;
                    watcher = null;
                    watcherRoot = null;
                    watcherFaulted = false;
                    nextWatcherAttempt = 0;
                    cache.Clear();
                    pendingHints.Clear();
                    dirtyHints.Clear();
                    pendingGlobalHint = 0;
                    cacheRoot = root;
                    accountContext = null;
                    MarkAllDirtyLocked();
                }
                if (!String.Equals(scanContext, context, StringComparison.Ordinal))
                {
                    scanContext = context;
                    MarkAllDirtyLocked();
                }
            }
            return stale;
        }

        private void ObserveAccounts(string context)
        {
            lock (sync)
            {
                if (String.Equals(accountContext, context, StringComparison.Ordinal)) return;
                accountContext = context;
                cache.Clear();
                MarkAllDirtyLocked();
            }
        }

        private bool FullReconciliationDue(long now)
        {
            lock (sync)
            {
                return !hasFullReconciliation || now < lastFullReconciliation ||
                    now - lastFullReconciliation >= FullReconcileMilliseconds;
            }
        }

        private void CompleteFullReconciliation(long now)
        {
            lock (sync)
            {
                hasFullReconciliation = true;
                lastFullReconciliation = now;
            }
        }

        private void MarkAccountDirty(string account)
        {
            string savedVariables = Path.Combine(account, "SavedVariables");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string fileName in FileNames)
                paths.Add(Path.GetFullPath(Path.Combine(savedVariables, fileName)));
            foreach (string path in paths) MarkPathDirty(path);
        }

        private void MarkPathDirty(string path)
        {
            lock (sync)
            {
                if (disposed) return;
                long version = ++hintSequence;
                if (dirtyHints.Count >= MaximumWatcherHints && !dirtyHints.ContainsKey(path))
                {
                    globalHint = version;
                    dirtyHints.Clear();
                }
                else dirtyHints[path] = version;
            }
        }

        private long HintVersion(string path)
        {
            lock (sync) return HintVersionLocked(path);
        }

        private long HintVersionLocked(string path)
        {
            long result = Math.Max(globalHint, pendingGlobalHint), value;
            if (dirtyHints.TryGetValue(path, out value) && value > result) result = value;
            if (pendingHints.TryGetValue(path, out value) && value > result) result = value;
            return result;
        }

        private void MarkAllDirtyLocked()
        {
            globalHint = ++hintSequence;
        }

        private void QueueWatcherHint(string path, bool all)
        {
            lock (sync)
            {
                if (disposed) return;
                long version = ++hintSequence;
                if (all || pendingHints.Count >= MaximumWatcherHints)
                {
                    pendingGlobalHint = version;
                    pendingHints.Clear();
                }
                else pendingHints[path] = version;
                try { watcherDebounce.Change(WatcherDebounceMilliseconds, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
            }
        }

        private void FlushWatcherHints(object state)
        {
            lock (sync)
            {
                if (disposed) return;
                if (pendingGlobalHint != 0)
                {
                    // A directory hint subsumes every later file hint in this debounce window.
                    globalHint = hintSequence;
                    pendingGlobalHint = 0;
                    pendingHints.Clear();
                }
                foreach (KeyValuePair<string, long> hint in pendingHints)
                {
                    long previous;
                    if (dirtyHints.Count >= MaximumWatcherHints && !dirtyHints.ContainsKey(hint.Key))
                    {
                        globalHint = hintSequence;
                        dirtyHints.Clear();
                        break;
                    }
                    if (!dirtyHints.TryGetValue(hint.Key, out previous) || previous < hint.Value) dirtyHints[hint.Key] = hint.Value;
                }
                pendingHints.Clear();
            }
        }

        private void WatcherChanged(object sender, FileSystemEventArgs args) { NoticeWatcherPath(args.FullPath); }
        private void WatcherRenamed(object sender, RenamedEventArgs args)
        {
            NoticeWatcherPath(args.OldFullPath);
            NoticeWatcherPath(args.FullPath);
        }

        private void NoticeWatcherPath(string path)
        {
            string root;
            lock (sync)
            {
                if (disposed) return;
                root = cacheRoot;
            }
            if (String.IsNullOrEmpty(root) || String.IsNullOrEmpty(path)) return;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { QueueWatcherHint(null, true); return; }
            bool all = String.Equals(full, root, StringComparison.OrdinalIgnoreCase);
            if (!all)
            {
                string prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                    ? root : root + Path.DirectorySeparatorChar;
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
                string relative = full.Substring(prefix.Length);
                string[] parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length <= 2) all = parts.Length == 1 ||
                    (parts.Length == 2 && String.Equals(parts[1], "SavedVariables", StringComparison.OrdinalIgnoreCase));
                else if (parts.Length != 3 || !String.Equals(parts[1], "SavedVariables", StringComparison.OrdinalIgnoreCase) ||
                    (Array.IndexOf(FileNames, parts[2]) < 0 &&
                     !String.Equals(parts[2], "ProfessionMaster.lua", StringComparison.OrdinalIgnoreCase) &&
                     !String.Equals(parts[2], "Overlord.lua", StringComparison.OrdinalIgnoreCase) &&
                     !String.Equals(parts[2], "Aurify.lua", StringComparison.OrdinalIgnoreCase)))
                    return;
            }
            QueueWatcherHint(all ? null : full, all);
        }

        private void WatcherError(object sender, ErrorEventArgs args)
        {
            lock (sync)
            {
                if (disposed || !Object.ReferenceEquals(sender, watcher)) return;
                watcherFaulted = true;
                watcherErrorVersion = ++hintSequence;
                globalHint = watcherErrorVersion;
                nextWatcherAttempt = 0;
            }
        }

        private void EnsureWatcher(string root, long now)
        {
            if (!enableWatcher || String.IsNullOrEmpty(root)) return;
            FileSystemWatcher stale = null;
            bool retryLater = false;
            lock (sync)
            {
                if (disposed) return;
                if (watcherFaulted)
                {
                    stale = watcher;
                    watcher = null;
                    watcherRoot = null;
                    watcherFaulted = false;
                }
                if (watcher != null && String.Equals(watcherRoot, root, StringComparison.OrdinalIgnoreCase)) return;
                if (watcher != null)
                {
                    if (stale == null) stale = watcher;
                    watcher = null;
                    watcherRoot = null;
                }
                if (now < nextWatcherAttempt) retryLater = true;
                else nextWatcherAttempt = now > long.MaxValue - WatcherRetryMilliseconds
                    ? long.MaxValue : now + WatcherRetryMilliseconds;
            }
            if (stale != null) stale.Dispose();
            if (retryLater) return;
            FileSystemWatcher created = null;
            try
            {
                created = new FileSystemWatcher(root, "*") {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 8192,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                        NotifyFilters.Size | NotifyFilters.CreationTime
                };
                created.Changed += WatcherChanged;
                created.Created += WatcherChanged;
                created.Deleted += WatcherChanged;
                created.Renamed += WatcherRenamed;
                created.Error += WatcherError;
                lock (sync)
                {
                    if (disposed || watcher != null || !String.Equals(Path.GetDirectoryName(cacheRoot), root, StringComparison.OrdinalIgnoreCase))
                    {
                        created.Dispose();
                        return;
                    }
                    watcher = created;
                    watcherRoot = root;
                }
                created.EnableRaisingEvents = true;
            }
            catch (IOException) { WatcherSetupFailed(created); }
            catch (UnauthorizedAccessException) { WatcherSetupFailed(created); }
            catch (ArgumentException) { WatcherSetupFailed(created); }
            catch (InvalidOperationException) { WatcherSetupFailed(created); }
            catch (System.Security.SecurityException) { WatcherSetupFailed(created); }
        }

        private void WatcherSetupFailed(FileSystemWatcher created)
        {
            lock (sync)
            {
                if (Object.ReferenceEquals(watcher, created))
                {
                    watcher = null;
                    watcherRoot = null;
                }
            }
            if (created != null) created.Dispose();
        }

        public void Dispose()
        {
            FileSystemWatcher stale;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                stale = watcher;
                watcher = null;
                watcherRoot = null;
                cache.Clear();
                pendingHints.Clear();
                dirtyHints.Clear();
            }
            if (stale != null) stale.Dispose();
            watcherDebounce.Dispose();
        }
    }
}
