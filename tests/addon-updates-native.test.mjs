import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import {
  copyFile,
  mkdir,
  mkdtemp,
  readFile,
  readdir,
  rm,
  writeFile,
} from "node:fs/promises";
import { createServer } from "node:http";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { crc32 } from "node:zlib";
import { nativeAsync } from "./native-bridge.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const work = join(root, ".work", "native-tests");
const token = "U".repeat(43);
const checkedAt = 1_798_000_000_000;
const updatesPath = "/members/api/aurify/addon-updates";
const catalog = {
  professions: {
    directory: "ProfessionMaster",
    toc: "ProfessionMaster_Camelot.toc",
    title: "|cffDA8CFFProfession Master |c999999FFForever",
    version: "7.0.30",
  },
  overlord: {
    directory: "Overlord",
    toc: "Overlord.toc",
    title: "Overlord Forever",
    version: "2.0.8",
  },
};

function toc(key, overrides = {}) {
  const info = { ...catalog[key], interface: 16001, ...overrides };
  return `## Title: ${info.title}\n## Version: ${info.version}\n## Interface: ${info.interface}\nCore.lua\n`;
}

// Real stored ZIP entries with CRCs; ApplyAsync performs the native extraction.
function syntheticZip(files) {
  const localParts = [];
  const centralParts = [];
  let offset = 0;
  for (const [path, content] of Object.entries(files)) {
    const name = Buffer.from(path);
    const bytes = Buffer.from(content);
    const checksum = crc32(bytes);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x0800, 6);
    local.writeUInt16LE(33, 12);
    local.writeUInt32LE(checksum, 14);
    local.writeUInt32LE(bytes.length, 18);
    local.writeUInt32LE(bytes.length, 22);
    local.writeUInt16LE(name.length, 26);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(0x0314, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt16LE(33, 14);
    central.writeUInt32LE(checksum, 16);
    central.writeUInt32LE(bytes.length, 20);
    central.writeUInt32LE(bytes.length, 24);
    central.writeUInt16LE(name.length, 28);
    central.writeUInt32LE((0o100644 << 16) >>> 0, 38);
    central.writeUInt32LE(offset, 42);
    localParts.push(local, name, bytes);
    centralParts.push(central, name);
    offset += local.length + name.length + bytes.length;
  }
  const central = Buffer.concat(centralParts);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(Object.keys(files).length, 8);
  end.writeUInt16LE(Object.keys(files).length, 10);
  end.writeUInt32LE(central.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...localParts, central, end]);
}

async function tree(directory) {
  const result = {};
  const entries = await readdir(directory, { withFileTypes: true });
  entries.sort((left, right) => left.name.localeCompare(right.name));
  for (const entry of entries) {
    const path = join(directory, entry.name);
    result[entry.name] = entry.isDirectory()
      ? await tree(path)
      : await readFile(path);
  }
  return result;
}

async function gameFixture(t, installed = {}) {
  const path = await mkdtemp(join(work, "addon-updates-"));
  t.after(() => rm(path, { recursive: true, force: true, maxRetries: 5 }));
  const game = join(path, "game");
  const addons = join(game, "Interface", "AddOns");
  const saved = join(game, "WTF", "Account", "SYNTHETIC", "SavedVariables");
  await mkdir(saved, { recursive: true });
  await mkdir(join(addons, "OtherAddon"), { recursive: true });
  await copyFile(join(work, "SyntheticGame.exe"), join(game, "Wow.exe"));
  await writeFile(
    join(saved, "ProfessionMaster.lua"),
    "synthetic = { keep = true }\n",
  );
  await writeFile(
    join(saved, "Overlord.lua"),
    Buffer.from([0, 13, 10, 255, 42]),
  );
  await writeFile(
    join(addons, "OtherAddon", "Core.lua"),
    "-- unrelated addon\n",
  );
  for (const [key, input] of Object.entries(installed)) {
    const directory = join(addons, catalog[key].directory);
    await mkdir(directory);
    if (!input.missingToc)
      await writeFile(join(directory, catalog[key].toc), toc(key, input));
    await writeFile(join(directory, "Core.lua"), `-- unmanaged ${key}\n`);
    await writeFile(join(directory, "user-settings.lua"), "keep = true\n");
  }
  const preservedTrees = new Map();
  for (const directory of [
    join(game, "WTF"),
    join(addons, "OtherAddon"),
    ...Object.keys(installed).map((key) =>
      join(addons, catalog[key].directory),
    ),
  ]) {
    preservedTrees.set(directory, await tree(directory));
  }
  return { game, addons, preservedTrees };
}

