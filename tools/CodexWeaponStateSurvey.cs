using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

// Records only the game's named hunter weapon state effect IDs. It never changes game state.
public class CodexWeaponStateSurvey {
    static readonly string DirectoryPath = Path.Combine(Environment.GetEnvironmentVariable("MOTIONVFX_GAME_ROOT") ?? AppContext.BaseDirectory, "reframework/data");
    const string IdFile = DirectoryPath + @"\codex_weapon_state_ids.tsv";
    const string SessionFile = DirectoryPath + @"\codex_weapon_session.txt";
    const string Output = DirectoryPath + @"\codex_weapon_state_survey.tsv";
    static readonly Dictionary<uint, string> Names = new();
    static readonly ConcurrentDictionary<uint, long> LastLoopSeen = new();
    static readonly ConcurrentQueue<string> Pending = new();
    static readonly List<MethodHook> Hooks = new();
    static string CurrentSession = "unmarked";
    static long LastSessionCheck;
    static long LastProbe;

    [PluginEntryPoint]
    public static void Main() {
        try {
            foreach (string line in File.ReadAllLines(IdFile)) {
                string[] parts = line.Split('\t');
                if (parts.Length == 3 && uint.TryParse(parts[0], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out uint id)) Names[id] = parts[1] + "\t" + parts[2];
            }
            Pending.Enqueue("READY\t" + DateTime.Now.ToString("O") + "\tids=" + Names.Count);
            Install("playEffect(app.EffectID_Common.ID, via.GameObject, app.cEffectOverwriteParams, System.UInt32, via.GameObject)", "PLAY");
            Install("playEffectLoop(app.EffectID_Common.ID, System.UInt64, via.GameObject, app.cEffectOverwriteParams, System.UInt32, via.GameObject)", "LOOP");
            InstallMotion();
            InstallProbe();
        } catch (Exception e) { Pending.Enqueue("ERROR\t" + e); }
    }

    static void InstallProbe() {
        try {
            var method = TDB.Get().FindType("app.Weapon")?.FindMethod("get_WpType()");
            if (method == null) { Pending.Enqueue("HOOK_MISSING\tPROBE"); return; }
            var hook = method.AddHook(false);
            hook.AddPre(args => {
                long now = DateTime.UtcNow.Ticks;
                if (now - LastProbe > TimeSpan.TicksPerSecond * 5) {
                    LastProbe = now;
                    Pending.Enqueue("PROBE\t" + DateTime.Now.ToString("O") + "\t" + CurrentSession);
                }
                return PreHookResult.Continue;
            });
            Hooks.Add(hook);
            Pending.Enqueue("HOOK_READY\tPROBE");
        } catch (Exception e) { Pending.Enqueue("HOOK_ERROR\tPROBE\t" + e.Message); }
    }

    static void InstallMotion() {
        try {
            var method = TDB.Get().FindType("via.effect.script.ObjectEffectManagerBase")?.FindMethod(
                "callEventMotionSequenceCreateEffect(via.effect.script.EffectID, via.effect.script.CreatedEffectContainer, via.effect.script.ObjectEffectManagerBase.CreateMotionSequenceEffectEventInfo.CallSequenceType)");
            if (method == null) { Pending.Enqueue("HOOK_MISSING\tMOTION"); return; }
            var hook = method.AddHook(false);
            hook.AddPre(args => {
                if (CurrentSession == "unmarked" || args.Length < 3 || args[2] == 0)
                    return PreHookResult.Continue;
                try {
                    var bytes = new byte[32];
                    Marshal.Copy(new IntPtr((long)args[2]), bytes, 0, bytes.Length);
                    Pending.Enqueue(DateTime.Now.ToString("O") + "\tMOTION\t" + CurrentSession +
                        "\t0x" + args[2].ToString("X") + "\t" + BitConverter.ToString(bytes));
                } catch (Exception e) { Pending.Enqueue("MOTION_DECODE_ERROR\t" + e.Message); }
                return PreHookResult.Continue;
            });
            Hooks.Add(hook);
            Pending.Enqueue("HOOK_READY\tMOTION");
        } catch (Exception e) { Pending.Enqueue("HOOK_ERROR\tMOTION\t" + e.Message); }
    }

    static void Install(string signature, string mode) {
        try {
            var method = TDB.Get().FindType("app.EPVExpertCommon")?.FindMethod(signature);
            if (method == null) { Pending.Enqueue("HOOK_MISSING\t" + mode); return; }
            var hook = method.AddHook(false);
            hook.AddPre(args => {
                if (args.Length < 3) return PreHookResult.Continue;
                uint id = (uint)args[2];
                if (!Names.TryGetValue(id, out string name)) return PreHookResult.Continue;
                long now = DateTime.UtcNow.Ticks;
                if (mode == "LOOP") {
                    long previous = LastLoopSeen.GetOrAdd(id, 0);
                    LastLoopSeen[id] = now;
                    if (now - previous < TimeSpan.TicksPerSecond * 2) return PreHookResult.Continue;
                }
                Pending.Enqueue(DateTime.Now.ToString("O") + "\t" + mode + "\t" + CurrentSession + "\t" + name +
                    "\t" + id + "\t0x" + id.ToString("X") + "\t0x" + args[1].ToString("X"));
                return PreHookResult.Continue;
            });
            Hooks.Add(hook);
            Pending.Enqueue("HOOK_READY\t" + mode);
        } catch (Exception e) { Pending.Enqueue("HOOK_ERROR\t" + mode + "\t" + e.Message); }
    }

    [Callback(typeof(UpdateBehavior), CallbackType.Pre)]
    public static void Update() {
        long now = DateTime.UtcNow.Ticks;
        if (now - LastSessionCheck > TimeSpan.TicksPerSecond) {
            LastSessionCheck = now;
            try {
                string next = File.Exists(SessionFile) ? File.ReadAllText(SessionFile).Trim() : "unmarked";
                if (next != CurrentSession) {
                    CurrentSession = next;
                    Pending.Enqueue("SESSION\t" + DateTime.Now.ToString("O") + "\t" + CurrentSession);
                }
            } catch (Exception e) { API.LogWarning("[CodexWeaponStateSurvey] session: " + e.Message); }
        }
        if (Pending.IsEmpty) return;
        try {
            var text = new StringBuilder();
            while (Pending.TryDequeue(out string line)) text.AppendLine(line);
            File.AppendAllText(Output, text.ToString());
        } catch (Exception e) { API.LogWarning("[CodexWeaponStateSurvey] " + e.Message); }
    }

    [PluginExitPoint]
    public static void OnUnload() {
        foreach (var hook in Hooks) hook.Dispose();
        Hooks.Clear();
    }
}
