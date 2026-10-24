using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

// Reads only standard-provider resources. The previous native-provider probe is intentionally absent.
public class CodexWeaponStandardResourceSurvey {
    static readonly string Base = Path.Combine(Environment.GetEnvironmentVariable("MOTIONVFX_GAME_ROOT") ?? AppContext.BaseDirectory, "reframework/data");
    const string SessionFile = Base + @"\codex_weapon_session.txt";
    const string OutputFile = Base + @"\codex_weapon_standard_resource_survey.tsv";
    static readonly ConcurrentQueue<string> Pending = new();
    static readonly ConcurrentDictionary<string, long> LastSeen = new();
    static readonly List<MethodHook> Hooks = new();
    static string Session = "unmarked";
    static long LastSessionCheck;
    static int Errors;

    [PluginEntryPoint]
    public static void Main() {
        try {
            var method = TDB.Get().FindType("via.effect.script.EPVStandard")?.FindMethod(
                "requestEffect(System.Collections.Generic.IEnumerable`1<via.effect.script.EPVStandardData.Element>, via.GameObject, via.effect.script.EffectManager.WwiseTriggerInfo, via.effect.script.EffectManager.RequestParam)");
            if (method == null) { Pending.Enqueue("HOOK_MISSING\tSTANDARD"); return; }
            var hook = method.AddHook(false);
            hook.AddPre(args => {
                if (Session == "unmarked" || args.Length < 3 || args[2] == 0)
                    return PreHookResult.Continue;
                try {
                    var source = ManagedObject.ToManagedObject(args[2]) as IObject;
                    var enumerator = source?.Call("System.Collections.IEnumerable.GetEnumerator") as IObject;
                    if (enumerator == null) return PreHookResult.Continue;
                    for (int e = 0; e < 24; e++) {
                        if (!Convert.ToBoolean(enumerator.Call("MoveNext"))) break;
                        var element = enumerator.Call("System.Collections.IEnumerator.get_Current") as IObject;
                        if (element == null) continue;
                        int count = Math.Min(32, Convert.ToInt32(element.Call("getResourcesCount")));
                        for (int i = 0; i < count; i++) {
                            var holder = element.Call("getResources(System.UInt64)", (ulong)i) as IObject;
                            string path = holder?.Call("get_ResourcePath") as string;
                            if (String.IsNullOrEmpty(path) ||
                                path.IndexOf("/Weapon/it", StringComparison.OrdinalIgnoreCase) < 0 ||
                                path.IndexOf(".efx", StringComparison.OrdinalIgnoreCase) < 0) continue;
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
                    if (System.Threading.Interlocked.Increment(ref Errors) <= 5)
                        Pending.Enqueue("READ_ERROR\t" + e.Message);
                }
                return PreHookResult.Continue;
            });
            Hooks.Add(hook);
            Pending.Enqueue("HOOK_READY\tSTANDARD");
        } catch (Exception e) { Pending.Enqueue("HOOK_ERROR\t" + e.Message); }
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
            } catch (Exception e) { API.LogWarning("[CodexWeaponStandardResourceSurvey] session: " + e.Message); }
        }
        if (Pending.IsEmpty) return;
        try {
            var text = new StringBuilder();
            while (Pending.TryDequeue(out string line)) text.AppendLine(line);
            File.AppendAllText(OutputFile, text.ToString());
        } catch (Exception e) { API.LogWarning("[CodexWeaponStandardResourceSurvey] " + e.Message); }
    }

    [PluginExitPoint]
    public static void OnUnload() {
        foreach (var hook in Hooks) hook.Dispose();
        Hooks.Clear();
    }
}
