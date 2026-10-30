import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { createServer, request as httpRequest } from "node:http";
import {
  appendFile,
  mkdir,
  mkdtemp,
  readFile,
  readdir,
  rename,
  rm,
  writeFile,
} from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { gunzipSync, gzipSync } from "node:zlib";
import test from "node:test";
import { nativeAsync } from "./native-bridge.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const work = join(root, ".work", "native-tests");
const credentialsName = "config.forever.credentials";
const initialTime = "2026-10-18T12:00:00Z";

async function temporary(t) {
  await mkdir(work, { recursive: true });
  const path = await mkdtemp(join(work, "forever-"));
  t.after(() => rm(path, { recursive: true, force: true, maxRetries: 5 }));
  return path;
}

async function logFixture(t, bytes) {
  const path = await temporary(t);
  const directory = join(path, "game", "Logs");
  await mkdir(directory, { recursive: true });
  const logPath = join(directory, "WoWCombatLog.txt");
  await writeFile(logPath, bytes);
  return { path, logPath };
}

async function listen(t, server) {
  await new Promise((resolvePromise, rejectPromise) => {
    server.once("error", rejectPromise);
    server.listen(0, "127.0.0.1", resolvePromise);
  });
  t.after(async () => {
    server.closeAllConnections();
    await new Promise((resolvePromise) => server.close(resolvePromise));
  });
  return `http://127.0.0.1:${server.address().port}`;
}

function callback(url, headers = {}) {
  return new Promise((resolvePromise) => {
    const request = httpRequest(url, { headers, agent: false }, (response) => {
      response.resume();
      response.once("end", () =>
        resolvePromise({ status: response.statusCode, error: null }),
      );
    });
    request.setTimeout(3000, () =>
      request.destroy(new Error("callback timeout")),
    );
    request.once("error", (error) =>
      resolvePromise({ status: null, error: error.code || error.message }),
    );
    request.end();
  });
}

