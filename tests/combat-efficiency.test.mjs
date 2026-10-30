import assert from "node:assert/strict";
import { createServer } from "node:http";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { nativeAsync } from "./native-bridge.mjs";

const work = resolve(
  dirname(fileURLToPath(import.meta.url)),
  "../.work/native-tests",
);
const token = "T".repeat(43);
const encoded = (value) => Buffer.from(value).toString("base64");

async function fixture(t, initial, options = {}) {
  const directory = await mkdtemp(join(work, "combat-efficiency-"));
  t.after(() => rm(directory, { recursive: true, force: true, maxRetries: 5 }));
  const path = join(directory, "SyntheticCombat.txt");
  await writeFile(path, initial);
  const streams = new Map();
  const requests = [];
  let step = -1,
    failure,
    lost = false,
    conflicted = false;
  const server = createServer(async (request, response) => {
    const reply = (status, body) => {
      response.writeHead(status, { "Content-Type": "application/json" });
      response.end(JSON.stringify(body));
    };
    try {
      const marker = /^\/probe\/step\/(\d+)$/.exec(request.url);
      if (marker) {
        step = Number(marker[1]);
        if (step === options.rollbackStep)
          for (const stream of streams.values()) stream.bytes = Buffer.alloc(0);
        return reply(200, {});
      }
      assert.equal(request.headers.authorization, `Bearer ${token}`);
      const chunks = [];
      for await (const chunk of request) chunks.push(chunk);
      const body = chunks.length ? JSON.parse(Buffer.concat(chunks)) : null;
      requests.push({ step, url: request.url, body });
      const match = /^\/members\/api\/ingest\/combat\/([^/]+)\/status$/.exec(
        request.url,
      );
      if (match) {
        if (step === options.failStatusStep) return reply(503, {});
        const stream = streams.get(match[1]);
        return stream
          ? reply(200, {
              offset: stream.bytes.length,
              reportId: stream.reportId,
            })
          : reply(404, {});
      }
      assert.equal(request.url, "/members/api/ingest/combat");
      let stream = streams.get(body.streamId);
      if (!stream) {
        stream = {
          bytes: Buffer.alloc(0),
          reportId: `synthetic-${streams.size}`,
          closed: false,
        };
        streams.set(body.streamId, stream);
      }
      if (step === options.conflictStep && !conflicted && !body.closed) {
        conflicted = true;
        stream.bytes = Buffer.alloc(0);
        return reply(409, { expectedOffset: 0 });
      }
      if (body.offset !== stream.bytes.length)
        return reply(409, { expectedOffset: stream.bytes.length });
      const data = Buffer.from(body.data, "base64");
      assert.ok(data.length <= 512 * 1024);
      stream.bytes = Buffer.concat([stream.bytes, data]);
      stream.closed = body.closed;
      if (
        !lost &&
        ((step === options.loseStep && !body.closed) ||
          (step === options.loseCloseStep && body.closed))
      ) {
        lost = true;
        response.destroy();
        return;
      }
      return reply(200, {
        accepted: true,
        offset: stream.bytes.length,
        reportId: stream.reportId,
      });
    } catch (error) {
      failure = error;
      response.writeHead(500).end();
    }
  });
  await new Promise((done) => server.listen(0, "127.0.0.1", done));
  t.after(async () => {
    server.closeAllConnections();
    await new Promise((done) => server.close(done));
    if (failure) throw failure;
  });
  return {
    streams,
    requests,
    status: (index) =>
      requests.filter((item) => item.step === index && item.body === null),
    uploads: (index) =>
      requests.filter((item) => item.step === index && item.body !== null),
    run: (steps, extra = {}) =>
      nativeAsync({
        operation: "combat-efficiency",
        root: path,
        serverUrl: `http://127.0.0.1:${server.address().port}`,
        token,
        now: Date.UTC(2026, 9, 26, 12),
        timezone: "UTC",
        steps,
        ...extra,
      }),
  };
}

