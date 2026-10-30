import assert from "node:assert/strict";
import { createServer } from "node:http";
import {
  copyFile,
  mkdir,
  mkdtemp,
  readFile,
  rm,
  writeFile,
} from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { nativeAsync } from "./native-bridge.mjs";

const work = resolve(
  dirname(fileURLToPath(import.meta.url)),
  "../.work/native-tests",
);
async function temporary(t) {
  const directory = await mkdtemp(join(work, "host-efficiency-"));
  t.after(() =>
    rm(directory, {
      recursive: true,
      force: true,
      maxRetries: 10,
      retryDelay: 100,
    }),
  );
  return directory;
}

test("host commits merge independent streams, roll back a failed save and persist acknowledged work during shutdown", async (t) => {
  const result = await nativeAsync({
    operation: "host-efficiency",
    action: "persistence",
    root: await temporary(t),
  });
  const key = `activity:${"a".repeat(64)}`;
  assert.equal(result.merged.snapshots[key].rawHash, "b".repeat(64));
  assert.deepEqual(result.merged.combat.pendingClosures, [
    {
      streamId: "11111111-1111-4111-8111-111111111111",
      fileDate: "2026-10-26",
    },
  ]);
  assert.equal(result.failedSave, "IOException");
  assert.deepEqual(result.rolledBack, result.merged);
  assert.deepEqual(result.durableAfterFailure, result.merged);
  assert.equal(result.drained.snapshots[key].rawHash, "d".repeat(64));
  assert.deepEqual(result.drained.combat, result.merged.combat);
  assert.equal(result.stale, "OperationCanceledException");
});

test("host combat upload completes while snapshots and maintenance are pending, and drains wait for their worker", async (t) => {
  const directory = await temporary(t);
  const game = join(directory, "game"),
    addon = join(game, "Interface", "AddOns", "Aurify");
  const saved = join(game, "WTF", "Account", "SYNTHETIC", "SavedVariables");
  await mkdir(addon, { recursive: true });
  await mkdir(saved, { recursive: true });
  await mkdir(join(game, "Logs"));
  await copyFile(join(work, "SyntheticGame.exe"), join(game, "Wow.exe"));
  await writeFile(
    join(addon, "Aurify.toc"),
    await readFile(join(work, "addon", "Aurify", "Aurify.toc")),
  );
  await writeFile(
    join(saved, "Aurify.lua"),
    "AurifyDB={version=1,sessions={}}",
  );
  const bytes = Buffer.from("10/26 12:00:00.000  SYNTHETIC COMBAT\r\n");
  await writeFile(join(game, "Logs", "WoWCombatLog.txt"), bytes);
  let arrived = false,
    release,
    ready,
    failure;
  let received = Buffer.alloc(0);
  const events = [];
  const server = createServer(async (request, response) => {
    const reply = (status, body) =>
      response
        .writeHead(status, { "Content-Type": "application/json" })
        .end(JSON.stringify(body));
    try {
      if (request.url === "/probe/wait/snapshot") {
        if (!arrived)
          await new Promise((done) => {
            ready = done;
          });
        return reply(200, {});
      }
      if (request.url === "/probe/release/snapshot") {
        assert.ok(release);
        release();
        return reply(200, {});
      }
      assert.equal(request.headers.authorization, `Bearer ${"T".repeat(43)}`);
      const chunks = [];
      for await (const chunk of request) chunks.push(chunk);
      const body = chunks.length ? JSON.parse(Buffer.concat(chunks)) : null;
      if (request.url === "/members/api/ingest/activity") {
        events.push("snapshot-start");
        arrived = true;
        const released = new Promise((done) => {
          release = done;
        });
        ready?.();
        await released;
        events.push("snapshot-ack");
        return reply(200, { accepted: true, duplicate: false });
      }
      if (/^\/members\/api\/ingest\/combat\/[^/]+\/status$/.test(request.url))
        return reply(404, {});
      assert.equal(request.url, "/members/api/ingest/combat");
      assert.equal(body.offset, received.length);
      received = Buffer.concat([received, Buffer.from(body.data, "base64")]);
      events.push("combat-ack");
      return reply(200, {
        accepted: true,
        offset: received.length,
        reportId: "synthetic",
      });
    } catch (error) {
      failure = error;
      reply(500, {});
    }
  });
  await new Promise((done) => server.listen(0, "127.0.0.1", done));
  t.after(async () => {
    release?.();
    server.closeAllConnections();
    await new Promise((done) => server.close(done));
    if (failure) throw failure;
  });
  const result = await nativeAsync({
    operation: "host-efficiency",
    action: "scheduling",
    root: join(directory, "state"),
    game,
    serverUrl: `http://127.0.0.1:${server.address().port}`,
  });
  assert.deepEqual(received, bytes);
  assert.deepEqual(events, ["snapshot-start", "combat-ack", "snapshot-ack"]);
  assert.equal(result.combatBeforeSnapshot, true);
  assert.equal(result.maintenanceDrainWaited, true);
  assert.equal(result.state.combat.current.offset, bytes.length);
  assert.equal(Object.keys(result.state.snapshots).length, 1);
});

