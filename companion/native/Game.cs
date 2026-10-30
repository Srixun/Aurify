using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AurifyClient
{
    internal static class GameTools
    {
        internal const int TocLimit = 128 * 1024;
        private static readonly string[] ExecutableNames = { "WoW.exe", "WoW-64.exe", "WowB.exe" };
        private static readonly string[] GameNames = { "WoW Forever", "WoWForever", "Warcraft Forever", "World of Warcraft Forever", "Forever", "World of Warcraft", "WoW" };
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static Dictionary<string, string> ParseToc(string source)
        {
            if (source == null || Utf8.GetByteCount(source) > TocLimit)
                throw new InvalidDataException("Addon TOC is invalid.");
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in source.Split('\n'))
            {
                Match match = Regex.Match(line.TrimEnd('\r'), @"^##\s*([^:]{1,80}):\s*(.*?)\s*$");
                if (match.Success) metadata[match.Groups[1].Value.Trim().ToLowerInvariant()] = match.Groups[2].Value;
            }
            return metadata;
        }

        internal static Dictionary<string, string> ReadToc(string file, bool strictIdentity = true)
        {
            byte[] bytes = NativeGamePaths.ReadFile(file, TocLimit);
            string source = Utf8.GetString(bytes);
            if (source.Length > 0 && source[0] == '\ufeff') source = source.Substring(1);
            if (strictIdentity && Regex.Matches(source, @"^##\s*(?:title|interface|version)\s*:", RegexOptions.IgnoreCase | RegexOptions.Multiline).Count != 3)
                throw new InvalidDataException("Ambiguous addon TOC identity.");
            return ParseToc(source);
        }

        internal static string Field(Dictionary<string, string> metadata, string name)
        {
            string value;
            return metadata.TryGetValue(name, out value) ? value : "";
        }

        internal static bool SupportsForever(Dictionary<string, string> metadata)
        {
            return Array.IndexOf(Regex.Split(Field(metadata, "interface"), @"[\s,]+"), "16001") >= 0;
        }

        internal static string PlainTitle(string title)
        {
            return Regex.Replace(Regex.Replace(title ?? "", @"\|c[0-9a-f]{8}|\|r", "", RegexOptions.IgnoreCase), @"\s+", " ").Trim().ToLowerInvariant();
        }

        internal static AddonInfo Ready(Dictionary<string, string> metadata)
        {
            string version = Field(metadata, "version").Trim();
            return new AddonInfo { Status = "ready", Interface = 16001, Version = version.Substring(0, Math.Min(32, version.Length)) };
        }

        internal static GameInstallation Verify(string selected, bool exact = false)
        {
            if (String.IsNullOrWhiteSpace(selected) || selected.Length > 1024)
                throw new InvalidDataException("Choose the WoW Forever game folder.");
            string root;
            try { root = NativeGamePaths.LocalPath(selected.Trim()); }
            catch { throw new InvalidDataException("Choose a local WoW Forever game folder without linked folders."); }
            bool unavailable = false;
            HashSet<string> running = RunningPaths(ref unavailable);
            foreach (string candidate in SelectedRoots(root, exact))
            {
                try { return Inspect(candidate, running); }
                catch (Exception error)
                {
                    if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException || error is Win32Exception || error is System.Security.SecurityException)) throw;
                }
            }
            throw new InvalidDataException("The selected client could not be verified as WoW Forever. Choose the folder containing the supported client executable.");
        }

        internal static DiscoveryResult Discover()
        {
            var result = new DiscoveryResult();
            bool unavailable = false;
            HashSet<string> running = RunningPaths(ref unavailable);
            var seeds = new List<string>();
            var seenSeeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string image in running) AddSeed(seeds, seenSeeds, Path.GetDirectoryName(image));
            RegistryCandidates(seeds, seenSeeds, ref unavailable);
            ShortcutCandidates(seeds, seenSeeds, ref unavailable);
            var roots = new List<string> { "C:\\", "D:\\", "E:\\", "C:\\Games", "D:\\Games", "E:\\Games", Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") };
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!String.IsNullOrEmpty(home))
            {
                roots.Add(Path.Combine(home, "Games"));
                roots.Add(Path.Combine(home, "Downloads"));
                roots.Add(Path.Combine(home, "Desktop"));
            }
            foreach (string root in roots)
                if (!String.IsNullOrEmpty(root))
                    foreach (string name in GameNames) AddSeed(seeds, seenSeeds, Path.Combine(root, name));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string seed in seeds)
            {
                foreach (string root in SelectedRoots(seed, false))
                {
                    if (seen.Count >= 304) break;
                    if (!seen.Add(root)) continue;
                    try { result.Installations.Add(Inspect(root, running)); }
                    catch (Exception error)
                    {
                        if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException || error is Win32Exception || error is System.Security.SecurityException)) throw;
                    }
                }
            }
            result.ProbeUnavailable = unavailable;
            return result;
        }

        internal static bool IsRunning(GameInstallation installation)
        {
            if (installation == null || String.IsNullOrEmpty(installation.GameDir)) return false;
            bool unavailable = false;
            HashSet<string> paths = RunningPaths(ref unavailable);
            foreach (string name in ExecutableNames)
                if (paths.Contains(Path.Combine(installation.GameDir, name))) return true;
            return unavailable;
        }

        private static IEnumerable<string> SelectedRoots(string root, bool exact)
        {
            if (exact) return new[] { root };
            var roots = new List<string>();
            string leaf = Path.GetFileName(root);
            if (String.Equals(leaf, "AddOns", StringComparison.OrdinalIgnoreCase))
            {
                string parent = Path.GetDirectoryName(root);
                if (parent != null && Path.GetDirectoryName(parent) != null) roots.Add(Path.GetDirectoryName(parent));
            }
            else if (String.Equals(leaf, "Interface", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(root) != null)
                roots.Add(Path.GetDirectoryName(root));
            roots.Add(root);
            roots.Add(Path.Combine(root, "_forever_"));
            roots.Add(Path.Combine(root, "_classic_beta_"));
            return roots;
        }

        private static GameInstallation Inspect(string root, HashSet<string> running)
        {
            root = NativeGamePaths.LocalPath(root);
            using (NativeGamePaths.Guard guard = NativeGamePaths.LockDirectory(root))
            {
                string executable = null;
                string verified = null;
                bool knownVersion = false;
                bool active = false;
                foreach (string name in ExecutableNames)
                {
                    string file = Path.Combine(root, name);
                    try
                    {
                        using (FileStream stream = NativeGamePaths.OpenRead(file))
                        {
                            if (executable == null) executable = file;
                            active |= running.Contains(file);
                            FileVersionInfo version = FileVersionInfo.GetVersionInfo(file);
                            knownVersion |= version.FileMajorPart > 0;
                            // Blizzard packs the fixed numeric fields differently from
                            // the published client version (1.60.1.70450 -> 160.1.7045.0).
                            if (Regex.IsMatch(version.FileVersion ?? "", @"\A1\.60\.1(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant)) verified = file;
                        }
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                }
                if (executable == null) throw new InvalidDataException("The selected folder has no WoW client executable.");
                var installation = new GameInstallation { GameDir = root, Executable = verified ?? executable, Running = active };
                installation.Addons["professions"] = InspectAddon(root, "professions", "ProfessionMaster", "ProfessionMaster_Camelot.toc", "forever", false);
                installation.Addons["overlord"] = InspectAddon(root, "overlord", "Overlord", "Overlord.toc", @"overlord\s+forever", false);
                installation.Addons["activity"] = InspectAddon(root, null, "Aurify", "Aurify.toc", "^aurify$", true);
                // The managed helper is never client identity evidence. A known different
                // client version cannot be overridden by addon metadata.
                if (verified == null && (knownVersion || (installation.Addons["professions"].Status != "ready" && installation.Addons["overlord"].Status != "ready")))
                    throw new InvalidDataException("The selected client could not be verified as WoW Forever.");
                return installation;
            }
        }

        private static AddonInfo InspectAddon(string root, string key, string directory, string toc, string titlePattern, bool managed)
        {
            try
            {
                var metadata = ReadToc(Path.Combine(root, "Interface", "AddOns", directory, toc));
                if (!SupportsForever(metadata) || !Regex.IsMatch(Field(metadata, "title"), titlePattern, RegexOptions.IgnoreCase) || String.IsNullOrWhiteSpace(Field(metadata, "version")) || (key != null && !AddonVersions.IsSupported(key, Field(metadata, "version"))) || (managed && Field(metadata, "x-aurify-managed") != "1"))
                    return new AddonInfo { Status = "incompatible" };
                return Ready(metadata);
            }
            catch (FileNotFoundException) { return new AddonInfo(); }
            catch (DirectoryNotFoundException) { return new AddonInfo(); }
            catch { return new AddonInfo { Status = "unreadable" }; }
        }

        private static HashSet<string> RunningPaths(ref bool unavailable)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int inspected = 0;
            foreach (string name in ExecutableNames)
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)); }
                catch { unavailable = true; continue; }
                foreach (Process process in processes)
                {
                    using (process)
                    {
                        if (inspected >= 256) { unavailable = true; continue; }
                        inspected++;
                        try { paths.Add(NativeGamePaths.LocalPath(process.MainModule.FileName)); }
                        catch { unavailable = true; }
                    }
                }
            }
            return paths;
        }

        private static void AddSeed(List<string> seeds, HashSet<string> seen, string path)
        {
            if (seeds.Count >= 96 || String.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = NativeGamePaths.LocalPath(path.Trim());
                if (seen.Add(path)) seeds.Add(path);
            }
            catch { }
        }

        private static bool GameLabel(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 256) return false;
            return Regex.IsMatch(value, @"(?:wow|warcraft|forever)", RegexOptions.IgnoreCase);
        }

        private static void ExecutableSeed(List<string> seeds, HashSet<string> seen, string target)
        {
            if (String.IsNullOrEmpty(target) || target.Length > 1024) return;
            target = target.Trim();
            if (target.StartsWith("\"", StringComparison.Ordinal))
            {
                int quote = target.IndexOf('"', 1);
                if (quote < 0) return;
                target = target.Substring(1, quote - 1);
            }
            else
            {
                int comma = target.LastIndexOf(',');
                int icon;
                if (comma > 0 && Int32.TryParse(target.Substring(comma + 1), out icon)) target = target.Substring(0, comma);
            }
            try
            {
                target = NativeGamePaths.LocalPath(target);
                foreach (string name in ExecutableNames)
                    if (String.Equals(Path.GetFileName(target), name, StringComparison.OrdinalIgnoreCase))
                        AddSeed(seeds, seen, Path.GetDirectoryName(target));
            }
            catch { }
        }

        private static void RegistryCandidates(List<string> seeds, HashSet<string> seen, ref bool unavailable)
        {
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                        {
                            foreach (string name in ExecutableNames)
                                using (RegistryKey key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + name))
                                    if (key != null) ExecutableSeed(seeds, seen, key.GetValue(null) as string);
                            foreach (string name in new[] { @"Software\Blizzard Entertainment\World of Warcraft", @"Software\WoW Forever", @"Software\World of Warcraft Forever" })
                                using (RegistryKey key = root.OpenSubKey(name))
                                    if (key != null)
                                        foreach (string field in new[] { "InstallPath", "InstallLocation", "GamePath" })
                                        {
                                            string value = key.GetValue(field) as string;
                                            AddSeed(seeds, seen, value);
                                            ExecutableSeed(seeds, seen, value);
                                        }
                            using (RegistryKey uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
                            {
                                if (uninstall == null) continue;
                                foreach (string subkey in RegistrySubKeys(uninstall, 256))
                                {
                                    if (seeds.Count >= 96) break;
                                    using (RegistryKey key = uninstall.OpenSubKey(subkey))
                                    {
                                        if (key == null || !GameLabel(key.GetValue("DisplayName") as string)) continue;
                                        AddSeed(seeds, seen, key.GetValue("InstallLocation") as string);
                                        ExecutableSeed(seeds, seen, key.GetValue("DisplayIcon") as string);
                                    }
                                }
                            }
                        }
                    }
                    catch { unavailable = true; }
                }
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegEnumKeyEx(SafeRegistryHandle key, uint index, StringBuilder name, ref uint nameLength, IntPtr reserved, IntPtr keyClass, IntPtr classLength, IntPtr lastWriteTime);

        private static IEnumerable<string> RegistrySubKeys(RegistryKey key, int maximum)
        {
            for (uint index = 0; index < maximum; index++)
            {
                var name = new StringBuilder(1024);
                uint length = (uint)name.Capacity;
                int result = RegEnumKeyEx(key.Handle, index, name, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (result == 259) yield break;
                if (result != 0) throw new Win32Exception(result);
                yield return name.ToString(0, (int)length);
            }
        }

        private static void ShortcutCandidates(List<string> seeds, HashSet<string> seen, ref bool unavailable)
        {
            foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
            {
                string root = Environment.GetFolderPath(folder);
                if (String.IsNullOrEmpty(root)) continue;
                try
                {
                    ReadShortcuts(root, seeds, seen);
                    // Only named game folders, one level deep; no general recursive scans.
                    foreach (string name in GameNames)
                    {
                        string child = Path.Combine(root, name);
                        if (Directory.Exists(child)) ReadShortcuts(child, seeds, seen);
                    }
                }
                catch (DirectoryNotFoundException) { }
                catch { unavailable = true; }
            }
        }

        private static void ReadShortcuts(string root, List<string> seeds, HashSet<string> seen)
        {
            using (NativeGamePaths.Guard guard = NativeGamePaths.LockDirectory(root))
            {
                int count = 0;
                foreach (string path in Directory.EnumerateFiles(root, "*.lnk", SearchOption.TopDirectoryOnly))
                {
                    if (++count > 128) break;
                    if (!GameLabel(Path.GetFileNameWithoutExtension(path))) continue;
                    object shortcut = null;
                    try
                    {
                        using (FileStream file = NativeGamePaths.OpenRead(path))
                        {
                            if (file.Length > 256 * 1024) continue;
                            shortcut = new ShellLink();
                            ((IPersistFile)shortcut).Load(path, 0);
                            var target = new StringBuilder(1025);
                            // Raw path only: never Resolve a shortcut, read its arguments,
                            // contact a network location, or launch it.
                            ((IShellLinkW)shortcut).GetPath(target, target.Capacity, IntPtr.Zero, 4);
                            ExecutableSeed(seeds, seen, target.ToString());
                        }
                    }
                    catch { }
                    finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); }
                }
            }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int capacity, IntPtr findData, uint flags);
        }
    }

    // Windows handles pin every ancestry component while mutating a destination.
    // Opening the reparse point itself makes the link check independent of traversal.
    internal static class NativeGamePaths
    {
        private const uint ReadAttributes = 0x80;
        private const uint OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000;
        private const uint OpenReparsePoint = 0x00200000;

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation;
            public System.Runtime.InteropServices.ComTypes.FILETIME Access;
            public System.Runtime.InteropServices.ComTypes.FILETIME Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateDirectory(string path, IntPtr security);

        internal static string LocalPath(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length > 1024 || !Regex.IsMatch(path, @"^[a-zA-Z]:[\\/]"))
                throw new IOException("A local drive path is required.");
            if (path.IndexOf(':', 2) >= 0 || path.IndexOfAny(new[] { '\0', '*', '?', '"', '<', '>', '|' }) >= 0)
                throw new IOException("Unsafe local path.");
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            foreach (string component in full.Substring(root.Length).Split(Path.DirectorySeparatorChar))
                if (component.Length > 0 && (component.EndsWith(".", StringComparison.Ordinal) || component.EndsWith(" ", StringComparison.Ordinal)))
                    throw new IOException("Ambiguous local path.");
            return full.Length == root.Length ? full : full.TrimEnd(Path.DirectorySeparatorChar);
        }

        internal sealed class Guard : IDisposable
        {
            private readonly List<SafeFileHandle> handles = new List<SafeFileHandle>();
            internal string Identity;
            internal Guard(string path)
            {
                try
                {
                    path = LocalPath(path);
                    string root = Path.GetPathRoot(path);
                    string current = root;
                    Add(current);
                    foreach (string component in path.Substring(root.Length).Split(Path.DirectorySeparatorChar))
                    {
                        if (component.Length == 0) continue;
                        current = Path.Combine(current, component);
                        Add(current);
                    }
                }
                catch { Dispose(); throw; }
            }
            private void Add(string path)
            {
                // No share-write or share-delete: another process cannot replace this
                // directory with a junction during child creation or publication.
                SafeFileHandle handle = CreateFile(path, ReadAttributes, 1, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    ThrowPathError(error, path);
                }
                handles.Add(handle);
                FileInformation info;
                if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.Attributes & (uint)FileAttributes.Directory) == 0)
                    throw new IOException("Unsafe linked or non-directory destination.");
                Identity = info.Volume.ToString("x8") + ":" + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8");
            }
            public void Dispose()
            {
                for (int index = handles.Count - 1; index >= 0; index--) handles[index].Dispose();
                handles.Clear();
            }
        }

        internal static Guard LockDirectory(string path) { return new Guard(path); }

        internal static string DirectoryIdentity(string path)
        {
            using (Guard guard = LockDirectory(path)) return guard.Identity;
        }

        internal static void CreateNewDirectory(string path)
        {
            if (!CreateDirectory(path, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private static void ThrowPathError(int error, string path)
        {
            if (error == 2) throw new FileNotFoundException("File not found.", path);
            if (error == 3) throw new DirectoryNotFoundException("Directory not found.");
            if (error == 5) throw new UnauthorizedAccessException("The destination is not accessible.");
            throw new Win32Exception(error);
        }

        internal static FileStream OpenRead(string path)
        {
            path = LocalPath(path);
            SafeFileHandle handle = CreateFile(path, 0x80000000, 1, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                ThrowPathError(error, path);
            }
            try
            {
                FileInformation info;
                if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0 || info.Links != 1)
                    throw new IOException("The file is linked or is not a regular file.");
                return new FileStream(handle, FileAccess.Read);
            }
            catch { handle.Dispose(); throw; }
        }

        internal static byte[] ReadFile(string path, int maximum)
        {
            using (Guard guard = LockDirectory(Path.GetDirectoryName(LocalPath(path))))
            using (FileStream file = OpenRead(path))
            {
                if (file.Length > maximum) throw new InvalidDataException("File exceeds its permitted size.");
                byte[] bytes = new byte[(int)file.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = file.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw new InvalidDataException("File changed while reading.");
                    offset += read;
                }
                if (file.ReadByte() != -1) throw new InvalidDataException("File changed while reading.");
                return bytes;
            }
        }
    }
}
