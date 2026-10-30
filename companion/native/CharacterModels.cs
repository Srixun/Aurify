using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AurifyClient
{
    internal sealed class ModelFailure : Exception
    {
        internal readonly string Code;
        internal ModelFailure(string code) : base("Character model unavailable: " + code) { Code = code; }
    }

    internal static class ModelProtocol
    {
        internal const int JsonLimit = 4 * 1024 * 1024;
        internal const int ArtifactLimit = 64 * 1024 * 1024;
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal static readonly Regex Hash = new Regex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
        internal static readonly Regex Build = new Regex(@"\A1\.60\.(0|[1-9][0-9]{0,2})\.[1-9][0-9]{3,5}\z", RegexOptions.CultureInvariant);
        internal static void Require(bool valid) { if (!valid) throw new ModelFailure("invalid-model-data"); }
        internal static Dictionary<string, object> Object(object value)
        {
            var result = value as Dictionary<string, object>;
            Require(result != null);
            return result;
        }
        internal static object[] Array(object value, int maximum)
        {
            var result = value as object[];
            Require(result != null && result.Length <= maximum);
            return result;
        }
        internal static Dictionary<string, object> Parse(byte[] bytes)
        {
            Require(bytes.Length <= JsonLimit);
            return Object(new JavaScriptSerializer { MaxJsonLength = JsonLimit, RecursionLimit = 64 }.DeserializeObject(Utf8.GetString(bytes)));
        }
        internal static long Integer(object value, long minimum, long maximum)
        {
            Require(Json.Integer(value, minimum, maximum));
            return Json.Int(value);
        }
        internal static string Text(object value, int maximum)
        {
            string text = value as string;
            Require(!String.IsNullOrWhiteSpace(text) && text.Length <= maximum && !Regex.IsMatch(text, @"[\x00-\x1f\x7f]"));
            return text;
        }
        internal static Dictionary<string, object> Client(object value)
        {
            var row = Object(value);
            string build = Text(Json.Get(row, "build"), 20), product = Text(Json.Get(row, "product"), 32), key = Text(Json.Get(row, "buildKey"), 32);
            Require(Build.IsMatch(build) && (product == "wowf" || product == "wow_classic_beta") && Regex.IsMatch(key, @"\A[0-9a-f]{32}\z"));
            return new Dictionary<string, object> { { "build", build }, { "product", product }, { "buildKey", key } };
        }
        internal static bool SameClient(object value, Dictionary<string, object> expected)
        {
            var actual = Client(value);
            return (string)actual["build"] == (string)expected["build"] && (string)actual["product"] == (string)expected["product"] && (string)actual["buildKey"] == (string)expected["buildKey"];
        }
        internal static string Error(object value)
        {
            string code = value as string;
            switch (code)
            {
                case "unsupported-tabard": case "incomplete-equipment": case "invalid-appearance":
                case "renderer-timeout": case "runtime-invalid": case "unsupported-client":
                case "missing-model": case "missing-texture": case "invalid-model-data":
                case "invalid-request": case "client-mismatch": case "unsupported-equipment":
                case "missing-game-data": case "incomplete-materials": case "invalid-artifact":
                case "renderer-failed": return code;
                default: return "renderer-failed";
            }
        }
        internal static Dictionary<string, object> Job(object value, Dictionary<string, object> client)
        {
            var row = Object(value);
            Integer(Json.Get(row, "schemaVersion"), 1, 1);
            Require(SameClient(Json.Get(row, "client"), client));
            string hash = Text(Json.Get(row, "inputHash"), 64);
            Require(Hash.IsMatch(hash));
            var source = Object(Json.Get(row, "appearance"));
            var appearance = new Dictionary<string, object> {
                { "raceId", Integer(Json.Get(source, "raceId"), 1, Int32.MaxValue) },
                { "bodyType", Integer(Json.Get(source, "bodyType"), 0, 1) },
                { "modelId", Integer(Json.Get(source, "modelId"), 1, Int32.MaxValue) }
            };
            var choices = new List<object>();
            var options = new HashSet<long>();
            foreach (object item in Array(Json.Get(source, "choices"), 128))
            {
                var choice = Object(item);
                long option = Integer(Json.Get(choice, "optionId"), 1, Int32.MaxValue);
                Require(options.Add(option));
                choices.Add(new Dictionary<string, object> { { "optionId", option }, { "choiceId", Integer(Json.Get(choice, "choiceId"), 1, Int32.MaxValue) } });
            }
            appearance["choices"] = choices;
            if (source.ContainsKey("guildTabard"))
            {
                var inputTabard = Object(source["guildTabard"]);
                Require(inputTabard.Count == 5);
                var tabard = Json.New();
                foreach (string field in new[] { "background", "borderStyle", "borderColor", "emblemDesign", "emblemColor" })
                    tabard[field] = Integer(Json.Get(inputTabard, field), 0, Int32.MaxValue);
                appearance["guildTabard"] = tabard;
            }
            var equipment = new List<object>();
            var slots = new HashSet<long>();
            foreach (object item in Array(Json.Get(row, "equipment"), 19))
            {
                var gear = Object(item);
                long slot = Integer(Json.Get(gear, "slot"), 1, 19), id = Integer(Json.Get(gear, "itemId"), 0, Int32.MaxValue);
                Require(slots.Add(slot));
                string itemString = Json.Get(gear, "itemString") as string;
                if (id == 0) Require(itemString == null);
                else
                {
                    itemString = Text(itemString, 2048);
                    Match match = Regex.Match(itemString, @"\Aitem:([0-9]+)(?::[-0-9]*)*\z");
                    long parsed;
                    Require(match.Success && Int64.TryParse(match.Groups[1].Value, out parsed) && parsed == id);
                }
                // Item labels, player identity and icon metadata are neither needed nor sent to the worker.
                equipment.Add(new Dictionary<string, object> { { "slot", slot }, { "itemId", id }, { "itemString", itemString } });
            }
            // Static melee presentation intentionally does not require ranged/relic slot 18.
            foreach (int slot in new[] { 1, 3, 4, 5, 6, 7, 8, 9, 10, 15, 16, 17, 19 }) Require(slots.Contains(slot));
            return new Dictionary<string, object> {
                { "schemaVersion", 1 }, { "inputHash", hash }, { "profileRevision", Integer(Json.Get(row, "profileRevision"), 1, Int32.MaxValue) },
                { "client", client }, { "appearance", appearance }, { "equipment", equipment }
            };
        }
        internal static Dictionary<string, object> Catalog(object value, Dictionary<string, object> client)
        {
            var catalog = Object(value);
            Integer(Json.Get(catalog, "schemaVersion"), 1, 1);
            Require(SameClient(Json.Get(catalog, "client"), client));
            var models = new List<object>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in Array(Json.Get(catalog, "models"), 128))
            {
                var model = Object(item);
                long race = Integer(Json.Get(model, "raceId"), 1, Int32.MaxValue), body = Integer(Json.Get(model, "bodyType"), 0, 1), id = Integer(Json.Get(model, "modelId"), 1, Int32.MaxValue);
                Require(identities.Add(race + ":" + body + ":" + id));
                var options = new List<object>();
                var optionIds = new HashSet<long>();
                foreach (object optionValue in Array(Json.Get(model, "options"), 128))
                {
                    var option = Object(optionValue);
                    long optionId = Integer(Json.Get(option, "id"), 1, Int32.MaxValue);
                    Require(optionIds.Add(optionId) && Json.Get(option, "required") is bool);
                    var choices = new List<object>();
                    var choiceIds = new HashSet<long>();
                    foreach (object choiceValue in Array(Json.Get(option, "choices"), 2048))
                    {
                        var choice = Object(choiceValue);
                        long choiceId = Integer(Json.Get(choice, "id"), 1, Int32.MaxValue);
                        Require(choiceIds.Add(choiceId));
                        choices.Add(new Dictionary<string, object> { { "id", choiceId }, { "label", Text(Json.Get(choice, "label"), 120) } });
                    }
                    Require(choices.Count > 0);
                    options.Add(new Dictionary<string, object> { { "id", optionId }, { "label", Text(Json.Get(option, "label"), 120) }, { "required", option["required"] }, { "choices", choices } });
                }
                models.Add(new Dictionary<string, object> { { "raceId", race }, { "bodyType", body }, { "modelId", id }, { "raceName", Text(Json.Get(model, "raceName"), 120) }, { "bodyName", Text(Json.Get(model, "bodyName"), 120) }, { "options", options } });
            }
            Require(models.Count > 0);
            var result = new Dictionary<string, object> { { "schemaVersion", 1 }, { "client", client }, { "models", models } };
            if (catalog.ContainsKey("guildTabard"))
            {
                var inputTabard = Object(catalog["guildTabard"]);
                Require(inputTabard.Count == 5);
                var tabard = Json.New();
                foreach (string field in new[] { "background", "borderStyle", "borderColor", "emblemDesign", "emblemColor" })
                {
                    var values = new List<object>();
                    var ids = new HashSet<long>();
                    foreach (object choiceValue in Array(Json.Get(inputTabard, field), 2048))
                    {
                        var choice = Object(choiceValue);
                        long id = Integer(Json.Get(choice, "id"), 0, Int32.MaxValue);
                        Require(ids.Add(id));
                        values.Add(new Dictionary<string, object> { { "id", id }, { "label", Text(Json.Get(choice, "label"), 120) } });
                    }
                    tabard[field] = values;
                }
                result["guildTabard"] = tabard;
            }
            return result;
        }
        private static void NoPrivateMetadata(object value, int depth, bool material = false, bool unlitDeclared = false)
        {
            Require(depth <= 64);
            var row = value as Dictionary<string, object>;
            if (row != null)
            {
                if (depth == 0)
                {
                    foreach (string field in new[] { "extensionsUsed", "extensionsRequired" })
                    {
                        if (!row.ContainsKey(field)) continue;
                        object[] extensions = Array(row[field], 1);
                        Require(extensions.Length == 1 && Json.Str(extensions[0]) == "KHR_materials_unlit");
                    }
                    unlitDeclared = row.ContainsKey("extensionsUsed");
                    Require(!row.ContainsKey("extensionsRequired") || unlitDeclared);
                }
                bool unlitUsed = false;
                foreach (var pair in row)
                {
                    if (depth == 0 && (pair.Key == "extensionsUsed" || pair.Key == "extensionsRequired")) continue;
                    if (depth == 0 && pair.Key == "materials")
                    {
                        foreach (object item in Array(pair.Value, 4096))
                        {
                            var definition = Object(item);
                            NoPrivateMetadata(definition, depth + 1, true, unlitDeclared);
                            unlitUsed |= definition.ContainsKey("extensions");
                        }
                        continue;
                    }
                    if (pair.Key == "extensions")
                    {
                        Require(material && unlitDeclared);
                        var extensions = Object(pair.Value);
                        Require(extensions.Count == 1 && extensions.ContainsKey("KHR_materials_unlit") && Object(extensions["KHR_materials_unlit"]).Count == 0);
                        continue;
                    }
                    Require(pair.Key != "uri" && pair.Key != "name" && pair.Key != "extras" && pair.Key != "extensionsUsed" && pair.Key != "extensionsRequired");
                    if (pair.Value is string)
                    {
                        string text = (string)pair.Value;
                        // Exported GLB only permits glTF enum strings and the fixed exporter identifier.
                        switch (pair.Key)
                        {
                            case "version": Require(text == "2.0"); break;
                            case "generator": Require(text == "THREE.GLTFExporter r180"); break;
                            case "type": Require(text == "SCALAR" || text == "VEC2" || text == "VEC3" || text == "VEC4" || text == "MAT4"); break;
                            case "mimeType": Require(text == "image/png"); break;
                            case "alphaMode": Require(text == "OPAQUE" || text == "MASK" || text == "BLEND"); break;
                            default: throw new ModelFailure("invalid-model-data");
                        }
                    }
                    else NoPrivateMetadata(pair.Value, depth + 1, false, unlitDeclared);
                }
                if (depth == 0) Require(unlitDeclared == unlitUsed);
            }
            else
            {
                var array = value as object[];
                if (array != null) foreach (object item in array) NoPrivateMetadata(item, depth + 1, false, unlitDeclared);
                else Require(value == null || value is bool || Json.Numeric(value));
            }
        }
        internal static void Glb(byte[] bytes)
        {
            Require(bytes.Length >= 32 && bytes.Length <= ArtifactLimit && BitConverter.ToUInt32(bytes, 0) == 0x46546c67 && BitConverter.ToUInt32(bytes, 4) == 2 && BitConverter.ToUInt32(bytes, 8) == bytes.Length);
            uint jsonSize = BitConverter.ToUInt32(bytes, 12);
            Require(jsonSize > 0 && jsonSize <= JsonLimit && jsonSize % 4 == 0 && 28L + jsonSize <= bytes.Length && BitConverter.ToUInt32(bytes, 16) == 0x4e4f534a);
            byte[] json = new byte[(int)jsonSize];
            Buffer.BlockCopy(bytes, 20, json, 0, json.Length);
            var document = Parse(json);
            NoPrivateMetadata(document, 0);
            Require(Json.Str(Json.Get(Object(Json.Get(document, "asset")), "version")) == "2.0");
            int binaryHeader = 20 + (int)jsonSize;
            uint binarySize = BitConverter.ToUInt32(bytes, binaryHeader);
            Require(BitConverter.ToUInt32(bytes, binaryHeader + 4) == 0x004e4942 && binarySize % 4 == 0 && binaryHeader + 8L + binarySize == bytes.Length);
            object[] buffers = Array(Json.Get(document, "buffers"), 1);
            Require(buffers.Length == 1);
            long used = Integer(Json.Get(Object(buffers[0]), "byteLength"), 1, binarySize);
            Require(binarySize - used <= 3);
            object[] views = Array(Json.Get(document, "bufferViews"), 65536);
            foreach (object value in views)
            {
                var view = Object(value);
                Integer(Json.Get(view, "buffer"), 0, 0);
                long offset = view.ContainsKey("byteOffset") ? Integer(view["byteOffset"], 0, used) : 0;
                long count = Integer(Json.Get(view, "byteLength"), 1, used);
                Require(offset + count <= used);
            }
            if (document.ContainsKey("images")) foreach (object value in Array(document["images"], 2048))
            {
                var image = Object(value);
                Require(Json.Str(Json.Get(image, "mimeType")) == "image/png");
                int index = (int)Integer(Json.Get(image, "bufferView"), 0, views.Length - 1);
                var view = Object(views[index]);
                long offset = view.ContainsKey("byteOffset") ? Json.Int(view["byteOffset"]) : 0;
                Require(Json.Int(view["byteLength"]) >= 8);
                int start = binaryHeader + 8 + (int)offset;
                byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
                for (int i = 0; i < signature.Length; i++) Require(bytes[start + i] == signature[i]);
            }
            ValidateGeometry(document, views, bytes, binaryHeader + 8);
            Require(!document.ContainsKey("animations") && !document.ContainsKey("skins"));
        }
        private static void ValidateGeometry(Dictionary<string, object> document, object[] views, byte[] bytes, int binaryStart)
        {
            object[] accessors = Array(Json.Get(document, "accessors"), 65536);
            int[] starts = new int[accessors.Length], strides = new int[accessors.Length];
            long[] counts = new long[accessors.Length];
            for (int index = 0; index < accessors.Length; index++)
            {
                var accessor = Object(accessors[index]);
                Require(!accessor.ContainsKey("sparse"));
                var view = Object(views[(int)Integer(Json.Get(accessor, "bufferView"), 0, views.Length - 1)]);
                int component = (int)Integer(Json.Get(accessor, "componentType"), 5120, 5126);
                int width = component == 5120 || component == 5121 ? 1 : component == 5122 || component == 5123 ? 2 : component == 5125 || component == 5126 ? 4 : 0;
                string type = Json.Str(Json.Get(accessor, "type"));
                int components = type == "SCALAR" ? 1 : type == "VEC2" ? 2 : type == "VEC3" ? 3 : type == "VEC4" ? 4 : 0;
                Require(width > 0 && components > 0);
                int element = width * components;
                long offset = accessor.ContainsKey("byteOffset") ? Integer(accessor["byteOffset"], 0, ArtifactLimit) : 0;
                long viewOffset = view.ContainsKey("byteOffset") ? Json.Int(view["byteOffset"]) : 0;
                int stride = view.ContainsKey("byteStride") ? (int)Integer(view["byteStride"], element, 252) : element;
                Require(offset % width == 0 && (viewOffset + offset) % width == 0 && stride % width == 0);
                if (view.ContainsKey("byteStride")) Require(stride % 4 == 0);
                long count = Integer(Json.Get(accessor, "count"), 1, ArtifactLimit / width);
                Require(offset + (count - 1) * stride + element <= Json.Int(view["byteLength"]));
                starts[index] = binaryStart + (int)(viewOffset + offset);
                strides[index] = stride;
                counts[index] = count;
                if (component == 5126)
                {
                    for (long vertex = 0; vertex < count; vertex++)
                        for (int part = 0; part < components; part++)
                        {
                            float number = BitConverter.ToSingle(bytes, starts[index] + (int)vertex * stride + part * width);
                            Require(!Single.IsNaN(number) && !Single.IsInfinity(number));
                        }
                }
            }
            object[] materials = Array(Json.Get(document, "materials"), 4096);
            object[] meshes = Array(Json.Get(document, "meshes"), 4096);
            Require(meshes.Length > 0 && materials.Length > 0);
            foreach (object meshValue in meshes)
            {
                var mesh = Object(meshValue);
                Require(!mesh.ContainsKey("weights"));
                object[] primitives = Array(Json.Get(mesh, "primitives"), 4096);
                Require(primitives.Length > 0);
                foreach (object primitiveValue in primitives)
                {
                    var primitive = Object(primitiveValue);
                    Require(!primitive.ContainsKey("targets"));
                    if (primitive.ContainsKey("mode")) Integer(primitive["mode"], 4, 4);
                    Integer(Json.Get(primitive, "material"), 0, materials.Length - 1);
                    var attributes = Object(Json.Get(primitive, "attributes"));
                    int position = (int)Integer(Json.Get(attributes, "POSITION"), 0, accessors.Length - 1);
                    var positionAccessor = Object(accessors[position]);
                    Require(Json.Str(Json.Get(positionAccessor, "type")) == "VEC3" && Json.Int(Json.Get(positionAccessor, "componentType")) == 5126);
                    foreach (var attribute in attributes)
                    {
                        Require(attribute.Key == "POSITION" || attribute.Key == "NORMAL" || attribute.Key == "TANGENT" || attribute.Key == "TEXCOORD_0" || attribute.Key == "TEXCOORD_1" || attribute.Key == "COLOR_0");
                        int attributeIndex = (int)Integer(attribute.Value, 0, accessors.Length - 1);
                        Require(counts[attributeIndex] == counts[position]);
                    }
                    if (primitive.ContainsKey("indices"))
                    {
                        int index = (int)Integer(primitive["indices"], 0, accessors.Length - 1);
                        var accessor = Object(accessors[index]);
                        int component = (int)Json.Int(Json.Get(accessor, "componentType"));
                        Require(Json.Str(Json.Get(accessor, "type")) == "SCALAR" && (component == 5121 || component == 5123 || component == 5125) && counts[index] % 3 == 0);
                        for (long vertex = 0; vertex < counts[index]; vertex++)
                        {
                            int offset = starts[index] + (int)vertex * strides[index];
                            uint value = component == 5121 ? bytes[offset] : component == 5123 ? BitConverter.ToUInt16(bytes, offset) : BitConverter.ToUInt32(bytes, offset);
                            Require(value < counts[position]);
                        }
                    }
                    else Require(counts[position] % 3 == 0);
                }
            }
            object[] nodes = Array(Json.Get(document, "nodes"), 8192), scenes = Array(Json.Get(document, "scenes"), 1);
            Require(nodes.Length > 0 && scenes.Length == 1);
            if (document.ContainsKey("scene")) Integer(document["scene"], 0, 0);
            var visited = new HashSet<int>();
            foreach (object root in Array(Json.Get(Object(scenes[0]), "nodes"), nodes.Length))
                VisitNode((int)Integer(root, 0, nodes.Length - 1), nodes, meshes.Length, visited, 0);
            Require(visited.Count == nodes.Length);
            object[] images = document.ContainsKey("images") ? Array(document["images"], 2048) : new object[0];
            object[] textures = document.ContainsKey("textures") ? Array(document["textures"], 2048) : new object[0];
            object[] samplers = document.ContainsKey("samplers") ? Array(document["samplers"], 2048) : new object[0];
            foreach (object textureValue in textures)
            {
                var texture = Object(textureValue);
                Integer(Json.Get(texture, "source"), 0, images.Length - 1);
                if (texture.ContainsKey("sampler")) Integer(texture["sampler"], 0, samplers.Length - 1);
            }
            foreach (object materialValue in materials)
            {
                var material = Object(materialValue);
                var pbr = material.ContainsKey("pbrMetallicRoughness") ? Object(material["pbrMetallicRoughness"]) : Json.New();
                foreach (string key in new[] { "baseColorTexture", "metallicRoughnessTexture" })
                    if (pbr.ContainsKey(key)) Integer(Json.Get(Object(pbr[key]), "index"), 0, textures.Length - 1);
                foreach (string key in new[] { "normalTexture", "occlusionTexture", "emissiveTexture" })
                    if (material.ContainsKey(key)) Integer(Json.Get(Object(material[key]), "index"), 0, textures.Length - 1);
            }
        }
        private static void VisitNode(int index, object[] nodes, int meshes, HashSet<int> visited, int depth)
        {
            Require(depth <= 128 && visited.Add(index));
            var node = Object(nodes[index]);
            Require(!node.ContainsKey("skin") && !node.ContainsKey("weights") && !node.ContainsKey("camera"));
            if (node.ContainsKey("mesh")) Integer(node["mesh"], 0, meshes - 1);
            foreach (string key in new[] { "matrix", "translation", "rotation", "scale" })
            {
                if (!node.ContainsKey(key)) continue;
                int length = key == "matrix" ? 16 : key == "rotation" ? 4 : 3;
                object[] values = Array(node[key], length);
                Require(values.Length == length);
                foreach (object value in values) Require(Json.Numeric(value) && !Double.IsNaN(Json.Num(value, Double.NaN)) && !Double.IsInfinity(Json.Num(value)));
            }
            if (node.ContainsKey("matrix")) Require(!node.ContainsKey("translation") && !node.ContainsKey("rotation") && !node.ContainsKey("scale"));
            if (node.ContainsKey("children")) foreach (object child in Array(node["children"], nodes.Length))
                VisitNode((int)Integer(child, 0, nodes.Length - 1), nodes, meshes, visited, depth + 1);
        }
    }

    internal sealed partial class PortalClient
    {
        private const string ModelsRoute = "/members/api/ingest/character-models/";
        private async Task<Dictionary<string, object>> ModelRequestAsync(HttpMethod method, string path, HttpContent content, CancellationToken ct)
        {
            if (String.IsNullOrEmpty(token)) throw new PortalException("This companion is not paired", 401);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var request = new HttpRequestMessage(method, new Uri(new Uri(ServerUrl), ModelsRoute + path)))
            {
                deadline.CancelAfter(120000);
                request.Content = content;
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                using (deadline.Token.Register(delegate { response.Dispose(); }))
                {
                    if (!response.IsSuccessStatusCode) throw new PortalException("Character model request was rejected", (int)response.StatusCode);
                    ModelProtocol.Require(response.Content.Headers.ContentEncoding.Count == 0 && (!response.Content.Headers.ContentLength.HasValue || response.Content.Headers.ContentLength <= ModelProtocol.JsonLimit));
                    using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new MemoryStream())
                    {
                        byte[] buffer = new byte[32768];
                        for (;;)
                        {
                            int count = await input.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false);
                            if (count == 0) break;
                            ModelProtocol.Require(output.Length + count <= ModelProtocol.JsonLimit);
                            output.Write(buffer, 0, count);
                        }
                        return ModelProtocol.Parse(output.ToArray());
                    }
                }
            }
        }
        private static HttpContent ModelJson(object value)
        {
            byte[] bytes = ModelProtocol.Utf8.GetBytes(Json.Stringify(value));
            ModelProtocol.Require(bytes.Length <= ModelProtocol.JsonLimit);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return content;
        }
        internal Task<Dictionary<string, object>> ModelWorkAsync(Dictionary<string, object> identity, CancellationToken ct)
        { return ModelRequestAsync(HttpMethod.Post, "work", ModelJson(identity), ct); }
        internal async Task ModelCatalogAsync(Dictionary<string, object> catalog, CancellationToken ct)
        { ModelProtocol.Require(Json.Bool(Json.Get(await ModelRequestAsync(HttpMethod.Post, "catalog", ModelJson(catalog), ct).ConfigureAwait(false), "ok"))); }
        private static string ModelPath(string observation, string hash)
        {
            ModelProtocol.Require(observation != null && ClientStorage.Uuid.IsMatch(observation) && hash != null && ModelProtocol.Hash.IsMatch(hash));
            return "characters/" + observation + "/" + hash;
        }
        internal async Task ModelUploadAsync(string observation, string hash, byte[] model, CancellationToken ct)
        {
            ModelProtocol.Glb(model);
            var content = new ByteArrayContent(model);
            content.Headers.ContentType = new MediaTypeHeaderValue("model/gltf-binary");
            ModelProtocol.Require(Json.Bool(Json.Get(await ModelRequestAsync(HttpMethod.Put, ModelPath(observation, hash), content, ct).ConfigureAwait(false), "ok")));
        }
        internal async Task ModelErrorAsync(string observation, string hash, string code, CancellationToken ct)
        { ModelProtocol.Require(Json.Bool(Json.Get(await ModelRequestAsync(HttpMethod.Post, ModelPath(observation, hash) + "/error", ModelJson(new Dictionary<string, object> { { "code", ModelProtocol.Error(code) } }), ct).ConfigureAwait(false), "ok"))); }
        internal async Task ModelRuntimeAsync(Stream output, long expected, CancellationToken ct)
        {
            if (String.IsNullOrEmpty(token)) throw new PortalException("This companion is not paired", 401);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(ServerUrl), ModelsRoute + "runtime")))
            {
                deadline.CancelAfter(300000);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                using (deadline.Token.Register(delegate { response.Dispose(); }))
                {
                    if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count != 0 || (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength != expected)) throw new ModelFailure("runtime-invalid");
                    using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        byte[] buffer = new byte[65536];
                        long total = 0;
                        for (;;)
                        {
                            int count = await input.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false);
                            if (count == 0) break;
                            total += count;
                            if (total > expected) throw new ModelFailure("runtime-invalid");
                            await output.WriteAsync(buffer, 0, count, deadline.Token).ConfigureAwait(false);
                        }
                        if (total != expected) throw new ModelFailure("runtime-invalid");
                    }
                }
            }
        }
    }

    internal sealed class CharacterModelSynchronizer
    {
        private readonly string root;
        private readonly PortalClient portal;
        private readonly Action<string, string> report;
        internal CharacterModelSynchronizer(string root, PortalClient portal, Action<string, string> report = null)
        {
            this.root = root; this.portal = portal; this.report = report ?? delegate { };
        }
        internal static Dictionary<string, object> InstalledClient(GameInstallation installation, out string gameRoot)
        {
            gameRoot = null;
            string build, selectedProduct;
            using (var guard = NativeGamePaths.LockDirectory(installation.GameDir))
            using (var executable = NativeGamePaths.OpenRead(installation.Executable))
            {
                build = FileVersionInfo.GetVersionInfo(installation.Executable).FileVersion;
                string flavorPath = Path.Combine(installation.GameDir, ".flavor.info");
                if (!File.Exists(flavorPath)) throw new ModelFailure("unsupported-client");
                string[] flavor = ModelProtocol.Utf8.GetString(NativeGamePaths.ReadFile(flavorPath, 4096)).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (flavor.Length != 2 || flavor[0].TrimStart('\ufeff') != "Product Flavor!STRING:0"
                    || (flavor[1] != "wowf" && flavor[1] != "wow_classic_beta")) throw new ModelFailure("unsupported-client");
                selectedProduct = flavor[1];
            }
            if (build == null || !ModelProtocol.Build.IsMatch(build)) throw new ModelFailure("unsupported-client");
            Dictionary<string, object> found = null;
            foreach (string candidate in new[] { installation.GameDir, Path.GetDirectoryName(installation.GameDir) })
            {
                if (String.IsNullOrEmpty(candidate) || !File.Exists(Path.Combine(candidate, ".build.info"))) continue;
                using (var guard = NativeGamePaths.LockDirectory(candidate))
                {
                    string[] lines = ModelProtocol.Utf8.GetString(NativeGamePaths.ReadFile(Path.Combine(candidate, ".build.info"), 1024 * 1024)).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length < 2) continue;
                    string[] names = lines[0].TrimStart('\ufeff').Split('|');
                    int product = -1, version = -1, key = -1;
                    for (int index = 0; index < names.Length; index++)
                    {
                        string name = names[index].Split('!')[0];
                        if (name == "Product") product = index;
                        if (name == "Version") version = index;
                        if (name == "Build Key") key = index;
                    }
                    if (product < 0 || version < 0 || key < 0) continue;
                    for (int index = 1; index < lines.Length; index++)
                    {
                        string[] fields = lines[index].Split('|');
                        if (fields.Length != names.Length || fields[version] != build || fields[product] != selectedProduct) continue;
                        var identity = ModelProtocol.Client(new Dictionary<string, object> { { "build", build }, { "product", fields[product] }, { "buildKey", fields[key] } });
                        using (var dataGuard = NativeGamePaths.LockDirectory(Path.Combine(candidate, "Data"))) { }
                        if (found != null && (!ModelProtocol.SameClient(identity, found) || gameRoot != candidate)) throw new ModelFailure("unsupported-client");
                        found = identity;
                        gameRoot = candidate;
                    }
                }
            }
            if (found == null) throw new ModelFailure("unsupported-client");
            return found;
        }
        internal async Task ScanAsync(GameInstallation installation, CancellationToken ct)
        {
            string gameRoot;
            var identity = InstalledClient(installation, out gameRoot);
            var work = await portal.ModelWorkAsync(identity, ct).ConfigureAwait(false);
            ModelProtocol.Integer(Json.Get(work, "schemaVersion"), 1, 1);
            ModelProtocol.Require(Json.Get(work, "needsCatalog") is bool);
            object[] jobs = ModelProtocol.Array(Json.Get(work, "jobs"), 10);
            if (!Json.Bool(work["needsCatalog"]) && jobs.Length == 0) { report("model-idle", null); return; }
            if (Json.Bool(work["needsCatalog"]))
            {
                report("model-catalog", null);
                using (var runtime = await ModelRuntime.OpenAsync(root, portal, ct).ConfigureAwait(false))
                {
                    var result = await runtime.RunAsync(gameRoot, identity, null, ct).ConfigureAwait(false);
                    await portal.ModelCatalogAsync(ModelProtocol.Catalog(Json.Get(result, "catalog"), identity), ct).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                work = await portal.ModelWorkAsync(identity, ct).ConfigureAwait(false);
                ModelProtocol.Integer(Json.Get(work, "schemaVersion"), 1, 1);
                ModelProtocol.Require(Json.Get(work, "needsCatalog") is bool && !Json.Bool(work["needsCatalog"]));
                jobs = ModelProtocol.Array(Json.Get(work, "jobs"), 10);
                if (jobs.Length == 0) { report("model-idle", null); return; }
            }
            // One expensive job per pass; the server queue is the checkpoint. Nothing is
            // considered complete until upload acknowledgement, and stale inputs get 409.
            var source = ModelProtocol.Object(jobs[0]);
            string observation = ModelProtocol.Text(Json.Get(source, "observationId"), 36), hash = ModelProtocol.Text(Json.Get(source, "inputHash"), 64);
            ModelProtocol.Require(ClientStorage.Uuid.IsMatch(observation) && ModelProtocol.Hash.IsMatch(hash));
            string errorCode = null;
            try
            {
                var job = ModelProtocol.Job(source, identity);
                report("model-rendering", null);
                using (var runtime = await ModelRuntime.OpenAsync(root, portal, ct).ConfigureAwait(false))
                {
                    var result = await runtime.RunAsync(gameRoot, identity, job, ct).ConfigureAwait(false);
                    ModelProtocol.Require(Json.Str(Json.Get(result, "inputHash")) == hash && ModelProtocol.SameClient(Json.Get(result, "client"), identity) && Json.Str(Json.Get(result, "modelFile")) == "model.glb");
                    byte[] model = NativeGamePaths.ReadFile(Path.Combine(runtime.OutputDirectory, "model.glb"), ModelProtocol.ArtifactLimit);
                    ModelProtocol.Require(ModelProtocol.Integer(Json.Get(result, "bytes"), 1, ModelProtocol.ArtifactLimit) == model.Length && Json.Str(Json.Get(result, "sha256")) == Json.Hash(model));
                    await portal.ModelUploadAsync(observation, hash, model, ct).ConfigureAwait(false);
                    report("model-uploaded", null);
                }
            }
            catch (Exception error)
            {
                ct.ThrowIfCancellationRequested();
                var rejected = error as PortalException;
                if (rejected != null && (rejected.Status == 401 || rejected.Status == 403 || rejected.Status == 409)) return;
                var failure = error as ModelFailure;
                errorCode = failure == null ? "renderer-failed" : failure.Code;
            }
            if (errorCode != null)
            {
                await portal.ModelErrorAsync(observation, hash, errorCode, ct).ConfigureAwait(false);
                report("model-error", errorCode);
            }
        }
    }
}
