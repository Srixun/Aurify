import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import {
  copyFile,
  mkdir,
  mkdtemp,
  readFile,
  readdir,
  rm,
  symlink,
  writeFile,
} from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { crc32, deflateRawSync, gunzipSync } from "node:zlib";
import { native, nativeAsync } from "./native-bridge.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const work = join(root, ".work", "native-tests");
const catalog = [
  {
    key: "professions",
    directory: "ProfessionMaster",
    toc: "ProfessionMaster_Camelot.toc",
    title: "|cffDA8CFFProfession Master |c999999FFForever",
    version: "7.0.24",
  },
  {
    key: "overlord",
    directory: "Overlord",
    toc: "Overlord.toc",
    title: "Overlord Forever",
    version: "2.0.4",
  },
];

// Synthetic ZIP fixture writer; application extraction is performed by native code.
function syntheticZip(entries) {
  const localParts = [],
    centralParts = [];
  let offset = 0;
  for (const entry of entries) {
    const name = Buffer.from(entry.name);
    const localName = Buffer.from(entry.localName ?? entry.name);
    const directory = entry.name.endsWith("/");
    const bytes = Buffer.from(entry.bytes ?? "");
    const method = entry.method ?? (directory ? 0 : 8);
    const compressed =
      entry.compressedBytes ?? (method === 8 ? deflateRawSync(bytes) : bytes);
    const checksum = entry.checksum ?? crc32(bytes);
    const size = entry.size ?? bytes.length;
    const flags = entry.flags ?? 0x0800;
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(flags, 6);
    local.writeUInt16LE(method, 8);
    local.writeUInt16LE(33, 12);
    local.writeUInt32LE(checksum, 14);
    local.writeUInt32LE(compressed.length, 18);
    local.writeUInt32LE(size, 22);
    local.writeUInt16LE(localName.length, 26);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(0x0314, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(flags, 8);
    central.writeUInt16LE(method, 10);
    central.writeUInt16LE(33, 14);
    central.writeUInt32LE(checksum, 16);
    central.writeUInt32LE(compressed.length, 20);
    central.writeUInt32LE(size, 24);
    central.writeUInt16LE(name.length, 28);
    central.writeUInt32LE(
      entry.attributes ??
        (((directory ? 0o40755 : 0o100644) << 16) | (directory ? 0x10 : 0)) >>>
          0,
      38,
    );
    central.writeUInt32LE(offset, 42);
    localParts.push(local, localName, compressed);
    centralParts.push(central, name);
    offset += local.length + localName.length + compressed.length;
  }
  const central = Buffer.concat(centralParts);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(central.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...localParts, central, end]);
}

function toc(pkg, overrides = {}) {
  const info = { ...pkg, interface: 16001, ...overrides };
  return `## Title: ${info.title}\n## Version: ${info.version}\n## Interface: ${info.interface}\nCore.lua\n`;
}
function entries(pkg) {
  return [
    { name: `${pkg.directory}/` },
    { name: `${pkg.directory}/${pkg.toc}`, bytes: toc(pkg) },
    { name: `${pkg.directory}/Core.lua`, bytes: "-- synthetic addon\n" },
    {
      name: `${pkg.directory}/nested/Locale.lua`,
      bytes: "-- synthetic locale\n",
    },
  ];
}
function packages(change = (items) => items) {
  return Object.fromEntries(
    catalog.map((pkg) => {
      const bytes = syntheticZip(change(entries(pkg), pkg));
      return [
        pkg.key,
        {
          data: bytes.toString("base64"),
          sha256: createHash("sha256").update(bytes).digest("hex"),
        },
      ];
    }),
  );
}
async function fixture(t) {
  const path = await mkdtemp(join(work, "addons-"));
  t.after(() => rm(path, { recursive: true, force: true, maxRetries: 5 }));
  const game = join(path, "game");
  const addons = join(game, "Interface", "AddOns");
  await mkdir(addons, { recursive: true });
  await mkdir(join(game, "WTF"), { recursive: true });
  await copyFile(join(work, "SyntheticGame.exe"), join(game, "Wow.exe"));
  await writeFile(
    join(game, "WTF", "settings.txt"),
    "synthetic settings preserved",
  );
  return {
    path,
    game,
    addons,
    request: {
      operation: "addons",
      root: game,
      bundle: join(root, "companion", "addon", "Aurify"),
      packages: packages(),
    },
  };
}
async function preserved(fixture) {
  assert.equal(
    await readFile(join(fixture.game, "WTF", "settings.txt"), "utf8"),
    "synthetic settings preserved",
  );
  assert.deepEqual(
    await readFile(join(fixture.game, "Wow.exe")),
    await readFile(join(work, "SyntheticGame.exe")),
  );
  assert.equal(
    (await readdir(fixture.addons)).some((name) => name.startsWith(".aurify-")),
    false,
  );
}
async function rejected(fixture, result, retryable = false) {
  assert.equal(result.setup.Results.professions.Status, "failed");
  assert.equal(result.setup.Results.professions.Retryable, retryable);
  assert.equal(
    result.setup.Results.overlord.Status,
    "installed",
    JSON.stringify(result.setup.Results.overlord),
  );
  await assert.rejects(
    readFile(join(fixture.addons, "ProfessionMaster", "Core.lua")),
    { code: "ENOENT" },
  );
  await preserved(fixture);
}