async function providerFixture(t) {
  const fixture = {
    requests: [],
    reports: new Map(),
    codes: new Map(),
    issued: [],
    refreshInputs: [],
    callbackResults: [],
    userId: 701,
    username: "SyntheticSameName",
    unauthorizedNext: 0,
    alwaysUnauthorized: false,
    rejectSegments: false,
    loseAck: false,
    onAuthorize: null,
    onRefresh: null,
    intercept: null,
  };
  const issueTokens = () => {
    const number = fixture.issued.length + 1;
    const tokens = {
      accessToken: `synthetic-access-${number}-${"A".repeat(48)}`,
      refreshToken: `synthetic-refresh-${number}-${"R".repeat(48)}`,
      expiresIn: 3600,
      user: { id: fixture.userId, username: fixture.username },
    };
    fixture.issued.push(tokens);
    fixture.accessToken = tokens.accessToken;
    fixture.refreshToken = tokens.refreshToken;
    return tokens;
  };
  const server = createServer(async (request, response) => {
    try {
      const chunks = [];
      for await (const chunk of request) chunks.push(chunk);
      const bytes = Buffer.concat(chunks);
      const url = new URL(request.url, fixture.serverUrl);
      const body =
        bytes.length > 0 &&
        request.headers["content-type"]?.startsWith("application/json")
          ? JSON.parse(bytes.toString("utf8"))
          : null;
      const record = {
        path: url.pathname,
        url,
        method: request.method,
        headers: request.headers,
        body,
        bytes,
      };
      fixture.requests.push(record);
      const reply = (status, payload) => {
        response.writeHead(status, { "Content-Type": "application/json" });
        response.end(JSON.stringify(payload));
      };
      if (
        fixture.intercept &&
        (await fixture.intercept(record, response, reply))
      )
        return;
      if (url.pathname === "/api/auth/desktop/authorize") {
        assert.equal(request.method, "GET");
        assert.equal(url.searchParams.get("response_type"), "code");
        assert.equal(url.searchParams.get("code_challenge_method"), "S256");
        assert.equal(url.searchParams.get("scope"), "upload");
        const redirect = new URL(url.searchParams.get("redirect_uri"));
        assert.equal(redirect.protocol, "http:");
        assert.equal(redirect.hostname, "127.0.0.1");
        assert.equal(redirect.pathname, "/callback");
        assert.ok(redirect.port);
        const state = url.searchParams.get("state");
        assert.ok(state && state.length >= 32);
        const code = `synthetic-code-${fixture.codes.size + 1}`;
        fixture.codes.set(code, {
          challenge: url.searchParams.get("code_challenge"),
          redirect: redirect.href,
        });
        redirect.searchParams.set("code", code);
        redirect.searchParams.set("state", state);
        if (fixture.onAuthorize) await fixture.onAuthorize(redirect, url);
        else fixture.callbackResults.push(await callback(redirect));
        return reply(200, { synthetic: true });
      }
      if (url.pathname.startsWith("/api/auth/desktop/")) {
        assert.equal(request.method, "POST");
        assert.equal(request.headers.authorization, undefined);
        assert.equal(request.headers["x-client"], "aurify");
        if (url.pathname.endsWith("/token")) {
          const grant = fixture.codes.get(body.code);
          assert.ok(grant, "a callback code can be exchanged only once");
          assert.match(body.code_verifier, /^[A-Za-z0-9._~-]{43,128}$/);
          assert.equal(
            createHash("sha256").update(body.code_verifier).digest("base64url"),
            grant.challenge,
          );
          assert.equal(body.redirect_uri, grant.redirect);
          assert.equal(body.device_label, "Aurify");
          fixture.codes.delete(body.code);
          return reply(200, issueTokens());
        }
        if (url.pathname.endsWith("/refresh")) {
          fixture.refreshInputs.push(body.refreshToken);
          if (body.refreshToken !== fixture.refreshToken)
            return reply(401, { error: "invalid_refresh" });
          const tokens = issueTokens();
          if (fixture.onRefresh) await fixture.onRefresh();
          return reply(200, tokens);
        }
        if (url.pathname.endsWith("/revoke"))
          return reply(200, { revoked: true });
      }
      assert.equal(request.headers["x-client"], "aurify");
      assert.equal(request.headers["x-client-platform"], "win32");
      if (
        fixture.alwaysUnauthorized ||
        fixture.unauthorizedNext > 0 ||
        request.headers.authorization !== `Bearer ${fixture.accessToken}`
      ) {
        if (fixture.unauthorizedNext > 0) fixture.unauthorizedNext--;
        return reply(401, { error: "expired" });
      }
      if (url.pathname === "/api/live/start") {
        assert.equal(request.method, "POST");
        assert.ok(["private", "public"].includes(body.visibility));
        assert.equal(typeof body.timezone, "string");
        const id = String(1001 + fixture.reports.size);
        fixture.reports.set(id, {
          id,
          nextSeq: 1,
          bytes: Buffer.alloc(0),
          segments: [],
          finalized: false,
        });
        return reply(200, { reportId: Number(id) });
      }
      if (url.pathname === "/api/live/segment") {
        assert.equal(request.method, "POST");
        assert.equal(request.headers["content-type"], "application/gzip");
        const id = url.searchParams.get("reportId");
        const seq = Number(url.searchParams.get("seq"));
        assert.equal(request.headers["x-live-report-id"], id);
        assert.equal(request.headers["x-live-seq"], String(seq));
        const report = fixture.reports.get(id);
        if (!report) return reply(404, { error: "unknown_session" });
        if (fixture.rejectSegments) return reply(503, { error: "unavailable" });
        if (seq !== report.nextSeq)
          return reply(409, {
            error: "out_of_order",
            expectedSeq: report.nextSeq,
          });
        assert.equal(report.finalized, false);
        const raw = gunzipSync(bytes);
        report.bytes = Buffer.concat([report.bytes, raw]);
        report.segments.push({ seq, gzip: bytes, raw });
        report.nextSeq++;
        if (fixture.loseAck) {
          fixture.loseAck = false;
          response.destroy();
          return;
        }
        return reply(200, { accepted: true });
      }
      const status = /^\/api\/live\/(\d+)\/status$/.exec(url.pathname);
      if (status) {
        assert.equal(request.method, "GET");
        const report = fixture.reports.get(status[1]);
        return report
          ? reply(200, {
              session: { status: report.finalized ? "finalized" : "live" },
            })
          : reply(404, { error: "unknown_session" });
      }
      if (
        ["/api/live/heartbeat", "/api/live/finalize"].includes(url.pathname)
      ) {
        assert.equal(request.method, "POST");
        const report = fixture.reports.get(String(body.reportId));
        if (!report) return reply(404, { error: "unknown_session" });
        if (url.pathname.endsWith("/finalize")) report.finalized = true;
        return reply(200, { accepted: true });
      }
      throw new Error(
        `Unexpected synthetic provider endpoint: ${url.pathname}`,
      );
    } catch (error) {
      fixture.failure ||= error;
      if (!response.headersSent) response.writeHead(500);
      response.end();
    }
  });
  fixture.serverUrl = await listen(t, server);
  t.after(() => {
    if (fixture.failure) throw fixture.failure;
  });
  fixture.run = (path, actions, extra = {}) =>
    nativeAsync({
      operation: "forever",
      root: path,
      serverUrl: fixture.serverUrl,
      now: initialTime,
      actions,
      ...extra,
    });
  fixture.calls = (path) =>
    fixture.requests.filter((item) => item.path === path);
  fixture.scan = (game, extra = {}) =>
    fixture.run(game.path, [{ action: "scan", ...extra }], {
      logPath: game.logPath,
    });
  return fixture;
}