async function preserved(fixture) {
  for (const [directory, before] of fixture.preservedTrees)
    assert.deepEqual(await tree(directory), before, directory);
  assert.deepEqual(
    await readFile(join(fixture.game, "Wow.exe")),
    await readFile(join(work, "SyntheticGame.exe")),
  );
  assert.equal(
    (await readdir(fixture.addons)).some((name) => name.startsWith(".aurify-")),
    false,
  );
}

function statuses(results) {
  return Object.fromEntries(
    Object.entries(results).map(([key, value]) => [key, value.Status]),
  );
}

function assertRequests(portal, paths) {
  assert.deepEqual(
    portal.requests,
    paths.map((url) => ({
      method: "GET",
      url,
      host: new URL(portal.serverUrl).host,
      authorization: `Bearer ${token}`,
      body: "",
    })),
  );
}

async function portalFixture(t) {
  const portal = {
    status: 200,
    requests: [],
    packages: new Map(),
    failure: null,
  };
  portal.publish = (key, version = catalog[key].version) => {
    const pkg = catalog[key];
    const files = {
      [`${pkg.directory}/${pkg.toc}`]: toc(key, { version }),
      [`${pkg.directory}/Core.lua`]: `-- synthetic ${key} ${version}\n`,
    };
    const bytes = syntheticZip(files);
    const sha256 = createHash("sha256").update(bytes).digest("hex");
    const path = `/members/api/aurify/addon-packages/${key}/${sha256}.zip`;
    portal.packages.set(path, { status: 200, bytes, files });
    return {
      status: "ok",
      version,
      url:
        key === "professions"
          ? "https://www.curseforge.com/wow/addons/profession-master/files/9331989"
          : `https://github.com/leliondoc/overlord-forever-addon/releases/tag/${version}`,
      package: { bytes: bytes.length, sha256, path },
    };
  };
  portal.payload = {
    schema: 1,
    interface: 16001,
    checkedAt,
    addons: {
      professions: portal.publish("professions"),
      overlord: portal.publish("overlord"),
    },
  };
  const server = createServer(async (request, response) => {
    try {
      const chunks = [];
      for await (const chunk of request) chunks.push(chunk);
      portal.requests.push({
        method: request.method,
        url: request.url,
        host: request.headers.host,
        authorization: request.headers.authorization,
        body: Buffer.concat(chunks).toString("utf8"),
      });
      if (
        request.method !== "GET" ||
        request.headers.authorization !== `Bearer ${token}`
      ) {
        response.writeHead(401).end();
        return;
      }
      if (request.url === updatesPath) {
        response.writeHead(portal.status, {
          "Content-Type": "application/json",
          ...(portal.location ? { Location: portal.location } : {}),
        });
        response.end(JSON.stringify(portal.payload));
        return;
      }
      const pkg = portal.packages.get(request.url);
      assert.ok(pkg, `Unexpected package route: ${request.url}`);
      response.writeHead(pkg.status, {
        "Content-Type": "application/zip",
        "Content-Length": pkg.bytes.length,
        ...(pkg.location ? { Location: pkg.location } : {}),
      });
      response.end(pkg.bytes);
    } catch (error) {
      portal.failure = error;
      response.writeHead(500).end();
    }
  });
  await new Promise((resolvePromise) =>
    server.listen(0, "127.0.0.1", resolvePromise),
  );
  t.after(async () => {
    server.closeAllConnections();
    await new Promise((resolvePromise) => server.close(resolvePromise));
    if (portal.failure) throw portal.failure;
  });
  portal.serverUrl = `http://127.0.0.1:${server.address().port}`;
  portal.apply = (fixture, paired = true) =>
    nativeAsync({
      operation: "addon-updates",
      root: fixture.game,
      serverUrl: portal.serverUrl,
      ...(paired ? { token } : {}),
    });
  return portal;
}

