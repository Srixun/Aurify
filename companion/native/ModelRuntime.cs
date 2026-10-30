using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AurifyClient
{
    // Runtime files are executable code: only the descriptor embedded in Aurify is
    // trusted. No route response, cached manifest, or job can select an executable.
    internal sealed class ModelRuntime : IDisposable
    {
        private const long MaximumArchive = 1024L * 1024 * 1024;
        private const long MaximumExpanded = 3L * 1024 * 1024 * 1024;
        private readonly List<IDisposable> locks = new List<IDisposable>();
        private readonly string runtimeDirectory;
        private string jobDirectory;
        internal string OutputDirectory { get; private set; }
        private ModelRuntime(string path) { runtimeDirectory = path; }

        private sealed class Entry
        {
            internal string Path, Hash;
            internal long Bytes;
        }
        private static string RelativePath(object value)
        {
            string path = ModelProtocol.Text(value, 240);
            if (path.IndexOf('\\') >= 0 || path.StartsWith("/", StringComparison.Ordinal) || path.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|' }) >= 0) throw new ModelFailure("runtime-invalid");
            foreach (string component in path.Split('/'))
            {
                if (component.Length == 0 || component == "." || component == ".." || component.EndsWith(".", StringComparison.Ordinal) || component.EndsWith(" ", StringComparison.Ordinal)) throw new ModelFailure("runtime-invalid");
                string stem = component.Split('.')[0].ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$" || System.Text.RegularExpressions.Regex.IsMatch(stem, @"\A(?:COM|LPT)[0-9¹²³]\z")) throw new ModelFailure("runtime-invalid");
            }
            return path;
        }
        private static string Hash(Stream stream)
        {
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        private static Dictionary<string, object> Descriptor()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Aurify.ModelRuntime"))
            {
                if (stream == null || stream.Length > ModelProtocol.JsonLimit) throw new ModelFailure("runtime-invalid");
                using (var memory = new MemoryStream()) { stream.CopyTo(memory); return ModelProtocol.Parse(memory.ToArray()); }
            }
        }
        internal static async Task<ModelRuntime> OpenAsync(string storageRoot, PortalClient portal, CancellationToken ct)
        {
            var descriptor = Descriptor();
            ModelProtocol.Integer(Json.Get(descriptor, "schemaVersion"), 1, 1);
            if (Json.Str(Json.Get(descriptor, "version")) != "0.2.19-aurify.1" || Json.Str(Json.Get(descriptor, "entryPoint")) != "wow.export.exe") throw new ModelFailure("runtime-invalid");
            string archiveHash = Json.Str(Json.Get(descriptor, "sha256"));
            if (archiveHash == null || !ModelProtocol.Hash.IsMatch(archiveHash)) throw new ModelFailure("runtime-invalid");
            long archiveBytes = ModelProtocol.Integer(Json.Get(descriptor, "bytes"), 1, MaximumArchive), expanded = 0;
            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (object value in ModelProtocol.Array(Json.Get(descriptor, "files"), 20000))
            {
                var row = ModelProtocol.Object(value);
                string path = RelativePath(Json.Get(row, "path")), hash = Json.Str(Json.Get(row, "sha256"));
                long bytes = ModelProtocol.Integer(Json.Get(row, "bytes"), 0, MaximumArchive);
                expanded += bytes;
                if (hash == null || !ModelProtocol.Hash.IsMatch(hash) || expanded > MaximumExpanded || entries.ContainsKey(path)) throw new ModelFailure("runtime-invalid");
                entries.Add(path, new Entry { Path = path, Hash = hash, Bytes = bytes });
            }
            if (!entries.ContainsKey("wow.export.exe")) throw new ModelFailure("runtime-invalid");
            string cache = Path.Combine(storageRoot, "character-models");
            Files.PrivateDirectory(cache);
            using (var cacheGuard = NativeGamePaths.LockDirectory(cache))
            {
                string destination = Path.Combine(cache, archiveHash);
                Files.NoLinks(destination);
                if (!Directory.Exists(destination))
                {
                    string stage = Path.Combine(cache, "stage-" + Guid.NewGuid().ToString("N"));
                    string download = Path.Combine(cache, "download-" + Guid.NewGuid().ToString("N") + ".zip");
                    Files.PrivateDirectory(stage);
                    try
                    {
                        using (var output = new FileStream(download, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                            await portal.ModelRuntimeAsync(output, archiveBytes, ct).ConfigureAwait(false);
                        using (var input = NativeGamePaths.OpenRead(download))
                        {
                            if (input.Length != archiveBytes || Hash(input) != archiveHash) throw new ModelFailure("runtime-invalid");
                            input.Position = 0;
                            using (var archive = new ZipArchive(input, ZipArchiveMode.Read, true))
                            using (var stageGuard = NativeGamePaths.LockDirectory(stage))
                            {
                                if (archive.Entries.Count != entries.Count) throw new ModelFailure("runtime-invalid");
                                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                byte[] buffer = new byte[65536];
                                foreach (var zipped in archive.Entries)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    string relative = RelativePath(zipped.FullName);
                                    Entry expected;
                                    // Reject Unix symlinks/special files, DOS reparse points and directories.
                                    int unixType = (zipped.ExternalAttributes >> 16) & 0xf000;
                                    if ((unixType != 0 && unixType != 0x8000) || (zipped.ExternalAttributes & 0x410) != 0 || !seen.Add(relative) || !entries.TryGetValue(relative, out expected) || relative != expected.Path || zipped.Length != expected.Bytes) throw new ModelFailure("runtime-invalid");
                                    string target = Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar));
                                    Files.PrivateDirectory(Path.GetDirectoryName(target));
                                    using (var parent = NativeGamePaths.LockDirectory(Path.GetDirectoryName(target)))
                                    using (var source = zipped.Open())
                                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                    {
                                        long total = 0;
                                        for (;;)
                                        {
                                            ct.ThrowIfCancellationRequested();
                                            int count = source.Read(buffer, 0, buffer.Length);
                                            if (count == 0) break;
                                            total += count;
                                            if (total > expected.Bytes) throw new ModelFailure("runtime-invalid");
                                            output.Write(buffer, 0, count);
                                        }
                                        if (total != expected.Bytes) throw new ModelFailure("runtime-invalid");
                                    }
                                }
                            }
                        }
                        // Hash every extracted file before publishing; no trusted local manifest.
                        using (var verified = new ModelRuntime(stage)) { verified.Verify(entries, ct); }
                        Directory.Move(stage, destination);
                    }
                    finally
                    {
                        try { Files.Delete(download); } catch { }
                        try { RemoveDirectory(stage); } catch { }
                    }
                }
                Files.PrivateDirectory(destination);
                var runtime = new ModelRuntime(destination);
                try
                {
                    runtime.Verify(entries, ct);
                    string jobs = Path.Combine(cache, "jobs");
                    Files.PrivateDirectory(jobs);
                    runtime.jobDirectory = Path.Combine(jobs, Guid.NewGuid().ToString("N"));
                    Files.PrivateDirectory(runtime.jobDirectory);
                    runtime.OutputDirectory = Path.Combine(runtime.jobDirectory, "output");
                    Files.PrivateDirectory(runtime.OutputDirectory);
                    Files.PrivateDirectory(Path.Combine(runtime.jobDirectory, "profile"));
                    return runtime;
                }
                catch { runtime.Dispose(); throw; }
            }
        }
        private void Verify(Dictionary<string, Entry> expected, CancellationToken ct)
        {
            var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            VerifyDirectory(runtimeDirectory, "", expected, actual, ct);
            if (actual.Count != expected.Count) throw new ModelFailure("runtime-invalid");
        }
        private void VerifyDirectory(string directory, string prefix, Dictionary<string, Entry> expected, HashSet<string> actual, CancellationToken ct)
        {
            locks.Add(NativeGamePaths.LockDirectory(directory));
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ModelFailure("runtime-invalid");
                string relative = prefix + Path.GetFileName(path);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (relative.Length > 240) throw new ModelFailure("runtime-invalid");
                    bool needed = false;
                    foreach (string entry in expected.Keys) if (entry.StartsWith(relative + "/", StringComparison.Ordinal)) { needed = true; break; }
                    if (!needed) throw new ModelFailure("runtime-invalid");
                    VerifyDirectory(path, relative + "/", expected, actual, ct);
                }
                else
                {
                    Entry entry;
                    if (!expected.TryGetValue(relative, out entry) || entry.Path != relative || !actual.Add(relative)) throw new ModelFailure("runtime-invalid");
                    var stream = NativeGamePaths.OpenRead(path);
                    locks.Add(stream); // Deny writes/deletes until the owned worker tree has exited.
                    if (stream.Length != entry.Bytes || Hash(stream) != entry.Hash) throw new ModelFailure("runtime-invalid");
                }
            }
        }
        internal async Task<Dictionary<string, object>> RunAsync(string gameRoot, Dictionary<string, object> client, Dictionary<string, object> job, CancellationToken ct)
        {
            var request = new Dictionary<string, object> {
                { "schemaVersion", 1 }, { "mode", job == null ? "catalog" : "render" },
                { "client", client }, { "gameRoot", NativeGamePaths.LocalPath(gameRoot) }, { "outputDir", OutputDirectory }
            };
            if (job != null) request["job"] = job;
            string requestPath = Path.Combine(jobDirectory, "request.json");
            byte[] bytes = ModelProtocol.Utf8.GetBytes(Json.Stringify(request));
            ModelProtocol.Require(bytes.Length <= ModelProtocol.JsonLimit);
            using (var guard = NativeGamePaths.LockDirectory(jobDirectory))
            using (var gameGuard = NativeGamePaths.LockDirectory(gameRoot))
            using (var dataGuard = NativeGamePaths.LockDirectory(Path.Combine(gameRoot, "Data")))
            using (var buildInfoLock = NativeGamePaths.OpenRead(Path.Combine(gameRoot, ".build.info")))
            {
                using (var file = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(bytes, 0, bytes.Length);
                using (var requestLock = NativeGamePaths.OpenRead(requestPath))
                {
                    await OwnedModelProcess.RunAsync(Path.Combine(runtimeDirectory, "wow.export.exe"), jobDirectory, Path.Combine(jobDirectory, "profile"), requestPath, ct).ConfigureAwait(false);
                    var result = ModelProtocol.Parse(NativeGamePaths.ReadFile(Path.Combine(OutputDirectory, "result.json"), ModelProtocol.JsonLimit));
                    if (!Json.Bool(Json.Get(result, "ok"))) throw new ModelFailure(ModelProtocol.Error(Json.Get(result, "code")));
                    return result;
                }
            }
        }
        private static void RemoveDirectory(string path)
        {
            if (String.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            using (var guard = NativeGamePaths.LockDirectory(path))
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(path))
                {
                    FileAttributes attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked worker output was rejected.");
                    if ((attributes & FileAttributes.Directory) != 0) RemoveDirectory(child);
                    else Files.Delete(child);
                }
            }
            using (var parent = NativeGamePaths.LockDirectory(Path.GetDirectoryName(path))) Directory.Delete(path, false);
        }
        public void Dispose()
        {
            for (int index = locks.Count - 1; index >= 0; index--) locks[index].Dispose();
            locks.Clear();
            try { RemoveDirectory(jobDirectory); } catch { /* Never follow unsafe worker output during cleanup. */ }
        }
    }

    // Create suspended so the child cannot spawn outside our Job Object before
    // assignment. Closing this handle kills every descendant, including NW helpers.
    internal static class OwnedModelProcess
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            internal long ProcessTime, JobTime;
            internal uint Flags;
            internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            internal BasicLimits Basic;
            internal IoCounters Io;
            internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Accounting
        {
            internal long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
            internal uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Startup
        {
            internal uint Size;
            internal string Reserved, Desktop, Title;
            internal uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
            internal ushort ShowWindow, ReservedBytes;
            internal IntPtr ReservedPointer, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int kind, ref ExtendedLimits information, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, int kind, out Accounting information, uint size, IntPtr returned);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref Startup startup, out ProcessInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        private static string Quote(string value)
        {
            if (value.IndexOf('"') >= 0 || value.IndexOf('\0') >= 0 || value.EndsWith("\\", StringComparison.Ordinal)) throw new ModelFailure("runtime-invalid");
            return "\"" + value + "\"";
        }
        private static uint Active(IntPtr job)
        {
            Accounting info;
            if (!QueryInformationJobObject(job, 1, out info, (uint)Marshal.SizeOf(typeof(Accounting)), IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return info.ActiveProcesses;
        }
        internal static async Task RunAsync(string executable, string directory, string profile, string request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (IntPtr.Size != 8) throw new ModelFailure("unsupported-client");
            IntPtr job = CreateJobObject(IntPtr.Zero, null), environment = IntPtr.Zero;
            ProcessInformation process = new ProcessInformation();
            bool assigned = false;
            if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var limits = new ExtendedLimits();
                limits.Basic.Flags = 0x2000 | 0x200; // KILL_ON_JOB_CLOSE | JOB_MEMORY; no breakaway.
                limits.JobMemory = new UIntPtr(3UL * 1024 * 1024 * 1024);
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits)))) throw new Win32Exception(Marshal.GetLastWin32Error());
                // Never inherit an environment containing companion credentials or API keys.
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                    { "SystemRoot", windows }, { "WINDIR", windows }, { "PATH", Path.Combine(windows, "System32") },
                    { "TEMP", profile }, { "TMP", profile }, { "USERPROFILE", profile }, { "LOCALAPPDATA", profile }, { "APPDATA", profile },
                    { "ProgramData", profile }, { "ALLUSERSPROFILE", profile }
                };
                var block = new StringBuilder();
                foreach (var variable in variables) block.Append(variable.Key).Append('=').Append(variable.Value).Append('\0');
                block.Append('\0');
                environment = Marshal.StringToHGlobalUni(block.ToString());
                var startup = new Startup { Size = (uint)Marshal.SizeOf(typeof(Startup)), Flags = 1, ShowWindow = 0 };
                string arguments = Quote(executable) + " --disable-auto-update --user-data-dir=" + Quote(profile) + " --nwapp=" + Quote(Path.GetDirectoryName(executable)) + " --aurify-job=" + Quote(request);
                if (!CreateProcess(executable, new StringBuilder(arguments), IntPtr.Zero, IntPtr.Zero, false, 0x4 | 0x400 | 0x08000000, environment, directory, ref startup, out process)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!AssignProcessToJobObject(job, process.Process)) throw new Win32Exception(Marshal.GetLastWin32Error());
                assigned = true;
                if (ResumeThread(process.Thread) == UInt32.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    deadline.CancelAfter(TimeSpan.FromMinutes(8));
                    try
                    {
                        while (Active(job) != 0) await Task.Delay(100, deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        ct.ThrowIfCancellationRequested();
                        throw new ModelFailure("renderer-timeout");
                    }
                }
                uint exit;
                if (!GetExitCodeProcess(process.Process, out exit) || exit != 0) throw new ModelFailure("renderer-failed");
            }
            finally
            {
                try
                {
                    if (assigned)
                    {
                        if (!TerminateJobObject(job, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        // Cancellation never releases the controller's tracked task before
                        // the tree is gone; the handle is also kill-on-close on host death.
                        while (Active(job) != 0) Thread.Sleep(50);
                    }
                    else if (process.Process != IntPtr.Zero)
                    {
                        TerminateProcess(process.Process, 1);
                        WaitForSingleObject(process.Process, 10000);
                    }
                }
                finally
                {
                    if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
                    if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
                    CloseHandle(job);
                    if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
                }
            }
        }
    }
}
