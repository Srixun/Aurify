import { cp, mkdir, readdir, rm } from "node:fs/promises";
import { execFileSync } from "node:child_process";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const work = join(root, ".work", "native-tests");
const testRoot = join(work, "aurify-test");
const testPayload = join(testRoot, "Aurify.Payload.zip");
const testWorker = join(testRoot, "Aurify.Uninstall.exe");
const testHost = join(work, "AurifyTest.exe");
const windows = process.env.SystemRoot || process.env.WINDIR;
if (process.platform !== "win32" || !windows)
  throw new Error("Aurify native integration tests require Windows");
await mkdir(work, { recursive: true });
await rm(join(work, "addon"), { recursive: true, force: true });
await cp(join(root, "companion", "addon"), join(work, "addon"), {
  recursive: true,
});
const compiler = join(
  windows,
  "Microsoft.NET",
  "Framework64",
  "v4.0.30319",
  "csc.exe",
);
const buildEnvironment = { ...process.env, TEMP: work, TMP: work };
await rm(testRoot, { recursive: true, force: true });
await rm(testHost, { force: true });
await mkdir(testRoot, { recursive: true });
const sourceRoot = join(root, "companion", "native");
const sources = (await readdir(sourceRoot))
  .filter((name) => name.endsWith(".cs"))
  .sort()
  .map((name) => join(sourceRoot, name));
const references = [
  "System.Windows.Forms",
  "System.Web.Extensions",
  "System.IO.Compression",
  "System.IO.Compression.FileSystem",
  "System.Net.Http",
  "System.Security",
  "System.Management",
  "System.Drawing",
];
const compilerArgs = [
  "/nologo",
  "/platform:anycpu",
  "/optimize+",
  "/utf8output",
  ...references.map((name) => `/reference:${name}.dll`),
];
execFileSync(
  compiler,
  [
    "/nologo",
    "/target:exe",
    "/platform:anycpu",
    "/optimize+",
    "/utf8output",
    "/define:AURIFY_TEST",
    "/main:AurifyNativeProbe",
    `/out:${join(work, "NativeProbe.exe")}`,
    ...references.map((name) => `/reference:${name}.dll`),
    `/resource:${join(root, "companion", "update-public-key.xml")},Aurify.Update.PublicKey`,
    join(root, "companion", "launcher.cs"),
    ...sources,
    join(root, "tests", "native", "NativeProbe.cs"),
    join(root, "tests", "native", "ForeverProbe.cs"),
    join(root, "tests", "native", "UpdateProbe.cs"),
    join(root, "tests", "native", "CombatEfficiencyProbe.cs"),
    join(root, "tests", "native", "SnapshotEfficiencyProbe.cs"),
    join(root, "tests", "native", "HostEfficiencyProbe.cs"),
  ],
  {
    cwd: root,
    env: buildEnvironment,
    stdio: "pipe",
    encoding: "utf8",
    windowsHide: true,
  },
);
const powershell = join(
  windows,
  "System32",
  "WindowsPowerShell",
  "v1.0",
  "powershell.exe",
);
const payloadEntries = ["addon/Aurify/Aurify.toc", "addon/Aurify/Aurify.lua"];
execFileSync(
  powershell,
  [
    "-NoLogo",
    "-NoProfile",
    "-NonInteractive",
    "-Command",
    "$ErrorActionPreference='Stop'; Add-Type -AssemblyName System.IO.Compression; Add-Type -AssemblyName System.IO.Compression.FileSystem; $zip=[IO.Compression.ZipFile]::Open($env:AURIFY_TEST_PAYLOAD,[IO.Compression.ZipArchiveMode]::Create); try { foreach($entry in ($env:AURIFY_TEST_PAYLOAD_ENTRIES|ConvertFrom-Json)){ [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $env:AURIFY_TEST_SOURCE $entry),$entry,[IO.Compression.CompressionLevel]::Optimal)|Out-Null } } finally { $zip.Dispose() }",
  ],
  {
    cwd: root,
    env: {
      ...buildEnvironment,
      AURIFY_TEST_PAYLOAD: testPayload,
      AURIFY_TEST_PAYLOAD_ENTRIES: JSON.stringify(payloadEntries),
      AURIFY_TEST_SOURCE: join(root, "companion"),
    },
    stdio: "pipe",
    encoding: "utf8",
    windowsHide: true,
  },
);
execFileSync(
  compiler,
  [
    ...compilerArgs,
    "/target:winexe",
    "/define:UNINSTALL_WORKER,AURIFY_TEST",
    `/out:${testWorker}`,
    join(root, "companion", "launcher.cs"),
  ],
  {
    cwd: root,
    env: buildEnvironment,
    stdio: "pipe",
    encoding: "utf8",
    windowsHide: true,
  },
);
execFileSync(
  compiler,
  [
    ...compilerArgs,
    "/target:winexe",
    "/define:AURIFY_TEST",
    `/win32icon:${join(root, "companion", "branding", "aurify.ico")}`,
    `/win32manifest:${join(root, "companion", "app.manifest")}`,
    `/out:${testHost}`,
    `/resource:${testPayload},Aurify.Payload.zip`,
    `/resource:${testWorker},Aurify.Uninstall.exe`,
    `/resource:${join(root, "companion", "third-party", "UNICODE-LICENSE")},Aurify.Unicode.License`,
    `/resource:${join(root, "companion", "update-public-key.xml")},Aurify.Update.PublicKey`,
    join(root, "companion", "launcher.cs"),
    ...sources,
  ],
  {
    cwd: root,
    env: buildEnvironment,
    stdio: "pipe",
    encoding: "utf8",
    windowsHide: true,
  },
);
execFileSync(
  compiler,
  [
    "/nologo",
    "/target:winexe",
    `/out:${join(work, "SyntheticGame.exe")}`,
    join(root, "tests", "native", "SyntheticGame.cs"),
  ],
  {
    cwd: root,
    env: buildEnvironment,
    stdio: "pipe",
    encoding: "utf8",
    windowsHide: true,
  },
);
console.log("Built native behavioral probe and isolated Aurify host");
