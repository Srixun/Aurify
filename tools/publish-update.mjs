import {
  createHash,
  createPrivateKey,
  createPublicKey,
  constants,
  sign,
  verify,
} from "node:crypto";
import { execFileSync } from "node:child_process";
import {
  copyFile,
  mkdir,
  open,
  readFile,
  rename,
  rm,
  stat,
  writeFile,
} from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const maximumManifest = 32768;
const maximumPayload = 64 * 1024 * 1024;
const maximumReleases = 32;
const maximumWindow = 31 * 86400000;
const fields = [
  "Aurify.exe",
  "addon/Aurify/Aurify.toc",
  "addon/Aurify/Aurify.lua",
];
const envelopePattern =
  /^\{"payload":"([A-Za-z0-9+/]+={0,2})","signature":"([A-Za-z0-9+/]+={0,2})"\}$/;
const versionPattern = /^2\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$/;
const hashPattern = /^[0-9a-f]{64}$/;
const decoder = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true });
const sha256 = (value) => createHash("sha256").update(value).digest("hex");

function exactKeys(value, keys) {
  return (
    value !== null &&
    typeof value === "object" &&
    !Array.isArray(value) &&
    JSON.stringify(Object.keys(value)) === JSON.stringify(keys)
  );
}

function publicKeyFromXml(publicXml) {
  const value = (name) => {
    const encoded = new RegExp(`<${name}>([A-Za-z0-9+/=]+)</${name}>`).exec(
      publicXml,
    )?.[1];
    if (!encoded)
      throw new Error("The embedded public trust anchor is invalid");
    return Buffer.from(encoded, "base64").toString("base64url");
  };
  const key = createPublicKey({
    key: { kty: "RSA", n: value("Modulus"), e: value("Exponent") },
    format: "jwk",
  });
  if (key.asymmetricKeyDetails?.modulusLength !== 3072)
    throw new Error("The embedded public trust anchor must be RSA3072");
  return key;
}

function signingKeys(key, publicXml) {
  const privateKey = createPrivateKey(key);
  const publicKey = createPublicKey(privateKey);
  const expected = publicKeyFromXml(publicXml);
  const actualJwk = publicKey.export({ format: "jwk" });
  const expectedJwk = expected.export({ format: "jwk" });
  if (
    privateKey.asymmetricKeyType !== "rsa" ||
    privateKey.asymmetricKeyDetails?.modulusLength !== 3072 ||
    actualJwk.n !== expectedJwk.n ||
    actualJwk.e !== expectedJwk.e
  )
    throw new Error(
      "The signing key must be RSA3072 and match the embedded trust anchor",
    );
  return { privateKey, publicKey };
}

function signedEnvelope(payload, privateKey, publicKey) {
  const bytes = Buffer.from(JSON.stringify(payload), "utf8");
  const signature = sign("sha256", bytes, {
    key: privateKey,
    padding: constants.RSA_PKCS1_PADDING,
  });
  if (
    signature.length !== 384 ||
    !verify(
      "sha256",
      bytes,
      { key: publicKey, padding: constants.RSA_PKCS1_PADDING },
      signature,
    )
  )
    throw new Error("Release signature verification failed");
  const envelope = JSON.stringify({
    payload: bytes.toString("base64"),
    signature: signature.toString("base64"),
  });
  if (envelope.length > maximumManifest)
    throw new Error("Signed update catalog is too large");
  return envelope;
}

function validateWindow(issuedAt, expiresAt, now, allowExpired) {
  if (
    !Number.isSafeInteger(issuedAt) ||
    issuedAt < 1 ||
    !Number.isSafeInteger(expiresAt) ||
    expiresAt <= issuedAt ||
    expiresAt - issuedAt > maximumWindow ||
    issuedAt > now + 300000 ||
    (!allowExpired && expiresAt <= now)
  )
    throw new Error("The update catalog validity window is outside policy");
}

function validateRelease(release, previousSequence, versions, identities) {
  if (
    !exactKeys(release, [
      "version",
      "sequence",
      "releaseId",
      "payloadPath",
      "payloadSha256",
      "payloadLength",
    ]) ||
    typeof release.version !== "string" ||
    !versionPattern.test(release.version) ||
    !Number.isSafeInteger(release.sequence) ||
    release.sequence < 1 ||
    release.sequence >= previousSequence ||
    typeof release.releaseId !== "string" ||
    !hashPattern.test(release.releaseId) ||
    release.payloadPath !==
      `/members/api/aurify/releases/${release.releaseId}` ||
    typeof release.payloadSha256 !== "string" ||
    !hashPattern.test(release.payloadSha256) ||
    !Number.isSafeInteger(release.payloadLength) ||
    release.payloadLength < 1 ||
    release.payloadLength > maximumPayload ||
    versions.has(release.version) ||
    identities.has(release.releaseId)
  )
    throw new Error("The update catalog contains an invalid release");
  versions.add(release.version);
  identities.add(release.releaseId);
}

