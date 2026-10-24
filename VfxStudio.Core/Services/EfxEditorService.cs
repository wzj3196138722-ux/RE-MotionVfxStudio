using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ReeLib;
using ReeLib.Common;
using ReeLib.Efx;
using ReeLib.Efx.Enums;
using ReeLib.Efx.Structs.Basic;
using ReeLib.Efx.Structs.Transforms;
using VfxStudio.Core.Models;

namespace VfxStudio.Core.Services;

/// <summary>
/// Criteria for filtering EFX attributes.
/// </summary>
public class EfxAttributeFilterCriteria
{
    public EfxAttributeType? AttributeType { get; set; }
    public string? AttributeTypeName { get; set; }
    public string? PropertyOrFieldName { get; set; }
    public int? EntryIndex { get; set; }
    public string? EntryName { get; set; }
    public string? GroupName { get; set; }

    public bool Matches(EFXEntryBase entry, int entryIndex, EFXAttribute attr)
    {
        if (AttributeType.HasValue && attr.type != AttributeType.Value)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(AttributeTypeName))
        {
            var typeStr = attr.type.ToString();
            var className = attr.GetType().Name;
            if (!typeStr.Contains(AttributeTypeName, StringComparison.OrdinalIgnoreCase) &&
                !className.Contains(AttributeTypeName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (EntryIndex.HasValue && entryIndex != EntryIndex.Value)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(EntryName) &&
            !(entry.name?.Contains(EntryName, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(GroupName))
        {
            if (entry is EFXEntry effEntry)
            {
                if (!effEntry.Groups.Any(g => g.Contains(GroupName, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(PropertyOrFieldName))
        {
            var prop = attr.GetType().GetProperty(PropertyOrFieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var field = attr.GetType().GetField(PropertyOrFieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null && field == null)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// A matched attribute from filtering.
/// </summary>
public record EfxAttributeMatch(
    EFXEntryBase Entry,
    int EntryIndex,
    string EntryName,
    EFXAttribute Attribute,
    EfxAttributeType AttributeType
);

/// <summary>
/// Comprehensive service for RE Engine .efx files:
/// parameter filtering, batch overrides, group copy/paste, bone binding & transforms, and undo/redo integration.
/// </summary>
public class EfxEditorService
{
    // =========================================================================
    // I/O Operations
    // =========================================================================

    public EfxFile Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"EFX file not found: {filePath}", filePath);
        }

        var bytes = File.ReadAllBytes(filePath);
        return Load(bytes, filePath);
    }

    public EfxFile Load(byte[] data, string originalPath = "memory.efx.5571972")
    {
        if (!originalPath.Contains(".efx."))
        {
            originalPath += ".5571972";
        }
        var ms = new MemoryStream(data, writable: false);
        var handler = new FileHandler(ms, originalPath);
        var efx = new EfxFile(handler);
        efx.Read();
        return efx;
    }

    public void Save(EfxFile efx, string filePath)
    {
        SynchronizeEffectGroups(efx);
        filePath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        string temporaryPath = Path.Combine(dir!, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            // Keep the version suffix visible to FileHandler while validating the bytes.
            byte[] serialized = SaveToBytes(efx);
            var reopened = Load(serialized, filePath);
            try
            {
                if (reopened.Header.Version != efx.Header.Version ||
                    reopened.Entries.Count != efx.Entries.Count || reopened.Actions.Count != efx.Actions.Count)
                    throw new InvalidDataException("EFX save verification failed: header or structure count changed.");
            }
            finally { reopened.FileHandler.Dispose(); }
            File.WriteAllBytes(temporaryPath, serialized);
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public byte[] SaveToBytes(EfxFile efx)
    {
        SynchronizeEffectGroups(efx);
        using var ms = new MemoryStream();
        int versionNum = (int)efx.Header.Version > 0 ? (int)efx.Header.Version : 5571972;
        string virtualPath = $"output.efx.{versionNum}";
        using var handler = new FileHandler(ms, virtualPath);
        // The path supplies format/version metadata only. WriteTo defaults to
        // saving that path on disk, even for a MemoryStream-backed handler.
        if (!efx.WriteTo(handler, saveFile: false))
            throw new InvalidDataException("EFX serialization failed; no bytes were committed.");
        return ms.ToArray();
    }

    // =========================================================================
    // Parameter Filtering
    // =========================================================================

    public IReadOnlyList<EfxAttributeMatch> FilterAttributes(EfxFile efx, EfxAttributeFilterCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(efx);
        ArgumentNullException.ThrowIfNull(criteria);

        var matches = new List<EfxAttributeMatch>();

        for (int i = 0; i < efx.Entries.Count; i++)
        {
            var entry = efx.Entries[i];
            foreach (var attr in entry.Attributes)
            {
                if (criteria.Matches(entry, i, attr))
                {
                    matches.Add(new EfxAttributeMatch(entry, i, entry.name ?? $"Entry_{i}", attr, attr.type));
                }
            }
        }

        for (int i = 0; i < efx.Actions.Count; i++)
        {
            var action = efx.Actions[i];
            foreach (var attr in action.Attributes)
            {
                if (criteria.Matches(action, i, attr))
                {
                    matches.Add(new EfxAttributeMatch(action, i, action.name ?? $"Action_{i}", attr, attr.type));
                }
            }
        }

        return matches;
    }

    public IReadOnlyList<EfxAttributeMatch> FilterAttributesByType(EfxFile efx, EfxAttributeType type)
    {
        return FilterAttributes(efx, new EfxAttributeFilterCriteria { AttributeType = type });
    }

    public IReadOnlyList<EfxAttributeMatch> FilterAttributesByName(EfxFile efx, string searchPattern)
    {
        return FilterAttributes(efx, new EfxAttributeFilterCriteria { AttributeTypeName = searchPattern });
    }

    public IReadOnlyList<EfxAttributeMatch> FilterAttributesByProperty(EfxFile efx, string propertyName)
    {
        return FilterAttributes(efx, new EfxAttributeFilterCriteria { PropertyOrFieldName = propertyName });
    }

    public IReadOnlyList<string> GetAllAttributeTypeNames(EfxFile efx)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in efx.Entries)
        {
            foreach (var attr in entry.Attributes)
            {
                set.Add(attr.type.ToString());
            }
        }
        foreach (var action in efx.Actions)
        {
            foreach (var attr in action.Attributes)
            {
                set.Add(attr.type.ToString());
            }
        }
        return set.OrderBy(s => s).ToList();
    }

    // =========================================================================
    // Batch Attribute Override
    // =========================================================================

    public int BatchOverrideProperty(IEnumerable<EfxAttributeMatch> targets, string propertyOrFieldName, object? newValue)
    {
        int count = 0;
        foreach (var match in targets)
        {
            if (SetAttributeMemberValue(match.Attribute, propertyOrFieldName, newValue))
            {
                count++;
            }
        }
        return count;
    }

    public int BatchOverrideProperty(EfxFile efx, EfxAttributeFilterCriteria criteria, string propertyOrFieldName, object? newValue)
    {
        var matches = FilterAttributes(efx, criteria);
        return BatchOverrideProperty(matches, propertyOrFieldName, newValue);
    }

    public int BatchScaleVector3(IEnumerable<EfxAttributeMatch> targets, string propertyName, float scaleFactor)
    {
        int count = 0;
        foreach (var match in targets)
        {
            if (TryGetAttributeMemberValue(match.Attribute, propertyName, out var val) && val is Vector3 v)
            {
                var scaled = v * scaleFactor;
                if (SetAttributeMemberValue(match.Attribute, propertyName, scaled))
                {
                    count++;
                }
            }
        }
        return count;
    }

    public int BatchOffsetVector3(IEnumerable<EfxAttributeMatch> targets, string propertyName, Vector3 offset)
    {
        int count = 0;
        foreach (var match in targets)
        {
            if (TryGetAttributeMemberValue(match.Attribute, propertyName, out var val) && val is Vector3 v)
            {
                var shifted = v + offset;
                if (SetAttributeMemberValue(match.Attribute, propertyName, shifted))
                {
                    count++;
                }
            }
        }
        return count;
    }

    public static bool TryGetAttributeMemberValue(EFXAttribute attr, string memberName, out object? value)
    {
        value = null;
        var type = attr.GetType();
        var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop != null && prop.CanRead)
        {
            value = prop.GetValue(attr);
            return true;
        }

        var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (field != null)
        {
            value = field.GetValue(attr);
            return true;
        }

        return false;
    }

    public static bool SetAttributeMemberValue(EFXAttribute attr, string memberName, object? newValue)
    {
        var type = attr.GetType();
        var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop != null && prop.CanWrite)
        {
            var targetType = prop.PropertyType;
            var converted = ConvertValue(newValue, targetType);
            prop.SetValue(attr, converted);
            return true;
        }

        var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (field != null)
        {
            var targetType = field.FieldType;
            var converted = ConvertValue(newValue, targetType);
            field.SetValue(attr, converted);
            return true;
        }

        return false;
    }

    private static object? ConvertValue(object? val, Type targetType)
    {
        if (val == null) return null;
        if (targetType.IsAssignableFrom(val.GetType())) return val;

        if (targetType.IsEnum)
        {
            if (val is string str) return Enum.Parse(targetType, str, true);
            return Enum.ToObject(targetType, val);
        }

        if (targetType == typeof(float)) return Convert.ToSingle(val);
        if (targetType == typeof(double)) return Convert.ToDouble(val);
        if (targetType == typeof(int)) return Convert.ToInt32(val);
        if (targetType == typeof(uint)) return Convert.ToUInt32(val);
        if (targetType == typeof(short)) return Convert.ToInt16(val);
        if (targetType == typeof(ushort)) return Convert.ToUInt16(val);
        if (targetType == typeof(byte)) return Convert.ToByte(val);
        if (targetType == typeof(bool)) return Convert.ToBoolean(val);
        if (targetType == typeof(string)) return val.ToString();

        return Convert.ChangeType(val, targetType);
    }

    // =========================================================================
    // Group Structure Copy / Paste / Clone
    // =========================================================================

    public List<string> CopyGroups(EFXEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new List<string>(entry.Groups);
    }

    public void PasteGroups(EFXEntry targetEntry, IEnumerable<string> groups)
    {
        ArgumentNullException.ThrowIfNull(targetEntry);
        ArgumentNullException.ThrowIfNull(groups);

        targetEntry.Groups.Clear();
        targetEntry.Groups.AddRange(groups.Distinct());
    }

    public EFXEntry DeepCloneEntry(EfxFile efx, EFXEntry sourceEntry, string? newName = null)
    {
        ArgumentNullException.ThrowIfNull(efx);
        ArgumentNullException.ThrowIfNull(sourceEntry);

        SynchronizeEffectGroups(efx);
        int sourcePosition = efx.Entries.IndexOf(sourceEntry);
        int clonePosition = efx.Entries.Count;
        var clone = sourceEntry.Clone();
        clone.index = NextEntryId(efx);
        clone.name = newName ?? $"{sourceEntry.name ?? $"Entry_{sourceEntry.index}"}_copy";

        // RE Engine strict constraint: Attributes must be ordered by itemTypeId ascending
        clone.ReorderEntries();

        efx.Entries.Add(clone);
        if (sourcePosition >= 0)
        {
            // Repeated group names cannot identify a native group. Preserve
            // exactly which indexed groups contain the cloned source block.
            foreach (var group in efx.EffectGroups)
            {
                int occurrences = (group.efxEntryIndexes ?? []).Count(index => index == sourcePosition);
                if (occurrences == 0) continue;
                group.efxEntryIndexes = group.efxEntryIndexes!.Concat(Enumerable.Repeat(clonePosition, occurrences)).ToArray();
                group.valueCount = group.efxEntryIndexes.Length;
            }
        }
        SynchronizeEffectGroups(efx);
        return clone;
    }

    public void AddEntry(EfxFile efx, EFXEntry entry)
    {
        ArgumentNullException.ThrowIfNull(efx);
        ArgumentNullException.ThrowIfNull(entry);

        entry.index = NextEntryId(efx);
        entry.ReorderEntries();
        efx.Entries.Add(entry);
        SynchronizeEffectGroups(efx);
    }

    public bool RemoveEntry(EfxFile efx, int entryIndex)
    {
        ArgumentNullException.ThrowIfNull(efx);
        if (entryIndex < 0 || entryIndex >= efx.Entries.Count) return false;

        // Group references use list positions, so retain their original order
        // while translating positions before the list shrinks. Rebuilding from
        // the entries alone would lose each group's independently stored order.
        foreach (var group in efx.EffectGroups)
        {
            group.efxEntryIndexes = (group.efxEntryIndexes ?? Array.Empty<int>())
                .Where(index => index != entryIndex)
                .Select(index => index > entryIndex ? index - 1 : index)
                .ToArray();
            group.valueCount = group.efxEntryIndexes.Length;
        }
        efx.Entries.RemoveAt(entryIndex);

        // Serialized entry IDs are not list positions (real files contain gaps).
        // Only group membership uses positions; rebuild it without changing IDs.
        SynchronizeEffectGroups(efx);
        return true;
    }

    private static int NextEntryId(EfxFile efx) =>
        checked(efx.Entries.Select(entry => entry.index).DefaultIfEmpty(-1).Max() + 1);

    public bool AddAttributeToEntry(EFXEntryBase entry, EFXAttribute attr)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(attr);

        return entry.AddAttribute(attr);
    }

    public bool RemoveAttributeFromEntry(EFXEntryBase entry, EFXAttribute attr)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(attr);

        return entry.Attributes.Remove(attr);
    }

    /// <summary>
    /// Synchronizes membership while preserving each effect group's stored member order.
    /// </summary>
    public void SynchronizeEffectGroups(EfxFile efx)
    {
        ArgumentNullException.ThrowIfNull(efx);
        efx.UpdateEffectGroups();
    }

    // =========================================================================
    // Bone Hash Binding & Spatial Transforms
    // =========================================================================

    public (string? parentBone, uint boneHash) GetBoneBinding(EFXEntryBase entry, BoneHashRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var parentOpt = entry.Attributes.OfType<EFXAttributeParentOptions>().FirstOrDefault();
        if (parentOpt == null)
        {
            return (null, 0);
        }

        var boneName = parentOpt.ParentBone ?? parentOpt.BoneName;
        if (string.IsNullOrEmpty(boneName))
        {
            return (null, 0);
        }

        uint hash = BoneHashRegistry.GetHash(boneName);
        return (boneName, hash);
    }

    public bool SetBoneBinding(EfxFile efx, EFXEntryBase entry, string? parentBoneName, BoneHashRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var parentOpt = entry.Attributes.OfType<EFXAttributeParentOptions>().FirstOrDefault();
        if (parentOpt == null)
        {
            if (string.IsNullOrEmpty(parentBoneName)) return true; // nothing to do

            parentOpt = new EFXAttributeParentOptions
            {
                Version = entry.Version,
                ParentBone = parentBoneName,
                BoneName = parentBoneName
            };
            if (!entry.AddAttribute(parentOpt))
            {
                return false;
            }
        }
        else
        {
            parentOpt.ParentBone = parentBoneName;
            parentOpt.BoneName = parentBoneName;
        }

        // Ensure bone is in efx.Bones
        if (!string.IsNullOrEmpty(parentBoneName) && efx != null)
        {
            if (!efx.Bones.Any(b => b.name == parentBoneName))
            {
                uint hash = BoneHashRegistry.GetHash(parentBoneName);
                efx.Bones.Add(new EFXBone { name = parentBoneName, value = hash });
            }
        }

        return true;
    }

    public (Vector3 position, Vector3 rotation, Vector3 scale)? GetTransform3D(EFXEntryBase entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var transform = entry.Attributes.OfType<EFXAttributeTransform3D>().FirstOrDefault();
        if (transform == null) return null;

        return (transform.LocalPosition, transform.LocalRotation, transform.LocalScale);
    }

    public bool SetTransform3D(
        EFXEntryBase entry,
        Vector3? position = null,
        Vector3? rotation = null,
        Vector3? scale = null,
        RotationOrder? order = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var transform = entry.Attributes.OfType<EFXAttributeTransform3D>().FirstOrDefault();
        if (transform == null)
        {
            transform = new EFXAttributeTransform3D
            {
                Version = entry.Version,
                LocalPosition = position ?? Vector3.Zero,
                LocalRotation = rotation ?? Vector3.Zero,
                LocalScale = scale ?? Vector3.One,
                RotationOrder = order ?? RotationOrder.RotationOrder_XYZ
            };
            return entry.AddAttribute(transform);
        }

        if (position.HasValue) transform.LocalPosition = position.Value;
        if (rotation.HasValue) transform.LocalRotation = rotation.Value;
        if (scale.HasValue) transform.LocalScale = scale.Value;
        if (order.HasValue) transform.RotationOrder = order.Value;

        return true;
    }

    // =========================================================================
    // Undoable Command Factories
    // =========================================================================

    public IUndoableCommand CreateOverridePropertyCommand(
        IEnumerable<EfxAttributeMatch> targets,
        string propertyName,
        object? newValue)
    {
        var targetList = targets.ToList();
        var oldValues = targetList.Select(m =>
        {
            TryGetAttributeMemberValue(m.Attribute, propertyName, out var oldVal);
            return (Match: m, OldVal: oldVal);
        }).ToList();

        return new RelayUndoableCommand(
            $"Override {propertyName} on {targetList.Count} attributes",
            execute: () =>
            {
                foreach (var item in oldValues)
                {
                    SetAttributeMemberValue(item.Match.Attribute, propertyName, newValue);
                }
            },
            undo: () =>
            {
                foreach (var item in oldValues)
                {
                    SetAttributeMemberValue(item.Match.Attribute, propertyName, item.OldVal);
                }
            }
        );
    }

    public IUndoableCommand CreateSetTransformCommand(
        EFXEntryBase entry,
        Vector3? newPos,
        Vector3? newRot,
        Vector3? newScale)
    {
        var existing = GetTransform3D(entry);
        var oldPos = existing?.position ?? Vector3.Zero;
        var oldRot = existing?.rotation ?? Vector3.Zero;
        var oldScale = existing?.scale ?? Vector3.One;

        return new RelayUndoableCommand(
            $"Set Transform3D on {entry.name ?? "Entry"}",
            execute: () => SetTransform3D(entry, newPos, newRot, newScale),
            undo: () => SetTransform3D(entry, oldPos, oldRot, oldScale)
        );
    }

    public IUndoableCommand CreateSetBoneBindingCommand(
        EfxFile efx,
        EFXEntryBase entry,
        string? newBone,
        BoneHashRegistry? registry = null)
    {
        var (oldBone, _) = GetBoneBinding(entry, registry);

        return new RelayUndoableCommand(
            $"Set Bone Binding '{newBone}' on {entry.name ?? "Entry"}",
            execute: () => SetBoneBinding(efx, entry, newBone, registry),
            undo: () => SetBoneBinding(efx, entry, oldBone, registry)
        );
    }

    public IUndoableCommand CreatePasteGroupsCommand(EFXEntry targetEntry, IEnumerable<string> newGroups)
    {
        var oldGroups = CopyGroups(targetEntry);
        var newGroupsList = newGroups.ToList();

        return new RelayUndoableCommand(
            $"Paste Groups on {targetEntry.name ?? $"Entry_{targetEntry.index}"}",
            execute: () => PasteGroups(targetEntry, newGroupsList),
            undo: () => PasteGroups(targetEntry, oldGroups)
        );
    }
}
