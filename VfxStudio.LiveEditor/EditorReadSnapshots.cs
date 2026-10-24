using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReeLib;

namespace VfxStudio.LiveEditor;

internal sealed partial class EditorEngine
{
    private const int MaximumInspectionSnapshots = 32;
    private const int MaximumInspectionSnapshotBytes = 8 * 1024 * 1024;
    private const int MaximumSingleInspectionBytes = 4 * 1024 * 1024;
    private readonly Dictionary<string, (JsonObject Snapshot, int Bytes)> inspectionSnapshots = new(StringComparer.Ordinal);
    private EfxFile? inspectionDocument;
    private long inspectionRevision = -1;
    private int inspectionSnapshotBytes;
    private DocumentReadKey? documentReadKey;
    private JsonNode? documentReadSnapshot;

    // Document identity handles failed edits that restore a fresh model without
    // increasing revision. SavedHash and NotesRevision can change without an EFX edit.
    private sealed record DocumentReadKey(EfxFile Document, long Revision, byte[]? Bytes, string? SavedHash,
        string? Source, string SourceKind, CacheSourceDescriptor? CacheSource, JsonObject? GameSource,
        int UndoCount, int RedoCount, string? Warning, bool CanRestore, long NotesRevision);

    private void ClearInspectionSnapshots()
    {
        inspectionSnapshots.Clear(); inspectionSnapshotBytes = 0;
        inspectionDocument = document; inspectionRevision = revision;
    }

    private void WriteInspectionSnapshot(Target target, JsonObject response)
    {
        // Validate every incoming target before consulting the cache: malformed
        // or stale embedded paths must never accidentally hit another object.
        var selected = Resolve(target);
        var version = ResolveTargetFile(target).Header.Version;
        if (!ReferenceEquals(inspectionDocument, document) || inspectionRevision != revision) ClearInspectionSnapshots();
        string key = JsonSerializer.Serialize(target, Host.Json);
        if (!inspectionSnapshots.TryGetValue(key, out var cached))
        {
            var analysis = ContentInspector.Analyze(selected, version);
            var snapshot = JsonSerializer.SerializeToNode(new
            {
                fields = analysis.Fields.Select(x => x.Description),
                curves = analysis.Curves.Select(CurveEditor.Inspect),
                expressionCurves = ExpressionInspector.Inspect(selected),
                colorGroups = ColorEditor.Inspect(selected, analysis.Fields)
            }, Host.Json)!.AsObject();
            // Cache JSON descriptions only. No setters, mutable EFX models or curve
            // references escape the one inspection walk into the cache.
            int size = Encoding.UTF8.GetByteCount(snapshot.ToJsonString(Host.Json));
            cached = (snapshot, size);
            if (size <= MaximumSingleInspectionBytes)
            {
                while (inspectionSnapshots.Count >= MaximumInspectionSnapshots || inspectionSnapshotBytes + size > MaximumInspectionSnapshotBytes)
                {
                    string first = inspectionSnapshots.Keys.First();
                    inspectionSnapshotBytes -= inspectionSnapshots[first].Bytes;
                    inspectionSnapshots.Remove(first);
                }
                inspectionSnapshots.Add(key, cached); inspectionSnapshotBytes += size;
            }
        }
        foreach (string field in new[] { "fields", "curves", "expressionCurves", "colorGroups" })
            response[field] = cached.Snapshot[field]!.DeepClone();
    }

    private JsonNode? DocumentInfo()
    {
        if (document == null)
        {
            documentReadKey = null; documentReadSnapshot = null;
            if (inspectionDocument != null) ClearInspectionSnapshots();
            return null;
        }
        var key = new DocumentReadKey(document, revision, bytes, savedHash, source, sourceKind, cacheSource, gameSource,
            undo.Count, redo.Count, warning, baseline != null, groupNotes.Revision);
        if (documentReadSnapshot == null || documentReadKey != key)
        {
            documentReadSnapshot = BuildDocumentInfo();
            documentReadKey = key;
        }
        // Every response owns its JSON tree. A consumer or response-cache clone
        // cannot mutate the reusable snapshot or another request's response.
        return documentReadSnapshot!.DeepClone();
    }
}
