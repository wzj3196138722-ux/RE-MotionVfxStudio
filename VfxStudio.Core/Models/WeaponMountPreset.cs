using System.Numerics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VfxStudio.Core.Models;

/// <summary>
/// Per-part weapon mounting configuration (bone attachment + local XYZ offset/rotation/scale).
/// </summary>
public class WeaponPartMountPreset
{
    public int PartIndex { get; set; }
    public string PartDescription { get; set; } = string.Empty;
    public string TargetBoneName { get; set; } = "R_Hand_HJ_01";
    public float[] PositionOffset { get; set; } = [0f, 0f, 0f];
    public float[] RotationEuler { get; set; } = [0f, 0f, 0f];
    public float Scale { get; set; } = 1.0f;
    public bool UseWeaponBaseBoneInverse { get; set; } = true;

    [JsonIgnore]
    public Vector3 PositionVector
    {
        get => PositionOffset != null && PositionOffset.Length >= 3
            ? new Vector3(PositionOffset[0], PositionOffset[1], PositionOffset[2])
            : Vector3.Zero;
        set => PositionOffset = [value.X, value.Y, value.Z];
    }

    [JsonIgnore]
    public Vector3 RotationEulerVector
    {
        get => RotationEuler != null && RotationEuler.Length >= 3
            ? new Vector3(RotationEuler[0], RotationEuler[1], RotationEuler[2])
            : Vector3.Zero;
        set => RotationEuler = [value.X, value.Y, value.Z];
    }

    public WeaponPartMountPreset Clone()
    {
        return new WeaponPartMountPreset
        {
            PartIndex = PartIndex,
            PartDescription = PartDescription,
            TargetBoneName = TargetBoneName,
            PositionOffset = PositionOffset != null ? (float[])PositionOffset.Clone() : [0f, 0f, 0f],
            RotationEuler = RotationEuler != null ? (float[])RotationEuler.Clone() : [0f, 0f, 0f],
            Scale = Scale,
            UseWeaponBaseBoneInverse = UseWeaponBaseBoneInverse
        };
    }
}

