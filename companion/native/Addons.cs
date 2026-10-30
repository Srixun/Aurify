using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AurifyClient
{
    // Trusted assembly-only fixture seam. No configuration, command-line or UI
    // value can supply a catalog or fetch implementation to the production path.
    internal sealed class AddonPackage
    {
        internal readonly string Key, Directory, Name, Version, Toc, Title, Url, Sha256;
        internal readonly long Bytes;
        internal const int Interface = 16001;
        internal AddonPackage(string key, string directory, string name, string version, string toc, string title, string url, string sha256, long bytes = 0)
        {
            Key = key; Directory = directory; Name = name; Version = version;
            Toc = toc; Title = title; Url = url; Sha256 = sha256; Bytes = bytes;
        }
    }

    internal delegate Task<HttpResponseMessage> AddonFetch(AddonPackage package, CancellationToken cancellation);

    internal static class AddonInstaller
    {
        private const int DownloadLimit = 16 * 1024 * 1024;
        private const int UnpackedLimit = 64 * 1024 * 1024;
        private const int FileLimit = 8 * 1024 * 1024;
        private const int EntryLimit = 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private const string OwnershipName = ".aurify-package.json";
        private static readonly uint[] CrcTable = MakeCrcTable();
        private static readonly ReadOnlyCollection<AddonPackage> Packages = Array.AsReadOnly(new[]
        {
            new AddonPackage("professions", "ProfessionMaster", "ProfessionMaster", "7.0.24", "ProfessionMaster_Camelot.toc", "|cffDA8CFFProfession Master |c999999FFForever", "https://auratism.com/members/api/aurify/addon-packages/professions/40cb72ad24e31ee4edff3b37e4595fd04c093bcf0007a940ee3aeee9e5b2962d.zip", "40cb72ad24e31ee4edff3b37e4595fd04c093bcf0007a940ee3aeee9e5b2962d"),
            new AddonPackage("overlord", "Overlord", "Overlord Forever", "2.0.4", "Overlord.toc", "Overlord Forever", "https://auratism.com/members/api/aurify/addon-packages/overlord/f87fba59d3878c2f056909d6f8bd66e5d240d766c3d107aa1b2be229bc609b13.zip", "f87fba59d3878c2f056909d6f8bd66e5d240d766c3d107aa1b2be229bc609b13")
        });
        internal static IList<AddonPackage> Catalog { get { return Packages; } }


        private sealed class AddonError : IOException
        {
            internal readonly string Status;
            internal readonly bool Retryable;
            internal AddonError(string message, string status = "failed", bool retryable = false) : base(message)
            {
                Status = status;
                Retryable = retryable;
            }
        }


        internal static async Task<AddonSetup> InstallAsync(GameInstallation installation, CancellationToken ct, IEnumerable<AddonPackage> updates, AddonFetch authenticatedFetch)
        {
            if (updates == null) throw new ArgumentNullException("updates");
            var selected = new List<AddonPackage>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (AddonPackage package in updates)
            {
                if (package == null || !keys.Add(package.Key) || keys.Count > 2 || package.Bytes <= 0)
                    throw new ArgumentException("Invalid trusted addon updates.");
                AddonPackage required = null;
                foreach (AddonPackage candidate in Packages)
                    if (candidate.Key == package.Key) { required = candidate; break; }
                if (required == null) throw new ArgumentException("Invalid trusted addon update identity.");
                ValidateDescriptor(package, required);
                selected.Add(package);
            }
            if (selected.Count > 0 && authenticatedFetch == null)
                throw new ArgumentException("Authenticated addon package transport is required.");
            AddonFetch fetch = authenticatedFetch ?? delegate(AddonPackage package, CancellationToken cancellation) {
                throw new InvalidOperationException("No verified addon package is available.");
            };
            return await InstallAsync(
                installation,
                ct,
                selected,
                fetch,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "addon", "Aurify")).ConfigureAwait(false);
        }

        internal static async Task<AddonSetup> InstallAsync(GameInstallation installation, CancellationToken ct, IEnumerable<AddonPackage> catalog, AddonFetch fetch, string bundleDirectory)
        {
            if (installation == null || installation.Addons == null) throw new ArgumentNullException("installation");
            if (catalog == null || fetch == null) throw new ArgumentNullException("catalog");
            var descriptors = new Dictionary<string, AddonPackage>(StringComparer.Ordinal);
            foreach (AddonPackage package in catalog)
            {
                if (package == null || String.IsNullOrEmpty(package.Key) || descriptors.ContainsKey(package.Key))
                    throw new ArgumentException("The trusted addon catalog has duplicate or invalid keys.");
                descriptors.Add(package.Key, package);
                if (descriptors.Count > 2) throw new ArgumentException("The trusted addon catalog is too large.");
            }
            var setup = new AddonSetup();
            string owner = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            using (var mutation = new Semaphore(1, 1, @"Local\AurifyAddonMutation-" + Json.Hash(Utf8.GetBytes(owner + ":" + NativeGamePaths.DirectoryIdentity(installation.GameDir)))))
            {
            if (!mutation.WaitOne(0))
            {
                foreach (string key in new[] { "activity", "professions", "overlord" })
                    setup.Results[key] = Failure(installation, key, key, new AddonError("Another addon setup is in progress; installation will retry.", "deferred"), ct);
                setup.Status = "partial";
                return setup;
            }
            try
            {
            ct.ThrowIfCancellationRequested();
            try { setup.Results["activity"] = InstallHelper(installation, bundleDirectory, ct); }
            catch (Exception error) { setup.Results["activity"] = Failure(installation, "activity", "Aurify", error, ct); }
            foreach (AddonPackage required in Packages)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    RecoverTransaction(
                        SafeRoot(installation, true),
                        required.Directory,
                        installation,
                        ct);
                    AddonPackage package;
                    if (!descriptors.TryGetValue(required.Key, out package))
                    {
                        setup.Results[required.Key] = PackageUnavailable(installation, required);
                        continue;
                    }
                    ValidateDescriptor(package, required);
                    setup.Results[required.Key] = await InstallPackage(installation, package, fetch, ct).ConfigureAwait(false);
                }
                catch (Exception error) { setup.Results[required.Key] = Failure(installation, required.Key, required.Name, error, ct); }
            }
            setup.Status = "ready";
            foreach (AddonResult result in setup.Results.Values)
            {
                if (result.Status != "installed" && result.Status != "present") setup.Status = "partial";
                setup.RestartRequired |= result.RestartRequired;
            }
            return setup;
            }
            finally { mutation.Release(); }
            }
        }

        private static void ValidateDescriptor(AddonPackage package, AddonPackage required)
        {
            if (package.Key != required.Key || package.Name != required.Name || package.Directory != required.Directory || package.Toc != required.Toc || package.Title != required.Title || !AddonVersions.IsSupported(package.Key, package.Version) || package.Bytes < 0 || package.Bytes > DownloadLimit || String.IsNullOrEmpty(package.Url) || package.Url.Length > 2048 || !Regex.IsMatch(package.Sha256 ?? "", "^[0-9a-f]{64}$"))
                throw new AddonError("The trusted package identity is invalid.");
        }

        private static AddonResult PackageUnavailable(GameInstallation installation, AddonPackage required)
        {
            AddonInfo existing;
            installation.Addons.TryGetValue(required.Key, out existing);
            return new AddonResult {
                Status = "unavailable",
                Version = existing == null ? null : existing.Version,
                Message = "No verified " + required.Name + " package is currently available. Existing files were preserved."
            };
        }

        private static bool Transient(Exception error)
        {
            PortalException portal = error as PortalException;
            if (portal != null)
                return portal.Status == 0 || portal.Status == 408 || portal.Status == 409 ||
                    portal.Status == 429 || portal.Status >= 500;
            return error is HttpRequestException;
        }

        private static AddonResult Failure(GameInstallation installation, string key, string name, Exception error, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            AddonError addon = error as AddonError;
            string status = addon == null ? "failed" : addon.Status;
            if (status == "deferred")
            {
                AddonInfo previous;
                installation.Addons.TryGetValue(key, out previous);
                return new AddonResult { Status = status, Version = previous == null ? null : previous.Version, Message = addon.Message };
            }
            // An occupied folder must override an old discovery result, including
            // a stale ready result. Never upload data from a failed identity check.
            installation.Addons[key] = new AddonInfo { Status = status == "blocked" ? "incompatible" : "unreadable" };
            return new AddonResult
            {
                Status = status,
                Retryable = status == "failed" && (addon == null ? Transient(error) : addon.Retryable),
                Message = status == "blocked" ? addon.Message : name + " was not installed. " + (addon == null ? "The download or local file write did not finish." : addon.Message) + " Check your connection and game-folder permissions, then reopen setup to retry. Existing files and settings were preserved."
            };
        }

        private static AddonError Blocked(string directory)
        {
            return new AddonError(directory + " is already occupied by an unrecognized, incompatible, or linked addon. Nothing was replaced. Back it up and move it aside, or select the correct game folder, then reopen setup.", "blocked");
        }

        private static string SafeRoot(GameInstallation installation, bool create)
        {
            try
            {
                string current = NativeGamePaths.LocalPath(installation.GameDir);
                using (NativeGamePaths.Guard game = NativeGamePaths.LockDirectory(current))
                {
                    foreach (string component in new[] { "Interface", "AddOns" })
                    {
                        string child = Path.Combine(current, component);
                        using (NativeGamePaths.Guard parent = NativeGamePaths.LockDirectory(current))
                        {
                            if (create && !Exists(child))
                            {
                                ObserveMutation(installation, CancellationToken.None);
                                try { NativeGamePaths.CreateNewDirectory(child); }
                                catch (Win32Exception error) { if (error.NativeErrorCode != 183) throw; }
                            }
                            using (NativeGamePaths.Guard check = NativeGamePaths.LockDirectory(child)) { }
                        }
                        current = child;
                    }
                }
                return current;
            }
            catch (AddonError) { throw; }
            catch { throw new AddonError("The game or Interface/AddOns folder is unavailable or uses a link. Choose a writable game folder without linked destination folders.", "blocked"); }
        }

        private static bool Exists(string path)
        {
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private static string FindName(string parent, string name)
        {
            string match = null;
            int count = 0;
            foreach (string path in Directory.EnumerateFileSystemEntries(parent))
            {
                if (++count > 16384) throw new IOException("The addon directory has too many entries.");
                if (!String.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)) continue;
                if (match != null) throw Blocked(name);
                match = path;
            }
            return match;
        }

        private static AddonResult Existing(string root, AddonPackage package, GameInstallation installation)
        {
            using (NativeGamePaths.Guard guard = NativeGamePaths.LockDirectory(root))
            {
                string directory = FindName(root, package.Directory);
                if (directory == null) return null;
                try
                {
                    using (NativeGamePaths.Guard target = NativeGamePaths.LockDirectory(directory))
                    {
                        string toc = FindName(directory, package.Toc);
                        if (toc == null) throw Blocked(package.Directory);
                        Dictionary<string, string> metadata = GameTools.ReadToc(toc);
                        string observedVersion = GameTools.Field(metadata, "version");
                        Version installedVersion, bundledVersion;
                        if (GameTools.PlainTitle(GameTools.Field(metadata, "title")) != GameTools.PlainTitle(package.Title) ||
                            !GameTools.SupportsForever(metadata) ||
                            !AddonVersions.TryParse(observedVersion, out installedVersion) ||
                            !AddonVersions.TryParse(package.Version, out bundledVersion))
                            throw Blocked(package.Directory);
                        bool supported = AddonVersions.IsSupported(package.Key, observedVersion);
                        bool older = installedVersion < bundledVersion;
                        string ownership = FindName(directory, OwnershipName);
                        if (!supported)
                        {
                            // Unsupported installations are never accepted as ready. Aurify
                            // may only migrate an older copy whose exact files it already owns.
                            if (!older || ownership == null) throw Blocked(package.Directory);
                            ReadOwnership(directory, package);
                            return null;
                        }
                        installation.Addons[package.Key] = GameTools.Ready(metadata);
                        if (older)
                        {
                            // Legacy/user installs have no file ownership evidence. Report
                            // them as stale, but do not claim or overwrite them silently.
                            if (ownership == null)
                                return new AddonResult { Status = "update-needed", Version = installation.Addons[package.Key].Version,
                                    Message = package.Name + " has an older Forever version. Its unmanaged files were preserved." };
                            ReadOwnership(directory, package);
                            return null;
                        }
                        return new AddonResult { Status = "present", Version = installation.Addons[package.Key].Version, Message = package.Name + " is already installed and supports Forever. Its existing files and settings were preserved." };
                    }
                }
                catch { throw Blocked(package.Directory); }
            }
        }

        private static async Task<AddonResult> InstallPackage(GameInstallation installation, AddonPackage package, AddonFetch fetch, CancellationToken ct)
        {
            string root = SafeRoot(installation, true);
            string rootIdentity;
            using (NativeGamePaths.Guard guard = NativeGamePaths.LockDirectory(root))
            {
                AddonResult present = Existing(root, package, installation);
                if (present != null) return present;
                rootIdentity = guard.Identity;
            }
            ObserveMutation(installation, ct);
            byte[] bytes = await Download(package, fetch, ct).ConfigureAwait(false);
            SafeRoot(installation, false);
            using (NativeGamePaths.Guard rootGuard = NativeGamePaths.LockDirectory(root))
            {
                RequireIdentity(rootGuard.Identity, rootIdentity);
                AddonResult arrived = Existing(root, package, installation);
                if (arrived != null) return arrived;
                string stage = Path.Combine(root, ".aurify-addons-" + Guid.NewGuid().ToString("N"));
                ObserveMutation(installation, ct);
                NativeGamePaths.CreateNewDirectory(stage);
                string stageIdentity = NativeGamePaths.DirectoryIdentity(stage);
                try
                {
                    using (NativeGamePaths.Guard stageGuard = NativeGamePaths.LockDirectory(stage))
                    {
                        Dictionary<string, string> metadata = Unpack(bytes, package, stage, ct, installation);
                        ct.ThrowIfCancellationRequested();
                        SafeRoot(installation, false);
                        RequireIdentity(NativeGamePaths.DirectoryIdentity(root), rootIdentity);
                        RequireIdentity(NativeGamePaths.DirectoryIdentity(stage), stageIdentity);
                        AddonResult concurrent = Existing(root, package, installation);
                        if (concurrent != null) return concurrent;
                        string source = Path.Combine(stage, package.Directory);
                        string sourceIdentity = NativeGamePaths.DirectoryIdentity(source);
                        RequireIdentity(NativeGamePaths.DirectoryIdentity(source), sourceIdentity);
                        string destination = FindName(root, package.Directory);
                        if (destination == null)
                        {
                            ObserveMutation(installation, ct);
                            WriteOwnership(source, package);
                            ObserveMutation(installation, ct);
                            try { RenameDirectory(source, NativeGamePaths.DirectoryIdentity(source), Path.Combine(root, package.Directory)); }
                            catch (IOException)
                            {
                                AddonResult occupied = Existing(root, package, installation);
                                if (occupied != null) return occupied;
                                throw;
                            }
                        }
                        else RefreshPackage(destination, source, package, ct, installation);
                        installation.Addons[package.Key] = GameTools.Ready(metadata);
                        return new AddonResult { Status = "installed", Version = package.Version, RestartRequired = installation.Running, Message = package.Name + " " + package.Version + " was installed from its official publisher. Existing addons and settings were preserved." };
                    }
                }
                finally { if (!Exists(TransactionPath(root, package.Directory))) CleanupStage(root, rootIdentity, stage, stageIdentity, installation); }
            }
        }

        private static Dictionary<string, object> ReadOwnership(string directory, AddonPackage package)
        {
            string path = Path.Combine(directory, OwnershipName);
            var record = Json.Obj(Json.Parse(Utf8.GetString(NativeGamePaths.ReadFile(path, 256 * 1024))));
            var hashes = Json.Obj(Json.Get(record, "hashes"));
            Version recorded, pinned;
            if (Json.Str(Json.Get(record, "product")) != "Aurify" || Json.Str(Json.Get(record, "package")) != package.Key ||
                !AddonVersions.TryParse(Json.Str(Json.Get(record, "version")), out recorded) ||
                !AddonVersions.TryParse(package.Version, out pinned) || recorded > pinned || hashes.Count == 0 || hashes.Count > EntryLimit ||
                !hashes.ContainsKey(package.Toc))
                throw Blocked(package.Directory);
            foreach (KeyValuePair<string, object> entry in hashes)
                if (!SafeOwnedPath(entry.Key) || !Regex.IsMatch(Json.Str(entry.Value) ?? "", "^[0-9a-f]{64}$"))
                    throw Blocked(package.Directory);
            return hashes;
        }

        private static bool SafeOwnedPath(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length > 240 || path.Contains("\\") || path.Contains(":") || path.StartsWith("/") || path == OwnershipName) return false;
            foreach (string part in path.Split('/'))
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ")) return false;
            return true;
        }

        private static void PackageFiles(string root, string directory, Dictionary<string, object> hashes, ref long total)
        {
            using (NativeGamePaths.Guard guard = NativeGamePaths.LockDirectory(directory))
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw Blocked(Path.GetFileName(root));
                if ((attributes & FileAttributes.Directory) != 0) PackageFiles(root, path, hashes, ref total);
                else
                {
                    string relative = path.Substring(root.Length + 1).Replace('\\', '/');
                    if (!SafeOwnedPath(relative) || hashes.Count >= EntryLimit) throw Blocked(Path.GetFileName(root));
                    byte[] bytes = NativeGamePaths.ReadFile(path, FileLimit);
                    total += bytes.Length;
                    if (total > UnpackedLimit) throw new AddonError("The package is too large.");
                    hashes.Add(relative, Json.Hash(bytes));
                }
            }
        }

        private static Dictionary<string, object> SourceHashes(string source)
        {
            var hashes = Json.New();
            long total = 0;
            PackageFiles(source, source, hashes, ref total);
            return hashes;
        }

        private static byte[] OwnershipBytes(AddonPackage package, Dictionary<string, object> hashes)
        {
            return Utf8.GetBytes(Json.Stringify(new { product = "Aurify", package = package.Key, version = package.Version, hashes = hashes }));
        }

        private static void WriteOwnership(string source, AddonPackage package)
        {
            WriteNew(Path.Combine(source, OwnershipName), OwnershipBytes(package, SourceHashes(source)));
        }

        // Assembly-only fault injection. Never populated from settings or network data.
        internal static Action<string> TransactionCheckpoint;
        internal sealed class InterruptedTransaction : IOException { }
        private static void Checkpoint(string phase) { var hook = TransactionCheckpoint; if (hook != null) hook(phase); }
        private static string TransactionPath(string root, string directory) { return Path.Combine(root, ".aurify-transaction-" + directory + ".json"); }

        private static void RefreshPackage(string target, string source, AddonPackage package, CancellationToken ct, GameInstallation installation)
        {
            string root = Path.GetDirectoryName(target), stage = Path.GetDirectoryName(source);
            string intent = TransactionPath(root, package.Directory);
            Dictionary<string, object> owned = ReadOwnership(target, package), next = SourceHashes(source);
            string oldIdentity = NativeGamePaths.DirectoryIdentity(target);
            try
            {
                using (var previous = new TransactionTree(target, oldIdentity))
                {
                    foreach (KeyValuePair<string, object> entry in owned)
                        if (Json.Str(Json.Get(previous.Hashes, entry.Key)) != Json.Str(entry.Value)) throw Blocked(package.Directory);
                    foreach (KeyValuePair<string, object> entry in next)
                        if (previous.Hashes.ContainsKey(entry.Key) && !owned.ContainsKey(entry.Key)) throw Blocked(package.Directory);
                    // Preserve every unowned file and empty directory without adding it to
                    // ownership. Package/extra path and ancestor collisions fail closed.
                    foreach (KeyValuePair<string, object> entry in previous.Hashes)
                    {
                        if (entry.Key == OwnershipName || owned.ContainsKey(entry.Key)) continue;
                        ObserveMutation(installation, ct);
                        string destination = Path.Combine(source, entry.Key.Replace('/', Path.DirectorySeparatorChar));
                        if (Json.Str(entry.Value).StartsWith("dir:", StringComparison.Ordinal))
                        {
                            EnsurePackageDirectory(source, destination, installation, ct);
                            continue;
                        }
                        if (Exists(destination)) throw Blocked(package.Directory);
                        EnsurePackageDirectory(source, Path.GetDirectoryName(destination), installation, ct);
                        ObserveMutation(installation, ct);
                        WriteNew(destination, previous.Bytes(entry.Key));
                    }
                    ObserveMutation(installation, ct);
                    WriteNew(Path.Combine(source, OwnershipName), OwnershipBytes(package, next));
                    using (var replacement = new TransactionTree(source, NativeGamePaths.DirectoryIdentity(source)))
                    {
                        previous.RequireUnchanged();
                        replacement.RequireUnchanged();
                        string backup = ".aurify-backup-" + Guid.NewGuid().ToString("N");
                        byte[] journal = Utf8.GetBytes(Json.Stringify(new {
                            schema = 1, directory = package.Directory, root = NativeGamePaths.DirectoryIdentity(root),
                            stage = Path.GetFileName(stage), stageIdentity = NativeGamePaths.DirectoryIdentity(stage),
                            backup = backup, previous = previous.Identity, replacement = replacement.Identity,
                            oldTree = previous.Hashes, newTree = replacement.Hashes
                        }));
                        ObserveMutation(installation, ct);
                        WriteNew(intent, journal);
                        Checkpoint("prepared");
                        ObserveMutation(installation, ct);
                        previous.RequireUnchanged();
                        previous.ReleaseChildren();
                        Checkpoint("before-backup");
                        previous.Rename(Path.Combine(root, backup));
                        try { previous.Recheck(); }
                        catch
                        {
                            previous.ReleaseChildren();
                            ObserveMutation(installation, ct);
                            previous.Rename(target);
                            throw;
                        }
                        Checkpoint("backed-up");
                        ObserveMutation(installation, ct);
                        previous.RequireUnchanged();
                        replacement.RequireUnchanged();
                        replacement.ReleaseChildren();
                        replacement.Rename(target);
                        replacement.Recheck();
                        Checkpoint("published");
                        ObserveMutation(installation, ct);
                        previous.RequireUnchanged();
                        replacement.RequireUnchanged();
                        WriteNew(intent + ".committed", Utf8.GetBytes(Json.Hash(journal)));
                    }
                }
            }
            catch (InterruptedTransaction) { throw; }
            catch
            {
                if (Exists(intent)) RecoverTransaction(root, package.Directory, installation, CancellationToken.None);
                throw;
            }
            RecoverTransaction(root, package.Directory, installation, ct);
        }

        private static void RecoverTransaction(string root, string directory, GameInstallation installation, CancellationToken ct)
        {
            using (var rootGuard = NativeGamePaths.LockDirectory(root))
            {
                string intent = TransactionPath(root, directory), marker = intent + ".committed";
                if (!Exists(intent))
                {
                    if (Exists(marker)) throw Blocked(directory);
                    return;
                }
                ObserveMutation(installation, ct);
                byte[] bytes = NativeGamePaths.ReadFile(intent, 1024 * 1024);
                var record = Json.Obj(Json.Parse(Utf8.GetString(bytes)));
                string stageName = Json.Str(Json.Get(record, "stage")), backupName = Json.Str(Json.Get(record, "backup"));
                if (Json.Int(Json.Get(record, "schema")) != 1 || Json.Str(Json.Get(record, "directory")) != directory ||
                    !Regex.IsMatch(stageName ?? "", @"^\.aurify-addons-[0-9a-f]{32}$") ||
                    !Regex.IsMatch(backupName ?? "", @"^\.aurify-backup-[0-9a-f]{32}$")) throw Blocked(directory);
                RequireIdentity(rootGuard.Identity, Json.Str(Json.Get(record, "root")));
                string stage = Path.Combine(root, stageName), backup = Path.Combine(root, backupName);
                string source = Path.Combine(stage, directory), target = Path.Combine(root, directory);
                string oldId = Json.Str(Json.Get(record, "previous")), newId = Json.Str(Json.Get(record, "replacement"));
                string stageId = Json.Str(Json.Get(record, "stageIdentity"));
                RequireIdentity(NativeGamePaths.DirectoryIdentity(stage), stageId);
                var oldTree = Json.Obj(Json.Get(record, "oldTree"));
                var newTree = Json.Obj(Json.Get(record, "newTree"));
                if (oldTree.Count == 0 || newTree.Count == 0) throw Blocked(directory);
                bool committed = Exists(marker);
                if (committed && Utf8.GetString(NativeGamePaths.ReadFile(marker, 128)) != Json.Hash(bytes)) throw Blocked(directory);
                // Inspect every surviving identity before any recovery write. No guessing
                // about a missing/foreign target, stage, backup or partially changed tree.
                if (Exists(source)) RequireTree(source, newId, newTree);
                if (Exists(backup)) RequireTree(backup, oldId, oldTree);
                if (Exists(target))
                {
                    string id = NativeGamePaths.DirectoryIdentity(target);
                    if (id == oldId) RequireTree(target, oldId, oldTree);
                    else if (id == newId) RequireTree(target, newId, newTree);
                    else throw Blocked(directory);
                }
                if (committed)
                {
                    RequireTree(target, newId, newTree);
                    if (Exists(source)) throw Blocked(directory);
                    if (Exists(backup))
                    {
                        ObserveMutation(installation, ct);
                        DeleteOwnedTree(backup, oldId, 0, installation, oldTree, backup);
                    }
                }
                else if (Exists(backup))
                {
                    if (Exists(target))
                    {
                        RequireTree(target, newId, newTree);
                        if (Exists(source)) throw Blocked(directory);
                        ObserveMutation(installation, ct);
                        RenameDirectory(target, newId, source);
                    }
                    ObserveMutation(installation, ct);
                    RenameDirectory(backup, oldId, target);
                    RequireTree(target, oldId, oldTree);
                }
                else RequireTree(target, oldId, oldTree);
                ObserveMutation(installation, ct);
                // Delete intent before the marker: a stray marker is deliberately blocked,
                // never mistaken for permission to delete a backup.
                File.Delete(intent);
                if (committed) File.Delete(marker);
                CleanupStage(root, rootGuard.Identity, stage, stageId, installation);
            }
        }

        private static void RequireTree(string path, string identity, Dictionary<string, object> expected)
        {
            using (var tree = new TransactionTree(path, identity))
            {
                if (tree.Hashes.Count != expected.Count) throw Blocked(Path.GetFileName(path));
                foreach (var entry in expected)
                    if (Json.Str(Json.Get(tree.Hashes, entry.Key)) != Json.Str(entry.Value)) throw Blocked(Path.GetFileName(path));
                tree.RequireUnchanged();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TransactionFileInfo
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out TransactionFileInfo info);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr information, uint size);

        private static SafeFileHandle OpenTransactionDirectory(string path, bool rename, out string identity)
        {
            // Unlike NativeGamePaths.Guard, this handle owns DELETE access itself.
            // No share-delete/write means only this handle can rename this identity.
            SafeFileHandle handle = CreateFile(path, 0x80u | (rename ? 0x10000u : 0u), 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            try
            {
                TransactionFileInfo info;
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.Attributes & (uint)FileAttributes.Directory) == 0) throw Blocked(Path.GetFileName(path));
                identity = info.Volume.ToString("x8") + ":" + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8");
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }

        private static void RenameHandle(SafeFileHandle handle, string destination)
        {
            byte[] name = Encoding.Unicode.GetBytes(NativeGamePaths.LocalPath(destination));
            int rootOffset = IntPtr.Size == 8 ? 8 : 4, lengthOffset = rootOffset + IntPtr.Size, nameOffset = lengthOffset + 4;
            int size = nameOffset + name.Length + 2;
            IntPtr information = Marshal.AllocHGlobal(size);
            try
            {
                for (int index = 0; index < size; index++) Marshal.WriteByte(information, index, 0);
                Marshal.WriteInt32(information, lengthOffset, name.Length);
                Marshal.Copy(name, 0, IntPtr.Add(information, nameOffset), name.Length);
                if (!SetFileInformationByHandle(handle, 3, information, (uint)size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(information); }
        }

        private static void RenameDirectory(string source, string identity, string destination)
        {
            string actual;
            using (var handle = OpenTransactionDirectory(source, true, out actual))
            {
                RequireIdentity(actual, identity);
                RenameHandle(handle, destination);
            }
        }

        private sealed class TransactionTree : IDisposable
        {
            internal Dictionary<string, object> Hashes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            internal readonly string Identity;
            private readonly List<SafeFileHandle> directories = new List<SafeFileHandle>();
            private readonly Dictionary<string, FileStream> files = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
            private string root;
            private long total;
            internal TransactionTree(string path, string identity)
            {
                root = path;
                try
                {
                    string actual;
                    directories.Add(OpenTransactionDirectory(path, true, out actual));
                    RequireIdentity(actual, identity);
                    Identity = actual;
                    Capture(path, 0);
                }
                catch { Dispose(); throw; }
            }
            private void Capture(string directory, int depth)
            {
                if (depth > 64) throw Blocked(Path.GetFileName(root));
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (Hashes.Count >= EntryLimit * 2) throw Blocked(Path.GetFileName(root));
                    string relative = path.Substring(root.Length + 1).Replace('\\', '/');
                    if (relative != OwnershipName && !SafeOwnedPath(relative)) throw Blocked(Path.GetFileName(root));
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw Blocked(Path.GetFileName(root));
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        string identity;
                        directories.Add(OpenTransactionDirectory(path, false, out identity));
                        Hashes.Add(relative, "dir:" + identity);
                        Capture(path, depth + 1);
                    }
                    else
                    {
                        FileStream stream = NativeGamePaths.OpenRead(path);
                        files.Add(relative, stream);
                        total += stream.Length;
                        if (stream.Length > FileLimit || total > UnpackedLimit) throw Blocked(Path.GetFileName(root));
                        using (var hash = SHA256.Create()) Hashes.Add(relative, BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant());
                        stream.Position = 0;
                    }
                }
            }
            internal byte[] Bytes(string relative)
            {
                FileStream stream = files[relative];
                stream.Position = 0;
                byte[] bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int count = stream.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw Blocked(Path.GetFileName(root));
                    offset += count;
                }
                return bytes;
            }
            internal void RequireUnchanged()
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CheckPaths(root, paths, 0);
                if (paths.Count != Hashes.Count) throw Blocked(Path.GetFileName(root));
            }
            private void CheckPaths(string directory, HashSet<string> paths, int depth)
            {
                if (depth > 64) throw Blocked(Path.GetFileName(root));
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    string relative = path.Substring(root.Length + 1).Replace('\\', '/');
                    if (!Hashes.ContainsKey(relative) || !paths.Add(relative)) throw Blocked(Path.GetFileName(root));
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw Blocked(Path.GetFileName(root));
                    if ((attributes & FileAttributes.Directory) != 0) CheckPaths(path, paths, depth + 1);
                }
            }
            internal void ReleaseChildren()
            {
                foreach (FileStream file in files.Values) file.Dispose();
                files.Clear();
                for (int index = directories.Count - 1; index > 0; index--) directories[index].Dispose();
                if (directories.Count > 1) directories.RemoveRange(1, directories.Count - 1);
            }
            internal void Recheck()
            {
                var expected = Hashes;
                ReleaseChildren();
                Hashes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                total = 0;
                Capture(root, 0);
                if (Hashes.Count != expected.Count) throw Blocked(Path.GetFileName(root));
                foreach (var entry in expected)
                    if (Json.Str(Json.Get(Hashes, entry.Key)) != Json.Str(entry.Value)) throw Blocked(Path.GetFileName(root));
                RequireUnchanged();
            }
            internal void Rename(string destination) { RenameHandle(directories[0], destination); root = destination; }
            public void Dispose()
            {
                foreach (FileStream file in files.Values) file.Dispose();
                for (int index = directories.Count - 1; index >= 0; index--) directories[index].Dispose();
            }
        }

        private static void EnsurePackageDirectory(string root, string directory, GameInstallation installation, CancellationToken ct)
        {
            if (String.Equals(root, directory, StringComparison.OrdinalIgnoreCase)) return;
            string parent = Path.GetDirectoryName(directory);
            EnsurePackageDirectory(root, parent, installation, ct);
            using (NativeGamePaths.Guard guard = NativeGamePaths.LockDirectory(parent))
            {
                if (!Exists(directory))
                {
                    ObserveMutation(installation, ct);
                    NativeGamePaths.CreateNewDirectory(directory);
                }
                using (NativeGamePaths.Guard child = NativeGamePaths.LockDirectory(directory)) { }
            }
        }


        private static void RequireIdentity(string current, string expected)
        {
            if (current != expected) throw new AddonError("The addon destination changed. Reopen setup after checking the game folder.", "blocked");
        }

        private static void ObserveLate<T>(Task<T> pending, Action<T> disposeLate)
        {
            pending.ContinueWith(delegate(Task<T> task)
            {
                if (task.IsFaulted) GC.KeepAlive(task.Exception);
                else if (task.Status == TaskStatus.RanToCompletion && disposeLate != null)
                {
                    try { disposeLate(task.Result); }
                    catch { }
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static async Task<T> BeforeCancellation<T>(Task<T> pending, CancellationToken ct, Action<T> disposeLate = null)
        {
            var cancelled = new TaskCompletionSource<bool>();
            using (ct.Register(delegate { cancelled.TrySetResult(true); }))
            {
                if (await Task.WhenAny(pending, cancelled.Task).ConfigureAwait(false) != pending)
                {
                    // Observe a late failure and dispose any late response even when a
                    // fixture or a transport ignores cancellation.
                    ObserveLate(pending, disposeLate);
                    ct.ThrowIfCancellationRequested();
                }
                return await pending.ConfigureAwait(false);
            }
        }

        private static async Task<byte[]> Download(AddonPackage package, AddonFetch fetch, CancellationToken ct)
        {
            Uri url;
            if (!Uri.TryCreate(package.Url, UriKind.Absolute, out url) ||
                !String.IsNullOrEmpty(url.UserInfo) || !String.IsNullOrEmpty(url.Query) || !String.IsNullOrEmpty(url.Fragment) ||
                !AddonUpdates.TrustedPortalOrigin(new Uri(url.GetLeftPart(UriPartial.Authority))))
                throw new AddonError("The official package address is invalid.");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(30000);
                HttpResponseMessage response = null;
                Stream stream = null;
                try
                {
                    response = await BeforeCancellation(fetch(package, deadline.Token), deadline.Token,
                        delegate(HttpResponseMessage late) { if (late != null) late.Dispose(); }).ConfigureAwait(false);
                    if (response == null)
                        throw new AddonError("The official publisher did not return the package.", "failed", true);
                    int status = (int)response.StatusCode;
                    if (status != (int)HttpStatusCode.OK)
                        throw new AddonError(
                            "The official publisher did not return the package.",
                            "failed",
                            status == 408 || status == 409 || status == 429 || status >= 500);
                    if (response.Content == null || (response.RequestMessage != null && response.RequestMessage.RequestUri != null && response.RequestMessage.RequestUri.AbsoluteUri != url.AbsoluteUri))
                        throw new AddonError("The official publisher did not return the package.");
                    if (response.Content.Headers.ContentEncoding.Count != 0 && !(response.Content.Headers.ContentEncoding.Count == 1 && response.Content.Headers.ContentEncoding.Contains("identity")))
                        throw new AddonError("The official download has an unsupported encoding.");
                    long? expected = response.Content.Headers.ContentLength;
                    if (expected.HasValue && (expected.Value < 0 || expected.Value > DownloadLimit)) throw new AddonError("The official download exceeds the 16 MiB limit.");
                    if (package.Bytes > 0 && expected.HasValue && expected.Value != package.Bytes) throw new AddonError("The official download length does not match its trusted descriptor.");
                    stream = await BeforeCancellation(response.Content.ReadAsStreamAsync(), deadline.Token, delegate(Stream late) { if (late != null) late.Dispose(); }).ConfigureAwait(false);
                    if (stream == null) throw new AddonError("The official publisher did not return the package.");
                    using (var output = new MemoryStream(expected.HasValue ? (int)expected.Value : 0))
                    using (SHA256 hash = SHA256.Create())
                    {
                        byte[] buffer = new byte[65536];
                        while (true)
                        {
                            int count = await BeforeCancellation(stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token), deadline.Token).ConfigureAwait(false);
                            if (count == 0) break;
                            if (count < 0 || count > buffer.Length || output.Length + count > DownloadLimit || (package.Bytes > 0 && output.Length + count > package.Bytes) || (expected.HasValue && output.Length + count > expected.Value))
                                throw new AddonError("The official download has an invalid size.");
                            hash.TransformBlock(buffer, 0, count, buffer, 0);
                            output.Write(buffer, 0, count);
                        }
                        if (expected.HasValue && output.Length != expected.Value) throw new AddonError("The official download was incomplete.", "failed", true);
                        if (package.Bytes > 0 && output.Length != package.Bytes) throw new AddonError("The official download length does not match its trusted descriptor.");
                        hash.TransformFinalBlock(new byte[0], 0, 0);
                        string digest = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                        if (digest != package.Sha256) throw new AddonError("The official package failed its SHA-256 integrity check.");
                        return output.ToArray();
                    }
                }
                catch (OperationCanceledException)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new AddonError("The official download timed out after 30 seconds.", "failed", true);
                }
                catch (AddonError) { throw; }
                catch (HttpRequestException)
                {
                    throw new AddonError("The official package download was interrupted.", "failed", true);
                }
                catch (IOException)
                {
                    throw new AddonError("The official package download was interrupted.", "failed", true);
                }
                finally
                {
                    deadline.Cancel();
                    if (stream != null) stream.Dispose();
                    if (response != null) response.Dispose();
                }
            }
        }

        private sealed class ZipItem
        {
            internal string Name;
            internal bool Directory;
            internal uint Crc, Size, Compressed, Offset;
            internal int End;
        }
        private sealed class ZipPath
        {
            internal string Name;
            internal bool Directory, Explicit;
        }

        private static ushort U16(byte[] bytes, int offset)
        {
            RequireRange(bytes, offset, 2);
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }
        private static uint U32(byte[] bytes, int offset)
        {
            RequireRange(bytes, offset, 4);
            return (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
        }
        private static void RequireRange(byte[] bytes, int offset, long length)
        {
            if (offset < 0 || length < 0 || offset + length > bytes.Length) throw new AddonError("The package has truncated ZIP headers.");
        }
        private static void ExtraFields(byte[] bytes, int offset, int length)
        {
            RequireRange(bytes, offset, length);
            int end = offset + length;
            while (offset < end)
            {
                if (offset + 4 > end) throw new AddonError("The package has invalid ZIP extra fields.");
                ushort type = U16(bytes, offset);
                int size = U16(bytes, offset + 2);
                offset += 4;
                // ZIP64 is unnecessary for the bounded pinned packages. Alternative
                // filename and encryption fields make the ZIP identity ambiguous.
                if (type == 1 || type == 0x7075 || type == 0x9901 || offset + size > end)
                    throw new AddonError("The package has unsupported or ambiguous ZIP extra fields.");
                offset += size;
            }
        }

        private static void ValidatePath(string name, bool directory, AddonPackage package, Dictionary<string, ZipPath> paths)
        {
            if (name.Length > 1024 || Regex.IsMatch(name, "[^\\x20-\\x7e]|[\\\\:<>\"|?*]")) throw new AddonError("The package contains an unsafe addon path.");
            string[] components = (directory ? name.Substring(0, name.Length - 1) : name).Split('/');
            if (components.Length > 64 || components[0] != package.Directory || (!directory && components.Length < 2)) throw new AddonError("The package contains an unexpected addon path.");
            string current = "";
            for (int index = 0; index < components.Length; index++)
            {
                string component = components[index];
                if (component.Length == 0 || component.Length > 255 || component == "." || component == ".." || component.EndsWith(".", StringComparison.Ordinal) || component.EndsWith(" ", StringComparison.Ordinal) || Regex.IsMatch(component, @"^(?:con|prn|aux|nul|conin\$|conout\$|com[1-9]|lpt[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
                    throw new AddonError("The package contains an unsafe addon path.");
                current = index == 0 ? component : current + "/" + component;
                bool explicitEntry = index == components.Length - 1;
                bool isDirectory = !explicitEntry || directory;
                ZipPath previous;
                if (paths.TryGetValue(current, out previous))
                {
                    if (previous.Name != current || previous.Directory != isDirectory || (explicitEntry && previous.Explicit)) throw new AddonError("The package contains duplicate or ambiguous addon paths.");
                    previous.Explicit |= explicitEntry;
                }
                else paths.Add(current, new ZipPath { Name = current, Directory = isDirectory, Explicit = explicitEntry });
            }
        }

        private static List<ZipItem> InspectZip(byte[] bytes, AddonPackage package)
        {
            if (bytes == null || bytes.Length > DownloadLimit || bytes.Length < 22) throw new AddonError("The package is not a bounded ZIP archive.");
            int eocd = -1;
            for (int index = bytes.Length - 22; index >= Math.Max(0, bytes.Length - 65557); index--)
                if (U32(bytes, index) == 0x06054b50 && index + 22 + U16(bytes, index + 20) == bytes.Length) { eocd = index; break; }
            if (eocd < 0 || U16(bytes, eocd + 4) != 0 || U16(bytes, eocd + 6) != 0 || U16(bytes, eocd + 8) != U16(bytes, eocd + 10))
                throw new AddonError("The package has an invalid or multi-volume ZIP directory.");
            int count = U16(bytes, eocd + 10);
            uint centralSize = U32(bytes, eocd + 12);
            uint centralOffset = U32(bytes, eocd + 16);
            if (count < 1 || count > EntryLimit || (long)centralOffset + centralSize != eocd) throw new AddonError("The package exceeds the 1,024 entry limit or has invalid ZIP bounds.");
            int cursor = checked((int)centralOffset);
            var paths = new Dictionary<string, ZipPath>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<ZipItem>(count);
            long unpacked = 0;
            bool exactToc = false;
            for (int index = 0; index < count; index++)
            {
                RequireRange(bytes, cursor, 46);
                if (U32(bytes, cursor) != 0x02014b50) throw new AddonError("The package has invalid ZIP directory entries.");
                ushort needed = U16(bytes, cursor + 6);
                ushort flags = U16(bytes, cursor + 8);
                ushort method = U16(bytes, cursor + 10);
                uint crc = U32(bytes, cursor + 16), compressed = U32(bytes, cursor + 20), size = U32(bytes, cursor + 24);
                int nameLength = U16(bytes, cursor + 28), extraLength = U16(bytes, cursor + 30), commentLength = U16(bytes, cursor + 32);
                uint attributes = U32(bytes, cursor + 38), offset = U32(bytes, cursor + 42);
                int end = checked(cursor + 46 + nameLength + extraLength + commentLength);
                if (end > eocd || nameLength == 0 || U16(bytes, cursor + 34) != 0 || needed > 20 || (flags & ~0x080e) != 0 || (method != 0 && method != 8))
                    throw new AddonError("The package contains an encrypted, unsupported, or ambiguous ZIP entry.");
                RequireRange(bytes, cursor + 46, nameLength);
                string name = Utf8.GetString(bytes, cursor + 46, nameLength);
                bool directory = name.EndsWith("/", StringComparison.Ordinal);
                uint type = (attributes >> 16) & 0xf000;
                if ((type != 0 && type != (directory ? 0x4000U : 0x8000U)) || (attributes & 0x0448) != 0 || (!directory && (attributes & 0x10) != 0))
                    throw new AddonError("The package contains a link or special file.");
                if (size > FileLimit || (directory && size != 0) || (method == 0 && compressed != size) || compressed > DownloadLimit)
                    throw new AddonError("The package exceeds the 8 MiB file limit or has invalid directory data.");
                unpacked += size;
                if (unpacked > UnpackedLimit) throw new AddonError("The package exceeds the 64 MiB unpacked limit.");
                ValidatePath(name, directory, package, paths);
                ExtraFields(bytes, cursor + 46 + nameLength, extraLength);
                if (offset > Int32.MaxValue) throw new AddonError("The package has invalid local ZIP bounds.");
                int local = (int)offset;
                RequireRange(bytes, local, 30);
                if (U32(bytes, local) != 0x04034b50 || U16(bytes, local + 4) != needed || U16(bytes, local + 6) != flags || U16(bytes, local + 8) != method || U16(bytes, local + 26) != nameLength)
                    throw new AddonError("The package contains inconsistent ZIP file headers.");
                int localExtra = U16(bytes, local + 28);
                RequireRange(bytes, local + 30, nameLength + localExtra);
                for (int part = 0; part < nameLength; part++)
                    if (bytes[local + 30 + part] != bytes[cursor + 46 + part]) throw new AddonError("The package contains inconsistent ZIP filenames.");
                ExtraFields(bytes, local + 30 + nameLength, localExtra);
                uint localCrc = U32(bytes, local + 14), localCompressed = U32(bytes, local + 18), localSize = U32(bytes, local + 22);
                bool descriptor = (flags & 8) != 0;
                if ((!descriptor && (localCrc != crc || localCompressed != compressed || localSize != size)) || (descriptor && ((localCrc != 0 && localCrc != crc) || (localCompressed != 0 && localCompressed != compressed) || (localSize != 0 && localSize != size))))
                    throw new AddonError("The package contains inconsistent ZIP sizes or checksums.");
                long dataEnd = (long)local + 30 + nameLength + localExtra + compressed;
                if (dataEnd > centralOffset) throw new AddonError("The package has overlapping ZIP data.");
                int rangeEnd = (int)dataEnd;
                if (descriptor)
                {
                    int start = rangeEnd;
                    if (U32(bytes, start) == 0x08074b50) start += 4;
                    if (U32(bytes, start) != crc || U32(bytes, start + 4) != compressed || U32(bytes, start + 8) != size || (long)start + 12 > centralOffset)
                        throw new AddonError("The package has inconsistent ZIP data descriptors.");
                    rangeEnd = start + 12;
                }
                entries.Add(new ZipItem { Name = name, Directory = directory, Crc = crc, Size = size, Compressed = compressed, Offset = offset, End = rangeEnd });
                exactToc |= !directory && name == package.Directory + "/" + package.Toc;
                cursor = end;
            }
            if (cursor != eocd || !exactToc) throw new AddonError("The package is incomplete or lacks the exact Forever TOC.");
            var ranges = new List<ZipItem>(entries);
            ranges.Sort(delegate(ZipItem left, ZipItem right) { return left.Offset.CompareTo(right.Offset); });
            for (int index = 0; index < ranges.Count; index++)
                if ((index == 0 && ranges[index].Offset != 0) || (index > 0 && ranges[index].Offset < ranges[index - 1].End))
                    throw new AddonError("The package contains overlapping or ambiguous ZIP data.");
            return entries;
        }

        private static Dictionary<string, string> Unpack(byte[] bytes, AddonPackage package, string stage, CancellationToken ct, GameInstallation installation)
        {
            List<ZipItem> entries = InspectZip(bytes, package);
            using (var memory = new MemoryStream(bytes, false))
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Read, false, Utf8))
            {
                if (archive.Entries.Count != entries.Count) throw new AddonError("The package has inconsistent ZIP entry counts.");
                byte[] buffer = new byte[65536];
                for (int index = 0; index < entries.Count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    ZipItem item = entries[index];
                    ZipArchiveEntry entry = archive.Entries[index];
                    if (entry.FullName != item.Name || entry.Length != item.Size || entry.CompressedLength != item.Compressed)
                        throw new AddonError("The package has inconsistent ZIP metadata.");
                    string target = Path.Combine(stage, item.Name.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
                    EnsurePackageDirectory(stage, item.Directory ? target : Path.GetDirectoryName(target), installation, ct);
                    ObserveMutation(installation, ct);
                    using (NativeGamePaths.Guard parent = NativeGamePaths.LockDirectory(item.Directory ? target : Path.GetDirectoryName(target)))
                    using (Stream input = entry.Open())
                    using (FileStream output = item.Directory ? null : new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        long written = 0;
                        uint crc = 0xffffffff;
                        while (true)
                        {
                            ct.ThrowIfCancellationRequested();
                            int read = input.Read(buffer, 0, buffer.Length);
                            if (read == 0) break;
                            written += read;
                            if (written > item.Size || written > FileLimit || output == null) throw new AddonError("An extracted addon file exceeds its verified size.");
                            crc = UpdateCrc(crc, buffer, read);
                            ObserveMutation(installation, ct);
                            output.Write(buffer, 0, read);
                        }
                        if (written != item.Size || (crc ^ 0xffffffff) != item.Crc) throw new AddonError("An extracted addon file failed its size or CRC integrity check.");
                        if (output != null)
                        {
                            output.Flush(true);
                            if (output.Length != written) throw new AddonError("An addon file could not be completely written.");
                        }
                    }
                }
            }
            VerifyExtracted(stage, entries, ct);
            Dictionary<string, string> metadata = GameTools.ReadToc(Path.Combine(stage, package.Directory, package.Toc));
            if (GameTools.Field(metadata, "title") != package.Title || GameTools.Field(metadata, "interface") != "16001" || GameTools.Field(metadata, "version") != package.Version)
                throw new AddonError("The package does not match the verified addon version and Forever identity.");
            return metadata;
        }

        private static void VerifyExtracted(string stage, List<ZipItem> entries, CancellationToken ct)
        {
            var files = new Dictionary<string, ZipItem>(StringComparer.Ordinal);
            var directories = new HashSet<string>(StringComparer.Ordinal);
            foreach (ZipItem item in entries)
            {
                string normalized = item.Name.TrimEnd('/');
                string[] components = normalized.Split('/');
                string current = "";
                for (int index = 0; index < components.Length - (item.Directory ? 0 : 1); index++)
                {
                    current = index == 0 ? components[index] : current + "/" + components[index];
                    directories.Add(current);
                }
                if (!item.Directory) files.Add(normalized, item);
            }
            var seenFiles = new HashSet<string>(StringComparer.Ordinal);
            var seenDirectories = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            VerifyExtractedDirectory(stage, stage, directories, files, seenDirectories, seenFiles, ref count, new byte[65536], ct);
            if (seenFiles.Count != files.Count || seenDirectories.Count != directories.Count)
                throw new AddonError("The extracted package tree is incomplete.");
        }

        private static void VerifyExtractedDirectory(string stage, string directory, HashSet<string> directories, Dictionary<string, ZipItem> files, HashSet<string> seenDirectories, HashSet<string> seenFiles, ref int count, byte[] buffer, CancellationToken ct)
        {
            using (NativeGamePaths.Guard guard = NativeGamePaths.LockDirectory(directory))
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++count > EntryLimit * 65) throw new AddonError("The extracted package tree has too many paths.");
                    string relative = child.Substring(stage.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
                    FileAttributes attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new AddonError("The extracted package tree contains a link.");
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!directories.Contains(relative) || !seenDirectories.Add(relative)) throw new AddonError("The extracted package tree contains an unexpected directory.");
                        VerifyExtractedDirectory(stage, child, directories, files, seenDirectories, seenFiles, ref count, buffer, ct);
                    }
                    else
                    {
                        ZipItem item;
                        if (!files.TryGetValue(relative, out item) || !seenFiles.Add(relative)) throw new AddonError("The extracted package tree contains an unexpected file.");
                        using (FileStream input = NativeGamePaths.OpenRead(child))
                        {
                            if (input.Length != item.Size) throw new AddonError("An extracted addon file changed size.");
                            long readTotal = 0;
                            uint crc = 0xffffffff;
                            while (true)
                            {
                                int read = input.Read(buffer, 0, buffer.Length);
                                if (read == 0) break;
                                readTotal += read;
                                if (readTotal > item.Size) throw new AddonError("An extracted addon file changed size.");
                                crc = UpdateCrc(crc, buffer, read);
                            }
                            if (readTotal != item.Size || (crc ^ 0xffffffff) != item.Crc) throw new AddonError("An extracted addon file changed after verification.");
                        }
                    }
                }
            }
        }


        private static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                uint value = index;
                for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1;
                table[index] = value;
            }
            return table;
        }
        private static uint UpdateCrc(uint crc, byte[] bytes, int count)
        {
            for (int index = 0; index < count; index++) crc = CrcTable[(crc ^ bytes[index]) & 255] ^ (crc >> 8);
            return crc;
        }

        private static AddonResult InstallHelper(GameInstallation installation, string bundle, CancellationToken ct)
        {
            string[] names = { "Aurify.toc", "Aurify.lua" };
            byte[][] bytes = { NativeGamePaths.ReadFile(Path.Combine(bundle, names[0]), GameTools.TocLimit), NativeGamePaths.ReadFile(Path.Combine(bundle, names[1]), 256 * 1024) };
            Dictionary<string, string> metadata = GameTools.ReadToc(Path.Combine(bundle, names[0]));
            if (!GameTools.SupportsForever(metadata) || !String.Equals(GameTools.Field(metadata, "title"), "Aurify", StringComparison.OrdinalIgnoreCase) || String.IsNullOrWhiteSpace(GameTools.Field(metadata, "version")) || GameTools.Field(metadata, "x-aurify-managed") != "1")
                throw new AddonError("The bundled Aurify addon is invalid.");
            string root = SafeRoot(installation, true);
            RecoverTransaction(root, "Aurify", installation, ct);
            using (NativeGamePaths.Guard rootGuard = NativeGamePaths.LockDirectory(root))
            {
                string target = FindName(root, "Aurify");
                if (target != null)
                {
                    using (NativeGamePaths.Guard helper = NativeGamePaths.LockDirectory(target))
                    {
                        Dictionary<string, string> existing = GameTools.ReadToc(Path.Combine(target, names[0]));
                        Version current, bundled;
                        if (GameTools.SupportsForever(existing) && String.Equals(GameTools.Field(existing, "title"), "Aurify", StringComparison.OrdinalIgnoreCase) &&
                            (!Version.TryParse(GameTools.Field(existing, "version"), out current) ||
                            !Version.TryParse(GameTools.Field(metadata, "version"), out bundled) || current > bundled))
                        {
                            installation.Addons["activity"] = GameTools.Ready(existing);
                            return new AddonResult { Status = "present", Version = installation.Addons["activity"].Version,
                                Message = "A newer or unversioned Aurify helper was preserved without downgrading." };
                        }
                    }
                }
                bool changed;
                if (target != null) changed = RefreshHelper(target, bundle, names, bytes, ct, installation);
                else
                {
                    string stage = Path.Combine(root, ".aurify-addons-" + Guid.NewGuid().ToString("N"));
                    ObserveMutation(installation, ct);
                    NativeGamePaths.CreateNewDirectory(stage);
                    string stageIdentity = NativeGamePaths.DirectoryIdentity(stage);
                    try
                    {
                        using (NativeGamePaths.Guard stageGuard = NativeGamePaths.LockDirectory(stage))
                        {
                            string directory = Path.Combine(stage, "Aurify");
                            ObserveMutation(installation, ct);
                            NativeGamePaths.CreateNewDirectory(directory);
                            using (NativeGamePaths.Guard helper = NativeGamePaths.LockDirectory(directory))
                            {
                                for (int index = 0; index < names.Length; index++)
                                {
                                    ObserveMutation(installation, ct);
                                    WriteNew(Path.Combine(directory, names[index]), bytes[index]);
                                }
                                ObserveMutation(installation, ct);
                                WriteNew(Path.Combine(directory, OwnershipName), OwnershipBytes(
                                    HelperPackage(GameTools.Field(metadata, "version")), HelperHashes(names, bytes)));
                            }
                            target = FindName(root, "Aurify");
                            if (target != null) changed = RefreshHelper(target, bundle, names, bytes, ct, installation);
                            else
                            {
                                ObserveMutation(installation, ct);
                                try { RenameDirectory(directory, NativeGamePaths.DirectoryIdentity(directory), Path.Combine(root, "Aurify")); changed = true; }
                                catch (IOException)
                                {
                                    target = FindName(root, "Aurify");
                                    if (target == null) throw;
                                    changed = RefreshHelper(target, bundle, names, bytes, ct, installation);
                                }
                            }
                        }
                    }
                    finally { if (!Exists(TransactionPath(root, "Aurify"))) CleanupStage(root, rootGuard.Identity, stage, stageIdentity, installation); }
                }
                // Read the published identity, not merely the trusted source bytes.
                target = FindName(root, "Aurify");
                Dictionary<string, string> installed = GameTools.ReadToc(Path.Combine(target, names[0]));
                if (!GameTools.SupportsForever(installed) || !String.Equals(GameTools.Field(installed, "title"), "Aurify", StringComparison.OrdinalIgnoreCase) || GameTools.Field(installed, "version") != GameTools.Field(metadata, "version") || GameTools.Field(installed, "x-aurify-managed") != "1")
                    throw new AddonError("The Aurify helper changed while it was being installed.", "blocked");
                installation.Addons["activity"] = GameTools.Ready(installed);
                return new AddonResult { Status = changed ? "installed" : "present", Version = installation.Addons["activity"].Version, RestartRequired = changed && installation.Running, Message = changed ? "The bundled Aurify helper is ready. Other addon files and settings were preserved." : "The bundled Aurify helper is already current. Existing files and settings were preserved." };
            }
        }

        private static AddonPackage HelperPackage(string version)
        {
            return new AddonPackage("activity", "Aurify", "Aurify", version, "Aurify.toc", "Aurify", null, null);
        }

        private static Dictionary<string, object> HelperHashes(string[] names, byte[][] bytes)
        {
            var hashes = Json.New();
            for (int index = 0; index < names.Length; index++) hashes.Add(names[index], Json.Hash(bytes[index]));
            return hashes;
        }

        private static void ObserveMutation(GameInstallation installation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            installation.Running |= GameTools.IsRunning(installation);
            if (installation.Running) throw new AddonError("Close World of Warcraft to allow automatic addon installation. Existing addon files and identity were preserved.", "deferred");
            ct.ThrowIfCancellationRequested();
        }

        private static bool RefreshHelper(string target, string bundle, string[] names, byte[][] bytes, CancellationToken ct, GameInstallation installation)
        {
            AddonPackage package = HelperPackage(GameTools.Field(GameTools.ReadToc(Path.Combine(bundle, names[0])), "version"));
            bool changed = false;
            using (var guard = NativeGamePaths.LockDirectory(target))
            {
                Dictionary<string, string> metadata = GameTools.ReadToc(Path.Combine(target, names[0]));
                if (!GameTools.SupportsForever(metadata) || !String.Equals(GameTools.Field(metadata, "title"), "Aurify", StringComparison.OrdinalIgnoreCase) ||
                    GameTools.Field(metadata, "x-aurify-managed") != "1") throw Blocked("Aurify");
                var currentBytes = new byte[names.Length][];
                for (int index = 0; index < names.Length; index++)
                {
                    currentBytes[index] = NativeGamePaths.ReadFile(Path.Combine(target, names[index]), FileLimit);
                    changed |= !EqualBytes(currentBytes[index], bytes[index]);
                }
                Dictionary<string, object> owned;
                if (FindName(target, OwnershipName) == null)
                {
                    Dictionary<string, object> currentHashes = HelperHashes(names, currentBytes);
                    // Exact previously shipped 1.0.0 bytes are the only legacy
                    // adoption authority besides equality with today's bundle.
                    bool shipped = GameTools.Field(metadata, "version") == "1.0.0" &&
                        Json.Str(currentHashes["Aurify.toc"]) == "d604c62cf7a9cff73e1e33efaf4239554a244df716eaee23d63f6a5769f0f862" &&
                        Json.Str(currentHashes["Aurify.lua"]) == "4789c59812b867f1c1884a6469f5ce96b95da4a7d9ddef57c6f44d59b9307a44";
                    if (changed && !shipped)
                        throw new AddonError("The existing Aurify helper has no verified ownership record. Its files were preserved; review the installation before updating.", "update-needed");
                    ObserveMutation(installation, ct);
                    WriteNew(Path.Combine(target, OwnershipName), OwnershipBytes(
                        HelperPackage(GameTools.Field(metadata, "version")), currentHashes));
                    owned = currentHashes;
                }
                else owned = ReadOwnership(target, package);
                foreach (var entry in owned)
                    if (Json.Hash(NativeGamePaths.ReadFile(Path.Combine(target, entry.Key.Replace('/', Path.DirectorySeparatorChar)), FileLimit)) != Json.Str(entry.Value)) throw Blocked("Aurify");
            }
            if (!changed) return false;
            string root = Path.GetDirectoryName(target), stage = Path.Combine(root, ".aurify-addons-" + Guid.NewGuid().ToString("N"));
            ObserveMutation(installation, ct);
            NativeGamePaths.CreateNewDirectory(stage);
            string stageId = NativeGamePaths.DirectoryIdentity(stage), rootId = NativeGamePaths.DirectoryIdentity(root);
            try
            {
                using (var guard = NativeGamePaths.LockDirectory(stage))
                {
                    string source = Path.Combine(stage, "Aurify");
                    ObserveMutation(installation, ct);
                    NativeGamePaths.CreateNewDirectory(source);
                    for (int index = 0; index < names.Length; index++)
                    {
                        ObserveMutation(installation, ct);
                        WriteNew(Path.Combine(source, names[index]), bytes[index]);
                    }
                    RefreshPackage(target, source, package, ct, installation);
                }
                return true;
            }
            finally { if (!Exists(TransactionPath(root, "Aurify"))) CleanupStage(root, rootId, stage, stageId, installation); }
        }

        private static bool EqualBytes(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int index = 0; index < left.Length; index++) if (left[index] != right[index]) return false;
            return true;
        }
        private static void WriteNew(string path, byte[] bytes)
        {
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes, 0, bytes.Length);
                output.Flush(true);
                if (output.Length != bytes.Length) throw new IOException("The addon file could not be completely written.");
            }
        }

        private static void CleanupStage(string root, string rootIdentity, string stage, string stageIdentity, GameInstallation installation)
        {
            try
            {
                using (NativeGamePaths.Guard rootGuard = NativeGamePaths.LockDirectory(root))
                {
                    ObserveMutation(installation, CancellationToken.None);
                    if (rootGuard.Identity != rootIdentity || NativeGamePaths.DirectoryIdentity(stage) != stageIdentity) return;
                    DeleteOwnedTree(stage, stageIdentity, 0, installation, null, stage);
                }
            }
            catch { /* An uncertain tree is retained, never recursively followed. */ }
        }

        private static void DeleteOwnedTree(string directory, string identity, int depth, GameInstallation installation, Dictionary<string, object> expected, string treeRoot)
        {
            if (depth > 66) throw new IOException("Unsafe staging tree depth.");
            string actual;
            using (var guard = OpenTransactionDirectory(directory, true, out actual))
            {
                RequireIdentity(actual, identity);
                int count = 0;
                foreach (string child in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (++count > EntryLimit * 2) throw new IOException("Unsafe staging tree size.");
                    string relative = child.Substring(treeRoot.Length + 1).Replace('\\', '/');
                    string value = expected == null ? null : Json.Str(Json.Get(expected, relative));
                    if (expected != null && value == null) throw Blocked(Path.GetFileName(treeRoot));
                    FileAttributes attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Staging directory changed to a link.");
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        string childId;
                        using (var check = OpenTransactionDirectory(child, false, out childId)) { }
                        if (expected != null && value != "dir:" + childId) throw Blocked(Path.GetFileName(treeRoot));
                        DeleteOwnedTree(child, childId, depth + 1, installation, expected, treeRoot);
                    }
                    else
                    {
                        using (var file = CreateFile(child, 0x80010000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
                        {
                            TransactionFileInfo info;
                            if (file.IsInvalid || !GetFileInformationByHandle(file, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                            if ((info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0 || info.Links != 1) throw Blocked(Path.GetFileName(treeRoot));
                            using (var input = new FileStream(file, FileAccess.Read))
                            {
                                if (input.Length > FileLimit) throw Blocked(Path.GetFileName(treeRoot));
                                if (expected != null)
                                    using (var hash = SHA256.Create())
                                        if (BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant() != value) throw Blocked(Path.GetFileName(treeRoot));
                                ObserveMutation(installation, CancellationToken.None);
                                DeleteHandle(file);
                            }
                        }
                    }
                }
                ObserveMutation(installation, CancellationToken.None);
                DeleteHandle(guard);
            }
        }

        private static void DeleteHandle(SafeFileHandle handle)
        {
            IntPtr information = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(information, 1);
                if (!SetFileInformationByHandle(handle, 4, information, 4)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(information); }
        }
    }
}