async function installedFiles(fixture, pkg) {
  for (const [path, content] of Object.entries(pkg.files))
    assert.equal(await readFile(join(fixture.addons, path), "utf8"), content);
}

test("native apply compares numeric versions and preserves unmanaged older, current, and newer addons", async (t) => {
  const portal = await portalFixture(t);
  const older = await gameFixture(t, {
    professions: { version: "7.0.29" },
    overlord: { version: "2.0.8" },
  });
  const first = await portal.apply(older);
  assert.deepEqual(statuses(first.Updates), {
    professions: "update-needed",
    overlord: "current",
  });
  assert.equal(first.Results.professions.Status, "update-needed");
  assert.equal(first.Results.overlord.Status, "present");
  assert.equal(first.Updates.professions.InstalledVersion, "7.0.29");
  assert.equal(first.Updates.professions.LatestVersion, "7.0.30");
  assert.equal(first.Updates.professions.Retryable, false);
  await preserved(older);

  const newer = await gameFixture(t, {
    professions: { version: "7.0.30.1" },
    overlord: { version: "2.0.10" },
  });
  const higher = await portal.apply(newer);
  assert.deepEqual(statuses(higher.Updates), {
    professions: "current",
    overlord: "current",
  });
  assert.equal(higher.Results.professions.Status, "present");
  assert.equal(higher.Results.overlord.Status, "present");
  await preserved(newer);
  assertRequests(portal, [updatesPath, updatesPath]);
});

test("native apply blocks occupied folders with missing, incompatible, or invalid TOC identities", async (t) => {
  const portal = await portalFixture(t);
  const missingOrInvalid = await gameFixture(t, {
    professions: { missingToc: true },
    overlord: { version: "2.0" },
  });
  const incompatible = await gameFixture(t, {
    professions: { interface: 11500 },
    overlord: { version: "02.0.8" },
  });
  for (const game of [missingOrInvalid, incompatible]) {
    const setup = await portal.apply(game);
    assert.deepEqual(statuses(setup.Updates), {
      professions: "blocked",
      overlord: "blocked",
    });
    for (const key of Object.keys(catalog)) {
      assert.equal(setup.Results[key].Status, "blocked");
      assert.equal(setup.Results[key].Retryable, false);
      assert.equal(setup.Updates[key].Retryable, false);
      assert.equal(setup.Updates[key].InstalledVersion, null);
    }
    await preserved(game);
  }
  assertRequests(portal, [updatesPath, updatesPath]);
});

test("provider failures do not prevent the independent addon from installing", async (t) => {
  const portal = await portalFixture(t);
  const game = await gameFixture(t);
  const professionRelease = portal.payload.addons.professions;
  const overlordRelease = portal.payload.addons.overlord;
  portal.payload.addons.professions = {
    status: "unavailable",
    reason: "not-configured",
    error: "UNTRUSTED_PROVIDER_TEXT",
  };
  const unavailable = await portal.apply(game);
  assert.deepEqual(statuses(unavailable.Updates), {
    professions: "unavailable",
    overlord: "installed",
  });
  assert.equal(unavailable.Updates.professions.Retryable, false);
  assert.equal(
    JSON.stringify(unavailable).includes("UNTRUSTED_PROVIDER_TEXT"),
    false,
  );
  await installedFiles(game, portal.packages.get(overlordRelease.package.path));
  await assert.rejects(readdir(join(game.addons, "ProfessionMaster")), {
    code: "ENOENT",
  });
  const sibling = await tree(join(game.addons, "Overlord"));

  portal.payload.addons.professions = professionRelease;
  portal.payload.addons.overlord = { status: "ok", version: "2.0.8" };
  const malformed = await portal.apply(game);
  assert.deepEqual(statuses(malformed.Updates), {
    professions: "installed",
    overlord: "unavailable",
  });
  assert.equal(malformed.Updates.overlord.Retryable, false);
  await installedFiles(
    game,
    portal.packages.get(professionRelease.package.path),
  );
  assert.deepEqual(await tree(join(game.addons, "Overlord")), sibling);

  portal.payload.addons.professions = {
    status: "unavailable",
    reason: "upstream-unavailable",
  };
  portal.payload.addons.overlord = overlordRelease;
  const transient = await portal.apply(game);
  assert.deepEqual(statuses(transient.Updates), {
    professions: "unavailable",
    overlord: "current",
  });
  assert.equal(transient.Updates.professions.Retryable, true);
  await preserved(game);
  assertRequests(portal, [
    updatesPath,
    overlordRelease.package.path,
    updatesPath,
    professionRelease.package.path,
    updatesPath,
  ]);
});