function authenticatedPayload(envelope, publicXml) {
  if (typeof envelope !== "string" || envelope.length > maximumManifest)
    throw new Error("Update catalog is too large");
  const outer = envelopePattern.exec(envelope);
  if (!outer || outer[0] !== envelope)
    throw new Error("Update catalog envelope is not canonical");
  const bytes = Buffer.from(outer[1], "base64");
  const signature = Buffer.from(outer[2], "base64");
  if (
    bytes.toString("base64") !== outer[1] ||
    signature.length !== 384 ||
    signature.toString("base64") !== outer[2] ||
    !verify(
      "sha256",
      bytes,
      {
        key: publicKeyFromXml(publicXml),
        padding: constants.RSA_PKCS1_PADDING,
      },
      signature,
    )
  )
    throw new Error("Update catalog signature is invalid");
  return decoder.decode(bytes);
}

export function parseUpdateCatalog(
  envelope,
  publicXml,
  now = Date.now(),
  allowExpired = false,
) {
  const text = authenticatedPayload(envelope, publicXml);
  const payload = JSON.parse(text);
  if (
    JSON.stringify(payload) !== text ||
    !exactKeys(payload, [
      "schema",
      "product",
      "channel",
      "sequence",
      "issuedAt",
      "expiresAt",
      "releases",
    ]) ||
    payload.schema !== 2 ||
    payload.product !== "Aurify" ||
    payload.channel !== "stable" ||
    !Number.isSafeInteger(payload.sequence) ||
    payload.sequence < 1 ||
    !Array.isArray(payload.releases) ||
    payload.releases.length < 1 ||
    payload.releases.length > maximumReleases
  )
    throw new Error("Update catalog payload is not canonical");
  validateWindow(payload.issuedAt, payload.expiresAt, now, allowExpired);
  let previousSequence = Infinity;
  const versions = new Set();
  const identities = new Set();
  for (const release of payload.releases) {
    validateRelease(release, previousSequence, versions, identities);
    previousSequence = release.sequence;
  }
  if (payload.releases[0].sequence !== payload.sequence)
    throw new Error("Update catalog current sequence is inconsistent");
  return payload;
}

function parseLegacyHistory(envelope, publicXml, now) {
  const text = authenticatedPayload(envelope, publicXml);
  const value = JSON.parse(text);
  if (
    JSON.stringify(value) !== text ||
    !exactKeys(value, [
      "schema",
      "product",
      "channel",
      "sequence",
      "releaseId",
      "payloadPath",
      "payloadSha256",
      "payloadLength",
      "issuedAt",
      "expiresAt",
    ]) ||
    value.schema !== 1 ||
    value.product !== "Aurify" ||
    value.channel !== "stable" ||
    !Number.isSafeInteger(value.sequence) ||
    value.sequence < 1 ||
    typeof value.releaseId !== "string" ||
    !hashPattern.test(value.releaseId) ||
    value.payloadPath !==
      `/members/api/aurify/download?release=${value.releaseId}` ||
    typeof value.payloadSha256 !== "string" ||
    !hashPattern.test(value.payloadSha256) ||
    !Number.isSafeInteger(value.payloadLength) ||
    value.payloadLength < 1 ||
    value.payloadLength > maximumPayload
  )
    throw new Error("Previous legacy release is not canonical");
  validateWindow(value.issuedAt, value.expiresAt, now, true);
  return value;
}

