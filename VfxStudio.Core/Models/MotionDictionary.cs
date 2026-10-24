using System.Text.Json;

namespace VfxStudio.Core.Models;

public class MotionDictionary
{
    private Dictionary<string, Dictionary<string, string>> categories = new();

    public static MotionDictionary LoadFromJson(string filePath)
    {
        var dict = new MotionDictionary();
        if (File.Exists(filePath))
        {
            var text = File.ReadAllText(filePath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(text);
            if (parsed != null)
            {
                dict.categories = parsed;
            }
        }
        return dict;
    }

    public string GetMotionName(string weaponOrCategory, int motionId)
    {
        var key = motionId.ToString();
        if (categories.TryGetValue(weaponOrCategory.ToLowerInvariant(), out var map))
        {
            if (map.TryGetValue(key, out var name)) return name;
        }

        if (categories.TryGetValue("common", out var commonMap))
        {
            if (commonMap.TryGetValue(key, out var name)) return name;
        }

        return $"Motion_{motionId}";
    }

    public void SetMotionName(string weaponOrCategory, int motionId, string name)
    {
        var catKey = weaponOrCategory.ToLowerInvariant();
        if (!categories.TryGetValue(catKey, out var map))
        {
            map = new Dictionary<string, string>();
            categories[catKey] = map;
        }
        map[motionId.ToString()] = name;
    }

    public void SaveToJson(string filePath)
    {
        var json = JsonSerializer.Serialize(categories, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }
}