test("caught-up persistent combat scans skip only idle status until safety interval, restart, data or cancellation", async (t) => {
  const portal = await fixture(t, "synthetic first\n");
  const result = await portal.run([
    {},
    { advance: 1 },
    { advance: 59998 },
    { advance: 1 },
    {},
    { restart: true },
    { append: encoded("synthetic next\n") },
    {},
    { cancel: true },
    {},
    {},
  ]);
  assert.deepEqual(
    result.map((item) => item.error),
    [
      null,
      null,
      null,
      null,
      null,
      null,
      null,
      null,
      "OperationCanceledException",
      null,
      null,
    ],
  );
  assert.deepEqual(
    result.map((_, index) => portal.status(index).length),
    [1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 0],
  );
  assert.deepEqual(
    [...portal.streams.values()][0].bytes,
    Buffer.from("synthetic first\nsynthetic next\n"),
  );
});

test("full combat chunk with timestamp lookahead uploads selected bytes once and preserves exact ACK offsets", async (t) => {
  const chunk = Buffer.alloc(512 * 1024, "x");
  chunk[chunk.length - 1] = 10;
  const tail = Buffer.from("10/26 12:00:00.000  SYNTHETIC\r\n");
  const bytes = Buffer.concat([chunk, tail]);
  const portal = await fixture(t, bytes);
  const result = await portal.run([{}, {}]);
  assert.deepEqual(
    result.map((item) => item.error),
    [null, null],
  );
  assert.deepEqual(
    portal
      .uploads(0)
      .map(({ body }) => [
        body.offset,
        Buffer.from(body.data, "base64").length,
      ]),
    [
      [0, chunk.length],
      [chunk.length, tail.length],
    ],
  );
  assert.deepEqual([...portal.streams.values()][0].bytes, bytes);
  assert.equal(result[0].durable.combat.current.offset, bytes.length);
  assert.equal(portal.status(1).length, 0);
});

test("lost combat ACK reconciles on the same instance before becoming idle without duplicate bytes", async (t) => {
  const bytes = Buffer.from("synthetic lost ACK\n");
  const portal = await fixture(t, bytes, { loseStep: 0 });
  const result = await portal.run([{}, {}, {}]);
  assert.notEqual(result[0].error, null);
  assert.deepEqual(
    result.slice(1).map((item) => item.error),
    [null, null],
  );
  assert.deepEqual(
    result.map((_, index) => portal.status(index).length),
    [1, 1, 0],
  );
  assert.equal(portal.uploads(1).length, 0);
  assert.equal(result[1].durable.combat.current.offset, bytes.length);
  assert.deepEqual([...portal.streams.values()][0].bytes, bytes);
});

for (const failSaveAt of [2, 3]) {
  test(`failed ${failSaveAt === 2 ? "pre-dispatch" : "post-ACK"} durable combat save forces reconciliation and successful publication before idle`, async (t) => {
    const bytes = Buffer.from("synthetic save retry\n");
    const portal = await fixture(t, bytes);
    const result = await portal.run([{ failSaveAt }, {}, {}]);
    assert.equal(result[0].error, "IOException");
    assert.equal(result[0].durable.combat.current.offset, 0);
    assert.deepEqual(
      result.slice(1).map((item) => item.error),
      [null, null],
    );
    assert.deepEqual(
      result.map((_, index) => portal.status(index).length),
      [1, 1, 0],
    );
    assert.equal(result[1].durable.combat.current.offset, bytes.length);
    assert.equal(portal.uploads(failSaveAt === 2 ? 0 : 1).length, 0);
    assert.deepEqual([...portal.streams.values()][0].bytes, bytes);
  });
}

test("periodic reconciliation repairs server rollback while a failed status remains dirty", async (t) => {
  const bytes = Buffer.from("synthetic rollback\n");
  const portal = await fixture(t, bytes, {
    rollbackStep: 1,
    failStatusStep: 1,
  });
  const result = await portal.run([{}, { advance: 60000 }, {}, {}]);
  assert.equal(result[1].error, "PortalException");
  assert.deepEqual(
    result.map((_, index) => portal.status(index).length),
    [1, 1, 1, 0],
  );
  assert.equal(portal.uploads(2)[0].body.offset, 0);
  assert.equal(result[2].durable.combat.current.offset, bytes.length);
  assert.deepEqual([...portal.streams.values()][0].bytes, bytes);
});