test("UI coalescing displays the latest value and preserves a newer view after a callback fails", async () => {
  const result = await nativeAsync({
    operation: "host-efficiency",
    action: "views",
  });
  assert.equal(result.firstDisplay, "latest");
  assert.equal(result.finalDisplay, "recovered");
  assert.equal(result.firstQueued, true);
  assert.equal(result.secondQueued, false);
  assert.equal(result.repeatedQueued, false);
  assert.equal(result.changes, 3);
});

test("host checks upstream on startup and after 24 hours, not after six hours, without replacing installed addons", async (t) => {
  const directory = await temporary(t);
  const game = join(directory, "game");
  await mkdir(game);
  await copyFile(join(work, "SyntheticGame.exe"), join(game, "Wow.exe"));
  const preserved = new Map();
  for (const [folder, toc, title, version] of [
    [
      "ProfessionMaster",
      "ProfessionMaster_Camelot.toc",
      "Profession Master Forever",
      "7.0.24",
    ],
    ["Overlord", "Overlord.toc", "Overlord Forever", "2.0.4"],
  ]) {
    const target = join(game, "Interface", "AddOns", folder);
    await mkdir(target, { recursive: true });
    for (const [name, bytes] of [
      [
        toc,
        `## Title: ${title}\n## Version: ${version}\n## Interface: 16001\n`,
      ],
      ["user-settings.lua", "preserved = true\n"],
    ]) {
      const path = join(target, name);
      await writeFile(path, bytes);
      preserved.set(path, bytes);
    }
  }
  const packageHash = "1".repeat(64);
  let checks = 0,
    failure;
  const server = createServer((request, response) => {
    try {
      assert.equal(request.url, "/members/api/aurify/addon-updates");
      assert.equal(request.method, "GET");
      assert.equal(request.headers.authorization, `Bearer ${"T".repeat(43)}`);
      checks++;
      response.writeHead(200, { "Content-Type": "application/json" }).end(
        JSON.stringify({
          schema: 1,
          interface: 16001,
          checkedAt: Date.now(),
          addons: {
            professions: { status: "unavailable", reason: "not-configured" },
            overlord: {
              status: "ok",
              version: "2.0.8",
              url: "https://github.com/leliondoc/overlord-forever-addon/releases/tag/2.0.8",
              package: {
                bytes: 1,
                sha256: packageHash,
                path: `/members/api/aurify/addon-packages/overlord/${packageHash}.zip`,
              },
            },
          },
        }),
      );
    } catch (error) {
      failure = error;
      response.writeHead(500).end("{}");
    }
  });
  await new Promise((done) => server.listen(0, "127.0.0.1", done));
  t.after(async () => {
    server.closeAllConnections();
    await new Promise((done) => server.close(done));
    if (failure) throw failure;
  });
  const result = await nativeAsync({
    operation: "host-efficiency",
    action: "addon-scheduling",
    root: join(directory, "state"),
    game,
    serverUrl: `http://127.0.0.1:${server.address().port}`,
  });
  assert.deepEqual(
    result.map((entry) => entry.started),
    [true, false, true],
  );
  assert.equal(checks, 2);
  assert.equal(result[2].setup.Updates.overlord.Status, "update-needed");
  assert.equal(result[2].setup.Updates.professions.Status, "unavailable");
  for (const [path, bytes] of preserved)
    assert.equal(await readFile(path, "utf8"), bytes);
});

