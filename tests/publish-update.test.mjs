import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import {
  constants,
  createHash,
  generateKeyPairSync,
  verify,
} from "node:crypto";
import {
  copyFile,
  mkdir,
  mkdtemp,
  readFile,
  readdir,
  rm,
  writeFile,
} from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import test from "node:test";
import {
  createUpdatePublication,
  parseUpdateCatalog,
} from "../tools/publish-update.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const hash = (bytes) => createHash("sha256").update(bytes).digest("hex");

function releaseMetadata(executable, version) {
  const hashes = {
    "Aurify.exe": hash(executable),
    "addon/Aurify/Aurify.toc": hash("toc"),
    "addon/Aurify/Aurify.lua": hash("lua"),
  };
  return {
    schema: 1,
    product: "Aurify",
    version,
    releaseId: hash(
      Object.entries(hashes)
        .map(([name, digest]) => `${name}\n${digest}\n`)
        .join(""),
    ),
    payloadSha256: hashes["Aurify.exe"],
    payloadLength: executable.length,
    hashes,
    toolchain: {},
  };
}

async function snapshot(directory, relative = "") {
  const result = [];
  for (const entry of await readdir(join(directory, relative), {
    withFileTypes: true,
  })) {
    const name = relative ? `${relative}/${entry.name}` : entry.name;
    if (entry.isDirectory()) result.push(...(await snapshot(directory, name)));
    else result.push([name, await readFile(join(directory, name), "base64")]);
  }
  return result.sort(([left], [right]) => left.localeCompare(right));
}

