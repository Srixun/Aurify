using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("Aurify")]
[assembly: AssemblyProduct("Aurify")]
[assembly: AssemblyDescription("Aurify native guild sync and dual-upload companion")]
[assembly: AssemblyVersion("2.0.6.0")]

internal static class Aurify
{
    private const string Product = "Aurify";
    private const string StateName = ".aurify-install.json";
    private const string ManifestName = ".aurify-release.json";
    // Only these exact historical schemas are accepted for migration/removal.
    private const int OriginalPayloadLength = 7;
    private static readonly string[] Payload = {
        "Aurify.exe", "addon/Aurify/Aurify.toc", "addon/Aurify/Aurify.lua"
    };
    private static readonly string[] LegacyPayload = {
        "Aurify.exe", "node.exe", "aurify.mjs", "addon/Aurify/Aurify.toc",
        "addon/Aurify/Aurify.lua", "licenses/node-LICENSE", "licenses/luaparse-LICENSE",
        "licenses/yauzl-LICENSE", "licenses/pend-LICENSE"
    };
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 131072 };
    private static readonly string Executable = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);

    private sealed class Options
    {
        public string Root;
        public string Startup;
        public string Desktop;
        public bool Background;
        public bool Host;
        public bool Uninstall;
        public string RemoveToken;
        public int ParentPid;
        public long ParentStarted;
        public string UpdateManifest;
    }

    public sealed class Installation
    {
        public string Product { get; set; }
        public int Format { get; set; }
        public string Id { get; set; }
        public string Root { get; set; }
        public string Startup { get; set; }
        public string Desktop { get; set; }
        public string Current { get; set; }
        public List<string> Releases { get; set; }
        public string RemoveToken { get; set; }
        public string RemoveWorker { get; set; }
        public long UpdateSequence { get; set; }
        public string UpdateRelease { get; set; }
        public string Previous { get; set; }
    }

    public sealed class Release
    {
        public string Product { get; set; }
        public int Format { get; set; }
        public string Id { get; set; }
        public Dictionary<string, string> Hashes { get; set; }
    }

    public sealed class Control
    {
        public string token { get; set; }
    }


    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existing, string destination, uint flags);

    [STAThread]
    private static int Main(string[] args)
    {
        SetErrorMode(0x0001 | 0x0002 | 0x8000);
        Options options = null;
        bool quiet = Array.IndexOf(args, "--background") >= 0 || Array.IndexOf(args, "--host") >= 0;
        try
        {
            options = Parse(args);
#if UNINSTALL_WORKER
            if (options.RemoveToken == null) throw new InvalidOperationException("Use Aurify's Uninstall shortcut to remove an installation.");
            return RemoveInstallation(options);
#else
            if (options.RemoveToken != null) throw new InvalidOperationException("Use Aurify's Uninstall shortcut to remove an installation.");
            if (options.Host) return RunHost(options);
            if (options.Uninstall) return BeginUninstall(options);
            InstallAndRun(options);
            return 0;
#endif
        }
        catch (Exception error)
        {
            Log(options, "launcher-failed", FailureCode(error));
            if (!quiet)
            {
                string message = error is InvalidOperationException ? error.Message :
                    "Aurify could not access its own installation files. Download Aurify.exe to a writable local folder, close any Aurify installer already open, and try again. Your settings have not been removed.";
                MessageBox.Show(message, Product, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 1;
        }
    }

    private static Options Parse(string[] args)
    {
        Options options = new Options();
        string root = null;
        string startup = null;
        string desktop = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--background": options.Background = true; break;
                case "--host": options.Host = true; break;
                case "--uninstall": options.Uninstall = true; break;
                case "--install-root": root = Value(args, ref i); break;
                case "--startup-dir": startup = Value(args, ref i); break;
                case "--desktop-dir": desktop = Value(args, ref i); break;
                case "--remove-token": options.RemoveToken = Value(args, ref i); break;
                case "--parent-pid": options.ParentPid = Int32.Parse(Value(args, ref i), CultureInfo.InvariantCulture); break;
                case "--parent-started": options.ParentStarted = Int64.Parse(Value(args, ref i), CultureInfo.InvariantCulture); break;
                case "--update-manifest": options.UpdateManifest = LocalDirectory(Value(args, ref i)); break;
                default: throw new InvalidOperationException("This Aurify launch option is not supported.");
            }
        }
        if ((root == null) != (startup == null))
            throw new InvalidOperationException("Isolated launches require both --install-root and --startup-dir with absolute local paths.");
        if (desktop != null && root == null)
            throw new InvalidOperationException("A custom Desktop folder requires --install-root and --startup-dir.");
        if ((options.Host && options.Uninstall) || (options.Background && options.Uninstall) ||
            (options.RemoveToken != null && (options.Host || options.Uninstall || options.Background)))
            throw new InvalidOperationException("These Aurify launch modes cannot be combined.");
        if (options.UpdateManifest != null && (!options.Background || options.Host || options.Uninstall || options.RemoveToken != null))
            throw new InvalidOperationException("Updates require a background installer.");
        if (root == null)
        {
            // A directly opened installed copy retains its isolated installation, too.
            DirectoryInfo releaseDirectory = new DirectoryInfo(Path.GetDirectoryName(Executable));
            if (releaseDirectory.Parent != null && releaseDirectory.Parent.Name == "releases" &&
                releaseDirectory.Parent.Parent != null && releaseDirectory.Parent.Parent.Name == "App" &&
                releaseDirectory.Parent.Parent.Parent != null)
            {
                string candidate = releaseDirectory.Parent.Parent.Parent.FullName;
                Installation installed = LoadState(candidate, false);
                if (installed != null && installed.Releases.Contains(releaseDirectory.Name) &&
                    Same(ReleasePath(candidate, releaseDirectory.Name), releaseDirectory.FullName))
                {
                    root = candidate;
                    startup = installed.Startup;
                    desktop = installed.Desktop;
                }
            }
            if (root == null)
            {
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product);
                startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            }
        }
        options.Root = LocalDirectory(root);
        options.Startup = LocalDirectory(startup);
        if (desktop == null)
        {
            Installation installed = LoadState(options.Root, false);
            if (installed != null && Same(installed.Startup, options.Startup)) desktop = installed.Desktop;
            // Published older updaters supply the default root and Startup only.
            // Never infer the real Desktop for an isolated legacy installation.
            if (desktop == null &&
                Same(options.Root, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product)) &&
                Same(options.Startup, Environment.GetFolderPath(Environment.SpecialFolder.Startup)))
                desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (desktop == null)
                throw new InvalidOperationException("Isolated launches require an explicit --desktop-dir until their Desktop folder is recorded.");
        }
        options.Desktop = LocalDirectory(desktop);
        ValidateFolders(options.Root, options.Startup, options.Desktop);
        return options;
    }

    private static void ValidateFolders(string root, string startup, string desktop)
    {
        if (Inside(root, startup) || Inside(startup, root) ||
            (desktop != null && (Inside(root, desktop) || Inside(desktop, root) ||
                Inside(startup, desktop) || Inside(desktop, startup))))
            throw new InvalidOperationException("Aurify's install, Startup and Desktop folders must be separate, non-nested folders.");
    }

    private static void BindInstallation(Options options, Installation state)
    {
        if (!Same(state.Startup, options.Startup) || (state.Desktop != null && !Same(state.Desktop, options.Desktop)))
            throw new InvalidOperationException("The selected Startup or Desktop folder does not belong to this installation. Use its existing shortcut or a separate isolated install root.");
        state.Desktop = options.Desktop;
    }

    private static string Value(string[] args, ref int index)
    {
        if (++index >= args.Length || String.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
            throw new InvalidOperationException("An Aurify launch option is missing its value.");
        return args[index];
    }

    private static string LocalDirectory(string value)
    {
        if (String.IsNullOrWhiteSpace(value) || value.Length < 3 || value[1] != ':' ||
            (value[2] != '\\' && value[2] != '/') || !Char.IsLetter(value[0]))
            throw new InvalidOperationException("Aurify requires an absolute local folder path.");
        string path = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (path.Length <= 3) throw new InvalidOperationException("Aurify cannot use a drive root as an installation folder.");
        NoLinks(path);
        return path;
    }

    private static bool Same(string left, string right)
    {
        return String.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool Inside(string parent, string child)
    {
        return Same(parent, child) || Path.GetFullPath(child).StartsWith(Path.GetFullPath(parent).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void NoLinks(string path)
    {
        for (string current = Path.GetFullPath(path); !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Aurify will not install through linked folders or replace linked files. Choose ordinary local folders.");
        }
    }

    private static string Digest(byte[] bytes)
    {
        using (SHA256 hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    private static string FileDigest(string path)
    {
        NoLinks(path);
        using (FileStream stream = File.OpenRead(path))
        using (SHA256 hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private static bool IsHash(string value)
    {
        if (value == null || value.Length != 64) return false;
        foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
        return true;
    }

    internal static SecurityIdentifier RuntimeUser()
    {
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User == null) throw new InvalidOperationException("Aurify could not identify this Windows user.");
            return identity.User;
        }
    }

    internal static string RuntimeKey()
    {
#if AURIFY_TEST
        string testRuntime = Environment.GetEnvironmentVariable("AURIFY_TEST_RUNTIME_KEY");
        if (String.IsNullOrEmpty(testRuntime))
            throw new InvalidOperationException("The native test runtime identity is not configured.");
        return Digest(Encoding.UTF8.GetBytes("test:" + testRuntime)).Substring(0, 24);
#else
        return Digest(Encoding.UTF8.GetBytes(RuntimeUser().Value)).Substring(0, 24);
#endif
    }

    private static string Key(Options options)
    {
        return RuntimeKey();
    }

    private static string LegacyKey(Options options)
    {
        return Digest(Encoding.UTF8.GetBytes(Path.Combine(options.Root, "config.json").ToLowerInvariant())).Substring(0, 24);
    }

    private static string ReleasePath(string root, string version)
    {
        if (!IsHash(version)) throw new InvalidOperationException("Aurify's installation record is invalid. Download Aurify.exe again; existing data will be preserved.");
        return Path.Combine(root, "App", "releases", version);
    }

    private static T ReadJson<T>(string path)
    {
        NoLinks(path);
        if (new FileInfo(path).Length > 131072) throw new InvalidOperationException("Aurify's installation record is too large to read safely.");
        return Json.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
    }

    private static Installation LoadState(string root, bool required)
    {
        string path = Path.Combine(root, StateName);
        if (!File.Exists(path))
        {
            if (required) throw new InvalidOperationException("No owned Aurify installation was found. No files or startup entries were removed.");
            return null;
        }
        Installation state = ReadJson<Installation>(path);
        Guid id;
        if (state == null || state.Product != Product || state.Format != 1 || !Guid.TryParse(state.Id, out id) ||
            !Same(state.Root, root) || state.Releases == null || state.Releases.Count > 256 ||
            !IsHash(state.Current) || !state.Releases.Contains(state.Current))
            throw new InvalidOperationException("Aurify's installation record is not recognized. Existing files and settings were left untouched.");
        if (state.UpdateSequence < 0 || state.UpdateSequence > 9007199254740991L ||
            (state.UpdateSequence != 0 && !IsHash(state.UpdateRelease)) ||
            (state.Previous != null && (!IsHash(state.Previous) || !state.Releases.Contains(state.Previous))))
            throw new InvalidOperationException("Aurify's update recovery record is invalid.");
        LocalDirectory(state.Startup);
        if (state.Desktop != null) LocalDirectory(state.Desktop);
        ValidateFolders(root, state.Startup, state.Desktop);
        foreach (string release in state.Releases) ReleasePath(root, release);
        return state;
    }

    private static void SaveState(Options options, Installation state)
    {
        string path = Path.Combine(options.Root, StateName);
        NoLinks(path);
        string temporary = Path.Combine(options.Root, ".aurify-state-" + Guid.NewGuid().ToString("N") + ".tmp");
        // Same write-through/Flush(true)/MoveFileEx publication used by the
        // native storage layer, kept self-contained for UNINSTALL_WORKER.
        try
        {
            WriteDurable(temporary, new UTF8Encoding(false).GetBytes(Json.Serialize(state)));
            NoLinks(path);
            if (!MoveFileEx(temporary, path, 0x1 | 0x8)) throw new IOException("Aurify's installation state could not be published.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteDurable(string path, byte[] bytes)
    {
        using (FileStream output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
        {
            output.Write(bytes, 0, bytes.Length);
            output.Flush(true);
        }
    }

    private static FileStream Lock(Options options)
    {
        Directory.CreateDirectory(options.Root);
        string path = Path.Combine(options.Root, ".aurify-launcher.lock");
        NoLinks(path);
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                if (elapsed.ElapsedMilliseconds >= 90000) throw new InvalidOperationException("Another Aurify installation is busy. Wait for it to finish and try again.");
                Thread.Sleep(150);
            }
        }
    }

    private static ZipArchive EmbeddedPayload()
    {
        Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Aurify.Payload.zip");
        if (resource == null) throw new InvalidOperationException("Aurify's embedded runtime is missing. Download Aurify.exe again.");
        ZipArchive archive = new ZipArchive(resource, ZipArchiveMode.Read, false);
        try
        {
            if (archive.Entries.Count != Payload.Length - 1) throw new InvalidDataException("Unexpected payload entry count");
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.FullName == "Aurify.exe" || Array.IndexOf(Payload, entry.FullName) < 0 || !names.Add(entry.FullName) ||
                    entry.Length <= 0 || entry.Length > 32L * 1024 * 1024)
                    throw new InvalidDataException("Unexpected payload entry");
            }
            return archive;
        }
        catch { archive.Dispose(); throw; }
    }

    private static Release InspectPayload()
    {
        Release release = new Release { Product = Product, Format = 2, Hashes = new Dictionary<string, string>() };
        StringBuilder identity = new StringBuilder();
        using (ZipArchive archive = EmbeddedPayload())
        foreach (string relative in Payload)
        {
            string digest;
            if (relative == "Aurify.exe") digest = FileDigest(Executable);
            else
            {
                using (Stream stream = archive.GetEntry(relative).Open())
                using (SHA256 hash = SHA256.Create())
                    digest = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            release.Hashes.Add(relative, digest);
            identity.Append(relative).Append('\n').Append(digest).Append('\n');
        }
        release.Id = Digest(Encoding.UTF8.GetBytes(identity.ToString()));
        return release;
    }

    private static Release ReadRelease(string path, string version)
    {
        Release release = ReadJson<Release>(Path.Combine(path, ManifestName));
        if (release == null || release.Product != Product || release.Id != version || release.Hashes == null ||
            !((release.Format == 2 && release.Hashes.Count == Payload.Length) ||
              (release.Format == 1 && (release.Hashes.Count == OriginalPayloadLength || release.Hashes.Count == LegacyPayload.Length))))
            throw new InvalidOperationException("An Aurify release is not recognized. Its files were not overwritten or removed.");
        string[] schema = release.Format == 2 ? Payload : LegacyPayload;
        StringBuilder identity = new StringBuilder();
        for (int i = 0; i < release.Hashes.Count; i++)
        {
            string relative = schema[i];
            string hash;
            if (!release.Hashes.TryGetValue(relative, out hash) || !IsHash(hash))
                throw new InvalidOperationException("An Aurify release has an invalid file record.");
            identity.Append(relative).Append('\n').Append(hash).Append('\n');
        }
        if (Digest(Encoding.UTF8.GetBytes(identity.ToString())) != version)
            throw new InvalidOperationException("An Aurify release has an invalid identity.");
        return release;
    }

    private static string Stage(Options options, Installation state)
    {
        Release payload = InspectPayload();
        string destination = ReleasePath(options.Root, payload.Id);
        NoLinks(destination);
        if (Directory.Exists(destination))
        {
            ReadRelease(destination, payload.Id);
            foreach (string relative in Payload)
                if (FileDigest(Path.Combine(destination, relative)) != payload.Hashes[relative])
                    throw new InvalidOperationException("An installed Aurify release was changed. It will not be overwritten while it may be running. Keep your config and download a fresh Aurify.exe.");
            return payload.Id;
        }
        if (state != null && state.Releases.Count >= 256)
            throw new InvalidOperationException("Aurify has retained many previous releases. Uninstall Aurify while keeping its data, then run the new download.");
        string staging = Path.Combine(options.Root, "App", "releases", ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using (ZipArchive archive = EmbeddedPayload())
            foreach (string relative in Payload)
            {
                string target = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (Stream input = relative == "Aurify.exe" ? (Stream)File.OpenRead(Executable) : archive.GetEntry(relative).Open())
                using (FileStream output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768, FileOptions.WriteThrough))
                {
                    input.CopyTo(output);
                    output.Flush(true);
                }
                if (FileDigest(target) != payload.Hashes[relative])
                    throw new InvalidOperationException("Aurify's download changed during installation. Download it again and retry.");
            }
            WriteDurable(Path.Combine(staging, ManifestName), new UTF8Encoding(false).GetBytes(Json.Serialize(payload)));
            if (!MoveFileEx(staging, destination, 0x8)) throw new IOException("Aurify's verified release could not be published.");
        }
        finally
        {
            // Only this invocation's newly created, private staging directory is recursive.
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
        return payload.Id;
    }

#if !AURIFY_TEST
    private static bool ConfirmFirstInstall(Options options)
    {
        const string privacy = "https://auratism.com/privacy";
        using (Form dialog = new Form())
        using (TableLayoutPanel content = new TableLayoutPanel())
        using (Label heading = new Label())
        using (Label details = new Label())
        using (LinkLabel policy = new LinkLabel())
        using (FlowLayoutPanel actions = new FlowLayoutPanel())
        using (Button install = new Button())
        using (Button cancel = new Button())
        {
            dialog.Text = "Install Aurify";
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            dialog.StartPosition = FormStartPosition.CenterScreen;
            dialog.ClientSize = new System.Drawing.Size(680, 470);
            dialog.MinimumSize = new System.Drawing.Size(620, 430);
            dialog.MinimizeBox = false;
            dialog.MaximizeBox = false;
            dialog.ShowInTaskbar = true;
            dialog.AutoScaleMode = AutoScaleMode.Dpi;
            dialog.Font = new System.Drawing.Font("Segoe UI", 10F);

            content.Dock = DockStyle.Fill;
            content.AutoScroll = true;
            content.Padding = new Padding(24, 20, 24, 16);
            content.ColumnCount = 1;
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            heading.AutoSize = true;
            heading.Font = new System.Drawing.Font(dialog.Font, System.Drawing.FontStyle.Bold);
            heading.Margin = new Padding(0, 0, 0, 14);
            heading.Text = "Install Aurify for this Windows user?";

            details.AutoSize = true;
            details.MaximumSize = new System.Drawing.Size(610, 0);
            details.Margin = new Padding(0, 0, 0, 14);
            details.Text =
                "Aurify has not changed your system yet.\r\n\r\n" +
                "If you continue, Aurify will:\r\n" +
                "- Copy its managed app files to:\r\n  " + options.Root + "\r\n" +
                "- Create Desktop, automatic-startup, and Uninstall shortcuts.\r\n" +
                "- Start a tray companion when this Windows user signs in.\r\n\r\n" +
                "Pairing requests send the device label and pairing identifiers to auratism.com before approval; game data uploads begin only after you approve the device. Choosing Connect ForeverLogs starts an OAuth request to foreverlogs.gg; combat logs upload only after you approve that connection.\r\n\r\n" +
                "After pairing, Aurify checks for the latest authenticated update. It downloads that update only after you select Install latest update.\r\n\r\n" +
                "Uninstall removes the managed app and shortcuts, but intentionally keeps pairing, settings, checkpoints, game addons, pending data, and changed or unrecognized files. The privacy policy explains stored data, destinations, controls, and deletion.";

            policy.AutoSize = true;
            policy.Margin = new Padding(0, 0, 0, 12);
            policy.Text = "Read the full Aurify privacy policy";
            policy.Links.Add(0, policy.Text.Length, privacy);
            policy.LinkClicked += delegate
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = privacy, UseShellExecute = true });
                }
                catch
                {
                    MessageBox.Show("Open this address in a browser:\r\n\r\n" + privacy,
                        Product, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };

            install.AutoSize = true;
            install.MinimumSize = new System.Drawing.Size(100, 34);
            install.Text = "Install";
            install.DialogResult = DialogResult.OK;

            cancel.AutoSize = true;
            cancel.MinimumSize = new System.Drawing.Size(100, 34);
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;

            actions.AutoSize = true;
            actions.Dock = DockStyle.Bottom;
            actions.Padding = new Padding(16, 8, 24, 12);
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.WrapContents = false;
            actions.Controls.Add(cancel);
            actions.Controls.Add(install);

            content.Controls.Add(heading);
            content.Controls.Add(details);
            content.Controls.Add(policy);
            dialog.Controls.Add(content);
            dialog.Controls.Add(actions);
            dialog.CancelButton = cancel;
            dialog.ActiveControl = cancel;
            return dialog.ShowDialog() == DialogResult.OK;
        }
    }
#endif

    private static void InstallAndRun(Options options)
    {
        Installation observed = LoadState(options.Root, false);
#if !AURIFY_TEST
        if (observed == null)
        {
            if (options.Background)
                throw new InvalidOperationException("First installation requires interactive approval. Run Aurify.exe without --background.");
            if (!ConfirmFirstInstall(options)) return;
        }
#endif
        using (Lock(options))
        {
            Installation state = LoadState(options.Root, false);
#if !AURIFY_TEST
            if (observed != null && state == null)
                throw new InvalidOperationException("Aurify's installation changed before setup began. Run Aurify.exe again.");
#endif
            if (state != null) BindInstallation(options, state);
            if (state != null && state.RemoveToken != null)
                throw new InvalidOperationException("Aurify removal is pending. Complete removal before installing again.");
            PreflightShortcuts(options, state);
            // A journal survives process/power loss at every publication boundary.
            if (state != null && state.Previous != null) RestorePrevious(options, state);
            long sequence = 0;
#if !UNINSTALL_WORKER
            if (options.UpdateManifest != null)
            {
                if (state == null) throw new InvalidOperationException("An update requires an existing installation.");
                NoLinks(options.UpdateManifest);
                if (new FileInfo(options.UpdateManifest).Length > AurifyClient.NativeUpdate.MaximumManifest)
                    throw new InvalidDataException("Update manifest is too large.");
                Release payload = InspectPayload();
                Version application = Assembly.GetExecutingAssembly().GetName().Version;
                AurifyClient.UpdateRelease update = AurifyClient.NativeUpdate.VerifyLatest(
                    File.ReadAllText(options.UpdateManifest, new UTF8Encoding(false, true)),
                    AurifyClient.Json.Now(), state.UpdateSequence, AurifyClient.NativeUpdate.PublicKey(),
                    payload.Id, application.ToString(3));
                AurifyClient.NativeUpdate.VerifyFile(Executable, update);
                sequence = update.Sequence;
            }
#endif
            // Once a signed sequence is accepted, a direct installer may repair
            // only the exact current payload. Explicit/manual launch is not an
            // authorization to replace that signed release with unsigned bytes.
            if (state != null && state.UpdateSequence > 0 && options.UpdateManifest == null &&
                InspectPayload().Id != state.Current)
                throw new InvalidOperationException("This installation accepts only signed updates. Its current release and running host were preserved.");
            string version;
            if (state != null && state.Releases.Exists(delegate(string release) {
                return Same(Path.GetDirectoryName(Executable), ReleasePath(options.Root, release));
            }))
                version = state.Current;
            else version = Stage(options, state);
            if (state == null)
            {
                state = new Installation { Product = Product, Format = 1, Id = Guid.NewGuid().ToString("N"), Root = options.Root,
                    Startup = options.Startup, Desktop = options.Desktop, Current = version, Releases = new List<string>() };
            }
            ActivateRelease(options, state, version, sequence);
        }
    }

    private static void ActivateRelease(Options options, Installation state, string version, long sequence)
    {
            BindInstallation(options, state);
            PreflightShortcuts(options, state);
            bool switching = state.Current != version;
            if (!state.Releases.Contains(version)) state.Releases.Add(version);
            if (switching)
            {
                state.Previous = state.Current;
                // Keep the high-water mark even when this signed release fails readiness.
                if (sequence != 0) { state.UpdateSequence = sequence; state.UpdateRelease = version; }
                SaveState(options, state);
            }
            try
            {
                if (switching || LegacyRuntimeRunning(options)) StopHost(options);
                state.Current = version;
                SaveState(options, state);
                RegisterShortcuts(options, state);
                if (RuntimeRunning(options))
                {
                    if (!options.Background) RequestSetup(options);
                    PipeCommand(options, "ping\n", 5000);
                }
                else StartHost(options, state);
                state.Previous = null;
                SaveState(options, state);
                RemoveLegacyReleases(options, state);
            }
            catch
            {
                if (state.Previous != null)
                {
                    try { RestorePrevious(options, state); }
                    catch (Exception error) { Log(options, "update-rollback-pending", error.GetType().Name); }
                }
                throw;
            }
    }

    private static void RestorePrevious(Options options, Installation state)
    {
        BindInstallation(options, state);
        PreflightShortcuts(options, state);
        string previous = state.Previous;
        string directory = ReleasePath(options.Root, previous);
        string executable = Path.Combine(directory, "Aurify.exe");
        Release release = ReadRelease(directory, previous);
        foreach (KeyValuePair<string, string> file in release.Hashes)
            if (FileDigest(Path.Combine(directory, file.Key)) != file.Value)
                throw new InvalidDataException("The recovery release was changed.");
        // Never force-kill an unresponsive candidate. Retain the journal and retry
        // safely on the next launcher invocation if graceful shutdown cannot drain.
        if (state.Current != previous) StopHost(options);
        state.Current = previous;
        SaveState(options, state);
        RegisterShortcuts(options, state);
        if (!TargetRuntimeRunning(options, executable)) StartHost(options, state);
        else TargetPipeCommand(options, executable, "ping\n", 5000);
        state.Previous = null;
        SaveState(options, state);
        Log(options, "update-rolled-back", previous);
    }

#if !UNINSTALL_WORKER
    internal static Installation UpdateInstallation(string root) { return LoadState(root, true); }
    internal static void UpdateFailure(string root, Exception error)
    {
        Log(new Options { Root = root }, "update-deferred", error.GetType().Name);
    }

    internal static void LaunchUpdate(Installation state, string candidate, string manifest)
    {
        using (Start(candidate, Arguments("--background", "--install-root", state.Root, "--startup-dir", state.Startup) +
            (state.Desktop == null ? "" : " " + Arguments("--desktop-dir", state.Desktop)) +
            " " + Arguments("--update-manifest", manifest))) { }
    }
#endif

    private static Mutex RuntimeMutex(Options options)
    {
        SecurityIdentifier user = RuntimeUser();
        MutexSecurity security = new MutexSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(user);
        security.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
        bool created;
        // One SID-scoped object is shared by every Aurify release and logon session.
        return new Mutex(false, "Global\\Aurify.Runtime." + Key(options), out created, security);
    }

    private static Mutex LegacyRuntimeMutex(Options options)
    {
        return new Mutex(false, "Global\\Aurify.Runtime." + LegacyKey(options));
    }

    private static bool Take(Mutex mutex, int timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    private static bool Running(Mutex mutex)
    {
        using (mutex)
        {
            if (!Take(mutex, 0)) return true;
            mutex.ReleaseMutex();
            return false;
        }
    }

    private static bool RuntimeRunning(Options options)
    {
        return Running(RuntimeMutex(options));
    }

    private static bool LegacyRuntimeRunning(Options options)
    {
        return Running(LegacyRuntimeMutex(options));
    }

    // CommandLineToArgvW / Microsoft C runtime quoting. No command shell is involved.
    private static string Quote(string value)
    {
        StringBuilder result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
            else result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static string Arguments(params string[] values)
    {
        string[] quoted = new string[values.Length];
        for (int i = 0; i < values.Length; i++) quoted[i] = Quote(values[i]);
        return String.Join(" ", quoted);
    }

    private static string LocationArguments(Options options)
    {
        return Arguments("--install-root", options.Root, "--startup-dir", options.Startup, "--desktop-dir", options.Desktop);
    }

    private static string TargetLocationArguments(Options options, string executable)
    {
        // Rollback can restore a published launcher that predates --desktop-dir.
        Version version = AssemblyName.GetAssemblyName(executable).Version;
        return version != null && version >= new Version(2, 0, 1, 0) ? LocationArguments(options) :
            Arguments("--install-root", options.Root, "--startup-dir", options.Startup);
    }

    private static bool UsesLegacyRuntime(string executable)
    {
        Version version = AssemblyName.GetAssemblyName(executable).Version;
        return version == null || version < new Version(2, 0, 2, 0);
    }

    private static bool TargetRuntimeRunning(Options options, string executable)
    {
        return UsesLegacyRuntime(executable) ? LegacyRuntimeRunning(options) : RuntimeRunning(options);
    }

    private static void TargetPipeCommand(Options options, string executable, string command, int timeout)
    {
        if (UsesLegacyRuntime(executable)) LegacyPipeCommand(options, command, timeout);
        else PipeCommand(options, command, timeout);
    }

    private static Process Start(string executable, string arguments)
    {
        Process process = new Process();
        process.StartInfo = new ProcessStartInfo(executable, arguments) {
            // Native GUI executables control visibility themselves through --background.
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)
        };
        if (!process.Start()) throw new InvalidOperationException("Aurify could not start its native executable. Download Aurify.exe again and retry.");
        return process;
    }

    private static void StartHost(Options options, Installation state)
    {
        string executable = Path.Combine(ReleasePath(options.Root, state.Current), "Aurify.exe");
        string args = Arguments("--host") + " " + TargetLocationArguments(options, executable) + (options.Background ? " " + Arguments("--background") : "");
        using (Process host = Start(executable, args))
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < 30000)
            {
                try
                {
                    TargetPipeCommand(options, executable, "ping\n", 2000);
                    if (!options.Background) TargetPipeCommand(options, executable, "setup\n", 10000);
                    return;
                }
                catch (IOException) { }
                catch (TimeoutException) { }
                if (host.HasExited)
                    throw new InvalidOperationException("Aurify could not start its native uploader. Your settings and old releases were kept. Sanitized diagnostics are in launcher.log.");
                Thread.Sleep(100);
            }
            throw new InvalidOperationException("Aurify's native uploader did not become ready. Old release files were kept. Close Aurify from its tray menu and retry.");
        }
    }

    private static void RemoveLegacyReleases(Options options, Installation state)
    {
        bool changed = false;
        foreach (string version in new List<string>(state.Releases))
        {
            if (version == state.Current) continue;
            try
            {
                string directory = ReleasePath(options.Root, version);
                if (!Directory.Exists(directory)) continue;
                Release release = ReadRelease(directory, version);
                if (release.Format != 1) continue;
                foreach (KeyValuePair<string, string> entry in release.Hashes)
                {
                    string path = Path.Combine(directory, entry.Key);
                    if (!File.Exists(path)) continue;
                    if (FileDigest(path) != entry.Value)
                    {
                        Log(options, "legacy-file-kept", version + "/" + entry.Key);
                        continue;
                    }
                    try { File.Delete(path); }
                    catch (IOException) { Log(options, "legacy-file-busy", version + "/" + entry.Key); }
                    catch (UnauthorizedAccessException) { Log(options, "legacy-file-kept", version + "/" + entry.Key); }
                }
                RemoveEmpty(Path.Combine(directory, "addon", "Aurify"));
                RemoveEmpty(Path.Combine(directory, "addon"));
                RemoveEmpty(Path.Combine(directory, "licenses"));
                string[] remaining = Directory.GetFileSystemEntries(directory);
                if (remaining.Length == 1 && Same(remaining[0], Path.Combine(directory, ManifestName)))
                {
                    File.Delete(remaining[0]);
                    Directory.Delete(directory);
                    state.Releases.Remove(version);
                    changed = true;
                }
                else Log(options, "legacy-release-kept", version);
            }
            catch (Exception error) { Log(options, "legacy-cleanup-deferred", error.GetType().Name); }
        }
        if (changed) SaveState(options, state);
    }

    private static int RunHost(Options options)
    {
#if UNINSTALL_WORKER
        throw new InvalidOperationException("The uninstall worker cannot run Aurify.");
#else
        Installation state = LoadState(options.Root, true);
        BindInstallation(options, state);
        string directory = ReleasePath(options.Root, state.Current);
        if (!Same(Executable, Path.Combine(directory, "Aurify.exe")) || !Same(state.Startup, options.Startup) || state.RemoveToken != null)
            throw new InvalidOperationException("This Aurify host does not match the active installation.");
        Release release = ReadRelease(directory, state.Current);
        if (release.Format != 2) throw new InvalidOperationException("Run the newly downloaded Aurify.exe to upgrade this legacy release.");
        using (Mutex mutex = RuntimeMutex(options))
        {
            if (!Take(mutex, 0)) return 0;
            try
            {
                foreach (string relative in Payload)
                {
                    if (FileDigest(Path.Combine(directory, relative)) != release.Hashes[relative])
                        throw new InvalidOperationException("Aurify's installed files changed. Its uploader was not started. Run the original download or uninstall Aurify to recover.");
                }
                return AurifyClient.NativeHost.Run(options.Root, options.Background);
            }
            finally { mutex.ReleaseMutex(); }
        }
#endif
    }

    private static void RequestSetup(Options options)
    {
        try { PipeCommand(options, "setup\n", 10000); }
        catch (Exception)
        {
            throw new InvalidOperationException("The running Aurify uploader could not open setup yet. Wait a moment and run Aurify again. It has not been stopped.");
        }
    }

    private static void PipeCommand(Options options, string command, int timeout)
    {
        PipeExchange("Aurify-" + Key(options), command, timeout);
    }

    private static void LegacyPipeCommand(Options options, string command, int timeout)
    {
        PipeExchange("Aurify-" + LegacyKey(options), command, timeout);
    }

    private static void PipeExchange(string pipeName, string command, int timeout)
    {
        using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            pipe.Connect(5000);
            byte[] bytes = Encoding.ASCII.GetBytes(command);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            byte[] response = new byte[64];
            int count = 0;
            Stopwatch elapsed = Stopwatch.StartNew();
            while (count < response.Length)
            {
                IAsyncResult read = pipe.BeginRead(response, count, response.Length - count, null, null);
                int length;
                try
                {
                    if (!read.AsyncWaitHandle.WaitOne(Math.Max(0, timeout - (int)elapsed.ElapsedMilliseconds)))
                        throw new IOException("Control request timed out");
                    length = pipe.EndRead(read);
                }
                finally { read.AsyncWaitHandle.Close(); }
                if (length == 0) break;
                count += length;
                if (Encoding.ASCII.GetString(response, 0, count).Trim() == "ok") return;
            }
            throw new IOException("Control request was not acknowledged");
        }
    }

    private static string ControlToken(Options options)
    {
        string controlPath = Path.Combine(options.Root, "config.control.json");
        NoLinks(controlPath);
        if (new FileInfo(controlPath).Length > 1024) throw new IOException("Control authorization is invalid");
        Control control = ReadJson<Control>(controlPath);
        if (control == null || control.token == null || control.token.Length != 43)
            throw new IOException("Control authorization is unavailable");
        foreach (char c in control.token)
            if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-' && c != '_')
                throw new IOException("Control authorization is invalid");
        return control.token;
    }

    private static void WaitForStop(Mutex mutex)
    {
        using (mutex)
        {
            if (!Take(mutex, 10000)) throw new IOException("Uploader is still draining");
            mutex.ReleaseMutex();
        }
    }

    private static void StopHost(Options options)
    {
        bool current = RuntimeRunning(options);
        bool legacy = LegacyRuntimeRunning(options);
        if (!current && !legacy) return;
        try
        {
            // Published native releases before 2.0.2 used the installation-root key.
            // Retire them through their authenticated pipe before claiming the user-wide owner.
            if (legacy)
            {
                LegacyPipeCommand(options, "shutdown " + ControlToken(options) + "\n", 75000);
                WaitForStop(LegacyRuntimeMutex(options));
            }
            if (current)
            {
                // The pipe DACL admits only this Windows SID, so a handoff also works
                // when the active instance belongs to another older installation root.
                PipeCommand(options, "handoff\n", 75000);
                WaitForStop(RuntimeMutex(options));
            }
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Aurify is still finishing an upload or has not accepted shutdown. No running executable was overwritten and no process was forcibly stopped. Wait a minute and retry.");
        }
    }

    private static object Shortcut(object shell, string path)
    {
        return shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
    }

    private static string Property(object shortcut, string name)
    {
        return Convert.ToString(shortcut.GetType().InvokeMember(name, BindingFlags.GetProperty, null, shortcut, null), CultureInfo.InvariantCulture);
    }

    private static void SetProperty(object shortcut, string name, object value)
    {
        shortcut.GetType().InvokeMember(name, BindingFlags.SetProperty, null, shortcut, new object[] { value });
    }

    private enum ShortcutKind { Startup, Uninstall, Desktop }

    private static string ShortcutArguments(ShortcutKind kind, string locations)
    {
        return kind == ShortcutKind.Desktop ? locations :
            Arguments(kind == ShortcutKind.Uninstall ? "--uninstall" : "--background") + " " + locations;
    }

    private static bool OwnedShortcut(object shortcut, Installation state, ShortcutKind kind)
    {
        if (state == null || Property(shortcut, "Description") != "Aurify installation " + state.Id) return false;
        string locations = Arguments("--install-root", state.Root, "--startup-dir", state.Startup);
        string arguments = Property(shortcut, "Arguments");
        // Exact marked legacy links remain recognizable through interrupted migration
        // and rollback. Unmarked links (including manual desktop links) are never adopted.
        if (arguments != ShortcutArguments(kind, locations) &&
            (state.Desktop == null || arguments != ShortcutArguments(kind, locations + " " + Arguments("--desktop-dir", state.Desktop)))) return false;
        string target = Property(shortcut, "TargetPath");
        foreach (string release in state.Releases)
            if (Same(target, Path.Combine(ReleasePath(state.Root, release), "Aurify.exe"))) return true;
        return false;
    }

    private static void CheckShortcut(object shell, string path, Installation state, ShortcutKind kind)
    {
        NoLinks(path);
        if (Directory.Exists(path))
            throw new InvalidOperationException("An unrelated folder already uses Aurify's shortcut name. Aurify has left it untouched.");
        if (!File.Exists(path)) return;
        object shortcut = Shortcut(shell, path);
        try
        {
            if (!OwnedShortcut(shortcut, state, kind))
                throw new InvalidOperationException("An unrelated shortcut already uses Aurify's shortcut name. Aurify has left it untouched; move or rename it before retrying.");
        }
        finally { Marshal.FinalReleaseComObject(shortcut); }
    }

    private static void PreflightShortcuts(Options options, Installation state)
    {
        NoLinks(options.Startup);
        NoLinks(options.Desktop);
        if (File.Exists(options.Startup) || File.Exists(options.Desktop))
            throw new InvalidOperationException("Aurify's shortcut folder is occupied by an unrelated file. It was left untouched.");
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
        try
        {
            CheckShortcut(shell, Path.Combine(options.Startup, "Aurify.lnk"), state, ShortcutKind.Startup);
            CheckShortcut(shell, Path.Combine(options.Root, "Uninstall Aurify.lnk"), state, ShortcutKind.Uninstall);
            CheckShortcut(shell, Path.Combine(options.Desktop, "Aurify.lnk"), state, ShortcutKind.Desktop);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private static void SaveShortcut(object shell, string path, Installation state, ShortcutKind kind, string executable, string locations)
    {
        CheckShortcut(shell, path, state, kind);
        object shortcut = Shortcut(shell, path);
        try
        {
            SetProperty(shortcut, "TargetPath", executable);
            SetProperty(shortcut, "Arguments", ShortcutArguments(kind, locations));
            SetProperty(shortcut, "WorkingDirectory", Path.GetDirectoryName(executable));
            SetProperty(shortcut, "Description", "Aurify installation " + state.Id);
            SetProperty(shortcut, "IconLocation", executable + ",0");
            SetProperty(shortcut, "WindowStyle", kind == ShortcutKind.Desktop ? 1 : 7);
            shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally { Marshal.FinalReleaseComObject(shortcut); }
    }

    private static void RegisterShortcuts(Options options, Installation state)
    {
        PreflightShortcuts(options, state);
        string executable = Path.Combine(ReleasePath(options.Root, state.Current), "Aurify.exe");
        string locations = TargetLocationArguments(options, executable);
        Directory.CreateDirectory(options.Startup);
        Directory.CreateDirectory(options.Desktop);
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
        try
        {
            SaveShortcut(shell, Path.Combine(options.Startup, "Aurify.lnk"), state, ShortcutKind.Startup, executable, locations);
            SaveShortcut(shell, Path.Combine(options.Root, "Uninstall Aurify.lnk"), state, ShortcutKind.Uninstall, executable, locations);
            SaveShortcut(shell, Path.Combine(options.Desktop, "Aurify.lnk"), state, ShortcutKind.Desktop, executable, locations);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private static int BeginUninstall(Options options)
    {
        Installation state = LoadState(options.Root, true);
        BindInstallation(options, state);
        if (MessageBox.Show("Remove Aurify's installed app and its automatic startup for this Windows user?\n\nThe uploader will finish its current work and stop. Your pairing, settings, checkpoints, game addons, and any unrecognized or changed files will be kept.\n\nInstallation: " + options.Root,
            "Uninstall Aurify", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return 0;
        using (Lock(options))
        {
            state = LoadState(options.Root, true);
            BindInstallation(options, state);
            RestoreRecovery(options, state);
            string token = state.RemoveToken ?? Guid.NewGuid().ToString("N");
            // Reuse a failed removal's verified worker and recovery files on retry.
            string temporary = Path.Combine(Path.GetDirectoryName(options.Root), ".aurify-remove-" + token);
            NoLinks(temporary);
            Directory.CreateDirectory(temporary);
            string worker = Path.Combine(temporary, "Aurify.exe");
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Aurify.Uninstall.exe"))
            {
                if (resource == null) throw new InvalidOperationException("Aurify's uninstall worker is missing. Download Aurify.exe again and use its --uninstall action.");
                using (SHA256 hash = SHA256.Create())
                {
                    string expected = BitConverter.ToString(hash.ComputeHash(resource)).Replace("-", "").ToLowerInvariant();
                    if (!File.Exists(worker))
                    {
                        resource.Position = 0;
                        using (FileStream output = new FileStream(worker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            resource.CopyTo(output);
                    }
                    if (FileDigest(worker) != expected) throw new InvalidOperationException("Aurify's uninstall worker changed. No installation files were removed.");
                }
            }
            state.RemoveToken = token;
            state.RemoveWorker = worker;
            SaveState(options, state);
                using (Process current = Process.GetCurrentProcess())
                using (Process helper = Start(worker, Arguments("--remove-token", token, "--parent-pid", current.Id.ToString(CultureInfo.InvariantCulture),
                    "--parent-started", current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)) + " " + LocationArguments(options))) { }
        }
        return 0;
    }

    private static void RemoveShortcut(object shell, string path, Installation state, ShortcutKind kind)
    {
        if (!File.Exists(path)) return;
        NoLinks(path);
        object shortcut = Shortcut(shell, path);
        try { if (OwnedShortcut(shortcut, state, kind)) File.Delete(path); }
        finally { Marshal.FinalReleaseComObject(shortcut); }
    }

    private static void RemoveEmpty(string directory)
    {
        NoLinks(directory);
        if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0) Directory.Delete(directory);
    }

    private static string RecoveryPath(Options options, Installation state)
    {
        Guid token;
        if (!Guid.TryParseExact(state.RemoveToken, "N", out token))
            throw new InvalidOperationException("Aurify's pending removal record is invalid.");
        string directory = Path.Combine(Path.GetDirectoryName(options.Root), ".aurify-remove-" + state.RemoveToken);
        if (state.RemoveWorker == null || !Same(state.RemoveWorker, Path.Combine(directory, "Aurify.exe")))
            throw new InvalidOperationException("Aurify's pending removal worker is not recognized.");
        string recovery = Path.Combine(directory, "recovery");
        NoLinks(recovery);
        return recovery;
    }

    private static void CopyRecoveryFile(string source, string destination, string digest)
    {
        if (FileDigest(source) != digest) throw new InvalidOperationException("Aurify's recovery source changed. No changed file was overwritten.");
        NoLinks(destination);
        if (!File.Exists(destination))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temporary, false);
                if (FileDigest(temporary) != digest) throw new InvalidOperationException("Aurify's recovery copy could not be verified.");
                File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (FileDigest(destination) != digest) throw new InvalidOperationException("Aurify's recovery destination changed. It was left untouched.");
    }

    private static string PrepareRecovery(Options options, Installation state)
    {
        string recovery = RecoveryPath(options, state);
        Directory.CreateDirectory(recovery);
        foreach (string version in state.Releases)
        {
            string directory = ReleasePath(options.Root, version);
            if (!Directory.Exists(directory)) continue;
            Release release = ReadRelease(directory, version);
            string manifest = Path.Combine(directory, ManifestName);
            CopyRecoveryFile(manifest, Path.Combine(recovery, version, ManifestName), FileDigest(manifest));
            if (version == state.Current)
                CopyRecoveryFile(Path.Combine(directory, "Aurify.exe"), Path.Combine(recovery, version, "Aurify.exe"), release.Hashes["Aurify.exe"]);
        }
        string launcher = Path.Combine(recovery, state.Current, "Aurify.exe");
        Release current = ReadRelease(Path.Combine(recovery, state.Current), state.Current);
        if (FileDigest(launcher) != current.Hashes["Aurify.exe"])
            throw new InvalidOperationException("Aurify could not verify its recovery launcher.");
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
        try
        {
            string[] shortcuts = { Path.Combine(options.Startup, "Aurify.lnk"), Path.Combine(options.Root, "Uninstall Aurify.lnk"), Path.Combine(options.Desktop, "Aurify.lnk") };
            string[] backups = { "startup.lnk", "uninstall.lnk", "desktop.lnk" };
            for (int i = 0; i < shortcuts.Length; i++)
            {
                if (!File.Exists(shortcuts[i])) continue;
                NoLinks(shortcuts[i]);
                object original = Shortcut(shell, shortcuts[i]);
                try
                {
                    if (OwnedShortcut(original, state, (ShortcutKind)i))
                        CopyRecoveryFile(shortcuts[i], Path.Combine(recovery, backups[i]), FileDigest(shortcuts[i]));
                }
                finally { Marshal.FinalReleaseComObject(original); }
            }
            string path = Path.Combine(recovery, "Retry Uninstall Aurify.lnk");
            NoLinks(path);
            object shortcut = Shortcut(shell, path);
            try
            {
                string arguments = Arguments("--uninstall") + " " + TargetLocationArguments(options, launcher);
                if (File.Exists(path) && (!Same(Property(shortcut, "TargetPath"), launcher) ||
                    Property(shortcut, "Arguments") != arguments))
                    throw new InvalidOperationException("Aurify's recovery shortcut changed. It was left untouched.");
                SetProperty(shortcut, "TargetPath", launcher);
                SetProperty(shortcut, "Arguments", arguments);
                SetProperty(shortcut, "WorkingDirectory", Path.GetDirectoryName(launcher));
                SetProperty(shortcut, "WindowStyle", 7);
                shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally { Marshal.FinalReleaseComObject(shortcut); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        string ready = Path.Combine(recovery, "ready");
        NoLinks(ready);
        if (!File.Exists(ready)) File.WriteAllText(ready, state.Current, new UTF8Encoding(false));
        return recovery;
    }

    private static void RestoreRecovery(Options options, Installation state)
    {
        if (state.RemoveToken == null) return;
        string recovery = RecoveryPath(options, state);
        if (!File.Exists(Path.Combine(recovery, "ready"))) return;
        foreach (string version in state.Releases)
        {
            string backup = Path.Combine(recovery, version);
            if (!Directory.Exists(backup)) continue;
            Release release = ReadRelease(backup, version);
            string directory = ReleasePath(options.Root, version);
            string manifest = Path.Combine(directory, ManifestName);
            if (!File.Exists(manifest))
                CopyRecoveryFile(Path.Combine(backup, ManifestName), manifest, FileDigest(Path.Combine(backup, ManifestName)));
            if (version == state.Current && !File.Exists(Path.Combine(directory, "Aurify.exe")))
                CopyRecoveryFile(Path.Combine(backup, "Aurify.exe"), Path.Combine(directory, "Aurify.exe"), release.Hashes["Aurify.exe"]);
        }
        string[] sources = { "startup.lnk", "uninstall.lnk", "desktop.lnk" };
        string[] targets = { Path.Combine(options.Startup, "Aurify.lnk"), Path.Combine(options.Root, "Uninstall Aurify.lnk"), Path.Combine(options.Desktop, "Aurify.lnk") };
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
        try
        {
            for (int i = 0; i < sources.Length; i++)
            {
                string source = Path.Combine(recovery, sources[i]);
                if (!File.Exists(source) || File.Exists(targets[i])) continue;
                NoLinks(source);
                object shortcut = Shortcut(shell, source);
                try
                {
                    if (OwnedShortcut(shortcut, state, (ShortcutKind)i))
                        CopyRecoveryFile(source, targets[i], FileDigest(source));
                }
                finally { Marshal.FinalReleaseComObject(shortcut); }
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private static int RemoveInstallation(Options options)
    {
        if (options.ParentPid <= 0 || options.ParentStarted <= 0) throw new InvalidOperationException("An uninstall worker must be started by the Aurify uninstall dialog.");
        try
        {
            using (Process parent = Process.GetProcessById(options.ParentPid))
                if (parent.StartTime.ToUniversalTime().Ticks == options.ParentStarted && !parent.WaitForExit(15000))
                    throw new InvalidOperationException("The Aurify uninstall dialog is still closing. Try uninstalling again.");
        }
        catch (ArgumentException) { }
        using (Lock(options))
        {
            Installation state = LoadState(options.Root, true);
            BindInstallation(options, state);
            if (state.RemoveToken != options.RemoveToken || !Same(state.RemoveWorker, Executable) || !Same(state.Startup, options.Startup))
                throw new InvalidOperationException("The uninstall authorization no longer matches this installation. No files were removed.");
            string recovery = null;
            try
            {
                StopHost(options);
                recovery = PrepareRecovery(options, state);
                // Retain every manifest and the active GUI until all risky children are gone.
                foreach (string version in state.Releases)
                {
                    string directory = ReleasePath(options.Root, version);
                    if (!Directory.Exists(directory)) continue;
                    Release release = ReadRelease(directory, version);
                    foreach (string relative in release.Hashes.Keys)
                    {
                        if (relative == "Aurify.exe" && version == state.Current) continue;
                        string path = Path.Combine(directory, relative);
                        if (File.Exists(path) && FileDigest(path) == release.Hashes[relative]) File.Delete(path);
                    }
                    RemoveEmpty(Path.Combine(directory, "addon", "Aurify"));
                    RemoveEmpty(Path.Combine(directory, "addon"));
                    RemoveEmpty(Path.Combine(directory, "licenses"));
                }
                // Final cleanup can be rolled back using the verified external GUI and records.
                foreach (string version in state.Releases)
                {
                    string directory = ReleasePath(options.Root, version);
                    if (!Directory.Exists(directory)) continue;
                    Release release = ReadRelease(directory, version);
                    if (version == state.Current)
                    {
                        string launcher = Path.Combine(directory, "Aurify.exe");
                        if (File.Exists(launcher) && FileDigest(launcher) == release.Hashes["Aurify.exe"]) File.Delete(launcher);
                    }
                    File.Delete(Path.Combine(directory, ManifestName));
                    RemoveEmpty(directory);
                }
                RemoveEmpty(Path.Combine(options.Root, "App", "releases"));
                RemoveEmpty(Path.Combine(options.Root, "App"));
                object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                try
                {
                    RemoveShortcut(shell, Path.Combine(options.Startup, "Aurify.lnk"), state, ShortcutKind.Startup);
                    RemoveShortcut(shell, Path.Combine(options.Root, "Uninstall Aurify.lnk"), state, ShortcutKind.Uninstall);
                    RemoveShortcut(shell, Path.Combine(options.Desktop, "Aurify.lnk"), state, ShortcutKind.Desktop);
                }
                finally { Marshal.FinalReleaseComObject(shell); }
                // Commit last: a failure anywhere above still has its pending state.
                File.Delete(Path.Combine(options.Root, StateName));
                Log(options, "uninstalled", "local-data-kept");
            }
            catch (Exception error)
            {
                try { RestoreRecovery(options, state); }
                catch (Exception) { /* The external GUI shortcut and pending state remain available. */ }
                string retry = recovery == null ? Path.Combine(options.Root, "Uninstall Aurify.lnk") :
                    Path.Combine(recovery, "Retry Uninstall Aurify.lnk");
                throw new InvalidOperationException("Aurify removal did not finish. Close any program locking its files and retry the Uninstall shortcut. If that shortcut is unavailable, open:\n\n" + retry +
                    "\n\nSettings and changed or unrecognized files were kept.", error);
            }
            try
            {
                foreach (string version in state.Releases)
                {
                    string directory = Path.Combine(recovery, version);
                    if (!Directory.Exists(directory)) continue;
                    Release release = ReadRelease(directory, version);
                    string launcher = Path.Combine(directory, "Aurify.exe");
                    if (version == state.Current && File.Exists(launcher) && FileDigest(launcher) == release.Hashes["Aurify.exe"]) File.Delete(launcher);
                    File.Delete(Path.Combine(directory, ManifestName));
                    RemoveEmpty(directory);
                }
                foreach (string name in new[] { "startup.lnk", "uninstall.lnk", "desktop.lnk", "Retry Uninstall Aurify.lnk", "ready" })
                {
                    string path = Path.Combine(recovery, name);
                    NoLinks(path);
                    File.Delete(path);
                }
                RemoveEmpty(recovery);
            }
            catch (Exception)
            {
                Log(options, "uninstalled", "recovery-files-kept");
                MessageBox.Show("Aurify's installation was removed, but temporary recovery files could not all be deleted. After closing Aurify, you may remove this recovery folder:\n\n" +
                    recovery + "\n\nYour settings and changed or unrecognized installation files were kept.", Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        // This small, inert helper cannot delete its own mapped executable on Windows.
        // It contains no payload or credentials and can be removed after it exits.
        return 0;
    }

    private static string FailureCode(Exception error)
    {
        // Never log exception messages, paths, arguments, or persisted values.
        // Native error codes and our own method names identify startup failures
        // without disclosing member credentials or account/game data.
        StringBuilder code = new StringBuilder(error.GetType().Name);
        code.Append(":0x").Append(error.HResult.ToString("x8", CultureInfo.InvariantCulture));
        System.ComponentModel.Win32Exception native = error as System.ComponentModel.Win32Exception;
        if (native != null) code.Append(":win32=").Append(native.NativeErrorCode.ToString(CultureInfo.InvariantCulture));
        StackFrame[] frames = new StackTrace(error, false).GetFrames();
        if (frames != null)
        foreach (StackFrame frame in frames)
        {
            MethodBase method = frame.GetMethod();
            string type = method == null || method.DeclaringType == null ? null : method.DeclaringType.FullName;
            if (type == null || !(type == "Aurify" || type.StartsWith("AurifyClient.", StringComparison.Ordinal))) continue;
            code.Append(" ").Append(type).Append(".").Append(method.Name).Append("@").Append(frame.GetILOffset().ToString(CultureInfo.InvariantCulture));
            if (code.Length > 1024) break;
        }
        return code.ToString();
    }

    private static void Log(Options options, string action, string code)
    {
        if (options == null || !Directory.Exists(options.Root)) return;
        try
        {
            string path = Path.Combine(options.Root, "launcher.log");
            NoLinks(path);
            if (File.Exists(path) && new FileInfo(path).Length >= 65536) return;
            File.AppendAllText(path, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + action + " " + code + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }
}
