import { readFile, readdir, lstat, realpath } from "node:fs/promises";
import { basename, join, resolve } from "node:path";
import { inflateSync } from "node:zlib";
import { BrowserBlob } from "./browser-blob.mjs";
import * as THREE from "./vendor/three.module.js";
import { OBJLoader } from "./vendor/OBJLoader.js";
import { MTLLoader } from "./vendor/MTLLoader.js";
import { GLTFExporter } from "./vendor/GLTFExporter.js";

const fail = () => {
  throw new Error("invalid-artifact");
};
const MAX_BYTES = 64 * 1024 * 1024;
const MAX_PIXELS = 16777216;
const pngSignature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
function pngSize(bytes) {
  if (
    bytes.length < 33 ||
    !bytes.subarray(0, 8).equals(pngSignature) ||
    bytes.toString("ascii", 12, 16) !== "IHDR"
  )
    fail();
  const width = bytes.readUInt32BE(16),
    height = bytes.readUInt32BE(20);
  if (
    !width ||
    !height ||
    width > 8192 ||
    height > 8192 ||
    width * height > MAX_PIXELS ||
    bytes[24] !== 8 ||
    ![2, 6].includes(bytes[25]) ||
    bytes[26] ||
    bytes[27] ||
    bytes[28]
  )
    fail();
  return { width, height };
}
function opaquePngCanvas(bytes) {
  const { width, height } = pngSize(bytes);
  const channels = bytes[25] === 6 ? 4 : 3;
  const compressed = [];
  let offset = 8,
    ended = false;
  while (offset + 12 <= bytes.length) {
    const length = bytes.readUInt32BE(offset),
      type = bytes.toString("ascii", offset + 4, offset + 8);
    if (length > bytes.length - offset - 12) fail();
    if (type === "IDAT")
      compressed.push(bytes.subarray(offset + 8, offset + 8 + length));
    offset += length + 12;
    if (type === "IEND") {
      if (length) fail();
      ended = true;
      break;
    }
  }
  if (!ended || offset !== bytes.length || !compressed.length) fail();
  const stride = width * channels;
  const decoded = inflateSync(Buffer.concat(compressed), {
    maxOutputLength: (stride + 1) * height,
  });
  if (decoded.length !== (stride + 1) * height) fail();
  let previous = new Uint8Array(stride),
    row = new Uint8Array(stride);
  const pixels = new Uint8ClampedArray(width * height * 4);
  for (let y = 0; y < height; y++) {
    const start = y * (stride + 1),
      filter = decoded[start];
    if (filter > 4) fail();
    for (let x = 0; x < stride; x++) {
      const left = x >= channels ? row[x - channels] : 0;
      const up = previous[x],
        upperLeft = x >= channels ? previous[x - channels] : 0;
      let predictor = 0;
      if (filter === 1) predictor = left;
      else if (filter === 2) predictor = up;
      else if (filter === 3) predictor = Math.floor((left + up) / 2);
      else if (filter === 4) {
        const p = left + up - upperLeft;
        const a = Math.abs(p - left),
          b = Math.abs(p - up),
          c = Math.abs(p - upperLeft);
        predictor = a <= b && a <= c ? left : b <= c ? up : upperLeft;
      }
      row[x] = (decoded[start + 1 + x] + predictor) & 255;
    }
    for (let x = 0; x < width; x++) {
      const source = x * channels,
        target = (y * width + x) * 4;
      pixels[target] = row[source];
      pixels[target + 1] = row[source + 1];
      pixels[target + 2] = row[source + 2];
      pixels[target + 3] = 255;
    }
    const previousRow = previous;
    previous = row;
    row = previousRow;
  }
  // Decode straight RGB before making alpha opaque: Canvas2D would otherwise erase RGB in transparent texels.
  const canvas = document.createElement("canvas");
  canvas.width = width;
  canvas.height = height;
  const context = canvas.getContext("2d");
  if (!context) fail();
  context.putImageData(
    new canvas.ownerDocument.defaultView.ImageData(pixels, width, height),
    0,
    0,
  );
  return canvas;
}
function unpackGlb(bytes) {
  if (
    bytes.length < 28 ||
    bytes.readUInt32LE(0) !== 0x46546c67 ||
    bytes.readUInt32LE(4) !== 2 ||
    bytes.readUInt32LE(8) !== bytes.length
  )
    fail();
  const jsonSize = bytes.readUInt32LE(12);
  if (
    jsonSize % 4 ||
    bytes.readUInt32LE(16) !== 0x4e4f534a ||
    20 + jsonSize + 8 > bytes.length
  )
    fail();
  const binOffset = 20 + jsonSize;
  if (
    bytes.readUInt32LE(binOffset + 4) !== 0x004e4942 ||
    binOffset + 8 + bytes.readUInt32LE(binOffset) !== bytes.length
  )
    fail();
  return {
    json: JSON.parse(bytes.toString("utf8", 20, 20 + jsonSize)),
    bin: bytes.subarray(binOffset + 8),
  };
}
function sanitizeGlb(raw) {
  const { json, bin } = unpackGlb(Buffer.from(raw));
  if (
    json.asset?.version !== "2.0" ||
    json.asset.generator !== "THREE.GLTFExporter r180"
  )
    fail();
  // Only standard static output from this conversion is admitted. Remove names at every nesting level.
  const materialObjects = new Set(
    Array.isArray(json.materials) ? json.materials : [],
  );
  let hasUnlit = false;
  const walk = (value) => {
    if (!value || typeof value !== "object") return;
    if (Array.isArray(value)) {
      value.forEach(walk);
      return;
    }
    delete value.name;
    for (const [key, child] of Object.entries(value)) {
      if (
        ["uri", "extras", "animations", "skins", "cameras", "targets"].includes(
          key,
        )
      )
        fail();
      if (key === "extensions") {
        if (
          !materialObjects.has(value) ||
          !child ||
          Array.isArray(child) ||
          Object.keys(child).length !== 1 ||
          !Object.hasOwn(child, "KHR_materials_unlit")
        )
          fail();
        const unlit = child.KHR_materials_unlit;
        if (
          !unlit ||
          typeof unlit !== "object" ||
          Array.isArray(unlit) ||
          Object.keys(unlit).length
        )
          fail();
        hasUnlit = true;
        continue;
      }
      if (key === "extensionsUsed" || key === "extensionsRequired") {
        if (
          value !== json ||
          !Array.isArray(child) ||
          child.length !== 1 ||
          child[0] !== "KHR_materials_unlit"
        )
          fail();
        continue;
      }
      walk(child);
    }
  };
  walk(json);
  if (
    hasUnlit !== Object.hasOwn(json, "extensionsUsed") ||
    (Object.hasOwn(json, "extensionsRequired") && !hasUnlit)
  )
    fail();
  for (const key of Object.keys(json.asset))
    if (!["version", "generator"].includes(key)) delete json.asset[key];
  if (
    json.buffers?.length !== 1 ||
    json.buffers[0].byteLength > bin.length ||
    json.scenes?.length !== 1 ||
    !json.meshes?.length ||
    !json.images?.length ||
    !json.materials?.length
  )
    fail();
  let pixels = 0;
  for (const image of json.images) {
    if (image.mimeType !== "image/png" || !Number.isInteger(image.bufferView))
      fail();
    const view = json.bufferViews?.[image.bufferView];
    if (
      !view ||
      view.buffer !== 0 ||
      (view.byteOffset || 0) + view.byteLength > bin.length
    )
      fail();
    const size = pngSize(
      bin.subarray(
        view.byteOffset || 0,
        (view.byteOffset || 0) + view.byteLength,
      ),
    );
    pixels += size.width * size.height;
  }
  if (pixels > MAX_PIXELS) fail();
  for (const material of json.materials)
    if (!material.pbrMetallicRoughness?.baseColorTexture) fail();
  for (const mesh of json.meshes)
    for (const primitive of mesh.primitives || []) {
      if (
        (primitive.mode ?? 4) !== 4 ||
        Object.keys(primitive.attributes).some(
          (key) => !["POSITION", "NORMAL", "TEXCOORD_0"].includes(key),
        )
      )
        fail();
    }
  const text = Buffer.from(JSON.stringify(json));
  const padded = Buffer.alloc(Math.ceil(text.length / 4) * 4, 32);
  text.copy(padded);
  const bytes = Buffer.alloc(12 + 8 + padded.length + 8 + bin.length);
  bytes.writeUInt32LE(0x46546c67, 0);
  bytes.writeUInt32LE(2, 4);
  bytes.writeUInt32LE(bytes.length, 8);
  bytes.writeUInt32LE(padded.length, 12);
  bytes.writeUInt32LE(0x4e4f534a, 16);
  padded.copy(bytes, 20);
  bytes.writeUInt32LE(bin.length, 20 + padded.length);
  bytes.writeUInt32LE(0x004e4942, 24 + padded.length);
  bin.copy(bytes, 28 + padded.length);
  if (bytes.length > MAX_BYTES) fail();
  return bytes;
}
export async function convert(directory, exported) {
  if (
    !Array.isArray(exported?.files) ||
    !Array.isArray(exported?.materials) ||
    exported.files.length > 512 ||
    exported.materials.length > 2048
  )
    fail();
  const root = await realpath(directory);
  const expected = new Set();
  for (const entry of exported.files) {
    if (
      !["OBJ", "MTL", "PNG"].includes(entry.type) ||
      typeof entry.file !== "string" ||
      resolve(entry.file) !== join(root, basename(entry.file))
    )
      fail();
    expected.add(basename(entry.file));
  }
  const contents = new Map();
  let total = 0;
  for (const entry of await readdir(root, { withFileTypes: true })) {
    if (
      !entry.isFile() ||
      entry.isSymbolicLink() ||
      !expected.delete(entry.name) ||
      !/^(character\.(obj|mtl)|(?:data-)?\d+\.png)$/.test(entry.name)
    )
      fail();
    const file = join(root, entry.name),
      info = await lstat(file);
    if (
      info.size <= 0 ||
      info.size > MAX_BYTES ||
      (total += info.size) > 256 * 1024 * 1024
    )
      fail();
    contents.set(entry.name, await readFile(file));
  }
  if (
    expected.size ||
    !contents.has("character.obj") ||
    !contents.has("character.mtl")
  )
    fail();
  const obj = contents.get("character.obj").toString("utf8");
  const mtl = contents.get("character.mtl").toString("utf8");
  const materials = new Map();
  let current;
  for (const raw of mtl.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) continue;
    if (line.startsWith("newmtl ")) {
      current = line.slice(7);
      if (!current || materials.has(current)) fail();
      materials.set(current, null);
    } else if (line === "illum 1") {
      if (!current) fail();
    } else if (line.startsWith("map_Kd ")) {
      const texture = line.slice(7);
      if (
        !current ||
        materials.get(current) ||
        !contents.has(texture) ||
        !texture.endsWith(".png")
      )
        fail();
      materials.set(current, texture);
    } else fail();
  }
  if (!materials.size || [...materials.values()].some((value) => !value))
    fail();
  const groups = [];
  let group,
    material,
    libraries = 0,
    vertices = 0,
    normals = 0,
    uvs = 0,
    faces = 0;
  for (const raw of obj.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) continue;
    const space = line.indexOf(" "),
      kind = line.slice(0, space),
      value = line.slice(space + 1);
    if (["v", "vn", "vt"].includes(kind)) {
      const values = value.split(/\s+/).map(Number);
      if (
        values.length !== (kind === "vt" ? 2 : 3) ||
        values.some(
          (value) => !Number.isFinite(value) || Math.abs(value) > 100000,
        )
      )
        fail();
      if (kind === "v") vertices++;
      else if (kind === "vn") normals++;
      else uvs++;
    } else if (kind === "mtllib") {
      if (value !== "character.mtl" || ++libraries !== 1) fail();
    } else if (kind === "g") {
      group = { name: value, material: null, faces: 0 };
      groups.push(group);
      material = null;
    } else if (kind === "usemtl") {
      if (!group || !materials.has(value) || material) fail();
      group.material = material = value;
    } else if (kind === "f") {
      if (!group || !material) fail();
      const points = value.split(/\s+/);
      if (points.length !== 3) fail();
      for (const point of points) {
        if (!/^\d+\/\d+\/\d+$/.test(point)) fail();
        const [v, uv, n] = point.split("/").map(Number);
        if (v < 1 || v > vertices || uv < 1 || uv > uvs || n < 1 || n > normals)
          fail();
      }
      group.faces++;
      faces++;
    } else if (kind !== "o" && !(kind === "s" && value === "1")) fail();
  }
  if (
    libraries !== 1 ||
    !faces ||
    faces * 9 > 16000000 ||
    groups.length !== exported.materials.length
  )
    fail();
  for (let index = 0; index < groups.length; index++) {
    const hint = exported.materials[index],
      group = groups[index];
    if (
      hint.name !== group.name ||
      hint.material !== group.material ||
      hint.triangles !== group.faces ||
      ![0, 1, 2].includes(hint.blendingMode)
    )
      fail();
    if (
      !Number.isInteger(hint.textureFlags) ||
      hint.textureFlags < 0 ||
      hint.textureFlags > 4294967295
    )
      fail();
    if (
      !Number.isInteger(hint.flags) ||
      hint.flags < 0 ||
      hint.flags > 31 ||
      hint.flags & 8 ||
      typeof hint.textureAlpha !== "boolean"
    )
      fail();
    if (
      !Array.isArray(hint.color) ||
      hint.color.length !== 3 ||
      hint.color.some(
        (value) => !Number.isFinite(value) || value < 0 || value > 1,
      )
    )
      fail();
    if (
      !Number.isFinite(hint.opacity) ||
      hint.opacity < 0 ||
      hint.opacity > 1 ||
      (hint.opacity > 0 &&
        Boolean(hint.flags & 16) !== (hint.blendingMode === 2))
    )
      fail();
    if (
      !Array.isArray(hint.uvTransform) ||
      hint.uvTransform.length !== 6 ||
      hint.uvTransform.some(
        (value) => !Number.isFinite(value) || Math.abs(value) > 100000,
      )
    )
      fail();
    if (hint.uvSet === 1) {
      if (
        !(hint.alternateUV instanceof Float32Array) ||
        hint.alternateUV.length !== hint.triangles * 6
      )
        fail();
    } else if (hint.uvSet !== 0 || hint.alternateUV !== undefined) fail();
  }
  const textures = new Map();
  const texturePngs = new Map();
  const bitmaps = [];
  const disposable = new Set();
  let pixelTotal = 0;
  try {
    // Use Chromium's Blob brand, not Node's ESM-global Blob.
    const view = document.defaultView;
    for (const texture of new Set(materials.values())) {
      const bytes = contents.get(texture);
      const { width, height } = pngSize(bytes);
      pixelTotal += width * height;
      if (pixelTotal > MAX_PIXELS) fail();
      const bitmap = await view.createImageBitmap(
        new BrowserBlob([bytes], { type: "image/png" }),
        { premultiplyAlpha: "none", colorSpaceConversion: "none" },
      );
      if (bitmap.width !== width || bitmap.height !== height) fail();
      bitmaps.push(bitmap);
      // Canvas guarantees GLTFExporter encodes RGB/RGBA PNG, with no original ancillary metadata.
      const canvas = document.createElement("canvas");
      canvas.width = width;
      canvas.height = height;
      const context = canvas.getContext("2d");
      if (!context) fail();
      context.drawImage(bitmap, 0, 0);
      const map = new THREE.CanvasTexture(canvas);
      map.colorSpace = THREE.SRGBColorSpace;
      map.name = "";
      map.userData = {};
      textures.set(texture, map);
      disposable.add(map);
      texturePngs.set(map, bytes);
    }
    const creator = new MTLLoader().parse(mtl, "");
    // No TextureLoader URLs: all textures are decoded from the validated local closure above.
    creator.loadTexture = (url) => {
      const texture = textures.get(url);
      if (!texture) fail();
      return texture;
    };
    creator.preload();
    for (const material of Object.values(creator.materials))
      disposable.add(material);
    const parsed = new OBJLoader().setMaterials(creator).parse(obj);
    const scene = new THREE.Scene();
    const nonempty = exported.materials.filter((hint) => hint.triangles > 0);
    const samplerTextures = new Map();
    const opaqueImages = new Map();
    let meshIndex = 0;
    parsed.traverse((object) => {
      if (object === parsed) return;
      if (!object.isMesh) fail();
      const hint = nonempty[meshIndex++];
      if (
        !hint ||
        object.name !== hint.name ||
        Array.isArray(object.material) ||
        !object.material.map
      )
        fail();
      const geometry = object.geometry;
      disposable.add(geometry);
      for (const attribute of Object.keys(geometry.attributes))
        if (!["position", "normal", "uv"].includes(attribute)) fail();
      if (
        !geometry.attributes.position ||
        !geometry.attributes.normal ||
        !geometry.attributes.uv ||
        geometry.attributes.position.count !== hint.triangles * 3
      )
        fail();
      for (const attribute of Object.values(geometry.attributes))
        for (const value of attribute.array)
          if (!Number.isFinite(value)) fail();
      const uv = geometry.attributes.uv;
      const [a, b, tx, c, d, ty] = hint.uvTransform;
      for (let vertex = 0; vertex < uv.count; vertex++) {
        const u =
          hint.uvSet === 1 ? hint.alternateUV[vertex * 2] : uv.getX(vertex);
        const v =
          hint.uvSet === 1
            ? hint.alternateUV[vertex * 2 + 1]
            : 1 - uv.getY(vertex);
        // OBJ flips V on output; apply the sampled M2 transform in source coordinates, then flip once.
        const transformedU = a * u + b * v + tx,
          transformedV = 1 - (c * u + d * v + ty);
        if (
          !Number.isFinite(transformedU) ||
          !Number.isFinite(transformedV) ||
          Math.abs(transformedU) > 100000 ||
          Math.abs(transformedV) > 100000
        )
          fail();
        uv.setXY(vertex, transformedU, transformedV);
      }
      const sourceMap = object.material.map;
      let samplers = samplerTextures.get(sourceMap);
      if (!samplers) samplerTextures.set(sourceMap, (samplers = new Map()));
      const flags = hint.textureFlags & 3;
      const samplerKey = flags | (hint.textureAlpha ? 0 : 4);
      let map = samplers.get(samplerKey);
      if (!map) {
        if (hint.textureAlpha) {
          map = sourceMap.clone();
        } else {
          // Combiners_Opaque ignores source alpha even in blend mode; do not invent texture transparency.
          let image = opaqueImages.get(sourceMap);
          if (!image) {
            image = opaquePngCanvas(texturePngs.get(sourceMap));
            opaqueImages.set(sourceMap, image);
          }
          map = new THREE.CanvasTexture(image);
          map.colorSpace = sourceMap.colorSpace;
          map.flipY = sourceMap.flipY;
        }
        map.wrapS =
          flags & 1 ? THREE.RepeatWrapping : THREE.ClampToEdgeWrapping;
        map.wrapT =
          flags & 2 ? THREE.RepeatWrapping : THREE.ClampToEdgeWrapping;
        samplers.set(samplerKey, map);
        disposable.add(map);
      }
      const unlit = Boolean(hint.flags & 1);
      const Material = unlit
        ? THREE.MeshBasicMaterial
        : THREE.MeshStandardMaterial;
      const material = new Material({
        map,
        color: new THREE.Color().setRGB(
          ...hint.color,
          THREE.LinearSRGBColorSpace,
        ),
        opacity: hint.opacity,
        ...(unlit ? {} : { metalness: 0, roughness: 1 }),
        side: hint.flags & 4 ? THREE.DoubleSide : THREE.FrontSide,
        transparent: hint.blendingMode === 2 && hint.opacity > 0,
        // Upstream alpha-key tests texture alpha alone; glTF tests texture alpha multiplied by opacity.
        alphaTest:
          hint.opacity === 0
            ? 1
            : hint.blendingMode === 1 && hint.textureAlpha
              ? 0.501960814 * hint.opacity
              : 0,
      });
      disposable.add(material);
      const mesh = new THREE.Mesh(geometry, material);
      mesh.name = "";
      mesh.userData = {};
      geometry.name = "";
      geometry.userData = {};
      scene.add(mesh);
    });
    if (meshIndex !== nonempty.length || !meshIndex) fail();
    // The pinned M2 loader already converts vertices and normals to Y-up;
    // OBJWriter preserves those coordinates, including the baked pose.
    const result = await new GLTFExporter().parseAsync(scene, {
      binary: true,
      onlyVisible: true,
      animations: [],
      includeCustomExtensions: false,
    });
    return sanitizeGlb(result);
  } finally {
    for (const resource of disposable) resource.dispose();
    for (const bitmap of bitmaps) bitmap.close();
  }
}