test("native installer defers writes while WoW runs and still recognizes current addons", async (t) => {
  const game = await fixture(t);
  game.request.running = true;
  const deferred = native(game.request);
  for (const key of ["professions", "overlord", "activity"])
    assert.equal(deferred.setup.Results[key].Status, "deferred");
  assert.deepEqual(deferred.downloads, []);
  for (const pkg of catalog)
    await assert.rejects(
      readFile(join(game.addons, pkg.directory, "Core.lua")),
      { code: "ENOENT" },
    );

  game.request.running = false;
  const installed = native(game.request);
  for (const key of ["professions", "overlord", "activity"])
    assert.equal(
      installed.setup.Results[key].Status,
      "installed",
      `${key}: ${JSON.stringify(installed.setup.Results[key])}`,
    );
  for (const pkg of catalog)
    assert.equal(
      await readFile(join(game.addons, pkg.directory, "Core.lua"), "utf8"),
      "-- synthetic addon\n",
    );

  game.request.running = true;
  const current = native(game.request);
  assert.deepEqual(current.downloads, []);
  for (const key of ["professions", "overlord", "activity"])
    assert.equal(current.setup.Results[key].Status, "present");
  assert.equal(current.setup.RestartRequired, false);
  await preserved(game);
});

test("native installer reports unsupported unmanaged versions and preserves supported newer versions", async (t) => {
  const game = await fixture(t);
  for (const [index, pkg] of catalog.entries()) {
    await mkdir(join(game.addons, pkg.directory));
    await writeFile(
      join(game.addons, pkg.directory, pkg.toc),
      toc(pkg, { version: index ? "2.0.8" : "1.0" }),
    );
    await writeFile(
      join(game.addons, pkg.directory, "Core.lua"),
      "existing version",
    );
  }
  const result = native(game.request);
  assert.deepEqual(result.downloads, []);
  for (const pkg of catalog) {
    assert.equal(
      result.setup.Results[pkg.key].Status,
      pkg.key === "professions" ? "blocked" : "present",
    );
    assert.equal(
      await readFile(join(game.addons, pkg.directory, "Core.lua"), "utf8"),
      "existing version",
    );
  }
  await preserved(game);
});

test("occupied unknown or incompatible addon paths are never replaced", async (t) => {
  for (const kind of ["folder", "file", "incompatible"])
    await t.test(kind, async (t) => {
      const game = await fixture(t),
        target = join(game.addons, "ProfessionMaster");
      if (kind === "file") await writeFile(target, "keep");
      else {
        await mkdir(target);
        await writeFile(join(target, "keep.txt"), "keep");
        if (kind === "incompatible")
          await writeFile(
            join(target, catalog[0].toc),
            toc(catalog[0], { interface: 99999 }),
          );
      }
      const result = native(game.request);
      assert.equal(result.setup.Results.professions.Status, "blocked");
      assert.deepEqual(result.downloads, ["overlord"]);
      assert.equal(
        await readFile(
          kind === "file" ? target : join(target, "keep.txt"),
          "utf8",
        ),
        "keep",
      );
      await preserved(game);
    });
});

test("native download failure is isolated and later retries only the missing addon", async (t) => {
  const game = await fixture(t),
    valid = packages();
  game.request.packages.professions.sha256 = "0".repeat(64);
  await rejected(game, native(game.request));
  game.request.packages = valid;
  const retry = native(game.request);
  assert.deepEqual(retry.downloads, ["professions"]);
  assert.equal(retry.setup.Results.professions.Status, "installed");
  assert.equal(retry.setup.Results.overlord.Status, "present");
});

