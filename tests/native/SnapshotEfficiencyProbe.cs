using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AurifyClient;

internal static class SnapshotEfficiencyProbe
{
    internal static async Task<object> Run(Dictionary<string, object> request)
    {
        string root = Json.Str(Json.Get(request, "root"));
        string server = Json.Str(Json.Get(request, "serverUrl")).TrimEnd('/');
        string sourceKey = Json.Str(Json.Get(request, "sourceKey"));
        string guildName = Json.Str(Json.Get(request, "guildName"), "AURA");
        string account = Json.Str(Json.Get(request, "account"), "SYNTHETIC_ACCOUNT");
        bool enableWatcher = Json.Bool(Json.Get(request, "enableWatcher"));
        long now = Json.Int(Json.Get(request, "now"));
        Dictionary<string, object> state = Json.Obj(Json.Get(request, "state"));
        Dictionary<string, object> durable = Copy(state);
        GameInstallation installation = GameTools.Verify(root);
        var results = new List<object>();
        var checkpoints = new List<object>();
        var reports = new List<object>();
        int saveCalls = 0;
        int clockCalls = 0;
        int readCalls = 0;
        int metadataCalls = 0;
        int parseCalls = 0;
        bool failSave = false;

        Action save = delegate {
            saveCalls++;
            if (failSave) throw new IOException("Synthetic durable save failure.");
            durable = Copy(state);
            checkpoints.Add(Copy(durable));
        };
        Action<string, string> report = delegate(string name, string value) {
            reports.Add(new { name = name, value = value });
        };
        Func<long> clock = delegate {
            Interlocked.Increment(ref clockCalls);
            return now;
        };
        Func<string, StableFile> stableRead = delegate(string path) {
            Interlocked.Increment(ref readCalls);
            return Files.StableRead(path);
        };
        Func<string, StableFileMetadata> stableMetadata = delegate(string path) {
            Interlocked.Increment(ref metadataCalls);
            return Files.StableMetadata(path);
        };
        Func<string, Dictionary<string, object>> parseSaved = delegate(string text) {
            Interlocked.Increment(ref parseCalls);
            return LuaData.Parse(text);
        };

        using (var portal = new PortalClient(server, Json.Str(Json.Get(request, "token"))))
        using (var control = new HttpClient())
        {
            SnapshotSynchronizer synchronizer = null;
            try
            {
                int index = 0;
                foreach (object raw in Json.Items(Json.Get(request, "steps")))
                {
                    Dictionary<string, object> step = Json.Obj(raw);
                    int stepIndex = index++;
                    using (HttpResponseMessage marker = await control.GetAsync(server + "/probe/step/" + stepIndex).ConfigureAwait(false))
                        marker.EnsureSuccessStatusCode();

                    now += Json.Int(Json.Get(step, "advance"));
                    Rewrite(root, account, Json.Obj(Json.Get(step, "write")), Json.Bool(Json.Get(step, "restoreMtime")));
                    if (Json.Bool(Json.Get(step, "restart")))
                    {
                        if (synchronizer != null) synchronizer.Dispose();
                        synchronizer = null;
                        state = Copy(durable);
                        installation = GameTools.Verify(root);
                    }
                    ApplyVersions(installation, Json.Obj(Json.Get(step, "versions")));
                    if (synchronizer == null)
                        synchronizer = new SnapshotSynchronizer(portal, state, save, sourceKey, guildName, report,
                            clock, stableRead, stableMetadata, parseSaved, enableWatcher);
                    if (Json.Bool(Json.Get(step, "watcherOverflow"))) RaiseWatcherOverflow(synchronizer);
                    if (Json.Bool(Json.Get(step, "invalidate"))) synchronizer.Invalidate();

                    saveCalls = 0;
                    clockCalls = 0;
                    readCalls = 0;
                    metadataCalls = 0;
                    parseCalls = 0;
                    failSave = Json.Bool(Json.Get(step, "failSave"));
                    checkpoints = new List<object>();
                    reports = new List<object>();
                    Exception failure = null;
                    using (var cancellation = new CancellationTokenSource())
                    {
                        if (Json.Bool(Json.Get(step, "cancel"))) cancellation.Cancel();
                        Task scan = synchronizer.ScanAsync(installation, cancellation.Token);
                        string invalidateKind = Json.Str(Json.Get(step, "invalidateDuringUpload"));
                        string cancelKind = Json.Str(Json.Get(step, "cancelDuringUpload"));
                        string barrierKind = invalidateKind ?? cancelKind;
                        if (barrierKind != null)
                        {
                            string barrier = stepIndex + "/" + Uri.EscapeDataString(barrierKind);
                            using (HttpResponseMessage ready = await control.GetAsync(server + "/probe/wait/" + barrier).ConfigureAwait(false))
                                ready.EnsureSuccessStatusCode();
                            if (invalidateKind != null)
                            {
                                Rewrite(root, account, Json.Obj(Json.Get(step, "duringWrite")),
                                    Json.Bool(Json.Get(step, "duringRestoreMtime")));
                                synchronizer.Invalidate();
                            }
                            if (cancelKind != null) cancellation.Cancel();
                            using (HttpResponseMessage release = await control.GetAsync(server + "/probe/release/" + barrier).ConfigureAwait(false))
                                release.EnsureSuccessStatusCode();
                        }
                        try { await scan.ConfigureAwait(false); }
                        catch (Exception error) { failure = error; }
                    }
                    results.Add(new {
                        state = Copy(state),
                        durable = Copy(durable),
                        checkpoints = checkpoints,
                        reports = reports,
                        saveCalls = saveCalls,
                        clockCalls = clockCalls,
                        readCalls = readCalls,
                        metadataCalls = metadataCalls,
                        parseCalls = parseCalls,
                        error = failure == null ? null : failure.GetType().Name
                    });
                }
            }
            finally
            {
                if (synchronizer != null) synchronizer.Dispose();
            }
        }
        return results;
    }

