using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AurifyClient;

internal static class ForeverProbe
{
    internal static async Task<object> Run(Dictionary<string, object> request)
    {
        string root = Json.Str(Json.Get(request, "root"));
        string serviceUrl = Json.Str(Json.Get(request, "serverUrl"));
        Uri fixture = FixtureUri(serviceUrl);
        DateTime now = request.ContainsKey("now")
            ? DateTime.Parse(Json.Str(request["now"]), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
            : DateTime.UtcNow;
        List<string> messages = new List<string>();
        Action<string> status = delegate(string message) { lock (messages) messages.Add(message); };
        List<object> results = new List<object>();
        ForeverClient client = null;
        ForeverSynchronizer sync = null;
        CancellationTokenSource loginCancellation = null;
        string authorizeUrl = null;
        string reportId = null;
        using (CancellationTokenSource deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
        {
            try
            {
                client = new ForeverClient(root, serviceUrl, status);
                foreach (object item in Json.Items(Json.Get(request, "actions")))
                {
                    Dictionary<string, object> action = Json.Obj(item);
                    string name = Json.Str(Json.Get(action, "action"));
                    object value = null;
                    object failure = null;
                    try
                    {
                        string id = Json.Str(Json.Get(action, "reportId"), reportId);
                        if (name == "beginLogin" || name == "login")
                        {
                            if (loginCancellation != null) loginCancellation.Dispose();
                            loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                            authorizeUrl = await client.BeginLoginAsync(loginCancellation.Token);
                            value = authorizeUrl;
                            if (name == "login")
                            {
                                await VisitAuthorizeAsync(fixture, authorizeUrl, deadline.Token);
                                await WaitForLoginAsync(client, deadline.Token);
                            }
                        }
                        else if (name == "authorize")
                        {
                            value = await VisitAuthorizeAsync(fixture, authorizeUrl, deadline.Token);
                        }
                        else if (name == "waitLogin") await WaitForLoginAsync(client, deadline.Token);
                        else if (name == "cancelLogin") client.CancelLogin();
                        else if (name == "cancelToken")
                        {
                            if (loginCancellation == null) throw new InvalidOperationException("No pending probe login");
                            loginCancellation.Cancel();
                            await WaitForLoginAsync(client, deadline.Token);
                        }
                        else if (name == "restart")
                        {
                            client.Dispose();
                            client = null;
                            sync = null;
                            client = new ForeverClient(root, serviceUrl, status);
                        }
                        else if (name == "disconnect") await client.DisconnectAsync(deadline.Token);
                        else if (name == "start")
                        {
                            reportId = await client.StartAsync(Json.Str(Json.Get(action, "title"), "Synthetic combat"),
                                Json.Str(Json.Get(action, "visibility"), "private"),
                                Json.Str(Json.Get(action, "timezone"), "Etc/UTC"), deadline.Token);
                            value = reportId;
                        }
                        else if (name == "startCancelOnRefresh")
                        {
                            using (CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
                            {
                                Task cancellation = CancelAfterRefreshSignalAsync(fixture, caller, deadline.Token);
                                Task<string> start = client.StartAsync(Json.Str(Json.Get(action, "title"), "Synthetic combat"),
                                    Json.Str(Json.Get(action, "visibility"), "private"),
                                    Json.Str(Json.Get(action, "timezone"), "Etc/UTC"), caller.Token);
                                await Task.WhenAll(start, cancellation);
                                reportId = start.Result;
                                value = reportId;
                            }
                        }
                        else if (name == "status") value = await client.StatusAsync(id, deadline.Token);
                        else if (name == "segment")
                            await client.SegmentAsync(id, Json.Int(Json.Get(action, "seq")),
                                Convert.FromBase64String(Json.Str(Json.Get(action, "gzip"))), deadline.Token);
                        else if (name == "heartbeat") await client.HeartbeatAsync(id, deadline.Token);
                        else if (name == "finalize") await client.FinalizeAsync(id, deadline.Token);
                        else if (name == "scan" || name == "flush")
                        {
                            if (sync == null) sync = new ForeverSynchronizer(root, client, status, delegate { return now; });
                            if (name == "flush") await sync.FlushAsync(deadline.Token);
                            else await sync.ScanAsync(Json.Str(Json.Get(action, "logPath"), Json.Str(Json.Get(request, "logPath"))),
                                Json.Bool(Json.Get(action, "gameRunning"), true),
                                Json.Str(Json.Get(action, "visibility"), "private"),
                                Json.Str(Json.Get(action, "timezone"), "Etc/UTC"), deadline.Token);
                        }
                        else if (name == "advance") now = now.AddSeconds(Json.Int(Json.Get(action, "seconds")));
                        else throw new InvalidOperationException("Unknown Forever probe action");
                    }
                    catch (Exception error)
                    {
                        ForeverException forever = error as ForeverException;
                        failure = new { type = error.GetType().Name, message = error.Message,
                            status = forever == null ? 0 : forever.Status,
                            code = forever == null ? null : forever.Code };
                    }
                    results.Add(new { action = name, value = value, error = failure,
                        connected = client != null && client.IsConnected,
                        pending = client != null && client.LoginPending,
                        account = client == null ? null : client.AccountName,
                        reportUrl = sync == null ? null : sync.ReportUrl });
                }
            }
            finally
            {
                if (client != null) client.Dispose();
                if (loginCancellation != null) loginCancellation.Dispose();
            }
        }
        string[] captured;
        lock (messages) captured = messages.ToArray();
        return new { results = results, messages = captured };
    }

    private static Uri FixtureUri(string value)
    {
        Uri uri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "http" ||
            uri.Host != "127.0.0.1" || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("Forever probe requires its numeric-loopback HTTP fixture");
        return uri;
    }

    private static async Task<int> VisitAuthorizeAsync(Uri fixture, string value, CancellationToken ct)
    {
        Uri uri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != fixture.Scheme ||
            uri.Host != fixture.Host || uri.Port != fixture.Port || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/api/auth/desktop/authorize")
            throw new InvalidOperationException("Probe authorization cannot leave its synthetic fixture");
        using (HttpClientHandler handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        using (HttpClient http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
        using (HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            return (int)response.StatusCode;
        }
    }

    private static async Task CancelAfterRefreshSignalAsync(Uri fixture, CancellationTokenSource caller, CancellationToken ct)
    {
        using (HttpClientHandler handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        using (HttpClient http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
        {
            using (HttpResponseMessage ready = await http.GetAsync(new Uri(fixture, "/__probe/refresh-ready"),
                HttpCompletionOption.ResponseHeadersRead, ct))
                ready.EnsureSuccessStatusCode();
            caller.Cancel();
            using (ByteArrayContent body = new ByteArrayContent(new byte[0]))
            using (HttpResponseMessage released = await http.PostAsync(new Uri(fixture, "/__probe/release-refresh"), body, ct))
                released.EnsureSuccessStatusCode();
        }
    }

    private static async Task WaitForLoginAsync(ForeverClient client, CancellationToken ct)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (client.LoginPending)
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("Synthetic login did not settle");
            await Task.Delay(10, ct);
        }
    }
}
