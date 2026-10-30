using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AurifyClient
{
    internal static class NativeHost
    {
        internal static int Run(string root, bool background)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (HostContext context = new HostContext(root, background)) Application.Run(context);
            return 0;
        }
    }

    internal sealed class HostContext : ApplicationContext
    {
        private const long AddonRetryMs = 15L * 60 * 1000;
        private volatile SetupForm form;
        private readonly Control dispatcher;
        private volatile string[] discoveryCandidates = new string[0];
        private readonly NotifyIcon tray;
        private readonly Icon ownedIcon;
        private readonly ClientStorage storage;
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private readonly SemaphoreSlim operations = new SemaphoreSlim(1, 1);
        private readonly TaskCompletionSource<bool> initialized = new TaskCompletionSource<bool>();
        private readonly object pipeLock = new object();
        private readonly object stateLock = new object();
        private readonly object commandLock = new object();
        private readonly HashSet<Task> commands = new HashSet<Task>();
        private readonly HostViewQueue views = new HostViewQueue();
        private int viewEpoch;
        private long generation;
        private Task<Exception> snapshotTask;
        private CancellationTokenSource snapshotStopping;
        private Task<MaintenanceResult> maintenanceTask;
        private CancellationTokenSource maintenanceStopping;
        private UpdateCatalog updateCatalog;
        private UpdateRelease latestUpdate;
        private bool updatesRequested;
        private long notifiedUpdateSequence;
        private bool addonsRequested;
        private long nextProcessCheck;
        private readonly string pipeName;
        private readonly string controlToken;
        private NamedPipeServerStream activePipe;
        private Task pipeTask;
        private Task loopTask;
        private Task modelTask;
        private CancellationTokenSource modelStopping;
        private long nextModelScan;
        private int modelRequested;
        private volatile string modelMessage;
        private bool exiting;
        private bool stateReady;
        private bool configReady;
        private ClientConfig config;
        private ClientCredentials credentials;
        private Dictionary<string, object> state;
        private GameInstallation installation;
        private PortalClient portal;
        private SnapshotSynchronizer snapshotSync;
        private CombatSynchronizer combatSync;
        private AddonSetup addons;
        private ForeverClient forever;
        private ForeverSynchronizer foreverSync;
        private string foreverMessage = "Not connected. Connect ForeverLogs once to enable direct uploads from Aurify.";
        private string foreverReportUrl;
        private PairingRequest pairing;
        private long pairingExpires;
        private long nextPoll;
        private long nextScan;
        private long nextUpdate = Json.Now() + 60000;
        private long nextAddonCheck;
        private volatile bool runtimeReady;
        private string pairingOrigin;
        private string activity = "Starting Aurify...";
        private string pairingMessage = "Not paired. Choose Pair with Discord to explicitly approve this device.";
        private string discoveryMessage = "Looking for WoW Forever. You can choose its folder at any time.";

        internal HostContext(string root, bool background)
        {
            storage = new ClientStorage(root);
            pipeName = "Aurify-" + Aurify.RuntimeKey();
            byte[] token = new byte[32];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(token);
            controlToken = Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            ownedIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (ownedIcon == null) throw new InvalidOperationException("The Aurify executable icon could not be loaded.");
            ContextMenuStrip menu = null;
            bool controlTouched = false;
            try
            {
                // Background sync needs one STA dispatch handle, not the settings
                // form's full control/font/layout tree. Allocate that UI on demand.
                dispatcher = new Control { Visible = false };
                IntPtr handle = dispatcher.Handle;
                menu = new ContextMenuStrip();
                menu.Items.Add("Open Aurify", null, delegate { ShowSetup(); });
                menu.Items.Add("Exit Aurify", null, delegate { Shutdown(); });
                tray = new NotifyIcon();
                tray.Icon = ownedIcon;
                tray.Text = "Aurify - guild sync";
                tray.ContextMenuStrip = menu;
                tray.Visible = true;
                tray.DoubleClick += delegate { ShowSetup(); };
                if (!background) ShowSetup();
                controlTouched = true;
                storage.WriteControl(controlToken);
                // Bind before readiness can be acknowledged; access is limited to this Windows user.
                activePipe = CreatePipe();
                pipeTask = Task.Run((Func<Task>)ServePipe);
                loopTask = Task.Run((Func<Task>)RunController);
            }
            catch
            {
                if (tray != null) tray.Dispose();
                if (menu != null) menu.Dispose();
                if (form != null) form.Dispose();
                if (dispatcher != null) dispatcher.Dispose();
                ownedIcon.Dispose();
                if (controlTouched) storage.RemoveControl();
                throw;
            }
        }

        internal void ShowSetup()
        {
            if (exiting) return;
            if (form == null)
            {
                SetupForm created = new SetupForm(this);
                try
                {
                    created.Icon = ownedIcon;
                    created.SetCandidates(discoveryCandidates);
                    created.SetUpdate(latestUpdate, updateCatalog != null);
                    created.FormClosing += delegate(object sender, FormClosingEventArgs args) {
                        if (!exiting && args.CloseReason == CloseReason.UserClosing) { args.Cancel = true; created.Hide(); }
                        else if (!exiting) { args.Cancel = true; Shutdown(); }
                    };
                    created.VisibleChanged += delegate {
                        Interlocked.Increment(ref viewEpoch);
                        Publish();
                    };
                    form = created;
                }
                catch { created.Dispose(); throw; }
                Publish();
            }
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
            Interlocked.Increment(ref viewEpoch);
            Publish();
        }

        private void OnUi(Action action)
        {
            if (dispatcher == null || dispatcher.IsDisposed) return;
            try { dispatcher.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        private void Publish()
        {
            if (form == null) return;
            string game = installation == null ? discoveryMessage : "Verified game folder: " + installation.GameDir;
            string approval = credentials == null ? pairingMessage : "Connected to Auratism. Your approval is saved securely for this Windows account.";
            string addonText = "Required addons will be checked after a verified game folder is selected.";
            if (addons != null)
            {
                StringBuilder text = new StringBuilder("Addons: ");
                foreach (KeyValuePair<string, AddonResult> result in addons.Results)
                    text.Append(result.Key).Append(": ").Append(result.Value.Status).Append(". ").Append(result.Value.Message).AppendLine();
                if (addons.Updates.Count > 0)
                {
                    text.AppendLine("Automatic addon updates — checked on startup and every 24 hours:");
                    foreach (KeyValuePair<string, AddonUpdateResult> update in addons.Updates)
                        text.Append(update.Key == "professions" ? "ProfessionMaster" : "Overlord Forever")
                            .Append(": ").Append(update.Value.Message).AppendLine();
                }
                if (addons.RestartRequired) text.Append("Restart WoW to load the installed addons. This notice remains until WoW stops.");
                addonText = text.ToString();
            }
            string providerStatus = foreverMessage;
            string visibility = config == null ? "private" : config.foreverVisibility;
            bool providerReady = forever != null && foreverSync != null;
            bool connected = providerReady && forever.IsConnected;
            bool pending = providerReady && forever.LoginPending;
            string reportUrl = foreverReportUrl;
            string status = activity + (String.IsNullOrEmpty(modelMessage) ? "" : Environment.NewLine + modelMessage);
            HostView view = new HostView(form, Interlocked.CompareExchange(ref viewEpoch, 0, 0),
                game, approval, addonText, providerStatus, status, visibility,
                providerReady, connected, pending, reportUrl != null, discoveryCandidates);
            if (!views.Offer(view)) return;
            try { dispatcher.BeginInvoke((Action)DrainView); }
            catch (InvalidOperationException) { views.Reset(); }
        }

        private void DrainView()
        {
            HostView view = views.Take();
            if (view == null) return;
            try
            {
                if (view.Apply()) views.Complete(view);
                else views.Fail(view);
            }
            catch { views.Fail(view); throw; }
        }

        internal void Notify(string text)
        {
            // Picker messages are immediate and do not wait behind discovery/network work.
            if (form != null && !form.IsDisposed) form.SetActivity(text);
        }

        private async Task Serialized(Func<Task> operation)
        {
            await operations.WaitAsync(stopping.Token).ConfigureAwait(false);
            try { stopping.Token.ThrowIfCancellationRequested(); await operation().ConfigureAwait(false); }
            finally { operations.Release(); }
        }

        private void Queue(Func<Task> operation, Func<Task> completion = null)
        {
            lock (commandLock)
            {
                if (exiting) return;
                Task command = Task.Run(async delegate {
                    try
                    {
                        await initialized.Task.ConfigureAwait(false);
                        await Serialized(operation).ConfigureAwait(false);
                        if (completion != null) await completion().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception error) { activity = Describe(error); }
                    string result = activity;
                    OnUi(delegate { Notify(result); });
                    Publish();
                });
                commands.Add(command);
                command.ContinueWith(delegate(Task completed) {
                    lock (commandLock) commands.Remove(completed);
                    // Observe even a failure in dispatch/publication during shutdown.
                    if (completed.IsFaulted) { var ignored = completed.Exception; }
                }, TaskScheduler.Default);
            }
        }

        private static string Describe(Exception error)
        {
            PortalException portal = error as PortalException;
            if (portal != null)
            {
                if (portal.Status == 401 || portal.Status == 403) return "Auratism did not authorize this device or guild role. Check your membership and device approval on auratism.com/members; your saved connection was kept.";
                if (portal.Status == 429) return "Auratism is busy. Aurify will retry automatically.";
                return "The upload request did not complete. Check your connection; saved data and sync progress were kept.";
            }
            if (error is UnauthorizedAccessException) return "Windows denied access. Check folder permissions; existing files and settings were kept.";
            if (error is IOException) return "A local file is unavailable or busy. Aurify will retry without advancing unacknowledged data.";
            return "This operation could not finish (" + error.GetType().Name + "). Existing data was kept. Choose a verified game folder or retry the action.";
        }

        private async Task RunController()
        {
            try
            {
                try { await Serialized(Initialize).ConfigureAwait(false); }
                finally { initialized.TrySetResult(true); }
                while (!stopping.IsCancellationRequested)
                {
                    try
                    {
                        await Serialized(async delegate {
                            await PollPairing().ConfigureAwait(false);
                            ApplyMaintenance();
                            RefreshRunning(false);
                            StartMaintenance();
                            if (installation != null && Json.Now() >= nextScan &&
                                ((stateReady && credentials != null) || (forever != null && foreverSync != null && forever.IsConnected)))
                            {
                                nextScan = Json.Now() + config.pollMs;
                                await Scan().ConfigureAwait(false);
                            }
                            Publish();
                        }).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { activity = Describe(error); Publish(); }
                    await Task.Delay(1000, stopping.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { activity = Describe(error); Publish(); }
        }

        private async Task Initialize()
        {
            try { config = storage.LoadConfig(); configReady = true; }
            catch (Exception error) { activity = "Saved configuration could not be read; it has not been overwritten. " + Describe(error); }
            if (config == null) config = ClientStorage.NormalizeConfig(new ClientConfig());
            try { state = storage.LoadState(); stateReady = true; }
            catch (Exception) { activity = "Saved checkpoints could not be read. Sync is paused to preserve them; your game folder can still be selected."; }
            bool credentialsReady = false;
            try { credentials = storage.ReadCredentials(); credentialsReady = true; }
            catch (Exception) { pairingMessage = "Saved credentials could not be read. They have not been removed. Explicitly pair again only if you want to replace them."; }
            if (credentials != null) pairingMessage = "Paired.";
            try
            {
                string root = Path.GetDirectoryName(storage.ConfigPath);
                forever = new ForeverClient(root, ForeverStatus);
                foreverSync = new ForeverSynchronizer(root, forever, ForeverStatus);
                foreverReportUrl = foreverSync.ReportUrl;
                if (forever.IsConnected) foreverMessage = "Connected to ForeverLogs. Aurify will upload combat logs directly.";
                runtimeReady = configReady && stateReady && credentialsReady;
            }
            catch (Exception)
            {
                if (forever != null) forever.Dispose();
                forever = null;
                foreverSync = null;
                foreverMessage = "Saved ForeverLogs credentials or sync progress could not be read. They were kept unchanged; Auratism sync remains available.";
            }
            ResetAuratism();
            Publish();
            if (!String.IsNullOrEmpty(config.gameDir))
            {
                try { await AcceptInstallation(GameTools.Verify(config.gameDir)).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { discoveryMessage = "The saved game folder is unavailable. Choose a verified WoW Forever folder."; }
            }
            if (installation == null)
            {
                try
                {
                    DiscoveryResult found = GameTools.Discover();
                    discoveryCandidates = found.Installations.ConvertAll(delegate(GameInstallation item) { return item.GameDir; }).ToArray();
                    Publish();
                    if (found.Installations.Count == 1) await AcceptInstallation(found.Installations[0]).ConfigureAwait(false);
                    else discoveryMessage = found.Installations.Count > 1 ? "Several installations were found. Select one below or choose its folder." : "No verified game was found automatically. Choose game folder is available.";
                    if (found.ProbeUnavailable) discoveryMessage += " Some discovery sources were unavailable.";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { discoveryMessage = "Automatic discovery failed. Choose game folder is still available."; }
            }
            if (activity == "Starting Aurify...") activity = "Aurify is running. Closing this window keeps sync running in the tray; Exit Aurify stops it.";
            Publish();
        }

        private void SaveConfig()
        {
            if (!configReady) throw new InvalidOperationException("Saved configuration must be recovered before it can be replaced.");
            config = ClientStorage.NormalizeConfig(config);
            storage.SaveConfig(config);
        }

        private void SaveState() { lock (stateLock) storage.SaveState(state); }

        private static object CopyState(object value)
        {
            var map = value as Dictionary<string, object>;
            if (map != null)
            {
                var copy = Json.New();
                foreach (var entry in map) copy[entry.Key] = CopyState(entry.Value);
                return copy;
            }
            var list = value as System.Collections.IList;
            if (list != null)
            {
                var copy = new List<object>(list.Count);
                foreach (object item in list) copy.Add(CopyState(item));
                return copy;
            }
            return value;
        }

        private Dictionary<string, object> WorkingState(string key)
        {
            lock (stateLock)
            {
                var copy = Json.New();
                copy[key] = CopyState(Json.Get(state, key)) ?? Json.New();
                return copy;
            }
        }

        private void CommitState(string key, Dictionary<string, object> working, long owner)
        {
            object next = CopyState(Json.Get(working, key));
            lock (stateLock)
            {
                // Cancellation drains this generation before replacement/disposal.
                // Persist acknowledgements already accepted by the server during that drain.
                if (owner != generation) throw new OperationCanceledException();
                object previous;
                bool existed = state.TryGetValue(key, out previous);
                state[key] = next;
                try { storage.SaveState(state); }
                catch
                {
                    if (existed) state[key] = previous;
                    else state.Remove(key);
                    throw;
                }
            }
        }

        private static GameInstallation CopyInstallation(GameInstallation value)
        {
            if (value == null) return null;
            var copy = new GameInstallation { GameDir = value.GameDir, Executable = value.Executable, Running = value.Running };
            foreach (var entry in value.Addons)
                copy.Addons[entry.Key] = new AddonInfo { Status = entry.Value.Status, Version = entry.Value.Version, Interface = entry.Value.Interface };
            return copy;
        }

        private void ResetAuratism()
        {
            if (snapshotSync != null) snapshotSync.Dispose();
            if (portal != null) portal.Dispose();
            portal = null;
            snapshotSync = null;
            combatSync = null;
            if (!stateReady || credentials == null || config == null) return;
            PortalClient next = new PortalClient(config.serverUrl, credentials.deviceToken);
            try
            {
                long owner = generation;
                var snapshots = WorkingState("snapshots");
                var combat = WorkingState("combat");
                snapshotSync = new SnapshotSynchronizer(next, snapshots, delegate { CommitState("snapshots", snapshots, owner); }, credentials.sourceKey, config.guildName, Report);
                combatSync = new CombatSynchronizer(next, combat, delegate { CommitState("combat", combat, owner); }, Report);
                portal = next;
            }
            catch
            {
                if (snapshotSync != null) snapshotSync.Dispose();
                next.Dispose();
                snapshotSync = null;
                combatSync = null;
                throw;
            }
        }

        internal string GameDirectory { get { return installation == null ? "" : installation.GameDir; } }

        internal void SelectGame(string selected)
        {
            Notify("Verifying the selected game folder. The window and folder picker remain available.");
            Queue(async delegate {
                GameInstallation verified;
                try { verified = GameTools.Verify(selected); }
                catch (Exception) { activity = "The selected folder is not a supported, verified WoW Forever installation. Nothing was saved. Choose the folder containing the supported game client."; return; }
                if (verified == null) { activity = "That folder is not a verified WoW Forever installation. Nothing was saved."; return; }
                await AcceptInstallation(verified).ConfigureAwait(false);
                activity = "WoW Forever was verified and its folder saved. Compatible addons and combat logs sync independently.";
            });
        }

        private async Task AcceptInstallation(GameInstallation verified)
        {
            if (verified == null) throw new InvalidOperationException("Game verification failed.");
            await StopModels().ConfigureAwait(false);
            await StopSnapshots().ConfigureAwait(false);
            await StopMaintenance().ConfigureAwait(false);
            bool changed = !String.Equals(config.gameDir, verified.GameDir, StringComparison.OrdinalIgnoreCase);
            string previous = config.gameDir;
            config.gameDir = verified.GameDir;
            try { SaveConfig(); }
            catch { config.gameDir = previous; throw; }
            lock (stateLock) generation++;
            installation = CopyInstallation(verified);
            RefreshRunning(true);
            ResetAuratism();
            if (snapshotSync != null) snapshotSync.Invalidate();
            if (changed)
            {
                addons = null;
            }
            Publish();
            addonsRequested = true;
            StartMaintenance();
            nextScan = 0;
        }
        private static bool AddonUpdateDeferred(AddonSetup value)
        {
            if (value == null) return false;
            foreach (AddonResult result in value.Results.Values)
                if (result != null && result.Status == "deferred") return true;
            foreach (AddonUpdateResult result in value.Updates.Values)
                if (result != null && result.Status == "deferred") return true;
            return false;
        }
        private static bool AddonUpdateRetryable(AddonSetup value)
        {
            if (value == null) return false;
            foreach (AddonResult result in value.Results.Values)
                if (result != null && result.Retryable) return true;
            foreach (AddonUpdateResult result in value.Updates.Values)
                if (result != null && result.Retryable) return true;
            return false;
        }



        private void RefreshRunning(bool force)
        {
            if (installation == null || (!force && Json.Now() < nextProcessCheck)) return;
            bool wasRunning = installation.Running;
            installation.Running = GameTools.IsRunning(installation);
            nextProcessCheck = Json.Now() + 2000;
            if (!installation.Running)
            {
                if (addons != null) addons.RestartRequired = false;
                if (wasRunning && AddonUpdateDeferred(addons))
                {
                    addonsRequested = true;
                    nextAddonCheck = 0;
                }
            }
        }

        private sealed class MaintenanceResult
        {
            internal long Generation;
            internal GameInstallation Installation;
            internal AddonSetup Addons;
            internal Exception Failure;
            internal bool Update;
            internal UpdateCatalog Catalog;
            internal bool ExplicitUpdate;
        }

        private void StartMaintenance()
        {
            if (maintenanceTask != null || stopping.IsCancellationRequested) return;
            bool checkAddons = installation != null && (addonsRequested || Json.Now() >= nextAddonCheck);
            bool checkUpdate = configReady && stateReady && credentials != null &&
                (updatesRequested || Json.Now() >= nextUpdate);
            if (!checkAddons && !checkUpdate) return;
            if (maintenanceStopping != null) maintenanceStopping.Dispose();
            maintenanceStopping = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            CancellationToken ct = maintenanceStopping.Token;
            var result = new MaintenanceResult {
                Generation = generation, Installation = CopyInstallation(installation),
                Update = !checkAddons, ExplicitUpdate = !checkAddons && updatesRequested
            };
            ClientConfig settings = new ClientConfig { version = config.version, gameDir = config.gameDir, serverUrl = config.serverUrl,
                guildName = config.guildName, pollMs = config.pollMs, foreverVisibility = config.foreverVisibility };
            ClientCredentials approval = credentials == null ? null : new ClientCredentials {
                deviceToken = credentials.deviceToken, deviceId = credentials.deviceId, sourceKey = credentials.sourceKey };
            bool restart = addons != null && addons.RestartRequired;
            if (checkAddons)
            {
                addonsRequested = false;
                nextAddonCheck = Json.Now() + 24L * 60 * 60 * 1000;
                activity = "Checking required addons and upstream versions. You can continue using this window.";
            }
            else
            {
                updatesRequested = false;
                nextUpdate = Json.Now() + 6L * 60 * 60 * 1000;
                if (result.ExplicitUpdate) activity = "Checking the authenticated Aurify stable release catalog...";
            }
            maintenanceTask = Task.Run(async delegate {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (checkAddons)
                    {
                        result.Installation.Running = GameTools.IsRunning(result.Installation);
                        result.Addons = await AddonUpdates.ApplyAsync(result.Installation, settings, approval, ct).ConfigureAwait(false);
                        result.Installation.Running = GameTools.IsRunning(result.Installation);
                        result.Addons.RestartRequired = result.Installation.Running && (result.Addons.RestartRequired || restart);
                    }
                    else result.Catalog = await NativeUpdate.Check(Path.GetDirectoryName(storage.ConfigPath), settings, approval, ct).ConfigureAwait(false);
                }
                catch (Exception error) { result.Failure = error; }
                return result;
            });
        }

        private void ApplyMaintenance()
        {
            if (maintenanceTask == null || !maintenanceTask.IsCompleted) return;
            MaintenanceResult result = maintenanceTask.GetAwaiter().GetResult();
            maintenanceTask = null;
            if (result.Generation != generation || stopping.IsCancellationRequested) return;
            // Start the next day after completion, so the portal's daily cache
            // cannot keep a single client one refresh behind.
            if (!result.Update) nextAddonCheck = Json.Now() + 24L * 60 * 60 * 1000;
            if (result.Failure != null)
            {
                if (result.Failure is OperationCanceledException && maintenanceStopping.IsCancellationRequested) return;
                if (result.Update)
                {
                    Aurify.UpdateFailure(Path.GetDirectoryName(storage.ConfigPath), result.Failure);
                    if (result.ExplicitUpdate)
                        activity = "The authenticated update catalog could not be checked. Nothing was downloaded or changed.";
                }
                else
                {
                    nextAddonCheck = Json.Now() + AddonRetryMs;
                    activity = Describe(result.Failure) + " Aurify will retry the addon update check in 15 minutes.";
                }
                return;
            }
            if (result.Update)
            {
                updateCatalog = result.Catalog;
                latestUpdate = result.Catalog == null ? null
                    : NativeUpdate.Latest(result.Catalog, Aurify.UpdateInstallation(Path.GetDirectoryName(storage.ConfigPath)));
                if (latestUpdate == null)
                    activity = result.ExplicitUpdate ? "Aurify is up to date. No newer authenticated 2.x release is available." : activity;
                else
                {
                    activity = "Aurify " + latestUpdate.Version + " is available. Select Install latest update when ready.";
                    if (result.Catalog.Sequence > notifiedUpdateSequence)
                    {
                        notifiedUpdateSequence = result.Catalog.Sequence;
                        string message = activity;
                        OnUi(delegate {
                            if (!exiting) tray.ShowBalloonTip(5000, "Aurify update available", message, ToolTipIcon.Info);
                        });
                    }
                }
                UpdateRelease latest = latestUpdate;
                OnUi(delegate { if (form != null && !form.IsDisposed) form.SetUpdate(latest, result.Catalog != null); });
                return;
            }
            if (!result.Update && installation != null &&
                String.Equals(installation.GameDir, result.Installation.GameDir, StringComparison.OrdinalIgnoreCase))
            {
                installation = result.Installation;
                addons = result.Addons;
                nextProcessCheck = 0;
                if (snapshotSync != null) snapshotSync.Invalidate();
                if (AddonUpdateDeferred(addons))
                {
                    if (installation.Running)
                        activity = "Addon updates are paused while WoW is running. Aurify will retry automatically after WoW exits.";
                    else
                    {
                        addonsRequested = true;
                        nextAddonCheck = 0;
                        activity = "WoW stopped before a deferred addon update finished. Aurify is retrying automatically.";
                    }
                }
                else if (AddonUpdateRetryable(addons))
                {
                    nextAddonCheck = Json.Now() + AddonRetryMs;
                    activity = "An addon update source or download is temporarily unavailable. Aurify will retry in 15 minutes; existing addon files were preserved.";
                }
                else activity = "Addon update check complete. Existing settings and unmanaged addons were preserved; see individual results above.";
        }
        }

        private async Task StopMaintenance()
        {
            MaintenanceResult result = null;
            if (maintenanceStopping != null) maintenanceStopping.Cancel();
            try
            {
                if (maintenanceTask != null)
                {
                    result = await maintenanceTask.ConfigureAwait(false);
                    if (!stopping.IsCancellationRequested) ApplyMaintenance();
                }
            }
            finally
            {
                if (!stopping.IsCancellationRequested && result != null &&
                    result.Failure is OperationCanceledException)
                {
                    if (result.Update) nextUpdate = 0;
                    else addonsRequested = true;
                }
                maintenanceTask = null;
                if (maintenanceStopping != null) { maintenanceStopping.Dispose(); maintenanceStopping = null; }
            }
        }

        internal void RetryAddons()
        {
            Queue(delegate {
                if (installation == null) activity = "Choose a verified game folder first.";
                else { addonsRequested = true; ApplyMaintenance(); StartMaintenance(); }
                return Task.FromResult(0);
            });
        }

        internal void CheckUpdates()
        {
            Queue(delegate {
                if (credentials == null) activity = "Pair with Discord before checking authenticated application updates.";
                else
                {
                    updatesRequested = true;
                    nextUpdate = 0;
                    ApplyMaintenance();
                    StartMaintenance();
                }
                return Task.FromResult(0);
            });
        }

        internal void InstallUpdate()
        {
            Queue(async delegate {
                await StopMaintenance().ConfigureAwait(false);
                UpdateRelease latest = latestUpdate;
                if (latest == null || updateCatalog == null)
                {
                    activity = "Check for updates again before installing the latest release.";
                    return;
                }
                activity = "Downloading authenticated Aurify " + latest.Version + ". The current release stays active until verification completes.";
                await NativeUpdate.Install(Path.GetDirectoryName(storage.ConfigPath), config, credentials,
                    updateCatalog.Envelope, stopping.Token).ConfigureAwait(false);
                activity = "Aurify " + latest.Version + " was verified. Handing off to the transactional installer...";
            });
        }

        internal void Pair()
        {
            Queue(async delegate {
                if (credentials != null) { activity = "This device is already paired. Its saved approval is retained."; return; }
                if (pairing == null || Json.Now() >= pairingExpires)
                {
                    using (PortalClient portal = new PortalClient(config.serverUrl))
                        pairing = await portal.RequestDeviceAsync("Aurify", stopping.Token).ConfigureAwait(false);
                    pairingOrigin = ClientStorage.ValidateServerUrl(config.serverUrl);
                    pairingExpires = Json.Now() + (long)pairing.ExpiresIn * 1000;
                    nextPoll = Json.Now() + (long)pairing.Interval * 1000;
                }
                pairingMessage = "Approval code: " + pairing.UserCode + ". Continue with Discord and explicitly approve this device. Waiting for approval.";
                Uri approval;
                Uri origin = new Uri(pairingOrigin);
                if (!Uri.TryCreate(pairing.VerificationUri, UriKind.Absolute, out approval) || approval.Scheme != origin.Scheme || approval.Authority != origin.Authority)
                    throw new InvalidOperationException("Pairing approval address did not match Auratism.");
                string url = approval.AbsoluteUri;
                // Only this explicit button action can open an approval browser.
                OnUi(delegate {
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch { Notify("The approval browser could not open. Open auratism.com/members and use the displayed approval code, or select Pair with Discord to retry."); }
                });
            });
        }

        private async Task PollPairing()
        {
            if (pairing == null) return;
            if (Json.Now() >= pairingExpires)
            {
                pairing = null;
                pairingMessage = "This pairing code expired. Choose Pair with Discord to request another.";
                return;
            }
            if (Json.Now() < nextPoll) return;
            nextPoll = Json.Now() + (long)pairing.Interval * 1000;
            try
            {
                PairingApproval result;
                using (PortalClient portal = new PortalClient(pairingOrigin))
                    result = await portal.PollDeviceAsync(pairing.DeviceCode, stopping.Token).ConfigureAwait(false);
                if (result.Status == "pending") return;
                if (ClientStorage.ValidateServerUrl(result.ServerUrl) != pairingOrigin) throw new InvalidOperationException("Pairing origin changed.");
                await StopModels().ConfigureAwait(false);
                await StopSnapshots().ConfigureAwait(false);
                await StopMaintenance().ConfigureAwait(false);
                ClientCredentials approved = new ClientCredentials { deviceToken = result.Token, deviceId = result.DeviceId,
                    sourceKey = credentials == null ? ClientStorage.NewSourceKey() : credentials.sourceKey };
                if (stateReady && (credentials == null || credentials.deviceId != approved.deviceId))
                {
                    lock (stateLock)
                    {
                        object previous = Json.Get(state, "snapshots");
                        state["snapshots"] = Json.New();
                        try { SaveState(); }
                        catch { state["snapshots"] = previous; throw; }
                    }
                }
                storage.WriteCredentials(approved);
                credentials = approved;
                config.serverUrl = result.ServerUrl;
                SaveConfig();
                lock (stateLock) generation++;
                ResetAuratism();
                pairing = null;
                pairingMessage = "Device approval complete.";
                activity = "Device approval completed. Your connection is saved securely for this Windows account.";
                nextScan = 0;
                addonsRequested = installation != null;
            }
            catch (PortalException error)
            {
                if (error.Status == 403 || error.Status == 410)
                {
                    pairing = null;
                    pairingMessage = error.Status == 410 ? "The pairing code expired. Choose Pair with Discord to retry." : "Pairing was denied. No credentials were replaced.";
                }
                else pairingMessage = "Approval could not be checked. Aurify will retry until this code expires.";
            }
        }

        private void Report(string kind, string detail)
        {
            if (kind == "snapshot-uploaded")
            {
                activity = detail + " snapshot uploaded at " + DateTime.Now.ToShortTimeString() + ".";
                if (detail == "characters") { Interlocked.Exchange(ref modelRequested, 1); nextModelScan = 0; }
            }
            else if (kind == "combat-uploaded") activity = "Combat log synchronized at " + DateTime.Now.ToShortTimeString() + ".";
            else if (kind == "combat-rotated") activity = "A new combat-log generation was detected.";
            else if (kind == "snapshot-error") activity = detail + " data was not advanced; Aurify will retry.";
            Publish();
        }

        private void ReportModel(string kind, string detail)
        {
            switch (kind)
            {
                case "model-catalog": modelMessage = "Preparing character model options."; break;
                case "model-rendering": modelMessage = "Updating character model."; break;
                case "model-uploaded": modelMessage = "Character model synchronized."; break;
                case "model-idle": modelMessage = "No character model update is ready."; break;
                case "model-error":
                    modelMessage = detail == "unsupported-client" || detail == "client-mismatch"
                        ? "Character model unavailable: verify the selected game installation."
                        : "Character model update failed. Sync again to retry.";
                    break;
                default: return;
            }
            Publish();
        }

        private async Task StopModels()
        {
            if (modelStopping != null) modelStopping.Cancel();
            try { if (modelTask != null) await modelTask.ConfigureAwait(false); }
            finally
            {
                modelTask = null;
                if (modelStopping != null) { modelStopping.Dispose(); modelStopping = null; }
                nextModelScan = 0;
                Interlocked.Exchange(ref modelRequested, 0);
                modelMessage = null;
            }
        }

        private Task<Exception> StartSnapshot(bool force)
        {
            if (!stateReady || credentials == null || snapshotSync == null || installation == null)
                return Task.FromResult<Exception>(null);
            if (!force && snapshotTask != null && !snapshotTask.IsCompleted) return snapshotTask;
            if (snapshotStopping == null) snapshotStopping = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            CancellationToken ct = snapshotStopping.Token;
            SnapshotSynchronizer synchronizer = snapshotSync;
            GameInstallation target = CopyInstallation(installation);
            Task<Exception> previous = snapshotTask;
            // Manual requests append an actual forced scan; they never wait while
            // holding the controller semaphore and cannot disappear behind a scan.
            snapshotTask = Task.Run(async delegate {
                if (previous != null) await previous.ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (force) synchronizer.Invalidate();
                    await synchronizer.ScanAsync(target, ct).ConfigureAwait(false);
                    return null;
                }
                catch (OperationCanceledException error) { return (Exception)error; }
                catch (Exception error) { activity = Describe(error); Publish(); return error; }
            });
            return snapshotTask;
        }

        private async Task StopSnapshots()
        {
            if (snapshotStopping != null) snapshotStopping.Cancel();
            try { if (snapshotTask != null) await snapshotTask.ConfigureAwait(false); }
            finally
            {
                snapshotTask = null;
                if (snapshotStopping != null) { snapshotStopping.Dispose(); snapshotStopping = null; }
            }
        }

        private async Task SyncAuratism(string logPath)
        {
            if (!stateReady || credentials == null) return;
            if (portal == null || snapshotSync == null || combatSync == null) ResetAuratism();
            StartSnapshot(false);
            await combatSync.ScanAsync(logPath, stopping.Token).ConfigureAwait(false);
        }

        private async Task SyncForever(string logPath)
        {
            if (forever == null || foreverSync == null || !forever.IsConnected) return;
            try
            {
                await foreverSync.ScanAsync(logPath, installation.Running, config.foreverVisibility, ForeverTimeZone.Current(), stopping.Token).ConfigureAwait(false);
                foreverReportUrl = foreverSync.ReportUrl;
            }
            catch (OperationCanceledException) { throw; }
            catch (ForeverException error) { foreverMessage = error.Message; }
            catch (Exception) { foreverMessage = "ForeverLogs sync could not finish. Your queued logs were kept and Auratism sync remains available."; }
        }

        private async Task Scan()
        {
            string logPath = Path.Combine(installation.GameDir, "Logs", "WoWCombatLog.txt");
            Task auratismTask = SyncAuratism(logPath);
            Task foreverTask = SyncForever(logPath);
            Exception failure = null;
            try { await Task.WhenAll(auratismTask, foreverTask).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { failure = error; }
            // One tracked model operation at a time; expensive asset work must not hold
            // the controller semaphore or delay combat, snapshots, and ForeverLogs.
            if (stateReady && credentials != null && !stopping.IsCancellationRequested &&
                (modelTask == null || modelTask.IsCompleted) && Json.Now() >= nextModelScan)
            {
                nextModelScan = Json.Now() + 300000;
                string modelServer = config.serverUrl, modelToken = credentials.deviceToken;
                if (modelStopping != null) modelStopping.Dispose();
                modelStopping = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                CancellationToken modelCancellation = modelStopping.Token;
                GameInstallation modelInstallation = CopyInstallation(installation);
                modelTask = Task.Run(async delegate {
                    try
                    {
                        using (PortalClient modelPortal = new PortalClient(modelServer, modelToken))
                        {
                            do
                            {
                                Interlocked.Exchange(ref modelRequested, 0);
                                await new CharacterModelSynchronizer(storage.Root, modelPortal, ReportModel).ScanAsync(modelInstallation, modelCancellation).ConfigureAwait(false);
                            } while (!modelCancellation.IsCancellationRequested && Interlocked.CompareExchange(ref modelRequested, 0, 0) != 0);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception error)
                    {
                        var modelFailure = error as ModelFailure;
                        ReportModel("model-error", modelFailure == null ? null : modelFailure.Code);
                    }
                });
            }
            if (failure != null) throw failure;
        }

        internal void ScanNow()
        {
            Task<Exception> requested = null;
            long owner = -1;
            Queue(async delegate {
                if (installation == null || !((stateReady && credentials != null) || (forever != null && foreverSync != null && forever.IsConnected)))
                { activity = "Choose a verified game folder and connect to Auratism or ForeverLogs first. Unreadable saved progress is never overwritten."; return; }
                owner = generation;
                requested = StartSnapshot(true);
                await Scan().ConfigureAwait(false);
            }, async delegate {
                if (requested == null) return;
                Exception failure = await requested.ConfigureAwait(false);
                lock (stateLock)
                {
                    if (owner != generation || stopping.IsCancellationRequested) return;
                    activity = failure == null
                        ? "Scan completed at " + DateTime.Now.ToShortTimeString() + ". Only new saved data is uploaded."
                        : Describe(failure);
                }
            });
        }

        private void ForeverStatus(string message)
        {
            foreverMessage = message;
            Publish();
        }

        internal void ConnectForever()
        {
            Queue(async delegate {
                if (forever == null || foreverSync == null) return;
                if (forever.IsConnected) { foreverMessage = "ForeverLogs is already connected; your saved approval was kept."; return; }
                try
                {
                    string url = await forever.BeginLoginAsync(stopping.Token).ConfigureAwait(false);
                    Uri approval;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out approval) ||
                        approval.Scheme != "https" || approval.Host != "foreverlogs.gg" || !approval.IsDefaultPort ||
                        approval.UserInfo.Length != 0 || approval.AbsolutePath != "/api/auth/desktop/authorize")
                    {
                        forever.CancelLogin();
                        throw new InvalidOperationException("Unexpected ForeverLogs authorization address.");
                    }
                    foreverMessage = "Approve Aurify's upload access on ForeverLogs in your browser. No upload starts before you approve.";
                    OnUi(delegate {
                        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                        catch
                        {
                            forever.CancelLogin();
                            ForeverStatus("The approval browser could not open. Select Connect ForeverLogs to retry.");
                        }
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch (ForeverException error) { foreverMessage = error.Message; }
                catch (Exception) { foreverMessage = "ForeverLogs sign-in could not start. Your saved connection was not changed."; }
            });
        }

        internal void CancelForever()
        {
            if (forever != null) forever.CancelLogin();
        }

        internal void DisconnectForever()
        {
            Queue(async delegate {
                if (forever == null) return;
                try
                {
                    await forever.DisconnectAsync(stopping.Token).ConfigureAwait(false);
                    foreverMessage = "Disconnected from ForeverLogs. Pending logs are kept for the same account; Auratism sync is unchanged.";
                }
                catch (OperationCanceledException) { throw; }
                catch (ForeverException error) { foreverMessage = error.Message; }
                catch (Exception) { foreverMessage = "ForeverLogs could not be disconnected safely. Your saved data was kept."; }
            });
        }

        internal void SetForeverVisibility(string visibility)
        {
            if (visibility != "private" && visibility != "public") return;
            Queue(delegate {
                string previous = config.foreverVisibility;
                config.foreverVisibility = visibility;
                try { SaveConfig(); }
                catch { config.foreverVisibility = previous; throw; }
                foreverMessage = "New ForeverLogs reports will be " + visibility + ". Existing reports are unchanged.";
                return Task.FromResult(0);
            });
        }

        internal void OpenForeverReport()
        {
            Queue(delegate {
                Uri report;
                if (!Uri.TryCreate(foreverReportUrl, UriKind.Absolute, out report) ||
                    report.Scheme != "https" || report.Host != "foreverlogs.gg" || !report.IsDefaultPort ||
                    report.UserInfo.Length != 0 || report.Query.Length != 0 || report.Fragment.Length != 0 ||
                    !System.Text.RegularExpressions.Regex.IsMatch(report.AbsolutePath, @"\A/reports/[1-9][0-9]*\z"))
                { foreverMessage = "No ForeverLogs report is available yet."; return Task.FromResult(0); }
                OnUi(delegate {
                    try { Process.Start(new ProcessStartInfo(report.AbsoluteUri) { UseShellExecute = true }); }
                    catch { Notify("The report browser could not open. Try Open ForeverLogs report again."); }
                });
                return Task.FromResult(0);
            });
        }

        private NamedPipeServerStream CreatePipe()
        {
            PipeSecurity security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            SecurityIdentifier user = Aurify.RuntimeUser();
            security.SetOwner(user);
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 256, 256, security);
        }

        private async Task ServePipe()
        {
            while (!stopping.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                lock (pipeLock) { pipe = activePipe; }
                try
                {
                    await Task.Factory.FromAsync(pipe.BeginWaitForConnection, pipe.EndWaitForConnection, null).ConfigureAwait(false);
                    byte[] buffer = new byte[128];
                    int count = 0;
                    bool complete = false;
                    using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
                    {
                        deadline.CancelAfter(5000);
                        using (deadline.Token.Register(delegate { try { pipe.Dispose(); } catch { } }))
                        {
                            while (count < buffer.Length)
                            {
                                int read = await pipe.ReadAsync(buffer, count, buffer.Length - count, deadline.Token).ConfigureAwait(false);
                                if (read == 0) break;
                                count += read;
                                if (Array.IndexOf(buffer, (byte)'\n', 0, count) >= 0) { complete = true; break; }
                            }
                            string command = complete ? Encoding.ASCII.GetString(buffer, 0, count) : "";
                            bool authenticatedShutdown = command == "shutdown " + controlToken + "\n";
                            bool handoff = command == "handoff\n";
                            bool shutdown = authenticatedShutdown || handoff;
                            bool setup = command == "setup\n";
                            bool accepted = shutdown || setup || (command == "ping\n" && runtimeReady);
                            if (setup) OnUi(ShowSetup);
                            byte[] response = Encoding.ASCII.GetBytes(accepted ? "ok\n" : "unavailable\n");
                            await pipe.WriteAsync(response, 0, response.Length, deadline.Token).ConfigureAwait(false);
                            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                            if (shutdown) { OnUi(Shutdown); return; }
                        }
                    }
                }
                catch (Exception)
                {
                    if (stopping.IsCancellationRequested) return;
                }
                finally { pipe.Dispose(); }
                lock (pipeLock)
                {
                    if (stopping.IsCancellationRequested) return;
                    activePipe = CreatePipe();
                }
            }
        }

        internal async void Shutdown()
        {
            if (exiting) return;
            lock (commandLock) exiting = true;
            if (form != null)
            {
                form.SetActivity("Stopping sync safely...");
                form.Enabled = false;
            }
            stopping.Cancel();
            lock (pipeLock) { if (activePipe != null) activePipe.Dispose(); }
            try { if (loopTask != null) await loopTask; }
            catch (Exception) { }
            // Wait without blocking the STA for any explicit operation already holding the state lock.
            await operations.WaitAsync();
            operations.Release();
            Task[] pendingCommands;
            lock (commandLock) { pendingCommands = new Task[commands.Count]; commands.CopyTo(pendingCommands); }
            try { await Task.WhenAll(pendingCommands); }
            catch (Exception) { }
            try { await StopSnapshots(); }
            catch (Exception) { }
            try { await StopMaintenance(); }
            catch (Exception) { }
            try { if (pipeTask != null) await pipeTask; }
            catch (Exception) { }
            try { await StopModels(); }
            catch (Exception) { }
            try { storage.RemoveControl(); }
            catch (Exception) { }
            tray.Visible = false;
            if (form != null) form.Close();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (forever != null) forever.Dispose();
                if (snapshotSync != null) snapshotSync.Dispose();
                if (portal != null) portal.Dispose();
                tray.Dispose();
                if (form != null) form.Dispose();
                dispatcher.Dispose();
                ownedIcon.Dispose();
                stopping.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class HostViewQueue
    {
        private readonly object gate = new object();
        private HostView pending, applying, displayed;
        private bool queued;

        internal bool Offer(HostView view)
        {
            lock (gate)
            {
                if (view.Same(pending ?? applying ?? displayed)) return false;
                pending = view;
                if (queued) return false;
                queued = true;
                return true;
            }
        }

        internal HostView Take()
        {
            lock (gate)
            {
                applying = pending;
                pending = null;
                queued = false;
                return applying;
            }
        }

        internal void Complete(HostView view)
        {
            lock (gate) { displayed = view; applying = null; }
        }

        internal void Fail(HostView view)
        {
            lock (gate)
            {
                if (Object.ReferenceEquals(applying, view)) applying = null;
                // A partial callback may have changed controls; do not trust the old view.
                displayed = null;
            }
        }

        internal void Reset()
        {
            lock (gate) { queued = false; pending = null; applying = null; displayed = null; }
        }
    }

    // A captured presentation value: no worker reads a control's state, and
    // changing forms/visibility invalidates equality even when text is unchanged.
    internal sealed class HostView
    {
        private readonly SetupForm target;
        private readonly int epoch;
        private readonly string game, pairing, addons, forever, activity, visibility;
        private readonly bool ready, connected, pending, report;
        private readonly string[] candidates;

        internal HostView(SetupForm target, int epoch, string game, string pairing, string addons,
            string forever, string activity, string visibility, bool ready, bool connected,
            bool pending, bool report, string[] candidates)
        {
            this.target = target; this.epoch = epoch; this.game = game; this.pairing = pairing;
            this.addons = addons; this.forever = forever; this.activity = activity; this.visibility = visibility;
            this.ready = ready; this.connected = connected; this.pending = pending; this.report = report;
            this.candidates = candidates;
        }

        internal bool Same(HostView other)
        {
            if (other == null || target != other.target || epoch != other.epoch || game != other.game ||
                pairing != other.pairing || addons != other.addons || forever != other.forever ||
                activity != other.activity || visibility != other.visibility || ready != other.ready ||
                connected != other.connected || pending != other.pending || report != other.report ||
                candidates.Length != other.candidates.Length) return false;
            for (int index = 0; index < candidates.Length; index++)
                if (candidates[index] != other.candidates[index]) return false;
            return true;
        }

        internal bool Apply()
        {
            if (target == null || target.IsDisposed) return false;
            target.UpdateStatus(game, pairing, addons, forever, activity);
            target.UpdateForever(visibility, ready, connected, pending, report);
            target.SetCandidates(candidates);
            return true;
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly HostContext host;
        private readonly Label game = new Label();
        private readonly Label pairing = new Label();
        private readonly Label addons = new Label();
        private readonly Label foreverStatus = new Label();
        private readonly ComboBox foreverVisibility = new ComboBox();
        private readonly Button connectForever;
        private readonly Button cancelForever;
        private readonly Button disconnectForever;
        private readonly Button openForeverReport;
        private readonly Label updateStatus = new Label();
        private readonly Button installUpdate;
        private bool settingVisibility;
        private readonly Label activity = new Label();
        private readonly Label notice = new Label();
        private readonly ComboBox candidates = new ComboBox();
        private readonly FlowLayoutPanel discovered = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Visible = false };

        internal SetupForm(HostContext host)
        {
            this.host = host;
            Version appVersion = typeof(SetupForm).Assembly.GetName().Version;
            string version = appVersion.ToString(appVersion.Revision > 0 ? 4 : 3);
            Text = "Aurify v" + version + " - guild sync";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(690, 680);
            ClientSize = new Size(760, 780);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10F);
            TableLayoutPanel content = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18), ColumnCount = 1, RowCount = 0 };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(content);
            AddText(content, new Label { Text = "Aurify", Font = new Font("Segoe UI", 20F, FontStyle.Bold) });
            AddText(content, new Label { Text = "App version " + version });
            AddText(content, new Label { Text = "Uploads directly to your guild website. WoW Forever is detected automatically; closing this window keeps sync running." });
            AddText(content, new Label { Text = "Aurify updates", Font = new Font(Font, FontStyle.Bold) });
            AddText(content, updateStatus);
            installUpdate = Button("Install latest update", delegate { host.InstallUpdate(); });
            installUpdate.Enabled = false;
            AddButtons(content, Button("Check for updates", delegate { host.CheckUpdates(); }), installUpdate);
            AddText(content, game);
            AddButtons(content, Button("Choose game folder", ChooseGame), Button("Retry addon setup", delegate { host.RetryAddons(); }));
            candidates.DropDownStyle = ComboBoxStyle.DropDownList;
            candidates.Width = 500;
            discovered.Controls.Add(candidates);
            discovered.Controls.Add(Button("Use selected", delegate { if (candidates.SelectedItem != null) host.SelectGame((string)candidates.SelectedItem); else host.Notify("Choose a discovered folder or use Choose game folder."); }));
            content.Controls.Add(discovered);
            AddText(content, addons);
            AddText(content, pairing);
            AddButtons(content, Button("Pair with Discord", delegate { host.Pair(); }), Button("Sync now", delegate { host.ScanNow(); }));
            AddText(content, new Label { Text = "ForeverLogs — uploaded directly by Aurify", Font = new Font(Font, FontStyle.Bold) });
            AddText(content, foreverStatus);
            AddText(content, new Label { Text = "Approve once in your browser. Existing and new combat logs upload automatically; no separate uploader is needed. Private reports are visible to your ForeverLogs account." });
            foreverVisibility.DropDownStyle = ComboBoxStyle.DropDownList;
            foreverVisibility.Items.AddRange(new object[] { "Private — only your ForeverLogs account", "Public — publish new ForeverLogs reports" });
            foreverVisibility.Width = 420;
            foreverVisibility.SelectedIndex = 0;
            foreverVisibility.SelectedIndexChanged += delegate {
                if (settingVisibility) return;
                bool publish = foreverVisibility.SelectedIndex == 1;
                if (publish && MessageBox.Show(this,
                    "Make future combat reports uploaded by Aurify PUBLIC on foreverlogs.gg?\n\nLogs can contain player names and combat activity. Existing reports are unchanged.",
                    "Publish future ForeverLogs reports", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    settingVisibility = true;
                    foreverVisibility.SelectedIndex = 0;
                    settingVisibility = false;
                    return;
                }
                host.SetForeverVisibility(publish ? "public" : "private");
            };
            content.Controls.Add(foreverVisibility);
            connectForever = Button("Connect ForeverLogs", delegate { host.ConnectForever(); });
            cancelForever = Button("Cancel sign-in", delegate { host.CancelForever(); });
            disconnectForever = Button("Disconnect ForeverLogs", delegate { host.DisconnectForever(); });
            openForeverReport = Button("Open ForeverLogs report", delegate { host.OpenForeverReport(); });
            AddButtons(content, connectForever, cancelForever, disconnectForever, openForeverReport);
            AddText(content, activity);
            AddText(content, notice);
            AddButtons(content, Button("Hide to tray", delegate { Hide(); }), Button("Exit Aurify", delegate { host.Shutdown(); }));
            game.Text = "Looking for WoW Forever. Choose game folder is always available.";
            pairing.Text = "Pairing requires your explicit approval.";
            activity.Text = "Starting Aurify...";
            updateStatus.Text = "No update check has completed in this session. Nothing downloads until you select Install latest update.";
        }


        private static Button Button(string text, Action action)
        {
            Button button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(120, 34), Margin = new Padding(0, 3, 8, 8) };
            button.Click += delegate { action(); };
            return button;
        }

        private static void AddText(TableLayoutPanel content, Label label)
        {
            label.AutoSize = true;
            label.MaximumSize = new Size(690, 0);
            label.Margin = new Padding(0, 5, 0, 10);
            content.Controls.Add(label);
        }

        private static void AddButtons(TableLayoutPanel content, params Button[] buttons)
        {
            FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
            row.Controls.AddRange(buttons);
            content.Controls.Add(row);
        }

        private void ChooseGame()
        {
            using (FolderBrowserDialog picker = new FolderBrowserDialog { Description = "Choose the WoW Forever folder containing the supported game executable.", ShowNewFolderButton = false, RootFolder = Environment.SpecialFolder.MyComputer, SelectedPath = host.GameDirectory })
            {
                if (picker.ShowDialog(this) == DialogResult.OK) host.SelectGame(picker.SelectedPath);
                else host.Notify("Game folder selection cancelled. Your saved folder was not changed.");
            }
        }

        internal void UpdateForever(string visibility, bool ready, bool connected, bool pending, bool hasReport)
        {
            settingVisibility = true;
            foreverVisibility.SelectedIndex = visibility == "public" ? 1 : 0;
            settingVisibility = false;
            connectForever.Enabled = ready && !connected && !pending;
            cancelForever.Enabled = pending;
            disconnectForever.Enabled = connected;
            openForeverReport.Enabled = hasReport;
        }

        internal void SetUpdate(UpdateRelease latest, bool checkedCatalog)
        {
            installUpdate.Enabled = latest != null;
            if (latest == null)
            {
                updateStatus.Text = checkedCatalog
                    ? "Aurify is up to date. No newer authenticated 2.x release is available."
                    : "No update check has completed in this session. Nothing downloads until you select Install latest update.";
                return;
            }
            updateStatus.Text = "Latest release: Aurify " + latest.Version + " — " +
                latest.Length.ToString("N0", CultureInfo.CurrentCulture) + " bytes (" +
                (latest.Length / 1048576d).ToString("0.0", CultureInfo.CurrentCulture) +
                " MiB). Select Install latest update when ready; no download has started.";
        }

        internal void SetCandidates(string[] paths)
        {
            bool same = candidates.Items.Count == paths.Length;
            for (int index = 0; same && index < paths.Length; index++)
                same = String.Equals(candidates.Items[index] as string, paths[index], StringComparison.Ordinal);
            if (same) return;
            candidates.Items.Clear();
            candidates.Items.AddRange(paths);
            if (paths.Length > 0) candidates.SelectedIndex = 0;
            discovered.Visible = paths.Length > 1;
        }

        internal void SetActivity(string text) { notice.Text = text; }

        internal void UpdateStatus(string gameText, string pairingText, string addonText, string foreverText, string activityText)
        {
            game.Text = gameText;
            pairing.Text = pairingText;
            addons.Text = addonText;
            foreverStatus.Text = foreverText;
            activity.Text = activityText;
        }
    }
}
