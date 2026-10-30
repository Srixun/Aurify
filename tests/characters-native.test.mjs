import assert from "node:assert/strict";
import { createServer } from "node:http";
import { copyFile, mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { nativeAsync } from "./native-bridge.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const work = join(root, ".work", "native-tests");
const token = "C".repeat(43);

async function fixture(t, addonVersion = "1.0.0") {
  const directory = await mkdtemp(join(work, "characters-"));
  t.after(() => rm(directory, { recursive: true, force: true, maxRetries: 5 }));
  const game = join(directory, "game");
  const addon = join(game, "Interface", "AddOns", "Aurify");
  const saved = join(
    game,
    "WTF",
    "Account",
    "SYNTHETIC_ACCOUNT",
    "SavedVariables",
  );
  await mkdir(addon, { recursive: true });
  await mkdir(saved, { recursive: true });
  await copyFile(join(work, "SyntheticGame.exe"), join(game, "Wow.exe"));
  await writeFile(
    join(addon, "Aurify.toc"),
    `## Title: Aurify\n## Version: ${addonVersion}\n## Interface: 16001\n## X-Aurify-Managed: 1\n`,
  );
  const uploads = [];
  const portal = { loseCharacterAck: false };
  const server = createServer(async (request, response) => {
    try {
      assert.equal(request.headers.authorization, `Bearer ${token}`);
      const chunks = [];
      for await (const chunk of request) chunks.push(chunk);
      const body = JSON.parse(Buffer.concat(chunks));
      assert.equal(request.url, `/members/api/ingest/${body.kind}`);
      assert.ok(body.kind === "activity" || body.kind === "characters");
      const duplicate = uploads.some(
        (previous) => JSON.stringify(previous) === JSON.stringify(body),
      );
      uploads.push(body);
      if (body.kind === "characters" && portal.loseCharacterAck) {
        portal.loseCharacterAck = false;
        response.destroy();
        return;
      }
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify({ accepted: true, duplicate }));
    } catch (error) {
      response.writeHead(500, { "Content-Type": "application/json" });
      response.end(JSON.stringify({ error: error.message }));
    }
  });
  await new Promise((resolvePromise) =>
    server.listen(0, "127.0.0.1", resolvePromise),
  );
  t.after(() => new Promise((resolvePromise) => server.close(resolvePromise)));
  const request = {
    operation: "snapshots",
    root: game,
    token,
    sourceKey: Buffer.alloc(32, 9).toString("base64"),
    serverUrl: `http://127.0.0.1:${server.address().port}`,
  };
  return {
    root: game,
    portal,
    uploads,
    save: (lua) => writeFile(join(saved, "Aurify.lua"), lua),
    scan: (state) => nativeAsync({ ...request, ...(state ? { state } : {}) }),
  };
}

function characterSave(capturedAt) {
  return `AurifyDB = { version=1, sessions={}, characterSchemaVersion=1,
    characters={{guid="Player-1-ABC",name="Synthetic",realm="Synthetic Realm",
      capturedAt=${capturedAt},equipment={
        {slot=16,itemId=123,itemString="item:123:0:-7:0"},
        {slot=2,itemId=0}
      }}}
  }`;
}

function kinds(state) {
  return Object.keys(state.snapshots)
    .map((key) => key.split(":")[0])
    .sort();
}