function succeeded(result) {
  for (const action of result.results)
    assert.equal(action.error, null, JSON.stringify(action));
  return result;
}

async function login(fixture, path) {
  const result = succeeded(await fixture.run(path, [{ action: "login" }]));
  assert.equal(result.results[0].connected, true);
  assert.equal(result.results[0].pending, false);
  return result;
}

async function outbox(path) {
  const directory = join(path, "forever-outbox");
  const files = (await readdir(directory)).sort();
  return Promise.all(
    files.map(async (name) => [
      name,
      (await readFile(join(directory, name))).toString("base64"),
    ]),
  );
}

function rejectsCallback(result) {
  assert.ok(result.error || result.status >= 400, JSON.stringify(result));
}

test("native Forever PKCE survives invalid callbacks and exchanges the valid code only once", async (t) => {
  const path = await temporary(t);
  const fixture = await providerFixture(t);
  fixture.onAuthorize = async (valid, authorize) => {
    assert.equal(authorize.searchParams.has("code_verifier"), false);
    const wrongState = new URL(valid);
    wrongState.searchParams.set("state", "synthetic-wrong-state");
    rejectsCallback(await callback(wrongState));
    const wrongPath = new URL(valid);
    wrongPath.pathname = "/not-callback";
    rejectsCallback(await callback(wrongPath));
    rejectsCallback(await callback(valid, { Host: "untrusted.invalid" }));
    assert.equal(fixture.calls("/api/auth/desktop/token").length, 0);
    assert.equal((await callback(valid)).status, 200);
    rejectsCallback(await callback(valid));
  };
  await login(fixture, path);
  assert.equal(fixture.calls("/api/auth/desktop/token").length, 1);
});

test("native Forever cancellation and disposal close the pending loopback callback", async (t) => {
  const path = await temporary(t);
  const fixture = await providerFixture(t);
  fixture.onAuthorize = async (valid) => rejectsCallback(await callback(valid));
  const result = succeeded(
    await fixture.run(path, [
      { action: "beginLogin" },
      { action: "cancelLogin" },
      { action: "authorize" },
      { action: "beginLogin" },
      { action: "cancelToken" },
      { action: "authorize" },
      { action: "beginLogin" },
      { action: "restart" },
      { action: "authorize" },
    ]),
  );
  assert.equal(result.results.at(-1).connected, false);
  assert.equal(result.results.at(-1).pending, false);
  assert.equal(fixture.calls("/api/auth/desktop/token").length, 0);
  const states = fixture
    .calls("/api/auth/desktop/authorize")
    .map((call) => call.url.searchParams.get("state"));
  assert.equal(new Set(states).size, states.length);
});

test("native Forever denied consent consumes that login without exchanging a later replay", async (t) => {
  const path = await temporary(t);
  const fixture = await providerFixture(t);
  fixture.onAuthorize = async (valid) => {
    const denial = new URL(valid);
    denial.searchParams.delete("code");
    denial.searchParams.set("error", "access_denied");
    await callback(denial);
    rejectsCallback(await callback(valid));
  };
  const result = succeeded(await fixture.run(path, [{ action: "login" }]));
  assert.equal(result.results[0].connected, false);
  assert.equal(result.results[0].pending, false);
  assert.equal(fixture.calls("/api/auth/desktop/token").length, 0);
});

test("native Forever persists rotating refresh tokens across processes and recovers one 401", async (t) => {
  const path = await temporary(t);
  const fixture = await providerFixture(t);
  await login(fixture, path);
  fixture.unauthorizedNext = 1;
  const first = succeeded(await fixture.run(path, [{ action: "start" }]));
  const reportId = first.results[0].value;
  succeeded(await fixture.run(path, [{ action: "status", reportId }]));
  assert.deepEqual(
    fixture.refreshInputs,
    fixture.issued.slice(0, 3).map((tokens) => tokens.refreshToken),
  );
  assert.equal(fixture.calls("/api/live/start").length, 2);
  assert.equal(fixture.reports.size, 1);
  const vault = await readFile(join(path, credentialsName), "utf8");
  for (const tokens of fixture.issued) {
    assert.equal(vault.includes(tokens.refreshToken), false);
    assert.equal(vault.includes(tokens.accessToken), false);
  }
});