/// <summary>
/// Complete mounting preset for a weapon category (wp00 ~ wp13), containing all sub-parts
/// as well as optional per-motion overrides keyed by motion name (e.g. wp02_000).
/// </summary>
public class WeaponMountPreset
{
    public string WeaponCode { get; set; } = string.Empty;
    public string WeaponName { get; set; } = string.Empty;
    public List<WeaponPartMountPreset> Parts { get; set; } = new();
    public Dictionary<string, List<WeaponPartMountPreset>> MotionOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Persistent repository for all weapon mounting presets, stored at Assets/HunterBase/weapon_mount_presets.json.
/// </summary>
public class WeaponMountPresetStore
{
    public int Version { get; set; } = 1;
    public Dictionary<string, WeaponMountPreset> Weapons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    public static WeaponMountPresetStore LoadOrCreate(string jsonPath)
    {
        try
        {
            if (File.Exists(jsonPath))
            {
                string json = File.ReadAllText(jsonPath);
                var loaded = JsonSerializer.Deserialize<WeaponMountPresetStore>(json, JsonOptions);
                if (loaded != null && loaded.Weapons != null && loaded.Weapons.Count > 0)
                {
                    // Ensure case-insensitive dictionary
                    var normalized = new Dictionary<string, WeaponMountPreset>(loaded.Weapons, StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in normalized)
                    {
                        kv.Value.MotionOverrides = kv.Value.MotionOverrides != null
                            ? new Dictionary<string, List<WeaponPartMountPreset>>(kv.Value.MotionOverrides, StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, List<WeaponPartMountPreset>>(StringComparer.OrdinalIgnoreCase);
                    }
                    loaded.Weapons = normalized;
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WeaponMountPresetStore] Warning: Failed to read {jsonPath}: {ex.Message}");
        }

        var store = CreateDefaultStore();
        store.Save(jsonPath);
        return store;
    }

    public void Save(string jsonPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(jsonPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(jsonPath, json);
            Console.WriteLine($"[WeaponMountPresetStore] Saved weapon mount presets to: {jsonPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WeaponMountPresetStore] Error saving {jsonPath}: {ex.Message}");
        }
    }

    public WeaponPartMountPreset GetOrCreatePartPreset(string wpCode, int partIndex)
    {
        string key = wpCode.ToLowerInvariant();
        if (!Weapons.TryGetValue(key, out var wpPreset))
        {
            var def = WeaponDefinition.FromCode(key);
            wpPreset = new WeaponMountPreset
            {
                WeaponCode = key,
                WeaponName = def != null ? $"{def.NameCn} ({def.NameEn})" : key
            };
            Weapons[key] = wpPreset;
        }

        var part = wpPreset.Parts.FirstOrDefault(p => p.PartIndex == partIndex);
        if (part == null)
        {
            part = CreateDefaultPartPreset(key, partIndex);
            wpPreset.Parts.Add(part);
        }

        return part;
    }

    public bool HasMotionOverride(string wpCode, string? motionKey)
    {
        if (string.IsNullOrWhiteSpace(motionKey))
            return false;
        string key = wpCode.ToLowerInvariant();
        return Weapons.TryGetValue(key, out var wpPreset) &&
               wpPreset.MotionOverrides != null &&
               wpPreset.MotionOverrides.ContainsKey(motionKey.Trim());
    }

    public WeaponPartMountPreset GetEffectivePartPreset(string wpCode, int partIndex, string? motionKey)
    {
        string key = wpCode.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(motionKey) &&
            Weapons.TryGetValue(key, out var wpPreset) &&
            wpPreset.MotionOverrides != null &&
            wpPreset.MotionOverrides.TryGetValue(motionKey.Trim(), out var motionParts))
        {
            var overridePart = motionParts.FirstOrDefault(p => p.PartIndex == partIndex);
            if (overridePart != null)
                return overridePart;
        }
        return GetOrCreatePartPreset(wpCode, partIndex);
    }

    public void SetMotionOverride(string wpCode, string motionKey, IEnumerable<WeaponPartMountPreset> parts)
    {
        if (string.IsNullOrWhiteSpace(motionKey))
            return;
        string key = wpCode.ToLowerInvariant();
        if (!Weapons.TryGetValue(key, out var wpPreset))
        {
            var def = WeaponDefinition.FromCode(key);
            wpPreset = new WeaponMountPreset
            {
                WeaponCode = key,
                WeaponName = def != null ? $"{def.NameCn} ({def.NameEn})" : key
            };
            Weapons[key] = wpPreset;
        }
        wpPreset.MotionOverrides ??= new Dictionary<string, List<WeaponPartMountPreset>>(StringComparer.OrdinalIgnoreCase);
        wpPreset.MotionOverrides[motionKey.Trim()] = parts.Select(p => p.Clone()).ToList();
    }

    public bool ClearMotionOverride(string wpCode, string? motionKey)
    {
        if (string.IsNullOrWhiteSpace(motionKey))
            return false;
        string key = wpCode.ToLowerInvariant();
        if (Weapons.TryGetValue(key, out var wpPreset) && wpPreset.MotionOverrides != null)
        {
            return wpPreset.MotionOverrides.Remove(motionKey.Trim());
        }
        return false;
    }

    public void UpdatePartPreset(string wpCode, WeaponPartMountPreset updated)
    {
        string key = wpCode.ToLowerInvariant();
        if (!Weapons.TryGetValue(key, out var wpPreset))
        {
            var def = WeaponDefinition.FromCode(key);
            wpPreset = new WeaponMountPreset
            {
                WeaponCode = key,
                WeaponName = def != null ? $"{def.NameCn} ({def.NameEn})" : key
            };
            Weapons[key] = wpPreset;
        }

        int existingIdx = wpPreset.Parts.FindIndex(p => p.PartIndex == updated.PartIndex);
        if (existingIdx >= 0)
        {
            wpPreset.Parts[existingIdx] = updated.Clone();
        }
        else
        {
            wpPreset.Parts.Add(updated.Clone());
        }
    }

    public static WeaponMountPresetStore CreateDefaultStore()
    {
        var store = new WeaponMountPresetStore();
        foreach (var def in WeaponDefinition.AllWeapons)
        {
            var wpPreset = new WeaponMountPreset
            {
                WeaponCode = def.WpCode,
                WeaponName = $"{def.NameCn} ({def.NameEn})"
            };

            wpPreset.Parts.Add(CreateDefaultPartPreset(def.WpCode, 0));
            if (def.HasSecondaryMesh)
            {
                wpPreset.Parts.Add(CreateDefaultPartPreset(def.WpCode, 1));
            }

            store.Weapons[def.WpCode] = wpPreset;
        }
        return store;
    }

    public static WeaponPartMountPreset CreateDefaultPartPreset(string wpCode, int partIndex)
    {
        string code = wpCode.ToLowerInvariant();
        if (partIndex == 0)
        {
            string targetBone = code == "wp11" ? "L_Hand_HJ_01" : "R_Hand_HJ_01";
            float[] rot = code == "wp02" ? [0f, 0f, 180f] : [0f, 0f, 0f];
            string desc = code switch
            {
                "wp01" => "主手剑 (_0.mesh)",
                "wp02" => "右手刀 (_0.mesh)",
                "wp03" => "太刀本体 (_0.mesh)",
                "wp06" => "长枪本体 (_0.mesh)",
                "wp07" => "铳枪本体 (_0.mesh)",
                "wp09" => "盾斧剑形态 (_0.mesh)",
                "wp11" => "弓本体 (_0.mesh)",
                _ => "主武器 (_0.mesh)"
            };

            return new WeaponPartMountPreset
            {
                PartIndex = 0,
                PartDescription = desc,
                TargetBoneName = targetBone,
                PositionOffset = [0f, 0f, 0f],
                RotationEuler = rot,
                Scale = 1.0f,
                UseWeaponBaseBoneInverse = true
            };
        }
        else
        {
            return code switch
            {
                "wp01" or "wp06" or "wp07" or "wp09" => new WeaponPartMountPreset
                {
                    PartIndex = partIndex,
                    PartDescription = "左手盾牌 (_1.mesh)",
                    TargetBoneName = "L_Hand_HJ_01",
                    PositionOffset = [0f, 0f, 0f],
                    RotationEuler = [0f, -90f, 90f],
                    Scale = 1.0f,
                    UseWeaponBaseBoneInverse = true
                },
                "wp02" => new WeaponPartMountPreset
                {
                    PartIndex = partIndex,
                    PartDescription = "左手刀 (_1.mesh)",
                    TargetBoneName = "L_Hand_HJ_01",
                    PositionOffset = [0f, 0f, 0f],
                    RotationEuler = [0f, 0f, 180f],
                    Scale = 1.0f,
                    UseWeaponBaseBoneInverse = true
                },
                "wp03" => new WeaponPartMountPreset
                {
                    PartIndex = partIndex,
                    PartDescription = "太刀刀鞘 (_1.mesh)",
                    TargetBoneName = "Spine_2_WpConst",
                    PositionOffset = [0f, 0f, 0f],
                    RotationEuler = [81f, 0f, 14.3f],
                    Scale = 1.0f,
                    UseWeaponBaseBoneInverse = true
                },
                "wp11" => new WeaponPartMountPreset
                {
                    PartIndex = partIndex,
                    PartDescription = "背部箭筒 (_1.mesh)",
                    TargetBoneName = "Spine_2_WpConst",
                    PositionOffset = [0f, 0f, 0f],
                    RotationEuler = [0f, 0f, 0f],
                    Scale = 1.0f,
                    UseWeaponBaseBoneInverse = true
                },
                _ => new WeaponPartMountPreset
                {
                    PartIndex = partIndex,
                    PartDescription = $"副手部件 (_{partIndex}.mesh)",
                    TargetBoneName = "L_Hand_HJ_01",
                    PositionOffset = [0f, 0f, 0f],
                    RotationEuler = [0f, 0f, 0f],
                    Scale = 1.0f,
                    UseWeaponBaseBoneInverse = true
                }
            };
        }
    }
}
