using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

// Read-only log of the exact EFX resources requested by EffectManager.
public class CodexWeaponResourceSurvey {
    static readonly string Base = Path.Combine(Environment.GetEnvironmentVariable("MOTIONVFX_GAME_ROOT") ?? AppContext.BaseDirectory, "reframework/data");
    const string SessionFile = Base + @"\codex_weapon_session.txt";
    const string OutputFile = Base + @"\codex_weapon_resource_survey.tsv";
    static readonly ConcurrentQueue<string> Pending = new();
    static readonly ConcurrentDictionary<string, long> LastSeen = new();
    static readonly ConcurrentDictionary<string, int> ProbeCounts = new();
    static readonly List<MethodHook> Hooks = new();
    static string Session = "unmarked";
    static long LastSessionCheck;
    static int Errors;

    [PluginEntryPoint]
    public static void Main() {
        Install("createEffectInternal(via.GameObject, via.effect.script.EffectManager.RequestInfo)", "REQUEST");
        Install("createEffectInternal(via.GameObject, via.effect.ProviderData, via.vec3, via.Quaternion, via.GameObject, System.String, System.Collections.Generic.IEnumerable`1<via.effect.ExternParameter>, via.GameObject, System.Collections.Generic.IEnumerable`1<System.String>, via.effect.script.EffectManager.LODInfo, System.String, System.Boolean, via.effect.script.EffectManager.RequestParam, System.Collections.Generic.IEnumerable`1<via.effect.GroupParameter>, via.effect.script.EPVCollisionHelper.EPVCastRayResult)", "DIRECT");
        InstallProbe("via.effect.script.EPVStandard", "requestEffect(System.Collections.Generic.IEnumerable`1<via.effect.script.EPVStandardData.Element>, via.GameObject, via.effect.script.EffectManager.WwiseTriggerInfo, via.effect.script.EffectManager.RequestParam)", "STANDARD");
        InstallProbe("via.effect.script.EffectManager", "createEffectAutoDestroy(via.effect.script.EffectManager.CreateEffectParam)", "AUTO");
        InstallProbe("app.EPVExpertCommon", "getRequestInfo(System.Collections.Generic.List`1<app.EPVExpertCommonData.Element>, via.GameObject, via.GameObject, app.cEffectOverwriteParams, System.UInt32, app.EPVExpertCommon.cRequestInfoBack)", "EXPERT");
        Pending.Enqueue("READY\t" + DateTime.Now.ToString("O"));
    }

    static void InstallProbe(string typeName, string signature, string mode) {
        try {
            var method = TDB.Get().FindType(typeName)?.FindMethod(signature);
            if (method == null) { Pending.Enqueue("HOOK_MISSING\t" + mode); return; }
            var hook = method.AddHook(false);
            hook.AddPre(args => {
                if (Session != "unmarked") {
                    int probe = ProbeCounts.AddOrUpdate(mode, 1, (_, count) => count + 1);
                    if (probe <= 8) Pending.Enqueue("CALL\t" + mode + "\t" + Session + "\targs=" + args.Length);
                    if (mode == "STANDARD" && args.Length > 2 && args[2] != 0)
                        ReadStandardElements(args[2], probe);
                }
                return PreHookResult.Continue;
            });
            Hooks.Add(hook);
            Pending.Enqueue("HOOK_READY\t" + mode);
        } catch (Exception e) { Pending.Enqueue("HOOK_ERROR\t" + mode + "\t" + e.Message); }
    }