function buildRelease(metadata, executable, sequence) {
  if (
    !exactKeys(metadata, [
      "schema",
      "product",
      "version",
      "releaseId",
      "payloadSha256",
      "payloadLength",
      "hashes",
      "toolchain",
    ]) ||
    metadata.schema !== 1 ||
    metadata.product !== "Aurify" ||
    typeof metadata.version !== "string" ||
    !versionPattern.test(metadata.version) ||
    !Number.isSafeInteger(metadata.payloadLength) ||
    metadata.payloadLength < 1 ||
    metadata.payloadLength > maximumPayload ||
    typeof metadata.payloadSha256 !== "string" ||
    !hashPattern.test(metadata.payloadSha256) ||
    !exactKeys(metadata.hashes, fields) ||
    fields.some((path) => !hashPattern.test(metadata.hashes[path] ?? "")) ||
    metadata.hashes["Aurify.exe"] !== metadata.payloadSha256 ||
    metadata.releaseId !==
      sha256(
        fields.map((path) => `${path}\n${metadata.hashes[path]}\n`).join(""),
      ) ||
    !Number.isSafeInteger(sequence) ||
    sequence < 1 ||
    executable.length < 1 ||
    executable.length > maximumPayload
  )
    throw new Error(
      "Unsigned build provenance is invalid or the signed executable is outside policy",
    );
  const payloadSha256 = sha256(executable);
  const hashes = { ...metadata.hashes, "Aurify.exe": payloadSha256 };
  const releaseId = sha256(
    fields.map((path) => `${path}\n${hashes[path]}\n`).join(""),
  );
  return {
    version: metadata.version,
    sequence,
    releaseId,
    payloadPath: `/members/api/aurify/releases/${releaseId}`,
    payloadSha256,
    payloadLength: executable.length,
  };
}

export function createUpdatePublication(
  metadata,
  executable,
  key,
  publicXml,
  sequence,
  previousCatalog = null,
  now = Date.now(),
  expiresAt = now + maximumWindow,
) {
  validateWindow(now, expiresAt, now, false);
  const { privateKey, publicKey } = signingKeys(key, publicXml);
  const release = buildRelease(metadata, executable, sequence);
  const previous = previousCatalog
    ? parseUpdateCatalog(previousCatalog, publicXml, now, true)
    : null;
  if (previous && sequence <= previous.sequence)
    throw new Error(
      "The release sequence must be newer than the previous catalog",
    );
  const releases = [release, ...(previous ? previous.releases : [])].slice(
    0,
    maximumReleases,
  );
  const versions = new Set();
  const identities = new Set();
  let previousSequence = Infinity;
  for (const item of releases) {
    validateRelease(item, previousSequence, versions, identities);
    previousSequence = item.sequence;
  }
  const catalog = {
    schema: 2,
    product: "Aurify",
    channel: "stable",
    sequence,
    issuedAt: now,
    expiresAt,
    releases,
  };
  const legacy = {
    schema: 1,
    product: "Aurify",
    channel: "stable",
    sequence,
    releaseId: release.releaseId,
    payloadPath: `/members/api/aurify/download?release=${release.releaseId}`,
    payloadSha256: release.payloadSha256,
    payloadLength: release.payloadLength,
    issuedAt: now,
    expiresAt,
  };
  return {
    release,
    catalog,
    catalogEnvelope: signedEnvelope(catalog, privateKey, publicKey),
    legacyEnvelope: signedEnvelope(legacy, privateKey, publicKey),
  };
}

export function createUpdateEnvelope(
  metadata,
  executable,
  key,
  publicXml,
  sequence,
  now = Date.now(),
  expiresAt = now + maximumWindow,
) {
  return createUpdatePublication(
    metadata,
    executable,
    key,
    publicXml,
    sequence,
    null,
    now,
    expiresAt,
  ).legacyEnvelope;
}

async function readBounded(path, limit = maximumManifest) {
  const file = await open(path, "r");
  try {
    const buffer = Buffer.alloc(limit + 1);
    let length = 0;
    while (length < buffer.length) {
      const { bytesRead } = await file.read(
        buffer,
        length,
        buffer.length - length,
        null,
      );
      if (bytesRead === 0) break;
      length += bytesRead;
    }
    if (length > limit) throw new Error("Update catalog is too large");
    return decoder.decode(buffer.subarray(0, length));
  } finally {
    await file.close();
  }
}

async function verifyReleaseFile(path, release) {
  const info = await stat(path);
  if (!info.isFile() || info.size !== release.payloadLength)
    throw new Error(`Retained release length mismatch: ${release.releaseId}`);
  const file = await open(path, "r");
  const hash = createHash("sha256");
  try {
    const buffer = Buffer.alloc(65536);
    for (;;) {
      const { bytesRead } = await file.read(buffer, 0, buffer.length, null);
      if (bytesRead === 0) break;
      hash.update(buffer.subarray(0, bytesRead));
    }
  } finally {
    await file.close();
  }
  if (hash.digest("hex") !== release.payloadSha256)
    throw new Error(`Retained release hash mismatch: ${release.releaseId}`);
}

