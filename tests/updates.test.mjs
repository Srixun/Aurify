import assert from "node:assert/strict";
import { createHash, generateKeyPairSync, sign, constants } from "node:crypto";
import {
  appendFile,
  copyFile,
  mkdir,
  mkdtemp,
  readFile,
  readdir,
  rm,
  writeFile,
} from "node:fs/promises";
import { execFile, spawn } from "node:child_process";
import { setTimeout as delay } from "node:timers/promises";
import { promisify } from "node:util";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import {
  createUpdateEnvelope,
  createUpdatePublication,
} from "../tools/publish-update.mjs";
import { native, nativeAsync } from "./native-bridge.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const work = join(root, ".work", "native-tests");
const hostExecutable = join(work, "AurifyTest.exe");
const hash = (bytes) => createHash("sha256").update(bytes).digest("hex");
const { privateKey, publicKey } = generateKeyPairSync("rsa", {
  modulusLength: 3072,
});
const jwk = publicKey.export({ format: "jwk" });
const publicXml = `<RSAKeyValue><Modulus>${Buffer.from(jwk.n, "base64url").toString("base64")}</Modulus><Exponent>${Buffer.from(jwk.e, "base64url").toString("base64")}</Exponent></RSAKeyValue>`;
const pem = privateKey.export({ format: "pem", type: "pkcs8" });
const now = 1792454400000;
const executable = Buffer.from(
  "synthetic executable bytes for signature verification only",
);
const hashes = {
  "Aurify.exe": hash(executable),
  "addon/Aurify/Aurify.toc": hash("toc"),
  "addon/Aurify/Aurify.lua": hash("lua"),
};
const metadata = {
  schema: 1,
  product: "Aurify",
  version: "2.0.6",
  releaseId: hash(
    Object.entries(hashes)
      .map(([name, digest]) => `${name}\n${digest}\n`)
      .join(""),
  ),
  payloadSha256: hash(executable),
  payloadLength: executable.length,
  hashes,
  toolchain: {},
};
const publication = createUpdatePublication(
  metadata,
  executable,
  pem,
  publicXml,
  42,
  null,
  now,
  now + 7 * 86400000,
);
const envelope = publication.catalogEnvelope;
const release = publication.catalog.releases[0];
const request = {
  operation: "update",
  action: "verify",
  envelope,
  now,
  publicKey: publicXml,
};
function signedPayload(value) {
  const bytes = Buffer.from(
    typeof value === "string" ? value : JSON.stringify(value),
  );
  return JSON.stringify({
    payload: bytes.toString("base64"),
    signature: sign("sha256", bytes, {
      key: privateKey,
      padding: constants.RSA_PKCS1_PADDING,
    }).toString("base64"),
  });
}

test("same release skips installation and an accepted sequence cannot be retried or reused", () => {
  assert.equal(
    native({
      ...request,
      action: "decision",
      current: release.releaseId,
      installedSequence: 0,
    }),
    false,
  );
  assert.equal(
    native({
      ...request,
      action: "decision",
      current: "0".repeat(64),
      installedSequence: 41,
    }),
    true,
  );
  assert.equal(
    native({
      ...request,
      action: "decision",
      current: "0".repeat(64),
      installedSequence: 42,
      installedRelease: release.releaseId,
    }),
    false,
  );
  assert.throws(() =>
    native({
      ...request,
      action: "decision",
      installedSequence: 42,
      installedRelease: "0".repeat(64),
    }),
  );
});

