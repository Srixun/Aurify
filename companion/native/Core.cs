using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace AurifyClient
{
    internal static class Json
    {
        internal const long MaxInteger = 9007199254740991L;
        public static Dictionary<string, object> New() { return new Dictionary<string, object>(StringComparer.Ordinal); }
        public static Dictionary<string, object> Obj(object value) { return value as Dictionary<string, object> ?? New(); }
        public static object Get(Dictionary<string, object> value, string key)
        {
            object result;
            return value != null && value.TryGetValue(key, out result) ? result : null;
        }
        public static string Str(object value, string fallback = null) { return value as string ?? fallback; }
        public static double Num(object value, double fallback = 0)
        {
            if (value == null) return 0;
            if (value is bool) return (bool)value ? 1 : 0;
            string text = value as string;
            if (text != null)
            {
                text = text.Trim();
                if (text.Length == 0) return 0;
                double parsed;
                return Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && !Double.IsNaN(parsed) && !Double.IsInfinity(parsed) ? parsed : fallback;
            }
            if (!Numeric(value)) return fallback;
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return Double.IsNaN(number) || Double.IsInfinity(number) ? fallback : number;
        }
        public static long Int(object value, long fallback = 0)
        {
            double number = Num(value, Double.NaN);
            return !Double.IsNaN(number) && Math.Abs(number) <= MaxInteger && Math.Truncate(number) == number ? (long)number : fallback;
        }
        public static bool Bool(object value, bool fallback = false) { return value is bool ? (bool)value : fallback; }
        public static IEnumerable<object> Items(object value)
        {
            IList list = value as IList;
            if (list == null || value is string || value is IDictionary) yield break;
            foreach (object item in list) yield return item;
        }
        internal static bool Numeric(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is float || value is double || value is decimal;
        }
        internal static bool Integer(object value, long minimum = 0, long maximum = MaxInteger)
        {
            if (!Numeric(value)) return false;
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return !Double.IsNaN(number) && !Double.IsInfinity(number) && number >= minimum && number <= maximum && Math.Truncate(number) == number;
        }
        public static object Parse(string text)
        {
            return new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024, RecursionLimit = 100 }.DeserializeObject(text);
        }
        public static string Stringify(object value)
        {
            StringBuilder result = new StringBuilder();
            Append(result, value, 0);
            return result.ToString();
        }
        private static void Quote(StringBuilder result, string text)
        {
            result.Append('"');
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"': result.Append("\\\""); break;
                    case '\\': result.Append("\\\\"); break;
                    case '\b': result.Append("\\b"); break;
                    case '\f': result.Append("\\f"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\t': result.Append("\\t"); break;
                    default:
                        if (c < 32 || (Char.IsSurrogate(c) && !(Char.IsHighSurrogate(c) && i + 1 < text.Length && Char.IsLowSurrogate(text[i + 1]))))
                            result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                        {
                            result.Append(c);
                            if (Char.IsHighSurrogate(c)) result.Append(text[++i]);
                        }
                        break;
                }
            }
            result.Append('"');
        }
        private static string Number(object value)
        {
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (Double.IsNaN(number) || Double.IsInfinity(number)) return "null";
            if (number == 0) return "0";
            string text = number.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
            int e = text.IndexOf('e');
            if (e < 0) return text;
            int exponent = Int32.Parse(text.Substring(e + 1), CultureInfo.InvariantCulture);
            string mantissa = text.Substring(0, e);
            if (exponent >= -6 && exponent < 21)
            {
                bool negative = mantissa[0] == '-';
                if (negative) mantissa = mantissa.Substring(1);
                int dot = mantissa.IndexOf('.');
                if (dot < 0) dot = mantissa.Length;
                string digits = mantissa.Replace(".", "");
                int position = dot + exponent;
                string expanded = position <= 0 ? "0." + new string('0', -position) + digits : position >= digits.Length ? digits + new string('0', position - digits.Length) : digits.Insert(position, ".");
                return negative ? "-" + expanded : expanded;
            }
            return mantissa + "e" + (exponent >= 0 ? "+" : "-") + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
        }
        private static void Append(StringBuilder result, object value, int depth)
        {
            if (depth > 100) throw new InvalidDataException("JSON nesting is too deep");
            if (value == null) { result.Append("null"); return; }
            if (value is string) { Quote(result, (string)value); return; }
            if (value is bool) { result.Append((bool)value ? "true" : "false"); return; }
            if (Numeric(value)) { result.Append(Number(value)); return; }
            IDictionary dictionary = value as IDictionary;
            bool first = true;
            if (dictionary != null)
            {
                result.Append('{');
                foreach (DictionaryEntry item in dictionary)
                {
                    string key = item.Key as string;
                    if (key == null) throw new InvalidDataException("JSON object key is invalid");
                    if (!first) result.Append(',');
                    first = false;
                    Quote(result, key); result.Append(':'); Append(result, item.Value, depth + 1);
                }
                result.Append('}'); return;
            }
            IList list = value as IList;
            if (list != null)
            {
                result.Append('[');
                foreach (object item in list)
                {
                    if (!first) result.Append(',');
                    first = false; Append(result, item, depth + 1);
                }
                result.Append(']'); return;
            }
            // Only persisted DTOs reach this branch. Do not share a mutable serializer across threads.
            Append(result, new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024, RecursionLimit = 100 }.DeserializeObject(new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024, RecursionLimit = 100 }.Serialize(value)), depth + 1);
        }
        public static string Hash(byte[] bytes)
        {
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        public static long Now() { return (DateTime.UtcNow.Ticks - 621355968000000000L) / TimeSpan.TicksPerMillisecond; }
    }

    internal static class Files
    {
        private const uint ReparsePoint = 0x400;
        private const uint DirectoryAttribute = 0x10;
        private const uint OpenExisting = 3;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint BackupSemantics = 0x02000000;
        private const int MaximumWriteBytes = 64 * 1024 * 1024;
        [StructLayout(LayoutKind.Sequential)]
        private struct HandleInfo
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation;
            public System.Runtime.InteropServices.ComTypes.FILETIME Access;
            public System.Runtime.InteropServices.ComTypes.FILETIME Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, FileShare sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existing, string destination, uint flags);
        private static ulong Ticks(System.Runtime.InteropServices.ComTypes.FILETIME value) { return ((ulong)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime; }
        private static ulong Length(HandleInfo info) { return ((ulong)info.SizeHigh << 32) | info.SizeLow; }
        private static string Identity(HandleInfo info)
        {
            return "win32:" + info.Volume.ToString(CultureInfo.InvariantCulture) + ":" + (((ulong)info.IndexHigh << 32) | info.IndexLow).ToString(CultureInfo.InvariantCulture) + ":" + Ticks(info.Creation).ToString(CultureInfo.InvariantCulture);
        }
        private static StableFileMetadata Metadata(HandleInfo info)
        {
            ulong length = Length(info), write = Ticks(info.Write);
            if (length > long.MaxValue || write > long.MaxValue) throw new IOException("A required local file has invalid metadata");
            return new StableFileMetadata {
                Identity = Identity(info),
                Length = (long)length,
                LastWriteTicks = (long)write
            };
        }

        private static HandleInfo Information(SafeFileHandle handle)
        {
            HandleInfo info;
            if (!GetFileInformationByHandle(handle, out info)) throw new IOException("A required local file could not be inspected");
            if ((info.Attributes & ReparsePoint) != 0) throw new IOException("Linked paths are not allowed");
            return info;
        }
        private static string Absolute(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal) || full.IndexOf(':', root.Length) >= 0)
                throw new IOException("Special file paths are not allowed");
            return full;
        }
        public static void NoLinks(string path)
        {
            string current = Absolute(path);
            for (;;)
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked paths are not allowed");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                string parent = Path.GetDirectoryName(current);
                if (String.IsNullOrEmpty(parent) || String.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
        }
        // Hold every ancestor without FILE_SHARE_DELETE, preventing junction/rename swaps during I/O.
        private sealed class ParentGuard : IDisposable
        {
            private readonly List<SafeFileHandle> handles = new List<SafeFileHandle>();
            private readonly StringBuilder identity = new StringBuilder();
            internal string Identity { get { return identity.ToString(); } }
            public ParentGuard(string path)
            {
                try
                {
                    Stack<string> parents = new Stack<string>();
                    string current = Path.GetDirectoryName(path);
                    while (!String.IsNullOrEmpty(current))
                    {
                        parents.Push(current);
                        string next = Path.GetDirectoryName(current);
                        if (String.Equals(next, current, StringComparison.OrdinalIgnoreCase)) break;
                        current = next;
                    }
                    while (parents.Count != 0)
                    {
                        SafeFileHandle handle = CreateFile(parents.Pop(), 0, FileShare.ReadWrite, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
                        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("A required local directory could not be opened"); }
                        handles.Add(handle);
                        HandleInfo info = Information(handle);
                        if ((info.Attributes & DirectoryAttribute) == 0) throw new IOException("A required local directory is invalid");
                        identity.Append(Files.Identity(info)).Append('\0');
                    }
                }
                catch { Dispose(); throw; }
            }
            public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); handles.Clear(); }
        }
        private static DirectorySecurity PrivateDirectorySecurity()
        {
            DirectorySecurity security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            return security;
        }
        private static void EnsureDirectory(string full, bool privateDirectory)
        {
            NoLinks(full);
            bool exists;
            try
            {
                if ((File.GetAttributes(full) & FileAttributes.Directory) == 0) throw new IOException("A required local directory is invalid");
                exists = true;
            }
            catch (FileNotFoundException) { exists = false; }
            catch (DirectoryNotFoundException) { exists = false; }
            if (!exists)
            {
                string parent = Path.GetDirectoryName(full);
                if (String.IsNullOrEmpty(parent)) throw new IOException("A required local directory could not be created");
                EnsureDirectory(parent, false);
                using (ParentGuard guard = new ParentGuard(full))
                {
                    NoLinks(full);
                    if (privateDirectory) Directory.CreateDirectory(full, PrivateDirectorySecurity());
                    else Directory.CreateDirectory(full);
                }
            }
            using (ParentGuard guard = new ParentGuard(Path.Combine(full, ".guard")))
            {
                if (privateDirectory) Directory.SetAccessControl(full, PrivateDirectorySecurity());
            }
        }
        internal static void PrivateDirectory(string path) { EnsureDirectory(Absolute(path), true); }
        internal static void Delete(string path)
        {
            string full = Absolute(path);
            NoLinks(full);
            try { File.GetAttributes(full); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            using (ParentGuard guard = new ParentGuard(full)) { NoLinks(full); File.Delete(full); }
        }
        private static FileSecurity PrivateSecurity()
        {
            FileSecurity security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, AccessControlType.Allow));
            return security;
        }
        private static bool SameFile(HandleInfo first, HandleInfo second)
        {
            return first.Volume == second.Volume && first.IndexHigh == second.IndexHigh && first.IndexLow == second.IndexLow
                && Ticks(first.Creation) == Ticks(second.Creation) && Length(first) == Length(second) && Ticks(first.Write) == Ticks(second.Write);
        }
        public static StableFileMetadata StableMetadata(string path)
        {
            string full = Absolute(path);
            NoLinks(full);
            try { File.GetAttributes(full); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            using (ParentGuard guard = new ParentGuard(full))
            {
                NoLinks(full);
                using (SafeFileHandle handle = CreateFile(full, 0, FileShare.ReadWrite, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero))
                {
                    if (handle.IsInvalid)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 2 || error == 3) return null;
                        throw new IOException("A required local file could not be inspected");
                    }
                    HandleInfo info = Information(handle);
                    if ((info.Attributes & DirectoryAttribute) != 0) throw new IOException("A required local file is invalid");
                    NoLinks(full);
                    StableFileMetadata metadata = Metadata(info);
                    metadata.ParentIdentity = guard.Identity;
                    return metadata;
                }
            }
        }

        public static StableFile StableRead(string path, int maxBytes = 33554432)
        {
            if (maxBytes < 0) throw new ArgumentOutOfRangeException("maxBytes");
            string full = Absolute(path);
            NoLinks(full);
            try { File.GetAttributes(full); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            for (int attempt = 0; attempt < 4; attempt++)
            {
                using (ParentGuard guard = new ParentGuard(full))
                {
                    HandleInfo first;
                    // Release the sampling handle during settling so normal atomic saves can replace the file.
                    using (SafeFileHandle sample = CreateFile(full, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero))
                    {
                        if (sample.IsInvalid)
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error == 2 || error == 3) return null;
                            throw new IOException("A required local file could not be opened");
                        }
                        first = Information(sample);
                        if ((first.Attributes & DirectoryAttribute) != 0) throw new IOException("A required local file is invalid");
                        if (Length(first) > (ulong)maxBytes) throw new IOException("Local file is unexpectedly large");
                    }
                    Thread.Sleep(200);
                    NoLinks(full);
                    using (SafeFileHandle handle = CreateFile(full, 0x80000000, FileShare.ReadWrite, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero))
                    {
                        if (handle.IsInvalid)
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error == 2 || error == 3) return null;
                            throw new IOException("A required local file could not be opened");
                        }
                        HandleInfo before = Information(handle);
                        if ((before.Attributes & DirectoryAttribute) != 0) throw new IOException("A required local file is invalid");
                        if (Length(before) > (ulong)maxBytes) throw new IOException("Local file is unexpectedly large");
                        if (!SameFile(first, before)) continue;
                        byte[] bytes = new byte[(int)Length(before)];
                        using (FileStream stream = new FileStream(handle, FileAccess.Read))
                        {
                            int offset = 0;
                            while (offset < bytes.Length)
                            {
                                int count = stream.Read(bytes, offset, bytes.Length - offset);
                                if (count == 0) break;
                                offset += count;
                            }
                            if (offset != bytes.Length || stream.ReadByte() != -1) continue;
                            HandleInfo after = Information(handle);
                            NoLinks(full);
                            if (!SameFile(before, after)) continue;
                            StableFileMetadata metadata = Metadata(after);
                            metadata.ParentIdentity = guard.Identity;
                            return new StableFile { Bytes = bytes, Identity = metadata.Identity,
                                LastWriteMs = (metadata.LastWriteTicks / 10000L) - 11644473600000L, Metadata = metadata };
                        }
                    }
                }
            }
            return null;
        }
        public static byte[] ReadBytes(string path, int maxBytes)
        {
            StableFile value = StableRead(path, maxBytes);
            if (value == null) throw new IOException("Local file changed or could not be read");
            return value.Bytes;
        }
        public static void AtomicWrite(string path, byte[] bytes, bool privateFile = true)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            if (bytes.Length > MaximumWriteBytes) throw new IOException("Local file is unexpectedly large");
            string target = Absolute(path);
            string directory = Path.GetDirectoryName(target);
            NoLinks(target);
            if (privateFile) PrivateDirectory(directory); else EnsureDirectory(directory, false);
            using (ParentGuard guard = new ParentGuard(target))
            {
                NoLinks(target);
                string temporary = Path.Combine(directory, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (FileStream stream = privateFile ? new FileStream(temporary, FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 8192, FileOptions.WriteThrough, PrivateSecurity()) : new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    NoLinks(target);
                    // Same-directory atomic replacement plus write-through; new DACL travels with the file.
                    if (!MoveFileEx(temporary, target, 0x1 | 0x8)) throw new IOException("Local file could not be saved atomically");
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }

    internal sealed class ClientStorage
    {
        private const int MaximumJsonBytes = 2 * 1024 * 1024;
        internal static readonly Regex Uuid = new Regex(@"\A[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex HashPattern = new Regex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
        internal static readonly Regex DatePattern = new Regex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z", RegexOptions.CultureInvariant);
        public string Root { get; private set; }
        public string ConfigPath { get { return Path.Combine(Root, "config.json"); } }
        public string StatePath { get { return Path.Combine(Root, "config.state.json"); } }
        public string CredentialsPath { get { return Path.Combine(Root, "config.credentials"); } }
        public string ControlPath { get { return Path.Combine(Root, "config.control.json"); } }
        public ClientStorage(string root)
        {
            Root = Path.GetFullPath(root);
            Files.NoLinks(Root);
        }
        public static string ValidateServerUrl(string value)
        {
            Uri uri;
            if (value == null) value = "https://auratism.com";
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || String.IsNullOrEmpty(uri.Host)) throw new ArgumentException("Server URL must be a valid URL");
            bool loopback = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1" || uri.Host == "[::1]" || uri.Host == "::1";
            if (uri.Scheme != "https" && !(uri.Scheme == "http" && loopback)) throw new ArgumentException("Server URL must use HTTPS");
            if (!String.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment)) throw new ArgumentException("Server URL must contain only its origin");
            return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        }
        public static ClientConfig NormalizeConfig(ClientConfig input)
        {
            if (input == null) input = new ClientConfig { pollMs = 5000 };
            string guild = Regex.Replace(input.guildName ?? "", @"[\x00-\x1f\x7f]", "").Trim();
            if (guild.Length > 64) guild = guild.Substring(0, 64);
            return new ClientConfig {
                version = 1,
                gameDir = String.IsNullOrWhiteSpace(input.gameDir) ? null : Path.GetFullPath(input.gameDir.Trim()),
                serverUrl = ValidateServerUrl(String.IsNullOrEmpty(input.serverUrl) ? "https://auratism.com" : input.serverUrl),
                guildName = guild.Length == 0 ? "AURA" : guild,
                pollMs = Math.Max(1000, Math.Min(60000, input.pollMs)),
                foreverVisibility = input.foreverVisibility == "public" ? "public" : "private"
            };
        }
        private static object ReadJson(string path)
        {
            Files.NoLinks(path);
            try { File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            byte[] bytes = Files.ReadBytes(path, MaximumJsonBytes);
            return Json.Parse(new UTF8Encoding(false, true).GetString(bytes));
        }
        private static void WriteJson(string path, object value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Json.Stringify(value) + "\n");
            if (bytes.Length > MaximumJsonBytes) throw new IOException("Local state is unexpectedly large");
            Files.AtomicWrite(path, bytes);
        }
        public ClientConfig LoadConfig()
        {
            Dictionary<string, object> input = Json.Obj(ReadJson(ConfigPath));
            object serverValue = Json.Get(input, "serverUrl");
            if (serverValue != null && !(serverValue is string) && !(serverValue is bool && !(bool)serverValue) && !(Json.Numeric(serverValue) && Json.Num(serverValue) == 0))
                throw new ArgumentException("Server URL must be a valid URL");
            object pollValue = Json.Get(input, "pollMs");
            double poll = input.ContainsKey("pollMs") ? Json.Num(pollValue, Double.NaN) : Double.NaN;
            int pollMs = !Double.IsNaN(poll) && Math.Truncate(poll) == poll ? (int)Math.Max(1000, Math.Min(60000, poll)) : 5000;
            return NormalizeConfig(new ClientConfig { gameDir = Json.Str(Json.Get(input, "gameDir")), serverUrl = Json.Str(Json.Get(input, "serverUrl")), guildName = Json.Str(Json.Get(input, "guildName")), pollMs = pollMs, foreverVisibility = Json.Str(Json.Get(input, "foreverVisibility")) });
        }
        public void SaveConfig(ClientConfig config) { WriteJson(ConfigPath, NormalizeConfig(config)); }
        private static bool Matches(Regex pattern, object value) { string text = value as string; return text != null && pattern.IsMatch(text); }
        private static void CopyTimezone(Dictionary<string, object> source, Dictionary<string, object> destination, string key)
        {
            object value = Json.Get(source, key);
            if (Json.Integer(value, -840, 840)) destination[key] = Json.Int(value);
        }
        private static Dictionary<string, object> Cursor(object value)
        {
            Dictionary<string, object> source = Json.Obj(value);
            object offset = Json.Get(source, "offset"), civil = Json.Get(source, "lastCivilTime"), start = Json.Get(source, "lineStart");
            if (!Json.Integer(offset) || !(start is bool) || !source.ContainsKey("lastCivilTime") || (civil != null && !Json.Integer(civil, -8640000000000000L, 8640000000000000L))) return null;
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "offset", Json.Int(offset) }, { "lineStart", start }, { "lastCivilTime", civil == null ? null : (object)Json.Int(civil) } };
        }
        private static Dictionary<string, object> Current(object value)
        {
            Dictionary<string, object> input = Json.Obj(value);
            string fileKey = Json.Str(Json.Get(input, "fileKey"));
            if (!Matches(Uuid, Json.Get(input, "streamId")) || String.IsNullOrEmpty(fileKey) || fileKey.Length > 256 || !Matches(DatePattern, Json.Get(input, "fileDate")) || !Json.Integer(Json.Get(input, "offset")) || !Json.Integer(Json.Get(input, "lastSize")) || !Json.Integer(Json.Get(input, "prefixBytes"), 0, 4096) || !Matches(HashPattern, Json.Get(input, "prefixHash"))) return null;
            Dictionary<string, object> clean = Json.New();
            clean["streamId"] = input["streamId"]; clean["fileKey"] = fileKey; clean["fileDate"] = input["fileDate"];
            CopyTimezone(input, clean, "timezoneOffsetMinutes");
            clean["baseOffset"] = Json.Integer(Json.Get(input, "baseOffset")) ? Json.Int(input["baseOffset"]) : 0L;
            CopyTimezone(input, clean, "hostTimezoneOffsetMinutes");
            clean["offset"] = Json.Int(input["offset"]);
            foreach (string key in new[] { "timezoneCursor", "pendingTimezoneCursor" })
            {
                Dictionary<string, object> cursor = Cursor(Json.Get(input, key));
                if (cursor != null) clean[key] = cursor;
            }
            clean["lastSize"] = Json.Int(input["lastSize"]); clean["prefixBytes"] = Json.Int(input["prefixBytes"]); clean["prefixHash"] = input["prefixHash"];
            string report = Json.Str(Json.Get(input, "reportId")); clean["reportId"] = report != null && report.Length <= 256 ? report : null;
            return clean;
        }
        private static Dictionary<string, object> SanitizeState(object value)
        {
            Dictionary<string, object> input = Json.Obj(value), snapshots = Json.New();
            int count = 0;
            foreach (KeyValuePair<string, object> entry in Json.Obj(Json.Get(input, "snapshots")))
            {
                if (count++ >= 10000) break;
                if (!Regex.IsMatch(entry.Key, @"\A(professions|overlord|activity|characters):[0-9a-f]{64}\z", RegexOptions.CultureInvariant)) continue;
                Dictionary<string, object> checkpoint = Json.Obj(entry.Value);
                if (Matches(HashPattern, Json.Get(checkpoint, "rawHash")) && Matches(HashPattern, Json.Get(checkpoint, "payloadHash")))
                    snapshots[entry.Key] = new Dictionary<string, object>(StringComparer.Ordinal) { { "rawHash", checkpoint["rawHash"] }, { "payloadHash", checkpoint["payloadHash"] } };
            }
            Dictionary<string, object> combat = Json.Obj(Json.Get(input, "combat"));
            List<object> pending = new List<object>();
            IList entries = Json.Get(combat, "pendingClosures") as IList;
            if (entries != null)
            {
                for (int i = Math.Max(0, entries.Count - 32); i < entries.Count; i++)
                {
                    Dictionary<string, object> entry = Json.Obj(entries[i]);
                    if (!Matches(Uuid, Json.Get(entry, "streamId")) || !Matches(DatePattern, Json.Get(entry, "fileDate"))) continue;
                    Dictionary<string, object> clean = new Dictionary<string, object>(StringComparer.Ordinal) { { "streamId", entry["streamId"] }, { "fileDate", entry["fileDate"] } };
                    CopyTimezone(entry, clean, "timezoneOffsetMinutes"); pending.Add(clean);
                }
            }
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "version", 1 }, { "snapshots", snapshots }, { "combat", new Dictionary<string, object>(StringComparer.Ordinal) { { "current", Current(Json.Get(combat, "current")) }, { "pendingClosures", pending } } } };
        }
        public Dictionary<string, object> LoadState() { return SanitizeState(ReadJson(StatePath)); }
        public void SaveState(Dictionary<string, object> state) { WriteJson(StatePath, SanitizeState(state)); }
        private static byte[] DecodeBase64(string value)
        {
            string normalized = Regex.Replace((value ?? "").Replace('-', '+').Replace('_', '/'), @"[^A-Za-z0-9+/=]", "");
            int end = normalized.IndexOf('=');
            if (end >= 0) normalized = normalized.Substring(0, end);
            if (normalized.Length % 4 == 1) normalized = normalized.Substring(0, normalized.Length - 1);
            return Convert.FromBase64String(normalized.PadRight((normalized.Length + 3) / 4 * 4, '='));
        }
        private static ClientCredentials Credentials(object value)
        {
            Dictionary<string, object> input = Json.Obj(value);
            string token = Json.Str(Json.Get(input, "deviceToken")), device = Json.Str(Json.Get(input, "deviceId")), source = Json.Str(Json.Get(input, "sourceKey"));
            byte[] bytes = DecodeBase64(source);
            try
            {
                if (String.IsNullOrEmpty(token) || token.Length > 4096 || String.IsNullOrEmpty(device) || device.Length > 256 || bytes.Length != 32) throw new InvalidDataException("Stored credentials are invalid");
                return new ClientCredentials { deviceToken = token, deviceId = device, sourceKey = Convert.ToBase64String(bytes) };
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
        public ClientCredentials ReadCredentials()
        {
            object value;
            try { value = ReadJson(CredentialsPath); }
            catch { throw new IOException("Protected credentials could not be read"); }
            if (value == null) return null;
            Dictionary<string, object> envelope = Json.Obj(value);
            if (!Json.Integer(Json.Get(envelope, "version"), 1, 1) || !(Json.Get(envelope, "protected") is string)) throw new InvalidDataException("Protected credentials are invalid");
            byte[] plain = null, protectedBytes = null;
            try
            {
                protectedBytes = DecodeBase64((string)envelope["protected"]);
                plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return Credentials(Json.Parse(new UTF8Encoding(false, true).GetString(plain)));
            }
            catch { throw new InvalidDataException("Protected credentials cannot be decrypted for this Windows user"); }
            finally
            {
                if (plain != null) Array.Clear(plain, 0, plain.Length);
                if (protectedBytes != null) Array.Clear(protectedBytes, 0, protectedBytes.Length);
            }
        }
        public void WriteCredentials(ClientCredentials credentials)
        {
            if (credentials == null) throw new InvalidDataException("Stored credentials are invalid");
            ClientCredentials normalized = Credentials(new Dictionary<string, object>(StringComparer.Ordinal) { { "deviceToken", credentials.deviceToken }, { "deviceId", credentials.deviceId }, { "sourceKey", credentials.sourceKey } });
            byte[] plain = Encoding.UTF8.GetBytes(Json.Stringify(normalized)), protectedBytes = null;
            try
            {
                try { protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
                catch { throw new IOException("Windows credential protection failed"); }
                WriteJson(CredentialsPath, new Dictionary<string, object>(StringComparer.Ordinal) { { "version", 1 }, { "protected", Convert.ToBase64String(protectedBytes) } });
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
                if (protectedBytes != null) Array.Clear(protectedBytes, 0, protectedBytes.Length);
            }
        }
        public static string NewSourceKey()
        {
            byte[] bytes = new byte[32];
            try { using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes); return Convert.ToBase64String(bytes); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
        public void WriteControl(string token)
        {
            if (token == null || !Regex.IsMatch(token, @"\A[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant)) throw new ArgumentException("Invalid control token");
            WriteJson(ControlPath, new Dictionary<string, object>(StringComparer.Ordinal) { { "token", token } });
        }
        public void RemoveControl() { Files.Delete(ControlPath); }
    }

    internal sealed class PortalException : Exception
    {
        public int Status { get; private set; }
        public string Code { get; private set; }
        public long? ExpectedOffset { get; private set; }
        public PortalException(string message, int status = 0, string code = null, long? expectedOffset = null) : base(message) { Status = status; Code = code; ExpectedOffset = expectedOffset; }
    }

    internal sealed partial class PortalClient : IDisposable
    {
        private const int MaximumResponseBytes = 2 * 1024 * 1024;
        private readonly HttpClient client;
        private readonly string token;
        public string ServerUrl { get; private set; }
        static PortalClient()
        {
            // The directly compiled executable has no runtime config opting older Framework targets into system TLS defaults.
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        }
        public PortalClient(string serverUrl, string token = null)
        {
            ServerUrl = ClientStorage.ValidateServerUrl(serverUrl);
            this.token = token;
            HttpClientHandler handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None, UseDefaultCredentials = false };
            client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }
        private async Task<Dictionary<string, object>> RequestAsync(string path, object body, bool authenticated, CancellationToken cancellation, int timeoutMs = 15000)
        {
            if (authenticated && String.IsNullOrEmpty(token)) throw new PortalException("This companion is not paired", 401);
            using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (HttpRequestMessage request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, new Uri(new Uri(ServerUrl), path)))
            {
                deadline.CancelAfter(timeoutMs);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                if (authenticated)
                {
                    try { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); }
                    catch { throw new PortalException("Device authorization was rejected", 401); }
                }
                if (body != null) request.Content = new StringContent(Json.Stringify(body), Encoding.UTF8, "application/json");
                HttpResponseMessage response;
                try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false); }
                catch (Exception error)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!(error is HttpRequestException) && !(error is IOException) &&
                        !(error is OperationCanceledException) && !(error is TimeoutException)) throw;
                    throw new PortalException("The Auratism portal is temporarily unreachable");
                }
                using (response)
                using (deadline.Token.Register(delegate { response.Dispose(); }))
                {
                    int status = (int)response.StatusCode;
                    if (status >= 300 && status < 400) throw new PortalException("The Auratism portal returned a redirect", status);
                    Dictionary<string, object> payload;
                    try
                    {
                        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new PortalException("Portal response is too large", status);
                        using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (MemoryStream bytes = new MemoryStream())
                        {
                            byte[] buffer = new byte[8192];
                            for (;;)
                            {
                                int count = await stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false);
                                if (count == 0) break;
                                if (bytes.Length + count > MaximumResponseBytes) throw new PortalException("Portal response is too large", status);
                                bytes.Write(buffer, 0, count);
                            }
                            if (response.Content.Headers.ContentLength.HasValue && bytes.Length != response.Content.Headers.ContentLength.Value)
                                throw new PortalException("Portal response was interrupted", response.IsSuccessStatusCode ? 0 : status);
                            try { payload = bytes.Length == 0 ? Json.New() : Json.Obj(Json.Parse(Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length))); }
                            catch { throw new PortalException("Portal returned an invalid response", status); }
                        }
                    }
                    catch (PortalException) { throw; }
                    catch (Exception error)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        bool interrupted = deadline.IsCancellationRequested || error is IOException ||
                            error is HttpRequestException || error is OperationCanceledException;
                        throw new PortalException("Portal response could not be read", response.IsSuccessStatusCode && interrupted ? 0 : status);
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        object expected = Json.Get(payload, "expectedOffset");
                        long? expectedOffset = Json.Integer(expected) ? (long?)Json.Int(expected) : null;
                        string code = Json.Str(Json.Get(payload, "code"));
                        if (code != null && code.Length > 80) code = code.Substring(0, 80);
                        string message = status == 401 || status == 403 ? "Device authorization was rejected" : status == 409 && expectedOffset.HasValue ? "Combat stream offset must be reconciled" : "Portal request failed (" + status.ToString(CultureInfo.InvariantCulture) + ")";
                        throw new PortalException(message, status, code, expectedOffset);
                    }
                    return payload;
                }
            }
        }
        public async Task<PairingRequest> RequestDeviceAsync(string label, CancellationToken ct)
        {
            Dictionary<string, object> payload = await RequestAsync("/members/api/devices/request", new Dictionary<string, object> { { "label", label } }, false, ct).ConfigureAwait(false);
            string device = Json.Str(Json.Get(payload, "deviceCode")), user = Json.Str(Json.Get(payload, "userCode"));
            if (String.IsNullOrEmpty(device) || user == null || user.Length != 8 || Regex.IsMatch(user, @"[\x00-\x20\x7f]") || !Json.Integer(Json.Get(payload, "expiresIn"), 1, 3600) || !Json.Integer(Json.Get(payload, "interval"), 1, 60)) throw new PortalException("Portal returned an invalid pairing response");
            Uri verification;
            if (!Uri.TryCreate(Json.Str(Json.Get(payload, "verificationUri")), UriKind.Absolute, out verification)) throw new PortalException("Portal returned an invalid pairing address");
            if (!String.Equals(verification.GetLeftPart(UriPartial.Authority), ServerUrl, StringComparison.OrdinalIgnoreCase) || !String.IsNullOrEmpty(verification.UserInfo) || (verification.AbsolutePath != "/members" && !verification.AbsolutePath.StartsWith("/members/", StringComparison.Ordinal))) throw new PortalException("Portal returned an untrusted pairing address");
            return new PairingRequest { DeviceCode = device, UserCode = user, VerificationUri = verification.AbsoluteUri, ExpiresIn = (int)Json.Int(payload["expiresIn"]), Interval = (int)Json.Int(payload["interval"]) };
        }
        public async Task<PairingApproval> PollDeviceAsync(string deviceCode, CancellationToken ct)
        {
            if (String.IsNullOrEmpty(deviceCode)) throw new PortalException("No pairing request is active");
            Dictionary<string, object> payload = await RequestAsync("/members/api/devices/poll", new Dictionary<string, object> { { "deviceCode", deviceCode } }, false, ct).ConfigureAwait(false);
            if (Json.Str(Json.Get(payload, "status")) == "pending") return new PairingApproval { Status = "pending" };
            string approvedToken = Json.Str(Json.Get(payload, "token")), deviceId = Json.Str(Json.Get(payload, "deviceId"));
            if (Json.Str(Json.Get(payload, "status")) != "approved" || approvedToken == null || !Regex.IsMatch(approvedToken, @"\A[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant) || deviceId == null || !ClientStorage.Uuid.IsMatch(deviceId)) throw new PortalException("Portal returned an invalid pairing result");
            if (!(Json.Get(payload, "serverUrl") is string)) throw new PortalException("Portal returned an invalid pairing result");
            string server;
            try { server = ClientStorage.ValidateServerUrl(Json.Str(Json.Get(payload, "serverUrl"))); }
            catch { throw new PortalException("Portal returned an invalid pairing result"); }
            if (server != ServerUrl) throw new PortalException("Portal returned an untrusted pairing address");
            return new PairingApproval { Status = "approved", Token = approvedToken, DeviceId = deviceId, ServerUrl = server };
        }
        public async Task UploadSnapshotAsync(string kind, Dictionary<string, object> payload, CancellationToken ct)
        {
            if (kind != "professions" && kind != "overlord" && kind != "activity" && kind != "characters") throw new ArgumentException("Unsupported snapshot kind");
            Dictionary<string, object> reply = await RequestAsync("/members/api/ingest/" + kind, payload, true, ct).ConfigureAwait(false);
            if (!(Json.Get(reply, "accepted") is bool) || !Json.Bool(reply["accepted"]) || !(Json.Get(reply, "duplicate") is bool)) throw new PortalException("Portal returned an invalid snapshot acknowledgement");
        }
        private static CombatReply CombatResponse(Dictionary<string, object> payload, bool acknowledgement)
        {
            string report = Json.Str(Json.Get(payload, "reportId"));
            if (!Json.Integer(Json.Get(payload, "offset")) || String.IsNullOrEmpty(report) || (acknowledgement && !Json.Bool(Json.Get(payload, "accepted")))) throw new PortalException(acknowledgement ? "Portal returned an invalid combat acknowledgement" : "Portal returned an invalid combat status");
            return new CombatReply { Offset = Json.Int(payload["offset"]), ReportId = report };
        }
        public async Task<CombatReply> CombatStatusAsync(string streamId, CancellationToken ct)
        {
            if (streamId == null || !ClientStorage.Uuid.IsMatch(streamId)) throw new ArgumentException("Invalid combat stream identifier");
            try { return CombatResponse(await RequestAsync("/members/api/ingest/combat/" + Uri.EscapeDataString(streamId) + "/status", null, true, ct).ConfigureAwait(false), false); }
            catch (PortalException error) { if (error.Status == 404) return null; throw; }
        }
        public async Task<CombatReply> UploadCombatAsync(string streamId, long offset, byte[] data, int count, long capturedAt, string fileDate, int? timezoneOffsetMinutes, bool closed, CancellationToken ct)
        {
            if (streamId == null || !ClientStorage.Uuid.IsMatch(streamId)) throw new ArgumentException("Invalid combat stream identifier");
            if (offset < 0 || offset > Json.MaxInteger) throw new ArgumentException("Invalid combat stream offset");
            if (data == null || count < 0 || count > data.Length || count > 512 * 1024) throw new ArgumentException("Invalid combat chunk length");
            if (offset > Json.MaxInteger - count) throw new ArgumentException("Invalid combat chunk end offset");
            if (capturedAt < 1 || capturedAt > Json.MaxInteger || fileDate == null || !ClientStorage.DatePattern.IsMatch(fileDate)) throw new ArgumentException("Invalid combat chunk metadata");
            if (timezoneOffsetMinutes.HasValue && (timezoneOffsetMinutes.Value < -840 || timezoneOffsetMinutes.Value > 840)) throw new ArgumentException("Invalid combat timezone offset");
            Dictionary<string, object> body = new Dictionary<string, object>(StringComparer.Ordinal) { { "streamId", streamId }, { "offset", offset }, { "data", Convert.ToBase64String(data, 0, count) }, { "capturedAt", capturedAt }, { "fileDate", fileDate } };
            if (timezoneOffsetMinutes.HasValue) body["timezoneOffsetMinutes"] = timezoneOffsetMinutes.Value;
            body["closed"] = closed;
            return CombatResponse(await RequestAsync("/members/api/ingest/combat", body, true, ct).ConfigureAwait(false), true);
        }
        public void Dispose() { client.Dispose(); }
    }
}