test("host retries transient addon provider failures after fifteen minutes", async (t) => {
  const directory = await temporary(t);
  const game = join(directory, "game");
  await mkdir(game);
  await copyFile(join(work, "SyntheticGame.exe"), join(game, "Wow.exe"));
  for (const [folder, tocName, title, version] of [
    [
      "ProfessionMaster",
      "ProfessionMaster_Camelot.toc",
      "Profession Master Forever",
      "7.0.24",
    ],
    ["Overlord", "Overlord.toc", "Overlord Forever", "2.0.4"],
  ]) {
    const target = join(game, "Interface", "AddOns", folder);
    await mkdir(target, { recursive: true });
    await writeFile(
      join(target, tocName),
      `## Title: ${title}\n## Version: ${version}\n## Interface: 16001\n`,
    );
  }
  const server = createServer((request, response) => {
    response.writeHead(503, { "Content-Type": "application/json" }).end("{}");
  });
  await new Promise((done) => server.listen(0, "127.0.0.1", done));
  t.after(async () => {
    server.closeAllConnections();
    await new Promise((done) => server.close(done));
  });
  const result = await nativeAsync({
    operation: "host-efficiency",
    action: "addon-retry",
    root: join(directory, "state"),
    game,
    serverUrl: `http://127.0.0.1:${server.address().port}`,
  });
  assert.ok(result.delay > 14 * 60 * 1000 && result.delay <= 15 * 60 * 1000);
  assert.equal(result.setup.Updates.professions.Retryable, true);
  assert.equal(result.setup.Updates.overlord.Retryable, true);
  assert.equal(result.setup.Results.professions.Status, "unavailable");
  assert.equal(result.setup.Results.overlord.Status, "unavailable");
});

test("host retries temporary package failures but stops rapid retries after authorization rejection", async (t) => {
  const directory = await temporary(t);
  const game = join(directory, "game");
  const addons = join(game, "Interface", "AddOns");
  await mkdir(join(addons, "Overlord"), { recursive: true });
  await mkdir(join(game, "WTF"));
  await copyFile(join(work, "SyntheticGame.exe"), join(game, "Wow.exe"));
  const toc =
    "## Title: Overlord Forever\n## Version: 2.0.8\n## Interface: 16001\n";
  await writeFile(join(addons, "Overlord", "Overlord.toc"), toc);
  await writeFile(join(game, "WTF", "settings.txt"), "preserved");
  const hash = "1".repeat(64);
  const packagePath = `/members/api/aurify/addon-packages/professions/${hash}.zip`;
  let packageStatus = 503;
  const packageRequests = [];
  const server = createServer((request, response) => {
    if (request.url === packagePath) {
      packageRequests.push({
        status: packageStatus,
        authorization: request.headers.authorization,
      });
      response.writeHead(packageStatus).end();
      return;
    }
    if (request.url !== "/members/api/aurify/addon-updates") {
      response.writeHead(404).end();
      return;
    }
    response.writeHead(200, { "Content-Type": "application/json" }).end(
      JSON.stringify({
        schema: 1,
        interface: 16001,
        checkedAt: Date.now(),
        addons: {
          professions: {
            status: "ok",
            version: "7.1.0",
            url: "https://www.curseforge.com/wow/addons/profession-master/files/9350717",
            package: { bytes: 1, sha256: hash, path: packagePath },
          },
          overlord: {
            status: "ok",
            version: "2.0.8",
            url: "https://github.com/leliondoc/overlord-forever-addon/releases/tag/2.0.8",
            package: {
              bytes: 1,
              sha256: hash,
              path: `/members/api/aurify/addon-packages/overlord/${hash}.zip`,
            },
          },
        },
      }),
    );
  });
  await new Promise((done) => server.listen(0, "127.0.0.1", done));
  t.after(async () => {
    server.closeAllConnections();
    await new Promise((done) => server.close(done));
  });
  const request = {
    operation: "host-efficiency",
    action: "addon-retry",
    root: join(directory, "state"),
    game,
    serverUrl: `http://127.0.0.1:${server.address().port}`,
  };
  const transient = await nativeAsync(request);
  assert.equal(transient.setup.Updates.professions.Status, "failed");
  assert.equal(transient.setup.Updates.professions.Retryable, true);
  assert.ok(
    transient.delay > 14 * 60 * 1000 && transient.delay <= 15 * 60 * 1000,
  );
  packageStatus = 401;
  const permanent = await nativeAsync(request);
  assert.equal(permanent.setup.Updates.professions.Status, "failed");
  assert.equal(permanent.setup.Updates.professions.Retryable, false);
  assert.ok(
    permanent.delay > 23 * 60 * 60 * 1000 &&
      permanent.delay <= 24 * 60 * 60 * 1000,
  );
  assert.equal(permanent.setup.Updates.overlord.Status, "current");
  assert.deepEqual(
    packageRequests,
    [503, 401].map((status) => ({
      status,
      authorization: `Bearer ${"T".repeat(43)}`,
    })),
  );
  await assert.rejects(
    readFile(join(addons, "ProfessionMaster", "ProfessionMaster_Camelot.toc")),
    { code: "ENOENT" },
  );
  assert.equal(
    await readFile(join(addons, "Overlord", "Overlord.toc"), "utf8"),
    toc,
  );
  assert.equal(
    await readFile(join(game, "WTF", "settings.txt"), "utf8"),
    "preserved",
  );
});
