using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AurifyClient;

internal static class HostEfficiencyProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, Private).SetValue(target, value); }
    private static object Get(object target, string name) { return target.GetType().GetField(name, Private).GetValue(target); }
    private static object Call(object target, string name, params object[] args)
    {
        try { return target.GetType().GetMethod(name, Private).Invoke(target, args); }
        catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static HostContext Host(string root, string server)
    {
        // Avoid runtime discovery, the user-wide pipe and the live application's mutex.
        var host = (HostContext)FormatterServices.GetUninitializedObject(typeof(HostContext));
        var storage = new ClientStorage(root);
        Set(host, "storage", storage);
        Set(host, "stateLock", new object());
        Set(host, "stopping", new CancellationTokenSource());
        Set(host, "operations", new SemaphoreSlim(1, 1));
        Set(host, "generation", 1L);
        Set(host, "state", storage.LoadState());
        Set(host, "stateReady", true);
        Set(host, "configReady", true);
        Set(host, "config", ClientStorage.NormalizeConfig(new ClientConfig { serverUrl = server }));
        Set(host, "credentials", new ClientCredentials { deviceToken = new string('T', 43), sourceKey = Convert.ToBase64String(new byte[32]) });
        return host;
    }
    private static Dictionary<string, object> Snapshot(string hash)
    {
        return new Dictionary<string, object> { { "snapshots", new Dictionary<string, object> {
            { "activity:" + new string('a', 64), new Dictionary<string, object> { { "rawHash", hash }, { "payloadHash", hash } } }
        } } };
    }
    internal static async Task<object> Run(Dictionary<string, object> request)
    {
        string action = Json.Str(Json.Get(request, "action"));
        if (action == "views")
        {
            var done = new TaskCompletionSource<object>();
            var thread = new Thread(delegate() {
                try { done.SetResult(Views()); } catch (Exception error) { done.SetException(error); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await done.Task;
        }
        string server = Json.Str(Json.Get(request, "serverUrl"), "http://127.0.0.1:1");
        HostContext host = Host(Json.Str(Json.Get(request, "root")), server);
        var stopping = (CancellationTokenSource)Get(host, "stopping");
        var storage = (ClientStorage)Get(host, "storage");
        try
        {
            if (action == "addon-scheduling")
            {
                Set(host, "installation", GameTools.Verify(Json.Str(Json.Get(request, "game"))));
                Set(host, "nextUpdate", Int64.MaxValue);
                var checks = new List<object>();
                long firstDeadline = 0;
                foreach (long elapsed in new[] { 0L, 6L * 60 * 60 * 1000 + 1000, 24L * 60 * 60 * 1000 + 1000 })
                {
                    if (elapsed != 0) Set(host, "nextAddonCheck", firstDeadline - elapsed);
                    Call(host, "StartMaintenance");
                    Task pending = Get(host, "maintenanceTask") as Task;
                    if (pending != null)
                    {
                        await pending;
                        Call(host, "ApplyMaintenance");
                    }
                    if (elapsed == 0) firstDeadline = (long)Get(host, "nextAddonCheck");
                    checks.Add(new { started = pending != null, setup = Get(host, "addons") });
                }
                return checks;
            }
            if (action == "addon-retry")
            {
                Set(host, "installation", GameTools.Verify(Json.Str(Json.Get(request, "game"))));
                Set(host, "nextUpdate", Int64.MaxValue);
                Call(host, "StartMaintenance");
                Task pending = Get(host, "maintenanceTask") as Task;
                if (pending == null) throw new InvalidOperationException("Addon maintenance did not start.");
                await pending;
                Call(host, "ApplyMaintenance");
                return new {
                    delay = (long)Get(host, "nextAddonCheck") - Json.Now(),
                    activity = Get(host, "activity"),
                    setup = Get(host, "addons")
                };
            }
            if (action == "persistence")
            {
                var snapshots = Snapshot(new string('b', 64));
                var combat = new Dictionary<string, object> { { "combat", new Dictionary<string, object> {
                    { "current", null }, { "pendingClosures", new object[] { new Dictionary<string, object> {
                        { "streamId", "11111111-1111-4111-8111-111111111111" }, { "fileDate", "2026-10-26" }
                    } } }
                } } };
                await Task.WhenAll(Task.Run(delegate { Call(host, "CommitState", "snapshots", snapshots, 1L); }),
                    Task.Run(delegate { Call(host, "CommitState", "combat", combat, 1L); }));
                object merged = storage.LoadState();
                string failure = null;
                using (var locked = new FileStream(storage.StatePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    try { Call(host, "CommitState", "snapshots", Snapshot(new string('c', 64)), 1L); }
                    catch (IOException error) { failure = error.GetType().Name; }
                }
                object rolledBack = Json.Parse(Json.Stringify(Get(host, "state")));
                object durableAfterFailure = storage.LoadState();
                stopping.Cancel();
                Call(host, "CommitState", "snapshots", Snapshot(new string('d', 64)), 1L);
                string stale = null;
                try { Call(host, "CommitState", "snapshots", snapshots, 0L); }
                catch (OperationCanceledException error) { stale = error.GetType().Name; }
                return new { merged = merged, failedSave = failure, rolledBack = Json.Parse(Json.Stringify(rolledBack)),
                    durableAfterFailure = durableAfterFailure, drained = storage.LoadState(), stale = stale };
            }
            if (action != "scheduling") throw new ArgumentException("Unknown host efficiency action");
            var installation = GameTools.Verify(Json.Str(Json.Get(request, "game")));
            Set(host, "installation", installation);
            Call(host, "ResetAuratism");
            Type resultType = typeof(HostContext).GetNestedType("MaintenanceResult", BindingFlags.NonPublic);
            Type completionType = typeof(TaskCompletionSource<>).MakeGenericType(resultType);
            object completion = Activator.CreateInstance(completionType);
            var maintenance = (Task)completionType.GetProperty("Task").GetValue(completion, null);
            Set(host, "maintenanceTask", maintenance);
            Set(host, "maintenanceStopping", new CancellationTokenSource());
            Task snapshot = (Task)Call(host, "StartSnapshot", false);
            using (var control = new HttpClient())
            {
                using (var ready = await control.GetAsync(server + "/probe/wait/snapshot")) ready.EnsureSuccessStatusCode();
                await (Task)Call(host, "SyncAuratism", Path.Combine(installation.GameDir, "Logs", "WoWCombatLog.txt"));
                bool combatBeforeSnapshot = !snapshot.IsCompleted;
                Task draining = (Task)Call(host, "StopMaintenance");
                bool drainWaited = !draining.IsCompleted;
                object result = Activator.CreateInstance(resultType, true);
                Set(result, "Generation", 1L);
                Set(result, "Update", true);
                Set(result, "Failure", new OperationCanceledException());
                completionType.GetMethod("SetResult").Invoke(completion, new[] { result });
                await draining;
                using (var release = await control.GetAsync(server + "/probe/release/snapshot")) release.EnsureSuccessStatusCode();
                await snapshot;
                await (Task)Call(host, "StopSnapshots");
                return new { combatBeforeSnapshot = combatBeforeSnapshot, maintenanceDrainWaited = drainWaited,
                    state = storage.LoadState() };
            }
        }
        finally
        {
            stopping.Cancel();
            ((Task)Call(host, "StopMaintenance")).GetAwaiter().GetResult();
            ((Task)Call(host, "StopSnapshots")).GetAwaiter().GetResult();
            var synchronizer = Get(host, "snapshotSync") as IDisposable;
            if (synchronizer != null) synchronizer.Dispose();
            var portal = Get(host, "portal") as IDisposable;
            if (portal != null) portal.Dispose();
            stopping.Dispose();
            ((SemaphoreSlim)Get(host, "operations")).Dispose();
        }
    }
    private static object Views()
    {
        var host = (HostContext)FormatterServices.GetUninitializedObject(typeof(HostContext));
        using (var form = new SetupForm(host))
        {
            var queue = new HostViewQueue();
            Func<string, HostView> view = delegate(string text) {
                return new HostView(form, 0, "Synthetic game", "Not paired", "Synthetic addons", "Disconnected",
                    text, "private", false, false, false, false, new string[0]);
            };
            var label = (Label)Get(form, "activity");
            int changed = 0;
            label.TextChanged += delegate { changed++; };
            bool firstQueued = queue.Offer(view("first"));
            bool secondQueued = queue.Offer(view("latest"));
            HostView latest = queue.Take();
            latest.Apply(); queue.Complete(latest);
            string firstDisplay = label.Text;
            bool repeatedQueued = queue.Offer(view("latest"));
            queue.Offer(view("fails"));
            HostView failing = queue.Take();
            queue.Offer(view("recovered"));
            EventHandler reject = delegate { if (label.Text == "fails") throw new InvalidOperationException("Synthetic UI failure"); };
            label.TextChanged += reject;
            try { failing.Apply(); throw new Exception("Expected UI callback failure"); }
            catch (InvalidOperationException) { queue.Fail(failing); }
            label.TextChanged -= reject;
            HostView recovered = queue.Take();
            if (recovered == null) throw new Exception("Newest pending view was lost");
            recovered.Apply(); queue.Complete(recovered);
            return new { firstQueued = firstQueued, secondQueued = secondQueued, repeatedQueued = repeatedQueued,
                firstDisplay = firstDisplay, finalDisplay = label.Text, changes = changed };
        }
    }
}
