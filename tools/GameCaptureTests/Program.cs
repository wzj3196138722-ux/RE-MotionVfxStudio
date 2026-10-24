using System.Diagnostics;
using System.Text.Json;
using MotionVfx;

int checks = 0;
void Check(bool pass, string label) { if (!pass) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
void Reject(Action action, string label)
{
    bool rejected = false;
    try { action(); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, label);
}
var console = new GameCaptureWindow.Candidate(1, 100, "ConsoleWindowClass", "Monster Hunter Wilds", true, 1920, 1080);
var render = console with { Handle = 2, ClassName = "via" };
var outsider = render with { Handle = 3, ProcessId = 200 };
var dialog = console with { Handle = 4, ClassName = "#32770" };
var hidden = render with { Handle = 5, Visible = false };
var tiny = render with { Handle = 6, Width = 20 };
var allowed = new HashSet<int> { 100 };
Check(GameCaptureWindow.Choose([console, render, outsider, dialog, hidden, tiny], allowed, 1) == render,
    "render selected when a same-process console is the main/foreground window");
Check(GameCaptureWindow.Choose([console, dialog], allowed, 1) == null, "console and native dialogs never become capture targets");
Check(GameCaptureWindow.Choose([outsider], allowed, 3) == null, "another process cannot pass by game title/class");
Check(GameCaptureWindow.Choose([hidden, tiny], allowed, 5) == null, "hidden and invalid-size render windows rejected");
var second = render with { Handle = 7, Title = "Extra surface" };
Check(GameCaptureWindow.Choose([render, second], allowed, 7) == second, "foreground eligible game surface preferred");
Check(GameCaptureWindow.Choose([render, second], allowed, 3) == render, "known game title preferred if another app is foreground");
Check(GameCaptureWindow.MatchesRoot(@"D:\游戏\荒野\MonsterHunterWilds.exe", @"d:\游戏\荒野\"), "full root matching supports Unicode and case differences");
Check(!GameCaptureWindow.MatchesRoot(@"D:\游戏\荒野-old\MonsterHunterWilds.exe", @"D:\游戏\荒野"), "sibling game directory rejected");
Check(!GameCaptureWindow.MatchesRoot(@"D:\游戏\荒野\other.exe", @"D:\游戏\荒野"), "different executable rejected");
Check(!GameCaptureWindow.MatchesRoot(null, @"D:\游戏\荒野"), "unreadable executable is not trusted");
Check(GameCaptureWindow.ExecutablePath(Environment.ProcessId) == Environment.ProcessPath, "query-limited process path reads actual executable");

string directory = Path.Combine(AppContext.BaseDirectory, "recording-中文");
var frames = new[] { new ReplayFrame("MotionVfxLive/replays/test/frame_00000.jpg", 3.2),
    new ReplayFrame("MotionVfxLive/replays/test/frame_00001.jpg", 3.25),
    new ReplayFrame("MotionVfxLive/replays/test/frame_00002.jpg", 3.4) };
Reject(() => ReplayManifest.Create("empty", 20, 1280, 720, directory, []), "zero frames cannot produce a successful recording");
Reject(() => ReplayManifest.Create("bad-size", 20, 0, 720, directory, frames), "empty screenshot dimensions rejected");
Reject(() => ReplayManifest.Create("bad-clock", 20, 1280, 720, directory, [frames[1], frames[0]]), "backwards frame timestamps rejected");
Reject(() => ReplayManifest.Create("bad-clock", 20, 1280, 720, directory, [new ReplayFrame("frame.jpg", double.NaN)]), "nonfinite timestamps rejected");
var replay = ReplayManifest.Create("test", 20, 1280, 720, directory, frames);
Check(replay.Frames[0].Time == 0 && Math.Abs(replay.Frames[2].Time - .2) < .000001, "initial waiting time removed and variable frame intervals retained");
Check(Math.Abs(replay.Duration - .25) < .000001, "duration ends at last captured frame rather than a later stop/wait");
Check(Path.IsPathFullyQualified(replay.OutputDirectory) && replay.ManifestPath == Path.Combine(replay.OutputDirectory, "replay.json"), "saved replay includes usable absolute folder and manifest paths");
var json = JsonSerializer.SerializeToElement(replay, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
Check(json.GetProperty("frames")[0].GetProperty("path").GetString() == frames[0].Path && json.GetProperty("frames")[0].GetProperty("time").GetDouble() == 0,
    "manifest retains lowercase frame path/time contract required by Lua replay");
Check(json.GetProperty("outputDirectory").GetString() == directory && json.GetProperty("manifestPath").GetString() == replay.ManifestPath,
    "output paths survive serialization for user display/open-folder controls");
Check(Math.Abs(ReplayManifest.Create("single", 20, 1280, 720, directory, [frames[0]]).Duration - .05) < .000001,
    "single-frame manual stop remains valid and has a finite playback duration");
Console.WriteLine($"Game capture regression checks: {checks} passed; no screenshots, game input, or game writes performed.");
