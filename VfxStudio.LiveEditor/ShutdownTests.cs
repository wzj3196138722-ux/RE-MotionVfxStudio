using System.Text.Json.Nodes;

namespace VfxStudio.LiveEditor;

internal static class ShutdownTests
{
    internal static void Run(Options options, string output, Action<bool, string> check)
    {
        string sample = Path.Combine(options.Assets, "Samples", "11_it02_000.efx.5571972");
        using var engine = new EditorEngine(options with { Mailbox = Path.Combine(output, "shutdown-mailbox") });
        JsonObject Request(string action, JsonObject? arguments = null)
        {
            var request = arguments ?? new(); request["action"] = action; request["id"] = Guid.NewGuid().ToString("N");
            return engine.Handle(request);
        }
        var opened = Request("open", new() { ["path"] = sample });
        check(opened["ok"]!.GetValue<bool>(), "Shutdown regression opens its source fixture");
        long savedRevision = opened["revision"]!.GetValue<long>();
        var edited = Request("set", new()
        {
            ["revision"] = savedRevision, ["target"] = new JsonObject { ["kind"] = "entry", ["index"] = 0 },
            ["path"] = "name", ["value"] = "edit_after_checkpoint"
        });
        check(edited["ok"]!.GetValue<bool>() && edited["revision"]!.GetValue<long>() > savedRevision,
            "Shutdown regression simulates an edit after the checkpoint revision was read");
        long currentRevision = edited["revision"]!.GetValue<long>();
        string document = edited["document"]!.ToJsonString();
        var stale = Request("shutdown", new() { ["revision"] = savedRevision });
        check(!stale["ok"]!.GetValue<bool>() && !engine.ExitRequested &&
            stale["revision"]!.GetValue<long>() == currentRevision && stale["document"]!.ToJsonString() == document,
            "Shutdown with a stale checkpoint revision preserves later edits and keeps the backend running");
        var missing = Request("shutdown", new() { ["revision"] = null });
        check(!missing["ok"]!.GetValue<bool>() && !engine.ExitRequested && missing["document"]!.ToJsonString() == document,
            "A present but null shutdown revision cannot bypass the revision guard");
        var fractional = Request("shutdown", new() { ["revision"] = 1.5 });
        check(!fractional["ok"]!.GetValue<bool>() && !engine.ExitRequested,
            "Malformed shutdown revisions are rejected before requesting process exit");
        var current = Request("shutdown", new() { ["revision"] = currentRevision });
        check(current["ok"]!.GetValue<bool>() && engine.ExitRequested && current["document"]!.ToJsonString() == document,
            "Shutdown at the current revision succeeds without modifying the document");
        using var legacy = new EditorEngine(options with { Mailbox = Path.Combine(output, "legacy-shutdown-mailbox") });
        var compatible = legacy.Handle(new JsonObject { ["id"] = "legacy-shutdown", ["action"] = "shutdown" });
        check(compatible["ok"]!.GetValue<bool>() && legacy.ExitRequested,
            "Legacy shutdown requests that omit revision retain their existing behavior");
        Host.AtomicJson(Path.Combine(output, "shutdown-revision-guard.json"), new
        {
            staleRevision = savedRevision, currentRevision, staleShutdownRejected = true,
            editedDocumentPreserved = true, currentShutdownAccepted = true, legacyNoRevisionCompatible = true
        });
    }
}
