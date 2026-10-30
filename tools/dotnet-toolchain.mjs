import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { access, readFile } from "node:fs/promises";
import { dirname, join } from "node:path";

export const dotnetSdkVersion = "8.0.427";

const frameworkReferences = [
  "mscorlib.dll",
  "System.dll",
  "System.Core.dll",
  "Microsoft.CSharp.dll",
  "System.Windows.Forms.dll",
  "System.Web.Extensions.dll",
  "System.IO.Compression.dll",
  "System.IO.Compression.FileSystem.dll",
  "System.Net.Http.dll",
  "System.Security.dll",
  "System.Management.dll",
  "System.Drawing.dll",
];

async function fingerprint(path) {
  const bytes = await readFile(path);
  return {
    sha256: createHash("sha256").update(bytes).digest("hex"),
    bytes: bytes.length,
  };
}

export async function resolveDotnetToolchain() {
  const windows = process.env.SystemRoot || process.env.WINDIR;
  if (!windows) throw new Error("The Windows directory is unavailable");

  const sdkRows = execFileSync("dotnet", ["--list-sdks"], {
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: true,
  })
    .split(/\r?\n/)
    .map((line) => /^(\S+) \[(.+)\]$/.exec(line))
    .filter(Boolean)
    .map((match) => ({ version: match[1], root: match[2] }));
  const selected = sdkRows.filter(
    (candidate) => candidate.version === dotnetSdkVersion,
  );
  if (selected.length !== 1)
    throw new Error(
      `Exactly one .NET SDK ${dotnetSdkVersion} installation is required`,
    );

  const sdkRoot = join(selected[0].root, dotnetSdkVersion);
  const host = join(dirname(selected[0].root), "dotnet.exe");
  const compiler = join(sdkRoot, "Roslyn", "bincore", "csc.dll");
  const frameworkRoot = join(
    windows,
    "Microsoft.NET",
    "Framework64",
    "v4.0.30319",
  );
  const references = frameworkReferences.map((name) => ({
    name,
    path: join(frameworkRoot, name),
  }));
  for (const path of [host, compiler, ...references.map((entry) => entry.path)])
    await access(path);

  const artifacts = Object.fromEntries(
    await Promise.all(
      [
        ["dotnet.exe", host],
        [`sdk/${dotnetSdkVersion}/Roslyn/bincore/csc.dll`, compiler],
        ...references.map((entry) => [
          `framework/v4.0.30319/${entry.name}`,
          entry.path,
        ]),
      ].map(async ([name, path]) => [name, await fingerprint(path)]),
    ),
  );
  return {
    host,
    compiler,
    references: references.map((entry) => entry.path),
    provenance: {
      sdkVersion: dotnetSdkVersion,
      deterministic: true,
      artifacts,
    },
  };
}