test("native Forever persists a consumed refresh rotation before honoring caller cancellation", async (t) => {
  const path = await temporary(t);
  const fixture = await providerFixture(t);
  await login(fixture, path);
  let markConsumed;
  let releaseResponse;
  const consumed = new Promise((resolvePromise) => {
    markConsumed = resolvePromise;
  });
  const released = new Promise((resolvePromise) => {
    releaseResponse = resolvePromise;
  });
  t.after(() => releaseResponse());
  fixture.onRefresh = async () => {
    fixture.onRefresh = null;
    markConsumed();
    await released;
  };
  fixture.intercept = async (record, response, reply) => {
    if (record.path === "/__probe/refresh-ready") {
      await consumed;
      reply(200, { ready: true });
      return true;
    }
    if (record.path === "/__probe/release-refresh") {
      releaseResponse();
      reply(200, { released: true });
      return true;
    }
    return false;
  };
  const cancelled = await fixture.run(path, [
    { action: "startCancelOnRefresh" },
  ]);
  assert.match(
    cancelled.results[0].error?.type || "",
    /^(OperationCanceledException|TaskCanceledException)$/,
  );
  assert.equal(fixture.calls("/api/live/start").length, 0);
  assert.deepEqual(fixture.refreshInputs, [fixture.issued[0].refreshToken]);
  succeeded(await fixture.run(path, [{ action: "start" }]));
  assert.deepEqual(fixture.refreshInputs, [
    fixture.issued[0].refreshToken,
    fixture.issued[1].refreshToken,
  ]);
  assert.equal(fixture.reports.size, 1);
});

test("native Forever stops after one authentication retry instead of looping", async (t) => {
  const path = await temporary(t);
  const fixture = await providerFixture(t);
  await login(fixture, path);
  fixture.alwaysUnauthorized = true;
  const result = await fixture.run(path, [{ action: "start" }]);
  assert.equal(result.results[0].error?.status, 401);
  assert.equal(fixture.calls("/api/live/start").length, 2);
  assert.equal(fixture.refreshInputs.length, 2);
});

test("native Forever preserves the vault after temporary refresh failure but clears a rejected refresh", async (t) => {
  const path = await temporary(t);
  const fixture = await providerFixture(t);
  await login(fixture, path);
  const before = await readFile(join(path, credentialsName));
  let refreshStatus = 503;
  fixture.intercept = (record, response, reply) => {
    if (record.path !== "/api/auth/desktop/refresh") return false;
    reply(refreshStatus, { error: "synthetic-refresh-failure" });
    return true;
  };
  const temporaryFailure = await fixture.run(path, [{ action: "start" }]);
  assert.ok(temporaryFailure.results[0].error);
  assert.deepEqual(await readFile(join(path, credentialsName)), before);
  refreshStatus = 401;
  const rejected = await fixture.run(path, [{ action: "start" }]);
  assert.ok(rejected.results[0].error);
  assert.equal(rejected.results[0].connected, false);
  await assert.rejects(readFile(join(path, credentialsName)), {
    code: "ENOENT",
  });
});

test("native Forever leaves an unreadable credential vault intact", async (t) => {
  const path = await temporary(t);
  const fixture = await providerFixture(t);
  const corrupt = Buffer.from('{"version":1,"protected":"not-valid-dpapi"}');
  await writeFile(join(path, credentialsName), corrupt);
  await assert.rejects(fixture.run(path, [{ action: "start" }]));
  assert.deepEqual(await readFile(join(path, credentialsName)), corrupt);
  assert.equal(fixture.requests.length, 0);
});

test("native Forever never follows credential-bearing token or authenticated API redirects", async (t) => {
  const path = await temporary(t);
  const fixture = await providerFixture(t);
  const leaks = [];
  const sinkUrl = await listen(
    t,
    createServer((request, response) => {
      leaks.push({ url: request.url, headers: request.headers });
      request.resume();
      response.writeHead(200, { "Content-Type": "application/json" }).end("{}");
    }),
  );
  let redirectPath = "/api/auth/desktop/token";
  fixture.intercept = (record, response) => {
    if (record.path !== redirectPath) return false;
    response.writeHead(307, { Location: `${sinkUrl}/capture` }).end();
    return true;
  };
  const denied = await fixture.run(path, [{ action: "login" }]);
  assert.equal(denied.results[0].connected, false);
  assert.deepEqual(leaks, []);
  redirectPath = null;
  await login(fixture, path);
  redirectPath = "/api/live/start";
  const rejected = await fixture.run(path, [{ action: "start" }]);
  assert.ok(rejected.results[0].error);
  assert.deepEqual(leaks, []);
  const visible = JSON.stringify(rejected);
  for (const tokens of fixture.issued) {
    assert.equal(visible.includes(tokens.accessToken), false);
    assert.equal(visible.includes(tokens.refreshToken), false);
  }
});