test("native ZIP paths and case aliases cannot escape or partially publish", async (t) => {
  for (const name of [
    "ProfessionMaster/../../escaped.lua",
    "/escaped.lua",
    "C:/escaped.lua",
    "ProfessionMaster\\escaped.lua",
    "ProfessionMaster/file.lua:stream",
    "ProfessionMaster/NUL.lua",
    "ProfessionMaster/alias.",
    "ProfessionMaster/alias ",
    "ProfessionMaster/Core.LUA",
    "ProfessionMaster/Core.lua",
    "ProfessionMaster/NESTED/other.lua",
    "ProfessionMaster/Core.lua/child.lua",
    "OtherAddon/escaped.lua",
    "ProfessionMaster//escaped.lua",
  ]) {
    await t.test(name, async (t) => {
      const game = await fixture(t);
      game.request.packages = packages((items, pkg) =>
        pkg.key === "professions"
          ? [...items, { name, bytes: "unsafe" }]
          : items,
      );
      await rejected(game, native(game.request));
      await assert.rejects(
        readFile(join(game.game, "Interface", "escaped.lua")),
        { code: "ENOENT" },
      );
    });
  }
});

test("native ZIP headers types CRC and truncation fail atomically", async (t) => {
  for (const [name, properties] of [
    ["symlink", { attributes: (0o120777 << 16) >>> 0 }],
    ["fifo", { attributes: (0o010600 << 16) >>> 0 }],
    ["reparse", { attributes: 0x400 }],
    ["encrypted", { flags: 0x0801 }],
    ["compression", { method: 99 }],
    ["local-name", { localName: "ProfessionMaster/Other.lua" }],
    ["CRC", { checksum: 0 }],
    ["short", { size: 1000 }],
  ])
    await t.test(name, async (t) => {
      const game = await fixture(t);
      game.request.packages = packages((items, pkg) =>
        pkg.key === "professions"
          ? [
              ...items,
              {
                name: "ProfessionMaster/Late.lua",
                bytes: "synthetic",
                ...properties,
              },
            ]
          : items,
      );
      await rejected(game, native(game.request));
    });
});

test("native addon publication requires exact pinned TOC identity", async (t) => {
  for (const overrides of [
    { title: "Other addon" },
    { version: "99.0" },
    { interface: 99999 },
  ]) {
    const game = await fixture(t);
    game.request.packages = packages((items, pkg) =>
      items.map((item) =>
        pkg.key === "professions" && item.name.endsWith(pkg.toc)
          ? { ...item, bytes: toc(pkg, overrides) }
          : item,
      ),
    );
    await rejected(game, native(game.request));
  }
});

test("native download and expansion budgets reject oversized archives", async (t) => {
  for (const mode of ["advertised", "truncated", "entries", "file", "total"])
    await t.test(mode, async (t) => {
      const game = await fixture(t);
      if (mode === "advertised")
        game.request.packages.professions.length = 16 * 1024 * 1024 + 1;
      else if (mode === "truncated")
        game.request.packages.professions.length =
          Buffer.from(game.request.packages.professions.data, "base64").length +
          1;
      else
        game.request.packages = packages((items, pkg) =>
          pkg.key !== "professions"
            ? items
            : [
                ...items,
                ...Array.from(
                  {
                    length:
                      mode === "entries" ? 1024 : mode === "total" ? 9 : 1,
                  },
                  (_, index) => ({
                    name: `ProfessionMaster/large-${index}.lua`,
                    bytes:
                      mode === "entries"
                        ? ""
                        : Buffer.alloc(
                            8 * 1024 * 1024 + (mode === "file" ? 1 : 0),
                          ),
                  }),
                ),
              ],
        );
      await rejected(game, native(game.request), mode === "truncated");
    });
});

test(
  "native download timeout allows the other addon to finish",
  { timeout: 45_000 },
  async (t) => {
    const game = await fixture(t);
    game.request.packages.professions.stalled = true;
    await rejected(game, await nativeAsync(game.request), true);
  },
);

test("concurrent valid addon arrival is adopted without replacement", async (t) => {
  const game = await fixture(t);
  game.request.packages.professions.occupy = toc(catalog[0], {
    version: "7.1.0",
  });
  const result = native(game.request);
  assert.equal(result.setup.Results.professions.Status, "present");
  assert.equal(result.installation.Addons.professions.Status, "ready");
  assert.equal(result.installation.Addons.professions.Version, "7.1.0");
  assert.equal(
    await readFile(join(game.addons, "ProfessionMaster", "keep.txt"), "utf8"),
    "concurrent install preserved",
  );
  await preserved(game);
});

test("concurrent unsupported addon arrival is preserved but never accepted as ready", async (t) => {
  const game = await fixture(t);
  game.request.packages.professions.occupy = toc(catalog[0], {
    version: "8.0",
  });
  const result = native(game.request);
  assert.equal(result.setup.Results.professions.Status, "blocked");
  assert.notEqual(result.installation.Addons.professions.Status, "ready");
  assert.equal(
    await readFile(join(game.addons, "ProfessionMaster", "keep.txt"), "utf8"),
    "concurrent install preserved",
  );
  await preserved(game);
});