test("publisher retains authenticated releases and rejects rollback or replay without mutating output", async (t) => {
  const work = join(root, ".work");
  await mkdir(work, { recursive: true });
  const directory = await mkdtemp(join(work, "publish-update-test-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  await mkdir(join(directory, "tools"));
  await mkdir(join(directory, "companion"));
  const script = join(directory, "tools", "publish-update.mjs");
  await copyFile(join(root, "tools", "publish-update.mjs"), script);

  const { privateKey, publicKey } = generateKeyPairSync("rsa", {
    modulusLength: 3072,
  });
  const jwk = publicKey.export({ format: "jwk" });
  const publicXml = `<RSAKeyValue><Modulus>${Buffer.from(jwk.n, "base64url").toString("base64")}</Modulus><Exponent>${Buffer.from(jwk.e, "base64url").toString("base64")}</Exponent></RSAKeyValue>`;
  await writeFile(
    join(directory, "companion", "update-public-key.xml"),
    publicXml,
  );
  const keyPath = join(directory, "synthetic-private.pem");
  const privateKeyPem = privateKey.export({ format: "pem", type: "pkcs8" });
  await writeFile(keyPath, privateKeyPem, { mode: 0o600 });

  const executable = await readFile(
    join(root, ".work", "native-tests", "AurifyTest.exe"),
  );
  const metadata = releaseMetadata(executable, "2.0.6");
  const dist = join(directory, "dist");
  const catalogPath = join(dist, "Aurify-updates.json");
  const legacyPath = join(dist, "Aurify-update.json");
  async function cleanBuild() {
    await rm(dist, { recursive: true, force: true });
    await mkdir(dist);
    await writeFile(join(dist, "Aurify.exe"), executable);
    await writeFile(
      join(dist, "Aurify-release.json"),
      `${JSON.stringify(metadata)}\n`,
    );
  }
  function run(sequence, ...args) {
    const result = spawnSync(
      process.execPath,
      [script, "--key", keyPath, "--sequence", String(sequence), ...args],
      {
        cwd: directory,
        encoding: "utf8",
        timeout: 15000,
      },
    );
    assert.ifError(result.error);
    assert.equal(result.signal, null);
    return result;
  }
  async function refuse(sequence, ...args) {
    const before = await snapshot(dist);
    const result = run(sequence, ...args);
    assert.notEqual(result.status, 0, result.stdout);
    assert.deepEqual(
      await snapshot(dist),
      before,
      "failed publication must preserve every dist artifact",
    );
  }

  await cleanBuild();
  await refuse(1);
  assert.equal(run(1, "--first-release").status, 0);
  const bootstrap = parseUpdateCatalog(
    await readFile(catalogPath, "utf8"),
    publicXml,
    Date.now(),
  );
  assert.equal(bootstrap.releases[0].version, "2.0.6");

  const historicalSource = join(directory, "Historical.cs");
  const historicalPath = join(directory, "Aurify-old.exe");
  await writeFile(
    historicalSource,
    'using System.Reflection; [assembly:AssemblyVersion("2.0.5.0")] internal static class Historical { static void Main() {} }',
  );
  const compiled = spawnSync(
    join(
      process.env.SystemRoot,
      "Microsoft.NET",
      "Framework64",
      "v4.0.30319",
      "csc.exe",
    ),
    ["/nologo", "/target:winexe", `/out:${historicalPath}`, historicalSource],
    { encoding: "utf8", timeout: 15000 },
  );
  assert.equal(compiled.status, 0, compiled.stdout + compiled.stderr);
  const historicalExecutable = await readFile(historicalPath);
  const historical = createUpdatePublication(
    releaseMetadata(historicalExecutable, "2.0.5"),
    historicalExecutable,
    privateKeyPem,
    publicXml,
    40,
    null,
    1000,
    2000,
  );
  const history = join(directory, "history");
  const historyReleases = join(history, "Aurify-releases");
  await mkdir(historyReleases, { recursive: true });
  const previousPath = join(history, "Aurify-updates.json");
  await writeFile(previousPath, historical.catalogEnvelope);
  await writeFile(
    join(historyReleases, `${historical.catalog.releases[0].releaseId}.exe`),
    historicalExecutable,
  );

  await cleanBuild();
  await refuse(40, "--previous", previousPath);
  await refuse(41, "--previous", join(directory, "missing.json"));
  const invalidPath = join(history, "invalid.json");
  const invalid = JSON.parse(historical.catalogEnvelope);
  const signature = Buffer.from(invalid.signature, "base64");
  signature[0] ^= 1;
  invalid.signature = signature.toString("base64");
  await writeFile(invalidPath, JSON.stringify(invalid));
  await refuse(41, "--previous", invalidPath);

  const retainedPath = join(
    historyReleases,
    `${historical.catalog.releases[0].releaseId}.exe`,
  );
  await rm(retainedPath);
  await refuse(41, "--previous", previousPath);
  await writeFile(retainedPath, historicalExecutable);

  // A legacy channel keeps its watermark and its exact previous executable.
  const legacyHistoryPath = join(history, "Aurify-update.json");
  await writeFile(legacyHistoryPath, historical.legacyEnvelope);
  await writeFile(join(history, "Aurify.exe"), historicalExecutable);
  await writeFile(legacyPath, historical.legacyEnvelope);
  await refuse(39, "--first-release");
  await refuse(40, "--previous", legacyHistoryPath);
  const migrated = run(41, "--previous", legacyHistoryPath);
  assert.equal(migrated.status, 0, migrated.stderr);
  assert.deepEqual(
    parseUpdateCatalog(
      await readFile(catalogPath, "utf8"),
      publicXml,
    ).releases.map(({ version, sequence }) => ({ version, sequence })),
    [
      { version: "2.0.6", sequence: 41 },
      { version: "2.0.5", sequence: 40 },
    ],
  );
  await cleanBuild();
  await writeFile(legacyPath, historical.legacyEnvelope);

  // Fail the second manifest promotion after the first has committed.
  const faultHook = join(directory, "fail-promotion.mjs");
  await writeFile(
    faultHook,
    [
      'import fs from "node:fs";',
      'import { syncBuiltinESMExports } from "node:module";',
      "const rename = fs.promises.rename;",
      "let failed = false;",
      "fs.promises.rename = async (from, to) => {",
      '  if (!failed && String(to).endsWith("Aurify-updates.json")) {',
      '    failed = true; throw new Error("synthetic catalog promotion failure");',
      "  }",
      "  return rename(from, to);",
      "};",
      "syncBuiltinESMExports();",
    ].join("\n"),
  );
  const failed = spawnSync(
    process.execPath,
    [
      "--import",
      pathToFileURL(faultHook).href,
      script,
      "--key",
      keyPath,
      "--sequence",
      "41",
      "--previous",
      previousPath,
    ],
    { cwd: directory, encoding: "utf8", timeout: 15000 },
  );
  assert.equal(failed.status, 1, failed.stderr);
  assert.match(failed.stderr, /synthetic catalog promotion failure/);
  assert.equal(await readFile(legacyPath, "utf8"), historical.legacyEnvelope);
  await assert.rejects(readFile(catalogPath), { code: "ENOENT" });
  assert.deepEqual(
    await readFile(join(dist, "Aurify-releases", `${metadata.releaseId}.exe`)),
    executable,
  );

  function compete() {
    return new Promise((resolve, reject) => {
      const child = spawn(
        process.execPath,
        [
          script,
          "--key",
          keyPath,
          "--sequence",
          "41",
          "--previous",
          previousPath,
        ],
        { cwd: directory, stdio: "ignore", timeout: 15000 },
      );
      child.once("error", reject);
      child.once("close", (code, signal) => {
        if (signal) reject(new Error(`Publisher terminated with ${signal}`));
        else resolve(code);
      });
    });
  }
  const competing = await Promise.all([compete(), compete()]);
  assert.equal(
    competing.filter((code) => code === 0).length,
    1,
    "only one competing publisher may claim a sequence",
  );

  const catalogText = await readFile(catalogPath, "utf8");
  const catalog = parseUpdateCatalog(catalogText, publicXml, Date.now());
  assert.deepEqual(
    catalog.releases.map(({ version, sequence }) => ({ version, sequence })),
    [
      { version: "2.0.6", sequence: 41 },
      { version: "2.0.5", sequence: 40 },
    ],
  );
  assert.deepEqual(
    await readFile(
      join(dist, "Aurify-releases", `${catalog.releases[0].releaseId}.exe`),
    ),
    executable,
  );
  assert.deepEqual(
    await readFile(
      join(
        dist,
        "Aurify-releases",
        `${historical.catalog.releases[0].releaseId}.exe`,
      ),
    ),
    historicalExecutable,
  );

  const legacy = JSON.parse(await readFile(legacyPath, "utf8"));
  const legacyBytes = Buffer.from(legacy.payload, "base64");
  assert.equal(
    verify(
      "sha256",
      legacyBytes,
      { key: publicKey, padding: constants.RSA_PKCS1_PADDING },
      Buffer.from(legacy.signature, "base64"),
    ),
    true,
  );
  assert.deepEqual(JSON.parse(legacyBytes), {
    schema: 1,
    product: "Aurify",
    channel: "stable",
    sequence: 41,
    issuedAt: catalog.issuedAt,
    expiresAt: catalog.expiresAt,
    releaseId: metadata.releaseId,
    payloadPath: `/members/api/aurify/download?release=${metadata.releaseId}`,
    payloadSha256: hash(executable),
    payloadLength: executable.length,
  });

  await refuse(41, "--previous", previousPath);
  await refuse(42, "--previous", previousPath);
  await refuse(42, "--first-release");
});