test("native Forever bounds JSON bytes with both declared-length and chunked responses", async (t) => {
  const path = await temporary(t);
  const fixture = await providerFixture(t);
  await login(fixture, path);
  const started = succeeded(await fixture.run(path, [{ action: "start" }]));
  const reportId = started.results[0].value;
  // Below the JSON parser's character limit, above the transport's 2 MiB byte limit.
  const oversized = Buffer.from(
    JSON.stringify({ padding: "é".repeat(1_100_000) }),
  );
  let chunked = false;
  fixture.intercept = (record, response) => {
    if (record.path !== `/api/live/${reportId}/status`) return false;
    response.writeHead(200, {
      "Content-Type": "application/json",
      ...(chunked ? {} : { "Content-Length": oversized.length }),
    });
    response.write(oversized.subarray(0, 1024));
    response.end(oversized.subarray(1024));
    return true;
  };
  const declared = await fixture.run(path, [{ action: "status", reportId }]);
  assert.ok(
    declared.results[0].error,
    "oversized Content-Length must be rejected",
  );
  chunked = true;
  const streamed = await fixture.run(path, [{ action: "status", reportId }]);
  assert.ok(
    streamed.results[0].error,
    "chunked bytes must obey the same bound",
  );
});

test("native Forever acknowledges only already-accepted duplicate segment sequences", async (t) => {
  const path = await temporary(t);
  const fixture = await providerFixture(t);
  await login(fixture, path);
  const raw = Buffer.from("10/18 12:00:00.000  SYNTHETIC EVENT\r\n");
  const gzip = gzipSync(raw).toString("base64");
  const result = await fixture.run(path, [
    { action: "start" },
    { action: "segment", seq: 1, gzip },
    { action: "segment", seq: 1, gzip },
    { action: "segment", seq: 3, gzip },
  ]);
  assert.equal(result.results[1].error, null);
  assert.equal(result.results[2].error, null);
  assert.equal(result.results[3].error?.status, 409);
  assert.deepEqual([...fixture.reports.values()][0].bytes, raw);
});

test("native Forever resumes raw UTF8 bytes without sending an incomplete trailing line", async (t) => {
  const complete = Buffer.from("10/18 12:00:00.000  SYNTHETIC ÉVENT\r\n");
  const next = Buffer.from("10/18 12:00:01.000  SYNTHETIC ÉCHO\n");
  const cut = next.indexOf(Buffer.from("É")) + 1;
  const game = await logFixture(
    t,
    Buffer.concat([complete, next.subarray(0, cut)]),
  );
  const fixture = await providerFixture(t);
  await login(fixture, game.path);
  succeeded(await fixture.scan(game));
  assert.equal(fixture.reports.size, 1);
  const report = [...fixture.reports.values()][0];
  assert.deepEqual(report.bytes, complete);
  await appendFile(game.logPath, next.subarray(cut));
  succeeded(await fixture.scan(game));
  assert.equal(fixture.reports.size, 1);
  assert.deepEqual(report.bytes, Buffer.concat([complete, next]));
  assert.deepEqual(
    report.segments.map((segment) => segment.seq),
    [1, 2],
  );
  succeeded(await fixture.scan(game, { gameRunning: false }));
  assert.equal(report.finalized, true);
});

test("native Forever replays exact durable gzip after a lost ACK before rotating a replaced source", async (t) => {
  const original = Buffer.from(
    "10/18 12:00:00.000  ORIGINAL SYNTHETIC ÉVENT\r\n",
  );
  const replacement = Buffer.from(
    "10/18 12:01:00.000  REPLACEMENT SYNTHETIC EVENT\n",
  );
  const game = await logFixture(t, original);
  const fixture = await providerFixture(t);
  await login(fixture, game.path);
  fixture.loseAck = true;
  await fixture.scan(game);
  const firstAttempt = fixture.calls("/api/live/segment");
  assert.equal(firstAttempt.length, 1);
  assert.deepEqual([...fixture.reports.values()][0].bytes, original);
  await rename(game.logPath, `${game.logPath}.old`);
  await writeFile(game.logPath, replacement);
  succeeded(await fixture.scan(game));
  // A second scan may be needed to read the new file after closing the old session.
  succeeded(await fixture.scan(game));
  const attempts = fixture.calls("/api/live/segment");
  assert.deepEqual(attempts[1].bytes, attempts[0].bytes);
  assert.equal(attempts[1].url.search, attempts[0].url.search);
  const reports = [...fixture.reports.values()];
  assert.equal(reports.length, 2);
  assert.equal(reports[0].finalized, true);
  assert.deepEqual(reports[0].bytes, original);
  assert.deepEqual(reports[1].bytes, replacement);
});

