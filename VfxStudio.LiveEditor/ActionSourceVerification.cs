using System.Text.Json.Nodes;
namespace VfxStudio.LiveEditor;
internal sealed partial class EditorEngine
{
    private void VerifyActionSource(JsonObject proof)
    {
        var statusPath = Path.Combine(options.Mailbox, "ui-status.json");
        if (!File.Exists(statusPath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(statusPath) > TimeSpan.FromSeconds(20))
            throw new InvalidOperationException("游戏记录已离线；请在游戏中重新核实动作来源。");
        var status = JsonNode.Parse(File.ReadAllText(statusPath))!;
        var actor = status["captureActor"]!;
        if (proof["sceneEpoch"]?.ToJsonString() != actor["sceneEpoch"]?.ToJsonString() ||
            proof["actorIdentity"]?.ToJsonString() != actor["characterIdentity"]?.ToJsonString())
            throw new InvalidOperationException("动作来源已过期；请重新记录当前角色。");
        string? Canonical(string? value) => value == null ? null : status["actionPreview"]?["aliases"]?[value.ToLowerInvariant()]?.GetValue<string>() ?? value;
        bool Matches(JsonNode? r) => r?["componentKind"]?.GetValue<string>()=="character" &&
            r["actorIdentity"]?.ToJsonString()==proof["actorIdentity"]?.ToJsonString() && r["sceneEpoch"]?.ToJsonString()==proof["sceneEpoch"]?.ToJsonString() &&
            r["weaponType"]?.ToJsonString()==proof["weaponType"]?.ToJsonString() && r["weaponId"]?.ToJsonString()==proof["weaponId"]?.ToJsonString() &&
            r["motion"]?["componentIdentity"]?.ToJsonString()==proof["motionIdentity"]?.ToJsonString() &&
            r["motion"]?["motionId"]?.ToJsonString()==proof["motionId"]?.ToJsonString() && r["motion"]?["bankId"]?.ToJsonString()==proof["bankId"]?.ToJsonString() &&
            string.Equals(Canonical(r["motion"]?["bankResourcePath"]?.GetValue<string>()),Text(proof,"bankPath"),StringComparison.OrdinalIgnoreCase);
        if (proof["bankId"]==null || proof["motionId"]==null || status["captureActions"]?.AsArray().Any(Matches)!=true)
            throw new InvalidOperationException("未找到与此来源一致的角色动作记录。");
    }
}