test("metadata schema, endpoint, and auth failures fail closed and unpaired apply makes no requests", async (t) => {
  const portal = await portalFixture(t);
  const game = await gameFixture(t, { professions: {}, overlord: {} });
  portal.payload.schema = 2;
  const invalid = await portal.apply(game);
  assert.deepEqual(statuses(invalid.Updates), {
    professions: "unavailable",
    overlord: "unavailable",
  });
  assert.equal(invalid.Updates.professions.Retryable, false);
  assert.equal(invalid.Updates.overlord.Retryable, false);

  portal.status = 503;
  portal.payload = { error: "UPSTREAM_PRIVATE_FAILURE" };
  const endpointFailure = await portal.apply(game);
  assert.deepEqual(statuses(endpointFailure.Updates), {
    professions: "unavailable",
    overlord: "unavailable",
  });
  assert.equal(
    JSON.stringify(endpointFailure).includes("UPSTREAM_PRIVATE_FAILURE"),
    false,
  );
  assert.equal(endpointFailure.Updates.professions.Retryable, true);
  assert.equal(endpointFailure.Updates.overlord.Retryable, true);

  portal.status = 401;
  portal.payload = { error: "PRIVATE_AUTH_FAILURE" };
  const authFailure = await portal.apply(game);
  assert.deepEqual(statuses(authFailure.Updates), {
    professions: "unavailable",
    overlord: "unavailable",
  });
  assert.equal(
    JSON.stringify(authFailure).includes("PRIVATE_AUTH_FAILURE"),
    false,
  );
  assert.equal(authFailure.Updates.professions.Retryable, false);
  assert.equal(authFailure.Updates.overlord.Retryable, false);

  const unpaired = await portal.apply(game, false);
  assert.deepEqual(statuses(unpaired.Updates), {
    professions: "unavailable",
    overlord: "unavailable",
  });
  assert.equal(unpaired.Updates.professions.Retryable, false);
  assert.equal(unpaired.Updates.overlord.Retryable, false);
  await preserved(game);
  assertRequests(portal, [updatesPath, updatesPath, updatesPath]);
});

test("native apply rejects untrusted release URLs, mismatched tags, and unsupported major versions", async (t) => {
  const portal = await portalFixture(t);
  const game = await gameFixture(t, {
    professions: { version: "7.1.0" },
    overlord: { version: "2.0.8" },
  });
  const professions = portal.payload.addons.professions;
  const overlord = portal.payload.addons.overlord;
  portal.payload.addons.professions = {
    ...professions,
    url: `${professions.url}?next=https://evil.example`,
  };
  const badCurseForge = await portal.apply(game);
  assert.deepEqual(statuses(badCurseForge.Updates), {
    professions: "unavailable",
    overlord: "current",
  });
  assert.equal(badCurseForge.Updates.professions.Retryable, false);

  portal.payload.addons.professions = professions;
  portal.payload.addons.overlord = {
    ...overlord,
    url: "https://github.com/leliondoc/overlord-forever-addon/releases/tag/2.0.9",
  };
  const badGitHubTag = await portal.apply(game);
  assert.deepEqual(statuses(badGitHubTag.Updates), {
    professions: "current",
    overlord: "unavailable",
  });
  assert.equal(badGitHubTag.Updates.overlord.Retryable, false);

  portal.payload.addons = {
    professions: portal.publish("professions", "8.0.0"),
    overlord: portal.publish("overlord", "3.0.0"),
  };
  const unsupported = await portal.apply(game);
  assert.deepEqual(statuses(unsupported.Updates), {
    professions: "unavailable",
    overlord: "unavailable",
  });
  assert.equal(unsupported.Updates.professions.Retryable, false);
  assert.equal(unsupported.Updates.overlord.Retryable, false);
  await preserved(game);
  assertRequests(portal, [updatesPath, updatesPath, updatesPath]);
});