    private static Dictionary<string, object> Copy(Dictionary<string, object> value)
    {
        return Json.Obj(Json.Parse(Json.Stringify(value)));
    }

    private static void ApplyVersions(GameInstallation installation, Dictionary<string, object> versions)
    {
        foreach (KeyValuePair<string, object> entry in versions)
        {
            AddonInfo addon;
            if (!installation.Addons.TryGetValue(entry.Key, out addon) || addon == null)
                throw new InvalidOperationException("Unknown synthetic addon context.");
            addon.Version = Json.Str(entry.Value);
        }
    }

    private static void Rewrite(string root, string account, Dictionary<string, object> files, bool restoreMtime)
    {
        foreach (KeyValuePair<string, object> entry in files)
        {
            string name;
            if (String.Equals(entry.Key, "professions", StringComparison.OrdinalIgnoreCase)) name = "ProfessionMaster.lua";
            else if (String.Equals(entry.Key, "overlord", StringComparison.OrdinalIgnoreCase)) name = "Overlord.lua";
            else if (String.Equals(entry.Key, "activity", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(entry.Key, "characters", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(entry.Key, "aurify", StringComparison.OrdinalIgnoreCase)) name = "Aurify.lua";
            else throw new InvalidOperationException("Unknown synthetic SavedVariables file.");
            string path = Path.Combine(root, "WTF", "Account", account, "SavedVariables", name);
            DateTime previous = File.GetLastWriteTimeUtc(path);
            File.WriteAllBytes(path, Convert.FromBase64String(Json.Str(entry.Value)));
            File.SetLastWriteTimeUtc(path, restoreMtime ? previous : previous.AddSeconds(1));
        }
    }

    private static void RaiseWatcherOverflow(SnapshotSynchronizer synchronizer)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo watcherField = typeof(SnapshotSynchronizer).GetField("watcher", flags);
        MethodInfo errorMethod = typeof(SnapshotSynchronizer).GetMethod("WatcherError", flags);
        object watcher = watcherField == null ? null : watcherField.GetValue(synchronizer);
        if (watcher == null || errorMethod == null)
            throw new InvalidOperationException("Synthetic snapshot watcher is unavailable.");
        errorMethod.Invoke(synchronizer, new object[] {
            watcher, new ErrorEventArgs(new InternalBufferOverflowException("Synthetic watcher overflow."))
        });
    }
}
