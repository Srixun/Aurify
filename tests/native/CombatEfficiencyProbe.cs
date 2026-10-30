using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AurifyClient;

internal static class CombatEfficiencyProbe
{
    internal static async Task<object> Run(Dictionary<string, object> request)
    {
        string root = Json.Str(Json.Get(request, "root"));
        string server = Json.Str(Json.Get(request, "serverUrl"));
        var state = Json.Obj(Json.Get(request, "state"));
        object durable = Json.Parse(Json.Stringify(state));
        long now = Json.Int(Json.Get(request, "now"));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Json.Str(Json.Get(request, "timezone"), "UTC"));
        var results = new List<object>();
        var checkpoints = new List<object>();
        int saves = 0, failSaveAt = 0;
        Action save = delegate {
            saves++;
            if (saves == failSaveAt) throw new IOException("Synthetic durable save failure.");
            durable = Json.Parse(Json.Stringify(state));
            checkpoints.Add(durable);
        };
        using (var portal = new PortalClient(server, Json.Str(Json.Get(request, "token"))))
        using (var control = new HttpClient())
        {
            CombatSynchronizer synchronizer = null;
            int index = 0;
            foreach (object raw in Json.Items(Json.Get(request, "steps")))
            {
                var step = Json.Obj(raw);
                using (var marker = await control.GetAsync(server + "/probe/step/" + index++))
                    marker.EnsureSuccessStatusCode();
                now += Json.Int(Json.Get(step, "advance"));
                if (step.ContainsKey("append"))
                {
                    byte[] bytes = Convert.FromBase64String(Json.Str(step["append"]));
                    using (var file = new FileStream(root, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        file.Write(bytes, 0, bytes.Length);
                }
                if (step.ContainsKey("replace"))
                {
                    File.Move(root, root + ".old-" + index);
                    File.WriteAllBytes(root, Convert.FromBase64String(Json.Str(step["replace"])));
                }
                if (step.ContainsKey("rewrite")) File.WriteAllBytes(root, Convert.FromBase64String(Json.Str(step["rewrite"])));
                if (Json.Bool(Json.Get(step, "restart")))
                {
                    state = Json.Obj(Json.Parse(Json.Stringify(durable)));
                    synchronizer = null;
                }
                if (synchronizer == null)
                    synchronizer = new CombatSynchronizer(portal, state, save, null, zone, delegate { return now; }, 16);
                saves = 0;
                failSaveAt = (int)Json.Int(Json.Get(step, "failSaveAt"));
                checkpoints = new List<object>();
                Exception failure = null;
                using (var cancellation = new CancellationTokenSource())
                {
                    if (Json.Bool(Json.Get(step, "cancel"))) cancellation.Cancel();
                    try { await synchronizer.ScanAsync(root, cancellation.Token); }
                    catch (Exception error) { failure = error; }
                }
                results.Add(new {
                    state = Json.Parse(Json.Stringify(state)), durable = durable, checkpoints = checkpoints,
                    saveCalls = saves, error = failure == null ? null : failure.GetType().Name
                });
            }
        }
        return results;
    }
}