test("package descriptors must use the exact same-origin key and hash path and a valid size", async (t) => {
  const portal = await portalFixture(t);
  const game = await gameFixture(t);
  const professions = portal.payload.addons.professions;
  const overlord = portal.payload.addons.overlord;
  portal.payload.addons = {
    professions: {
      ...professions,
      package: {
        ...professions.package,
        path: `https://evil.example${professions.package.path}`,
      },
    },
    overlord: {
      ...overlord,
      package: { ...overlord.package, bytes: 0 },
    },
  };
  const external = await portal.apply(game);
  assert.deepEqual(statuses(external.Updates), {
    professions: "unavailable",
    overlord: "unavailable",
  });
  portal.payload.addons.professions = {
    ...professions,
    package: { ...professions.package, path: overlord.package.path },
  };
  const mismatched = await portal.apply(game);
  assert.equal(mismatched.Updates.professions.Status, "unavailable");
  assert.equal(mismatched.Updates.professions.Retryable, false);
  await assert.rejects(readdir(join(game.addons, "ProfessionMaster")), {
    code: "ENOENT",
  });
  await assert.rejects(readdir(join(game.addons, "Overlord")), {
    code: "ENOENT",
  });
  await preserved(game);
  assertRequests(portal, [updatesPath, updatesPath]);
});

test("native apply installs authenticated real ZIP packages and repeated apply performs no downloads", async (t) => {
  const portal = await portalFixture(t);
  const game = await gameFixture(t);
  const professionPath = portal.payload.addons.professions.package.path;
  const overlordPath = portal.payload.addons.overlord.package.path;
  const first = await portal.apply(game);
  assert.equal(first.Status, "ready");
  assert.deepEqual(statuses(first.Results), {
    activity: "installed",
    professions: "installed",
    overlord: "installed",
  });
  assert.deepEqual(statuses(first.Updates), {
    professions: "installed",
    overlord: "installed",
  });
  for (const key of Object.keys(catalog)) {
    assert.equal(first.Updates[key].InstalledVersion, catalog[key].version);
    await installedFiles(
      game,
      portal.packages.get(portal.payload.addons[key].package.path),
    );
  }
  assert.deepEqual(
    await readFile(join(game.addons, "Aurify", "Aurify.lua")),
    await readFile(join(work, "addon", "Aurify", "Aurify.lua")),
  );
  const installed = await tree(game.addons);
  await preserved(game);

  const repeated = await portal.apply(game);
  assert.equal(repeated.Status, "ready");
  assert.deepEqual(statuses(repeated.Results), {
    activity: "present",
    professions: "present",
    overlord: "present",
  });
  assert.deepEqual(statuses(repeated.Updates), {
    professions: "current",
    overlord: "current",
  });
  assert.deepEqual(await tree(game.addons), installed);
  await preserved(game);
  assertRequests(portal, [
    updatesPath,
    professionPath,
    overlordPath,
    updatesPath,
  ]);
});

