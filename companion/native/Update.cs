using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AurifyClient
{
    internal sealed class UpdateRelease
    {
        internal long Sequence, Length, IssuedAt, ExpiresAt;
        internal string Version, Id, Hash, Path;
    }

    internal sealed class UpdateCatalog
    {
        internal long Sequence, IssuedAt, ExpiresAt;
        internal string Envelope;
        internal readonly List<UpdateRelease> Releases = new List<UpdateRelease>();
    }

    internal static class NativeUpdate
    {
        internal const long MaximumPayload = 64L * 1024 * 1024;
        internal const int MaximumManifest = 32768;
        internal const int MaximumReleases = 32;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        // A single canonical grammar excludes duplicate/unknown keys, JSON coercions,
        // escapes, exponent numbers and alternate representations before parsing.
        private static readonly Regex Envelope = new Regex("\\A\\{\"payload\":\"([A-Za-z0-9+/]+={0,2})\",\"signature\":\"([A-Za-z0-9+/]+={0,2})\"\\}\\z", RegexOptions.CultureInvariant);
        private static readonly Regex LegacyPayload = new Regex("\\A\\{\"schema\":1,\"product\":\"Aurify\",\"channel\":\"stable\",\"sequence\":([1-9][0-9]{0,15}),\"releaseId\":\"([0-9a-f]{64})\",\"payloadPath\":\"(/members/api/aurify/download\\?release=[0-9a-f]{64})\",\"payloadSha256\":\"([0-9a-f]{64})\",\"payloadLength\":([1-9][0-9]{0,8}),\"issuedAt\":([1-9][0-9]{0,15}),\"expiresAt\":([1-9][0-9]{0,15})\\}\\z", RegexOptions.CultureInvariant);
        private static readonly Regex CatalogPayload = new Regex("\\A\\{\"schema\":2,\"product\":\"Aurify\",\"channel\":\"stable\",\"sequence\":([1-9][0-9]{0,15}),\"issuedAt\":([1-9][0-9]{0,15}),\"expiresAt\":([1-9][0-9]{0,15}),\"releases\":\\[([^\\r\\n]*)\\]\\}\\z", RegexOptions.CultureInvariant);
        private static readonly Regex CatalogEntry = new Regex("\\G\\{\"version\":\"(2\\.(?:0|[1-9][0-9]{0,4})\\.(?:0|[1-9][0-9]{0,4}))\",\"sequence\":([1-9][0-9]{0,15}),\"releaseId\":\"([0-9a-f]{64})\",\"payloadPath\":\"(/members/api/aurify/releases/[0-9a-f]{64})\",\"payloadSha256\":\"([0-9a-f]{64})\",\"payloadLength\":([1-9][0-9]{0,8})\\}", RegexOptions.CultureInvariant);

        internal static string PublicKey()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Aurify.Update.PublicKey"))
            {
                if (stream == null) throw new InvalidDataException("Update trust anchor is unavailable.");
                using (var reader = new StreamReader(stream, Utf8)) return reader.ReadToEnd();
            }
        }

        private static string VerifyEnvelope(string envelope, string publicKey)
        {
            if (envelope == null || envelope.Length > MaximumManifest) throw new InvalidDataException("Update manifest is too large.");
            Match outer = Envelope.Match(envelope);
            if (!outer.Success) throw new InvalidDataException("Update envelope is not canonical.");
            byte[] payload = Convert.FromBase64String(outer.Groups[1].Value);
            byte[] signature = Convert.FromBase64String(outer.Groups[2].Value);
            if (Convert.ToBase64String(payload) != outer.Groups[1].Value || Convert.ToBase64String(signature) != outer.Groups[2].Value || signature.Length != 384)
                throw new InvalidDataException("Update signature encoding is invalid.");
            using (var rsa = new RSACryptoServiceProvider(new CspParameters(24)))
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(publicKey);
                if (rsa.KeySize != 3072 || !rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"), signature))
                    throw new CryptographicException("Update authenticity could not be verified.");
            }
            return Utf8.GetString(payload);
        }

        private static void ValidateWindow(long issuedAt, long expiresAt, long now)
        {
            if (issuedAt > Json.MaxInteger || expiresAt > Json.MaxInteger ||
                issuedAt > now + 300000 || expiresAt <= now || expiresAt <= issuedAt ||
                expiresAt - issuedAt > 31L * 24 * 60 * 60 * 1000)
                throw new InvalidDataException("Update manifest is expired or outside policy.");
        }

        private static UpdateRelease ParseLegacy(string body, long now, long sequence)
        {
            Match match = LegacyPayload.Match(body);
            if (!match.Success) throw new InvalidDataException("Update payload is not canonical.");
            var release = new UpdateRelease {
                Sequence = Int64.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), Id = match.Groups[2].Value,
                Path = match.Groups[3].Value, Hash = match.Groups[4].Value,
                Length = Int64.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture),
                IssuedAt = Int64.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture),
                ExpiresAt = Int64.Parse(match.Groups[7].Value, CultureInfo.InvariantCulture)
            };
            ValidateWindow(release.IssuedAt, release.ExpiresAt, now);
            if (release.Sequence > Json.MaxInteger || release.Sequence <= sequence || release.Length > MaximumPayload ||
                release.Path != "/members/api/aurify/download?release=" + release.Id)
                throw new InvalidDataException("Update is out of sequence or outside policy.");
            return release;
        }

        private static UpdateCatalog ParseCatalog(string envelope, string body, long now)
        {
            Match match = CatalogPayload.Match(body);
            if (!match.Success) throw new InvalidDataException("Update catalog is not canonical.");
            var catalog = new UpdateCatalog {
                Envelope = envelope,
                Sequence = Int64.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                IssuedAt = Int64.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                ExpiresAt = Int64.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)
            };
            if (catalog.Sequence > Json.MaxInteger) throw new InvalidDataException("Update catalog sequence is outside policy.");
            ValidateWindow(catalog.IssuedAt, catalog.ExpiresAt, now);
            string entries = match.Groups[4].Value;
            int offset = 0;
            long previousSequence = Json.MaxInteger + 1;
            var versions = new HashSet<string>(StringComparer.Ordinal);
            var identities = new HashSet<string>(StringComparer.Ordinal);
            while (offset < entries.Length)
            {
                if (catalog.Releases.Count == MaximumReleases) throw new InvalidDataException("Update catalog has too many releases.");
                Match entry = CatalogEntry.Match(entries, offset);
                if (!entry.Success || entry.Index != offset) throw new InvalidDataException("Update catalog release is not canonical.");
                var release = new UpdateRelease {
                    Version = entry.Groups[1].Value,
                    Sequence = Int64.Parse(entry.Groups[2].Value, CultureInfo.InvariantCulture),
                    Id = entry.Groups[3].Value, Path = entry.Groups[4].Value, Hash = entry.Groups[5].Value,
                    Length = Int64.Parse(entry.Groups[6].Value, CultureInfo.InvariantCulture),
                    IssuedAt = catalog.IssuedAt, ExpiresAt = catalog.ExpiresAt
                };
                Version parsed;
                if (!System.Version.TryParse(release.Version, out parsed) || parsed.Major != 2 ||
                    parsed.Revision != -1 || parsed.ToString(3) != release.Version ||
                    release.Sequence > Json.MaxInteger || release.Sequence >= previousSequence ||
                    release.Length > MaximumPayload ||
                    release.Path != "/members/api/aurify/releases/" + release.Id ||
                    !versions.Add(release.Version) || !identities.Add(release.Id))
                    throw new InvalidDataException("Update catalog release is outside policy.");
                catalog.Releases.Add(release);
                previousSequence = release.Sequence;
                offset += entry.Length;
                if (offset == entries.Length) break;
                if (entries[offset] != ',' || offset + 1 == entries.Length) throw new InvalidDataException("Update catalog release list is not canonical.");
                offset++;
            }
            if (catalog.Releases.Count == 0 || catalog.Releases[0].Sequence != catalog.Sequence)
                throw new InvalidDataException("Update catalog has no current release.");
            return catalog;
        }


        internal static UpdateCatalog VerifyCatalog(string envelope, long now, string publicKey)
        {
            return ParseCatalog(envelope, VerifyEnvelope(envelope, publicKey), now);
        }

        internal static UpdateRelease VerifyLatest(string envelope, long now, long sequence, string publicKey, string releaseId, string version)
        {
            string body = VerifyEnvelope(envelope, publicKey);
            if (body.StartsWith("{\"schema\":1,", StringComparison.Ordinal))
            {
                UpdateRelease legacy = ParseLegacy(body, now, sequence);
                if (legacy.Id != releaseId) throw new InvalidDataException("Update release identity does not match its payload.");
                return legacy;
            }
            UpdateCatalog catalog = ParseCatalog(envelope, body, now);
            UpdateRelease release = catalog.Releases[0];
            if (release.Id != releaseId)
                throw new InvalidDataException("Only the latest release in the signed catalog can be installed.");
            if (release.Sequence <= sequence || release.Version != version)
                throw new InvalidDataException("Update release version or sequence does not match its payload.");
            return release;
        }

        internal static void VerifyFile(string path, UpdateRelease release)
        {
            Files.NoLinks(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create())
            {
                if (stream.Length != release.Length || BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() != release.Hash)
                    throw new InvalidDataException("Update download integrity failed.");
            }
        }

        private static async Task Fetch(HttpClient client, Uri uri, string token, Stream output, long limit, long expected, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count != 0 ||
                        (response.Content.Headers.ContentLength.HasValue && (response.Content.Headers.ContentLength > limit ||
                        (expected > 0 && response.Content.Headers.ContentLength != expected))))
                        throw new IOException("Update response was rejected.");
                    using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (ct.Register(delegate { input.Dispose(); }))
                    {
                        byte[] buffer = new byte[32768];
                        long total = 0;
                        int count;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) != 0)
                        {
                            total += count;
                            if (total > limit) throw new InvalidDataException("Update response exceeded its limit.");
                            await output.WriteAsync(buffer, 0, count, ct).ConfigureAwait(false);
                        }
                        if (expected > 0 && total != expected) throw new InvalidDataException("Update response was truncated.");
                    }
                }
            }
        }

        internal static UpdateRelease Latest(UpdateCatalog catalog, Aurify.Installation installed)
        {
            foreach (UpdateRelease release in catalog.Releases)
            {
                if (release.Sequence == installed.UpdateSequence &&
                    (String.IsNullOrEmpty(installed.UpdateRelease) || release.Id != installed.UpdateRelease))
                    throw new InvalidDataException("Update sequence was reused.");
            }
            UpdateRelease latest = catalog.Releases[0];
            return latest.Sequence > installed.UpdateSequence && latest.Id != installed.Current ? latest : null;
        }



        internal static async Task<UpdateCatalog> Check(string root, ClientConfig config, ClientCredentials credentials, CancellationToken ct)
        {
            if (credentials == null) return null;
            Uri origin = new Uri(ClientStorage.ValidateServerUrl(config.serverUrl));
            if (origin.Scheme != "https") return null;
            Aurify.Installation installed = Aurify.UpdateInstallation(root);
            if (installed == null || installed.RemoveToken != null || installed.Previous != null) return null;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (HttpClientHandler handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, AutomaticDecompression = DecompressionMethods.None })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(-1) })
            using (var memory = new MemoryStream())
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                timeout.CancelAfter(120000);
                await Fetch(client, new Uri(origin, "/members/api/aurify/updates"), credentials.deviceToken, memory, MaximumManifest, 0, timeout.Token).ConfigureAwait(false);
                UpdateCatalog catalog = VerifyCatalog(Utf8.GetString(memory.ToArray()), Json.Now(), PublicKey());
                Latest(catalog, installed);
                return catalog;
            }
        }

        internal static async Task Install(string root, ClientConfig config, ClientCredentials credentials, string envelope, CancellationToken ct)
        {
            if (credentials == null) throw new InvalidOperationException("Pair this device before installing an update.");
            Uri origin = new Uri(ClientStorage.ValidateServerUrl(config.serverUrl));
            if (origin.Scheme != "https") throw new InvalidOperationException("Application updates require HTTPS.");
            Aurify.Installation installed = Aurify.UpdateInstallation(root);
            if (installed == null || installed.RemoveToken != null || installed.Previous != null)
                throw new InvalidOperationException("The current installation is not ready to update.");
            UpdateCatalog catalog = VerifyCatalog(envelope, Json.Now(), PublicKey());
            UpdateRelease selected = Latest(catalog, installed);
            if (selected == null) throw new InvalidOperationException("No newer authenticated Aurify release is available.");
            string directory = System.IO.Path.Combine(root, "App", "updates");
            Files.PrivateDirectory(directory);
            string lockPath = System.IO.Path.Combine(directory, ".download.lock");
            Files.NoLinks(lockPath);
            using (var ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (HttpClientHandler handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, AutomaticDecompression = DecompressionMethods.None })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(-1) })
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                timeout.CancelAfter(120000);
                string candidate = System.IO.Path.Combine(directory, selected.Hash + ".exe");
                string manifest = candidate + ".json";
                Files.NoLinks(candidate);
                if (File.Exists(candidate)) VerifyFile(candidate, selected);
                else
                {
                    string temporary = System.IO.Path.Combine(directory, ".download-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768, FileOptions.WriteThrough))
                        {
                            await Fetch(client, new Uri(origin, selected.Path), credentials.deviceToken, output, selected.Length, selected.Length, timeout.Token).ConfigureAwait(false);
                            output.Flush(true);
                        }
                        VerifyFile(temporary, selected);
                        File.Move(temporary, candidate);
                    }
                    finally { if (File.Exists(temporary)) Files.Delete(temporary); }
                }
                Files.AtomicWrite(manifest, Utf8.GetBytes(envelope));
                ct.ThrowIfCancellationRequested();
                Aurify.LaunchUpdate(installed, candidate, manifest);
            }
        }
    }
}
