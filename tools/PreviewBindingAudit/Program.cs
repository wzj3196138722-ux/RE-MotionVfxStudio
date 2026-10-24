using System.Security.Cryptography;
using System.Text.Json;
using VfxStudio.App.UI;
using VfxStudio.Core.Services;

if (args.Length != 2) throw new ArgumentException("Usage: PreviewBindingAudit <source-sidecar> <evidence-directory>");
string source = Path.GetFullPath(args[0]);
string evidenceDir = Path.GetFullPath(args[1]);
Directory.CreateDirectory(evidenceDir);
string testDir = Path.Combine(evidenceDir, "store-audit-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"));
Directory.CreateDirectory(testDir);
string copy = Path.Combine(testDir, "preview_debug_settings.json");
string originalHash = Hash(source);
File.Copy(source, copy);
var tests = new List<string>();
var store = PreviewDebugStore.Load(copy);
Require(store.CanSave, "Source configuration parses");
Require(store.BackupPath != null && Hash(store.BackupPath) == originalHash, "Legacy byte-for-byte backup before migration");
var originalIds = store.Effects.Select(e => e.Id).ToArray();
int originalNotes = store.Notes.Count;
var assetHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var catalogs = new Dictionary<string, PreviewMotionDescriptor[]>();
var diagnostics = new List<string>();
var manager = new MotionVfxSlotManager();
foreach (var context in store.Effects.Cast<PreviewMotionReference>().Concat(store.Notes)
    .GroupBy(e => (e.MotlistPath, e.WeaponCode)))
{
    var (path, weapon) = context.Key;
    assetHashes[path] = Hash(path);
    var motions = manager.GetMotions(manager.LoadMotlist(path), weapon)
        .Select(m => new PreviewMotionDescriptor(m.Index, m.MotId, m.Name, m.FrameCount)).ToArray();
    catalogs[path] = motions;
    var reconciliation = store.ReconcileMotionCatalog(path, weapon, motions);
    diagnostics.AddRange(reconciliation.Diagnostics);
    Require(context.All(e => e.IdentityVersion == 1), "All current source identities migrate: " + weapon);
    foreach (var binding in context.OfType<PreviewEffectBinding>())
    {
        var descriptor = motions.Single(m => PreviewDebugStore.MatchesMotion(path, weapon, m, binding));
        Require(store.GetBindings(path, weapon, descriptor, motions).Contains(binding), "Current binding resolves: " + binding.Id);
    }
}
Require(store.Effects.Select(e => e.Id).SequenceEqual(originalIds) && store.Notes.Count == originalNotes,
    "All original binding IDs and notes retained");
store.Save(copy);
var migrated = PreviewDebugStore.Load(copy);
Require(migrated.BackupPath == null && migrated.SchemaVersion == 2,
    "Restart loads migrated schema without repeated backup");
Require(migrated.Effects.Select(e => e.Id).SequenceEqual(originalIds) && migrated.Notes.Count == originalNotes,
    "Restart preserves current bindings and notes");
foreach (var entry in migrated.Effects)
{
    var reordered = catalogs[entry.MotlistPath].Reverse().Select((m, i) => m with { Index = i + 1000 }).ToArray();
    var descriptor = reordered.Single(m => PreviewDebugStore.MatchesMotion(entry.MotlistPath, entry.WeaponCode, m, entry));
    Require(migrated.GetBindings(entry.MotlistPath, entry.WeaponCode, descriptor, reordered).Contains(entry),
        "List reorder retains identity: " + entry.Id);
}

string sourceList = Path.Combine(testDir, "synthetic.motlist.992");
string missingEfx = Path.Combine(testDir, "missing.efx.5571972");
var catalog = new[] {
    new PreviewMotionDescriptor(0, 7, "demon_on", 69.75f),
    new PreviewMotionDescriptor(1, 7, "demon_off", 60.5f),
    new PreviewMotionDescriptor(2, 8, "duplicate_name", 60.5f, "clip-a"),
    new PreviewMotionDescriptor(3, 8, "duplicate_name", 60.5f, "clip-b")
};
var isolated = new PreviewDebugStore();
var first = isolated.CreateBinding(sourceList, "wp02", catalog[0], catalog, missingEfx);
var second = isolated.CreateBinding(sourceList, "wp02", catalog[0], catalog, missingEfx);
var differentName = isolated.CreateBinding(sourceList, "wp02", catalog[1], catalog, missingEfx);
var discriminatorA = isolated.CreateBinding(sourceList, "wp02", catalog[2], catalog, missingEfx);
var discriminatorB = isolated.CreateBinding(sourceList, "wp02", catalog[3], catalog, missingEfx);
Require(first.Id != second.Id && isolated.GetBindings(sourceList, "wp02", catalog[0], catalog).Count == 2,
    "Repeated same file has independent binding IDs");
Require(isolated.GetBindings(sourceList, "wp02", catalog[1], catalog).Single() == differentName,
    "Repeated MotionId disambiguated by source name");
Require(isolated.GetBindings(sourceList, "wp02", catalog[2], catalog).Single() == discriminatorA &&
    isolated.GetBindings(sourceList, "wp02", catalog[3], catalog).Single() == discriminatorB,
    "Repeated MotionId and name disambiguated by source discriminator");
