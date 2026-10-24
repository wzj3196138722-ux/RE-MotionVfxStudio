using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MotionVfx;
using VfxStudio.LiveEditor;

if (args.Length == 1 && args[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    return FakeHelper(args[0]);

string fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
string executable = Environment.ProcessPath!;
int checks = 0;
var opts = new Options(fixture, fixture);
using var bridge = new DesktopBridge(opts, executable);
JsonObject Start(string mode, string? text = null) => JsonSerializer.SerializeToNode(bridge.Start(new JsonObject {
    ["mode"] = mode, ["site"] = "bilibili", ["text"] = text, ["exportToken"] = "token-中文"
}))!.AsObject();
string Id(JsonObject result) => result["id"]!.GetValue<string>();
string StatusFile(JsonObject result) => Path.Combine(fixture, result["statusFile"]!.GetValue<string>());
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
void Reject(Action operation, string label)
{
    bool rejected = false;
    try { operation(); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, label);
}
JsonObject Read(string path)
{
    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    return JsonNode.Parse(file)!.AsObject();
}
JsonObject Wait(JsonObject result, string phase)
{
    var watch = Stopwatch.StartNew();
    while (watch.Elapsed.TotalSeconds < 8)
    {
        if (File.Exists(StatusFile(result)))
        {
            var status = Read(StatusFile(result));
            if (status["phase"]?.GetValue<string>() == phase) return status;
        }
        Thread.Sleep(10);
    }
    throw new Exception("Timed out: " + Id(result) + " " + phase);
}
void Finish(JsonObject result) => File.WriteAllText(Path.Combine(fixture, "finish-" + Id(result)), "done");

var recording = Start("record");
Wait(recording, "recording");
var picking = Start("pick_pfb");
Wait(picking, "choosing");
var author = Start("author_link");
Wait(author, "complete");
Check(Read(Path.Combine(fixture, "desktop-recording-status.json"))["id"]!.GetValue<string>() == Id(recording), "author link/file picker preserve recorder status");
Check(Read(StatusFile(picking))["phase"]!.GetValue<string>() == "choosing", "link cannot overwrite pending picker result");
Reject(() => Start("record"), "second recording rejected while first still active");
var refocused = Start("pick_efx");
Check(Id(refocused) == Id(picking) && refocused["mode"]!.GetValue<string>() == "pick_pfb" && refocused["reused"]!.GetValue<bool>(),
    "second file-dialog click reuses and focuses the actual pending picker");
Check(File.Exists(Path.Combine(fixture, "desktop-focus-" + Id(picking) + ".json")), "focus signal belongs only to pending dialog");
var copied = Start("copy");
Wait(copied, "complete");
Check(Read(StatusFile(author))["mode"]!.GetValue<string>() == "author_link", "per-request utility results retained");
var stop = JsonSerializer.SerializeToNode(bridge.Stop())!;
Check(stop["id"]!.GetValue<string>() == Id(recording), "stop targets recording after file/link/copy operations");
Wait(recording, "complete");
Check(!File.Exists(Path.Combine(fixture, "desktop-stop-" + Id(picking) + ".json")), "stop recorder does not stop picker");
Finish(picking);
var picked = Wait(picking, "complete");
Check(picked["files"]![0]!.GetValue<string>().EndsWith("中文.pfb.18"), "PFB selection survives recording completion");
Check(picked["exportToken"]!.GetValue<string>() == "token-中文", "export token contract survives request status routing");
Thread.Sleep(100);
Check(JsonSerializer.SerializeToNode(bridge.Stop())!["requested"]!.GetValue<bool>() == false, "no recording means stop is a no-op");

foreach (string mode in new[] { "pick", "pick_efx", "pick_motion", "export" })
{
    var request = Start(mode);
    Wait(request, "choosing");
    Finish(request);
    Wait(request, "complete");
    Thread.Sleep(100);
    Check(Read(StatusFile(request))["mode"]!.GetValue<string>() == mode, mode + " retains independent result");
}

var crashed = Start("copy", "crash");
Wait(crashed, "running");
Thread.Sleep(150);
Wait(crashed, "error");
Check(true, "unexpected child exit is reported without another UI request");
var cancelled = Start("pick_motion");
Wait(cancelled, "choosing");
var cancelResult = Start("cancel_dialog");
Wait(cancelResult, "complete");
Check(File.Exists(Path.Combine(fixture, "desktop-stop-" + Id(cancelled) + ".json")), "cancel dialog targets the pending picker");
Wait(cancelled, "complete");
Thread.Sleep(100);
string staleId = Guid.NewGuid().ToString("N");
DesktopStatus.Write(fixture, new JsonObject { ["id"] = staleId, ["mode"] = "pick_pfb" }, "choosing", "previous backend");
var staleCancel = Start("cancel_dialog");
Wait(staleCancel, "complete");
Check(Read(Path.Combine(fixture, "desktop-dialog-status.json"))["phase"]!.GetValue<string>() == "cancelled", "cancel clears stale previous-backend choosing state");
Check(Read(Path.Combine(fixture, "desktop-dialog-status.json"))["mode"]!.GetValue<string>() == "pick_pfb", "stale cancellation preserves actual dialog mode");
Check(Read(Path.Combine(fixture, "desktop-recording-status.json"))["id"]!.GetValue<string>() == Id(recording), "stale dialog cancellation preserves recording channel");

// Another bridge instance runs the same fake helper executable in a different mailbox.
// Disposing ours must not stop/kill that process merely because its name matches.
string otherMailbox = Path.Combine(fixture, "other");
Directory.CreateDirectory(otherMailbox);
using var other = new DesktopBridge(new Options(otherMailbox, otherMailbox), executable);
var outsider = JsonSerializer.SerializeToNode(other.Start(new JsonObject { ["mode"] = "record" }))!;
string outsiderId = outsider["id"]!.GetValue<string>();
var outsiderWatch = Stopwatch.StartNew();
string outsiderPidFile = Path.Combine(otherMailbox, "pid-" + outsiderId);
while (!File.Exists(outsiderPidFile) && outsiderWatch.Elapsed.TotalSeconds < 8) Thread.Sleep(10);
using var outsideProcess = Process.GetProcessById(int.Parse(File.ReadAllText(outsiderPidFile)));
var stuckDialog = Start("pick_pfb", "ignore-stop");
Wait(stuckDialog, "choosing");
var ownRecord = Start("record");
Wait(ownRecord, "recording");
int ownDialogPid = int.Parse(File.ReadAllText(Path.Combine(fixture, "pid-" + Id(stuckDialog))));
using var dialogProcess = Process.GetProcessById(ownDialogPid);
bridge.Dispose();
Check(dialogProcess.HasExited, "dispose terminates only its own hung dialog");
Check(Read(StatusFile(stuckDialog))["phase"]!.GetValue<string>() == "cancelled", "terminated dialog receives cancellation status");
Check(Read(StatusFile(ownRecord))["phase"]!.GetValue<string>() == "complete", "dispose allows own recorder to complete cooperatively");
Check(!outsideProcess.HasExited, "unrelated same-name helper stays alive");
Check(!File.Exists(Path.Combine(otherMailbox, "desktop-stop-" + outsiderId + ".json")), "no stop signal written for unrelated recorder");
other.Stop();
Check(outsideProcess.WaitForExit(3000), "unrelated recorder remains controllable by its owner");
Check(Directory.GetFiles(fixture, "*.tmp", SearchOption.AllDirectories).Length == 0, "atomic status writes leave no temporary files");
Console.WriteLine($"Desktop bridge integration: {checks} checks passed. Fixture: {fixture}");
return 0;

static int FakeHelper(string requestPath)
{
    var request = JsonNode.Parse(File.ReadAllText(requestPath))!.AsObject();
    string mailbox = Path.GetDirectoryName(requestPath)!, mode = request["mode"]!.GetValue<string>(), id = request["id"]!.GetValue<string>();
    string? behavior = request["text"]?.GetValue<string>();
    File.WriteAllText(Path.Combine(mailbox, "pid-" + id), Environment.ProcessId.ToString());
    string phase = mode == "record" ? "recording" : DesktopStatus.Channel(mode) == "dialog" ? "choosing" : "running";
    DesktopStatus.Write(mailbox, request, phase, "fake helper ready");
    if (behavior == "crash") return 17;
    if (DesktopStatus.Channel(mode) != "utility")
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed.TotalSeconds < 25 && !File.Exists(Path.Combine(mailbox, "finish-" + id)))
        {
            if (behavior != "ignore-stop" && File.Exists(Path.Combine(mailbox, "desktop-stop-" + id + ".json"))) break;
            Thread.Sleep(20);
        }
    }
    DesktopStatus.Write(mailbox, request, "complete", "fake helper complete",
        files: DesktopStatus.Channel(mode) == "dialog" ? new[] { "C:/测试/中文.pfb.18" } : null,
        replay: mode == "record" ? new { id = "fake-replay", frames = new[] { "frame.jpg" } } : null);
    return 0;
}

namespace VfxStudio.LiveEditor
{
    internal sealed record Options(string Mailbox, string? GameRoot);
    internal static class Host
    {
        internal static void AtomicJson(string path, object body)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(body));
            File.Move(temp, path, true);
        }
    }
}