test("native verifier accepts a signed catalog and rejects signature, expiry, and canonical-schema changes", () => {
  assert.deepEqual(native(request), {
    id: release.releaseId,
    version: "2.0.6",
    sequence: 42,
    length: executable.length,
    releases: 1,
  });
  const signed = JSON.parse(envelope);
  const payload = JSON.parse(
    Buffer.from(signed.payload, "base64").toString("utf8"),
  );
  assert.throws(() => native({ ...request, now: payload.expiresAt }));
  const signature = Buffer.from(signed.signature, "base64");
  signature[0] ^= 1;
  assert.throws(() =>
    native({
      ...request,
      envelope: JSON.stringify({
        ...signed,
        signature: signature.toString("base64"),
      }),
    }),
  );
  for (const change of [
    { schema: 3 },
    { channel: "preview" },
    { sequence: 9007199254740992 },
    { issuedAt: now + 300001 },
    { expiresAt: payload.issuedAt },
  ])
    assert.throws(() =>
      native({
        ...request,
        envelope: signedPayload({ ...payload, ...change }),
      }),
    );
  for (const change of [
    { version: "2.00.6" },
    { payloadPath: "https://other.example/update.exe" },
    { payloadLength: 67108865 },
    { payloadSha256: release.payloadSha256.toUpperCase() },
  ])
    assert.throws(() =>
      native({
        ...request,
        envelope: signedPayload({
          ...payload,
          releases: [{ ...payload.releases[0], ...change }],
        }),
      }),
    );
  const canonical = JSON.stringify(payload);
  assert.throws(() =>
    native({
      ...request,
      envelope: signedPayload(
        canonical.replace('"schema":2', '"schema":2,"schema":2'),
      ),
    }),
  );
  assert.throws(() =>
    native({
      ...request,
      envelope: signedPayload(
        canonical.replace('"sequence":42', '"sequence":4.2e1'),
      ),
    }),
  );
  assert.throws(() =>
    native({
      ...request,
      envelope: signedPayload(canonical.replace("}]}", "},]}")),
    }),
  );
});

test("latest release identity must still match the freshly verified catalog", () => {
  assert.equal(
    native({
      ...request,
      action: "verify-latest",
      current: "0".repeat(64),
      installedSequence: 41,
    }).id,
    release.releaseId,
  );
  for (const changed of [
    { candidateId: "0".repeat(64) },
    { candidateVersion: "2.0.5" },
    { installedSequence: 42 },
  ])
    assert.throws(() =>
      native({
        ...request,
        action: "verify-latest",
        current: "0".repeat(64),
        installedSequence: 41,
        ...changed,
      }),
    );
});