function verifyExecutableVersion(path, expected) {
  const script =
    "$ErrorActionPreference='Stop'; $v=(Get-Item -LiteralPath $env:AURIFY_PUBLISH_EXE).VersionInfo; [pscustomobject]@{ProductVersion=$v.ProductVersion;FileVersion=$v.FileVersion}|ConvertTo-Json -Compress";
  const result = JSON.parse(
    execFileSync(
      "powershell.exe",
      ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
      {
        encoding: "utf8",
        windowsHide: true,
        env: { ...process.env, AURIFY_PUBLISH_EXE: path },
      },
    ),
  );
  const version = String(result.ProductVersion).replace(/\.0$/, "");
  if (
    !versionPattern.test(version) ||
    result.FileVersion !== `${version}.0` ||
    (expected !== undefined && version !== expected)
  )
    throw new Error("Executable version does not match release provenance");
  return version;
}

async function atomicWrite(path, content) {
  const temporary = `${path}.${process.pid}.${Date.now()}.tmp`;
  try {
    await writeFile(temporary, content, { flag: "wx", mode: 0o600 });
    await rename(temporary, path);
  } finally {
    await rm(temporary, { force: true });
  }
}

async function main() {
  const args = process.argv.slice(2);
  const options = {};
  const usage =
    "Usage: node tools/publish-update.mjs --key <private.pem> --sequence <integer> (--previous <Aurify-updates.json> | --first-release) [--expires-at <epoch-ms>]";
  for (let index = 0; index < args.length; index++) {
    const option = args[index];
    if (Object.hasOwn(options, option)) throw new Error(usage);
    if (option === "--first-release") options[option] = true;
    else {
      if (
        !["--key", "--sequence", "--expires-at", "--previous"].includes(
          option,
        ) ||
        !args[index + 1] ||
        args[index + 1].startsWith("--")
      )
        throw new Error(usage);
      options[option] = args[++index];
    }
  }
  if (Boolean(options["--previous"]) === Boolean(options["--first-release"]))
    throw new Error(
      `Choose exactly one publication mode: --previous or --first-release. ${usage}`,
    );
  const keyPath = options["--key"] || process.env.AURIFY_UPDATE_PRIVATE_KEY;
  if (!keyPath)
    throw new Error(
      "Supply the private key file explicitly using --key or AURIFY_UPDATE_PRIVATE_KEY",
    );
  const sequence = Number(
    options["--sequence"] || process.env.AURIFY_UPDATE_SEQUENCE,
  );
  if (!Number.isSafeInteger(sequence) || sequence < 1)
    throw new Error("A positive monotonic sequence is required");
  const lockPath = join(root, "dist", ".Aurify-update.lock");
  const lock = await open(lockPath, "wx", 0o600);
  try {
    await publish(options, keyPath, sequence);
  } finally {
    await lock.close();
    await rm(lockPath);
  }
}

