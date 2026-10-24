using System.Numerics;
using System.Text.Json;

namespace VfxStudio.Core.Models;

public class WeaponGrip
{
    public string Description { get; set; } = string.Empty;
    public Dictionary<string, float[]> LeftHand { get; set; } = new();
    public Dictionary<string, float[]> RightHand { get; set; } = new();

    public static WeaponGrip LoadFromJson(string filePath)
    {
        var text = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<WeaponGrip>(text) ?? new WeaponGrip();
    }

    /// <summary>
    /// Gets Quaternion rotation (W, X, Y, Z) for given finger bone name.
    /// </summary>
    public Quaternion? GetRotation(string boneName)
    {
        if (LeftHand.TryGetValue(boneName, out var l) && l.Length == 4)
        {
            return new Quaternion(l[1], l[2], l[3], l[0]); // System.Numerics Quaternion is (X, Y, Z, W)
        }
        if (RightHand.TryGetValue(boneName, out var r) && r.Length == 4)
        {
            return new Quaternion(r[1], r[2], r[3], r[0]);
        }
        return null;
    }
}