Require(isolated.GetBindings(sourceList, "wp03", catalog[0], catalog).Count == 0 &&
    isolated.GetBindings(sourceList + ".other", "wp02", catalog[0], catalog).Count == 0,
    "Weapon and source contexts do not cross-bind");
Require(isolated.GetBindings(sourceList, "wp02", catalog[0], catalog).Count == 2,
    "Missing resource retains both tracks");

var ambiguous = new[] { catalog[0], catalog[0] with { Index = 90 } };
Require(isolated.GetBindings(sourceList, "wp02", catalog[0], ambiguous).Count == 0,
    "Unresolved identical source identities never silently bind by list index");
bool refused = false;
try { isolated.CreateBinding(sourceList, "wp02", catalog[0], ambiguous, missingEfx); }
catch (InvalidOperationException) { refused = true; }
Require(refused, "New ambiguous binding is refused");

first.StartFrame = 1.125f;
first.EndFrame = 69.625f;
first.Enabled = false;
first.MountBoneName = "R_Hand_HJ_01";
first.DisplayName = "独立轨道 A";
first.EndPolicy = PreviewEffectEndPolicy.NaturalEnd;
isolated.PreviewEffectsMuted = true;
isolated.PreviewTracksCollapsed = true;
isolated.NativeTracksCollapsed = true;
isolated.TimelineHeight = 333.25f;
isolated.SetNote(sourceList, "wp02", catalog[0], catalog, "保留备注");
string independent = Path.Combine(testDir, "independent.json");
isolated.Save(independent);
var restarted = PreviewDebugStore.Load(independent);
var restored = restarted.Effects.Single(e => e.Id == first.Id);
Require(restored.StartFrame == 1.125f && restored.EndFrame == 69.625f && !restored.Enabled &&
    restored.MountBoneName == "R_Hand_HJ_01" && restored.DisplayName == "独立轨道 A" &&
    restored.EndPolicy == PreviewEffectEndPolicy.NaturalEnd && restarted.PreviewEffectsMuted &&
    restarted.PreviewTracksCollapsed && restarted.NativeTracksCollapsed && restarted.TimelineHeight == 333.25f,
    "Restart preserves fractions, state, mount, end-policy intent, and layout");
Require(restarted.GetNote(sourceList, "wp02", catalog[0] with { Index = 99 }, catalog)?.Text == "保留备注",
    "Notes share stable identity and survive reorder");
var shorter = catalog.Select(m => m == catalog[0] ? m with { FrameCount = 30.375f } : m).ToArray();
var result = restarted.ReconcileMotionCatalog(sourceList, "wp02", shorter);
Require(result.Changed && restored.StartFrame == 1.125f && restored.EndFrame == 30.375f &&
    restored.FrameRangeWarning != null, "Shortened motion clamps out-of-range endpoint without rounding fractions");
Require(PreviewDebugStore.ClampFrameRange(-5, float.NaN, 7.5f) == new PreviewFrameRange(0, 7.5f, true,
    PreviewDebugStore.ClampFrameRange(-5, float.NaN, 7.5f).Message), "Negative and non-finite frame bounds corrected");
var reversed = PreviewDebugStore.ClampFrameRange(8.25f, 2.75f, 10.5f);
Require(reversed.StartFrame == 8.25f && reversed.EndFrame == 8.25f,
    "Reversed range corrected without integer snapping");
Require(PreviewDebugStore.ClampFrameRange(0, 1, 0).EndFrame == 0,
    "Zero-duration motion is supported without invented frame");

string invalid = Path.Combine(testDir, "invalid.json");
File.WriteAllText(invalid, "{not-json}");
var blocked = PreviewDebugStore.Load(invalid);
bool saveRefused = false;
try { blocked.Save(invalid); } catch (InvalidOperationException) { saveRefused = true; }
Require(!blocked.CanSave && saveRefused && File.ReadAllText(invalid) == "{not-json}",
    "Malformed configuration cannot be overwritten by empty defaults");
Require(Hash(source) == originalHash && assetHashes.All(p => Hash(p.Key) == p.Value),
    "User sidecar and all read source MOTLIST files unchanged");

var report = new {
    utc = DateTime.UtcNow, verification = "Offline data migration and serialization; not UI or game rendering",
    source, sourceSha256 = originalHash, originalBindingCount = originalIds.Length, originalNoteCount = originalNotes,
    bindingIds = originalIds, legacyBackup = store.BackupPath, testDirectory = testDir,
    assetHashes, passedCount = tests.Count, passed = tests, diagnostics,
    limitations = new[] { "NaturalEnd is persisted intent, not a claim of playback support.",
        "Identical MotionId/name/discriminator is retained as unresolved; no index fallback after migration.",
        "No source sidecar or game files were modified by this audit." }
};
string reportPath = Path.Combine(evidenceDir, "preview-binding-store-audit.json");
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {tests.Count} assertions; {originalIds.Length} original bindings and {originalNotes} notes. Evidence: {reportPath}");

void Require(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    tests.Add(name);
}
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
