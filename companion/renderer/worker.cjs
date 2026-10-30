"use strict";
const fs = require("node:fs/promises");
const path = require("node:path");
const { createHash } = require("node:crypto");
const { pathToFileURL } = require("node:url");
const MAX_JSON = 4 * 1024 * 1024;
const MAX_MODEL = 64 * 1024 * 1024;
const codes = new Set([
  "invalid-request",
  "client-mismatch",
  "invalid-appearance",
  "incomplete-equipment",
  "unsupported-tabard",
  "unsupported-equipment",
  "missing-game-data",
  "incomplete-materials",
  "invalid-artifact",
  "renderer-failed",
]);
const fail = (code) => {
  throw new Error(code);
};
const inside = (root, candidate) => {
  const relative = path.relative(root, candidate);
  return (
    relative === "" ||
    (!relative.startsWith(".." + path.sep) &&
      relative !== ".." &&
      !path.isAbsolute(relative))
  );
};
async function plainPath(file, directory) {
  if (
    typeof file !== "string" ||
    !path.isAbsolute(file) ||
    file.includes("\0") ||
    file.startsWith("\\\\")
  )
    fail("invalid-request");
  const absolute = path.resolve(file);
  const parsed = path.parse(absolute);
  let current = parsed.root;
  for (const component of absolute
    .slice(parsed.root.length)
    .split(path.sep)
    .filter(Boolean)) {
    current = path.join(current, component);
    const info = await fs.lstat(current);
    if (info.isSymbolicLink()) fail("invalid-request");
  }
  const info = await fs.lstat(absolute);
  if (directory ? !info.isDirectory() : !info.isFile()) fail("invalid-request");
  if (
    path.resolve(await fs.realpath(absolute)).toLowerCase() !==
    absolute.toLowerCase()
  )
    fail("invalid-request");
  return absolute;
}
async function rejectAssetLinks(root) {
  let count = 0;
  const walk = async (directory) => {
    for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
      if (++count > 200000 || entry.isSymbolicLink()) fail("missing-game-data");
      const file = path.join(directory, entry.name);
      if (entry.isDirectory()) await walk(file);
      else if (!entry.isFile()) fail("missing-game-data");
    }
  };
  await walk(root);
}
function clientValid(client) {
  return (
    client &&
    /^\d+\.\d+\.\d+\.\d+$/.test(client.build) &&
    ["wowf", "wow_classic_beta"].includes(client.product) &&
    /^[a-f0-9]{32}$/.test(client.buildKey)
  );
}
function sameClient(a, b) {
  return (
    clientValid(a) &&
    clientValid(b) &&
    a.build === b.build &&
    a.product === b.product &&
    a.buildKey === b.buildKey
  );
}
async function run(api) {
  let outputDir;
  let finishing = false;
  const finish = async (result) => {
    if (finishing) return;
    finishing = true;
    try {
      if (outputDir) {
        const bytes = Buffer.from(JSON.stringify(result));
        if (bytes.length > MAX_JSON) throw new Error("invalid-artifact");
        await fs.writeFile(path.join(outputDir, "result.json.tmp"), bytes, {
          flag: "wx",
        });
        await fs.rename(
          path.join(outputDir, "result.json.tmp"),
          path.join(outputDir, "result.json"),
        );
      }
      // Structured errors are successful protocol completion, not successful rendering.
      nw.App.quit();
    } catch {
      process.exit(1);
    }
  };
  const failure = (error) =>
    finish({
      ok: false,
      code: codes.has(error?.message) ? error.message : "renderer-failed",
    });
  process.on("uncaughtException", failure);
  process.on("unhandledRejection", failure);
  try {
    const args = nw.App.argv.filter((value) =>
      value.startsWith("--aurify-job="),
    );
    if (args.length !== 1) fail("invalid-request");
    const requestFile = await plainPath(
      args[0].slice("--aurify-job=".length),
      false,
    );
    if ((await fs.stat(requestFile)).size > MAX_JSON) fail("invalid-request");
    const request = JSON.parse(await fs.readFile(requestFile, "utf8"));
    if (
      request.schemaVersion !== 1 ||
      !["catalog", "render"].includes(request.mode) ||
      !clientValid(request.client)
    )
      fail("invalid-request");
    request.outputDir = await plainPath(request.outputDir, true);
    request.gameRoot = await plainPath(request.gameRoot, true);
    const runtime = path.dirname(process.execPath);
    const profile = await plainPath(nw.App.dataPath, true);
    for (const target of [request.outputDir, profile, requestFile]) {
      if (inside(runtime, target) || inside(request.gameRoot, target))
        fail("invalid-request");
    }
    if (
      !inside(path.dirname(requestFile), request.outputDir) ||
      inside(request.outputDir, profile) ||
      inside(profile, request.outputDir) ||
      inside(request.outputDir, requestFile) ||
      (await fs.readdir(request.outputDir)).length
    )
      fail("invalid-request");
    outputDir = request.outputDir;
    // Reconstruct identity from the admitted triple; extra request properties never become output metadata.
    request.client = {
      build: request.client.build,
      product: request.client.product,
      buildKey: request.client.buildKey,
    };
    if (
      request.mode === "render" &&
      (request.job?.schemaVersion !== 1 ||
        !/^[a-f0-9]{64}$/.test(request.job.inputHash) ||
        !sameClient(request.client, request.job.client))
    )
      fail("invalid-request");
    const data = await plainPath(path.join(request.gameRoot, "Data"), true);
    await plainPath(path.join(request.gameRoot, ".build.info"), false);
    await rejectAssetLinks(data);
    const exporter = await api.initialize(request);
    if (finishing) return;
    if (request.mode === "catalog") {
      const catalog = await exporter.catalog();
      if (Buffer.byteLength(JSON.stringify({ ok: true, catalog })) > MAX_JSON)
        fail("invalid-artifact");
      await finish({ ok: true, catalog });
      return;
    }
    const closure = path.join(outputDir, "closure");
    await fs.mkdir(closure);
    const objPath = path.join(closure, "character.obj");
    const exported = await exporter.render(objPath);
    if (finishing) return;
    const { convert } = await import(
      pathToFileURL(path.join(__dirname, "convert.mjs")).href
    );
    const model = await convert(closure, exported);
    if (
      !Buffer.isBuffer(model) ||
      model.length < 20 ||
      model.length > MAX_MODEL
    )
      fail("invalid-artifact");
    const hash = createHash("sha256").update(model).digest("hex");
    await fs.writeFile(path.join(outputDir, "model.glb"), model, {
      flag: "wx",
    });
    await finish({
      ok: true,
      inputHash: request.job.inputHash,
      client: request.client,
      modelFile: "model.glb",
      sha256: hash,
      bytes: model.length,
    });
  } catch (error) {
    await failure(error);
  }
}
module.exports = { run };