test("native Forever detects a rewritten log prefix rather than skipping the replacement", async (t) => {
  const original = Buffer.from("10/18 12:00:00.000  FIRST\n");
  const replacement = Buffer.from(
    "10/18 12:01:00.000  LONGER REPLACEMENT SYNTHETIC EVENT\n",
  );
  const game = await logFixture(t, original);
  const fixture = await providerFixture(t);
  await login(fixture, game.path);
  succeeded(await fixture.scan(game));
  await writeFile(game.logPath, replacement);
  succeeded(await fixture.scan(game));
  succeeded(await fixture.scan(game));
  const reports = [...fixture.reports.values()];
  assert.equal(reports.length, 2);
  assert.equal(reports[0].finalized, true);
  assert.deepEqual(reports[0].bytes, original);
  assert.deepEqual(reports[1].bytes, replacement);
});

test("native Forever idle finalization preserves the cursor when later bytes reopen a report", async (t) => {
  const original = Buffer.from("10/18 12:00:00.000  BEFORE IDLE\n");
  const later = Buffer.from("10/18 12:16:00.000  AFTER IDLE\n");
  const game = await logFixture(t, original);
  const fixture = await providerFixture(t);
  await login(fixture, game.path);
  succeeded(await fixture.scan(game));
  succeeded(
    await fixture.run(game.path, [{ action: "scan" }], {
      logPath: game.logPath,
      now: "2026-10-18T12:16:00Z",
    }),
  );
  const first = [...fixture.reports.values()][0];
  assert.equal(first.finalized, true);
  await appendFile(game.logPath, later);
  succeeded(
    await fixture.run(game.path, [{ action: "scan" }], {
      logPath: game.logPath,
      now: "2026-10-18T12:17:00Z",
    }),
  );
  const reports = [...fixture.reports.values()];
  assert.equal(reports.length, 2);
  assert.deepEqual(reports[0].bytes, original);
  assert.deepEqual(reports[1].bytes, later);
});

test("native Forever preserves an unavailable session's pending data instead of silently restarting it", async (t) => {
  const raw = Buffer.from("10/18 12:00:00.000  PENDING SYNTHETIC EVENT\n");
  const game = await logFixture(t, raw);
  const fixture = await providerFixture(t);
  await login(fixture, game.path);
  fixture.rejectSegments = true;
  await fixture.scan(game);
  const stored = await outbox(game.path);
  assert.ok(stored.length > 0, "unsent combat bytes must survive process exit");
  fixture.intercept = (record, response, reply) => {
    if (!/^\/api\/live\/\d+\/status$/.test(record.path)) return false;
    reply(404, { error: "unknown_session" });
    return true;
  };
  fixture.rejectSegments = false;
  await fixture.scan(game);
  assert.equal(fixture.calls("/api/live/start").length, 1);
  assert.deepEqual(await outbox(game.path), stored);
  assert.deepEqual([...fixture.reports.values()][0].bytes, Buffer.alloc(0));
  fixture.intercept = null;
  succeeded(await fixture.scan(game));
  assert.equal(fixture.calls("/api/live/start").length, 1);
  assert.deepEqual([...fixture.reports.values()][0].bytes, raw);
});

test("native Forever does not capture appended bytes under a nonlive report after restart", async (t) => {
  const pending = Buffer.from(
    "10/18 12:00:00.000  DURABLE PENDING SYNTHETIC EVENT\n",
  );
  const appended = Buffer.from("10/18 12:01:00.000  LATER SYNTHETIC EVENT\n");
  const game = await logFixture(t, pending);
  const fixture = await providerFixture(t);
  await login(fixture, game.path);
  fixture.rejectSegments = true;
  await fixture.scan(game);
  const stored = await outbox(game.path);
  assert.ok(stored.length > 0);
  const attemptsBefore = fixture.calls("/api/live/segment").length;
  await appendFile(game.logPath, appended);
  fixture.rejectSegments = false;
  fixture.intercept = (record, response, reply) => {
    if (!/^\/api\/live\/\d+\/status$/.test(record.path)) return false;
    reply(200, { session: { status: "finalized" } });
    return true;
  };
  await fixture.scan(game);
  assert.deepEqual(await outbox(game.path), stored);
  await fixture.scan(game);
  assert.deepEqual(await outbox(game.path), stored);
  assert.equal(fixture.calls("/api/live/segment").length, attemptsBefore);
  assert.equal(fixture.calls("/api/live/start").length, 1);
  fixture.intercept = null;
  succeeded(await fixture.scan(game));
  succeeded(await fixture.scan(game));
  const reports = [...fixture.reports.values()];
  assert.equal(reports.length, 1);
  assert.deepEqual(reports[0].bytes, Buffer.concat([pending, appended]));
  assert.deepEqual(
    reports[0].segments.map((segment) => segment.seq),
    [1, 2],
  );
});

