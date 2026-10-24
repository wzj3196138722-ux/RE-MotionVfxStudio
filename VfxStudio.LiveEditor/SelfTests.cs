using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Common;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

internal static class SelfTests
{
    public static int Run(Options options)
    {
        string output = Path.Combine(options.Output, "self-test-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(output);
        var passed = new List<string>();
        var failures = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); passed.Add(name); }
        try
        {
            var candidates = Directory.EnumerateFiles(options.Assets, "*.efx.5571972", SearchOption.AllDirectories).OrderBy(p => p).ToArray();
            Check(candidates.Length > 0, "Real Wilds EFX fixtures are available");
            string sample = candidates.FirstOrDefault(p => p.Contains(Path.DirectorySeparatorChar + "Samples" + Path.DirectorySeparatorChar)) ?? candidates[0];
            byte[] sourceBytes = File.ReadAllBytes(sample);
            var testOptions = options with { Mailbox = Path.Combine(output, "mailbox"), Output = Path.Combine(output, "saved"), GameRoot = Path.Combine(output, "mock-game"), SelfTest = false };
            Directory.CreateDirectory(testOptions.GameRoot!);
            using var engine = new EditorEngine(testOptions);
            long revision = 0; int counter = 0;
            JsonObject Request(string action, object? data = null, bool success = true)
            {
                var request = data == null ? new JsonObject() : JsonSerializer.SerializeToNode(data, Host.Json)!.AsObject();
                request["id"] = "test-" + ++counter; request["action"] = action;
                request["revision"] = revision;
                var result = engine.Handle(request);
                Check(result["ok"]!.GetValue<bool>() == success, action + " response " + counter + (success ? "" : " rejects invalid operation") + (success && result["error"] != null ? ": " + result["error"] : ""));
                revision = result["revision"]?.GetValue<long>() ?? revision;
                return result;
            }
            JsonObject Target(int entry, int? attribute = null) => new() { ["kind"] = "entry", ["index"] = entry, ["attributeIndex"] = attribute };
            JsonArray Fields(JsonObject target) => Request("inspect", new { target })["fields"]!.AsArray();
            string Value(JsonObject target, string path) => Fields(target).Single(f => f!["path"]!.GetValue<string>() == path)!["value"]!.GetValue<string>();

            // A captured logical resource is not interchangeable with a same-named fixture.
            string resolverAssets = Path.Combine(output, "resolver-assets");
            var resolverOptions = testOptions with { Assets = resolverAssets };
            const string logicalResource = "Art/VFX/EffectEditor/Weapon/it02/resolver_fixture.efx";
            string fixtureName = "resolver_fixture.efx.5571972";
            string flatA = Path.Combine(resolverAssets, "Samples", fixtureName);
            Directory.CreateDirectory(Path.GetDirectoryName(flatA)!); File.WriteAllBytes(flatA, sourceBytes);
            var flatOnly = ResourceResolver.Resolve(resolverOptions, logicalResource);
            Check(flatOnly.ResolvedPath == null && flatOnly.Candidates.Length == 1 && flatOnly.Candidates[0].Match == "basename", "A unique basename does not prove a captured logical path");
            string flatB = Path.Combine(resolverAssets, "WeaponBases", "wp02", fixtureName);
            Directory.CreateDirectory(Path.GetDirectoryName(flatB)!); File.WriteAllBytes(flatB, sourceBytes);
            Check(ResourceResolver.Resolve(resolverOptions, logicalResource).Candidates.Length == 2, "Resolver retains every same-name source for explicit selection");
            string exactA = Path.Combine(resolverAssets, "Original", "natives", "stm", logicalResource.Replace('/', Path.DirectorySeparatorChar) + ".5571972");
            Directory.CreateDirectory(Path.GetDirectoryName(exactA)!); File.WriteAllBytes(exactA, sourceBytes);
            Check(ResourceResolver.Resolve(resolverOptions, "@natives\\stm\\" + logicalResource + ".5571972").ResolvedPath == exactA, "Full engine path resolves exactly with prefix and version normalization");
            string exactB = Path.Combine(resolverAssets, "Alternative", logicalResource.Replace('/', Path.DirectorySeparatorChar) + ".5571972");
            Directory.CreateDirectory(Path.GetDirectoryName(exactB)!); File.WriteAllBytes(exactB, sourceBytes);
            Check(ResourceResolver.Resolve(resolverOptions, logicalResource).ResolvedPath == null, "Multiple exact project copies require a user choice");
            string gameFixture = Path.Combine(testOptions.GameRoot!, "natives", "STM", logicalResource.Replace('/', Path.DirectorySeparatorChar) + ".5571972");
            Directory.CreateDirectory(Path.GetDirectoryName(gameFixture)!); File.WriteAllBytes(gameFixture, sourceBytes);
            var gameResolved = ResourceResolver.Resolve(resolverOptions, logicalResource);
            Check(gameResolved.ResolvedPath == gameFixture && gameResolved.Candidates.Length == 5, "Current game loose resource takes precedence without hiding alternatives");
            Check(ResourceResolver.Resolve(resolverOptions, "Art/VFX/absent.efx").Candidates.Length == 0, "Missing resources remain unresolved");
            foreach (string invalid in new[] { "../escape.efx", "Art/../escape.efx", "C:/absolute.efx", "Art/*.efx", "Art//empty.efx" })
            {
                bool rejected = false;
                try { ResourceResolver.Normalize(invalid); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Resource resolver rejects invalid path: " + invalid);
            }

            var opened = Request("open", new { path = sample });
            int originalCount = opened["document"]!["entries"]!.AsArray().Count;
            Check(originalCount > 0, "Real file contains entries");
            var rootTarget = Target(0);
            string oldName = Value(rootTarget, "name");
            Request("set", new { target = rootTarget, path = "name", value = "实时编辑_测试_汉字" });
            Check(Value(rootTarget, "name") == "实时编辑_测试_汉字", "Unicode entry name survives serialization and reopen");
            long beforeResolve = revision;
            Request("resolve_resource", new { resourcePath = logicalResource, sourceKind = "local_file" });
            Check(revision == beforeResolve && Value(rootTarget, "name") == "实时编辑_测试_汉字", "Resolving a captured path preserves the open document and unsaved changes");
            Request("undo"); Check(Value(rootTarget, "name") == oldName, "Undo restores the original bytes");
            Request("redo"); Check(Value(rootTarget, "name") == "实时编辑_测试_汉字", "Redo restores the edited bytes");

            var stale = engine.Handle(new JsonObject { ["id"] = "stale", ["action"] = "remove_entry", ["index"] = 0, ["revision"] = revision - 1 });
            Check(stale["ok"]!.GetValue<bool>() == false, "Stale revisions are rejected");
            var noRevision = engine.Handle(new JsonObject { ["id"] = "no-revision", ["action"] = "remove_entry", ["index"] = 0 });
            Check(!noRevision["ok"]!.GetValue<bool>(), "Mutations require an explicit revision");
            var duplicateRequest = new JsonObject { ["id"] = "duplicate-id", ["action"] = "duplicate_entry", ["index"] = 0, ["revision"] = revision };
            var firstDuplicate = engine.Handle(duplicateRequest);
            Check(firstDuplicate["ok"]!.GetValue<bool>(), "Entry duplication serializes successfully");
            revision = firstDuplicate["revision"]!.GetValue<long>();
            var replay = engine.Handle((JsonObject)duplicateRequest.DeepClone());
            Check(replay.ToJsonString() == firstDuplicate.ToJsonString(), "Duplicate request is returned from cache without reapplying");
            duplicateRequest["index"] = 1;
            Check(!engine.Handle(duplicateRequest)["ok"]!.GetValue<bool>(), "Reused request id with different payload is rejected");
            Check(firstDuplicate["document"]!["entries"]!.AsArray().Count == originalCount + 1, "Duplicate adds exactly one entry");
            Request("remove_entry", new { index = originalCount });
            Request("add_entry", new { name = "New empty editor block" });
            Request("add_attribute", new { target = Target(originalCount), type = "Life" });
            var lifeTarget = Target(originalCount, 0);
            var scalar = Fields(lifeTarget).First(f => f!["editable"]!.GetValue<bool>() && f["type"]!.GetValue<string>() == "System.Int32")!;
            string scalarPath = scalar["path"]!.GetValue<string>();
            Request("set", new { target = lifeTarget, path = scalarPath, value = "73" });
            Check(Value(lifeTarget, scalarPath) == "73", "Nested struct field is written through its boxed parent and survives reopening");
            Request("set", new { target = lifeTarget, path = "UniqueID", value = "99999" }, false);
            Request("set", new { target = lifeTarget, path = scalarPath, value = "2147483648" }, false);
            Check(Value(lifeTarget, scalarPath) == "73", "Invalid integer edits preserve the previous document");
            Request("add_attribute", new { target = Target(originalCount), type = "Life" }, false);
            Request("remove_attribute", new { target = lifeTarget });
            Request("undo");
            Check(Fields(lifeTarget).Any(f => f!["path"]!.GetValue<string>() == scalarPath), "Undo restores a removed attribute");
            string saved = Request("save")["outputPath"]!.GetValue<string>();
            Check(File.Exists(saved), "Save writes a new versioned EFX file");
            Request("save", new { path = sample }, false);
            Request("save", new { path = saved }, false);
            Check(File.ReadAllBytes(sample).AsSpan().SequenceEqual(sourceBytes), "Imported source remains byte-identical");
            var publish1 = Request("publish"); var publish2 = Request("publish");
            Check(publish1["resourcePath"]!.GetValue<string>() != publish2["resourcePath"]!.GetValue<string>(), "Every preview publish uses a new game resource path");
            Check(File.ReadAllBytes(publish2["outputPath"]!.GetValue<string>()).AsSpan().SequenceEqual(File.ReadAllBytes(saved)), "Published preview bytes equal the verified saved document");
            var service = new EfxEditorService();
            var persisted = service.Load(saved);
            try
            {
                var duplicate = persisted.Entries[0];
                Check(persisted.Entries.Count == originalCount + 1, "Saved entry structure reopens correctly");
                Check(persisted.Entries[^1].Attributes.Count == 1, "Saved attribute structure reopens correctly");
            }
            finally { persisted.FileHandler.Dispose(); }

            // Exercise the manual writer adapters independently of whichever fields a fixture happens to contain.
            var clip = new EfxClipData { clips = [new EfxClipHeader { frameCount = 1, valueType = ClipValueType.Int }], frames = [new EfxClipFrame()], interpolationData = [new EfxClipInterpolationTangents()] };
            var clipFields = ContentInspector.Describe(clip, EfxVersion.MHWilds);
            clipFields.Single(f => f.Description.Path == "frames[0].IntValue").Set!(12345);
            Check(clip.frames[0].IntValue == 12345, "Integer curve keys bypass the Core no-op IntValue setter");
            clipFields.Single(f => f.Description.Path == "interpolationData[0].out_x").Set!(1.25f);
            Check(clip.interpolationData[0].out_x == 1.25f, "Bezier tangent array structs write back");
            Check(!clipFields.Single(f => f.Description.Path == "clipCount").Description.Editable, "Derived curve counts are read-only");
            var bits = new BitSet(64);
            ContentInspector.Describe(bits, EfxVersion.MHWilds).Single(f => f.Description.Path == "Bits[1]").Set!(unchecked((int)0x80000001));
            Check(bits.HasBit(32) && bits.HasBit(63), "BitSet storage exposes and preserves every bit");
            var boundedBits = new BitSet(6);
            bool invalidBitRejected = false;
            try { ContentInspector.Describe(boundedBits, EfxVersion.MHWilds).Single(f => f.Description.Path == "Bits[0]").Set!(1 << 8); }
            catch (ArgumentException) { invalidBitRejected = true; }
            Check(invalidBitRejected, "BitSet edits reject bits outside the declared storage domain");
            bool bezierRejected = false;
            try { clipFields.Single(f => f.Description.Path == "frames[0].type").Set!(FrameInterpolationType.Bezier); }
            catch (InvalidOperationException) { bezierRejected = true; }
            Check(bezierRejected, "Interpolation edits cannot introduce a missing tangent sequence");
            var material = new MdfProperty(EfxVersion.MHWilds) { parameterType = MaterialParameterType.Texture, texturePath = "old.tex" };
            ContentInspector.Describe(material, EfxVersion.MHWilds).Single(f => f.Description.Path == "texturePath").Set!("new.tex");
            Check(material.texturePath == "new.tex", "Manually written material texture path is editable");
            var floatMaterial = new MdfProperty(EfxVersion.MHWilds) { parameterType = MaterialParameterType.Float };
            ContentInspector.Describe(floatMaterial, EfxVersion.MHWilds).Single(f => f.Description.Path == "VectorValue.X").Set!(0.75f);
            Check(floatMaterial.VectorValue.X == 0.75f, "Material vector property writes to its serialized raw bytes");

            CurveTests.Run(options, output, Check);
            EntryStructureTests.Run(options, Check);
            ExpressionInspectorTests.Run(options, output, Check);
            CacheSourceTests.Run(options, output, Check);
            GameInstallSourceTests.Run(options, output, Check);
            GroupEditingTests.Run(options, output, Check);
            DeletionTests.Run(options, output, Check);
            PlayEfxSerializationTests.Run(options, output, Check);
            EmbeddedActionTests.Run(options, output, Check);
            GroupNoteTests.Run(options, output, Check);
            ColorEditorTests.Run(options, output, Check);
            ParameterGroupTests.Run(options, output, Check);
            LocalFileTests.Run(options, output, Check);
            SessionDocumentTests.Run(options, output, Check);
            ShutdownTests.Run(options, output, Check);
            PrecacheTests.Run(options, output, Check);

            // Inspect every distinct real file, recording concrete unsupported formats instead of concealing them.
            var hashes = new HashSet<string>();
            var coverage = new SortedDictionary<string, SortedDictionary<string, FieldDescription>>(StringComparer.Ordinal);
            int parsedFiles = 0, totalAttributes = 0, totalFields = 0, editableFields = 0;
            var unsupported = new List<object>();
            var adapterChecks = new Dictionary<string, object>();
            var adapterAttempts = new Dictionary<string, int>();
            var adapters = new Dictionary<string, Func<EditableField, bool>>
            {
                ["curve_float"] = f => f.Description.Path.EndsWith("frames[0].FloatValue", StringComparison.Ordinal),
                ["curve_integer"] = f => f.Description.Path.EndsWith("frames[0].IntValue", StringComparison.Ordinal),
                ["material_texture"] = f => f.Description.Path.EndsWith("texturePath", StringComparison.Ordinal),
                ["material_vector"] = f => f.Description.Path.EndsWith("VectorValue.X", StringComparison.Ordinal),
                ["bitset"] = f => f.Description.Path.EndsWith("Bits[0]", StringComparison.Ordinal),
                ["nested_effect"] = f => f.Description.Path.StartsWith("efxrData.", StringComparison.Ordinal) && f.Description.Path.EndsWith(".name", StringComparison.Ordinal)
            };
            foreach (string path in candidates)
            {
                byte[] raw = File.ReadAllBytes(path);
                if (!hashes.Add(Convert.ToHexString(SHA256.HashData(raw)))) continue;
                EfxFile? file = null;
                try
                {
                    file = service.Load(raw, path);
                    var attributes = file.GetAttributesAndActions(true).ToArray();
                    for (int attributeIndex = 0; attributeIndex < attributes.Length; attributeIndex++)
                    {
                        var attribute = attributes[attributeIndex];
                        totalAttributes++;
                        string type = attribute.GetType().FullName!;
                        if (!coverage.TryGetValue(type, out var fields)) coverage[type] = fields = new(StringComparer.Ordinal);
                        var description = ContentInspector.Describe(attribute, file.Header.Version);
                        foreach (var field in description)
                        {
                            totalFields++; if (field.Description.Editable) editableFields++;
                            string normalized = Regex.Replace(field.Description.Path, @"\[\d+\]", "[]");
                            fields.TryAdd(normalized, field.Description with { Path = normalized, Label = normalized });
                        }
                        foreach (var adapter in adapters)
                        {
                            if (adapterChecks.ContainsKey(adapter.Key) || adapterAttempts.GetValueOrDefault(adapter.Key) >= 20) continue;
                            var field = description.FirstOrDefault(f => f.Description.Editable && adapter.Value(f));
                            if (field == null) continue;
                            adapterAttempts[adapter.Key] = adapterAttempts.GetValueOrDefault(adapter.Key) + 1;
                            var copy = service.Load(raw, path);
                            try
                            {
                                var actual = ContentInspector.Describe(copy.GetAttributesAndActions(true).ElementAt(attributeIndex), copy.Header.Version).Single(f => f.Description.Path == field.Description.Path);
                                string entered = actual.ValueType == typeof(string) ? (adapter.Key == "material_texture" ? "Art/VFX/editor_fixture.tex" : "nested_editor_fixture")
                                    : actual.ValueType == typeof(float) ? "0.125" : adapter.Key == "bitset" ? (int.Parse(actual.Description.Value) ^ 1).ToString() : "12345";
                                object? value = ContentInspector.Parse(entered, actual.ValueType);
                                actual.Set!(value);
                                byte[] modified = service.SaveToBytes(copy);
                                var reopenedAdapter = service.Load(modified, path);
                                try
                                {
                                    var after = ContentInspector.Describe(reopenedAdapter.GetAttributesAndActions(true).ElementAt(attributeIndex), reopenedAdapter.Header.Version).Single(f => f.Description.Path == actual.Description.Path);
                                    if (after.Description.Value != ContentInspector.Format(value)) continue;
                                    if (!modified.AsSpan().SequenceEqual(service.SaveToBytes(reopenedAdapter))) continue;
                                    string artifact = Path.Combine(output, "manual-" + adapter.Key + ".efx.5571972");
                                    File.WriteAllBytes(artifact, modified);
                                    adapterChecks.Add(adapter.Key, new { source = path, attribute = type, field = actual.Description.Path, value = entered, artifact });
                                }
                                finally { reopenedAdapter.FileHandler.Dispose(); }
                            }
                            catch (Exception) { /* Try another real instance; the final required check fails if no instance can round-trip. */ }
                            finally { copy.FileHandler.Dispose(); }
                        }
                    }
                    byte[] canonical = service.SaveToBytes(file);
                    var reopened = service.Load(canonical, path);
                    try { if (!canonical.AsSpan().SequenceEqual(service.SaveToBytes(reopened))) throw new InvalidDataException("Unstable serializer round trip"); }
                    finally { reopened.FileHandler.Dispose(); }
                    parsedFiles++;
                }
                catch (Exception ex) { unsupported.Add(new { path, error = ex.GetBaseException().Message }); }
                finally { file?.FileHandler.Dispose(); }
            }
            Check(parsedFiles > 0 && totalAttributes > 0 && totalFields > 0, "Real EFX corpus inspection produced concrete editable fields");
            foreach (string adapter in adapters.Keys) Check(adapterChecks.ContainsKey(adapter), "Real EFX " + adapter + " field edits survive save and reopen");
            Host.AtomicJson(Path.Combine(output, "field-coverage.json"), new { uniqueFiles = hashes.Count, parsedFiles, totalAttributes, totalFields, editableFields, attributeTypes = coverage.Count, unsupported, fieldsByType = coverage });
            Host.AtomicJson(Path.Combine(output, "results.json"), new { ok = true, version = Host.Version, at = DateTimeOffset.UtcNow, passed = passed.Count, tests = passed, uniqueFiles = hashes.Count, parsedFiles, unsupportedFiles = unsupported.Count, attributeTypes = coverage.Count, totalFields, editableFields, adapterChecks, saved, liveGameTested = false });
            Console.WriteLine($"PASS: {passed.Count} checks; {parsedFiles}/{hashes.Count} unique real EFX files; {coverage.Count} attribute types; {editableFields}/{totalFields} editable field observations. Unsupported: {unsupported.Count}. Report: {output}");
            return 0;
        }
        catch (Exception ex)
        {
            failures.Add(ex.ToString());
            Host.AtomicJson(Path.Combine(output, "results.json"), new { ok = false, passed = passed.Count, tests = passed, failures });
            Console.Error.WriteLine(ex); Console.Error.WriteLine("Report: " + output); return 1;
        }
    }
}
