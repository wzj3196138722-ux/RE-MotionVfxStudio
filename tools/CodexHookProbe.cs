using System;
using System.IO;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

public class CodexHookProbe {
    static readonly string Output = Path.Combine(Environment.GetEnvironmentVariable("MOTIONVFX_GAME_ROOT") ?? AppContext.BaseDirectory, "reframework/data/codex_hook_probe.txt");
    static MethodHook Hook;
    static MethodHook LoopHook;
    static MethodHook PlayHook;
    static MethodHook MotionHook;
    static MethodHook ChargeHook;
    static int HitCount;
    static int LoopCount;
    static int PlayCount;
    static int MotionCount;
    static int ChargeCount;
    static ulong LastLoopId;
    static long LastWrite;

    [PluginEntryPoint]
    public static void Main() {
        var method = TDB.Get().FindType("app.Weapon")?.FindMethod("get_WpType()");
        if (method == null) { File.AppendAllText(Output, "MISSING\n"); return; }
        Hook = method.AddHook(false);
        Hook.AddPre(args => { HitCount++; return PreHookResult.Continue; });
        var loop = TDB.Get().FindType("app.EPVExpertCommon")?.FindMethod(
            "playEffectLoop(app.EffectID_Common.ID, System.UInt64, via.GameObject, app.cEffectOverwriteParams, System.UInt32, via.GameObject)");
        if (loop != null) {
            LoopHook = loop.AddHook(false);
            LoopHook.AddPre(args => {
                if (args.Length > 2) { LoopCount++; LastLoopId = args[2]; }
                return PreHookResult.Continue;
            });
        }
        var play = TDB.Get().FindType("app.EPVExpertCommon")?.FindMethod(
            "playEffect(app.EffectID_Common.ID, via.GameObject, app.cEffectOverwriteParams, System.UInt32, via.GameObject)");
        if (play != null) {
            PlayHook = play.AddHook(false);
            PlayHook.AddPre(args => { PlayCount++; return PreHookResult.Continue; });
        }
        var motion = TDB.Get().FindType("via.effect.script.ObjectEffectManagerBase")?.FindMethod(
            "callEventMotionSequenceCreateEffect(via.effect.script.EffectID, via.effect.script.CreatedEffectContainer, via.effect.script.ObjectEffectManagerBase.CreateMotionSequenceEffectEventInfo.CallSequenceType)");
        if (motion != null) {
            MotionHook = motion.AddHook(false);
            MotionHook.AddPre(args => { MotionCount++; return PreHookResult.Continue; });
        }
        var charge = TDB.Get().FindType("app.cHunterWp00Handling")?.FindMethod("callChargeEffect()");
        if (charge != null) {
            ChargeHook = charge.AddHook(false);
            ChargeHook.AddPre(args => { ChargeCount++; return PreHookResult.Continue; });
        }
        File.AppendAllText(Output, "READY " + DateTime.Now.ToString("O") + "\n");
    }

    [Callback(typeof(UpdateBehavior), CallbackType.Pre)]
    public static void Update() {
        if (DateTime.UtcNow.Ticks - LastWrite < TimeSpan.TicksPerSecond) return;
        LastWrite = DateTime.UtcNow.Ticks;
        int count = HitCount;
        HitCount = 0;
        int loops = LoopCount;
        LoopCount = 0;
        int plays = PlayCount; PlayCount = 0;
        int motions = MotionCount; MotionCount = 0;
        int charges = ChargeCount; ChargeCount = 0;
        if (count > 0 || loops > 0 || plays > 0 || motions > 0 || charges > 0)
            File.AppendAllText(Output, "HIT " + count + " LOOP " + loops +
                " PLAY " + plays + " MOTION " + motions + " WP00_CHARGE " + charges +
                " LAST_ID 0x" + LastLoopId.ToString("X") + " " + DateTime.Now.ToString("O") + "\n");
    }

    [PluginExitPoint]
    public static void OnUnload() {
        Hook?.Dispose(); LoopHook?.Dispose(); PlayHook?.Dispose(); MotionHook?.Dispose(); ChargeHook?.Dispose();
    }
}
