import { rendererMetadata } from "./metadata-lock.mjs";

// CASC v2 stores one current generation per bucket, not a union of old journals.
// References: wowdev CASC / .IDX Journals; CascLib LoadLocalIndexFiles/NewSubIndex.
export function selectLocalIndexFiles(entries) {
  const latest = new Map();
  for (const entry of entries) {
    if (!entry.isFile() || !entry.name.endsWith(".idx")) continue;
    const match = /^([0-9a-f]{2})([0-9a-f]{8})\.idx$/i.exec(entry.name);
    if (!match || Number.parseInt(match[1], 16) > 15)
      throw new Error("missing-game-data");
    const bucket = Number.parseInt(match[1], 16);
    const generation = Number.parseInt(match[2], 16);
    const previous = latest.get(bucket);
    if (
      previous &&
      generation === previous.generation &&
      entry.name !== previous.name
    )
      throw new Error("missing-game-data");
    if (!previous || generation > previous.generation)
      latest.set(bucket, { name: entry.name, generation });
  }
  return Array.from(latest.values(), (entry) => entry.name);
}

// All patches apply only to the SHA-256-pinned 0.2.19 distribution, never to a research copy.
export function patchExporter(source, adapter, bootstrap) {
  // Required PK tables stay demand-only; only these two relation tables admit readable sections.
  const requiredDBFiles = [
    "DBFilesClient/CreatureDisplayInfo.db2",
    "DBFilesClient/CreatureModelData.db2",
    "DBFilesClient/Item.db2",
    "DBFilesClient/ItemSparse.db2",
    "DBFilesClient/ItemAppearance.db2",
    "DBFilesClient/ItemDisplayInfo.db2",
  ];
  const bestEffortDBFiles = [
    "DBFilesClient/ItemModifiedAppearance.db2",
    "DBFilesClient/ItemDisplayInfoModelMatRes.db2",
  ];
  const readableDBFiles = JSON.stringify([
    ...requiredDBFiles,
    ...bestEffortDBFiles,
  ]);
  const once = (text, from, to, count = 1) => {
    if (text.split(from).length - 1 !== count)
      throw new Error("wow.export patch anchor drift: " + from.slice(0, 90));
    return text.replaceAll(from, to);
  };
  const scope = (name, transform) => {
    const start = source.indexOf("var require_" + name + " =");
    const end = source.indexOf("\n// ", start);
    if (start < 0 || end < 0)
      throw new Error("wow.export module anchor drift: " + name);
    source =
      source.slice(0, start) +
      transform(source.slice(start, end)) +
      source.slice(end);
  };
  const dbdManifestEntries = rendererMetadata.dbd.entries.map(
    ([table, fileDataID]) => [table, fileDataID],
  );
  scope(
    "dbd_manifest",
    () => `var require_dbd_manifest = __commonJS((exports2, module2) => {
  /*!
   * wow.export (https://github.com/Kruithne/wow.export)
   * Authors: Kruithne <kruithne@gmail.com>
   * License: MIT
   */
  const entries = ${JSON.stringify(dbdManifestEntries)};
  const table_to_id = new Map(entries);
  const id_to_table = new Map(entries.map(([table, id]) => [id, table]));
  function preload() {}
  async function prepareManifest() {
    return true;
  }
  function getByID(id) {
    return id_to_table.get(id);
  }
  function getByTableName(table_name) {
    return table_to_id.get(table_name);
  }
  function getAllTableNames() {
    return Array.from(table_to_id.keys()).sort();
  }
  module2.exports = { preload, prepareManifest, getByID, getByTableName, getAllTableNames };
});`,
  );
  scope("listfile", (text) => {
    const binaryStart = text.indexOf(
      "  var listfile_preload_binary = async () => {",
    );
    const binaryEnd = text.indexOf(
      "\n  var listfile_binary_find_component_child =",
      binaryStart,
    );
    if (binaryStart < 0 || binaryEnd < 0)
      throw new Error("wow.export binary listfile anchor drift");
    text =
      text.slice(0, binaryStart) +
      "  var listfile_preload_binary = async () => false;" +
      text.slice(binaryEnd);
    const remoteLoad = [
      '      if (url.startsWith("http")) {',
      "        let cached = null;",
      "        let last_modified = 0;",
      "        try {",
      "          cached = await BufferWrapper.readFile(cache_file);",
      "          last_modified = (await fsp.stat(cache_file)).mtime.getTime();",
      "        } catch (e) {}",
      "        if (listfile_check_cache_expiry(last_modified)) {",
      "          try {",
      "            let fallback_url = String(core.view.config.listfileFallbackURL);",
      '            fallback_url = fallback_url.replace("%s", "");',
      "            data = await generics.downloadFile([url, fallback_url]);",
      "            await fsp.writeFile(cache_file, data.raw);",
      "          } catch (e) {",
      "            if (cached === null) {",
      '              log.write("Failed to download listfile during preload, no cached version for fallback: %s", e.message);',
      "              return false;",
      "            }",
      '            log.write("Failed to download listfile during preload, using cached version: %s", e.message);',
      "            data = cached;",
      "          }",
      "        } else {",
      "          data = cached;",
      "        }",
      "      } else {",
      '        log.write("Preloading user-defined local listfile: %s", url);',
      "        data = await BufferWrapper.readFile(url);",
      "      }",
    ].join("\n");
    return once(
      text,
      remoteLoad,
      [
        '      if (!path.isAbsolute(url)) throw new Error("Remote listfiles are disabled");',
        '      log.write("Preloading bundled listfile: %s", url);',
        "      data = await BufferWrapper.readFile(url);",
      ].join("\n"),
    );
  });
  scope("tact_keys", (text) => {
    text = once(
      text,
      '      const tactKeys = JSON.parse(await fsp.readFile(constants.CACHE.TACT_KEYS, "utf8"));',
      '      const tactKeys = JSON.parse(await fsp.readFile(require("path").join(require("path").dirname(process.execPath), "src", "aurify-tact-keys.json"), "utf8"));',
    );
    const remoteLoad = [
      "    const tact_url = core.view.config.tactKeysURL;",
      "    const tact_url_fallback = core.view.config.tactKeysFallbackURL;",
      "    const res = await generics.get([tact_url, tact_url_fallback]);",
      "    if (!res.ok)",
      "      throw new Error(`Unable to update tactKeys, HTTP ${res.status}`);",
      "    const data = await res.text();",
      "    const lines = data.split(/\\r?\\n/);",
      "    let remoteAdded = 0;",
      "    for (const line of lines) {",
      '      const parts = line.split(" ");',
      "      if (parts.length !== 2)",
      "        continue;",
      "      const keyName = parts[0].trim();",
      "      const key = parts[1].trim();",
      "      if (validateKeyPair(keyName, key)) {",
      "        KEY_RING[keyName.toLowerCase()] = key.toLowerCase();",
      "        remoteAdded++;",
      "      } else {",
      '        log.write("Skipping invalid remote tact key: %s -> %s", keyName, key);',
      "      }",
      "    }",
      "    if (remoteAdded > 0)",
      '      log.write("Added %d tact keys from %s", remoteAdded, tact_url);',
    ].join("\n");
    return once(text, remoteLoad, "");
  });
  scope("DBCReader", (text) => {
    text = once(
      text,
      [
        "      const cache = core.view.casc?.cache;",
        "      const cache_key = dbd_name.toLowerCase();",
        "      let raw_dbd = cache ? await cache.getFile(cache_key, constants.CACHE.DIR_DBD) : null;",
        "      if (raw_dbd !== null)",
        "        structure = new DBDParser(raw_dbd).getStructure(this.build_id, null);",
      ].join("\n"),
      [
        "      let raw_dbd;",
        "      try {",
        '        raw_dbd = await BufferWrapper.readFile(path.join(path.dirname(process.execPath), "src", "aurify-dbd", dbd_name));',
        "      } catch {",
        '        throw new Error("missing-game-data");',
        "      }",
        "      structure = new DBDParser(raw_dbd).getStructure(this.build_id, null);",
      ].join("\n"),
    );
    const remoteSchema = [
      "      if (structure === null) {",
      "        const dbd_url = util.format(core.view.config.dbdURL, table_name);",
      "        const dbd_url_fallback = util.format(core.view.config.dbdFallbackURL, table_name);",
      "        try {",
      "          log.write(`No cached DBD or no matching structure, downloading from ${dbd_url}`);",
      "          raw_dbd = await generics.downloadFile([dbd_url, dbd_url_fallback]);",
      "          if (cache)",
      "            await cache.storeFile(cache_key, raw_dbd, constants.CACHE.DIR_DBD);",
      "          structure = new DBDParser(raw_dbd).getStructure(this.build_id, null);",
      "        } catch (e) {",
      '          log.write("Failed to download DBD for %s: %s", table_name, e.message);',
      "        }",
      "      }",
      "      if (structure === null) {",
      '        log.write("No table definition available for %s, using raw field names", table_name);',
      "        this._build_fallback_schema();",
      "        return;",
      "      }",
    ].join("\n");
    return once(
      text,
      remoteSchema,
      '      if (structure === null) throw new Error("missing-game-data");',
    );
  });
  source = once(
    source,
    '      { product: "wow_beta", title: "Beta: World of Warcraft", tag: "Beta" },',
    '      { product: "wow_beta", title: "Beta: World of Warcraft", tag: "Beta" },\n      { product: "wowf", title: "World of Warcraft Forever", tag: "Forever" },',
  );
  // Proven correction: posed OBJ geometry is in character space, not preview model/world space.
  scope("M2RendererGL", (text) => {
    text = once(
      text,
      "getAttachmentTransform(attachmentId) {",
      "getAttachmentTransform(attachmentId, includeModelTransform = true) {",
    );
    text = once(
      text,
      "      mat4_multiply(result, this.model_matrix, result);",
      "      if (includeModelTransform)\n        mat4_multiply(result, this.model_matrix, result);",
    );
    // An unmatched weighted collection bone must not silently borrow the same numeric character index.
    text = once(
      text,
      "        remap[i] = found >= 0 ? found : i;",
      "        remap[i] = found;",
    );
    text = once(
      text,
      "        const char_idx = this.bone_remap_table[i];",
      "        const char_idx = this.bone_remap_table[i];\n        if (char_idx < 0) continue;",
    );
    // Independently implemented math, not copied implementation:
    // wowmodelviewer/wowmodelviewer@678071f0f9156fa0daa5e6bd0ef1426c2255cb01:docs/fbx-customization.md
    // q=A*p; correctedLocal=T(q)*C*T(-q)*A, then correctedParent*correctedLocal.
    // This is a documented independent-renderer agreement, not a proprietary-client capture.
    text = once(
      text,
      "        const offset = idx * 16;\n        if (parent_idx >= 0 && parent_idx < bone_count) {",
      [
        "        const correction = this.aurifyBoneCorrections?.get(idx);",
        "        if (correction) {",
        "          const qx = local_mat[0] * px + local_mat[4] * py + local_mat[8] * pz + local_mat[12];",
        "          const qy = local_mat[1] * px + local_mat[5] * py + local_mat[9] * pz + local_mat[13];",
        "          const qz = local_mat[2] * px + local_mat[6] * py + local_mat[10] * pz + local_mat[14];",
        "          mat4_from_translation(trans_mat, -qx, -qy, -qz);",
        "          mat4_multiply(temp_result, trans_mat, local_mat);",
        "          mat4_multiply(local_mat, correction, temp_result);",
        "          mat4_from_translation(trans_mat, qx, qy, qz);",
        "          mat4_multiply(temp_result, trans_mat, local_mat);",
        "          mat4_copy(local_mat, temp_result);",
        "        }",
        "        const offset = idx * 16;",
        "        if (parent_idx >= 0 && parent_idx < bone_count) {",
      ].join("\n"),
    );
    text = once(
      text,
      "        calculated[idx] = 1;",
      [
        "        if (this.aurifyBoneCorrections) {",
        "          for (let component = offset; component < offset + 16; component++)",
        '            if (!Number.isFinite(this.bone_matrices[component])) throw new Error("invalid-appearance");',
        "        }",
        "        calculated[idx] = 1;",
      ].join("\n"),
    );
    text = once(text, "catch (e) {", "catch (e) { throw e;", 3);
    return once(
      text,
      [
        "          try {",
        "            const data = await texture.getTextureFile();",
        "            const blp = new BLPFile(data);",
        "            const gl_tex = new GLTexture(this.ctx);",
        "            gl_tex.set_blp(blp, { flags: texture.flags });",
        "            this.textures.set(i, gl_tex);",
        "            if (ribbonSlot !== null)",
        "              textureRibbon.setSlotSrc(ribbonSlot, blp.getDataURL(7), this.syncID);",
        "          } catch (e) { throw e;",
        '            log.write("Failed to load texture %d: %s", texture.fileDataID, e.message);',
        "          }",
      ].join("\n"),
      [
        "          let blp, gl_tex;",
        "          try {",
        "            const data = await texture.getTextureFile();",
        "            blp = new BLPFile(data);",
        "            gl_tex = new GLTexture(this.ctx);",
        "            gl_tex.set_blp(blp, { flags: texture.flags });",
        "          } catch (e) {",
        "            gl_tex?.dispose();",
        "            this.textures.get(i)?.dispose();",
        "            this.textures.delete(i);",
        '            log.write("Failed to load texture %d: %s", texture.fileDataID, e.message);',
        "            continue;",
        "          }",
        "          this.textures.set(i, gl_tex);",
        "          if (ribbonSlot !== null)",
        "            textureRibbon.setSlotSrc(ribbonSlot, blp.getDataURL(7), this.syncID);",
      ].join("\n"),
    );
  });
  scope("CharacterExporter", (text) => {
    text = once(
      text,
      "getAttachmentTransform?.(attachment_id);",
      "getAttachmentTransform?.(attachment_id, false);",
    );
    text = once(
      text,
      "        } else {\n          vertices = new Float32Array(m2.vertices);\n          normals = new Float32Array(m2.normals);\n        }\n        if (renderer.bone_remap_table",
      '        } else {\n          throw new Error("unsupported-equipment");\n        }\n        if (renderer.bone_remap_table',
    );
    text = once(
      text,
      "        } else {\n          vertices = new Float32Array(m2.vertices);\n          normals = new Float32Array(m2.normals);\n        }\n      } else {",
      '        } else {\n          throw new Error("unsupported-equipment");\n        }\n      } else {',
    );
    return text;
  });
  scope("M2Exporter", (text) => {
    text = once(text, "catch (e) {", "catch (e) { throw e;", 4);
    text = once(
      text,
      "    async exportTextures(out, raw = false, mtl = null, helper, fullTexPaths = false, glbMode = false) {",
      "    async exportTextures(out, raw = false, mtl = null, helper, fullTexPaths = false, glbMode = false, requiredTextureIndices = null) {",
    );
    text = once(
      text,
      "      await this.m2.load();\n      const useAlpha = config.modelsExportAlpha;",
      "      await this.m2.load();\n      const requiredTextureTypes = requiredTextureIndices && new Set(Array.from(requiredTextureIndices, index => this.m2.textureTypes[index]));\n      const useAlpha = config.modelsExportAlpha;",
    );
    text = once(
      text,
      "      for (const [textureName, dataTexture] of this.dataTextures) {",
      "      for (const [textureName, dataTexture] of this.dataTextures) {\n        if (requiredTextureTypes && !requiredTextureTypes.has(textureName)) continue;",
    );
    text = once(
      text,
      "      for (const texture of this.m2.textures) {",
      "      for (const texture of this.m2.textures) {\n        if (requiredTextureIndices && !requiredTextureIndices.has(textureIndex)) {\n          textureIndex++;\n          continue;\n        }",
    );
    text = once(
      text,
      "      if (glbMode)\n        return { validTextures, texture_buffers, files_to_cleanup };",
      '      if (requiredTextureIndices) {\n        for (const index of requiredTextureIndices) {\n          const type = this.m2.textureTypes[index];\n          const key = this.dataTextures.has(type) ? "data-" + type : this.m2.textures[index].fileDataID;\n          if (!validTextures.get(key)?.matName) throw new Error("incomplete-materials");\n        }\n      }\n      if (glbMode)\n        return { validTextures, texture_buffers, files_to_cleanup };',
    );
    text = once(
      text,
      "      const validTextures = await this.exportTextures(outDir, false, mtl, helper);",
      '      const requiredTextureIndices = this.aurifyRequiredTextureIndices;\n      if (!(requiredTextureIndices instanceof Set) || !requiredTextureIndices.size || !config.modelsExportTextures) throw new Error("incomplete-materials");\n      for (const index of requiredTextureIndices)\n        if (!Number.isInteger(index) || index < 0 || index >= this.m2.textures.length) throw new Error("incomplete-materials");\n      const validTextures = await this.exportTextures(outDir, false, mtl, helper, false, false, requiredTextureIndices);',
    );
    const objStart = text.indexOf("    async exportAsOBJ(");
    const objEnd = text.indexOf("    async exportAsSTL(", objStart);
    if (objStart < 0 || objEnd < 0)
      throw new Error("Character OBJ method anchor drift");
    const objMethod = once(
      text.slice(objStart, objEnd),
      "        if (texUnit)\n          texture = this.m2.textures[this.m2.textureCombos[texUnit.textureComboIndex]];",
      '        if (texUnit) {\n          const textureIndex = this.m2.textureCombos[texUnit.textureComboIndex];\n          if (!requiredTextureIndices.has(textureIndex)) throw new Error("incomplete-materials");\n          texture = this.m2.textures[textureIndex];\n        }',
    );
    text = text.slice(0, objStart) + objMethod + text.slice(objEnd);
    const start = text.indexOf("    async _exportEquipmentToOBJ(");
    const end = text.indexOf("    async _exportEquipmentToSTL(", start);
    if (start < 0 || end < 0)
      throw new Error("Equipment OBJ method anchor drift");
    let method = once(
      text.slice(start, end),
      [
        "          if (textureType >= 11 && textureType < 14) {",
        "            const texInfo = equipTextures.get(textureType - 11);",
        "            if (texInfo)",
        "              matName = texInfo.matName;",
        "          } else if (textureType > 1 && textureType < 5) {",
        "            const texInfo = equipTextures.get(textureType - 2);",
        "            if (texInfo)",
        "              matName = texInfo.matName;",
        "          } else if (texture?.fileDataID > 0 && validTextures.has(texture.fileDataID)) {",
        "            matName = validTextures.get(texture.fileDataID).matName;",
        "          }",
      ].join("\n"),
      [
        "          if (textureType !== 0) {",
        '            if (!equip.aurifyTextures?.has(textureType)) throw new Error("incomplete-materials");',
        "            matName = validTextures.get(equip.aurifyTextures.get(textureType))?.matName;",
        "          } else if (texture?.fileDataID > 0) {",
        "            matName = validTextures.get(texture.fileDataID)?.matName;",
        "          }",
      ].join("\n"),
    );
    method = once(
      method,
      "      if (!renderer?.m2)\n        return;",
      '      if (!renderer?.m2) throw new Error("incomplete-materials");',
    );
    method = once(
      method,
      "      if (!skin)\n        return;",
      '      if (!skin) throw new Error("incomplete-materials");\n      const requiredTextureIndices = equip.aurifyRequiredTextureIndices;\n      if (!(requiredTextureIndices instanceof Set) || !requiredTextureIndices.size || !config.modelsExportTextures) throw new Error("incomplete-materials");\n      for (const index of requiredTextureIndices)\n        if (!Number.isInteger(index) || index < 0 || index >= m2.textures.length) throw new Error("incomplete-materials");',
    );
    method = once(
      method,
      [
        "      const equipTextures = new Map;",
        "      if (config.modelsExportTextures && textures) {",
        "        for (let i = 0;i < textures.length; i++) {",
        "          const texFileDataID = textures[i];",
        "          if (!texFileDataID || texFileDataID <= 0)",
        "            continue;",
        "          if (validTextures.has(texFileDataID)) {",
        "            equipTextures.set(i, validTextures.get(texFileDataID));",
        "            continue;",
        "          }",
      ].join("\n"),
      [
        "      {",
        "        for (const i of requiredTextureIndices) {",
        "          const textureType = m2.textureTypes[i];",
        "          const texFileDataID = textureType === 0 ? m2.textures[i].fileDataID : equip.aurifyTextures?.get(textureType);",
        '          if (!texFileDataID || texFileDataID <= 0) throw new Error("incomplete-materials");',
        "          if (validTextures.has(texFileDataID)) {",
        '            if (!validTextures.get(texFileDataID)?.matName) throw new Error("incomplete-materials");',
        "            continue;",
        "          }",
      ].join("\n"),
    );
    method = once(method, "            equipTextures.set(i, texInfo);\n", "");
    method = once(
      method,
      "          const textureIdx = m2.textureCombos[texUnit.textureComboIndex];",
      '          const textureIdx = m2.textureCombos[texUnit.textureComboIndex];\n          if (!requiredTextureIndices.has(textureIdx)) throw new Error("incomplete-materials");',
    );
    text = text.slice(0, start) + method + text.slice(end);
    text = once(
      text,
      "        obj.addMesh(meshName, verts, matName);",
      '        if (!matName) throw new Error("incomplete-materials");\n        this.aurifyMeshMaterials.push({ name: meshName, material: matName, ...this.aurifyMaterialHint(renderer, mI, verts), triangles: verts.length / 3 });\n        obj.addMesh(meshName, verts, matName);',
    );
    return once(
      text,
      "        obj.addMesh(GeosetMapper.getGeosetName(mI, mesh.submeshID), verts, matName);",
      '        if (!matName) throw new Error("incomplete-materials");\n        this.aurifyMeshMaterials.push({ name: GeosetMapper.getGeosetName(mI, mesh.submeshID), material: matName, ...this.aurifyMaterialHint(null, mI, verts), triangles: verts.length / 3 });\n        obj.addMesh(GeosetMapper.getGeosetName(mI, mesh.submeshID), verts, matName);',
    );
  });
  scope("character_appearance", (text) => {
    for (const condition of [
      "chr_model_texture_layer === undefined",
      "chr_model_material === undefined",
      "char_component_texture_section === undefined",
    ]) {
      // Scoped first occurrence belongs to apply_customization_textures; later helpers legitimately inspect unrelated types.
      const anchor = `        if (${condition})\n          continue;`;
      if (!text.includes(anchor))
        throw new Error("Customization patch anchor drift");
      text = text.replace(
        anchor,
        `        if (${condition})\n          throw new Error("incomplete-materials");`,
      );
    }
    return text;
  });
  scope("tab_characters", (text) => {
    text = once(text, "catch (e) {", "catch (e) { throw e;", 14);
    text = once(
      text,
      "    for (const [sex, model_id] of models_for_race) {\n      if (model_id === model_selection.id)\n        return { raceID: race_id, genderIndex: sex };\n    }\n    return null;",
      '    const body_type = core.view.aurifyBodyType;\n    if ((body_type !== 0 && body_type !== 1) || models_for_race.get(body_type) !== model_selection.id) throw new Error("invalid-appearance");\n    return { raceID: race_id, genderIndex: body_type };',
    );
    // A texture-bearing item must never disappear because a layout/section/layer is unknown.
    for (const condition of ["!section", "!layer", "!chr_model_material"]) {
      const anchor = `            if (${condition})\n              continue;`;
      text = once(
        text,
        anchor,
        `            if (${condition})\n              throw new Error("incomplete-materials");`,
        2,
      );
    }
    return once(
      text,
      "  module2.exports = {\n    get_default_characters_dir,",
      adapter +
        "\n  module2.exports = {\n    aurify: Object.freeze({ catalog: aurify_catalog, render: aurify_render }),\n    get_default_characters_dir,",
    );
  });
  scope("DBItemCharTextures", (text) =>
    once(
      text,
      "      if (file_data_ids && file_data_ids.length > 0) {",
      '      if (!file_data_ids?.length) throw new Error("incomplete-materials");\n      if (file_data_ids && file_data_ids.length > 0) {',
    ),
  );
  scope("DBComponentModelFileData", (text) => {
    text = once(
      text,
      "    return file_data_ids[0];\n  };",
      "    return null;\n  };",
    );
    return once(
      text,
      "      return candidates.length > 0 ? candidates[0].fdid : null;",
      "      return null;",
    );
  });
  scope("DBComponentTextureFileData", (text) => {
    text = once(
      text,
      "    if (!candidates.some((c) => c.info))\n      return file_data_ids[0];",
      '    if (!candidates.some((c) => c.info))\n      throw new Error("incomplete-materials");',
    );
    return once(
      text,
      "      return gender_ok && class_ok;",
      "      return gender_ok && class_ok && (c.info.raceID === 0 || c.info.raceID === race_id);",
    );
  });
  // The adapter declares exact item -> appearance -> display PK sets first.
  // The two best-effort relations expose readable rows; all selected PK targets remain required.
  for (const [name, tables] of [
    ["DBItems", ["Item", "ItemSparse"]],
    ["DBItemCharTextures", ["ItemAppearance"]],
    ["DBItemGeosets", ["ItemAppearance", "ItemDisplayInfo"]],
    ["DBItemModels", ["ItemAppearance", "ItemDisplayInfo"]],
  ]) {
    scope(name, (text) => {
      for (const table of tables)
        text = once(
          text,
          `db2.${table}.getAllRows()`,
          `db2.${table}.getRequiredRows()`,
        );
      return text;
    });
  }
  scope("DBCreatures", (text) =>
    once(
      text,
      "  module2.exports = {\n    initializeCreatureData,",
      `
  var aurify_required_player_rows = false;
  var setRequiredPlayerRows = (displays, models) => {
    if (isInitialized || !(displays instanceof Map) || !(models instanceof Map) || !displays.size || !models.size)
      throw new Error("missing-game-data");
    for (const [id, row] of models) {
      if (row.ID !== id || !Number.isInteger(row.FileDataID) || row.FileDataID <= 0 || row.FileDataID > 2147483647)
        throw new Error("missing-game-data");
      modelDataIDToFileDataID.set(id, row.FileDataID);
    }
    for (const [id, row] of displays) {
      const fdid = modelDataIDToFileDataID.get(row.ModelID);
      if (row.ID !== id || !fdid) throw new Error("missing-game-data");
      displayIDToFileDataID.set(id, fdid);
    }
    aurify_required_player_rows = true;
    isInitialized = true;
  };
  module2.exports = {
    setRequiredPlayerRows,
    hasRequiredPlayerRows: () => aurify_required_player_rows,
    initializeCreatureData,`,
    ),
  );
  scope("DBCharacterCustomization", (text) =>
    once(
      text,
      "    await DBCreatures.initializeCreatureData();",
      '    if (!DBCreatures.hasRequiredPlayerRows()) throw new Error("missing-game-data");',
    ),
  );
  scope("db2", (text) =>
    once(
      text,
      "          return async function(...args) {\n            if (!reader_target.isLoaded) {",
      '          return async function(...args) {\n            if (prop === "getRequiredRows") reader_target.aurifyRequireRows(args[0] ?? reader_target.aurifyRequiredIDs);\n            if (!reader_target.isLoaded) {',
    ),
  );
  scope("file_writer", (text) =>
    once(
      text,
      "    close() {\n      this.stream.end();\n    }",
      '    close() {\n      return new Promise((resolve, reject) => {\n        this.stream.once("error", reject);\n        this.stream.end(resolve);\n      });\n    }',
    ),
  );
  for (const name of ["OBJWriter", "MTLWriter"])
    scope(name, (text) =>
      once(text, "      writer.close();", "      await writer.close();"),
    );
  scope("casc_source_local", (text) => {
    // Load only the newest journal per bucket. Never resurrect retired offsets
    // from an older generation if the selected file or its data cannot be read.
    text = once(
      text,
      '      for (const entry of entries) {\n        if (entry.isFile() && entry.name.endsWith(".idx")) {\n          await this.parseIndex(path.join(this.storageDir, entry.name));\n          indexCount++;\n        }\n      }',
      `      for (const name of (${selectLocalIndexFiles.toString()})(entries)) {
        await this.parseIndex(path.join(this.storageDir, name));
        indexCount++;
      }`,
    );
    // Local indexes, encoding and root consume BuildKey, never CDN archive config.
    // Remote fallback stays disabled; do not require metadata for that unused path.
    text = once(
      text,
      "      this.cdnConfig = await this.getConfigFileWithRemoteFallback(this.build.CDNKey);\n",
      "",
    );
    text = once(
      text,
      '      log.write("CDNConfig: %o", this.cdnConfig);\n',
      "",
    );
    text = once(
      text,
      'if (await generics.fileExists("fakebuildconfig")) {',
      "if (false) {",
    );
    text = once(
      text,
      "    async getFile(fileDataID, partialDecryption = false, suppressLog = false, supportFallback = true, forceFallback = false, contentKey = null) {",
      `
    async getRequiredDBFile(fileName) {
      if (!${readableDBFiles}.includes(fileName))
        throw new Error("missing-game-data");
      const table = fileName.slice(14, -4);
      const fileDataID = require_dbd_manifest().getByTableName(table);
      if (!Number.isInteger(fileDataID) || fileDataID <= 0) throw new Error("missing-game-data");
      const encodingKey = await super.getFile(fileDataID);
      const data = await this.getDataFile(encodingKey);
      // In-memory partial reads never enter the ordinary complete-file cache.
      const reader = new BLTEReader(data, encodingKey, { aurifyRequiredDB: fileName });
      reader.processAllBlocks();
      reader.seek(0);
      return reader;
    }
    async getFile(fileDataID, partialDecryption = false, suppressLog = false, supportFallback = true, forceFallback = false, contentKey = null) {`,
    );
    return once(
      text,
      "      const blte = new BLTEReader(data, encodingKey, true);",
      "      const blte = new BLTEReader(data, encodingKey, false);",
    );
  });
  // Ordinary readers stay strict. Only the eight allowlisted tables track unreadable byte ranges.
  scope("blte_reader", (text) => {
    text = once(
      text,
      "      this.partialDecrypt = partialDecrypt;",
      `      this.partialDecrypt = false;\n      this.aurifyRequiredDB = partialDecrypt && typeof partialDecrypt === "object" && ${readableDBFiles}.includes(partialDecrypt.aurifyRequiredDB);\n      this.aurifyMissingRanges = [];`,
    );
    text = once(
      text,
      "      this.blocks = metadata.blocks;\n      this._buf = Buffer.alloc(metadata.totalSize);",
      '      if (this.aurifyRequiredDB && (metadata.totalSize > 268435456 || metadata.blocks.length > 65536)) throw new Error("missing-game-data");\n      this.blocks = metadata.blocks;\n      this._buf = Buffer.alloc(metadata.totalSize);',
    );
    text = once(
      text,
      "      this._handleBlock(this._blte, bltePos + block.CompSize, this.blockIndex);",
      '      this._handleBlock(this._blte, bltePos + block.CompSize, this.blockIndex);\n      if (this.aurifyRequiredDB && this.offset !== this.blockWriteIndex + block.DecompSize) throw new Error("missing-game-data");',
    );
    text = once(
      text,
      "      const expectedSize = this.blocks[index].DecompSize;",
      '      const expectedSize = this.blocks[index].DecompSize;\n      if (this.aurifyRequiredDB && decomp.byteLength !== expectedSize) throw new Error("missing-game-data");',
    );
    text = once(
      text,
      "    processAllBlocks() {",
      `
    aurifyHasRange(offset, length) {
      if (!Number.isSafeInteger(offset) || !Number.isSafeInteger(length) || offset < 0 || length < 0 || offset + length > this.byteLength)
        throw new Error("missing-game-data");
      return !this.aurifyMissingRanges.some(([start, end]) => offset < end && offset + length > start);
    }
    aurifyAssertRange(offset, length) {
      if (!this.aurifyHasRange(offset, length)) throw new Error("missing-game-data");
    }
    processAllBlocks() {`,
    );
    return once(
      text,
      [
        "            if (e instanceof EncryptionError) {",
        "              if (this.partialDecrypt)",
        "                this._ofs += this.blocks[index].DecompSize;",
        "              else",
        "                throw e;",
        "            }",
      ].join("\n"),
      [
        "            if (e instanceof EncryptionError && this.aurifyRequiredDB) {",
        "              const start = this.blockWriteIndex, end = start + this.blocks[index].DecompSize;",
        "              this.aurifyMissingRanges.push([start, end]);",
        "              this._ofs = end;",
        "            } else {",
        '              throw new Error("missing-game-data");',
        "            }",
      ].join("\n"),
    );
  });
  scope("blte_stream_reader", (text) => {
    text = once(
      text,
      "      this.partialDecrypt = partialDecrypt;",
      "      this.partialDecrypt = false;",
    );
    return once(
      text,
      [
        "            if (e instanceof EncryptionError) {",
        "              if (this.partialDecrypt) {",
        "                const size = this.metadata.blocks[index].DecompSize;",
        "                return BufferWrapper.alloc(size, true);",
        "              }",
        "              throw e;",
        "            }",
        "            throw e;",
      ].join("\n"),
      '            throw new Error("missing-game-data");',
    );
  });
  scope("WDCReader", (text) => {
    text = once(
      text,
      "    get size() {",
      `
    aurifyRequireRows(ids) {
      if (!${JSON.stringify(requiredDBFiles)}.includes(this.fileName) ||
          !(ids instanceof Set || Array.isArray(ids)) || (ids.size ?? ids.length) > 65536)
        throw new Error("missing-game-data");
      if (!this.aurifyRequiredIDs) {
        if (this.isLoaded || this.data) throw new Error("missing-game-data");
        this.aurifyRequiredIDs = new Set;
      }
      for (const id of ids) {
        if (!Number.isInteger(id) || id <= 0 || id > 2147483647) throw new Error("missing-game-data");
        this.aurifyRequiredIDs.add(id);
      }
      if (!this.aurifyRequiredIDs.size || this.aurifyRequiredIDs.size > 65536) throw new Error("missing-game-data");
    }
    getRequiredRows(ids = this.aurifyRequiredIDs) {
      const rows = new Map;
      for (const id of ids) {
        const row = this.getRow(id);
        if (!row || row.ID !== id) throw new Error("missing-game-data");
        rows.set(id, row);
      }
      return rows;
    }
    get size() {`,
    );
    text = once(
      text,
      "    getRow(recordID) {",
      '    getRow(recordID) {\n      if (this.aurifyRequiredIDs && !this.aurifyRequiredIDs.has(recordID)) throw new Error("missing-game-data");',
    );
    text = once(
      text,
      "    getAllRows() {",
      '    getAllRows() {\n      if (this.aurifyRequiredIDs) throw new Error("missing-game-data");',
    );
    text = once(
      text,
      "    getRelationRows(foreignKeyValue) {",
      '    getRelationRows(foreignKeyValue) {\n      if (this.aurifyRequiredIDs) throw new Error("missing-game-data");',
    );
    text = once(
      text,
      "      const data = await core.view.casc.getVirtualFileByName(this.fileName, false);",
      `      this.aurifyReadablePartial = Boolean(this.aurifyRequiredIDs) || ${JSON.stringify(bestEffortDBFiles)}.includes(this.fileName);\n      const data = this.aurifyReadablePartial ? await core.view.casc.getRequiredDBFile(this.fileName) : await core.view.casc.getVirtualFileByName(this.fileName, false);\n      if (this.aurifyReadablePartial) data.aurifyAssertRange(0, 4);`,
    );
    text = once(
      text,
      "      const wdcVersion = format.wdcVersion;",
      "      const wdcVersion = format.wdcVersion;\n      if (this.aurifyReadablePartial) data.aurifyAssertRange(0, wdcVersion === 5 ? 204 : 72);",
    );
    text = once(
      text,
      "      const sectionCount = data.readUInt32LE();",
      `
      const sectionCount = data.readUInt32LE();
      if (this.aurifyReadablePartial) {
        if (sectionCount > 65536 || totalFieldCount > 4096 || fieldStorageInfoSize % 24)
          throw new Error("missing-game-data");
        data.aurifyAssertRange(0, data.offset + sectionCount * (wdcVersion === 2 ? 36 : 40) + totalFieldCount * 4 + fieldStorageInfoSize + palletDataSize + commonDataSize);
      }`,
    );
    text = once(
      text,
      "          let entryCount = data.readUInt32LE();\n          data.move(entryCount * 4);",
      "          if (this.aurifyReadablePartial) data.aurifyAssertRange(data.offset, 4);\n          let entryCount = data.readUInt32LE();\n          if (this.aurifyReadablePartial) data.aurifyAssertRange(data.offset, entryCount * 4);\n          data.move(entryCount * 4);",
    );
    text = once(
      text,
      "      this.sections = new Array(sectionCount);",
      `
      const aurifyMetadataEnd = data.offset;
      if (this.aurifyReadablePartial) {
        this.aurifySectionEnds = new Array(sectionCount).fill(null);
        let end = data.byteLength;
        for (let index = sectionCount - 1; index >= 0; index--) {
          const header = sectionHeaders[index];
          if (!(header.recordCount || header.stringTableSize || header.idListSize || header.copyTableSize || header.copyTableCount || header.offsetMapIDCount || header.relationshipDataSize)) continue;
          if (header.fileOffset < aurifyMetadataEnd || header.fileOffset > end) throw new Error("missing-game-data");
          this.aurifySectionEnds[index] = end;
          end = header.fileOffset;
        }
      }
      this.sections = new Array(sectionCount);`,
    );
    text = once(
      text,
      "        const header = sectionHeaders[sectionIndex];\n        const isNormal = !(this.flags & 1);",
      `
        const header = sectionHeaders[sectionIndex];
        const isNormal = !(this.flags & 1);
        if (this.aurifyReadablePartial) {
          const end = this.aurifySectionEnds[sectionIndex];
          if (end === null) {
            this.sections[sectionIndex] = {
              header, isNormal, recordDataOfs: data.offset, recordDataSize: 0, stringBlockOfs: data.offset,
              stringTableOffset: data.offset, stringTableOffsetBase: previousStringTableSize, idList: [], offsetMap: [], isEncrypted: false
            };
            continue;
          }
          const recordDataSize = isNormal ? this.recordSize * header.recordCount : (wdcVersion === 2 ? header.offsetMapOffset : header.offsetRecordsEnd) - header.fileOffset;
          const suffixSize = header.idListSize + (wdcVersion === 2 ? header.copyTableSize : header.copyTableCount * 8 + header.offsetMapIDCount * 10) + header.relationshipDataSize;
          if (recordDataSize < 0 || header.fileOffset + recordDataSize + header.stringTableSize + suffixSize > end) throw new Error("missing-game-data");
          if (wdcVersion === 2 && !isNormal && (header.offsetMapOffset < header.fileOffset || header.offsetMapOffset + (this.maxID - this.minID + 1) * 6 > end)) throw new Error("missing-game-data");
          data.seek(header.fileOffset);
          if (!data.aurifyHasRange(header.fileOffset, end - header.fileOffset)) {
            this.sections[sectionIndex] = {
              header, isNormal, recordDataOfs: header.fileOffset, recordDataSize,
              stringBlockOfs: header.fileOffset + recordDataSize, stringTableOffset: header.fileOffset + recordDataSize,
              stringTableOffsetBase: previousStringTableSize, idList: [], offsetMap: [], isEncrypted: true
            };
            if (wdcVersion > 2) previousStringTableSize += header.stringTableSize;
            data.seek(end);
            continue;
          }
        }`,
    );
    text = once(
      text,
      "          const relationshipEntryCount = data.readUInt32LE();",
      '          const relationshipEntryCount = data.readUInt32LE();\n          if (this.aurifyReadablePartial && 12 + relationshipEntryCount * 8 > header.relationshipDataSize) throw new Error("missing-game-data");',
    );
    text = once(
      text,
      "        this.sections[sectionIndex] = { header, isNormal, recordDataOfs, recordDataSize, stringBlockOfs, stringTableOffset, stringTableOffsetBase, idList, offsetMap, relationshipMap, isEncrypted: false };",
      '        if (this.aurifyReadablePartial && data.offset > this.aurifySectionEnds[sectionIndex]) throw new Error("missing-game-data");\n        this.sections[sectionIndex] = { header, isNormal, recordDataOfs, recordDataSize, stringBlockOfs, stringTableOffset, stringTableOffsetBase, idList, offsetMap, relationshipMap, isEncrypted: false };',
    );
    text = once(
      text,
      "      if (hasIDMap && emptyIDMap)\n        recordID = recordIndex;",
      '      if (this.aurifyReadablePartial && !this.aurifyRequiredIDs && emptyIDMap) throw new Error("missing-game-data");\n      if (hasIDMap && emptyIDMap)\n        recordID = recordIndex;',
    );
    text = once(
      text,
      "      const absoluteRecordOffs = recordOfs - this.recordCount * this.recordSize;",
      `
      const absoluteRecordOffs = recordOfs - this.recordCount * this.recordSize;
      if (this.aurifyReadablePartial) {
        const start = isNormal ? section.recordDataOfs + recordOfs : recordOfs;
        const size = isNormal ? this.recordSize : offsetMap[this.wdcVersion === 2 ? recordID : recordIndex].size;
        if (start < section.recordDataOfs || start + size > section.recordDataOfs + section.recordDataSize) throw new Error("missing-game-data");
        this.data.aurifyAssertRange(start, size);
      }`,
    );
    text = once(
      text,
      "      if (section.relationshipMap && section.relationshipMap.has(recordIndex)) {\n        const foreignID = section.relationshipMap.get(recordIndex);",
      '      if (this.aurifyReadablePartial && (this.data.offset < section.recordDataOfs || this.data.offset > this.aurifySectionEnds[sectionIndex])) throw new Error("missing-game-data");\n      if (section.relationshipMap && section.relationshipMap.has(recordIndex)) {\n        const foreignID = section.relationshipMap.get(recordIndex);',
    );
    text = once(
      text,
      "        const section = this.sections[sectionIndex];\n        const header = section.header;\n        const offsetMap = section.offsetMap;",
      "        const section = this.sections[sectionIndex];\n        if (section.isEncrypted) continue;\n        const header = section.header;\n        const offsetMap = section.offsetMap;",
    );
    text = once(
      text,
      [
        "          if (isZeroed) {",
        '            log.write("Skipping all-zero encrypted section " + sectionIndex + " in file " + this.fileName);',
        "            section.isEncrypted = true;",
        "            continue;",
        "          }",
      ].join("\n"),
      [
        "          if (isZeroed && (header.recordCount > 0 || header.copyTableCount > 0 || header.offsetMapIDCount > 0)) {",
        '            if (!this.aurifyReadablePartial) throw new Error("missing-game-data");',
        "            section.isEncrypted = true;",
        "            continue;",
        "          }",
      ].join("\n"),
    );
    text = once(
      text,
      '      return this.data.readString(this.data.indexOf(0) - this.data.offset, "utf8");',
      '      const stringEnd = this.data.indexOf(0);\n      if (this.aurifyReadablePartial && (stringEnd < this.data.offset || stringEnd >= targetSection.stringTableOffset + targetSection.header.stringTableSize)) throw new Error("missing-game-data");\n      return this.data.readString(stringEnd - this.data.offset, "utf8");',
    );
    return once(
      text,
      '      if (!targetSection)\n        throw new Error("String table index out of range");',
      '      if (!targetSection)\n        throw new Error("String table index out of range");\n      if (targetSection.isEncrypted) throw new Error("missing-game-data");\n      if (this.aurifyReadablePartial) this.data.aurifyAssertRange(targetSection.stringTableOffset, targetSection.header.stringTableSize);',
    );
  });
  // Runtime startup is replaced, so no UI source selector, updater or cache collector can execute.
  const boot = "\n// src/app.js\n";
  if (source.split(boot).length !== 2)
    throw new Error("Upstream boot anchor drift");
  source = source.slice(0, source.indexOf(boot)) + "\n" + bootstrap;
  source = once(
    source,
    'SUBMIT_URL: "https://www.kruithne.net/wow.export/v2/cache/submit"',
    "SUBMIT_URL: null",
  );
  source = once(
    source,
    'FINALIZE_URL: "https://www.kruithne.net/wow.export/v2/cache/finalize"',
    "FINALIZE_URL: null",
  );
  return source;
}
