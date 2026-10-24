using System.Text.Json;
using System.Text.Json.Nodes;

namespace MotionVfx;

// Each request owns a durable result. Channel snapshots keep the recorder visible
// while a file picker/browser is active; the legacy snapshot is compatibility only.
internal static class DesktopStatus
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static string Channel(string mode) => mode switch
    {
        "record" => "recording",
        "pick" or "pick_efx" or "pick_motion" or "pick_pfb" or "export" => "dialog",
        "copy" or "author_link" or "open_replay_folder" or "cancel_dialog" => "utility",
        _ => throw new ArgumentException("不支持的桌面操作。")
    };
    internal static string FileName(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("无效的桌面操作编号。");
        return "desktop-status-" + id + ".json";
    }
    internal static void Write(string mailbox, JsonObject request, string phase, string message,
        object? files = null, object? replay = null, object? destinations = null)
    {
        string id = request["id"]!.GetValue<string>(), mode = request["mode"]!.GetValue<string>();
        string channel = Channel(mode);
        var body = new { id, mode, channel, phase, message, files, replay, destinations,
            exportToken = request["exportToken"]?.GetValue<string>(), updatedUtc = DateTime.UtcNow };
        Atomic(Path.Combine(mailbox, FileName(id)), body);
        Atomic(Path.Combine(mailbox, "desktop-" + channel + "-status.json"), body);
        Atomic(Path.Combine(mailbox, "desktop-status.json"), body);
    }
    internal static bool IsTerminal(string mailbox, string id)
    {
        try
        {
            var status = JsonNode.Parse(File.ReadAllText(Path.Combine(mailbox, FileName(id))));
            return status?["phase"]?.GetValue<string>() is "complete" or "cancelled" or "error";
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
    }
    private static void Atomic(string path, object value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, path, true); break; }
                catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
                { Thread.Sleep(20); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
