import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { createContext, Script } from "node:vm";
import { Matrix3, Matrix4, Vector3 } from "three";
import { selectLocalIndexFiles } from "../companion/renderer/patch-exporter.mjs";

const adapterUrl = new URL("../companion/renderer/adapter.js", import.meta.url);
const context = createContext();
new Script(await readFile(adapterUrl, "utf8"), {
  filename: adapterUrl.pathname,
}).runInContext(context);
const readBoneCorrections = new Script(
  "aurify_read_bone_corrections",
).runInContext(context);
const frozenEnvironmentUV = new Script(
  "aurify_frozen_environment_uv",
).runInContext(context);

// Only the binary reader boundary is synthetic; the complete adapter above is real.
class BinaryReader {
  constructor(bytes) {
    this.bytes = bytes;
    this.offset = 0;
  }
  get byteLength() {
    return this.bytes.length;
  }
  get remainingBytes() {
    return this.byteLength - this.offset;
  }
  readUInt32LE() {
    const value = this.bytes.readUInt32LE(this.offset);
    this.offset += 4;
    return value;
  }
  readUInt16LE() {
    const value = this.bytes.readUInt16LE(this.offset);
    this.offset += 2;
    return value;
  }
  readFloatLE() {
    const value = this.bytes.readFloatLE(this.offset);
    this.offset += 4;
    return value;
  }
}

const IDENTITY = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
// Column-major native matrices, authored independently of the parser's basis math.
// Scale (2,3,4), rotate +90 degrees about Z, then translate (10,20,30).
const ROTATE_Z = [0, 2, 0, 0, -3, 0, 0, 0, 0, 0, 4, 0, 10, 20, 30, 1];
// Scale (5,6,7), rotate +90 degrees about X, then translate (-4,8,-12).
const ROTATE_X = [5, 0, 0, 0, 0, 0, 6, 0, 0, -7, 0, 0, -4, 8, -12, 1];

function bones() {
  return Array.from({ length: 7 }, (_, index) => ({
    parentBone: -1,
    flags: 0x200,
    pivot: [index, index + 1, -index],
  }));
}

function chunk(tag, payload) {
  const header = Buffer.alloc(8);
  header.write(tag, 0, 4, "ascii");
  header.writeUInt32LE(payload.length, 4);
  return Buffer.concat([header, payload]);
}

function indexChunk(indices) {
  const payload = Buffer.alloc(indices.length * 2);
  indices.forEach((index, at) => payload.writeUInt16LE(index, at * 2));
  return chunk("BIDA", payload);
}

function matrixChunk(matrices) {
  const payload = Buffer.alloc(matrices.length * 64);
  matrices.forEach((matrix, at) => {
    matrix.forEach((value, component) => {
      payload.writeFloatLE(value, at * 64 + component * 4);
    });
  });
  return chunk("BOMT", payload);
}

function file(...chunks) {
  return Buffer.concat([Buffer.from([1, 0, 0, 0]), ...chunks]);
}

function fixture(indices = [1], matrices = [IDENTITY]) {
  return file(indexChunk(indices), matrixChunk(matrices));
}

function parse(bytes, metadata = bones()) {
  return readBoneCorrections(new BinaryReader(bytes), metadata);
}

function point(matrix, [x, y, z]) {
  return [0, 1, 2, 3].map(
    (row) =>
      matrix[row] * x +
      matrix[4 + row] * y +
      matrix[8 + row] * z +
      matrix[12 + row],
  );
}

function changedMatrix(component, value) {
  const matrix = [...IDENTITY];
  matrix[component] = value;
  return matrix;
}

test("sparse BONE corrections preserve target identity and conjugate the entire affine transform", () => {
  const corrections = parse(fixture([5, 1], [ROTATE_Z, ROTATE_X]));
  assert.deepEqual(
    Array.from(corrections.keys()).sort((a, b) => a - b),
    [1, 5],
  );
  assert.deepEqual(point(corrections.get(5), [0, 0, 0]), [10, 30, -20, 1]);
  // Renderer (1,3,-2) is native (1,2,3). Native Z correction gives (4,22,42).
  assert.deepEqual(point(corrections.get(5), [1, 3, -2]), [4, 42, -22, 1]);
  assert.deepEqual(point(corrections.get(5), [-2, 5, 4]), [22, 50, -16, 1]);
  assert.deepEqual(point(corrections.get(1), [0, 0, 0]), [-4, -12, -8, 1]);
  assert.deepEqual(point(corrections.get(1), [1, 3, -2]), [1, 0, 13, 1]);
});

