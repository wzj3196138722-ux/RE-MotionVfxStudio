using ReeLib;
using ReeLib.Efx;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

internal static class EntryStructureTests
{
    public static void Run(Options options, Action<bool, string> check)
    {
        var sample = Path.Combine(options.Assets, "VfxShared/natives/stm/art/vfx/effecteditor/weapon/it00/11_it00_900.efx.5571972");
        var service = new EfxEditorService();
        var file = service.Load(sample);
        var originalIds = file.Entries.Select(e => e.index).ToArray();
        var originalGroups = file.EffectGroups.Select(g => (g.groupName, Members: g.efxEntryIndexes!.ToArray())).ToArray();
        check(originalGroups[0].Members.SequenceEqual(new[] { 2, 1 }),
            "Real weapon fixture contains independently ordered effect group members [2,1]");
        void Reload(byte[] data)
        {
            file.FileHandler.Dispose();
            file = service.Load(data, sample);
        }
        bool SameGroups((string groupName, int[] Members)[] expected) =>
            file.EffectGroups.Count == expected.Length && file.EffectGroups.Select((group, i) =>
                group.groupName == expected[i].groupName && group.efxEntryIndexes!.SequenceEqual(expected[i].Members)).All(value => value);
        // Exercise the underlying writer separately: fixing only the service
        // would still let EfxFile.DoWrite silently reorder these indices.
        using (var stream = new MemoryStream())
        using (var handler = new FileHandler(stream, "output.efx.5571972"))
        {
            file.WriteTo(handler);
            Reload(stream.ToArray());
        }
        check(SameGroups(originalGroups), "Direct Core serialization preserves every original effect group order");
        Reload(service.SaveToBytes(file));
        check(SameGroups(originalGroups), "No-op service save preserves every original effect group order");
        var count = file.Entries.Count;
        var source = file.Entries.First(e => e.Groups.Count > 0);
        var sourceGroups = source.Groups.ToArray();
        service.DeepCloneEntry(file, source);
        service.DeepCloneEntry(file, source);
        service.AddEntry(file, new EFXEntry { Version = file.Header.Version, name = "empty_test" });
        Reload(service.SaveToBytes(file));
        check(file.Entries.Select(e => e.index).Distinct().Count() == count + 3,
            "Repeated real weapon block duplication/addition keeps sparse entry IDs unique after reopening");
        check(file.Entries.Take(count).Select(e => e.index).SequenceEqual(originalIds),
            "Adding blocks preserves every original serialized entry ID");
        check(file.Entries[count].Groups.SequenceEqual(source.Groups) && file.Entries[count + 1].Groups.SequenceEqual(source.Groups),
            "Both duplicated blocks retain their effect group membership after reopening");
        check(SameGroups(originalGroups.Select(group => (group.groupName,
            sourceGroups.Contains(group.groupName) ? group.Members.Concat(new[] { count, count + 1 }).ToArray() : group.Members)).ToArray()),
            "Duplicated blocks append to their groups without sorting pre-existing members");
        var survivingIds = file.Entries.Where((_, i) => i != 1).Select(e => e.index).ToArray();
        var survivingGroups = file.Entries.Where((_, i) => i != 1).Select(e => string.Join('|', e.Groups.Order())).ToArray();
        var orderedSurvivors = file.EffectGroups.Select(g => (g.groupName,
            g.efxEntryIndexes!.Where(index => index != 1).Select(index => index > 1 ? index - 1 : index).ToArray())).ToArray();
        service.RemoveEntry(file, 1);
        Reload(service.SaveToBytes(file));
        check(file.Entries.Select(e => e.index).SequenceEqual(survivingIds),
            "Deleting an interior block preserves the IDs of all surviving blocks");
        check(file.Entries.Select(e => string.Join('|', e.Groups.Order())).SequenceEqual(survivingGroups),
            "Deleting an interior block remaps group list positions without changing membership");
        check(SameGroups(orderedSurvivors), "Deleting an interior block preserves the relative order of all surviving group members");
        check(file.EffectGroups.All(g => g.efxEntryIndexes != null && g.efxEntryIndexes.All(i => i >= 0 && i < file.Entries.Count)),
            "Structural edits leave no out-of-range effect group positions");
        // Use a non-ascending three-member group to distinguish correct remaps
        // from simply retaining still-valid numeric positions after deletion.
        var group = file.EffectGroups[0];
        group.efxEntryIndexes = group.efxEntryIndexes!.Reverse().ToArray();
        var groupOrder = group.efxEntryIndexes.ToArray();
        Reload(service.SaveToBytes(file));
        check(file.EffectGroups[0].efxEntryIndexes!.SequenceEqual(groupOrder),
            "Explicitly reordered multi-member group survives serialization unchanged");
        var groupName = file.EffectGroups[0].groupName;
        var removedPosition = groupOrder[1];
        file.Entries[removedPosition].Groups.Remove(groupName);
        var newlyAssignedPosition = file.Entries.Count - 1;
        file.Entries[newlyAssignedPosition].Groups.Add(groupName);
        Reload(service.SaveToBytes(file));
        var reassignedOrder = groupOrder.Where(index => index != removedPosition).Append(newlyAssignedPosition).ToArray();
        check(file.EffectGroups[0].efxEntryIndexes!.SequenceEqual(reassignedOrder),
            "Removing group membership preserves surviving order and appends newly assigned members");
        var afterRootRemoval = file.EffectGroups.Select(g => (g.groupName,
            g.efxEntryIndexes!.Where(index => index != 0).Select(index => index - 1).ToArray())).ToArray();
        service.RemoveEntry(file, 0);
        Reload(service.SaveToBytes(file));
        check(SameGroups(afterRootRemoval), "Removing an ungrouped leading entry remaps ordered groups without changing their traversal order");
        file.FileHandler.Dispose();
    }
}
