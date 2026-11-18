using System.Text.Json;
using System.Text.Json.Nodes;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Basic;
using ReeLib.Efx.Structs.Main;

namespace VfxStudio.LiveEditor;

internal sealed partial class EditorEngine
{
    private int? offlinePreviewEntry;
    private int offlinePreviewEntryId;
    private JsonObject? offlinePreviewManifest;
    private long offlinePreviewRevision = -1;
    private string? offlinePreviewDocument;

    private void ConfigureOfflinePreview(JsonObject request)
    {
        var file = Require();
        if (file.Header.Version != EfxVersion.RE4) throw new NotSupportedException("离线预览当前仅支持 RE4 EFX。");
        int position;
        if (request["entryPosition"] != null) position = Int(request, "entryPosition");
        else
        {
            int id = Int(request, "entryId");
            var candidates = file.Entries.Select((entry, index) => (entry, index)).Where(x => x.entry.index == id).ToArray();
            if (candidates.Length != 1) throw new ArgumentException("无法唯一定位所选发射器，请指定 entryPosition。");
            position = candidates[0].index;
        }
        if (position < 0 || position >= file.Entries.Count) throw new ArgumentOutOfRangeException(nameof(request), "entry 位置超出范围。");
        offlinePreviewEntry = position;
        offlinePreviewEntryId = file.Entries[position].index;
        offlinePreviewDocument = documentSessionKey;
        offlinePreviewRevision = -1;
    }

    // Every top-level entry can be the edited one. Editing does not depend on drawing: the window draws the entries
    // its renderer supports and lists the others as not drawn, but their attributes stay editable.

    private void AttachOfflinePreview(JsonObject response, bool force)
    {
        if (offlinePreviewEntry == null) return;
        if (offlinePreviewDocument != documentSessionKey)
        {
            offlinePreviewEntry = null;
            offlinePreviewManifest = null;
            return;
        }
        try
        {
            var entries=Require().Entries;
            if(offlinePreviewEntry.Value>=entries.Count || entries[offlinePreviewEntry.Value].index!=offlinePreviewEntryId)
            {
                // Entries added, removed or restored before the target shift its position: follow its ID.
                var moved=entries.Select((entry,index)=>(entry,index)).Where(x=>x.entry.index==offlinePreviewEntryId).ToArray();
                if(moved.Length>1) throw new InvalidOperationException("预览目标已变化，请重新选择发射器。");
                // The target itself is gone (for example undoing the paste or duplicate that created it):
                // continue with the entry nearest to where it was.
                int fallback=moved.Length==1?moved[0].index:entries.Count==0?-1:Math.Clamp(offlinePreviewEntry.Value,0,entries.Count-1);
                if(fallback<0) throw new InvalidOperationException("编辑目标已删除，文件里没有其他 entry。");
                offlinePreviewEntry=fallback;
                offlinePreviewEntryId=entries[fallback].index;
                offlinePreviewRevision=-1;
            }
            var selectedEntry = entries[offlinePreviewEntry.Value];
            var billboard = selectedEntry.Attributes.OfType<EFXAttributeTypeBillboard3D>().SingleOrDefault();
            var life = selectedEntry.Attributes.OfType<EFXAttributeLife>().SingleOrDefault();
            // The dedicated colour/size form covers Billboard3D only; other types use the generic form.
            if (billboard != null) response["offlinePreviewParameters"] = JsonSerializer.SerializeToNode(new
            {
                entryPosition = offlinePreviewEntry.Value,
                billboardAttribute = selectedEntry.Attributes.IndexOf(billboard),
                lifeAttribute = life == null ? -1 : selectedEntry.Attributes.IndexOf(life),
                color = new[] { billboard.Color.R, billboard.Color.G, billboard.Color.B, billboard.Color.A },
                colorRange = new[] { billboard.ColorRange.R, billboard.ColorRange.G, billboard.ColorRange.B, billboard.ColorRange.A },
                colorRate = billboard.ColorRate,
                sizeScalar = new[] { billboard.SizeScalar.center, billboard.SizeScalar.spread },
                sizeX = new[] { billboard.SizeX.center, billboard.SizeX.spread },
                sizeY = new[] { billboard.SizeY.center, billboard.SizeY.spread },
                appear = life == null ? null : new[] { life.AppearFrame.min, life.AppearFrame.max },
                keep = life == null ? null : new[] { life.KeepFrame.min, life.KeepFrame.max },
                vanish = life == null ? null : new[] { life.VanishFrame.min, life.VanishFrame.max }
            }, Host.Json);
            if (force || offlinePreviewRevision != revision)
            {
                var file = Require();
                int position = offlinePreviewEntry.Value;
                if (position >= file.Entries.Count) throw new InvalidOperationException("所选 entry 已变化，请重新选择。");
                // Immutable revision files. Publish the manifest only after full EFX readback succeeds.
                byte[] content = Validate(file, source!);
                using var reopened = service.Load(content, source!);
                var selected = reopened.Entries[position];
                string directory = Path.Combine(options.Mailbox, "offline-preview");
                string path = Path.Combine(directory, $"revision_{revision}_{Guid.NewGuid():N}.efx.{(int)file.Header.Version}");
                WriteNewFile(path, content);
                var manifest = JsonSerializer.SerializeToNode(new
                {
                    schema = 1, documentId = Hash(System.Text.Encoding.UTF8.GetBytes(documentSessionKey ?? source!)),
                    revision, path, sha256 = Hash(content), entryPosition = position, entryId = selected.index,
                    offset = selected.Start, source, restart = true
                }, Host.Json)!.AsObject();
                Host.AtomicJson(Path.Combine(directory, "preview.json"), manifest);
                offlinePreviewManifest = manifest;
                offlinePreviewRevision = revision;
            }
            response["offlinePreview"] = offlinePreviewManifest?.DeepClone();
        }
        catch (Exception ex)
        {
            // A committed edit stays committed if publication fails. Retrying publication must
            // not apply the edit twice or consume another undo step.
            response["offlinePreview"] = offlinePreviewManifest?.DeepClone();
            response["offlinePreviewError"] = ex.GetBaseException().Message;
        }
    }
}
