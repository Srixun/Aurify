// Replaces the stock desktop startup: no UI modules, updater, collector, armory, or saved characters.
BUILD_RELEASE = true;
(async () => {
  const path = require("path");
  const worker = require(
    path.join(path.dirname(process.execPath), "src", "worker.cjs"),
  );
  await worker.run({
    async initialize(request) {
      const fs = require("fs").promises;
      await fs.mkdir(nw.App.dataPath, { recursive: true });
      const core = require_core();
      const Vue = require_vue_cjs();
      core.view = Vue.reactive(core.makeNewView());
      core.view.$watch = (key, callback, options) =>
        Vue.watch(
          () =>
            key.split(".").reduce((value, part) => value?.[part], core.view),
          callback,
          options,
        );
      const constants = require_constants();
      const generics = require_generics();
      const runtimeSource = path.join(path.dirname(process.execPath), "src");
      const listfilePath = path.join(
        runtimeSource,
        "aurify-listfile-withcapitals.csv",
      );
      const dbdSource = path.join(runtimeSource, "aurify-dbd");
      const dbdEntries = await fs.readdir(dbdSource, { withFileTypes: true });
      if (
        dbdEntries.length !== 27 ||
        dbdEntries.some(
          (entry) => !entry.isFile() || !/^[A-Za-z0-9]+\.dbd$/.test(entry.name),
        )
      )
        throw new Error("missing-game-data");
      const tactSource = path.join(runtimeSource, "aurify-tact-keys.json");
      const tactKeys = JSON.parse(await fs.readFile(tactSource, "utf8"));
      if (
        Object.keys(tactKeys).length === 0 ||
        Object.entries(tactKeys).some(
          ([name, key]) =>
            !/^[a-f0-9]{16}$/i.test(name) || !/^[a-f0-9]{32}$/i.test(key),
        )
      )
        throw new Error("missing-game-data");
      // Read only shipped defaults, never a previous user's wow.export preferences.
      core.view.config = await generics.readJSON(
        constants.CONFIG.DEFAULT_PATH,
        true,
      );
      Object.assign(core.view.config, {
        exportDirectory: request.outputDir,
        exportCharacterFormat: "OBJ",
        exportTextureFormat: "PNG",
        enableSharedTextures: false,
        enableSharedChildren: false,
        enableAbsoluteMTLPaths: false,
        enableAbsoluteGLTFPaths: false,
        exportM2Meta: false,
        exportM2Bones: false,
        exportBLPMeta: false,
        modelsExportTextures: true,
        modelsExportAlpha: true,
        modelsExportUV2: false,
        modelsExportAnimations: false,
        modelsExportCollision: false,
        overwriteFiles: true,
        pathFormat: "posix",
        chrExportApplyPose: true,
        chrIncludeBaseClothing: true,
        chrIsDemonHunter: false,
        cacheExpiry: 0,
        enableBinaryListfile: false,
        listfileBinarySource: null,
        listfileURL: listfilePath,
        listfileFallbackURL: null,
        dbdURL: null,
        dbdFallbackURL: null,
        dbdFilenameURL: null,
        dbdFilenameFallbackURL: null,
        tactKeysURL: null,
        tactKeysFallbackURL: null,
        updateURL: null,
        realmListURL: null,
        armoryURL: null,
        cdnFallbackHosts: "",
      });
      const CASCLocal = require_casc_source_local();
      const casc = new CASCLocal(request.gameRoot);
      // Fail on incomplete local installation rather than silently loading a different CDN build.
      casc.initializeRemoteCASC = async () => {
        throw new Error("missing-game-data");
      };
      await casc.init();
      const matching = casc.builds
        .map((build, index) => ({ build, index }))
        .filter(
          ({ build }) =>
            build.Product === request.client.product &&
            build.Version === request.client.build &&
            build.BuildKey === request.client.buildKey,
        );
      if (matching.length !== 1) throw new Error("client-mismatch");
      // Only the bundled, hash-pinned public key set is loaded.
      await require_tact_keys().load();
      await casc.load(matching[0].index);
      if (
        casc.build.Product !== request.client.product ||
        casc.build.Version !== request.client.build ||
        casc.build.BuildKey !== request.client.buildKey
      )
        throw new Error("client-mismatch");
      const adapter = require_tab_characters().aurify;
      return {
        catalog: () => adapter.catalog(request.client),
        render: (objPath) => adapter.render(core, request, objPath),
      };
    },
  });
})();