test("native character dispatch retries lost acknowledgements and retains them across restart", async (t) => {
  const game = await fixture(t);
  const lua = characterSave(Date.now() - 10_000);
  await game.save(lua);
  game.portal.loseCharacterAck = true;
  const lost = await game.scan();
  assert.ok(lost.error);
  assert.deepEqual(kinds(lost.state), ["activity"]);
  assert.deepEqual(
    game.uploads.map((payload) => payload.kind),
    ["activity", "characters"],
  );
  const uploaded = game.uploads[1];
  assert.deepEqual(uploaded.characters[0].equipment, [
    {
      slot: 2,
      itemId: 0,
      itemString: null,
      name: null,
      itemLevel: null,
      quality: null,
    },
    {
      slot: 16,
      itemId: 123,
      itemString: "item:123:0:-7:0",
      name: null,
      itemLevel: null,
      quality: null,
    },
  ]);
  assert.equal(
    uploaded.characters[0].equipment.some((row) => row.slot === 1),
    false,
  );
  assert.equal(Object.hasOwn(uploaded.characters[0], "region"), false);
  assert.equal(JSON.stringify(uploaded).includes("SYNTHETIC_ACCOUNT"), false);

  const retry = await game.scan(lost.state);
  assert.equal(retry.error, null);
  assert.deepEqual(kinds(retry.state), ["activity", "characters"]);
  assert.deepEqual(game.uploads[2], uploaded);
  await game.scan(retry.state);
  assert.equal(game.uploads.length, 3);
  const stateRoot = join(dirname(game.root), "state");
  await nativeAsync({
    operation: "storage",
    root: stateRoot,
    credentials: {
      deviceToken: token,
      deviceId: "11111111-1111-4111-8111-111111111111",
      sourceKey: Buffer.alloc(32, 9).toString("base64"),
    },
    state: retry.state,
  });
  const reloaded = await nativeAsync({
    operation: "storage",
    root: stateRoot,
  });
  const restarted = await game.scan(reloaded.state);
  assert.equal(restarted.error, null);
  assert.equal(
    game.uploads.length,
    3,
    "acknowledged snapshots must not replay after restart",
  );
  await game.save(`-- Formatting alone is not a new observation\n${lua}\n`);
  const reformatted = await game.scan(restarted.state);
  assert.equal(reformatted.error, null);
  assert.equal(game.uploads.length, 3);
});

test("native ingestion preserves observed absence without manufacturing unavailable facts or authority", async (t) => {
  const game = await fixture(t, "1.0.5");
  const capturedAt = Date.now() - 10_000;
  await game.save(`AurifyDB={version=1,sessions={},characterSchemaVersion=1,
    characters={{guid="Player-1-ABC",name="Synthetic",realm="Synthetic Realm",
      capturedAt=${capturedAt},spec="Restoration",guildRank="",guildClubId="",realmMode="pvp",level=60,
      ownerId="forged",rank="Guild Master",region="EU",specObservedAt=${capturedAt},
      equipment={}}}}`);
  const first = await game.scan();
  assert.equal(first.error, null);
  assert.deepEqual(
    game.uploads.map((payload) => payload.kind),
    ["activity", "characters"],
  );
  assert.deepEqual(game.uploads[1].characters, [
    {
      guid: "Player-1-ABC",
      name: "Synthetic",
      realm: "Synthetic Realm",
      class: null,
      level: 60,
      spec: "Restoration",
      guildRank: null,
      guildClubId: null,
      realmMode: "pvp",
      capturedAt,
      equipment: [],
    },
  ]);
  await game.save(characterSave(capturedAt + 1_000));
  const next = await game.scan(first.state);
  assert.equal(next.error, null);
  const uploaded = game.uploads.at(-1);
  assert.equal(uploaded.kind, "characters");
  assert.equal(uploaded.characters[0].capturedAt, capturedAt + 1_000);
  for (const field of [
    "spec",
    "guildRank",
    "guildClubId",
    "realmMode",
    "region",
  ])
    assert.equal(Object.hasOwn(uploaded.characters[0], field), false);
});

test("old Aurify data skips characters but malformed character schemas remain retryable", async (t) => {
  const game = await fixture(t);
  await game.save("AurifyDB={version=1,sessions={}}");
  const old = await game.scan();
  assert.equal(old.error, null);
  assert.deepEqual(kinds(old.state), ["activity"]);
  assert.deepEqual(
    game.uploads.map((payload) => payload.kind),
    ["activity"],
  );
  assert.equal(
    old.reports.some((report) => report.name === "snapshot-error"),
    false,
  );

  await game.save(
    "AurifyDB={version=1,sessions={},characterSchemaVersion=2,characters={}}",
  );
  const malformed = await game.scan(old.state);
  assert.ok(malformed.error);
  assert.deepEqual(kinds(malformed.state), ["activity"]);
  assert.equal(game.uploads.length, 1);
  assert.equal(
    malformed.reports.some(
      (report) =>
        report.name === "snapshot-error" && report.value === "characters",
    ),
    true,
  );

  await game.save(characterSave(Date.now() - 10_000));
  const recovered = await game.scan(malformed.state);
  assert.equal(recovered.error, null);
  assert.deepEqual(kinds(recovered.state), ["activity", "characters"]);
  assert.deepEqual(
    game.uploads.map((payload) => payload.kind),
    ["activity", "characters"],
  );
});