test("409 rollback after new data cannot mark combat reconciliation clean prematurely", async (t) => {
  const portal = await fixture(t, "synthetic initial\n", { conflictStep: 1 });
  const result = await portal.run([
    {},
    { append: encoded("synthetic append\n") },
    {},
    {},
  ]);
  assert.deepEqual(
    result.map((item) => item.error),
    [null, null, null, null],
  );
  assert.deepEqual(
    result.map((_, index) => portal.status(index).length),
    [1, 1, 1, 0],
  );
  assert.deepEqual(
    [...portal.streams.values()][0].bytes,
    Buffer.from("synthetic initial\nsynthetic append\n"),
  );
});

test("idle combat identity and prefix checks still rotate replacement and same-size rewrite, closing old streams with empty bytes", async (t) => {
  const portal = await fixture(t, "synthetic one\n");
  const result = await portal.run([
    {},
    { replace: encoded("synthetic two\n") },
    {},
    { rewrite: encoded("synthetic six\n") },
    {},
  ]);
  assert.deepEqual(
    result.map((item) => item.error),
    [null, null, null, null, null],
  );
  assert.deepEqual(
    [...portal.streams.values()].map((stream) => [
      stream.bytes.toString(),
      stream.closed,
    ]),
    [
      ["synthetic one\n", true],
      ["synthetic two\n", true],
      ["synthetic six\n", false],
    ],
  );
  for (const index of [1, 3]) {
    const closure = portal.uploads(index).find(({ body }) => body.closed);
    assert.equal(closure.body.data, "");
    assert.equal(closure.body.offset, Buffer.byteLength("synthetic one\n"));
    assert.equal(portal.status(index).length, 2);
  }
  assert.equal(portal.status(2).length, 0);
  assert.equal(portal.status(4).length, 0);
});

test("lost closure ACK keeps the replacement stream dirty until the old closure is reconciled", async (t) => {
  const portal = await fixture(t, "synthetic old\n", { loseCloseStep: 1 });
  const result = await portal.run([
    {},
    { replace: encoded("synthetic new\n") },
    {},
    {},
  ]);
  assert.notEqual(result[1].error, null);
  assert.equal(result[1].durable.combat.pendingClosures.length, 1);
  assert.equal(result[2].error, null);
  assert.equal(result[2].durable.combat.pendingClosures.length, 0);
  assert.equal(portal.status(2).length, 2);
  assert.equal(portal.status(3).length, 0);
  assert.deepEqual(
    [...portal.streams.values()].map((stream) => [
      stream.bytes.toString(),
      stream.closed,
    ]),
    [
      ["synthetic old\n", true],
      ["synthetic new\n", false],
    ],
  );
});

test("host timezone transition reconciles and closes an idle stream before the safety interval", async (t) => {
  const portal = await fixture(t, "synthetic timezone\n");
  const result = await portal.run([{}, { advance: 20000 }, {}], {
    timezone: "Eastern Standard Time",
    now: Date.UTC(2026, 10, 1, 5, 59, 50),
  });
  assert.deepEqual(
    result.map((item) => item.error),
    [null, null, null],
  );
  assert.equal(result[0].durable.combat.current.hostTimezoneOffsetMinutes, 240);
  assert.equal(result[1].durable.combat.current.hostTimezoneOffsetMinutes, 300);
  assert.notEqual(
    result[0].durable.combat.current.streamId,
    result[1].durable.combat.current.streamId,
  );
  assert.equal(
    result[1].durable.combat.current.baseOffset,
    Buffer.byteLength("synthetic timezone\n"),
  );
  assert.equal(portal.status(1).length, 2);
  assert.equal(portal.status(2).length, 1);
  assert.equal([...portal.streams.values()][0].closed, true);
});
