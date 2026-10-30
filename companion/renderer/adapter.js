// Production-only narrow entry points. No import-character UI, default choices or export UI events.
let aurify_catalog_cache;
let aurify_supported_choices;
let aurify_conditional_choices;
let aurify_player_records;
let aurify_bone_choices;
const aurify_fail = (code) => {
  throw new Error(code);
};
const aurify_id = (value) =>
  Number.isInteger(value) && value > 0 && value <= 2147483647;
const aurify_nonnegative_id = (value) =>
  Number.isInteger(value) && value >= 0 && value <= 2147483647;
const aurify_label = (value) => {
  if (
    typeof value !== "string" ||
    !value.trim() ||
    value.length > 120 ||
    /[\x00-\x1f\x7f]/.test(value)
  )
    aurify_fail("invalid-appearance");
  return value;
};
// Independent parser for wowdev.wiki/BONE; native (x,y,z) becomes renderer (x,z,-y).
function aurify_read_bone_corrections(data, bones) {
  try {
    if (!Array.isArray(bones) || !bones.length || bones.length > 65536)
      aurify_fail("invalid-appearance");
    const count = bones.length;
    const state = new Uint8Array(count);
    const order = [];
    const trail = [];
    for (const bone of bones) {
      if (
        !bone ||
        !Number.isInteger(bone.parentBone) ||
        bone.parentBone < -1 ||
        bone.parentBone >= count ||
        !Number.isInteger(bone.flags) ||
        bone.flags < 0 ||
        bone.flags > 0xffffffff ||
        !bone.pivot ||
        bone.pivot.length !== 3 ||
        !Number.isFinite(bone.pivot[0]) ||
        !Number.isFinite(bone.pivot[1]) ||
        !Number.isFinite(bone.pivot[2])
      )
        aurify_fail("invalid-appearance");
    }
    for (let index = 0; index < count; index++) {
      if (state[index]) continue;
      trail.length = 0;
      let current = index;
      while (current >= 0 && !state[current]) {
        state[current] = 1;
        trail.push(current);
        current = bones[current].parentBone;
      }
      if (current >= 0 && state[current] === 1)
        aurify_fail("invalid-appearance");
      for (let at = trail.length - 1; at >= 0; at--) {
        state[trail[at]] = 2;
        order.push(trail[at]);
      }
    }
    if (
      !data ||
      !Number.isSafeInteger(data.byteLength) ||
      data.byteLength < 20 ||
      data.byteLength > 20 + count * 66 ||
      data.offset !== 0 ||
      data.readUInt32LE() !== 1
    )
      aurify_fail("invalid-appearance");
    let indices, matrices;
    const native = new Float64Array(16);
    const axes = [0, 2, 1, 3];
    const signs = [1, 1, -1, 1];
    while (data.remainingBytes) {
      if (data.remainingBytes < 8) aurify_fail("invalid-appearance");
      const tag = data.readUInt32LE();
      const size = data.readUInt32LE();
      const end = data.offset + size;
      if (end > data.byteLength) aurify_fail("invalid-appearance");
      if (tag === 0x41444942) {
        if (indices || !size || size % 2 || size / 2 > count)
          aurify_fail("invalid-appearance");
        indices = new Uint16Array(size / 2);
        const seen = new Set();
        for (let at = 0; at < indices.length; at++) {
          const index = data.readUInt16LE();
          if (index >= count || seen.has(index))
            aurify_fail("invalid-appearance");
          seen.add(index);
          indices[at] = index;
        }
      } else if (tag === 0x544d4f42) {
        if (matrices || !size || size % 64 || size / 64 > count)
          aurify_fail("invalid-appearance");
        matrices = new Array(size / 64);
        for (let at = 0; at < matrices.length; at++) {
          for (let component = 0; component < 16; component++) {
            native[component] = data.readFloatLE();
            if (!Number.isFinite(native[component]))
              aurify_fail("invalid-appearance");
          }
          if (
            native[3] !== 0 ||
            native[7] !== 0 ||
            native[11] !== 0 ||
            native[15] !== 1
          )
            aurify_fail("invalid-appearance");
          const determinant =
            native[0] * (native[5] * native[10] - native[9] * native[6]) -
            native[4] * (native[1] * native[10] - native[9] * native[2]) +
            native[8] * (native[1] * native[6] - native[5] * native[2]);
          if (!Number.isFinite(determinant) || determinant === 0)
            aurify_fail("invalid-appearance");
          const matrix = new Float32Array(16);
          for (let column = 0; column < 4; column++)
            for (let row = 0; row < 4; row++)
              matrix[column * 4 + row] =
                signs[row] *
                signs[column] *
                native[axes[column] * 4 + axes[row]];
          matrices[at] = matrix;
        }
      } else {
        aurify_fail("invalid-appearance");
      }
      if (data.offset !== end) aurify_fail("invalid-appearance");
    }
    if (!indices || !matrices || indices.length !== matrices.length)
      aurify_fail("invalid-appearance");
    const corrections = new Map();
    const relevant = new Uint8Array(count);
    const affected = new Uint8Array(count);
    for (let at = 0; at < indices.length; at++) {
      const index = indices[at];
      corrections.set(index, matrices[at]);
      for (
        let parent = index;
        parent >= 0 && !relevant[parent];
        parent = bones[parent].parentBone
      )
        relevant[parent] = 1;
    }
    // wowdev.wiki/M2#LDV1: 0x10000/0x20000/0x40000 select reduced-LOD
    // emitter suppression; the LOD0 mask is zero. Pinned wow.export 0.2.19
    // loads regular skin 0 (not lodSkins) and calculates every bone matrix.
    const ordinaryLod0Flags = 0x200 | 0x10000 | 0x20000 | 0x40000;
    for (const index of order) {
      const parent = bones[index].parentBone;
      affected[index] =
        corrections.has(index) || (parent >= 0 && affected[parent]) ? 1 : 0;
      // LOD suppression metadata needs no transform at full detail. Parent suppression,
      // billboards, physics, helmet scaling, alternate-parent and unknown bits still fail closed.
      if (
        (relevant[index] || affected[index]) &&
        bones[index].flags & ~ordinaryLod0Flags
      )
        aurify_fail("invalid-appearance");
    }
    return corrections;
  } catch {
    aurify_fail("invalid-appearance");
  }
}
async function aurify_tabard_catalog() {
  await DBGuildTabard.ensureInitialized();
  const backgrounds = await db2.GuildTabardBackground.getAllRows();
  const borders = await db2.GuildTabardBorder.getAllRows();
  const emblems = await db2.GuildTabardEmblem.getAllRows();
  if (!backgrounds.size || !borders.size || !emblems.size) return undefined;
  const options = (rows, field, label) => {
    const ids = [
      ...new Set(
        [...rows.values()]
          .filter(
            (row) =>
              [3, 4].includes(row.Component) && aurify_id(row.FileDataID),
          )
          .map((row) => row[field]),
      ),
    ].sort((a, b) => a - b);
    if (
      !ids.length ||
      ids.length > 2048 ||
      ids.some((id) => !aurify_nonnegative_id(id))
    )
      aurify_fail("unsupported-tabard");
    return ids.map((id) => ({ id, label: label + " " + id }));
  };
  return {
    background: options(backgrounds, "Color", "Background"),
    borderStyle: options(borders, "BorderID", "Border"),
    borderColor: options(borders, "Color", "Border color"),
    emblemDesign: options(emblems, "EmblemID", "Emblem"),
    emblemColor: options(emblems, "Color", "Emblem color"),
  };
}
async function aurify_initialize_player_records() {
  if (aurify_player_records) return aurify_player_records;
  // These reference tables remain whole-table strict. They define the complete player demand set.
  const races = await db2.ChrRaces.getAllRows();
  const raceModels = await db2.ChrRaceXChrModel.getAllRows();
  const models = await db2.ChrModel.getAllRows();
  const options = await db2.ChrCustomizationOption.getAllRows();
  const choices = await db2.ChrCustomizationChoice.getAllRows();
  const elements = await db2.ChrCustomizationElement.getAllRows();
  const conditionalRows = await db2.ChrCustomizationCondModel.getAllRows();
  const playableModels = new Set();
  const raceBodies = new Map();
  for (const row of raceModels.values()) {
    const race = races.get(row.ChrRacesID);
    if (!race) aurify_fail("missing-game-data");
    if (
      ((race.Flags & 1) !== 0 &&
        row.ChrRacesID !== 23 &&
        row.ChrRacesID !== 75) ||
      (row.Sex !== 0 && row.Sex !== 1)
    )
      continue;
    if (!aurify_id(row.ChrModelID) || !models.has(row.ChrModelID))
      aurify_fail("missing-game-data");
    const key = row.ChrRacesID + ":" + row.Sex;
    if (raceBodies.has(key) && raceBodies.get(key) !== row.ChrModelID)
      aurify_fail("missing-game-data");
    raceBodies.set(key, row.ChrModelID);
    playableModels.add(row.ChrModelID);
  }
  if (!playableModels.size || raceBodies.size > 128)
    aurify_fail("invalid-appearance");
  const playerOptions = new Set();
  for (const [id, row] of options)
    if (playableModels.has(row.ChrModelID)) playerOptions.add(id);
  const playerChoices = new Set();
  const playerChoiceModels = new Map();
  for (const [id, row] of choices) {
    if (!playerOptions.has(row.ChrCustomizationOptionID)) continue;
    playerChoices.add(id);
    playerChoiceModels.set(
      id,
      options.get(row.ChrCustomizationOptionID).ChrModelID,
    );
  }
  const displayIds = new Set(),
    modelDataIds = new Set();
  for (const id of playableModels) {
    const displayId = models.get(id).DisplayID;
    if (!aurify_id(displayId)) aurify_fail("missing-game-data");
    displayIds.add(displayId);
  }
  for (const row of elements.values()) {
    if (
      !playerChoices.has(row.ChrCustomizationChoiceID) ||
      !row.ChrCustomizationCondModelID
    )
      continue;
    const modelDataId = conditionalRows.get(
      row.ChrCustomizationCondModelID,
    )?.CreatureModelDataID;
    if (!aurify_id(modelDataId)) aurify_fail("missing-game-data");
    modelDataIds.add(modelDataId);
  }
  const displays = await db2.CreatureDisplayInfo.getRequiredRows(displayIds);
  for (const row of displays.values()) {
    if (!aurify_id(row.ModelID)) aurify_fail("missing-game-data");
    modelDataIds.add(row.ModelID);
  }
  const creatureModels =
    await db2.CreatureModelData.getRequiredRows(modelDataIds);
  for (const row of creatureModels.values())
    if (!aurify_id(row.FileDataID)) aurify_fail("missing-game-data");
  require_DBCreatures().setRequiredPlayerRows(displays, creatureModels);
  aurify_player_records = {
    playableModels,
    playerChoices,
    playerChoiceModels,
    conditionalRows,
    elements,
    creatureModels,
  };
  return aurify_player_records;
}
async function aurify_catalog(client) {
  if (aurify_catalog_cache) return aurify_catalog_cache;
  const playerRecords = await aurify_initialize_player_records();
  await DBCharacterCustomization.ensureInitialized();
  // Upstream excludes Flags & 32 from its active-by-default option IDs.
  // Read option membership only; never import its first-choice selection behavior.
  const requiredOptions = new Set(
    DBCharacterCustomization.get_default_options(),
  );
  const unsupported = new Set();
  aurify_conditional_choices = new Map();
  aurify_bone_choices = new Map();
  let boneSets;
  const { conditionalRows, creatureModels } = playerRecords;
  for (const row of playerRecords.elements.values()) {
    if (!playerRecords.playerChoices.has(row.ChrCustomizationChoiceID))
      continue;
    // Admit only implemented effects; metadata IDs are not visual effects.
    for (const [key, value] of Object.entries(row)) {
      if (
        ![
          "ID",
          "ChrCustomizationChoiceID",
          "RelatedChrCustomizationChoiceID",
          "ChrCustomizationBoneSetID",
          "ChrCustomizationGeosetID",
          "ChrCustomizationMaterialID",
          "ChrCustomizationSkinnedModelID",
          "ChrCustomizationCondModelID",
        ].includes(key) &&
        value > 0
      )
        unsupported.add(row.ChrCustomizationChoiceID);
    }
    if (row.ChrCustomizationBoneSetID) {
      if (!boneSets) boneSets = await db2.ChrCustomizationBoneSet.getAllRows();
      const boneSet = boneSets.get(row.ChrCustomizationBoneSetID);
      const baseModel = DBCharacterCustomization.get_model_file_data_id(
        playerRecords.playerChoiceModels.get(row.ChrCustomizationChoiceID),
      );
      if (
        !boneSet ||
        boneSet.ID !== row.ChrCustomizationBoneSetID ||
        !aurify_id(boneSet.BoneFileDataID) ||
        !aurify_id(boneSet.ModelFileDataID) ||
        !aurify_nonnegative_id(row.RelatedChrCustomizationChoiceID)
      )
        aurify_fail("missing-game-data");
      if (boneSet.ModelFileDataID !== baseModel) {
        unsupported.add(row.ChrCustomizationChoiceID);
      } else {
        if (!aurify_bone_choices.has(row.ChrCustomizationChoiceID))
          aurify_bone_choices.set(row.ChrCustomizationChoiceID, []);
        aurify_bone_choices.get(row.ChrCustomizationChoiceID).push({
          owner: row.ChrCustomizationChoiceID,
          related: row.RelatedChrCustomizationChoiceID,
          boneSetId: boneSet.ID,
          fileDataId: boneSet.BoneFileDataID,
          modelFileDataId: boneSet.ModelFileDataID,
        });
      }
    }
    if (row.ChrCustomizationCondModelID) {
      const conditional = conditionalRows.get(row.ChrCustomizationCondModelID);
      const fileDataId = creatureModels.get(
        conditional?.CreatureModelDataID,
      )?.FileDataID;
      if (
        !aurify_id(fileDataId) ||
        DBCharacterCustomization.get_choice_cond_model_file_data_id(
          row.ChrCustomizationChoiceID,
        ) !== fileDataId
      ) {
        aurify_fail("missing-game-data");
      } else {
        if (!aurify_conditional_choices.has(row.ChrCustomizationChoiceID))
          aurify_conditional_choices.set(
            row.ChrCustomizationChoiceID,
            new Set(),
          );
        aurify_conditional_choices
          .get(row.ChrCustomizationChoiceID)
          .add(fileDataId);
      }
    }
  }
  aurify_supported_choices = new Set();
  const models = [];
  for (const [raceId, race] of DBCharacterCustomization.get_chr_race_map()) {
    if (race.isNPCRace) continue;
    const raceModels = DBCharacterCustomization.get_race_models(raceId);
    if (!raceModels) continue;
    for (const [bodyType, modelId] of raceModels) {
      if (bodyType !== 0 && bodyType !== 1) continue;
      const sourceOptions =
        DBCharacterCustomization.get_options_for_model(modelId);
      if (!DBCharacterCustomization.get_model_file_data_id(modelId))
        aurify_fail("missing-game-data");
      if (!sourceOptions?.length) aurify_fail("missing-game-data");
      if (sourceOptions.length > 128) aurify_fail("invalid-appearance");
      const options = [];
      let representable = true;
      for (const option of sourceOptions) {
        const sourceChoices = DBCharacterCustomization.get_choices_for_option(
          option.id,
        );
        if (
          !sourceChoices ||
          sourceChoices.length > 2048 ||
          !aurify_id(option.id)
        )
          aurify_fail("invalid-appearance");
        const choices = sourceChoices
          .filter((choice) => !unsupported.has(choice.id))
          .map((choice) => {
            if (!aurify_id(choice.id)) aurify_fail("invalid-appearance");
            aurify_supported_choices.add(choice.id);
            return { id: choice.id, label: aurify_label(choice.label) };
          });
        if (!choices.length) representable = false;
        options.push({
          id: option.id,
          label: aurify_label(option.label),
          required: requiredOptions.has(option.id),
          choices,
        });
      }
      // A model with an unrepresentable required dimension is not offered as a configurable model.
      if (!representable) continue;
      if (!aurify_id(raceId) || !aurify_id(modelId))
        aurify_fail("invalid-appearance");
      models.push({
        raceId,
        bodyType,
        modelId,
        raceName: aurify_label(race.name),
        bodyName: "Type " + (bodyType + 1),
        options,
      });
    }
  }
  if (!models.length || models.length > 128) aurify_fail("invalid-appearance");
  const guildTabard = await aurify_tabard_catalog();
  aurify_catalog_cache = {
    schemaVersion: 1,
    client,
    models,
    ...(guildTabard ? { guildTabard } : {}),
  };
  return aurify_catalog_cache;
}
async function aurify_initialize_equipment_records(itemIds) {
  // An explicitly empty visible loadout needs no item tables, never placeholder items.
  if (!itemIds.size) return new Map();
  const itemRows = await db2.Item.getRequiredRows(itemIds);
  const sparseRows = await db2.ItemSparse.getRequiredRows(itemIds);
  for (const id of itemIds) {
    const item = itemRows.get(id);
    const sparse = sparseRows.get(id);
    if (
      !aurify_nonnegative_id(item.ClassID) ||
      !aurify_nonnegative_id(item.SubclassID) ||
      !aurify_nonnegative_id(sparse.InventoryType)
    )
      aurify_fail("missing-game-data");
  }
  // ItemID is not the primary key. Best-effort ItemModifiedAppearance reads
  // contain only complete readable sections; every selected item still needs a concrete mapping.
  const modifiedRows = await db2.ItemModifiedAppearance.getAllRows();
  const modifiers = new Map();
  const appearanceIds = new Set();
  for (const [id, row] of modifiedRows) {
    if (!itemIds.has(row.ItemID)) continue;
    if (
      row.ID !== id ||
      !aurify_id(id) ||
      !aurify_nonnegative_id(row.ItemAppearanceModifierID) ||
      !aurify_id(row.ItemAppearanceID)
    )
      aurify_fail("missing-game-data");
    let byModifier = modifiers.get(row.ItemID);
    if (!byModifier) modifiers.set(row.ItemID, (byModifier = new Map()));
    const previous = byModifier.get(row.ItemAppearanceModifierID);
    if (previous !== undefined && previous !== row.ItemAppearanceID)
      aurify_fail("unsupported-equipment");
    byModifier.set(row.ItemAppearanceModifierID, row.ItemAppearanceID);
    appearanceIds.add(row.ItemAppearanceID);
  }
  for (const id of itemIds)
    if (!modifiers.has(id)) aurify_fail("missing-game-data");
  const appearances = await db2.ItemAppearance.getRequiredRows(appearanceIds);
  const displayIds = new Set();
  for (const row of appearances.values()) {
    if (!aurify_id(row.ItemDisplayInfoID)) aurify_fail("missing-game-data");
    displayIds.add(row.ItemDisplayInfoID);
  }
  const displays = await db2.ItemDisplayInfo.getRequiredRows(displayIds);
  // Required PKs remain strict. Only ItemModifiedAppearance and ItemDisplayInfoModelMatRes
  // permit complete readable relation sections; all other mapping tables remain whole-table strict.
  await DBItems.ensureInitialized();
  await DBItemCharTextures.ensureInitialized();
  await DBItemGeosets.ensureInitialized();
  await DBItemModels.ensureInitialized();
  return displays;
}
// Pinned wow.export 0.2.19 ModelViewerGL.fit_camera_for_character and
// m2.vertex.shader calc_env_coord: freeze the real spherical reflection at
// the exporter's reference view, not at the interactive viewer's orbit.
function aurify_frozen_environment_uv(renderer) {
  const m2 = renderer.m2;
  const vertexCount = m2.vertices.length / 3;
  const model = renderer.model_matrix;
  if (
    !Number.isInteger(vertexCount) ||
    vertexCount <= 0 ||
    m2.normals.length !== m2.vertices.length ||
    model?.length !== 16 ||
    !model.every(Number.isFinite) ||
    model[3] !== 0 ||
    model[7] !== 0 ||
    model[11] !== 0 ||
    model[15] !== 1
  )
    aurify_fail("incomplete-materials");
  const eyeY = 1.609,
    eyeZ = 2.347;
  const distance = Math.hypot(eyeY - 1.247, eyeZ - 0.537);
  const fy = (eyeY - 1.247) / distance,
    fz = (eyeZ - 0.537) / distance;
  const view = new Float64Array([
    1,
    0,
    0,
    0,
    0,
    fz,
    fy,
    0,
    0,
    -fy,
    fz,
    0,
    0,
    -(fz * eyeY - fy * eyeZ),
    -(fy * eyeY + fz * eyeZ),
    1,
  ]);
  const modelView = new Float64Array(16);
  for (let column = 0; column < 4; column++)
    for (let row = 0; row < 4; row++)
      for (let component = 0; component < 4; component++)
        modelView[column * 4 + row] +=
          view[component * 4 + row] * model[column * 4 + component];
  const bones = renderer.bone_matrices;
  const boneCount = renderer.bones?.length || 0;
  if (
    boneCount &&
    (!bones ||
      bones.length < boneCount * 16 ||
      m2.boneIndices.length !== vertexCount * 4 ||
      m2.boneWeights.length !== vertexCount * 4)
  )
    aurify_fail("incomplete-materials");
  // Allocate once for the body. Only requested reflection vertices are projected,
  // and shared triangle vertices/submeshes reuse their cached result.
  const uv = new Float64Array(vertexCount * 2);
  const ready = new Uint8Array(vertexCount);
  const weighted = new Float64Array(16);
  const combined = new Float64Array(16);
  const ensure = (vertex) => {
    if (!Number.isInteger(vertex) || vertex < 0 || vertex >= vertexCount)
      aurify_fail("incomplete-materials");
    if (ready[vertex]) return;
    if (boneCount) {
      weighted.fill(0);
      let totalWeight = 0;
      for (let influence = 0; influence < 4; influence++) {
        const index = vertex * 4 + influence;
        const weight = m2.boneWeights[index];
        if (!Number.isInteger(weight) || weight < 0 || weight > 255)
          aurify_fail("incomplete-materials");
        if (weight === 0) continue;
        const bone = m2.boneIndices[index];
        if (!Number.isInteger(bone) || bone < 0 || bone >= boneCount)
          aurify_fail("incomplete-materials");
        totalWeight += weight;
        const normalizedWeight = weight / 255;
        const boneOffset = bone * 16;
        for (let component = 0; component < 16; component++)
          weighted[component] +=
            bones[boneOffset + component] * normalizedWeight;
      }
      if (totalWeight !== 255) aurify_fail("incomplete-materials");
      combined.fill(0);
      for (let column = 0; column < 4; column++)
        for (let row = 0; row < 4; row++)
          for (let component = 0; component < 4; component++)
            combined[column * 4 + row] +=
              modelView[component * 4 + row] * weighted[column * 4 + component];
    } else combined.set(modelView);
    for (const value of combined)
      if (!Number.isFinite(value)) aurify_fail("incomplete-materials");
    const offset = vertex * 3;
    const x = m2.vertices[offset],
      y = m2.vertices[offset + 1],
      z = m2.vertices[offset + 2];
    let px = combined[0] * x + combined[4] * y + combined[8] * z + combined[12];
    let py = combined[1] * x + combined[5] * y + combined[9] * z + combined[13];
    let pz =
      combined[2] * x + combined[6] * y + combined[10] * z + combined[14];
    // Cofactor matrix / determinant is transpose(inverse(mat3(view*model*weightedBone))).
    // Do not use getBakedGeometry's ordinary-matrix normals for nonuniform bone scales.
    const a = combined[0],
      b = combined[4],
      c = combined[8];
    const d = combined[1],
      e = combined[5],
      f = combined[9];
    const g = combined[2],
      h = combined[6],
      i = combined[10];
    const c00 = e * i - f * h,
      c01 = f * g - d * i,
      c02 = d * h - e * g;
    const determinant = a * c00 + b * c01 + c * c02;
    if (!Number.isFinite(determinant) || determinant === 0)
      aurify_fail("incomplete-materials");
    const sx = m2.normals[offset],
      sy = m2.normals[offset + 1],
      sz = m2.normals[offset + 2];
    let nx = (c00 * sx + c01 * sy + c02 * sz) / determinant;
    let ny =
      ((c * h - b * i) * sx + (a * i - c * g) * sy + (b * g - a * h) * sz) /
      determinant;
    let nz =
      ((b * f - c * e) * sx + (c * d - a * f) * sy + (a * e - b * d) * sz) /
      determinant;
    const positionLength = Math.hypot(px, py, pz),
      normalLength = Math.hypot(nx, ny, nz);
    if (
      !Number.isFinite(positionLength) ||
      positionLength === 0 ||
      !Number.isFinite(normalLength) ||
      normalLength === 0
    )
      aurify_fail("incomplete-materials");
    px /= positionLength;
    py /= positionLength;
    pz /= positionLength;
    nx /= normalLength;
    ny /= normalLength;
    nz /= normalLength;
    const twiceDot = 2 * (px * nx + py * ny + pz * nz);
    const rx = px - twiceDot * nx,
      ry = py - twiceDot * ny,
      rz = pz - twiceDot * nz;
    const denominator = 2 * Math.hypot(rx, ry, rz + 1);
    if (!Number.isFinite(denominator) || denominator <= 1e-12)
      aurify_fail("incomplete-materials");
    const u = rx / denominator + 0.5,
      v = ry / denominator + 0.5;
    if (!Number.isFinite(u) || !Number.isFinite(v))
      aurify_fail("incomplete-materials");
    uv[vertex * 2] = u;
    uv[vertex * 2 + 1] = v;
    ready[vertex] = 1;
  };
  return { uv, ensure };
}