test("native character ingestion rejects malformed UTF-8 instead of changing identity", async (t) => {
  const game = await fixture(t);
  const lua = characterSave(Date.now() - 10_000);
  const [before, after] = lua.split("Synthetic Realm");
  await game.save(
    Buffer.concat([
      Buffer.from(before),
      Buffer.from([0xed, 0xa0, 0x80]),
      Buffer.from(after),
    ]),
  );
  const invalid = await game.scan();
  assert.equal(
    invalid.reports.some(
      (report) =>
        report.name === "snapshot-error" && report.value === "characters",
    ),
    true,
  );
  assert.deepEqual(game.uploads, []);
  await game.save(lua);
  const recovered = await game.scan(invalid.state);
  assert.equal(recovered.error, null);
  assert.equal(
    game.uploads.find((payload) => payload.kind === "characters").characters[0]
      .realm,
    "Synthetic Realm",
  );
});

test("native ingestion uploads exact per-slot icon metadata without accepting image URLs", async (t) => {
  const game = await fixture(t, "1.0.1");
  await game.save(`AurifyDB={version=1,sessions={},characterSchemaVersion=1,
    characters={{guid="Player-1-ABC",name="Synthetic",realm="Synthetic Realm",
      capturedAt=${Date.now() - 10_000},equipment={
        {slot=16,itemId=123,itemString="item:123:0:-7:0",
          iconFileDataId=134400,iconBuild="1.60.1.70450",
          iconUrl="https://untrusted.invalid/icon.png",iconPath="../../icon.png"},
        {slot=1,itemId=456,itemString="item:456",
          iconFileDataId=2147483647,iconBuild="1.60.0.1000"},
        {slot=2,itemId=0}
      }}}
  }`);
  const result = await game.scan();
  assert.equal(result.error, null);
  assert.deepEqual(kinds(result.state), ["activity", "characters"]);
  assert.deepEqual(
    game.uploads.map((payload) => payload.kind),
    ["activity", "characters"],
  );
  assert.deepEqual(game.uploads[1].characters[0].equipment, [
    {
      slot: 1,
      itemId: 456,
      itemString: "item:456",
      name: null,
      itemLevel: null,
      quality: null,
      iconFileDataId: 2147483647,
      iconBuild: "1.60.0.1000",
    },
    {
      slot: 2,
      itemId: 0,
      itemString: null,
      name: null,
      itemLevel: null,
      quality: null,
    },
    {
      slot: 16,
      itemId: 123,
      itemString: "item:123:0:-7:0",
      name: null,
      itemLevel: null,
      quality: null,
      iconFileDataId: 134400,
      iconBuild: "1.60.1.70450",
    },
  ]);
});

test("native ingestion rejects malformed icon pairs and retries corrected gear", async (t) => {
  const game = await fixture(t, "1.0.1");
  const capturedAt = Date.now() - 10_000;
  const occupied = 'slot=16,itemId=123,itemString="item:123"';
  const invalidRows = [
    `${occupied},iconFileDataId=134400`,
    `${occupied},iconBuild="1.60.1.70450"`,
    `${occupied},iconFileDataId="https://untrusted.invalid/icon.png",iconBuild="1.60.1.70450"`,
    `${occupied},iconFileDataId=0,iconBuild="1.60.1.70450"`,
    `${occupied},iconFileDataId=1.5,iconBuild="1.60.1.70450"`,
    `${occupied},iconFileDataId=2147483648,iconBuild="1.60.1.70450"`,
    `${occupied},iconFileDataId=134400,iconBuild="../../70450"`,
    `${occupied},iconFileDataId=134400,iconBuild="12.0.1.70450"`,
    `${occupied},iconFileDataId=134400,iconBuild="1.60.01.70450"`,
    `${occupied},iconFileDataId=134400,iconBuild="1.60.1.070450"`,
    'slot=2,itemId=0,iconFileDataId=134400,iconBuild="1.60.1.70450"',
  ];
  let state;
  for (const row of invalidRows) {
    await game.save(`AurifyDB={version=1,sessions={},characterSchemaVersion=1,
      characters={{guid="Player-1-ABC",name="Synthetic",realm="Synthetic Realm",
        capturedAt=${capturedAt},equipment={{${row}}}}}
    }`);
    const result = await game.scan(state);
    assert.ok(result.error, row);
    assert.equal(
      result.reports.some(
        (report) =>
          report.name === "snapshot-error" && report.value === "characters",
      ),
      true,
      row,
    );
    assert.deepEqual(kinds(result.state), ["activity"], row);
    state = result.state;
  }
  assert.deepEqual(
    game.uploads.map((payload) => payload.kind),
    ["activity"],
  );
  await game.save(characterSave(capturedAt));
  const recovered = await game.scan(state);
  assert.equal(recovered.error, null);
  assert.deepEqual(kinds(recovered.state), ["activity", "characters"]);
  assert.equal(game.uploads[1].characters[0].equipment[1].itemId, 123);
});