test("junction destinations never redirect addon writes", async (t) => {
  const game = await fixture(t),
    outside = join(game.path, "outside");
  await mkdir(outside);
  await writeFile(join(outside, "sentinel"), "untouched");
  await symlink(outside, join(game.addons, "ProfessionMaster"), "junction");
  const result = native(game.request);
  assert.equal(result.setup.Results.professions.Status, "blocked");
  assert.deepEqual(await readdir(outside), ["sentinel"]);
  assert.equal(result.setup.Results.overlord.Status, "installed");
});

test("genuine shipped legacy helper refresh preserves extra files after hash-based adoption", async (t) => {
  const game = await fixture(t);
  const helper = join(game.addons, "Aurify");
  await mkdir(helper);
  await writeFile(join(helper, "keep.txt"), "keep");
  // Historical adoption must use the genuinely shipped bytes, not today's bundle.
  for (const name of ["Aurify.toc", "Aurify.lua"]) {
    await writeFile(
      join(helper, name),
      gunzipSync(
        await readFile(
          new URL(`./fixtures/aurify-1.0.0/${name}.gz`, import.meta.url),
        ),
      ),
    );
  }
  const result = native(game.request);
  assert.equal(result.setup.Results.activity.Status, "installed");
  for (const name of ["Aurify.toc", "Aurify.lua"])
    assert.deepEqual(
      await readFile(join(helper, name)),
      await readFile(join(game.request.bundle, name)),
    );
  assert.equal(await readFile(join(helper, "keep.txt"), "utf8"), "keep");
  await preserved(game);
});

test("trusted stale packages refresh owned files without losing extras or saved settings", async (t) => {
  const game = await fixture(t);
  for (const pkg of catalog) {
    const directory = join(game.addons, pkg.directory);
    await mkdir(directory);
    const files = {
      [pkg.toc]: toc(pkg, { version: "1.0.0" }),
      "Core.lua": "-- old official code\n",
    };
    for (const [name, content] of Object.entries(files))
      await writeFile(join(directory, name), content);
    await writeFile(
      join(directory, ".aurify-package.json"),
      JSON.stringify({
        product: "Aurify",
        package: pkg.key,
        version: "1.0.0",
        hashes: Object.fromEntries(
          Object.entries(files).map(([name, content]) => [
            name,
            createHash("sha256").update(content).digest("hex"),
          ]),
        ),
      }),
    );
    await writeFile(
      join(directory, "user-extra.lua"),
      "-- retained custom extra\n",
    );
  }
  const result = native(game.request);
  assert.deepEqual(result.downloads, ["professions", "overlord"]);
  for (const pkg of catalog) {
    assert.equal(result.setup.Results[pkg.key].Status, "installed");
    assert.equal(result.installation.Addons[pkg.key].Version, pkg.version);
    assert.equal(
      await readFile(join(game.addons, pkg.directory, "Core.lua"), "utf8"),
      "-- synthetic addon\n",
    );
    assert.equal(
      await readFile(
        join(game.addons, pkg.directory, "user-extra.lua"),
        "utf8",
      ),
      "-- retained custom extra\n",
    );
  }
  assert.deepEqual(native(game.request).downloads, []);
  await preserved(game);
});

test("transaction journal recovers interrupted managed refreshes without losing settings or extras", async (t) => {
  for (const checkpoint of ["prepared", "backed-up", "published"])
    await t.test(checkpoint, async (t) => {
      const game = await fixture(t);
      const pkg = catalog[0];
      const directory = join(game.addons, pkg.directory);
      await mkdir(directory);
      const files = {
        [pkg.toc]: toc(pkg, { version: "1.0.0" }),
        "Core.lua": "-- old official code\n",
      };
      for (const [name, content] of Object.entries(files))
        await writeFile(join(directory, name), content);
      await writeFile(
        join(directory, ".aurify-package.json"),
        JSON.stringify({
          product: "Aurify",
          package: pkg.key,
          version: "1.0.0",
          hashes: Object.fromEntries(
            Object.entries(files).map(([name, content]) => [
              name,
              createHash("sha256").update(content).digest("hex"),
            ]),
          ),
        }),
      );
      await writeFile(
        join(directory, "user-extra.lua"),
        "-- retained through recovery\n",
      );

      game.request.checkpoint = checkpoint;
      const interrupted = native(game.request);
      assert.equal(interrupted.setup.Results.professions.Status, "failed");
      assert.ok(
        (await readdir(game.addons)).includes(
          ".aurify-transaction-ProfessionMaster.json",
        ),
      );

      delete game.request.checkpoint;
      game.request.unavailable = true;
      const unavailable = native(game.request);
      assert.equal(unavailable.setup.Results.professions.Status, "unavailable");
      assert.equal(
        await readFile(join(directory, "Core.lua"), "utf8"),
        "-- old official code\n",
      );
      assert.equal(
        (await readdir(game.addons)).includes(
          ".aurify-transaction-ProfessionMaster.json",
        ),
        false,
      );
      delete game.request.unavailable;
      const recovered = native(game.request);
      assert.equal(recovered.setup.Results.professions.Status, "installed");
      assert.equal(
        await readFile(join(directory, "Core.lua"), "utf8"),
        "-- synthetic addon\n",
      );
      assert.equal(
        await readFile(join(directory, "user-extra.lua"), "utf8"),
        "-- retained through recovery\n",
      );
      await preserved(game);
    });
});