test("native download verification rejects tampering and truncated bytes", async (t) => {
  const directory = await mkdtemp(join(work, "update-integrity-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const file = join(directory, "candidate.exe");
  await writeFile(file, executable);
  assert.equal(native({ ...request, file }).length, executable.length);
  const tampered = Buffer.from(executable);
  tampered[0] ^= 1;
  await writeFile(file, tampered);
  assert.throws(() => native({ ...request, file }));
  await writeFile(file, executable.subarray(1));
  assert.throws(() => native({ ...request, file }));
});

test("publisher refuses mismatched artifacts and trust anchors", () => {
  assert.throws(() =>
    createUpdateEnvelope(
      { ...metadata, payloadSha256: hash("changed") },
      executable,
      pem,
      publicXml,
      43,
      now,
    ),
  );
  assert.throws(() =>
    createUpdateEnvelope(
      metadata,
      executable,
      pem,
      publicXml.replace(jwk.e, "invalid"),
      0,
      now,
    ),
  );
  const other = generateKeyPairSync("rsa", {
    modulusLength: 3072,
  }).privateKey.export({ format: "pem", type: "pkcs8" });
  assert.throws(() =>
    createUpdateEnvelope(metadata, executable, other, publicXml, 43, now),
  );
});

test("publication retains historical payloads but native updates accept only the latest release", async (t) => {
  const finalBytes = Buffer.concat([
    executable,
    Buffer.from("synthetic signature overlay"),
  ]);
  const next = createUpdatePublication(
    { ...metadata, version: "2.0.7" },
    finalBytes,
    pem,
    publicXml,
    43,
    envelope,
    now,
    now + 7 * 86400000,
  );
  assert.notEqual(next.release.releaseId, metadata.releaseId);
  assert.equal(next.release.payloadSha256, hash(finalBytes));
  assert.equal(next.release.payloadLength, finalBytes.length);
  const directory = await mkdtemp(join(work, "update-latest-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const file = join(directory, "candidate.exe");
  const latest = {
    ...request,
    envelope: next.catalogEnvelope,
    action: "latest",
    current: "0".repeat(64),
    installedSequence: 41,
  };
  assert.deepEqual(native(latest), {
    id: next.release.releaseId,
    version: "2.0.7",
    sequence: 43,
    length: finalBytes.length,
    releases: 2,
  });
  assert.equal(native({ ...latest, current: next.release.releaseId }), null);
  assert.equal(
    native({
      ...latest,
      installedSequence: 43,
      installedRelease: next.release.releaseId,
    }),
    null,
  );
  assert.throws(() =>
    native({
      ...latest,
      installedSequence: 43,
      installedRelease: release.releaseId,
    }),
  );
  await writeFile(file, executable);
  // The retained candidate has authentic metadata and intact bytes, and its
  // sequence is above the watermark; only its superseded status rejects it.
  assert.equal(
    native({ ...request, action: "verify-latest", installedSequence: 41, file })
      .id,
    release.releaseId,
  );
  assert.throws(() =>
    native({
      ...latest,
      action: "verify-latest",
      candidateId: release.releaseId,
      candidateVersion: release.version,
      file,
    }),
  );
  await writeFile(file, finalBytes);
  const candidate = {
    ...latest,
    action: "verify-latest",
    candidateId: next.release.releaseId,
    candidateVersion: next.release.version,
    file,
  };
  assert.equal(native(candidate).id, next.release.releaseId);
  assert.throws(() => native({ ...candidate, installedSequence: 43 }));
});

async function rollbackFixture(t) {
  // Leave room for the .NET Framework uninstall recovery copy's hash and temporary suffix.
  const directory = await mkdtemp(join(root, ".work", "update-"));
  const install = join(directory, "install");
  const startup = join(directory, "startup");
  const desktop = join(directory, "desktop");
  const game = join(directory, "game");
  await mkdir(startup, { recursive: true });
  await mkdir(desktop, { recursive: true });
  await mkdir(install, { recursive: true });
  await mkdir(game, { recursive: true });
  await copyFile(join(work, "SyntheticGame.exe"), join(game, "Wow.exe"));
  for (const pkg of [
    {
      dir: "ProfessionMaster",
      toc: "ProfessionMaster_Camelot.toc",
      title: "|cffDA8CFFProfession Master |c999999FFForever",
      version: "7.0.24",
    },
    {
      dir: "Overlord",
      toc: "Overlord.toc",
      title: "Overlord Forever",
      version: "2.0.4",
    },
  ]) {
    const destination = join(game, "Interface", "AddOns", pkg.dir);
    await mkdir(destination, { recursive: true });
    await writeFile(
      join(destination, pkg.toc),
      `## Interface: 16001\n## Title: ${pkg.title}\n## Version: ${pkg.version}\n`,
    );
  }
  const fixtureFiles = [
    "Aurify.exe",
    "addon/Aurify/Aurify.toc",
    "addon/Aurify/Aurify.lua",
  ];
  async function release(exe) {
    const content = await Promise.all(
      fixtureFiles.map((file) =>
        readFile(file === "Aurify.exe" ? exe : join(root, "companion", file)),
      ),
    );
    const digests = Object.fromEntries(
      fixtureFiles.map((file, index) => [file, hash(content[index])]),
    );
    const id = hash(
      fixtureFiles.map((file) => `${file}\n${digests[file]}\n`).join(""),
    );
    const path = join(install, "App", "releases", id);
    await mkdir(join(path, "addon", "Aurify"), { recursive: true });
    for (let index = 0; index < fixtureFiles.length; index++)
      await writeFile(join(path, fixtureFiles[index]), content[index]);
    await writeFile(
      join(path, ".aurify-release.json"),
      JSON.stringify({ Product: "Aurify", Format: 2, Id: id, Hashes: digests }),
    );
    return id;
  }
  const old = await release(hostExecutable);
  const candidate = await release(join(work, "SyntheticGame.exe")); // Exits immediately in --host mode; never owns the host mutex.
  native({
    operation: "storage",
    root: install,
    config: { gameDir: game, serverUrl: "https://127.0.0.1:1" },
    credentials: {
      deviceToken: "synthetic-update-device",
      deviceId: "synthetic-update-id",
      sourceKey: Buffer.alloc(32, 7).toString("base64"),
    },
    state: {
      snapshots: {
        [`activity:${"a".repeat(64)}`]: {
          rawHash: "b".repeat(64),
          payloadHash: "c".repeat(64),
        },
      },
    },
  });
  const state = {
    Product: "Aurify",
    Format: 1,
    Id: "0123456789abcdef0123456789abcdef",
    Root: install,
    Startup: startup,
    Desktop: desktop,
    Current: old,
    Releases: [old, candidate],
    UpdateSequence: 1,
    UpdateRelease: old,
  };
  const statePath = join(install, ".aurify-install.json");
  await writeFile(statePath, JSON.stringify(state));
  const preserved = {};
  for (const file of ["config.json", "config.state.json", "config.credentials"])
    preserved[file] = await readFile(join(install, file));
  t.after(async () => {
    await nativeAsync({
      operation: "update",
      action: "stop",
      root: install,
      startup,
      desktop,
    });
    await rm(directory, {
      recursive: true,
      force: true,
      maxRetries: 20,
      retryDelay: 100,
    });
  });
  return {
    install,
    startup,
    desktop,
    old,
    candidate,
    state,
    statePath,
    preserved,
  };
}

function installArguments(fixture) {
  return [
    "--install-root",
    fixture.install,
    "--startup-dir",
    fixture.startup,
    "--desktop-dir",
    fixture.desktop,
  ];
}

function shortcutArguments(fixture) {
  return installArguments(fixture)
    .map((value) => `"${value}"`)
    .join(" ");
}

function installedExecutable(fixture, release = fixture.old) {
  return join(fixture.install, "App", "releases", release, "Aurify.exe");
}

function launchFixtureHost(fixture) {
  return spawn(
    installedExecutable(fixture),
    ["--host", "--background", ...installArguments(fixture)],
    { windowsHide: true, stdio: "ignore" },
  );
}

function waitForChildExit(child, timeout = 15000) {
  if (child.exitCode !== null) return Promise.resolve(child.exitCode);
  return new Promise((resolveExit, rejectExit) => {
    const timer = globalThis.setTimeout(() => {
      child.removeListener("exit", onExit);
      child.removeListener("error", onError);
      rejectExit(new Error("Child process did not exit before the deadline"));
    }, timeout);
    const onExit = (code) => {
      globalThis.clearTimeout(timer);
      child.removeListener("error", onError);
      resolveExit(code);
    };
    const onError = (error) => {
      globalThis.clearTimeout(timer);
      child.removeListener("exit", onExit);
      rejectExit(error);
    };
    child.once("exit", onExit);
    child.once("error", onError);
  });
}

test("fresh installation creates an owned foreground desktop shortcut with its icon and isolated bindings", async (t) => {
  const fixture = await rollbackFixture(t);
  await rm(fixture.statePath);
  await promisify(execFile)(
    hostExecutable,
    ["--background", ...installArguments(fixture)],
    {
      windowsHide: true,
      timeout: 30000,
    },
  );
  const state = JSON.parse(await readFile(fixture.statePath, "utf8"));
  const links = await nativeAsync({
    operation: "update",
    action: "shortcuts",
    root: fixture.install,
    startup: fixture.startup,
    desktop: fixture.desktop,
  });
  const executable = installedExecutable(fixture, state.Current);
  assert.equal(state.Desktop, fixture.desktop);
  assert.equal(
    resolve(links.Desktop.target).toLowerCase(),
    executable.toLowerCase(),
  );
  assert.equal(links.Desktop.arguments, shortcutArguments(fixture));
  assert.equal(links.Desktop.description, `Aurify installation ${state.Id}`);
  assert.equal(
    resolve(links.Desktop.workingDirectory).toLowerCase(),
    dirname(executable).toLowerCase(),
  );
  assert.equal(
    links.Desktop.icon.toLowerCase(),
    `${executable},0`.toLowerCase(),
  );
  assert.equal(links.Desktop.windowStyle, "1");
  assert.equal(
    links.Startup.arguments,
    `"--background" ${shortcutArguments(fixture)}`,
  );
  assert.equal(
    links.Uninstall.arguments,
    `"--uninstall" ${shortcutArguments(fixture)}`,
  );
});

test("legacy receipts require an explicit isolated desktop and migrate exact owned links without losing old-updater launches", async (t) => {
  const fixture = await rollbackFixture(t);
  delete fixture.state.Desktop;
  await writeFile(fixture.statePath, JSON.stringify(fixture.state));
  const request = {
    operation: "update",
    root: fixture.install,
    startup: fixture.startup,
    desktop: fixture.desktop,
  };
  const legacyLocations = installArguments(fixture)
    .slice(0, 4)
    .map((value) => `"${value}"`)
    .join(" ");
  await assert.rejects(
    nativeAsync({ ...request, action: "parse", desktop: undefined }),
  );
  assert.equal(
    (await nativeAsync({ ...request, action: "parse" })).desktop,
    fixture.desktop,
  );
  for (const [kind, mode] of [
    ["Startup", "--background"],
    ["Uninstall", "--uninstall"],
  ])
    await nativeAsync({
      ...request,
      action: "write-shortcut",
      kind,
      target: installedExecutable(fixture),
      arguments: `"${mode}" ${legacyLocations}`,
      description: `Aurify installation ${fixture.state.Id}`,
    });
  await promisify(execFile)(
    hostExecutable,
    ["--background", ...installArguments(fixture)],
    {
      windowsHide: true,
      timeout: 30000,
    },
  );
  const state = JSON.parse(await readFile(fixture.statePath, "utf8"));
  assert.equal(state.Desktop, fixture.desktop);
  const links = await nativeAsync({ ...request, action: "shortcuts" });
  assert.equal(
    links.Startup.arguments,
    `"--background" ${shortcutArguments(fixture)}`,
  );
  assert.equal(
    links.Uninstall.arguments,
    `"--uninstall" ${shortcutArguments(fixture)}`,
  );
  assert.equal(links.Desktop.arguments, shortcutArguments(fixture));
  assert.equal(
    (await nativeAsync({ ...request, action: "parse", desktop: undefined }))
      .desktop,
    fixture.desktop,
  );
  const control = await readFile(join(fixture.install, "config.control.json"));
  await promisify(execFile)(
    hostExecutable,
    ["--background", ...installArguments(fixture).slice(0, 4)],
    {
      windowsHide: true,
      timeout: 30000,
    },
  );
  assert.deepEqual(
    await readFile(join(fixture.install, "config.control.json")),
    control,
  );
  const before = await readFile(fixture.statePath);
  await assert.rejects(
    promisify(execFile)(
      hostExecutable,
      [
        "--background",
        ...installArguments(fixture).slice(0, 4),
        "--desktop-dir",
        join(dirname(fixture.install), "other-desktop"),
      ],
      { windowsHide: true, timeout: 30000 },
    ),
    (error) => error.code === 1,
  );
  assert.deepEqual(await readFile(fixture.statePath), before);
});

for (const conflict of ["manual", "wrong-binding"])
  test(`desktop ${conflict} collision preserves the link, receipt and running host before switching releases`, async (t) => {
    const fixture = await rollbackFixture(t);
    const request = {
      operation: "update",
      root: fixture.install,
      startup: fixture.startup,
      desktop: fixture.desktop,
    };
    await nativeAsync({
      ...request,
      action: "activate",
      candidate: fixture.old,
      sequence: 1,
    });
    await nativeAsync({
      ...request,
      action: "write-shortcut",
      kind: "Desktop",
      target: installedExecutable(fixture),
      arguments:
        conflict === "manual"
          ? ""
          : shortcutArguments({
              ...fixture,
              desktop: join(dirname(fixture.install), "other-desktop"),
            }),
      description:
        conflict === "manual" ? "" : `Aurify installation ${fixture.state.Id}`,
    });
    const paths = [
      fixture.statePath,
      join(fixture.install, "config.control.json"),
      join(fixture.startup, "Aurify.lnk"),
      join(fixture.desktop, "Aurify.lnk"),
      join(fixture.install, "Uninstall Aurify.lnk"),
    ];
    const before = await Promise.all(paths.map((path) => readFile(path)));
    const result = await nativeAsync({
      ...request,
      action: "activate",
      candidate: fixture.candidate,
      sequence: 2,
    });
    assert.equal(result.failure, "InvalidOperationException");
    assert.equal(result.current, fixture.old);
    assert.equal(result.sequence, 1);
    assert.equal(result.ready, true);
    for (let index = 0; index < paths.length; index++)
      assert.deepEqual(await readFile(paths[index]), before[index]);
  });

test("uninstall recovery restores owned desktop links and never removes or overwrites a replacement", async (t) => {
  const fixture = await rollbackFixture(t);
  const request = {
    operation: "update",
    root: fixture.install,
    startup: fixture.startup,
    desktop: fixture.desktop,
  };
  await nativeAsync({ ...request, action: "register" });
  const paths = [
    join(fixture.startup, "Aurify.lnk"),
    join(fixture.install, "Uninstall Aurify.lnk"),
    join(fixture.desktop, "Aurify.lnk"),
  ];
  const before = await Promise.all(paths.map((path) => readFile(path)));
  const recovery = await nativeAsync({ ...request, action: "prepare-removal" });
  assert.deepEqual(await readFile(join(recovery, "desktop.lnk")), before[2]);
  const removed = await nativeAsync({ ...request, action: "remove-shortcuts" });
  assert.deepEqual(removed, { Startup: null, Uninstall: null, Desktop: null });
  await nativeAsync({ ...request, action: "restore-removal" });
  for (let index = 0; index < paths.length; index++)
    assert.deepEqual(await readFile(paths[index]), before[index]);
  await nativeAsync({
    ...request,
    action: "write-shortcut",
    kind: "Desktop",
    target: installedExecutable(fixture),
    arguments: "",
    description: "Manual shortcut",
  });
  const replacement = await readFile(paths[2]);
  await nativeAsync({ ...request, action: "remove-shortcuts" });
  assert.deepEqual(await readFile(paths[2]), replacement);
  await nativeAsync({ ...request, action: "restore-removal" });
  assert.deepEqual(await readFile(paths[2]), replacement);
  for (const [file, bytes] of Object.entries(fixture.preserved))
    assert.deepEqual(await readFile(join(fixture.install, file)), bytes);
});

for (const action of ["activate", "recover"])
  test(`native ${action} restores prior readiness, startup, desktop and saved state`, async (t) => {
    const fixture = await rollbackFixture(t);
    await nativeAsync({
      operation: "update",
      action: "register",
      root: fixture.install,
      startup: fixture.startup,
      desktop: fixture.desktop,
    });
    if (action === "recover")
      await writeFile(
        fixture.statePath,
        JSON.stringify({
          ...fixture.state,
          Current: fixture.candidate,
          Previous: fixture.old,
          UpdateSequence: 2,
          UpdateRelease: fixture.candidate,
        }),
      );
    if (action === "recover") {
      const candidateLinks = await nativeAsync({
        operation: "update",
        action: "register",
        root: fixture.install,
        startup: fixture.startup,
        desktop: fixture.desktop,
      });
      assert.equal(
        resolve(candidateLinks.Desktop.target).toLowerCase(),
        installedExecutable(fixture, fixture.candidate).toLowerCase(),
      );
    }
    const result = await nativeAsync({
      operation: "update",
      action,
      root: fixture.install,
      startup: fixture.startup,
      desktop: fixture.desktop,
      candidate: fixture.candidate,
      sequence: 2,
    });
    assert.equal(result.current, fixture.old);
    assert.equal(result.previous, null);
    assert.equal(result.sequence, 2);
    assert.equal(result.ready, true);
    for (const kind of ["Startup", "Desktop", "Uninstall"])
      assert.equal(
        resolve(result.shortcuts[kind].target).toLowerCase(),
        join(
          fixture.install,
          "App",
          "releases",
          fixture.old,
          "Aurify.exe",
        ).toLowerCase(),
      );
    if (action === "activate")
      assert.equal(result.failure, "InvalidOperationException");
    await nativeAsync({
      operation: "update",
      action: "stop",
      root: fixture.install,
      startup: fixture.startup,
      desktop: fixture.desktop,
    });
    for (const [file, bytes] of Object.entries(fixture.preserved))
      assert.deepEqual(await readFile(join(fixture.install, file)), bytes);
  });

test("direct unsigned replacement cannot bypass an accepted sequence or stop its ready host", async (t) => {
  const fixture = await rollbackFixture(t);
  await nativeAsync({
    operation: "update",
    action: "activate",
    root: fixture.install,
    startup: fixture.startup,
    desktop: fixture.desktop,
    candidate: fixture.old,
    sequence: 1,
  });
  const paths = [
    fixture.statePath,
    join(fixture.install, "config.control.json"),
    join(fixture.startup, "Aurify.lnk"),
    join(fixture.desktop, "Aurify.lnk"),
  ];
  const before = await Promise.all(paths.map((path) => readFile(path)));
  const releases = join(fixture.install, "App", "releases");
  const originalReleases = (await readdir(releases)).sort();
  const direct = join(
    dirname(fixture.install),
    "unsigned-different-release.exe",
  );
  await copyFile(hostExecutable, direct);
  // A PE overlay preserves runnable installer code/resources but changes its
  // full executable hash, hence the actual embedded release identity.
  await appendFile(direct, "\nsynthetic different unsigned release\n");
  const args = [
    "--background",
    "--install-root",
    fixture.install,
    "--startup-dir",
    fixture.startup,
    "--desktop-dir",
    fixture.desktop,
  ];
  await assert.rejects(
    promisify(execFile)(direct, args, { windowsHide: true, timeout: 30000 }),
    (error) => error.code === 1,
  );
  for (let index = 0; index < paths.length; index++)
    assert.deepEqual(await readFile(paths[index]), before[index]);
  assert.deepEqual((await readdir(releases)).sort(), originalReleases);
  assert.equal(
    await nativeAsync({
      operation: "update",
      action: "readiness",
      root: fixture.install,
      startup: fixture.startup,
      desktop: fixture.desktop,
    }),
    true,
  );
  const linkRequest = {
    operation: "update",
    root: fixture.install,
    startup: fixture.startup,
    desktop: fixture.desktop,
  };
  const links = await nativeAsync({ ...linkRequest, action: "shortcuts" });
  await nativeAsync({
    ...linkRequest,
    action: "write-shortcut",
    kind: "Desktop",
    target: installedExecutable(fixture, fixture.candidate),
    arguments: links.Desktop.arguments,
    description: links.Desktop.description,
  });
  // The unmodified current payload remains a permitted direct repair/relaunch.
  await promisify(execFile)(hostExecutable, args, {
    windowsHide: true,
    timeout: 30000,
  });
  assert.equal(
    await nativeAsync({
      operation: "update",
      action: "readiness",
      root: fixture.install,
      startup: fixture.startup,
      desktop: fixture.desktop,
    }),
    true,
  );
  assert.deepEqual(await readFile(paths[1]), before[1]);
  const after = JSON.parse(await readFile(fixture.statePath, "utf8"));
  assert.equal(after.Current, fixture.old);
  assert.equal(after.UpdateSequence, 1);
  assert.equal(after.UpdateRelease, fixture.old);
  const repaired = await nativeAsync({ ...linkRequest, action: "shortcuts" });
  assert.equal(
    resolve(repaired.Desktop.target).toLowerCase(),
    installedExecutable(fixture).toLowerCase(),
  );
  assert.equal(repaired.Desktop.arguments, shortcutArguments(fixture));
});

test("native runtime admits one process per Windows user across installation roots", async (t) => {
  const first = await rollbackFixture(t);
  const second = await rollbackFixture(t);
  const owner = launchFixtureHost(first);
  t.after(() => {
    if (owner.exitCode === null) owner.kill();
  });
  let ready = false;
  for (let attempt = 0; attempt < 20 && !ready; attempt++) {
    ready = await nativeAsync({
      operation: "update",
      action: "readiness",
      root: first.install,
      startup: first.startup,
      desktop: first.desktop,
    });
    if (!ready) await delay(100);
  }
  assert.equal(ready, true);

  const duplicate = launchFixtureHost(second);
  assert.equal(await waitForChildExit(duplicate), 0);
  assert.equal(owner.exitCode, null);
  assert.equal(
    await nativeAsync({
      operation: "update",
      action: "readiness",
      root: second.install,
      startup: second.startup,
      desktop: second.desktop,
    }),
    true,
  );

  await nativeAsync({
    operation: "update",
    action: "stop",
    root: second.install,
    startup: second.startup,
    desktop: second.desktop,
  });
  assert.equal(await waitForChildExit(owner), 0);
});

for (const variant of [
  "absent",
  "valid",
  "schema",
  "dpapi",
  "forever-dpapi",
  "forever-state",
])
  test(`native host readiness respects ${variant} protected persisted data`, async (t) => {
    const fixture = await rollbackFixture(t);
    let protectedPath = join(fixture.install, "config.credentials");
    if (variant === "absent") await rm(protectedPath);
    else if (variant === "schema")
      await writeFile(protectedPath, '{"version":99,"protected":"AAAA"}');
    else if (variant === "dpapi")
      await writeFile(protectedPath, '{"version":1,"protected":"AAAA"}');
    else if (variant === "forever-dpapi") {
      protectedPath = join(fixture.install, "config.forever.credentials");
      await writeFile(protectedPath, '{"version":1,"protected":"AAAA"}');
    } else if (variant === "forever-state") {
      protectedPath = join(fixture.install, "config.forever.state.json");
      await writeFile(protectedPath, "{invalid checkpoint");
    }
    const protectedBytes =
      variant === "absent" ? null : await readFile(protectedPath);
    const host = spawn(
      join(fixture.install, "App", "releases", fixture.old, "Aurify.exe"),
      [
        "--host",
        "--background",
        "--install-root",
        fixture.install,
        "--startup-dir",
        fixture.startup,
        "--desktop-dir",
        fixture.desktop,
      ],
      { windowsHide: true, stdio: ["ignore", "pipe", "pipe"] },
    );
    let launchError;
    host.on("error", (error) => {
      launchError = error;
    });
    let processOutput = "";
    const capture = (chunk) => {
      processOutput = (processOutput + chunk.toString()).slice(-16384);
    };
    host.stdout.on("data", capture);
    host.stderr.on("data", capture);
    async function diagnostics() {
      let launcherLog;
      try {
        launcherLog = (
          await readFile(join(fixture.install, "launcher.log"), "utf8")
        ).slice(-16384);
      } catch (error) {
        launcherLog = `<unavailable:${error.code}>`;
      }
      return JSON.stringify({
        variant,
        exitCode: host.exitCode,
        signalCode: host.signalCode,
        launchError: launchError?.message,
        launcherLog,
        processOutput,
      });
    }
    const healthy = variant === "absent" || variant === "valid";
    let ready = false;
    // Exercise the actual private pipe over several controller turns, not the
    // flag expression. A credential-load regression would acknowledge ping.
    for (let attempt = 0; attempt < 8; attempt++) {
      ready = await nativeAsync({
        operation: "update",
        action: "readiness",
        root: fixture.install,
        startup: fixture.startup,
        desktop: fixture.desktop,
      });
      if (ready) break;
      assert.equal(
        launchError,
        undefined,
        launchError ? await diagnostics() : undefined,
      );
      assert.equal(
        host.exitCode,
        null,
        host.exitCode !== null ? await diagnostics() : undefined,
      );
      await delay(500);
    }
    assert.equal(
      ready,
      healthy,
      ready !== healthy ? await diagnostics() : undefined,
    );
    await nativeAsync({
      operation: "update",
      action: "stop",
      root: fixture.install,
      startup: fixture.startup,
      desktop: fixture.desktop,
    });
    if (protectedBytes)
      assert.deepEqual(await readFile(protectedPath), protectedBytes);
    else
      assert.equal(
        (await readdir(fixture.install)).includes("config.credentials"),
        false,
      );
  });