const modelClients = [
  { product: "wowf", build: "1.60.1.12345", buildKey: "a".repeat(32) },
  {
    product: "wow_classic_beta",
    build: "1.60.1.12345",
    buildKey: "b".repeat(32),
  },
];

async function modelClientFixture(t, clients = modelClients) {
  const directory = await mkdtemp(join(work, "model-client-"));
  t.after(() => rm(directory, { recursive: true, force: true, maxRetries: 5 }));
  const games = ["alpha", "bravo"].map((name) => join(directory, name));
  await mkdir(join(directory, "Data"));
  for (const [index, game] of games.entries()) {
    await mkdir(join(game, "WTF"), { recursive: true });
    await copyFile(join(work, "SyntheticGame.exe"), join(game, "Wow.exe"));
    await writeFile(
      join(game, ".flavor.info"),
      `Product Flavor!STRING:0\n${modelClients[index].product}\n`,
    );
  }
  await writeFile(
    join(directory, ".build.info"),
    [
      "Product!STRING:0|Version!STRING:0|Build Key!HEX:16",
      ...clients.map(
        ({ product, build, buildKey }) => `${product}|${build}|${buildKey}`,
      ),
      "",
    ].join("\n"),
  );
  return { directory, games };
}

test("native model client binds side-by-side products at the same version to the selected flavor", async (t) => {
  const { directory, games } = await modelClientFixture(t);
  for (const [index, game] of games.entries()) {
    assert.deepEqual(
      await nativeAsync({ operation: "model-client", root: game }),
      { client: modelClients[index], gameRoot: directory },
    );
  }
});

test("native model client fails closed on missing, malformed, unknown or conflicting selected flavor metadata", async (t) => {
  const { games } = await modelClientFixture(t);
  const flavorPath = join(games[0], ".flavor.info");
  const cases = [
    ["missing", null],
    ["malformed header", "Product!STRING:0\nwowf\n"],
    ["missing value", "Product Flavor!STRING:0\n"],
    ["unknown product", "Product Flavor!STRING:0\nwow\n"],
    [
      "conflicting products",
      "Product Flavor!STRING:0\nwowf\nwow_classic_beta\n",
    ],
  ];
  for (const [name, content] of cases) {
    if (content === null) await rm(flavorPath);
    else await writeFile(flavorPath, content);
    await assert.rejects(
      nativeAsync({ operation: "model-client", root: games[0] }),
      { type: "ModelFailure", message: /unsupported-client/ },
      name,
    );
  }
});

test("native model client rejects differing build keys for the selected product and version", async (t) => {
  const { games } = await modelClientFixture(t, [
    ...modelClients,
    { ...modelClients[0], buildKey: "c".repeat(32) },
  ]);
  await assert.rejects(
    nativeAsync({ operation: "model-client", root: games[0] }),
    { type: "ModelFailure", message: /unsupported-client/ },
  );
});

test("native model client cannot resolve a selected flavor with a different executable version", async (t) => {
  const { games } = await modelClientFixture(t, [
    { ...modelClients[0], build: "1.60.1.12346" },
    modelClients[1],
  ]);
  await assert.rejects(
    nativeAsync({ operation: "model-client", root: games[0] }),
    { type: "ModelFailure", message: /unsupported-client/ },
  );
});

