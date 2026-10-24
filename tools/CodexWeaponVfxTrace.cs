using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

// Read-only trace of state and motion driven VFX calls in the running game.
// Method signatures were obtained from the bundled REE MCP type explorer.
public class CodexWeaponVfxTrace {
    static readonly string Output = Path.Combine(Environment.GetEnvironmentVariable("MOTIONVFX_GAME_ROOT") ?? AppContext.BaseDirectory, "reframework/data/codex_weapon_vfx_trace_cs.tsv");
    static readonly string ArmFlag = Path.Combine(Environment.GetEnvironmentVariable("MOTIONVFX_GAME_ROOT") ?? AppContext.BaseDirectory, "reframework/data/codex_weapon_vfx_arm.flag");
    static readonly ConcurrentQueue<string> Pending = new();
    static readonly ConcurrentDictionary<string, long> LastSeen = new();
    static readonly List<MethodHook> Hooks = new();
    static int count;
    static long armedUntilTicks;

    [PluginEntryPoint]
    public static void Main() {
        Install("app.EPVExpertCommon", "playEffect(app.EffectID_Common.ID, via.GameObject, app.cEffectOverwriteParams, System.UInt32, via.GameObject)", "COMMON_PLAY");
        Install("app.EPVExpertCommon", "playEffectLoop(app.EffectID_Common.ID, System.UInt64, via.GameObject, app.cEffectOverwriteParams, System.UInt32, via.GameObject)", "COMMON_LOOP");
        Install("via.effect.script.ObjectEffectManagerBase", "callEventMotionSequenceCreateEffect(via.effect.script.EffectID, via.effect.script.CreatedEffectContainer, via.effect.script.ObjectEffectManagerBase.CreateMotionSequenceEffectEventInfo.CallSequenceType)", "MOTION_CREATE");
        Install("app.Weapon", "get_WpType()", "PROBE_WEAPON_TYPE");
        Install("app.cHunterWp02Handling", "startKijinMode(System.Boolean, System.Boolean)", "WP02_START_KIJIN");
        Install("app.cHunterWp02Handling", "setKijinOn(System.Boolean)", "WP02_SET_KIJIN");
        Install("app.cHunterWp02Handling", "updateEffect()", "WP02_UPDATE_EFFECT");
        Pending.Enqueue("READY\t" + DateTime.Now.ToString("O"));
    }

    static void Install(string typeName, string signature, string name) {
        try {
            var method = TDB.Get().FindType(typeName)?.FindMethod(signature);
            if (method == null) {
                Pending.Enqueue("HOOK_MISSING\t" + typeName + "." + signature);
                return;
            }
            var hook = method.AddHook(false);
            hook.AddPre(args => {
                long now = DateTime.UtcNow.Ticks;
                if (now > System.Threading.Volatile.Read(ref armedUntilTicks) || count >= 10000)
                    return PreHookResult.Continue;
                string key = name + ":" + (args.Length > (name == "EFFECT_CREATE" ? 3 : 2)
                    ? args[name == "EFFECT_CREATE" ? 3 : 2].ToString("X") : "none");
                if (LastSeen.TryGetValue(key, out var previous) && now - previous < TimeSpan.TicksPerSecond / 4)
                    return PreHookResult.Continue;
                LastSeen[key] = now;
                count++;
                var builder = new StringBuilder();
                builder.Append(DateTime.Now.ToString("O")).Append('\t').Append(name).Append('\t');
                for (int i = 0; i < args.Length && i < 8; i++) {
                    if (i > 0) builder.Append(',');
                    builder.Append("0x").Append(args[i].ToString("X"));
                }
                if (name == "EFFECT_CREATE" && args.Length > 3) {
                    try {
                        var request = ManagedObject.ToManagedObject(args[3]) as IObject;
                        var data = request?.GetField("Data") as IObject;
                        int resourceCount = data == null ? 0 : Math.Min(8, Convert.ToInt32(data.Call("getResourcesCount")));
                        for (int i = 0; i < resourceCount; i++) {
                            var holder = data?.Call("getResources(System.UInt64)", (ulong)i) as IObject;
                            builder.Append('\t').Append(holder?.Call("get_ResourcePath")?.ToString() ?? "null");
                        }
                    } catch (Exception e) { builder.Append("\tDECODE_ERROR:").Append(e.Message); }
                }
                Pending.Enqueue(builder.ToString());
                return PreHookResult.Continue;
            });
            Hooks.Add(hook);
            Pending.Enqueue("HOOK_READY\t" + typeName + "." + signature);
        } catch (Exception e) {
            Pending.Enqueue("HOOK_ERROR\t" + name + "\t" + e.Message);
        }
    }

    [Callback(typeof(UpdateBehavior), CallbackType.Pre)]
    public static void Update() {
        if (File.Exists(ArmFlag)) {
            try {
                File.Delete(ArmFlag);
                LastSeen.Clear();
                count = 0;
                System.Threading.Volatile.Write(ref armedUntilTicks, DateTime.UtcNow.AddMinutes(3).Ticks);
                Pending.Enqueue("ARM\t" + DateTime.Now.ToString("O") + "\t180s");
            } catch (Exception e) { API.LogWarning("[CodexWeaponVfxTrace] Arm failed: " + e.Message); }
        }
        if (Pending.IsEmpty) return;
        try {
            var lines = new StringBuilder();
            while (Pending.TryDequeue(out var line)) lines.AppendLine(line);
            File.AppendAllText(Output, lines.ToString());
        } catch (Exception e) { API.LogWarning("[CodexWeaponVfxTrace] " + e.Message); }
    }

    [PluginExitPoint]
    public static void OnUnload() {
        foreach (var hook in Hooks) hook.Dispose();
        Hooks.Clear();
    }
}