async function aurify_render(core, request, objPath) {
  const catalog = await aurify_catalog(request.client);
  const appearance = request.job.appearance;
  const selected = catalog.models.find(
    (model) =>
      model.raceId === appearance?.raceId &&
      model.bodyType === appearance?.bodyType &&
      model.modelId === appearance?.modelId,
  );
  if (
    !selected ||
    !Array.isArray(appearance.choices) ||
    appearance.choices.length > selected.options.length ||
    appearance.choices.length > 128
  )
    aurify_fail("invalid-appearance");
  const chosen = new Map();
  for (const choice of appearance.choices) {
    const option = selected.options.find(
      (option) => option.id === choice.optionId,
    );
    if (
      !option ||
      chosen.has(choice.optionId) ||
      !option.choices.some((entry) => entry.id === choice.choiceId) ||
      !aurify_supported_choices.has(choice.choiceId)
    )
      aurify_fail("invalid-appearance");
    chosen.set(choice.optionId, choice.choiceId);
  }
  for (const option of selected.options)
    if (option.required && !chosen.has(option.id))
      aurify_fail("invalid-appearance");
  const layout = DBCharacterCustomization.get_texture_layout_id(
    selected.modelId,
  );
  const activeChoices = [...chosen].map(([optionID, choiceID]) => ({
    optionID,
    choiceID,
  }));
  const expectedSkinned = new Map();
  const conditionalModels = new Set();
  const selectedChoiceIds = new Set(chosen.values());
  let selectedBoneSet;
  const expectedTextureTargets = new Map();
  const expectTexture = (id, fileDataId) => {
    const key = id + ":" + fileDataId;
    expectedTextureTargets.set(key, (expectedTextureTargets.get(key) || 0) + 1);
  };
  for (const choice of activeChoices) {
    for (const entry of aurify_bone_choices.get(choice.choiceID) || []) {
      if (entry.related && !selectedChoiceIds.has(entry.related)) continue;
      if (
        selectedBoneSet &&
        (selectedBoneSet.owner !== entry.owner ||
          selectedBoneSet.boneSetId !== entry.boneSetId)
      )
        aurify_fail("invalid-appearance");
      selectedBoneSet = entry;
    }
    const gating = character_appearance.get_texture_gating(
      choice.choiceID,
      layout,
    );
    if (
      !gating.has_ungated &&
      gating.gates.size &&
      ![...gating.gates].some(([option, choices]) =>
        choices.has(chosen.get(option)),
      )
    )
      aurify_fail("invalid-appearance");
    const geosetRaw = DBCharacterCustomization.get_choice_geoset_raw(
      choice.choiceID,
    );
    if (
      geosetRaw &&
      !aurify_nonnegative_id(
        DBCharacterCustomization.get_choice_geoset_id(choice.choiceID),
      )
    )
      aurify_fail("invalid-appearance");
    const rawSkinned =
      DBCharacterCustomization.get_choice_skinned_model(choice.choiceID) || [];
    const resolvedSkinned =
      DBCharacterCustomization.get_skinned_model_for_choice(choice.choiceID) ||
      [];
    if (rawSkinned.length !== resolvedSkinned.length)
      aurify_fail("invalid-appearance");
    for (const entry of resolvedSkinned) {
      if (!aurify_id(entry.FileDataID) || !aurify_nonnegative_id(entry.geoset))
        aurify_fail("invalid-appearance");
      if (!expectedSkinned.has(entry.FileDataID))
        expectedSkinned.set(entry.FileDataID, new Set());
      expectedSkinned.get(entry.FileDataID).add(entry.geoset);
    }
    const expectedConditional = aurify_conditional_choices.get(choice.choiceID);
    const conditionalModel =
      DBCharacterCustomization.get_choice_cond_model_file_data_id(
        choice.choiceID,
      );
    if (expectedConditional) {
      if (
        expectedConditional.size !== 1 ||
        !expectedConditional.has(conditionalModel)
      )
        aurify_fail("invalid-appearance");
      conditionalModels.add(conditionalModel);
    } else if (conditionalModel !== undefined)
      aurify_fail("invalid-appearance");
    for (const material of DBCharacterCustomization.get_choice_materials(
      choice.choiceID,
    ) || []) {
      if (
        material.RelatedChrCustomizationChoiceID &&
        !activeChoices.some(
          (choice) =>
            choice.choiceID === material.RelatedChrCustomizationChoiceID,
        )
      )
        continue;
      const resolved = DBCharacterCustomization.get_chr_cust_material(
        material.ChrCustomizationMaterialID,
      );
      if (!resolved || !aurify_id(resolved.FileDataID))
        aurify_fail("incomplete-materials");
      expectTexture(resolved.ChrModelTextureTargetID, resolved.FileDataID);
    }
  }
  if (conditionalModels.size > 1) aurify_fail("invalid-appearance");
  await DBGuildTabard.ensureInitialized();
  const equipment = request.job.equipment;
  const visible = [1, 3, 4, 5, 6, 7, 8, 9, 10, 15, 16, 17, 19];
  if (!Array.isArray(equipment) || equipment.length > 19)
    aurify_fail("incomplete-equipment");
  const observed = new Map();
  for (const entry of equipment) {
    if (
      !Number.isInteger(entry.slot) ||
      entry.slot < 1 ||
      entry.slot > 19 ||
      observed.has(entry.slot) ||
      !Number.isInteger(entry.itemId) ||
      entry.itemId < 0 ||
      entry.itemId > 2147483647
    )
      aurify_fail("incomplete-equipment");
    observed.set(entry.slot, entry);
  }
  for (const slot of visible)
    if (!observed.has(slot)) aurify_fail("incomplete-equipment");
  const requiredItemIds = new Set(
    visible.map((slot) => observed.get(slot).itemId).filter((id) => id > 0),
  );
  const displayRows =
    await aurify_initialize_equipment_records(requiredItemIds);
  const guildItem = observed.get(19).itemId;
  const guildTabard = appearance.guildTabard;
  let tabardConfig = null;
  if (guildTabard !== undefined && guildTabard !== null) {
    const keys = [
      "background",
      "borderStyle",
      "borderColor",
      "emblemDesign",
      "emblemColor",
    ];
    if (
      typeof guildTabard !== "object" ||
      Array.isArray(guildTabard) ||
      Object.keys(guildTabard).length !== keys.length
    )
      aurify_fail("unsupported-tabard");
    for (const key of keys)
      if (
        !aurify_nonnegative_id(guildTabard[key]) ||
        !catalog.guildTabard?.[key]?.some(
          (choice) => choice.id === guildTabard[key],
        )
      )
        aurify_fail("unsupported-tabard");
    tabardConfig = {
      background: guildTabard.background,
      border_style: guildTabard.borderStyle,
      border_color: guildTabard.borderColor,
      emblem_design: guildTabard.emblemDesign,
      emblem_color: guildTabard.emblemColor,
    };
  }
  if (DBGuildTabard.isGuildTabard(guildItem)) {
    if (!tabardConfig) aurify_fail("unsupported-tabard");
    const tier = DBGuildTabard.getTabardTier(guildItem);
    for (const component of [3, 4]) {
      const fdids = [
        DBGuildTabard.getBackgroundFDID(
          tier,
          component,
          tabardConfig.background,
        ),
        DBGuildTabard.getBorderFDID(
          tier,
          component,
          tabardConfig.border_style,
          tabardConfig.border_color,
        ),
        DBGuildTabard.getEmblemFDID(
          component,
          tabardConfig.emblem_design,
          tabardConfig.emblem_color,
        ),
      ];
      if (fdids.some((fdid) => !aurify_id(fdid)))
        aurify_fail("unsupported-tabard");
      const layer = get_slot_layer(19) * 100;
      expectTexture(layer + component, fdids[0]);
      expectTexture(layer + 20 + component, fdids[1]);
      expectTexture(layer + 10 + component, fdids[2]);
    }
  }
  const items = {};
  const modifiers = {};
  const displays = new Map();
  const expectedModels = new Map();
  const materialCounts = new Map();
  const modelTextureDisplays = new Set();
  if (requiredItemIds.size) {
    for (const row of (
      await db2.ItemDisplayInfoMaterialRes.getAllRows()
    ).values())
      materialCounts.set(
        row.ItemDisplayInfoID,
        (materialCounts.get(row.ItemDisplayInfoID) || 0) + 1,
      );
    for (const row of (
      await db2.ItemDisplayInfoModelMatRes.getAllRows()
    ).values())
      if (row.MaterialResourcesID > 0)
        modelTextureDisplays.add(row.ItemDisplayInfoID);
  }
  for (const slot of visible) {
    const entry = observed.get(slot);
    if (!entry.itemId) continue;
    if (entry.itemString != null) {
      if (
        typeof entry.itemString !== "string" ||
        entry.itemString.length > 2048 ||
        !/^item:\d+(?::[-\d]*)*$/.test(entry.itemString) ||
        Number(entry.itemString.split(":")[1]) !== entry.itemId
      )
        aurify_fail("unsupported-equipment");
      // Retail bonus/modifier/transmog encodings require a richer appearance resolver, not item-ID fallback.
      const fields = entry.itemString.split(":");
      if (fields.slice(11).some((value) => value !== "" && Number(value) !== 0))
        aurify_fail("unsupported-equipment");
    }
    const available = DBItemModels.getItemModifiers(entry.itemId);
    if (!available.length) aurify_fail("unsupported-equipment");
    const visualIds = new Set(
      available.map((modifier) =>
        DBItemModels.getDisplayId(entry.itemId, modifier),
      ),
    );
    if (visualIds.size !== 1) aurify_fail("unsupported-equipment");
    const modifier = available.includes(0) ? 0 : available[0];
    const displayId = DBItemModels.getDisplayId(entry.itemId, modifier);
    const displayRow = displayRows.get(displayId);
    if (!displayRow) aurify_fail("unsupported-equipment");
    const mappedSlots = slot === 3 ? [3, 30] : [slot];
    const itemTextures =
      DBItemCharTextures.getItemTextures(
        entry.itemId,
        selected.raceId,
        selected.bodyType,
        modifier,
      ) || [];
    if (
      (materialCounts.get(displayId) || 0) !== itemTextures.length ||
      itemTextures.some((texture) => !aurify_id(texture.fileDataID))
    )
      aurify_fail("incomplete-materials");
    const modelResources = (displayRow.ModelResourcesID || []).filter(
      (id) => id > 0,
    );
    for (const mapped of mappedSlots) {
      const display = DBItemModels.getItemDisplay(
        entry.itemId,
        selected.raceId,
        selected.bodyType,
        modifier,
        mapped === 3 ? "left" : mapped === 30 ? "right" : undefined,
      );
      const expectedCount =
        slot === 3 ? (modelResources.length ? 1 : 0) : modelResources.length;
      if ((display?.models?.length || 0) !== expectedCount)
        aurify_fail("unsupported-equipment");
      if (
        display &&
        modelTextureDisplays.has(displayId) &&
        !display.textures?.length
      )
        aurify_fail("incomplete-materials");
      if (
        !expectedCount &&
        !itemTextures.length &&
        !DBGuildTabard.isGuildTabard(entry.itemId)
      )
        aurify_fail("unsupported-equipment");
      if (!DBGuildTabard.isGuildTabard(entry.itemId)) {
        for (const texture of itemTextures)
          expectTexture(
            get_slot_layer(mapped) * 100 + texture.section,
            texture.fileDataID,
          );
      }
      items[mapped] = entry.itemId;
      modifiers[mapped] = modifier;
      displays.set(mapped, display);
      expectedModels.set(mapped, expectedCount);
    }
  }
  reset_module_state();
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = 512;
  gl_context = new (require_GLContext())(canvas);
  const state = core.view;
  state.chrModelViewerContext = { gl_context, controls: null };
  state.chrCustRaceSelection = [{ id: selected.raceId }];
  state.aurifyBodyType = selected.bodyType;
  state.chrCustModelSelection = [{ id: selected.modelId }];
  state.chrCustActiveChoices = activeChoices;
  state.chrEquippedItems = items;
  state.chrEquippedItemSkins = modifiers;
  state.chrGuildTabardConfig = tabardConfig;
  state.chrCustBakedNPCTexture = null;
  current_char_component_texture_layout_id = layout;
  default_model_file_data_id = DBCharacterCustomization.get_model_file_data_id(
    selected.modelId,
  );
  const expectedActiveModel = conditionalModels.size
    ? [...conditionalModels][0]
    : default_model_file_data_id;
  if (!aurify_id(expectedActiveModel)) aurify_fail("missing-game-data");
  if (
    selectedBoneSet &&
    selectedBoneSet.modelFileDataId !== expectedActiveModel
  )
    aurify_fail("invalid-appearance");
  // Resolve first so the upstream conditional-swap check does not load and dispose an unused base mesh.
  await load_character_model(core, expectedActiveModel);
  if (
    !active_renderer?.m2 ||
    !active_renderer.draw_calls?.length ||
    !chr_materials.size
  )
    aurify_fail("missing-game-data");
  if (
    active_model !== expectedActiveModel ||
    skinned_model_renderers.size !== expectedSkinned.size
  )
    aurify_fail("missing-game-data");
  for (const [fileDataId, geosets] of expectedSkinned) {
    const loaded = skinned_model_renderers.get(fileDataId);
    const skin = await loaded?.renderer?.m2?.getSkin(0);
    if (
      !skin ||
      loaded.geosets?.size !== geosets.size ||
      [...geosets].some(
        (id) =>
          !loaded.geosets.has(id) ||
          !skin.subMeshes.some((mesh) => mesh.submeshID === id),
      )
    )
      aurify_fail("missing-game-data");
  }
  const appliedTextureTargets = new Map();
  for (const material of chr_materials.values()) {
    for (const target of material.textureTargets) {
      const key = target.id + ":" + target.custMaterial.FileDataID;
      appliedTextureTargets.set(key, (appliedTextureTargets.get(key) || 0) + 1);
    }
  }
  for (const [target, count] of expectedTextureTargets)
    if (appliedTextureTargets.get(target) !== count)
      aurify_fail("incomplete-materials");
  for (const [slot, count] of expectedModels) {
    const loaded =
      (equipment_model_renderers.get(slot)?.renderers.length || 0) +
      (collection_model_renderers.get(slot)?.renderers.length || 0);
    if (loaded !== count) aurify_fail("unsupported-equipment");
  }
  // Best-effort materials retain only a verified primary diffuse layer, never an arbitrary pass.
  const usedTextureIndices = new Map();
  const materialSamples = new Map();
  const frozenEnvironmentMeshes = new Set();
  let frozenEnvironmentUV;
  const shaderMapper = require_ShaderMapper();
  const checkRenderer = async (renderer) => {
    const skin = await renderer.m2.getSkin(0);
    if (!skin?.subMeshes?.length) aurify_fail("missing-game-data");
    const samples = new Map();
    const needsSample = renderer.draw_calls.some(
      (call) =>
        call.visible &&
        (call.color_idx >= 0 ||
          call.tex_weight_idx >= 0 ||
          (call.vertex_shader !== 1 &&
            call.tex_matrix_idxs[call.vertex_shader === 10 ? 1 : 0] >= 0)),
    );
    if (needsSample) {
      if (renderer !== active_renderer) {
        const source = renderer.skelLoader || renderer.m2;
        const stand = source.animations?.findIndex(
          (animation) => animation.id === 0 && animation.variationIndex === 0,
        );
        if (!Number.isInteger(stand) || stand < 0)
          aurify_fail("incomplete-materials");
        await renderer.playAnimation(stand);
      }
      // The upstream material samplers also work for models without bones; updateAnimation alone does not.
      renderer._update_tex_matrices();
      renderer._update_submesh_colors();
      renderer._update_tex_weights();
    }
    const used = new Set();
    for (let index = 0; index < skin.subMeshes.length; index++) {
      if (
        renderer.draw_calls[index]?.visible === false ||
        (renderer === active_renderer &&
          state.chrCustGeosets[index]?.checked === false)
      )
        continue;
      const units = skin.textureUnits.filter(
        (unit) => unit.skinSectionIndex === index,
      );
      if (units.length !== 1 || ![1, 2].includes(units[0].textureCount))
        aurify_fail("incomplete-materials");
      const material = renderer.m2.materials[units[0].materialIndex];
      if (!material || ![0, 1, 2].includes(material.blendingMode))
        aurify_fail("incomplete-materials");
      const textureIndex =
        renderer.m2.textureCombos[units[0].textureComboIndex];
      if (
        !Number.isInteger(textureIndex) ||
        textureIndex < 0 ||
        !renderer.m2.textures[textureIndex] ||
        !renderer.textures.has(textureIndex)
      )
        aurify_fail("incomplete-materials");
      const call = renderer.draw_calls[index];
      const textureCount = units[0].textureCount;
      const vertexShader = shaderMapper.getVertexShader(
        textureCount,
        units[0].shaderID,
      );
      const pixelShader = shaderMapper.getPixelShader(
        textureCount,
        units[0].shaderID,
      );
      // Pinned 0.2.19 m2.vertex.shader cases 2/7 both use texture1 with UV0/matrix0.
      // m2.fragment.shader cases 8/10 use mesh_color * tex1.rgb as diffuse;
      // approximate them as Combiners_Mod, omitting secondary specular and
      // (case 8) secondary alpha-sum. This is NOT equivalent shader output.
      const primaryLayerOnly =
        textureCount === 2 &&
        ["Diffuse_T1_T2", "Diffuse_T1_T1"].includes(vertexShader) &&
        ["Combiners_Mod_Add", "Combiners_Mod_AddNA"].includes(pixelShader);
      // Pinned vertex case 3 uses UV0/matrix0 for diffuse and environment UVs
      // only for texture2. Fragment case 13 keeps mesh_color * tex1.rgb and
      // uses tex2.rgb * tex2.a only for specular, never texture-alpha coverage.
      // Approximate as Combiners_Opaque, omitting the secondary reflection.
      const opaquePrimaryLayerOnly =
        textureCount === 2 &&
        vertexShader === "Diffuse_T1_Env" &&
        pixelShader === "Combiners_Opaque_AddAlpha";
      const ordinary =
        textureCount === 1 &&
        ["Diffuse_T1", "Diffuse_T2"].includes(vertexShader) &&
        ["Combiners_Opaque", "Combiners_Mod"].includes(pixelShader);
      const frozenEnvironment =
        renderer === active_renderer &&
        textureCount === 1 &&
        vertexShader === "Diffuse_Env" &&
        pixelShader === "Combiners_Mod" &&
        material.flags === 16 &&
        material.blendingMode === 2;
      if (
        !ordinary &&
        !primaryLayerOnly &&
        !opaquePrimaryLayerOnly &&
        !frozenEnvironment
      )
        aurify_fail("incomplete-materials");
      // uvSet 1 selects the existing explicit alternateUV transport, not a guessed mesh UV.
      const uvSet = frozenEnvironment || vertexShader === "Diffuse_T2" ? 1 : 0;
      const vertexShaderId = primaryLayerOnly
        ? vertexShader === "Diffuse_T1_T2"
          ? 2
          : 7
        : opaquePrimaryLayerOnly
          ? 3
          : frozenEnvironment
            ? 1
            : uvSet
              ? 10
              : 0;
      const pixelShaderId = primaryLayerOnly
        ? pixelShader === "Combiners_Mod_Add"
          ? 8
          : 10
        : opaquePrimaryLayerOnly
          ? 13
          : pixelShader === "Combiners_Mod"
            ? 1
            : 0;
      if (
        !call ||
        call.texture_count !== textureCount ||
        call.tex_indices?.[0] !== textureIndex ||
        call.vertex_shader !== vertexShaderId ||
        call.pixel_shader !== pixelShaderId
      )
        aurify_fail("incomplete-materials");
      const flags = material.flags;
      if (!Number.isInteger(flags) || flags < 0 || flags > 31 || flags & 8)
        aurify_fail("incomplete-materials");
      const color =
        call.color_idx === -1
          ? [1, 1, 1, 1]
          : Array.from(
              renderer.submesh_colors.subarray(
                call.color_idx * 4,
                call.color_idx * 4 + 4,
              ),
            );
      const weight =
        call.tex_weight_idx === -1
          ? 1
          : renderer.tex_weights[call.tex_weight_idx];
      if (
        color.length !== 4 ||
        color.some(
          (value) => !Number.isFinite(value) || value < 0 || value > 1,
        ) ||
        !Number.isFinite(weight) ||
        weight < 0 ||
        weight > 1
      )
        aurify_fail("incomplete-materials");
      const opacity = color[3] * weight;
      // Standard glTF has depth testing, depth-writing opaque/mask, and non-depth-writing alpha blend.
      if (opacity > 0 && Boolean(flags & 16) !== (material.blendingMode === 2))
        aurify_fail("incomplete-materials");
      // Diffuse_Env ignores texture matrices in the pinned vertex shader.
      const matrixIndex = frozenEnvironment ? -1 : call.tex_matrix_idxs[uvSet];
      let uvTransform = [1, 0, 0, 0, 1, 0];
      if (matrixIndex !== -1) {
        if (
          !Number.isInteger(matrixIndex) ||
          matrixIndex < 0 ||
          !renderer.tex_matrices ||
          (matrixIndex + 1) * 16 > renderer.tex_matrices.length
        )
          aurify_fail("incomplete-materials");
        const offset = matrixIndex * 16;
        uvTransform = [0, 4, 12, 1, 5, 13].map(
          (component) => renderer.tex_matrices[offset + component],
        );
        if (
          uvTransform.some(
            (value) => !Number.isFinite(value) || Math.abs(value) > 100000,
          )
        )
          aurify_fail("incomplete-materials");
      }
      samples.set(index, {
        flags,
        blendingMode: material.blendingMode,
        textureFlags: renderer.m2.textures[textureIndex].flags,
        color: color.slice(0, 3),
        opacity,
        uvSet,
        uvTransform,
        // Opaque_AddAlpha's secondary alpha is reflectivity, not coverage.
        textureAlpha: primaryLayerOnly || pixelShader === "Combiners_Mod",
      });
      if (frozenEnvironment) frozenEnvironmentMeshes.add(index);
      used.add(textureIndex);
    }
    if (!used.size) aurify_fail("missing-game-data");
    usedTextureIndices.set(renderer, used);
    materialSamples.set(renderer, samples);
  };
  // Reset even when no selected effect remains; never reuse a previous member's correction.
  active_renderer.aurifyBoneCorrections = null;
  if (selectedBoneSet) {
    const data = await core.view.casc.getFile(selectedBoneSet.fileDataId);
    active_renderer.aurifyBoneCorrections = aurify_read_bone_corrections(
      data,
      active_renderer.bones,
    );
    // The GPU uniform buffer includes spare matrices beyond the active skeleton.
    // Require capacity for every bone; padding is not a truncated pose.
    if (
      !(
        active_renderer.bone_matrices?.length >=
        active_renderer.bones.length * 16
      )
    )
      aurify_fail("invalid-appearance");
  }
  const animationSource = active_renderer.skelLoader || active_renderer.m2;
  const stand = animationSource.animations.findIndex(
    (animation) => animation.id === 0 && animation.variationIndex === 0,
  );
  if (stand < 0) aurify_fail("missing-game-data");
  await active_renderer.playAnimation(stand);
  active_renderer.animation_time = 0;
  const mainhand = equipment_model_renderers.get(16)?.renderers || [];
  const offhand = equipment_model_renderers.get(17)?.renderers || [];
  const mainUsesLeft = mainhand.some(
    (entry) => entry.attachment_id === ATTACHMENT_ID.HAND_LEFT,
  );
  active_renderer.setHandGrip(
    mainhand.length > 0 && !mainUsesLeft,
    mainUsesLeft ||
      offhand.some((entry) => entry.attachment_id !== ATTACHMENT_ID.SHIELD),
  );
  active_renderer.updateAnimation(0);
  await checkRenderer(active_renderer);
  for (const entry of equipment_model_renderers.values())
    for (const child of entry.renderers) await checkRenderer(child.renderer);
  for (const entry of collection_model_renderers.values())
    for (const renderer of entry.renderers) await checkRenderer(renderer);
  for (const entry of skinned_model_renderers.values())
    await checkRenderer(entry.renderer);
  const checkSkinning = async (renderer) => {
    const { m2, bone_remap_table: remap, bone_matrices: matrices } = renderer;
    if (
      !remap ||
      !matrices ||
      !active_renderer.bone_matrices ||
      !m2.boneIndices ||
      !m2.boneWeights
    )
      aurify_fail("unsupported-equipment");
    const skin = await m2.getSkin(0);
    for (let index = 0; index < skin.subMeshes.length; index++) {
      if (!renderer.draw_calls[index]?.visible) continue;
      const mesh = skin.subMeshes[index];
      for (
        let offset = mesh.triangleStart;
        offset < mesh.triangleStart + mesh.triangleCount;
        offset++
      ) {
        const vertex = skin.indices[skin.triangles[offset]];
        let totalWeight = 0;
        for (let component = 0; component < 4; component++) {
          const position = vertex * 4 + component;
          const weight = m2.boneWeights[position];
          if (!weight) continue;
          totalWeight += weight;
          const bone = m2.boneIndices[position];
          const target = remap[bone];
          if (
            !Number.isInteger(target) ||
            target < 0 ||
            (bone + 1) * 16 > matrices.length ||
            (target + 1) * 16 > active_renderer.bone_matrices.length
          )
            aurify_fail("unsupported-equipment");
        }
        if (totalWeight !== 255) aurify_fail("unsupported-equipment");
      }
    }
  };
  for (const entry of collection_model_renderers.values())
    for (const renderer of entry.renderers) await checkSkinning(renderer);
  for (const entry of skinned_model_renderers.values())
    await checkSkinning(entry.renderer);
  const baked = active_renderer.getBakedGeometry();
  if (!baked?.vertices?.length || !baked.normals?.length)
    aurify_fail("missing-game-data");
  const exporter = new M2Exporter(
    await core.view.casc.getFile(active_model),
    [],
    active_model,
  );
  exporter.aurifyMeshMaterials = [];
  exporter.aurifyRequiredTextureIndices =
    usedTextureIndices.get(active_renderer);
  exporter.aurifyMaterialHint = (renderer, index, vertices) => {
    renderer ||= active_renderer;
    const sample = materialSamples.get(renderer)?.get(index);
    if (!sample) aurify_fail("incomplete-materials");
    if (renderer === active_renderer && frozenEnvironmentMeshes.has(index)) {
      frozenEnvironmentUV ||= aurify_frozen_environment_uv(renderer);
      const alternateUV = new Float32Array(vertices.length * 2);
      for (let vertex = 0; vertex < vertices.length; vertex++) {
        const source = vertices[vertex];
        frozenEnvironmentUV.ensure(source);
        alternateUV[vertex * 2] = frozenEnvironmentUV.uv[source * 2];
        alternateUV[vertex * 2 + 1] = frozenEnvironmentUV.uv[source * 2 + 1];
      }
      return { ...sample, alternateUV };
    }
    if (sample.uvSet === 0) return sample;
    const source = renderer.m2.uv2;
    const alternateUV = new Float32Array(vertices.length * 2);
    for (let vertex = 0; vertex < vertices.length; vertex++) {
      const offset = vertices[vertex] * 2;
      const u = source?.[offset],
        v = source?.[offset + 1];
      if (!Number.isFinite(u) || !Number.isFinite(v))
        aurify_fail("incomplete-materials");
      // M2RendererGL.loadSkin flips UV2 before the vertex shader applies its sampled matrix.
      alternateUV[vertex * 2] = u;
      alternateUV[vertex * 2 + 1] = 1 - v;
    }
    return { ...sample, alternateUV };
  };
  for (const [type, material] of chr_materials)
    exporter.addURITexture(type, material.getURI());
  exporter.setGeosetMask(state.chrCustGeosets);
  exporter.setPosedGeometry(baked.vertices, baked.normals);
  const characterExporter = new CharacterExporter(
    active_renderer,
    equipment_model_renderers,
    collection_model_renderers,
    skinned_model_renderers,
  );
  const geometry = characterExporter.get_equipment_geometry(true);
  if (
    geometry.length !==
    [...expectedModels.values()].reduce(
      (sum, count) => sum + count,
      expectedSkinned.size,
    )
  )
    aurify_fail("unsupported-equipment");
  const exportedSkinned = new Set();
  for (const geom of geometry) {
    if (geom.item_id !== null) continue;
    if (
      exportedSkinned.has(geom.slot_id) ||
      skinned_model_renderers.get(geom.slot_id)?.renderer !== geom.renderer
    )
      aurify_fail("missing-game-data");
    exportedSkinned.add(geom.slot_id);
  }
  if (
    exportedSkinned.size !== expectedSkinned.size ||
    [...expectedSkinned.keys()].some((id) => !exportedSkinned.has(id))
  )
    aurify_fail("missing-game-data");
  const choiceTextures = character_appearance.resolve_replaceable_textures(
    activeChoices,
    layout,
  );
  exporter.setEquipmentModels(
    geometry.map((geom) => {
      const textures = [];
      const aurifyTextures = new Map();
      for (const index of usedTextureIndices.get(geom.renderer)) {
        const type = geom.renderer.m2.textureTypes[index];
        let texture;
        if (type === 0) {
          texture = geom.renderer.m2.textures[index].fileDataID;
        } else if (
          type === character_appearance.SKIN_TEXTURE_TYPE ||
          type === character_appearance.SKIN_EXTRA_TEXTURE_TYPE
        ) {
          const compositeType = chr_materials.has(type)
            ? type
            : character_appearance.SKIN_TEXTURE_TYPE;
          if (!chr_materials.has(compositeType))
            aurify_fail("incomplete-materials");
          texture = "data-" + compositeType;
        } else if (geom.item_id === null) {
          texture = choiceTextures.get(type);
        } else if (type >= 11 && type < 14) {
          texture = displays.get(geom.slot_id)?.textures?.[type - 11];
        } else if (type > 1 && type < 5) {
          texture = displays.get(geom.slot_id)?.textures?.[type - 2];
        } else aurify_fail("incomplete-materials");
        if (typeof texture !== "string") {
          if (!aurify_id(texture)) aurify_fail("incomplete-materials");
          if (!textures.includes(texture)) textures.push(texture);
        }
        // Explicit type mapping avoids index-based fallbacks accidentally selecting another texture.
        if (type !== 0) aurifyTextures.set(type, texture);
      }
      return {
        ...geom,
        textures,
        aurifyTextures,
        aurifyRequiredTextureIndices: usedTextureIndices.get(geom.renderer),
      };
    }),
  );
  const manifest = [];
  await exporter.exportAsOBJ(
    objPath,
    false,
    { isCancelled: () => false },
    manifest,
  );
  return { files: manifest, materials: exporter.aurifyMeshMaterials };
}
