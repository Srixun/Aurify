using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AurifyClient
{
    internal sealed class ForeverException : Exception
    {
        internal int Status { get; private set; }
        internal string Code { get; private set; }
        internal ForeverException(string message, int status = 0, string code = null) : base(message)
        {
            Status = status;
            Code = code;
        }
    }

    internal sealed class ForeverClient : IDisposable
    {
        private const int ResponseLimit = 2 * 1024 * 1024;
        private readonly object stateLock = new object();
        private readonly SemaphoreSlim authGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Uri origin;
        private readonly string vaultPath;
        private readonly Action<string> status;
        private string refreshToken, accessToken, accountName, accountId;
        private DateTime accessExpires;
        private bool disposed, disconnecting, vaultFault;
        private long epoch;
        private Login pending;

        private sealed class Login
        {
            internal TcpListener Listener;
            internal CancellationTokenSource Cancellation;
            internal string State, Verifier, Redirect, Host;
            internal DateTime Expires;
        }

        private sealed class Reply
        {
            internal int Status;
            internal Dictionary<string, object> Body;
            internal bool HasJson;
        }

        internal ForeverClient(string root, Action<string> status) : this(root, "https://foreverlogs.gg", status) { }

        // This overload is only for in-process fixtures; it is never a setting or command-line option.
        internal ForeverClient(string root, string serviceUrl, Action<string> status)
        {
            Uri parsed;
            IPAddress address;
            if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out parsed) || parsed.UserInfo.Length != 0 ||
                parsed.Query.Length != 0 || parsed.Fragment.Length != 0 || parsed.AbsolutePath != "/" ||
                !((parsed.Scheme == "https" && parsed.Host == "foreverlogs.gg" && parsed.Port == 443) ||
                  (parsed.Scheme == "http" && IPAddress.TryParse(parsed.Host.Trim('[', ']'), out address) && IPAddress.IsLoopback(address))))
                throw new ForeverException("ForeverLogs service origin is invalid.");
            origin = parsed;
            vaultPath = Path.Combine(Path.GetFullPath(root), "config.forever.credentials");
            this.status = status;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            LoadVault();
        }

        internal bool IsConnected { get { lock (stateLock) return !disposed && !vaultFault && !String.IsNullOrEmpty(refreshToken); } }
        internal bool LoginPending { get { lock (stateLock) return pending != null; } }
        internal string AccountName { get { lock (stateLock) return accountName; } }
        internal string AccountId { get { lock (stateLock) return accountId; } }
        internal long ConnectionGeneration { get { lock (stateLock) return epoch; } }

        private void Notify(string message)
        {
            if (status == null) return;
            try { status(message); } catch { }
        }

        private void CheckAvailable()
        {
            if (disposed) throw new ObjectDisposedException("ForeverClient");
            if (vaultFault) throw new ForeverException("ForeverLogs protected credentials could not be saved. Reconnect after resolving local storage.", 0, "credential_storage");
            if (disconnecting) throw new ForeverException("ForeverLogs is disconnecting.", 0, "not_connected");
        }

        private static bool SecretValid(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 16384) return false;
            foreach (char c in value) if (c <= 32 || c > 126) return false;
            return true;
        }

        private static string Username(object value)
        {
            string name = value as string;
            if (name == null) return null;
            if (name.Length > 256) throw new ForeverException("ForeverLogs returned an invalid account response.", 0, "invalid_response");
            foreach (char c in name) if (Char.IsControl(c)) throw new ForeverException("ForeverLogs returned an invalid account response.", 0, "invalid_response");
            return name;
        }

        private static string Identity(object value)
        {
            string id = value as string;
            if (id == null && Json.Integer(value, 1)) id = Json.Int(value).ToString(CultureInfo.InvariantCulture);
            if (String.IsNullOrWhiteSpace(id) || id.Length > 256) throw new ForeverException("ForeverLogs returned an invalid account identity.", 0, "invalid_response");
            foreach (char c in id) if (Char.IsControl(c)) throw new ForeverException("ForeverLogs returned an invalid account identity.", 0, "invalid_response");
            return id;
        }

        private void LoadVault()
        {
            byte[] envelopeBytes = null, protectedBytes = null, plain = null;
            try
            {
                Files.NoLinks(vaultPath);
                try { File.GetAttributes(vaultPath); }
                catch (FileNotFoundException) { return; }
                catch (DirectoryNotFoundException) { return; }
                envelopeBytes = Files.ReadBytes(vaultPath, 128 * 1024);
                Dictionary<string, object> envelope = Json.Obj(Json.Parse(new UTF8Encoding(false, true).GetString(envelopeBytes)));
                if (!Json.Integer(Json.Get(envelope, "version"), 1, 1)) throw new InvalidDataException();
                protectedBytes = Convert.FromBase64String(Json.Str(Json.Get(envelope, "protected")) ?? "");
                plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                Dictionary<string, object> payload = Json.Obj(Json.Parse(new UTF8Encoding(false, true).GetString(plain)));
                string token = Json.Str(Json.Get(payload, "refreshToken"));
                if (!SecretValid(token)) throw new InvalidDataException();
                accountName = Username(Json.Get(payload, "username"));
                accountId = Identity(Json.Get(payload, "accountId"));
                refreshToken = token;
            }
            catch { throw new ForeverException("ForeverLogs protected credentials cannot be read for this Windows user. The stored file was not changed.", 0, "credential_storage"); }
            finally
            {
                if (envelopeBytes != null) Array.Clear(envelopeBytes, 0, envelopeBytes.Length);
                if (protectedBytes != null) Array.Clear(protectedBytes, 0, protectedBytes.Length);
                if (plain != null) Array.Clear(plain, 0, plain.Length);
            }
        }

        // Caller holds stateLock: cancellation/disconnect cannot race the durable rotation commit.
        private void SaveVault(string token, string username, string id)
        {
            byte[] plain = Encoding.UTF8.GetBytes(Json.Stringify(new Dictionary<string, object> { { "refreshToken", token }, { "username", username }, { "accountId", id } }));
            byte[] protectedBytes = null, envelope = null;
            try
            {
                protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                envelope = Encoding.UTF8.GetBytes(Json.Stringify(new Dictionary<string, object> { { "version", 1 }, { "protected", Convert.ToBase64String(protectedBytes) } }));
                Files.AtomicWrite(vaultPath, envelope);
            }
            catch
            {
                vaultFault = true;
                accessToken = null;
                throw new ForeverException("ForeverLogs protected credentials could not be saved. Reconnect after resolving local storage.", 0, "credential_storage");
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
                if (protectedBytes != null) Array.Clear(protectedBytes, 0, protectedBytes.Length);
                if (envelope != null) Array.Clear(envelope, 0, envelope.Length);
            }
        }

        private static string RandomUrlToken()
        {
            byte[] bytes = new byte[32];
            try
            {
                using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
                return Base64Url(bytes);
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        private static string Base64Url(byte[] bytes) { return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

        internal Task<string> BeginLoginAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Login login;
            string url;
            lock (stateLock)
            {
                CheckAvailable();
                if (pending != null) throw new ForeverException("ForeverLogs sign-in is already pending.", 0, "login_pending");
                login = new Login { State = RandomUrlToken(), Verifier = RandomUrlToken(), Expires = DateTime.UtcNow.AddMinutes(10) };
                login.Listener = new TcpListener(IPAddress.Loopback, 0);
                login.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
                try
                {
                    login.Listener.Start(8);
                    int port = ((IPEndPoint)login.Listener.LocalEndpoint).Port;
                    login.Host = "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
                    login.Redirect = "http://" + login.Host + "/callback";
                    string challenge;
                    using (SHA256 sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(login.Verifier)));
                    url = new Uri(origin, "/api/auth/desktop/authorize").AbsoluteUri + "?response_type=code&code_challenge=" + challenge +
                        "&code_challenge_method=S256&redirect_uri=" + Uri.EscapeDataString(login.Redirect) + "&scope=upload&state=" + login.State;
                    login.Cancellation.CancelAfter(TimeSpan.FromMinutes(10));
                    pending = login;
                }
                catch
                {
                    login.Listener.Stop();
                    login.Cancellation.Dispose();
                    throw new ForeverException("ForeverLogs could not start the local sign-in listener.", 0, "callback_unavailable");
                }
            }
            // No auth gate is held while the user considers browser consent.
            Task.Run(() => ListenAsync(login));
            Notify("ForeverLogs sign-in awaits browser approval.");
            return Task.FromResult(url);
        }

        internal void CancelLogin()
        {
            lock (stateLock)
            {
                if (pending == null) return;
                Login login = pending;
                pending = null;
                login.Cancellation.Cancel();
                login.Listener.Stop();
            }
        }

        private async Task ListenAsync(Login login)
        {
            CancellationToken ct = login.Cancellation.Token;
            string resultMessage = "ForeverLogs sign-in could not be completed.";
            try
            {
                using (ct.Register(() => login.Listener.Stop()))
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        using (TcpClient connection = await login.Listener.AcceptTcpClientAsync().ConfigureAwait(false))
                        using (CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            probe.CancelAfter(TimeSpan.FromSeconds(5));
                            using (probe.Token.Register(() => connection.Close()))
                            {
                                Dictionary<string, string> query = null;
                                try { query = await ReadCallbackAsync(connection.GetStream(), login, probe.Token).ConfigureAwait(false); }
                                catch (IOException) { }
                                catch (ObjectDisposedException) { }
                                catch (OperationCanceledException) { }
                                catch (InvalidOperationException) { }
                                catch (SocketException) { }
                                string state, code, error;
                                bool valid = query != null && query.TryGetValue("state", out state) && EqualState(state, login.State);
                                code = query != null && query.ContainsKey("code") ? query["code"] : null;
                                error = query != null && query.ContainsKey("error") ? query["error"] : null;
                                valid = valid && ((SecretValid(code) && String.IsNullOrEmpty(error)) || (!String.IsNullOrEmpty(error) && String.IsNullOrEmpty(code)));
                                if (!valid)
                                {
                                    await AnswerCallbackAsync(connection, false, probe.Token).ConfigureAwait(false);
                                    continue;
                                }
                                ct.ThrowIfCancellationRequested();
                                if (DateTime.UtcNow >= login.Expires) throw new OperationCanceledException(ct);
                                login.Cancellation.CancelAfter(Timeout.Infinite);
                                ct.ThrowIfCancellationRequested();
                                login.Listener.Stop();
                                await AnswerCallbackAsync(connection, true, probe.Token).ConfigureAwait(false);
                                if (!String.IsNullOrEmpty(error))
                                {
                                    resultMessage = error == "access_denied" ? "ForeverLogs sign-in was denied." : "ForeverLogs sign-in was not approved.";
                                    return;
                                }
                                await CompleteLoginAsync(login, code, ct).ConfigureAwait(false);
                                resultMessage = "ForeverLogs connected.";
                                return;
                            }
                        }
                    }
                }
            }
            catch (ForeverException ex) { resultMessage = ex.Message; }
            catch (Exception) { resultMessage = ct.IsCancellationRequested ? "ForeverLogs sign-in was cancelled or expired." : "ForeverLogs sign-in could not be completed."; }
            finally
            {
                login.Listener.Stop();
                lock (stateLock)
                {
                    if (Object.ReferenceEquals(pending, login)) pending = null;
                    login.Cancellation.Dispose();
                }
                Notify(resultMessage);
            }
        }

        private static bool EqualState(string left, string right)
        {
            if (left == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        private static async Task<Dictionary<string, string>> ReadCallbackAsync(NetworkStream stream, Login login, CancellationToken ct)
        {
            byte[] bytes = new byte[16384];
            int used = 0, end = -1;
            while (used < bytes.Length && end < 0)
            {
                int count = await stream.ReadAsync(bytes, used, bytes.Length - used, ct).ConfigureAwait(false);
                if (count == 0) return null;
                int previous = used;
                used += count;
                for (int i = Math.Max(0, previous - 3); i + 3 < used; i++)
                    if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10) { end = i; break; }
            }
            if (end < 0 || end + 4 != used) return null;
            for (int i = 0; i < end; i++) if (bytes[i] > 126 || (bytes[i] < 32 && bytes[i] != 13 && bytes[i] != 10)) return null;
            string[] lines = Encoding.ASCII.GetString(bytes, 0, end).Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string[] request = lines[0].Split(' ');
            if (request.Length != 3 || request[0] != "GET" || request[2] != "HTTP/1.1" || !request[1].StartsWith("/callback?", StringComparison.Ordinal) || request[1].IndexOf('#') >= 0) return null;
            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0 || Char.IsWhiteSpace(lines[i][0])) return null;
                string name = lines[i].Substring(0, colon);
                foreach (char c in name) if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '-')) return null;
                if (headers.ContainsKey(name)) return null;
                headers.Add(name, lines[i].Substring(colon + 1).Trim());
            }
            string host, length;
            if (!headers.TryGetValue("Host", out host) || host != login.Host || headers.ContainsKey("Transfer-Encoding") ||
                (headers.TryGetValue("Content-Length", out length) && length != "0")) return null;
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string part in request[1].Substring(10).Split('&'))
            {
                int equals = part.IndexOf('=');
                if (equals <= 0) return null;
                string key, value;
                if (!DecodeQuery(part.Substring(0, equals), out key) || !DecodeQuery(part.Substring(equals + 1), out value) || result.ContainsKey(key)) return null;
                result.Add(key, value);
            }
            return result;
        }

        private static bool DecodeQuery(string encoded, out string value)
        {
            value = null;
            for (int i = 0; i < encoded.Length; i++)
            {
                if (encoded[i] == '%')
                {
                    if (i + 2 >= encoded.Length || !Uri.IsHexDigit(encoded[i + 1]) || !Uri.IsHexDigit(encoded[i + 2])) return false;
                    i += 2;
                }
            }
            try { value = Uri.UnescapeDataString(encoded.Replace('+', ' ')); }
            catch (UriFormatException) { return false; }
            foreach (char c in value) if (Char.IsControl(c)) return false;
            return true;
        }

        private static async Task AnswerCallbackAsync(TcpClient connection, bool accepted, CancellationToken ct)
        {
            string body = accepted ? "Sign-in response received. Return to Aurify." : "Invalid sign-in callback.";
            byte[] bytes = Encoding.ASCII.GetBytes("HTTP/1.1 " + (accepted ? "200 OK" : "400 Bad Request") + "\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n" + body);
            try { await connection.GetStream().WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
            catch (SocketException) { }
        }

        private async Task CompleteLoginAsync(Login login, string code, CancellationToken ct)
        {
            await authGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (stateLock)
                {
                    CheckAvailable();
                    if (!Object.ReferenceEquals(pending, login)) throw new OperationCanceledException(ct);
                }
                Reply reply = await SendAsync("POST", "/api/auth/desktop/token", JsonBytes(new Dictionary<string, object> {
                    { "code", code }, { "code_verifier", login.Verifier }, { "redirect_uri", login.Redirect }, { "device_label", "Aurify" }
                }), null, null, 30, ct).ConfigureAwait(false);
                RequireSuccess(reply);
                lock (stateLock)
                {
                    ct.ThrowIfCancellationRequested();
                    CheckAvailable();
                    if (!Object.ReferenceEquals(pending, login)) throw new OperationCanceledException(ct);
                    InstallTokens(reply.Body, true, null, null, null);
                    epoch++;
                }
            }
            finally { authGate.Release(); }
        }

        private void InstallTokens(Dictionary<string, object> body, bool initial, string previousRefresh, string previousName, string previousId)
        {
            string access = Json.Str(Json.Get(body, "accessToken"));
            object refreshValue = Json.Get(body, "refreshToken");
            string refresh = refreshValue == null && !initial ? previousRefresh : Json.Str(refreshValue);
            if (!SecretValid(access) || !SecretValid(refresh)) throw new ForeverException("ForeverLogs returned an invalid authentication response.", 0, "invalid_response");
            object expiry = Json.Get(body, "expiresIn");
            if (expiry != null && !Json.Integer(expiry, 1, 31536000)) throw new ForeverException("ForeverLogs returned an invalid authentication response.", 0, "invalid_response");
            long seconds = expiry == null ? 3600 : Json.Int(expiry);
            object user = Json.Get(body, "user");
            string name = user == null && !initial ? previousName : Username(Json.Get(Json.Obj(user), "username"));
            string id = user == null && !initial ? previousId : Identity(Json.Get(Json.Obj(user), "id"));
            if (!initial && !String.Equals(id, previousId, StringComparison.Ordinal))
                throw new ForeverException("ForeverLogs returned a different account during refresh. Connect again.", 0, "account_mismatch");
            SaveVault(refresh, name, id);
            // Disposal cannot discard a completed rotation or repopulate in-memory credentials.
            if (disposed) return;
            refreshToken = refresh;
            accessToken = access;
            accountName = name;
            accountId = id;
            accessExpires = DateTime.UtcNow.AddSeconds(seconds);
        }

        private async Task<string> AccessAsync(string rejected, CancellationToken ct)
        {
            await authGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string refresh, previousName, previousId;
                long generation;
                lock (stateLock)
                {
                    CheckAvailable();
                    if (String.IsNullOrEmpty(refreshToken)) throw new ForeverException("ForeverLogs is not connected.", 401, "not_connected");
                    if (accessToken != null && accessExpires > DateTime.UtcNow.AddSeconds(30) && (rejected == null || accessToken != rejected)) return accessToken;
                    refresh = refreshToken;
                    previousName = accountName;
                    previousId = accountId;
                    generation = epoch;
                }
                ct.ThrowIfCancellationRequested();
                // A rotating credential may already be consumed remotely after dispatch.
                // Finish the bounded exchange and durable commit before observing cancellation.
                Reply reply = await SendAsync("POST", "/api/auth/desktop/refresh", JsonBytes(new Dictionary<string, object> { { "refreshToken", refresh } }), null, null, 30, CancellationToken.None, true).ConfigureAwait(false);
                lock (stateLock)
                {
                    if (generation != epoch) throw new ForeverException("ForeverLogs connection changed.", 401, "not_connected");
                    if (reply.Status == 400 || reply.Status == 401)
                    {
                        ClearVault();
                        throw new ForeverException("ForeverLogs sign-in expired. Connect again.", reply.Status, "invalid_grant");
                    }
                    RequireSuccess(reply);
                    InstallTokens(reply.Body, false, refresh, previousName, previousId);
                    ct.ThrowIfCancellationRequested();
                    if (disposed) throw new ObjectDisposedException("ForeverClient");
                    return accessToken;
                }
            }
            finally { authGate.Release(); }
        }

        private void ClearVault()
        {
            refreshToken = null;
            accessToken = null;
            accountName = null;
            accountId = null;
            accessExpires = DateTime.MinValue;
            epoch++;
            try { Files.Delete(vaultPath); vaultFault = false; }
            catch { vaultFault = true; throw new ForeverException("ForeverLogs protected credentials could not be removed.", 0, "credential_storage"); }
        }

        internal async Task DisconnectAsync(CancellationToken ct)
        {
            string refresh;
            lock (stateLock)
            {
                if (disposed) throw new ObjectDisposedException("ForeverClient");
                if (disconnecting) throw new ForeverException("ForeverLogs is already disconnecting.");
                disconnecting = true;
                CancelLogin();
            }
            try
            {
                await authGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    lock (stateLock) { refresh = refreshToken; ClearVault(); }
                    if (refresh != null)
                    {
                        try
                        {
                            Reply reply = await SendAsync("POST", "/api/auth/desktop/revoke", JsonBytes(new Dictionary<string, object> { { "refreshToken", refresh } }), null, null, 30, ct).ConfigureAwait(false);
                            RequireSuccess(reply);
                        }
                        catch (ForeverException) { Notify("ForeverLogs disconnected locally; remote sign-out could not be confirmed."); }
                    }
                }
                finally { authGate.Release(); }
            }
            finally { lock (stateLock) disconnecting = false; }
        }

        private static byte[] JsonBytes(object value) { return Encoding.UTF8.GetBytes(Json.Stringify(value)); }

        private async Task<Reply> AuthorizedAsync(string method, string path, byte[] body, Dictionary<string, string> headers, int seconds, CancellationToken ct)
        {
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token))
            {
                long generation;
                lock (stateLock) { CheckAvailable(); generation = epoch; }
                string token = await AccessAsync(null, linked.Token).ConfigureAwait(false);
                CheckGeneration(generation);
                Reply reply = await SendAsync(method, path, body, token, headers, seconds, linked.Token).ConfigureAwait(false);
                if (reply.Status != 401) return reply;
                CheckGeneration(generation);
                token = await AccessAsync(token, linked.Token).ConfigureAwait(false);
                CheckGeneration(generation);
                return await SendAsync(method, path, body, token, headers, seconds, linked.Token).ConfigureAwait(false);
            }
        }

        private void CheckGeneration(long generation)
        {
            lock (stateLock)
            {
                CheckAvailable();
                if (epoch != generation) throw new ForeverException("ForeverLogs connection changed. Pending uploads were kept.", 401, "account_mismatch");
            }
        }

        private async Task<Reply> SendAsync(string method, string path, byte[] body, string bearer, Dictionary<string, string> headers, int seconds, CancellationToken ct, bool completeRotation = false)
        {
            Uri target = new Uri(origin, path);
            if (target.Scheme != origin.Scheme || target.Host != origin.Host || target.Port != origin.Port || target.UserInfo.Length != 0 || !path.StartsWith("/api/", StringComparison.Ordinal))
                throw new ForeverException("ForeverLogs request origin is invalid.");
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, completeRotation ? CancellationToken.None : lifetime.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(target);
                request.Method = method;
                request.AllowAutoRedirect = false;
                request.UseDefaultCredentials = false;
                request.Credentials = null;
                request.CookieContainer = null;
                request.AutomaticDecompression = DecompressionMethods.None;
                request.Timeout = seconds * 1000;
                request.ReadWriteTimeout = seconds * 1000;
                request.Accept = "application/json";
                request.Headers["X-Client"] = "aurify";
                request.Headers["X-Client-Version"] = "2.0.0";
                request.Headers["X-Client-Platform"] = "win32";
                if (bearer != null) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + bearer;
                if (headers != null) foreach (KeyValuePair<string, string> pair in headers) request.Headers[pair.Key] = pair.Value;
                request.ContentType = headers == null ? "application/json" : "application/gzip";
                if (body != null) request.ContentLength = body.Length;
                using (timeout.Token.Register(() => request.Abort()))
                {
                    try
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        if (body != null)
                            using (Stream output = await request.GetRequestStreamAsync().ConfigureAwait(false))
                                await output.WriteAsync(body, 0, body.Length, timeout.Token).ConfigureAwait(false);
                        HttpWebResponse response = null;
                        try { response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false); }
                        catch (WebException ex)
                        {
                            response = ex.Response as HttpWebResponse;
                            if (response == null) throw;
                        }
                        using (response)
                        {
                            int code = (int)response.StatusCode;
                            if (code >= 300 && code < 400) throw new ForeverException("ForeverLogs refused an unexpected redirect.", code, "redirect");
                            if (response.ContentLength > ResponseLimit) throw new ForeverException("ForeverLogs response exceeded the size limit.", code, "response_too_large");
                            Dictionary<string, object> payload = null;
                            using (Stream input = response.GetResponseStream())
                            using (MemoryStream buffer = new MemoryStream())
                            {
                                byte[] chunk = new byte[8192];
                                while (true)
                                {
                                    int count = await input.ReadAsync(chunk, 0, chunk.Length, timeout.Token).ConfigureAwait(false);
                                    if (count == 0) break;
                                    if (buffer.Length + count > ResponseLimit) throw new ForeverException("ForeverLogs response exceeded the size limit.", code, "response_too_large");
                                    buffer.Write(chunk, 0, count);
                                }
                                if (buffer.Length != 0)
                                {
                                    try { payload = Json.Parse(new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length)) as Dictionary<string, object>; }
                                    catch (Exception) { payload = null; }
                                }
                            }
                            return new Reply { Status = code, Body = payload ?? Json.New(), HasJson = payload != null };
                        }
                    }
                    catch (ForeverException) { throw; }
                    catch (Exception ex)
                    {
                        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                        if (!completeRotation && lifetime.IsCancellationRequested) throw new OperationCanceledException(lifetime.Token);
                        if (timeout.IsCancellationRequested) throw new ForeverException("ForeverLogs request timed out. Pending uploads were kept.", 0, "timeout");
                        if (ex is WebException || ex is IOException || ex is SocketException || ex is ObjectDisposedException)
                            throw new ForeverException("ForeverLogs is unavailable. Pending uploads were kept.", 0, "network_error");
                        throw new ForeverException("ForeverLogs request could not be completed.", 0, "request_failed");
                    }
                }
            }
        }

        private static void RequireSuccess(Reply reply)
        {
            if (reply.Status >= 200 && reply.Status < 300) return;
            string code = Json.Str(Json.Get(reply.Body, "error"));
            if (code != "out_of_order" && code != "invalid_grant" && code != "unauthorized" && code != "not_found" && code != "session_expired") code = "http_error";
            throw new ForeverException(reply.Status == 401 ? "ForeverLogs authorization failed. Connect again if needed." : "ForeverLogs rejected the request. Pending uploads were kept.", reply.Status, code);
        }

        private static string ReportId(string reportId)
        {
            if (String.IsNullOrEmpty(reportId) || reportId.Length > 20 || reportId[0] == '0') throw new ForeverException("ForeverLogs report identity is invalid.", 0, "invalid_report");
            foreach (char c in reportId) if (c < '0' || c > '9') throw new ForeverException("ForeverLogs report identity is invalid.", 0, "invalid_report");
            return reportId;
        }

        internal async Task<string> StartAsync(string title, string visibility, string timezone, CancellationToken ct)
        {
            if (visibility != "private" && visibility != "public") throw new ForeverException("ForeverLogs visibility must be private or public.");
            Reply reply = await AuthorizedAsync("POST", "/api/live/start", JsonBytes(new Dictionary<string, object> { { "title", title }, { "visibility", visibility }, { "timezone", timezone } }), null, 30, ct).ConfigureAwait(false);
            RequireSuccess(reply);
            object id = Json.Get(reply.Body, "reportId") ?? Json.Get(Json.Obj(Json.Get(reply.Body, "report")), "id");
            string text = id as string;
            if (text == null && Json.Integer(id, 1)) text = Json.Int(id).ToString(CultureInfo.InvariantCulture);
            return ReportId(text);
        }

        internal async Task SegmentAsync(string reportId, long seq, byte[] gzip, CancellationToken ct)
        {
            ReportId(reportId);
            if (seq < 1 || seq > Json.MaxInteger || gzip == null) throw new ForeverException("ForeverLogs segment is invalid.");
            string sequence = seq.ToString(CultureInfo.InvariantCulture);
            Reply reply = await AuthorizedAsync("POST", "/api/live/segment?reportId=" + reportId + "&seq=" + sequence, gzip,
                new Dictionary<string, string> { { "X-Live-Report-Id", reportId }, { "X-Live-Seq", sequence } }, 60, ct).ConfigureAwait(false);
            object expected = Json.Get(reply.Body, "expectedSeq");
            if (reply.Status == 409 && Json.Str(Json.Get(reply.Body, "error")) == "out_of_order" && Json.Integer(expected, 1) && Json.Int(expected) > seq) return;
            RequireSuccess(reply);
        }

        internal async Task<Dictionary<string, object>> StatusAsync(string reportId, CancellationToken ct)
        {
            Reply reply = await AuthorizedAsync("GET", "/api/live/" + ReportId(reportId) + "/status", null, null, 30, ct).ConfigureAwait(false);
            RequireSuccess(reply);
            if (!reply.HasJson) throw new ForeverException("ForeverLogs returned an invalid status response.", reply.Status, "invalid_response");
            return reply.Body;
        }

        internal async Task HeartbeatAsync(string reportId, CancellationToken ct)
        {
            Reply reply = await AuthorizedAsync("POST", "/api/live/heartbeat", JsonBytes(new Dictionary<string, object> { { "reportId", ReportId(reportId) } }), null, 30, ct).ConfigureAwait(false);
            RequireSuccess(reply);
        }

        internal async Task FinalizeAsync(string reportId, CancellationToken ct)
        {
            Reply reply = await AuthorizedAsync("POST", "/api/live/finalize", JsonBytes(new Dictionary<string, object> { { "reportId", ReportId(reportId) } }), null, 60, ct).ConfigureAwait(false);
            RequireSuccess(reply);
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (disposed) return;
                disposed = true;
                // Account generation is unchanged: an already dispatched refresh must still
                // commit its replacement to disk, without restoring disposed memory state.
                CancelLogin();
                lifetime.Cancel();
                accessToken = null;
                refreshToken = null;
                accountName = null;
                accountId = null;
            }
            // In-flight async operations still reference the gate and lifetime token.
        }
    }
}
