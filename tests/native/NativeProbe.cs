using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AurifyClient;

internal static class AurifyNativeProbe
{
    private static int Main()
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            Dictionary<string, object> request = Json.Obj(Json.Parse(Console.In.ReadToEnd()));
            object result = Run(request).GetAwaiter().GetResult();
            Console.WriteLine(Json.Stringify(new { ok = true, result = result }));
            return 0;
        }
        catch (Exception error)
        {
            if (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            PortalException portal = error as PortalException;
            Console.WriteLine(Json.Stringify(new {
                ok = false, type = error.GetType().Name, message = error.Message,
                status = portal == null ? 0 : portal.Status,
                expectedOffset = portal == null ? null : portal.ExpectedOffset
            }));
            return 0;
        }
    }

    private static async Task<object> Run(Dictionary<string, object> request)
    {
        string operation = Json.Str(Json.Get(request, "operation"));
        if (operation == "update") return UpdateProbe.Run(request);
        if (operation == "forever") return await ForeverProbe.Run(request);
        if (operation == "snapshots-efficiency") return await SnapshotEfficiencyProbe.Run(request);
        if (operation == "combat-efficiency") return await CombatEfficiencyProbe.Run(request);
        if (operation == "host-efficiency") return await HostEfficiencyProbe.Run(request);
        if (operation == "parse") return LuaData.Parse(Json.Str(Json.Get(request, "lua")));
        if (operation == "addon-updates")
        {
            GameInstallation installation = GameTools.Verify(Json.Str(Json.Get(request, "root")));
            string token = Json.Str(Json.Get(request, "token"));
            ClientCredentials approval = token == null ? null : new ClientCredentials { deviceToken = token };
            return await AddonUpdates.ApplyAsync(
                installation,
                new ClientConfig { serverUrl = Json.Str(Json.Get(request, "serverUrl")) },
                approval,
                CancellationToken.None);
        }
        if (operation == "normalize")
        {
            string kind = Json.Str(Json.Get(request, "kind"));
            Dictionary<string, object> saved = request.ContainsKey("lua")
                ? LuaData.Parse(Json.Str(request["lua"])) : Json.Obj(Json.Get(request, "saved"));
            Dictionary<string, object> source = Json.Obj(Json.Get(request, "source"));
            if (kind == "professions") return Professions.Normalize(saved, source, Json.Str(Json.Get(request, "guildName"), "AURA"));
            if (kind == "overlord") return Overlord.Normalize(saved, source);
            if (kind == "activity") return Activity.Normalize(saved, source);
            if (kind == "characters") return Characters.Normalize(saved, source);
            throw new InvalidOperationException("Unsupported normalization kind");
        }
        string root = Json.Str(Json.Get(request, "root"));
        if (operation == "verify") return GameTools.Verify(root);
        if (operation == "model-client")
        {
            string gameRoot;
            var client = CharacterModelSynchronizer.InstalledClient(GameTools.Verify(root), out gameRoot);
            return new { client = client, gameRoot = gameRoot };
        }
        if (operation == "discover") return GameTools.Discover();
        if (operation == "stable")
        {
            Task writer = request.ContainsKey("replacement")
                ? Task.Delay(75).ContinueWith(delegate {
                    File.WriteAllText(root, Json.Str(request["replacement"]), new UTF8Encoding(false));
                  })
                : Task.FromResult(0);
            StableFile file = Files.StableRead(root);
            await writer;
            return file == null ? null : new { data = Convert.ToBase64String(file.Bytes), identity = file.Identity, modified = file.LastWriteMs };
        }
        if (operation == "storage")
        {
            ClientStorage storage = new ClientStorage(root);
            ClientConfig config = storage.LoadConfig();
            Dictionary<string, object> values = Json.Obj(Json.Get(request, "config"));
            if (values.ContainsKey("serverUrl")) config.serverUrl = Json.Str(values["serverUrl"]);
            if (values.ContainsKey("gameDir")) config.gameDir = Json.Str(values["gameDir"]);
            storage.SaveConfig(config);
            Dictionary<string, object> credentials = Json.Obj(Json.Get(request, "credentials"));
            if (credentials.Count > 0)
                storage.WriteCredentials(new ClientCredentials {
                    deviceToken = Json.Str(Json.Get(credentials, "deviceToken")),
                    deviceId = Json.Str(Json.Get(credentials, "deviceId")),
                    sourceKey = Json.Str(Json.Get(credentials, "sourceKey"))
                });
            if (request.ContainsKey("state")) storage.SaveState(Json.Obj(request["state"]));
            return new { config = storage.LoadConfig(), credentials = storage.ReadCredentials(), state = storage.LoadState(), envelope = File.ReadAllText(storage.CredentialsPath) };
        }
        if (operation == "release")
        {
            MethodInfo reader = typeof(Aurify).GetMethod("ReadRelease", BindingFlags.NonPublic | BindingFlags.Static);
            return reader.Invoke(null, new object[] { root, Json.Str(Json.Get(request, "version")) });
        }
        if (operation == "addons")
        {
            List<AddonPackage> packages = new List<AddonPackage>();
            Dictionary<string, object> inputs = Json.Obj(Json.Get(request, "packages"));
            List<string> downloads = new List<string>();
            foreach (AddonPackage original in AddonInstaller.Catalog)
            {
                Dictionary<string, object> input = Json.Obj(Json.Get(inputs, original.Key));
                packages.Add(new AddonPackage(
                    original.Key,
                    original.Directory,
                    original.Name,
                    Json.Str(Json.Get(input, "version"), original.Version),
                    original.Toc,
                    original.Title,
                    Json.Str(Json.Get(input, "url"), original.Url),
                    Json.Str(Json.Get(input, "sha256"), original.Sha256),
                    input.ContainsKey("bytes") ? Json.Int(input["bytes"]) : 0));
            }
            if (Json.Bool(Json.Get(request, "unavailable"))) packages.Clear();
            AddonFetch fetch = delegate(AddonPackage package, CancellationToken ct)
            {
                downloads.Add(package.Key);
                Dictionary<string, object> input = Json.Obj(Json.Get(inputs, package.Key));
                string occupied = Json.Str(Json.Get(input, "occupy"));
                if (occupied != null)
                {
                    string target = Path.Combine(root, "Interface", "AddOns", package.Directory);
                    Directory.CreateDirectory(target);
                    File.WriteAllText(Path.Combine(target, package.Toc), occupied, new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(target, "keep.txt"), "concurrent install preserved");
                }
                if (Json.Bool(Json.Get(input, "stalled")))
                    return new TaskCompletionSource<HttpResponseMessage>().Task;
                byte[] bytes = Convert.FromBase64String(Json.Str(Json.Get(input, "data"), ""));
                HttpResponseMessage response = new HttpResponseMessage((HttpStatusCode)(input.ContainsKey("status") ? Json.Int(input["status"]) : 200));
                response.Content = new ByteArrayContent(bytes);
                if (input.ContainsKey("length")) response.Content.Headers.ContentLength = Json.Int(input["length"]);
                return Task.FromResult(response);
            };
            string checkpoint = Json.Str(Json.Get(request, "checkpoint"));
            if (checkpoint != null)
                AddonInstaller.TransactionCheckpoint = delegate(string phase) {
                    if (phase == checkpoint) throw new AddonInstaller.InterruptedTransaction();
                };
            try
            {
                GameInstallation installation = GameTools.Verify(root);
                installation.Running = Json.Bool(Json.Get(request, "running"));
                AddonSetup result = await AddonInstaller.InstallAsync(installation, CancellationToken.None, packages, fetch,
                    Json.Str(Json.Get(request, "bundle")));
                return new { setup = result, installation = installation, downloads = downloads };
            }
            finally
            {
                AddonInstaller.TransactionCheckpoint = null;
            }
        }
        using (PortalClient portal = new PortalClient(Json.Str(Json.Get(request, "serverUrl")), Json.Str(Json.Get(request, "token"))))
        {
            if (operation == "pair-request") return await portal.RequestDeviceAsync("Synthetic native device", CancellationToken.None);
            if (operation == "pair-poll") return await portal.PollDeviceAsync(Json.Str(Json.Get(request, "deviceCode")), CancellationToken.None);
            if (operation == "combat-status") return await portal.CombatStatusAsync(Json.Str(Json.Get(request, "streamId")), CancellationToken.None);
            Dictionary<string, object> state = Json.Obj(Json.Get(request, "state"));
            List<object> checkpoints = new List<object>();
            List<object> reports = new List<object>();
            int saveCalls = 0;
            Action save = delegate {
                saveCalls++;
                if (Json.Bool(Json.Get(request, "failSave"))) throw new IOException("Synthetic checkpoint persistence failure.");
                checkpoints.Add(Json.Parse(Json.Stringify(state)));
            };
            Action<string, string> report = delegate(string name, string value) { reports.Add(new { name = name, value = value }); };
            Exception failure = null;
            try
            {
                if (operation == "combat")
                {
                    string zoneId = Json.Str(Json.Get(request, "timezone"));
                    TimeZoneInfo zone = zoneId == null ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(zoneId);
                    long now = request.ContainsKey("now") ? Json.Int(request["now"]) : Json.Now();
                    int maxChunks = request.ContainsKey("maxChunks") ? (int)Json.Int(request["maxChunks"]) : 16;
                    await new CombatSynchronizer(portal, state, save, report, zone, delegate { return now; }, maxChunks)
                        .ScanAsync(root, CancellationToken.None);
                }
                else if (operation == "snapshots")
                {
                    GameInstallation installation = GameTools.Verify(root);
                    using (SnapshotSynchronizer synchronizer = new SnapshotSynchronizer(portal, state, save,
                        Json.Str(Json.Get(request, "sourceKey")), "AURA", report))
                        await synchronizer.ScanAsync(installation, CancellationToken.None);
                }
                else throw new InvalidOperationException("Unsupported probe operation");
            }
            catch (Exception error) { failure = error; }
            return new { state = state, checkpoints = checkpoints, saveCalls = saveCalls, reports = reports,
                error = failure == null ? null : new { type = failure.GetType().Name, message = failure.Message } };
        }
    }
}