test("native Forever rechecks report liveness after reconnecting in the same process", async (t) => {
  const original = Buffer.from("10/18 12:00:00.000  BEFORE DISCONNECT\n");
  const appended = Buffer.from("10/18 12:01:00.000  WHILE DISCONNECTED\n");
  const game = await logFixture(t, original);
  const fixture = await providerFixture(t);
  let disconnectedOutbox;
  fixture.intercept = async (record, response, reply) => {
    if (record.path === "/api/auth/desktop/revoke") {
      disconnectedOutbox = await outbox(game.path);
      const report = [...fixture.reports.values()][0];
      report.finalized = true;
      await appendFile(game.logPath, appended);
    }
    if (record.path === "/api/live/segment") {
      const report = fixture.reports.get(
        record.url.searchParams.get("reportId"),
      );
      if (report?.finalized) {
        reply(409, { error: "session_closed" });
        return true;
      }
    }
    return false;
  };
  succeeded(
    await fixture.run(
      game.path,
      [
        { action: "login" },
        { action: "scan" },
        { action: "disconnect" },
        { action: "login" },
        { action: "scan" },
      ],
      { logPath: game.logPath },
    ),
  );
  assert.deepEqual(await outbox(game.path), disconnectedOutbox);
  assert.equal(fixture.calls("/api/live/segment").length, 1);
  assert.equal(fixture.calls("/api/live/start").length, 1);
  const report = [...fixture.reports.values()][0];
  assert.deepEqual(report.bytes, original);
  report.finalized = false;
  succeeded(await fixture.scan(game));
  assert.equal(fixture.reports.size, 1);
  assert.deepEqual(report.bytes, Buffer.concat([original, appended]));
  assert.deepEqual(
    report.segments.map((segment) => segment.seq),
    [1, 2],
  );
});

test("native Forever recovers an interrupted owned journal write but preserves unknown outbox files", async (t) => {
  const raw = Buffer.from("10/18 12:00:00.000  DURABLE SYNTHETIC EVENT\n");
  const game = await logFixture(t, raw);
  const fixture = await providerFixture(t);
  await login(fixture, game.path);
  fixture.rejectSegments = true;
  await fixture.scan(game);
  const stored = await outbox(game.path);
  const journal = stored.find(([name]) => name.endsWith(".segment"));
  assert.ok(journal, "the failed upload must have a completed durable journal");
  const firstAttempt = fixture.calls("/api/live/segment")[0];
  const directory = join(game.path, "forever-outbox");
  const unexpectedPath = join(directory, "foreign-upload.tmp");
  const unexpectedBytes = Buffer.from(
    "synthetic foreign file must not be deleted",
  );
  await writeFile(unexpectedPath, unexpectedBytes);
  const withUnknown = await outbox(game.path);
  fixture.rejectSegments = false;
  await fixture.scan(game);
  assert.equal(fixture.calls("/api/live/segment").length, 1);
  assert.equal(fixture.calls("/api/live/start").length, 1);
  assert.deepEqual(await outbox(game.path), withUnknown);
  await rm(unexpectedPath);
  const interruptedPath = join(
    directory,
    `.${journal[0]}.${"a".repeat(32)}.tmp`,
  );
  await writeFile(
    interruptedPath,
    Buffer.from(journal[1], "base64").subarray(0, 5),
  );
  succeeded(await fixture.scan(game));
  const attempts = fixture.calls("/api/live/segment");
  assert.equal(attempts.length, 2);
  assert.deepEqual(attempts[1].bytes, firstAttempt.bytes);
  assert.equal(attempts[1].url.search, firstAttempt.url.search);
  await assert.rejects(readFile(interruptedPath), { code: "ENOENT" });
  const reports = [...fixture.reports.values()];
  assert.equal(reports.length, 1);
  assert.deepEqual(reports[0].bytes, raw);
  assert.equal(reports[0].segments.length, 1);
});