test("modified stale managed files block replacement before any package file changes", async (t) => {
  const game = await fixture(t);
  const pkg = catalog[0];
  const directory = join(game.addons, pkg.directory);
  await mkdir(directory);
  const oldToc = toc(pkg, { version: "1.0.0" });
  await writeFile(join(directory, pkg.toc), oldToc);
  await writeFile(join(directory, "Core.lua"), "-- local modification\n");
  await writeFile(
    join(directory, ".aurify-package.json"),
    JSON.stringify({
      product: "Aurify",
      package: pkg.key,
      version: "1.0.0",
      hashes: {
        [pkg.toc]: createHash("sha256").update(oldToc).digest("hex"),
        "Core.lua": createHash("sha256")
          .update("-- old official code\n")
          .digest("hex"),
      },
    }),
  );
  const result = native(game.request);
  assert.equal(result.setup.Results.professions.Status, "blocked");
  assert.equal(await readFile(join(directory, pkg.toc), "utf8"), oldToc);
  assert.equal(
    await readFile(join(directory, "Core.lua"), "utf8"),
    "-- local modification\n",
  );
  await preserved(game);
});

test("newer managed helper is not downgraded by an older bundled release", async (t) => {
  const game = await fixture(t);
  const directory = join(game.addons, "Aurify");
  await mkdir(directory);
  const current =
    "## Title: Aurify\n## Interface: 16001\n## Version: 99.0.0\n## X-Aurify-Managed: 1\nAurify.lua\n";
  await writeFile(join(directory, "Aurify.toc"), current);
  await writeFile(join(directory, "Aurify.lua"), "-- newer helper\n");
  const result = native(game.request);
  assert.equal(result.setup.Results.activity.Status, "present");
  assert.equal(await readFile(join(directory, "Aurify.toc"), "utf8"), current);
  assert.equal(
    await readFile(join(directory, "Aurify.lua"), "utf8"),
    "-- newer helper\n",
  );
  await preserved(game);
});

test("modified helper Lua is preserved despite its managed TOC marker", async (t) => {
  const game = await fixture(t);
  native(game.request);
  const helper = join(game.addons, "Aurify");
  const originalToc = await readFile(join(helper, "Aurify.toc"));
  const modified = "-- locally modified helper must survive\n";
  await writeFile(join(helper, "Aurify.lua"), modified);
  const result = native(game.request);
  assert.equal(result.setup.Results.activity.Status, "blocked");
  assert.equal(await readFile(join(helper, "Aurify.lua"), "utf8"), modified);
  assert.deepEqual(await readFile(join(helper, "Aurify.toc")), originalToc);
  await preserved(game);
});

test("unknown marker-only legacy helper remains untouched and requires attention", async (t) => {
  const game = await fixture(t);
  const helper = join(game.addons, "Aurify");
  await mkdir(helper);
  const oldToc =
    "## Title: Aurify\n## Interface: 16001\n## Version: 0.5\n## X-Aurify-Managed: 1\nAurify.lua\n";
  await writeFile(join(helper, "Aurify.toc"), oldToc);
  await writeFile(join(helper, "Aurify.lua"), "-- unknown legacy bytes\n");
  const result = native(game.request);
  assert.equal(result.setup.Results.activity.Status, "update-needed");
  assert.equal(await readFile(join(helper, "Aurify.toc"), "utf8"), oldToc);
  assert.equal(
    await readFile(join(helper, "Aurify.lua"), "utf8"),
    "-- unknown legacy bytes\n",
  );
  assert.equal((await readdir(helper)).includes(".aurify-package.json"), false);
  await preserved(game);
});