    static void ReadStandardElements(ulong pointer, int probe) {
        try {
            var source = ManagedObject.ToManagedObject(pointer) as IObject;
            if (probe <= 8) Pending.Enqueue("STANDARD_SOURCE\t" + (source == null ? "null" : "object"));
            var enumerator = source?.Call("System.Collections.IEnumerable.GetEnumerator") as IObject;
            if (probe <= 8) Pending.Enqueue("STANDARD_ENUM\t" + (enumerator == null ? "null" : "object"));
            if (enumerator == null) return;
            for (int e = 0; e < 24; e++) {
                if (!Convert.ToBoolean(enumerator.Call("MoveNext"))) break;
                var element = enumerator.Call("System.Collections.IEnumerator.get_Current") as IObject;
                if (probe <= 8) Pending.Enqueue("STANDARD_ELEMENT\t" + e + "\t" + (element == null ? "null" : "object"));
                if (element == null) continue;
                int count = Math.Min(32, Convert.ToInt32(element.Call("getResourcesCount")));
                if (probe <= 8) Pending.Enqueue("STANDARD_COUNT\t" + e + "\t" + count);
                for (int i = 0; i < count; i++) {
                    var holder = element.Call("getResources(System.UInt64)", (ulong)i) as IObject;
                    string path = holder?.Call("get_ResourcePath") as string;
                    if (probe <= 8) Pending.Enqueue("STANDARD_PATH\t" + e + "\t" + i + "\t" + (path ?? "null"));
                    if (String.IsNullOrEmpty(path) || path.IndexOf(".efx", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    string key = Session + "|" + path;
                    long now = DateTime.UtcNow.Ticks;
                    if (LastSeen.TryGetValue(key, out long previous) &&
                        now - previous < TimeSpan.TicksPerSecond / 4) continue;
                    LastSeen[key] = now;
                    Pending.Enqueue(DateTime.Now.ToString("O") + "\tRESOURCE\t" + Session +
                        "\tSTANDARD\t" + i + "\t" + path);
                }
            }
        } catch (Exception e) {
            if (System.Threading.Interlocked.Increment(ref Errors) <= 20)
                Pending.Enqueue("STANDARD_ERROR\t" + e.Message);
        }
    }

    static void Install(string signature, string mode) {
        try {
            var method = TDB.Get().FindType("via.effect.script.EffectManager")?.FindMethod(signature);
            if (method == null) { Pending.Enqueue("HOOK_MISSING\t" + mode); return; }
            var hook = method.AddHook(false);
            hook.AddPre(args => {
                if (Session == "unmarked" || args.Length < 4 || args[3] == 0)
                    return PreHookResult.Continue;
                int probe = ProbeCounts.AddOrUpdate(mode, 1, (_, count) => count + 1);
                if (probe <= 8)
                    Pending.Enqueue("CALL\t" + mode + "\t" + Session + "\targs=" + args.Length +
                        "\targ3=0x" + args[3].ToString("X"));
                try {
                    IObject source = ManagedObject.ToManagedObject(args[3]) as IObject;
                    if (source == null) {
                        string typeName = mode == "REQUEST"
                            ? "via.effect.script.EffectManager.RequestInfo" : "via.effect.ProviderData";
                        var type = TDB.Get().FindType(typeName);
                        if (type != null) source = new NativeObject(args[3], type);
                    }
                    IObject data = mode == "REQUEST" ? source?.GetField("Data") as IObject : source;
                    if (data == null) {
                        if (probe <= 8) Pending.Enqueue("NULL_DATA\t" + mode + "\tsource=" + (source == null ? "null" : "object"));
                        return PreHookResult.Continue;
                    }
                    int count = Math.Min(32, Convert.ToInt32(data.Call("getResourcesCount")));
                    if (probe <= 8) Pending.Enqueue("RESOURCE_COUNT\t" + mode + "\t" + count);
                    for (int i = 0; i < count; i++) {
                        var holder = data.Call("getResources(System.UInt64)", (ulong)i) as IObject;
                        string path = holder?.Call("get_ResourcePath") as string;
                        if (probe <= 8) Pending.Enqueue("RESOURCE_PROBE\t" + mode + "\t" + i + "\t" + (path ?? "null"));
                        if (String.IsNullOrEmpty(path) || path.IndexOf(".efx", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        string key = Session + "|" + path;
                        long now = DateTime.UtcNow.Ticks;
                        if (LastSeen.TryGetValue(key, out long previous) &&
                            now - previous < TimeSpan.TicksPerSecond / 4) continue;
                        LastSeen[key] = now;
                        Pending.Enqueue(DateTime.Now.ToString("O") + "\tRESOURCE\t" + Session +
                            "\t" + mode + "\t" + i + "\t" + path);
                    }
                } catch (Exception e) {
                    if (System.Threading.Interlocked.Increment(ref Errors) <= 20)
                        Pending.Enqueue("DECODE_ERROR\t" + mode + "\t" + e.Message);
                }
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
                if (next != Session) {
                    Session = next;
                    Pending.Enqueue("SESSION\t" + DateTime.Now.ToString("O") + "\t" + Session);
                }
            } catch (Exception e) { API.LogWarning("[CodexWeaponResourceSurvey] session: " + e.Message); }
        }
        if (Pending.IsEmpty) return;
        try {
            var text = new StringBuilder();
            while (Pending.TryDequeue(out string line)) text.AppendLine(line);
            File.AppendAllText(OutputFile, text.ToString());
        } catch (Exception e) { API.LogWarning("[CodexWeaponResourceSurvey] " + e.Message); }
    }

    [PluginExitPoint]
    public static void OnUnload() {
        foreach (var hook in Hooks) hook.Dispose();
        Hooks.Clear();
    }
}
