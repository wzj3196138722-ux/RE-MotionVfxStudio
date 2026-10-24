namespace VfxStudio.Core.Models;

/// <summary>
/// 14 Weapon categories in Monster Hunter Wilds.
/// </summary>
public enum WeaponType
{
    GreatSword = 0,     // wp00 (it00)
    SwordAndShield = 1, // wp01 (it01)
    DualBlades = 2,     // wp02 (it02)
    LongSword = 3,      // wp03 (it03)
    Hammer = 4,         // wp04 (it04)
    HuntingHorn = 5,    // wp05 (it05)
    Lance = 6,          // wp06 (it06)
    Gunlance = 7,       // wp07 (it07)
    SwitchAxe = 8,      // wp08 (it08)
    ChargeBlade = 9,    // wp09 (it09)
    InsectGlaive = 10,  // wp10 (it10)
    Bow = 11,           // wp11 (it11)
    HeavyBowgun = 12,   // wp12 (it12)
    LightBowgun = 13    // wp13 (it13)
}

public class WeaponDefinition
{
    public WeaponType Type { get; set; }
    public string WpCode { get; set; } = string.Empty; // e.g. "wp00"
    public string ItCode { get; set; } = string.Empty; // e.g. "it00"
    public string NameCn { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string FirstOrdinalFolder { get; set; } = string.Empty;
    public string PrimaryAttachBone { get; set; } = "R_Hand_HJ_01";
    public string? SecondaryAttachBone { get; set; } = null;
    public bool HasSecondaryMesh { get; set; } = false;

    public static readonly IReadOnlyList<WeaponDefinition> AllWeapons = new List<WeaponDefinition>
    {
        new WeaponDefinition
        {
            Type = WeaponType.GreatSword,
            WpCode = "wp00",
            ItCode = "it00",
            NameCn = "大剑",
            NameEn = "Great Sword",
            FirstOrdinalFolder = "natives/stm/art/model/item/it00/00/0000/",
            PrimaryAttachBone = "R_Hand_HJ_01"
        },
        new WeaponDefinition
        {
            Type = WeaponType.SwordAndShield,
            WpCode = "wp01",
            ItCode = "it01",
            NameCn = "片手剑",
            NameEn = "Sword & Shield",
            FirstOrdinalFolder = "natives/stm/art/model/item/it01/00/0002/",
            PrimaryAttachBone = "R_Hand_HJ_01",
            SecondaryAttachBone = "L_Hand_HJ_01",
            HasSecondaryMesh = true // _0 is Sword, _1 is Shield
        },
        new WeaponDefinition
        {
            Type = WeaponType.DualBlades,
            WpCode = "wp02",
            ItCode = "it02",
            NameCn = "双刀",
            NameEn = "Dual Blades",
            FirstOrdinalFolder = "natives/stm/art/model/item/it02/00/0000/",
            PrimaryAttachBone = "R_Hand_HJ_01",
            SecondaryAttachBone = "L_Hand_HJ_01",
            HasSecondaryMesh = true // _0 is Right Blade, _1 is Left Blade
        },
        new WeaponDefinition
        {
            Type = WeaponType.LongSword,
            WpCode = "wp03",
            ItCode = "it03",
            NameCn = "太刀",
            NameEn = "Long Sword",
            FirstOrdinalFolder = "natives/stm/art/model/item/it03/00/0000/",
            PrimaryAttachBone = "R_Hand_HJ_01",
            SecondaryAttachBone = "Spine_2_WpConst",
            HasSecondaryMesh = true // _0 is Katana, _1 is Sheath (刀鞘)
        },
        new WeaponDefinition
        {
            Type = WeaponType.Hammer,
            WpCode = "wp04",
            ItCode = "it04",
            NameCn = "大锤",
            NameEn = "Hammer",
            FirstOrdinalFolder = "natives/stm/art/model/item/it04/00/0000/",
            PrimaryAttachBone = "R_Hand_HJ_01"
        },
        new WeaponDefinition
        {
            Type = WeaponType.HuntingHorn,
            WpCode = "wp05",
            ItCode = "it05",
            NameCn = "狩猎笛",
            NameEn = "Hunting Horn",
            FirstOrdinalFolder = "natives/stm/art/model/item/it05/00/0000/",
            PrimaryAttachBone = "R_Hand_HJ_01"
        },
        new WeaponDefinition
        {
            Type = WeaponType.Lance,
            WpCode = "wp06",
            ItCode = "it06",
            NameCn = "长枪",
            NameEn = "Lance",
            FirstOrdinalFolder = "natives/stm/art/model/item/it06/00/0000/",
            PrimaryAttachBone = "R_Hand_HJ_01",
            SecondaryAttachBone = "L_Hand_HJ_01",
            HasSecondaryMesh = true // _0 is Lance, _1 is Shield
        },
        new WeaponDefinition
        {
            Type = WeaponType.Gunlance,
            WpCode = "wp07",
            ItCode = "it07",
            NameCn = "铳枪",
            NameEn = "Gunlance",
            FirstOrdinalFolder = "natives/stm/art/model/item/it07/00/0000/",
            PrimaryAttachBone = "R_Hand_HJ_01",
            SecondaryAttachBone = "L_Hand_HJ_01",
            HasSecondaryMesh = true // _0 is Gunlance, _1 is Shield
        },
        new WeaponDefinition
        {
            Type = WeaponType.SwitchAxe,
            WpCode = "wp08",
            ItCode = "it08",
            NameCn = "斩斧",
            NameEn = "Switch Axe",
            FirstOrdinalFolder = "natives/stm/art/model/item/it08/00/0000/",
            PrimaryAttachBone = "R_Hand_HJ_01"
        },
        new WeaponDefinition
        {
            Type = WeaponType.ChargeBlade,
            WpCode = "wp09",
            ItCode = "it09",
            NameCn = "盾斧",
            NameEn = "Charge Blade",
            FirstOrdinalFolder = "natives/stm/art/model/item/it09/00/0000/",
            PrimaryAttachBone = "R_Hand_HJ_01",
            SecondaryAttachBone = "L_Hand_HJ_01",
            HasSecondaryMesh = true // _0 is Sword, _1 is Shield/Axe head
        },
        new WeaponDefinition
        {
            Type = WeaponType.InsectGlaive,
            WpCode = "wp10",
            ItCode = "it10",
            NameCn = "操虫棍",
            NameEn = "Insect Glaive",
            FirstOrdinalFolder = "natives/stm/art/model/item/it10/00/0000/",
            PrimaryAttachBone = "R_Hand_HJ_01"
        },
        new WeaponDefinition
        {
            Type = WeaponType.Bow,
            WpCode = "wp11",
            ItCode = "it11",
            NameCn = "弓",
            NameEn = "Bow",
            FirstOrdinalFolder = "natives/stm/art/model/item/it11/00/0000/",
            PrimaryAttachBone = "L_Hand_HJ_01",
            SecondaryAttachBone = "Spine_2_WpConst",
            HasSecondaryMesh = true // _0 is Bow, _1 is Quiver (箭筒)
        },
        new WeaponDefinition
        {
            Type = WeaponType.HeavyBowgun,
            WpCode = "wp12",
            ItCode = "it12",
            NameCn = "重弩",
            NameEn = "Heavy Bowgun",
            FirstOrdinalFolder = "natives/stm/art/model/item/it12/00/0000/",
            PrimaryAttachBone = "R_Hand_HJ_01"
        },
        new WeaponDefinition
        {
            Type = WeaponType.LightBowgun,
            WpCode = "wp13",
            ItCode = "it13",
            NameCn = "轻弩",
            NameEn = "Light Bowgun",
            FirstOrdinalFolder = "natives/stm/art/model/item/it13/00/0001/",
            PrimaryAttachBone = "R_Hand_HJ_01"
        }
    };

    public static WeaponDefinition? FromCode(string code)
    {
        var lower = code.ToLowerInvariant();
        return AllWeapons.FirstOrDefault(w => w.WpCode == lower || w.ItCode == lower);
    }
}
