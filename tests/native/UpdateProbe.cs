using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using AurifyClient;

internal static class UpdateProbe
{
    private static object Invoke(string method, params object[] args)
    {
        return typeof(Aurify).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
    }

    private static readonly string[] ShortcutKinds = { "Startup", "Uninstall", "Desktop" };

    private static string[] ShortcutPaths(string root, string startup, string desktop)
    {
        return new[] { Path.Combine(startup, "Aurify.lnk"), Path.Combine(root, "Uninstall Aurify.lnk"), Path.Combine(desktop, "Aurify.lnk") };
    }

    private static Dictionary<string, object> ReadShortcuts(string[] paths)
    {
        Dictionary<string, object> result = new Dictionary<string, object>();
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
        try
        {
            for (int i = 0; i < paths.Length; i++)
            {
                if (!File.Exists(paths[i])) { result[ShortcutKinds[i]] = null; continue; }
                object shortcut = Invoke("Shortcut", shell, paths[i]);
                try
                {
                    result[ShortcutKinds[i]] = new {
                        target = (string)Invoke("Property", shortcut, "TargetPath"),
                        arguments = (string)Invoke("Property", shortcut, "Arguments"),
                        description = (string)Invoke("Property", shortcut, "Description"),
                        workingDirectory = (string)Invoke("Property", shortcut, "WorkingDirectory"),
                        icon = (string)Invoke("Property", shortcut, "IconLocation"),
                        windowStyle = (string)Invoke("Property", shortcut, "WindowStyle")
                    };
                }
                finally { Marshal.FinalReleaseComObject(shortcut); }
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        return result;
    }

    internal static object Run(Dictionary<string, object> request)
    {
        string action = Json.Str(Json.Get(request, "action"));
        if (action == "verify" || action == "decision" || action == "latest" || action == "verify-latest")
        {
            UpdateCatalog catalog = NativeUpdate.VerifyCatalog(Json.Str(Json.Get(request, "envelope")),
                Json.Int(Json.Get(request, "now")), Json.Str(Json.Get(request, "publicKey")));
            UpdateRelease release = catalog.Releases[0];
            if (action == "decision" || action == "latest")
            {
                release = NativeUpdate.Latest(catalog, new Aurify.Installation {
                    Current = Json.Str(Json.Get(request, "current")), UpdateSequence = Json.Int(Json.Get(request, "installedSequence")),
                    UpdateRelease = Json.Str(Json.Get(request, "installedRelease"))
                });
                if (action == "decision") return release != null;
                if (release == null) return null;
            }
            if (action == "verify-latest")
                release = NativeUpdate.VerifyLatest(Json.Str(Json.Get(request, "envelope")),
                    Json.Int(Json.Get(request, "now")), Json.Int(Json.Get(request, "installedSequence")),
                    Json.Str(Json.Get(request, "publicKey")),
                    Json.Str(Json.Get(request, "candidateId")) ?? release.Id,
                    Json.Str(Json.Get(request, "candidateVersion")) ?? release.Version);
            string path = Json.Str(Json.Get(request, "file"));
            if (path != null) NativeUpdate.VerifyFile(path, release);
            return new { id = release.Id, version = release.Version, sequence = release.Sequence,
                length = release.Length, releases = catalog.Releases.Count };
        }
        string root = Json.Str(Json.Get(request, "root"));
        string startup = Json.Str(Json.Get(request, "startup"));
        string desktop = Json.Str(Json.Get(request, "desktop"));
        Type optionsType = typeof(Aurify).GetNestedType("Options", BindingFlags.NonPublic);
        if (action == "parse")
        {
            List<string> args = new List<string> { "--install-root", root, "--startup-dir", startup };
            if (desktop != null) { args.Add("--desktop-dir"); args.Add(desktop); }
            object parsed = Invoke("Parse", (object)args.ToArray());
            return new { root = optionsType.GetField("Root").GetValue(parsed), startup = optionsType.GetField("Startup").GetValue(parsed),
                desktop = optionsType.GetField("Desktop").GetValue(parsed) };
        }
        // Every mutating probe must name its fixture Desktop, never the user's folder.
        if (String.IsNullOrEmpty(desktop)) throw new InvalidOperationException("A fixture Desktop is required.");
        object options = Activator.CreateInstance(optionsType, true);
        optionsType.GetField("Root").SetValue(options, root);
        optionsType.GetField("Startup").SetValue(options, startup);
        optionsType.GetField("Desktop").SetValue(options, desktop);
        optionsType.GetField("Background").SetValue(options, true);
        if (action == "stop") { Invoke("StopHost", options); return true; }
        if (action == "readiness")
        {
            try { Invoke("PipeCommand", options, "ping\n", 5000); return true; }
            catch (TargetInvocationException) { return false; }
        }
        string[] paths = ShortcutPaths(root, startup, desktop);
        if (action == "shortcuts") return ReadShortcuts(paths);
        string failure = null;
        using ((IDisposable)Invoke("Lock", options))
        {
            Aurify.Installation state = (Aurify.Installation)Invoke("LoadState", root, true);
            Invoke("BindInstallation", options, state);
            if (action == "write-shortcut")
            {
                int index = Array.IndexOf(ShortcutKinds, Json.Str(Json.Get(request, "kind")));
                if (index < 0) throw new InvalidOperationException("Unknown shortcut kind.");
                object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(paths[index]));
                    object shortcut = Invoke("Shortcut", shell, paths[index]);
                    try
                    {
                        Invoke("SetProperty", shortcut, "TargetPath", Json.Str(Json.Get(request, "target")));
                        Invoke("SetProperty", shortcut, "Arguments", Json.Str(Json.Get(request, "arguments")));
                        Invoke("SetProperty", shortcut, "Description", Json.Str(Json.Get(request, "description")));
                        shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                    }
                    finally { Marshal.FinalReleaseComObject(shortcut); }
                }
                finally { Marshal.FinalReleaseComObject(shell); }
                return ReadShortcuts(paths);
            }
            if (action == "register") { Invoke("RegisterShortcuts", options, state); return ReadShortcuts(paths); }
            if (action == "prepare-removal")
            {
                state.RemoveToken = Guid.NewGuid().ToString("N");
                state.RemoveWorker = Path.Combine(Path.GetDirectoryName(root), ".aurify-remove-" + state.RemoveToken, "Aurify.exe");
                Invoke("SaveState", options, state);
                return Invoke("PrepareRecovery", options, state);
            }
            if (action == "remove-shortcuts")
            {
                Type kindType = typeof(Aurify).GetNestedType("ShortcutKind", BindingFlags.NonPublic);
                object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                try
                {
                    for (int i = 0; i < paths.Length; i++) Invoke("RemoveShortcut", shell, paths[i], state, Enum.Parse(kindType, ShortcutKinds[i]));
                }
                finally { Marshal.FinalReleaseComObject(shell); }
                return ReadShortcuts(paths);
            }
            if (action == "restore-removal") { Invoke("RestoreRecovery", options, state); return ReadShortcuts(paths); }
            if (action == "recover") Invoke("RestorePrevious", options, state);
            else if (action == "activate")
            {
                try { Invoke("ActivateRelease", options, state, Json.Str(Json.Get(request, "candidate")), Json.Int(Json.Get(request, "sequence"))); }
                catch (TargetInvocationException error) { failure = error.InnerException.GetType().Name; }
            }
            else throw new InvalidOperationException("Unknown update probe action.");
            state = (Aurify.Installation)Invoke("LoadState", root, true);
            Invoke("TargetPipeCommand", options, Path.Combine(root, "App", "releases", state.Current, "Aurify.exe"), "ping\n", 5000);
            return new { current = state.Current, previous = state.Previous, sequence = state.UpdateSequence,
                shortcuts = ReadShortcuts(paths), ready = true, failure = failure };
        }
    }
}
