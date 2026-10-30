import assert from "node:assert/strict";
import { createServer } from "node:http";
import { copyFile, mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { nativeAsync } from "./native-bridge.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const work = join(root, ".work", "native-tests");
const token = "S".repeat(43);
const sourceKey = Buffer.alloc(32, 11).toString("base64");
const now = Date.UTC(2026, 9, 26, 12);
const encoded = (value) => Buffer.from(value).toString("base64");

function profession(skill) {
  return `PM_Settings={storeVersion=14} PM_Data={["Forever-A"]={guilds={AURA={members={["Synthetic-Forever"]=true}}},guildmates={["Synthetic-Forever"]={[171]={${skill}}}}}}`;
}

function aurify(revision, characters = true) {
  const startedAt = now - 120_000 + revision;
  const character = characters
    ? `,characterSchemaVersion=1,characters={{guid="Player-1-ABC",name="Synthetic",realm="Synthetic Realm",capturedAt=${now - 60_000 + revision},equipment={}}}`
    : "";
  return `AurifyDB={version=1,sessions={{id="${startedAt}-409-${revision}",kind="raid",instanceId=409,difficultyId=9,name="Synthetic instance",startedAt=${startedAt},playerGuid="Player-1-00000ABC",realm="Synthetic Realm"}}${character}}`;
}

const catalog = {
  professions: [
    "ProfessionMaster",
    "ProfessionMaster_Camelot.toc",
    "Profession Master Forever",
    "7.0.24",
    "",
  ],
  overlord: ["Overlord", "Overlord.toc", "Overlord Forever", "2.0.4", ""],
  activity: [
    "Aurify",
    "Aurify.toc",
    "Aurify",
    "1.0.0",
    "## X-Aurify-Managed: 1\n",
  ],
};

async function fixture(
  t,
  {
    addons = ["professions", "overlord", "activity"],
    files = {
      professions: profession(101),
      overlord: "OverlordDB={}",
      aurify: aurify(1),
    },
    enableWatcher = false,
  } = {},
) {
  const directory = await mkdtemp(join(work, "snapshot-efficiency-"));
  t.after(() => rm(directory, { recursive: true, force: true, maxRetries: 5 }));
  const game = join(directory, "game");
  const addonRoot = join(game, "Interface", "AddOns");
  const saved = join(
    game,
    "WTF",
    "Account",
    "SYNTHETIC_ACCOUNT",
    "SavedVariables",
  );
  await mkdir(addonRoot, { recursive: true });
  await mkdir(saved, { recursive: true });
  await copyFile(join(work, "SyntheticGame.exe"), join(game, "Wow.exe"));
  for (const key of addons) {
    const [folder, toc, title, version, extra] = catalog[key];
    await mkdir(join(addonRoot, folder));
    await writeFile(
      join(addonRoot, folder, toc),
      `## Title: ${title}\n## Version: ${version}\n## Interface: 16001\n${extra}`,
    );
  }
  const fileNames = {
    professions: "ProfessionMaster.lua",
    overlord: "Overlord.lua",
    aurify: "Aurify.lua",
  };
  for (const [key, value] of Object.entries(files))
    await writeFile(join(saved, fileNames[key]), value);

  const attempts = [];
  const accepted = [];
  const arrivals = new Set();
  const arrivalWaiters = new Map();
  const releaseWaiters = new Map();
  let steps = [];
  let step = -1;
  let failure;
  const reply = (response, status, body) => {
    response.writeHead(status, { "Content-Type": "application/json" });
    response.end(JSON.stringify(body));
  };
  const waitForRelease = (key) => {
    arrivals.add(key);
    arrivalWaiters.get(key)?.();
    arrivalWaiters.delete(key);
    return new Promise((resolvePromise) =>
      releaseWaiters.set(key, resolvePromise),
    );
  };
  const server = createServer(async (request, response) => {
    try {
      const marker = /^\/probe\/step\/(\d+)$/.exec(request.url);
      if (marker) {
        step = Number(marker[1]);
        return reply(response, 200, {});
      }
      const wait = /^\/probe\/wait\/(\d+)\/([a-z]+)$/.exec(request.url);
      if (wait) {
        const key = `${wait[1]}/${wait[2]}`;
        if (!arrivals.has(key))
          await new Promise((resolvePromise) =>
            arrivalWaiters.set(key, resolvePromise),
          );
        return reply(response, 200, {});
      }
      const release = /^\/probe\/release\/(\d+)\/([a-z]+)$/.exec(request.url);
      if (release) {
        const key = `${release[1]}/${release[2]}`;
        const resume = releaseWaiters.get(key);
        assert.ok(resume, `missing upload barrier ${key}`);
        releaseWaiters.delete(key);
        resume();
        return reply(response, 200, {});
      }

      assert.equal(request.headers.authorization, `Bearer ${token}`);
      const chunks = [];
      for await (const chunk of request) chunks.push(chunk);
      const body = JSON.parse(Buffer.concat(chunks));
      assert.equal(request.url, `/members/api/ingest/${body.kind}`);
      attempts.push({ step, body });
      const action = steps[step] || {};
      if (
        action.invalidateDuringUpload === body.kind ||
        action.cancelDuringUpload === body.kind
      )
        await waitForRelease(`${step}/${body.kind}`);
      if (action.failUpload === body.kind)
        return reply(response, 503, { error: "synthetic upload failure" });
      if (action.cancelDuringUpload === body.kind) {
        response.destroy();
        return;
      }
      accepted.push({ step, body });
      return reply(response, 200, { accepted: true, duplicate: false });
    } catch (error) {
      failure = error;
      if (!response.destroyed) reply(response, 500, { error: error.message });
    }
  });
  await new Promise((resolvePromise) =>
    server.listen(0, "127.0.0.1", resolvePromise),
  );
  t.after(async () => {
    server.closeAllConnections();
    await new Promise((resolvePromise) => server.close(resolvePromise));
    if (failure) throw failure;
  });
  return {
    attempts: (index) =>
      attempts.filter((item) => item.step === index).map((item) => item.body),
    uploads: (index) =>
      accepted.filter((item) => item.step === index).map((item) => item.body),
    run: (actions) => {
      steps = actions;
      return nativeAsync({
        operation: "snapshots-efficiency",
        root: game,
        serverUrl: `http://127.0.0.1:${server.address().port}`,
        token,
        sourceKey,
        now,
        enableWatcher,
        steps: actions,
      });
    },
  };
}

const uploadedKinds = (probe, step) =>
  probe
    .uploads(step)
    .map((payload) => payload.kind)
    .sort();
const checkpointKinds = (result) =>
  Object.keys(result.durable.snapshots || {})
    .map((key) => key.split(":")[0])
    .sort();
const professionSkill = (probe, step) =>
  probe.uploads(step).find((payload) => payload.kind === "professions")
    .records[0].skills[0].id;
const activityRevision = (probe, step) =>
  probe.uploads(step).find((payload) => payload.kind === "activity").sessions[0]
    .id;

test("persistent snapshot cache skips stable reads and shares the Aurify parse", async (t) => {
  const probe = await fixture(t);
  const result = await probe.run([
    {},
    {},
    { write: { professions: encoded(profession(102)) } },
  ]);
  assert.deepEqual(
    result.map((item) => item.error),
    [null, null, null],
  );
  assert.deepEqual(
    [result[0].readCalls, result[0].parseCalls],
    [3, 3],
    "four snapshot kinds use three file reads and three parses",
  );
  assert.deepEqual([result[1].readCalls, result[1].parseCalls], [0, 0]);
  assert.deepEqual([result[2].readCalls, result[2].parseCalls], [1, 1]);
  assert.deepEqual(
    result.map((item) => item.saveCalls),
    [1, 0, 1],
  );
  assert.deepEqual(uploadedKinds(probe, 0), [
    "activity",
    "characters",
    "overlord",
    "professions",
  ]);
  assert.deepEqual(uploadedKinds(probe, 1), []);
  assert.deepEqual(uploadedKinds(probe, 2), ["professions"]);
  assert.equal(professionSkill(probe, 2), 102);
});

test("restart, context invalidation and periodic reconciliation find same-metadata bytes", async (t) => {
  const probe = await fixture(t);
  const result = await probe.run([
    {},
    {
      write: { professions: encoded(profession(102)) },
      restoreMtime: true,
      restart: true,
    },
    {
      write: { professions: encoded(profession(103)) },
      restoreMtime: true,
      versions: { activity: "1.0.3" },
    },
    {
      advance: 60_000,
      write: { professions: encoded(profession(104)) },
      restoreMtime: true,
    },
  ]);
  assert.deepEqual(
    result.map((item) => item.error),
    [null, null, null, null],
  );
  assert.deepEqual(
    result.map((item) => item.readCalls),
    [3, 3, 3, 3],
  );
  for (const step of [1, 3])
    assert.deepEqual(
      uploadedKinds(probe, step),
      ["professions"],
      `step ${step}`,
    );
  assert.deepEqual(uploadedKinds(probe, 2), [
    "activity",
    "characters",
    "professions",
  ]);
  assert.deepEqual(
    [1, 2, 3].map((step) => professionSkill(probe, step)),
    [102, 103, 104],
  );
});

test("a failed upload stays dirty while accepted siblings commit once", async (t) => {
  const probe = await fixture(t);
  const result = await probe.run([{ failUpload: "professions" }, {}]);
  assert.equal(result[0].error, "PortalException");
  assert.equal(result[0].saveCalls, 1);
  assert.equal(result[0].checkpoints.length, 1);
  assert.deepEqual(checkpointKinds(result[0]), [
    "activity",
    "characters",
    "overlord",
  ]);
  assert.deepEqual(uploadedKinds(probe, 0), [
    "activity",
    "characters",
    "overlord",
  ]);
  assert.equal(result[1].error, null);
  assert.deepEqual([result[1].readCalls, result[1].parseCalls], [1, 1]);
  assert.deepEqual(uploadedKinds(probe, 1), ["professions"]);
  assert.deepEqual(checkpointKinds(result[1]), [
    "activity",
    "characters",
    "overlord",
    "professions",
  ]);
});

test("a failed durable save rolls back state and cannot clean the changed file", async (t) => {
  const probe = await fixture(t);
  const result = await probe.run([
    {},
    {
      write: { professions: encoded(profession(102)) },
      failSave: true,
    },
    {},
  ]);
  assert.equal(result[1].error, "IOException");
  assert.equal(result[1].saveCalls, 1);
  assert.equal(result[1].checkpoints.length, 0);
  assert.deepEqual(result[1].state, result[0].durable);
  assert.deepEqual(result[1].durable, result[0].durable);
  assert.deepEqual(uploadedKinds(probe, 1), ["professions"]);
  assert.deepEqual(uploadedKinds(probe, 2), ["professions"]);
  assert.deepEqual([result[2].readCalls, result[2].parseCalls], [1, 1]);
  assert.equal(professionSkill(probe, 2), 102);
});

test("cancellation retains accepted batch checkpoints without cleaning the file", async (t) => {
  const probe = await fixture(t, {
    addons: ["activity"],
    files: { aurify: aurify(1) },
  });
  const result = await probe.run([
    { cancelDuringUpload: "characters" },
    {},
    {},
  ]);
  assert.equal(result[0].error, "OperationCanceledException");
  assert.equal(result[0].saveCalls, 1);
  assert.equal(result[0].checkpoints.length, 1);
  assert.deepEqual(checkpointKinds(result[0]), ["activity"]);
  assert.deepEqual(uploadedKinds(probe, 0), ["activity"]);
  assert.deepEqual(
    probe.attempts(0).map((payload) => payload.kind),
    ["activity", "characters"],
  );
  assert.equal(result[1].error, null);
  assert.deepEqual([result[1].readCalls, result[1].parseCalls], [1, 1]);
  assert.deepEqual(uploadedKinds(probe, 1), ["characters"]);
  assert.deepEqual([result[2].readCalls, result[2].parseCalls], [0, 0]);
});

test("invalidation during upload preserves a same-metadata rewrite for the next scan", async (t) => {
  const probe = await fixture(t, {
    addons: ["activity"],
    files: { aurify: aurify(1, false) },
  });
  const result = await probe.run([
    {},
    {
      write: { aurify: encoded(aurify(2, false)) },
      invalidateDuringUpload: "activity",
      duringWrite: { aurify: encoded(aurify(3, false)) },
      duringRestoreMtime: true,
    },
    {},
    {},
  ]);
  assert.deepEqual(
    result.map((item) => item.error),
    [null, null, null, null],
  );
  assert.deepEqual(
    result.map((item) => item.readCalls),
    [1, 1, 1, 0],
  );
  assert.equal(activityRevision(probe, 1), `${now - 120_000 + 2}-409-2`);
  assert.equal(activityRevision(probe, 2), `${now - 120_000 + 3}-409-3`);
  assert.deepEqual(uploadedKinds(probe, 3), []);
});

test("synthetic watcher overflow recovers and delivers restored-mtime bytes", async (t) => {
  const probe = await fixture(t, {
    addons: ["activity"],
    files: { aurify: aurify(1, false) },
    enableWatcher: true,
  });
  const result = await probe.run([
    {},
    {
      write: { aurify: encoded(aurify(2, false)) },
      restoreMtime: true,
      watcherOverflow: true,
    },
  ]);
  assert.deepEqual(
    result.map((item) => item.error),
    [null, null],
  );
  assert.equal(result[1].readCalls, 1);
  assert.deepEqual(uploadedKinds(probe, 1), ["activity"]);
  assert.equal(activityRevision(probe, 1), `${now - 120_000 + 2}-409-2`);
});