test("native Forever recovers a complete unpublished journal after the source rotates", async (t) => {
  const seed = Buffer.from("10/18 12:00:00.000  ALREADY ACKNOWLEDGED\n");
  const pending = Buffer.from(
    "10/18 12:01:00.000  OLD GENERATION DURABLE TAIL\n",
  );
  const replacement = Buffer.from("10/18 12:02:00.000  NEW GENERATION\n");
  const game = await logFixture(t, seed);
  const fixture = await providerFixture(t);
  await login(fixture, game.path);
  succeeded(await fixture.scan(game));
  const statePath = join(game.path, "config.forever.state.json");
  const beforeEnqueue = await readFile(statePath);
  await appendFile(game.logPath, pending);
  fixture.rejectSegments = true;
  await fixture.scan(game);
  const journal = (await outbox(game.path)).find(([name]) =>
    name.endsWith(".segment"),
  );
  assert.ok(
    journal,
    "capture a real completed journal, including its recovery metadata",
  );
  const pendingAttempt = fixture.calls("/api/live/segment")[1];
  const directory = join(game.path, "forever-outbox");
  const unpublishedPath = join(
    directory,
    `.${journal[0]}.${"b".repeat(32)}.tmp`,
  );
  // Reconstruct the crash boundary after writing the full record but before
  // publishing its filename or advancing the checkpoint. Neither is invented.
  await writeFile(unpublishedPath, Buffer.from(journal[1], "base64"));
  await rm(join(directory, journal[0]));
  await writeFile(statePath, beforeEnqueue);
  await rename(game.logPath, `${game.logPath}.old`);
  await writeFile(game.logPath, replacement);
  await rm(`${game.logPath}.old`);
  fixture.rejectSegments = false;
  succeeded(await fixture.scan(game));
  succeeded(await fixture.scan(game));
  const attempts = fixture.calls("/api/live/segment");
  assert.deepEqual(attempts[2].bytes, pendingAttempt.bytes);
  assert.equal(attempts[2].url.search, pendingAttempt.url.search);
  await assert.rejects(readFile(unpublishedPath), { code: "ENOENT" });
  const reports = [...fixture.reports.values()];
  assert.equal(reports.length, 2);
  assert.equal(reports[0].finalized, true);
  assert.deepEqual(reports[0].bytes, Buffer.concat([seed, pending]));
  assert.deepEqual(
    reports[0].segments.map((segment) => segment.seq),
    [1, 2],
  );
  assert.deepEqual(reports[1].bytes, replacement);
  assert.deepEqual(
    reports[1].segments.map((segment) => segment.seq),
    [1],
  );
});

test("native Forever never sends another account's durable pending combat bytes", async (t) => {
  const raw = Buffer.from(
    "10/18 12:00:00.000  ACCOUNT A PRIVATE SYNTHETIC EVENT\n",
  );
  const game = await logFixture(t, raw);
  const fixture = await providerFixture(t);
  await login(fixture, game.path);
  fixture.rejectSegments = true;
  await fixture.scan(game);
  const stored = await outbox(game.path);
  assert.ok(stored.length > 0);
  const before = fixture.calls("/api/live/segment").length;
  fixture.userId = 702;
  fixture.rejectSegments = false;
  const changed = await fixture.run(
    game.path,
    [{ action: "disconnect" }, { action: "login" }, { action: "scan" }],
    { logPath: game.logPath },
  );
  assert.equal(changed.results[1].connected, true);
  assert.equal(fixture.calls("/api/live/segment").length, before);
  assert.equal(fixture.calls("/api/live/start").length, 1);
  assert.deepEqual(await outbox(game.path), stored);
  fixture.userId = 701;
  succeeded(
    await fixture.run(
      game.path,
      [{ action: "disconnect" }, { action: "login" }, { action: "scan" }],
      { logPath: game.logPath },
    ),
  );
  assert.deepEqual([...fixture.reports.values()][0].bytes, raw);
});

test("native Forever fails closed without replacing a corrupt synchronization checkpoint", async (t) => {
  const game = await logFixture(
    t,
    Buffer.from("10/18 12:00:00.000  SYNTHETIC EVENT\n"),
  );
  const fixture = await providerFixture(t);
  await login(fixture, game.path);
  const statePath = join(game.path, "config.forever.state.json");
  const corrupt = Buffer.from('{"version":1,"unfinished":');
  await writeFile(statePath, corrupt);
  await fixture.scan(game);
  assert.deepEqual(await readFile(statePath), corrupt);
  assert.equal(fixture.calls("/api/live/start").length, 0);
  assert.equal(fixture.calls("/api/live/segment").length, 0);
});

test("native Forever does not expose provider error bodies or credentials in diagnostics", async (t) => {
  const path = await temporary(t);
  const fixture = await providerFixture(t);
  await login(fixture, path);
  const marker = "synthetic-private-provider-diagnostic";
  fixture.intercept = (record, response, reply) => {
    if (record.path !== "/api/live/start") return false;
    reply(503, {
      error: `${marker} ${fixture.accessToken} ${fixture.refreshToken}`,
      detail: `${fixture.serverUrl}/private-account-data`,
    });
    return true;
  };
  const result = await fixture.run(path, [{ action: "start" }]);
  assert.equal(result.results[0].error?.status, 503);
  const visible = JSON.stringify(result);
  assert.equal(visible.includes(marker), false);
  assert.equal(visible.includes(fixture.serverUrl), false);
  for (const tokens of fixture.issued) {
    assert.equal(visible.includes(tokens.accessToken), false);
    assert.equal(visible.includes(tokens.refreshToken), false);
  }
});