test("managed updates recover after retryable 503 and permanent auth or integrity rejection without touching sibling or WTF bytes", async (t) => {
  const portal = await portalFixture(t);
  const game = await gameFixture(t, { overlord: {} });
  const initialPath = portal.payload.addons.professions.package.path;
  const initial = await portal.apply(game);
  assert.equal(initial.Updates.professions.Status, "installed");
  assert.equal(initial.Updates.overlord.Status, "current");
  await installedFiles(game, portal.packages.get(initialPath));
  const target = join(game.addons, "ProfessionMaster");
  const before = await tree(target);
  portal.payload.addons.professions = portal.publish("professions", "7.0.31");
  const nextPath = portal.payload.addons.professions.package.path;
  const next = portal.packages.get(nextPath);

  next.status = 503;
  const transient = await portal.apply(game);
  assert.equal(transient.Status, "partial");
  assert.equal(transient.Results.professions.Status, "failed");
  assert.equal(transient.Results.professions.Retryable, true);
  assert.equal(transient.Updates.professions.Status, "failed");
  assert.equal(transient.Updates.professions.Retryable, true);
  assert.equal(transient.Updates.overlord.Status, "current");
  assert.deepEqual(await tree(target), before);
  await preserved(game);

  next.status = 401;
  const unauthorized = await portal.apply(game);
  assert.equal(unauthorized.Results.professions.Status, "failed");
  assert.equal(unauthorized.Results.professions.Retryable, false);
  assert.equal(unauthorized.Updates.professions.Status, "failed");
  assert.equal(unauthorized.Updates.professions.Retryable, false);
  assert.equal(unauthorized.Updates.overlord.Status, "current");
  assert.deepEqual(await tree(target), before);
  await preserved(game);

  next.status = 200;
  const validBytes = next.bytes;
  next.bytes = Buffer.from(validBytes);
  next.bytes[next.bytes.length - 1] ^= 1;
  const corrupt = await portal.apply(game);
  assert.equal(corrupt.Results.professions.Status, "failed");
  assert.equal(corrupt.Results.professions.Retryable, false);
  assert.equal(corrupt.Updates.professions.Status, "failed");
  assert.equal(corrupt.Updates.professions.Retryable, false);
  assert.equal(corrupt.Updates.overlord.Status, "current");
  assert.deepEqual(await tree(target), before);
  await preserved(game);

  next.bytes = validBytes;
  const recovered = await portal.apply(game);
  assert.equal(recovered.Status, "ready");
  assert.equal(recovered.Results.professions.Status, "installed");
  assert.equal(recovered.Updates.professions.Status, "installed");
  assert.equal(recovered.Updates.professions.InstalledVersion, "7.0.31");
  assert.equal(recovered.Updates.overlord.Status, "current");
  await installedFiles(game, next);
  await preserved(game);
  assertRequests(portal, [
    updatesPath,
    initialPath,
    updatesPath,
    nextPath,
    updatesPath,
    nextPath,
    updatesPath,
    nextPath,
    updatesPath,
    nextPath,
  ]);
});

test("metadata and package redirects are never followed or given device credentials", async (t) => {
  const redirectedRequests = [];
  const destination = createServer((request, response) => {
    redirectedRequests.push({
      url: request.url,
      authorization: request.headers.authorization,
    });
    response.writeHead(200).end("redirect must not be followed");
  });
  await new Promise((done) => destination.listen(0, "127.0.0.1", done));
  t.after(async () => {
    destination.closeAllConnections();
    await new Promise((done) => destination.close(done));
  });
  const location = `http://127.0.0.1:${destination.address().port}/redirected`;
  const portal = await portalFixture(t);
  const game = await gameFixture(t);
  portal.status = 302;
  portal.location = location;
  const metadataRedirect = await portal.apply(game);
  assert.deepEqual(statuses(metadataRedirect.Updates), {
    professions: "unavailable",
    overlord: "unavailable",
  });
  for (const key of Object.keys(catalog)) {
    assert.equal(metadataRedirect.Updates[key].Retryable, false);
    await assert.rejects(readdir(join(game.addons, catalog[key].directory)), {
      code: "ENOENT",
    });
  }
  assert.deepEqual(redirectedRequests, []);
  await preserved(game);

  portal.status = 200;
  delete portal.location;
  const professionPath = portal.payload.addons.professions.package.path;
  const overlordPath = portal.payload.addons.overlord.package.path;
  const redirectedPackage = portal.packages.get(professionPath);
  redirectedPackage.status = 307;
  redirectedPackage.location = location;
  const packageRedirect = await portal.apply(game);
  assert.equal(packageRedirect.Results.professions.Status, "failed");
  assert.equal(packageRedirect.Results.professions.Retryable, false);
  assert.equal(packageRedirect.Updates.professions.Status, "failed");
  assert.equal(packageRedirect.Updates.professions.Retryable, false);
  assert.equal(packageRedirect.Updates.overlord.Status, "installed");
  await assert.rejects(readdir(join(game.addons, "ProfessionMaster")), {
    code: "ENOENT",
  });
  await installedFiles(game, portal.packages.get(overlordPath));
  assert.deepEqual(redirectedRequests, []);
  assertRequests(portal, [
    updatesPath,
    updatesPath,
    professionPath,
    overlordPath,
  ]);
  await preserved(game);
});