test("character snapshots bind the selected installation and reupload when its build context changes", async (t) => {
  const game = await fixture(t, "1.0.3");
  await game.save(characterSave(Date.now() - 10_000));
  const legacy = await game.scan();
  assert.equal(Object.hasOwn(game.uploads.at(-1).source, "gameClient"), false);
  await mkdir(join(game.root, "Data"));
  await writeFile(
    join(game.root, ".flavor.info"),
    "Product Flavor!STRING:0\nwowf\n",
  );
  const writeBuilds = async (clients) =>
    writeFile(
      join(game.root, ".build.info"),
      [
        "Product!STRING:0|Version!STRING:0|Build Key!HEX:16",
        ...clients.map(
          ({ product, build, buildKey }) => `${product}|${build}|${buildKey}`,
        ),
        "",
      ].join("\n"),
    );
  await writeBuilds(modelClients);
  const bound = await game.scan(legacy.state);
  assert.equal(bound.error, null);
  assert.deepEqual(game.uploads.at(-1).source.gameClient, modelClients[0]);
  const count = game.uploads.length;
  await writeFile(
    join(game.root, ".flavor.info"),
    "Product Flavor!STRING:0\nwow_classic_beta\n",
  );
  const switched = await game.scan(bound.state);
  assert.equal(switched.error, null);
  assert.equal(game.uploads.length, count + 1);
  assert.equal(game.uploads.at(-1).kind, "characters");
  assert.deepEqual(game.uploads.at(-1).source.gameClient, modelClients[1]);
  // Corrupt optional installation metadata must also remove the old binding
  // without suppressing the ordinary character upload.
  await writeFile(join(game.root, ".flavor.info"), Buffer.from([0xff]));
  const undecodable = await game.scan(switched.state);
  assert.equal(undecodable.error, null);
  assert.equal(game.uploads.length, count + 2);
  assert.equal(Object.hasOwn(game.uploads.at(-1).source, "gameClient"), false);
  // Unsupported installation binding must not block otherwise valid character data.
  await writeFile(
    join(game.root, ".flavor.info"),
    "Product Flavor!STRING:0\nwow\n",
  );
  const unbound = await game.scan(undecodable.state);
  assert.equal(unbound.error, null);
  assert.equal(Object.hasOwn(game.uploads.at(-1).source, "gameClient"), false);
});

test("native ingestion retains bounded captured tooltips and stale actual appearance without refreshing its timestamp", async (t) => {
  const game = await fixture(t, "1.0.3");
  const capturedAt = Date.now() - 10_000;
  const appearanceAt = capturedAt - 86_400_000;
  await game.save(`AurifyDB={version=1,sessions={},characterSchemaVersion=1,
    characters={{guid="Player-1-ABC",name="Synthetic",realm="Synthetic Realm",
      capturedAt=${capturedAt},raceId=4,race="Night Elf",bodyType=1,classId=11,
      appearance={build="1.60.1.70450",capturedAt=${appearanceAt},raceId=4,bodyType=1,
        choices={{optionId=20,choiceId=200},{optionId=10,choiceId=100}}},
      raceIcon={build="1.60.1.70450",fileDataId=1662186,left=0,right=0.125,top=0.25,bottom=0.375},
      equipment={{slot=16,itemId=123,itemString="item:123:0:-7:0",
        tooltip={lines={{leftText="+12 Stamina",leftColor="00ff00"},{leftText="Damage",rightText="40 - 60"}}}}}}}}`);
  const result = await game.scan();
  assert.equal(result.error, null);
  const row = game.uploads.at(-1).characters[0];
  assert.equal(row.appearance.capturedAt, appearanceAt);
  assert.equal(Object.hasOwn(row.appearance, "modelId"), false);
  assert.deepEqual(row.appearance.choices, [
    { optionId: 10, choiceId: 100 },
    { optionId: 20, choiceId: 200 },
  ]);
  assert.deepEqual(row.equipment[0].tooltip, {
    lines: [
      { leftText: "+12 Stamina", leftColor: "00ff00" },
      { leftText: "Damage", rightText: "40 - 60" },
    ],
  });
  await game.save(characterSave(capturedAt + 1_000));
  const unavailable = await game.scan(result.state);
  assert.equal(unavailable.error, null);
  assert.equal(
    Object.hasOwn(game.uploads.at(-1).characters[0], "appearance"),
    false,
  );
});
