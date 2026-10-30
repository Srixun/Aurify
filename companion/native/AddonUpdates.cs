using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace AurifyClient
{
    internal sealed class AddonUpdateResult
    {
        public string Status;
        public string Message;
        public string InstalledVersion;
        public string LatestVersion;
        public string ReleaseUrl;
        public bool Retryable;
    }

    internal static class AddonUpdates
    {
        private const int ForeverInterface = 16001;
        private const int MaximumPackageBytes = 16 * 1024 * 1024;
        private static readonly string[] Keys = { "professions", "overlord" };

        private sealed class UpdateSource
        {
            internal Dictionary<string, AddonUpdateResult> Results;
            internal List<AddonPackage> Packages;
        }

        public static async Task<AddonSetup> ApplyAsync(
            GameInstallation installation,
            ClientConfig settings,
            ClientCredentials approval,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            PortalClient portal = null;
            UpdateSource source = new UpdateSource {
                Results = UnavailableResults(installation, "Pair this companion to check addon updates."),
                Packages = new List<AddonPackage>()
            };
            try
            {
                if (approval != null && !String.IsNullOrWhiteSpace(approval.deviceToken))
                {
                    try
                    {
                        portal = new PortalClient(settings == null ? null : settings.serverUrl, approval.deviceToken);
                        source = await ReadSourceAsync(portal, installation, ct).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (portal != null) portal.Dispose();
                        portal = null;
                        source = new UpdateSource {
                            Results = UnavailableResults(installation, "Addon update information is temporarily unavailable.", Retryable(error)),
                            Packages = new List<AddonPackage>()
                        };
                    }
                }

                AddonFetch fetch = source.Packages.Count == 0 || portal == null
                    ? null : new AddonFetch(portal.GetAddonPackageAsync);
                AddonSetup setup = await AddonInstaller.InstallAsync(
                    installation, ct, source.Packages, fetch).ConfigureAwait(false);
                foreach (string key in Keys)
                {
                    AddonUpdateResult update;
                    AddonResult installed;
                    if (source.Results.TryGetValue(key, out update) &&
                        update.Status != "unavailable" &&
                        setup.Results.TryGetValue(key, out installed))
                        ApplyInstallOutcome(key, update, installed);
                }
                setup.Updates = source.Results;
                return setup;
            }
            finally
            {
                if (portal != null) portal.Dispose();
            }
        }


        private static async Task<UpdateSource> ReadSourceAsync(
            PortalClient portal,
            GameInstallation installation,
            CancellationToken ct)
        {
            Dictionary<string, object> payload = await portal.GetAddonUpdatesAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Dictionary<string, object> releases;
            if (!ValidEnvelope(payload, out releases))
                return new UpdateSource {
                    Results = UnavailableResults(installation, "The portal returned invalid addon update information."),
                    Packages = new List<AddonPackage>()
                };

            var source = new UpdateSource {
                Results = NewResults(),
                Packages = new List<AddonPackage>()
            };
            foreach (string key in Keys)
            {
                try
                {
                    object value = Json.Get(releases, key);
                    AddonUpdateResult result = EvaluateRelease(key, value, installation);
                    if (result.Status != "unavailable")
                    {
                        AddonPackage package;
                        if (!TryPackage(key, value, portal.ServerUrl, out package))
                            result = Unavailable(key, installation, "The portal returned invalid addon update information.");
                        else
                            source.Packages.Add(package);
                    }
                    source.Results[key] = result;
                }
                catch
                {
                    source.Results[key] = Unavailable(key, installation, "The portal returned invalid addon update information.");
                }
            }
            ct.ThrowIfCancellationRequested();
            return source;
        }
        private static bool Retryable(Exception error)
        {
            PortalException portal = error as PortalException;
            if (portal != null)
                return portal.Status == 0 || portal.Status == 408 || portal.Status == 409 ||
                    portal.Status == 429 || portal.Status >= 500;
            return error is HttpRequestException || error is OperationCanceledException ||
                error is TimeoutException;
        }


        private static bool ValidEnvelope(Dictionary<string, object> payload, out Dictionary<string, object> releases)
        {
            releases = payload == null ? null : Json.Get(payload, "addons") as Dictionary<string, object>;
            return releases != null &&
                Json.Integer(Json.Get(payload, "schema"), 1, 1) &&
                Json.Integer(Json.Get(payload, "interface"), ForeverInterface, ForeverInterface) &&
                Json.Integer(Json.Get(payload, "checkedAt"), 1, Json.MaxInteger);
        }

        private static AddonUpdateResult EvaluateRelease(string key, object value, GameInstallation installation)
        {
            Dictionary<string, object> release = value as Dictionary<string, object>;
            if (release == null)
                return Unavailable(key, installation, "The portal returned invalid addon update information.");

            string status = Json.Str(Json.Get(release, "status"));
            if (status == "unavailable")
            {
                string reason = Json.Str(Json.Get(release, "reason"));
                if (reason == "not-configured")
                    return Unavailable(key, installation, Name(key) + " update checks are not configured on the portal.");
                if (reason == "no-compatible-release")
                    return Unavailable(key, installation, "No compatible " + Name(key) + " release is available.");
                if (reason == "upstream-unavailable")
                    return Unavailable(key, installation, Name(key) + " update information is temporarily unavailable.", true);
                return Unavailable(key, installation, "The portal returned invalid addon update information.");
            }
            if (status != "ok")
                return Unavailable(key, installation, "The portal returned invalid addon update information.");

            string latestText = Json.Str(Json.Get(release, "version"));
            Version latest;
            string releaseUrl = Json.Str(Json.Get(release, "url"));
            if (!AddonVersions.IsSupported(key, latestText) ||
                !AddonVersions.TryParse(latestText, out latest) ||
                !TrustedReleaseUrl(key, latestText, releaseUrl))
                return Unavailable(key, installation, "The portal returned invalid addon update information.");

            AddonInfo addon = InstalledAddon(installation, key);
            Version installed;
            string installedText = ValidObservedVersion(key, addon, out installed);
            AddonUpdateResult result = new AddonUpdateResult {
                InstalledVersion = installedText,
                LatestVersion = latestText,
                ReleaseUrl = releaseUrl
            };
            if (addon == null || addon.Status != "ready" || addon.Interface != ForeverInterface || installedText == null)
            {
                result.Status = "unknown";
                result.Message = "Latest compatible version: " + latestText + ". The installed " + Name(key) + " version could not be verified.";
                return result;
            }
            if (installed.CompareTo(latest) < 0)
            {
                result.Status = "update-available";
                result.Message = Name(key) + " " + latestText + " is available (installed " + installedText + ").";
                return result;
            }
            result.Status = "current";
            result.Message = Name(key) + " " + installedText + " is current (latest compatible version: " + latestText + ").";
            return result;
        }

        private static Dictionary<string, AddonUpdateResult> UnavailableResults(GameInstallation installation, string message, bool retryable = false)
        {
            Dictionary<string, AddonUpdateResult> results = NewResults();
            foreach (string key in Keys) results[key] = Unavailable(key, installation, message, retryable);
            return results;
        }

        private static Dictionary<string, AddonUpdateResult> NewResults()
        {
            return new Dictionary<string, AddonUpdateResult>(StringComparer.Ordinal);
        }

        private static AddonUpdateResult Unavailable(string key, GameInstallation installation, string message, bool retryable = false)
        {
            Version ignored;
            return new AddonUpdateResult {
                Status = "unavailable",
                Message = message,
                InstalledVersion = ValidObservedVersion(key, InstalledAddon(installation, key), out ignored),
                Retryable = retryable
            };
        }

        private static AddonInfo InstalledAddon(GameInstallation installation, string key)
        {
            AddonInfo addon;
            return installation != null && installation.Addons != null && installation.Addons.TryGetValue(key, out addon) ? addon : null;
        }

        private static string ValidObservedVersion(string key, AddonInfo addon, out Version version)
        {
            if (addon != null && AddonVersions.IsSupported(key, addon.Version) &&
                AddonVersions.TryParse(addon.Version, out version))
                return addon.Version;
            version = null;
            return null;
        }

        private static string Name(string key)
        {
            return key == "professions" ? "Profession Master" : "Overlord Forever";
        }

        private static void ApplyInstallOutcome(
            string key,
            AddonUpdateResult update,
            AddonResult installed)
        {
            if (installed == null) return;
            if (!String.IsNullOrEmpty(installed.Version))
                update.InstalledVersion = installed.Version;
            if (installed.Status == "installed")
            {
                update.Status = "installed";
                update.Message = installed.Message;
                return;
            }
            if (installed.Status == "present")
            {
                Version actual, latest;
                if (AddonVersions.IsSupported(key, installed.Version) &&
                    AddonVersions.TryParse(installed.Version, out actual) &&
                    AddonVersions.TryParse(update.LatestVersion, out latest) &&
                    actual.CompareTo(latest) >= 0)
                {
                    update.Status = "current";
                    update.Message = Name(key) + " " + installed.Version +
                        " is current (latest compatible version: " + update.LatestVersion + ").";
                }
                else
                {
                    update.Status = "unknown";
                    update.Message = installed.Message;
                }
                return;
            }
            update.Status = installed.Status;
            update.Message = installed.Message;
            update.Retryable = installed.Retryable;
        }

        private static bool TryPackage(
            string key,
            object value,
            string serverUrl,
            out AddonPackage result)
        {
            result = null;
            Dictionary<string, object> release = value as Dictionary<string, object>;
            Dictionary<string, object> package = release == null
                ? null : Json.Get(release, "package") as Dictionary<string, object>;
            string releaseVersion = release == null ? null : Json.Str(Json.Get(release, "version"));
            string sha256 = package == null ? null : Json.Str(Json.Get(package, "sha256"));
            string path = package == null ? null : Json.Str(Json.Get(package, "path"));
            object size = package == null ? null : Json.Get(package, "bytes");
            if (release == null || Json.Str(Json.Get(release, "status")) != "ok" ||
                !AddonVersions.IsSupported(key, releaseVersion) ||
                !Json.Integer(size, 1, MaximumPackageBytes) ||
                !LowerHex64(sha256) ||
                path != "/members/api/aurify/addon-packages/" + key + "/" + sha256 + ".zip")
                return false;

            Uri origin, target;
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out origin) ||
                !TrustedPortalOrigin(origin) ||
                !Uri.TryCreate(origin, path, out target) ||
                target.Scheme != origin.Scheme ||
                !String.IsNullOrEmpty(target.UserInfo) ||
                !String.IsNullOrEmpty(target.Query) ||
                !String.IsNullOrEmpty(target.Fragment) ||
                !String.Equals(target.GetLeftPart(UriPartial.Authority),
                    origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) ||
                target.AbsolutePath != path)
                return false;

            AddonPackage required = null;
            foreach (AddonPackage candidate in AddonInstaller.Catalog)
                if (candidate.Key == key) { required = candidate; break; }
            if (required == null) return false;
            result = new AddonPackage(
                required.Key,
                required.Directory,
                required.Name,
                releaseVersion,
                required.Toc,
                required.Title,
                target.AbsoluteUri,
                sha256,
                Json.Int(size));
            return true;
        }

        private static bool LowerHex64(string value)
        {
            if (value == null || value.Length != 64) return false;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (!((character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f')))
                    return false;
            }
            return true;
        }

        internal static bool TrustedPortalOrigin(Uri origin)
        {
            if (origin == null || !String.IsNullOrEmpty(origin.UserInfo) ||
                origin.AbsolutePath != "/" || !String.IsNullOrEmpty(origin.Query) ||
                !String.IsNullOrEmpty(origin.Fragment))
                return false;
            if (origin.Scheme == Uri.UriSchemeHttps && origin.IsDefaultPort &&
                origin.Host.Equals("auratism.com", StringComparison.OrdinalIgnoreCase))
                return true;
            bool loopback =
                origin.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                origin.Host == "127.0.0.1" || origin.Host == "[::1]" || origin.Host == "::1";
            return loopback &&
                (origin.Scheme == Uri.UriSchemeHttp || origin.Scheme == Uri.UriSchemeHttps);
        }

        private static bool TrustedReleaseUrl(string key, string version, string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 512) return false;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.Port != 443 ||
                !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment))
                return false;

            if (key == "overlord")
            {
                const string prefix = "https://github.com/leliondoc/overlord-forever-addon/releases/tag/";
                string path = "/leliondoc/overlord-forever-addon/releases/tag/" + version;
                return value == prefix + version && uri.Host == "github.com" && uri.AbsolutePath == path;
            }
            if (key == "professions")
            {
                const string prefix = "https://www.curseforge.com/wow/addons/profession-master/files/";
                if (!value.StartsWith(prefix, StringComparison.Ordinal) || uri.Host != "www.curseforge.com") return false;
                string file = value.Substring(prefix.Length);
                long fileId;
                if (file.Length == 0 || file.Length > 19 || (file.Length > 1 && file[0] == '0') ||
                    !Int64.TryParse(file, NumberStyles.None, CultureInfo.InvariantCulture, out fileId) || fileId <= 0)
                    return false;
                return uri.AbsolutePath == "/wow/addons/profession-master/files/" + file;
            }
            return false;
        }
    }

    internal sealed partial class PortalClient
    {
        internal Task<Dictionary<string, object>> GetAddonUpdatesAsync(CancellationToken ct)
        {
            return RequestAsync(AddonUpdatesRoute, null, true, ct, 35000);
        }

        internal async Task<HttpResponseMessage> GetAddonPackageAsync(
            AddonPackage package,
            CancellationToken ct)
        {
            if (package == null || String.IsNullOrEmpty(token))
                throw new PortalException("This companion is not paired", 401);
            Uri origin, target;
            string expected = "/members/api/aurify/addon-packages/" +
                package.Key + "/" + package.Sha256 + ".zip";
            if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out origin) ||
                !AddonUpdates.TrustedPortalOrigin(origin) ||
                !Uri.TryCreate(package.Url, UriKind.Absolute, out target) ||
                !String.Equals(target.GetLeftPart(UriPartial.Authority),
                    origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) ||
                target.Scheme != origin.Scheme ||
                !String.IsNullOrEmpty(target.UserInfo) ||
                !String.IsNullOrEmpty(target.Query) ||
                !String.IsNullOrEmpty(target.Fragment) ||
                target.AbsolutePath != expected)
                throw new PortalException("Portal returned an invalid addon package address", 400);

            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, target);
            try
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct).ConfigureAwait(false);
            }
            catch
            {
                request.Dispose();
                ct.ThrowIfCancellationRequested();
                throw new PortalException("The Auratism portal is temporarily unreachable");
            }
        }

        private const string AddonUpdatesRoute = "/members/api/aurify/addon-updates";
    }
}
