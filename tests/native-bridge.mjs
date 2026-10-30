import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { execFile, execFileSync } from "node:child_process";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const executable = join(root, ".work", "native-tests", "NativeProbe.exe");
const runtimeKey = process.env.AURIFY_TEST_RUNTIME_KEY || randomUUID();
process.env.AURIFY_TEST_RUNTIME_KEY = runtimeKey;
const options = {
  cwd: root,
  encoding: "utf8",
  windowsHide: true,
  timeout: 60_000,
  maxBuffer: 32 * 1024 * 1024,
  env: { ...process.env, AURIFY_TEST_RUNTIME_KEY: runtimeKey },
};

export function nativeRaw(request) {
  return JSON.parse(
    execFileSync(executable, [], {
      ...options,
      input: JSON.stringify(request),
    }).replace(/^\uFEFF/, ""),
  );
}

export function native(request) {
  const response = nativeRaw(request);
  if (!response.ok) {
    const error = new Error(response.message);
    error.status = response.status;
    error.type = response.type;
    throw error;
  }
  return response.result;
}

export async function nativeAsync(request) {
  const child = execFile(executable, [], options);
  const done = new Promise((resolvePromise, rejectPromise) => {
    let stdout = "";
    let stderr = "";
    child.stdout.setEncoding("utf8").on("data", (chunk) => (stdout += chunk));
    child.stderr.setEncoding("utf8").on("data", (chunk) => (stderr += chunk));
    child.once("error", rejectPromise);
    child.once("close", (code) => {
      if (code !== 0)
        return rejectPromise(
          new Error(`Native probe exited ${code}: ${stderr}`),
        );
      try {
        const response = JSON.parse(stdout.replace(/^\uFEFF/, ""));
        if (!response.ok) {
          const error = new Error(response.message);
          error.status = response.status;
          error.type = response.type;
          rejectPromise(error);
        } else resolvePromise(response.result);
      } catch (error) {
        rejectPromise(error);
      }
    });
  });
  child.stdin.end(JSON.stringify(request));
  return done;
}

// The same fixture must produce the same public payload on the device and server.
// Returning native output makes all existing semantic/privacy assertions exercise C#.
export function compareNativeNormalization(kind, serverNormalize) {
  return (saved, source, options = {}) => {
    const request = {
      operation: "normalize",
      kind,
      saved,
      source,
      guildName: options.guildName || "AURA",
    };
    let expected;
    try {
      expected = serverNormalize(saved, source, options);
    } catch (error) {
      const actual = nativeRaw(request);
      assert.equal(
        actual.ok,
        false,
        `${kind} native normalization accepted rejected data`,
      );
      throw error;
    }
    const actual = native(request);
    assert.deepEqual(
      actual,
      expected,
      `${kind} device/server projection differs`,
    );
    return actual;
  };
}