test("a literal identity correction preserves points on bone zero", () => {
  const corrections = parse(fixture([0], [IDENTITY]));
  assert.deepEqual(Array.from(corrections.keys()), [0]);
  assert.deepEqual(point(corrections.get(0), [-2, 3, 5]), [-2, 3, 5, 1]);
});

test("LOD bone flags preserve full-detail corrections without admitting unknown flags", () => {
  const metadata = bones();
  metadata[1].flags = 0x40200;
  metadata[3].flags = 0x60200;
  metadata[3].parentBone = 1;
  metadata[5].flags = 0x70200;
  metadata[5].parentBone = 3;
  const corrections = parse(fixture([3], [ROTATE_Z]), metadata);
  assert.deepEqual(point(corrections.get(3), [1, 3, -2]), [4, 42, -22, 1]);
  metadata[3].flags |= 0x80000;
  assert.throws(() => parse(fixture([3], [ROTATE_Z]), metadata));
});

test("BONE rejects unsupported versions and unknown chunk records", () => {
  const unsupported = fixture();
  unsupported.writeUInt32LE(2, 0);
  assert.throws(() => parse(unsupported));
  assert.throws(() =>
    parse(file(chunk("NOPE", Buffer.from([1, 0])), matrixChunk([IDENTITY]))),
  );
});

test("BONE rejects incomplete headers and payloads rather than accepting a partial correction", () => {
  const complete = fixture();
  assert.throws(() => parse(complete.subarray(0, 3)), "truncated version");
  assert.throws(
    () => parse(Buffer.concat([complete, Buffer.from("BID")])),
    "truncated trailing chunk header",
  );
  assert.throws(
    () => parse(complete.subarray(0, complete.length - 1)),
    "truncated matrix",
  );
  const oversized = fixture();
  oversized.writeUInt32LE(0xffffffff, 8);
  assert.throws(
    () => parse(oversized),
    "declared payload exceeds available bytes",
  );
});

test("BONE requires complete index and matrix records, not fractional entries", () => {
  assert.throws(() =>
    parse(file(chunk("BIDA", Buffer.from([1, 0, 2])), matrixChunk([IDENTITY]))),
  );
  assert.throws(() =>
    parse(file(indexChunk([1]), chunk("BOMT", Buffer.alloc(63)))),
  );
});

test("BONE requires both nonempty chunks and equal index and matrix counts", () => {
  assert.throws(() => parse(file(matrixChunk([IDENTITY]))), "missing indices");
  assert.throws(() => parse(file(indexChunk([1]))), "missing matrices");
  assert.throws(() => parse(fixture([], [IDENTITY])), "empty indices");
  assert.throws(() => parse(fixture([1], [])), "empty matrices");
  assert.throws(() => parse(fixture([1, 5], [IDENTITY])), "unpaired index");
  assert.throws(
    () => parse(fixture([1], [IDENTITY, ROTATE_Z])),
    "unpaired matrix",
  );
});

test("BONE rejects repeated chunks rather than replacing an earlier correction", () => {
  assert.throws(() =>
    parse(file(indexChunk([1]), indexChunk([5]), matrixChunk([IDENTITY]))),
  );
  assert.throws(() =>
    parse(
      file(indexChunk([1]), matrixChunk([IDENTITY]), matrixChunk([ROTATE_Z])),
    ),
  );
});

test("BONE rejects duplicate targets and indices outside the model", () => {
  assert.throws(() => parse(fixture([1, 1], [IDENTITY, ROTATE_Z])));
  assert.throws(() => parse(fixture([7], [IDENTITY])));
});

test("BONE rejects nonfinite linear and translation components", () => {
  assert.throws(() => parse(fixture([1], [changedMatrix(0, Number.NaN)])));
  assert.throws(() =>
    parse(fixture([1], [changedMatrix(12, Number.POSITIVE_INFINITY)])),
  );
});

test("BONE rejects perspective rows and non-unit homogeneous coordinates", () => {
  for (const component of [3, 7, 11]) {
    assert.throws(
      () => parse(fixture([1], [changedMatrix(component, 0.25)])),
      `perspective component ${component}`,
    );
  }
  assert.throws(() => parse(fixture([1], [changedMatrix(15, 2)])));
});

test("BONE rejects singular corrections before they can collapse skinned geometry", () => {
  assert.throws(() => parse(fixture([1], [changedMatrix(0, 0)])));
});

test("BONE rejects cyclic and invalid parent chains", () => {
  const cyclic = bones();
  cyclic[1].parentBone = 5;
  cyclic[5].parentBone = 1;
  assert.throws(() => parse(fixture(), cyclic));
  const outside = bones();
  outside[1].parentBone = outside.length;
  assert.throws(() => parse(fixture(), outside));
});

