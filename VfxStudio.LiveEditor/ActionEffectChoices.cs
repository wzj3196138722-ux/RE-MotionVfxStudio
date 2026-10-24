using System.Text.Json.Nodes;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

internal sealed partial class EditorEngine
{
    private object ActionEffectGroups(JsonObject request)
    {
        string resource = ResourceResolver.Normalize(Text(request, "resourcePath"));
        if (Precache.List().Length > 0)
        {
            var paths = Precache.Materialize();
            if (paths.TryGetValue(resource, out string? actual)) resource = actual;
        }
        var snapshot = new GameInstallResourceReader(options).ReadActionResource(resource, "efx");
        var efx = new EfxEditorService().Load(snapshot.Content, resource + ".5571972");
        try
        {
            return new { resourcePath = resource, groups = efx.EffectGroups.Select(g => g.groupName).Distinct(StringComparer.Ordinal).ToArray() };
        }
        finally { efx.FileHandler.Dispose(); }
    }
}