async function publish(options, keyPath, sequence) {
  const dist = join(root, "dist");
  const legacyPath = join(dist, "Aurify-update.json");
  const catalogPath = join(dist, "Aurify-updates.json");
  const releaseDirectory = join(dist, "Aurify-releases");
  const publicXml = await readFile(
    join(root, "companion", "update-public-key.xml"),
    "utf8",
  );
  const key = await readFile(resolve(keyPath));
  let previousEnvelope = null;
  let previousCatalog = null;
  let previousDirectory = null;
  let migratedRelease = null;
  let migratedSource = null;
  if (options["--previous"]) {
    const previousPath = resolve(options["--previous"]);
    previousEnvelope = await readBounded(previousPath);
    const previousBody = authenticatedPayload(previousEnvelope, publicXml);
    previousDirectory = join(dirname(previousPath), "Aurify-releases");
    if (previousBody.startsWith('{"schema":1,')) {
      const legacy = parseLegacyHistory(
        previousEnvelope,
        publicXml,
        Date.now(),
      );
      migratedSource = join(dirname(previousPath), "Aurify.exe");
      await verifyReleaseFile(migratedSource, legacy);
      migratedRelease = {
        version: verifyExecutableVersion(migratedSource),
        sequence: legacy.sequence,
        releaseId: legacy.releaseId,
        payloadPath: `/members/api/aurify/releases/${legacy.releaseId}`,
        payloadSha256: legacy.payloadSha256,
        payloadLength: legacy.payloadLength,
      };
      const { privateKey, publicKey } = signingKeys(key, publicXml);
      previousEnvelope = signedEnvelope(
        {
          schema: 2,
          product: "Aurify",
          channel: "stable",
          sequence: legacy.sequence,
          issuedAt: legacy.issuedAt,
          expiresAt: legacy.expiresAt,
          releases: [migratedRelease],
        },
        privateKey,
        publicKey,
      );
    }
    previousCatalog = parseUpdateCatalog(
      previousEnvelope,
      publicXml,
      Date.now(),
      true,
    );
  }
  const existing = await readBounded(catalogPath).catch((error) => {
    if (error.code === "ENOENT") return null;
    throw error;
  });
  const existingLegacy = await readBounded(legacyPath).catch((error) => {
    if (error.code === "ENOENT") return null;
    throw error;
  });
  if (existingLegacy !== null) {
    const legacy = parseLegacyHistory(existingLegacy, publicXml, Date.now());
    if (!previousCatalog || legacy.sequence > previousCatalog.sequence)
      throw new Error(
        "The explicit previous release cannot discard the legacy publication watermark",
      );
  }
  if (options["--first-release"] && existing !== null)
    throw new Error("--first-release cannot overwrite an existing catalog");
  if (existing !== null) {
    const current = parseUpdateCatalog(existing, publicXml, Date.now(), true);
    if (!previousCatalog || current.sequence > previousCatalog.sequence)
      throw new Error(
        "The explicit previous catalog cannot discard newer published history",
      );
    if (
      current.sequence === previousCatalog.sequence &&
      JSON.stringify(current.releases) !==
        JSON.stringify(previousCatalog.releases)
    )
      throw new Error(
        "The previous catalog conflicts with published release history",
      );
  }
  if (previousCatalog && sequence <= previousCatalog.sequence)
    throw new Error("The sequence must be newer than the previous catalog");
  const metadata = JSON.parse(
    await readFile(join(dist, "Aurify-release.json"), "utf8"),
  );
  const executablePath = join(dist, "Aurify.exe");
  const executable = await readFile(executablePath);
  verifyExecutableVersion(executablePath, metadata.version);
  const publication = createUpdatePublication(
    metadata,
    executable,
    key,
    publicXml,
    sequence,
    previousEnvelope,
    Date.now(),
    options["--expires-at"] ? Number(options["--expires-at"]) : undefined,
  );
  await mkdir(releaseDirectory, { recursive: true });
  const created = [];
  let promotionStarted = false;
  try {
    for (const release of publication.catalog.releases.slice(1)) {
      const destination = join(releaseDirectory, `${release.releaseId}.exe`);
      const present = await stat(destination).catch((error) => {
        if (error.code === "ENOENT") return null;
        throw error;
      });
      if (present === null) {
        if (!previousDirectory)
          throw new Error("A previous release store is required");
        const source =
          migratedRelease && release.releaseId === migratedRelease.releaseId
            ? migratedSource
            : join(previousDirectory, `${release.releaseId}.exe`);
        await verifyReleaseFile(source, release);
        await copyFile(source, destination, 0x1);
        created.push(destination);
      }
      await verifyReleaseFile(destination, release);
    }
    const currentDestination = join(
      releaseDirectory,
      `${publication.release.releaseId}.exe`,
    );
    const currentPresent = await stat(currentDestination).catch((error) => {
      if (error.code === "ENOENT") return null;
      throw error;
    });
    if (currentPresent === null) {
      await copyFile(executablePath, currentDestination, 0x1);
      created.push(currentDestination);
    }
    await verifyReleaseFile(currentDestination, publication.release);
    promotionStarted = true;
    try {
      await atomicWrite(legacyPath, publication.legacyEnvelope);
      await atomicWrite(catalogPath, publication.catalogEnvelope);
    } catch (error) {
      // Keep immutable payloads even if restoring a manifest fails. A published
      // envelope must never point at a file deleted by failure cleanup.
      for (const [path, previous] of [
        [legacyPath, existingLegacy],
        [catalogPath, existing],
      ]) {
        if (previous === null) await rm(path, { force: true });
        else await atomicWrite(path, previous);
      }
      throw error;
    }
  } catch (error) {
    if (!promotionStarted)
      await Promise.all(created.map((path) => rm(path, { force: true })));
    throw error;
  }
  console.log(
    `Signed stable catalogs for Aurify ${publication.release.version} (sequence ${sequence}); retained ${publication.catalog.releases.length} release(s); private key was not copied`,
  );
}

if (
  process.argv[1] &&
  resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