test("unsupported bone semantics cannot enter a correction through its ancestor or descendant", () => {
  for (const unsupportedIndex of [1, 3, 5]) {
    const metadata = bones();
    metadata[3].parentBone = 1;
    metadata[5].parentBone = 3;
    metadata[unsupportedIndex].flags = 0x8;
    assert.throws(() => parse(fixture([3], [ROTATE_Z]), metadata));
  }
  const unrelated = bones();
  unrelated[6].flags = 0x8;
  const corrections = parse(fixture([3], [ROTATE_Z]), unrelated);
  assert.deepEqual(Array.from(corrections.keys()), [3]);
  assert.deepEqual(point(corrections.get(3), [1, 3, -2]), [4, 42, -22, 1]);
});

test("frozen reflection UVs preserve inverse-transpose normals under weighted nonuniform skinning", () => {
  const model = new Matrix4()
    .makeRotationY(0.23)
    .scale(new Vector3(1.1, 0.9, 1.2))
    .setPosition(0.14, -0.28, 0.34);
  const firstBone = new Matrix4()
    .makeRotationX(0.31)
    .scale(new Vector3(2, 0.7, 1.3))
    .setPosition(0.2, -0.1, 0.3);
  const secondBone = new Matrix4()
    .makeRotationZ(-0.47)
    .scale(new Vector3(0.8, 1.6, 0.9))
    .setPosition(-0.3, 0.2, 0.1);
  const renderer = {
    m2: {
      vertices: new Float64Array([0.4, 0.8, -0.2]),
      normals: new Float64Array([0.2, 0.7, -0.4]),
      boneIndices: new Uint8Array([0, 1, 0, 0]),
      boneWeights: new Uint8Array([93, 162, 0, 0]),
    },
    model_matrix: model.elements,
    bones: [{}, {}],
    bone_matrices: new Float64Array([
      ...firstBone.elements,
      ...secondBone.elements,
    ]),
  };
  const eye = new Vector3(0, 1.609, 2.347);
  const view = new Matrix4()
    .lookAt(eye, new Vector3(0, 1.247, 0.537), new Vector3(0, 1, 0))
    .setPosition(eye)
    .invert();
  const weighted = new Matrix4().fromArray(
    firstBone.elements.map(
      (value, index) => (value * 93 + secondBone.elements[index] * 162) / 255,
    ),
  );
  const combined = view.multiply(model).multiply(weighted);
  const normal = new Vector3(...renderer.m2.normals).applyNormalMatrix(
    new Matrix3().getNormalMatrix(combined),
  );
  const reflected = new Vector3(...renderer.m2.vertices)
    .applyMatrix4(combined)
    .normalize()
    .reflect(normal);
  const denominator =
    2 * Math.sqrt(reflected.x ** 2 + reflected.y ** 2 + (reflected.z + 1) ** 2);
  const expected = [
    reflected.x / denominator + 0.5,
    reflected.y / denominator + 0.5,
  ];
  const projected = frozenEnvironmentUV(renderer);
  projected.ensure(0);
  for (let component = 0; component < 2; component++)
    assert(Math.abs(projected.uv[component] - expected[component]) < 1e-12);

  renderer.bone_matrices.fill(0);
  assert.throws(
    () => frozenEnvironmentUV(renderer).ensure(0),
    /incomplete-materials/,
    "A singular skinned normal transform must not invent reflection UVs",
  );
});

test("local CASC indexes select the newest generation per bucket regardless of enumeration order", () => {
  const entries = [
    "0400000096.idx",
    "0a7fffffff.idx",
    "0400000098.idx",
    "0a80000000.idx",
    "0400000097.idx",
    "data.000",
  ].map((name) => ({ name, isFile: () => true }));
  entries.push({ name: "04ffffffff.idx", isFile: () => false });
  const expected = new Set(["0400000098.idx", "0a80000000.idx"]);
  assert.deepEqual(new Set(selectLocalIndexFiles(entries)), expected);
  assert.deepEqual(
    new Set(selectLocalIndexFiles(entries.toReversed())),
    expected,
  );
});

test("local CASC indexes reject unknown or ambiguous journal identities rather than selecting an older file", () => {
  const older = { name: "0400000096.idx", isFile: () => true };
  for (const name of [
    "040000009g.idx",
    "1000000098.idx",
    "0400000098-extra.idx",
  ])
    assert.throws(
      () => selectLocalIndexFiles([older, { name, isFile: () => true }]),
      /missing-game-data/,
    );
  assert.throws(
    () =>
      selectLocalIndexFiles(
        ["04000000aa.idx", "04000000AA.idx"].map((name) => ({
          name,
          isFile: () => true,
        })),
      ),
    /missing-game-data/,
  );
});
