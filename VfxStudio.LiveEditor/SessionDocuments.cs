using ReeLib;

namespace VfxStudio.LiveEditor;

internal sealed partial class EditorEngine
{
    // In-memory only: changing the selected file or exporting never changes the
    // immutable restoration baseline. The game-session owner clears this store.
    private sealed record WorkingDocument(byte[] Bytes, byte[] Baseline, string SavedHash,
        byte[][] Undo, byte[][] Redo, int EntryHighWater, int AttributeHighWater);
    private readonly Dictionary<string, WorkingDocument> workingDocuments = new(StringComparer.OrdinalIgnoreCase);
    private byte[]? baseline;
    private string? documentSessionKey;
    private int entryIdHighWater = -1, attributeIdHighWater = -1;
    private string? gameProcessIdentity;
    internal void PollGameSession()
    {
        if (options.GameRoot == null || options.SelfTest) return;
        string? current = null;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("MonsterHunterWilds"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(Path.GetDirectoryName(process.MainModule?.FileName), Path.GetFullPath(options.GameRoot), StringComparison.OrdinalIgnoreCase))
                        current = process.Id + ":" + process.StartTime.ToUniversalTime().Ticks;
                }
                catch { return; } // Unreadable is not proof that the game ended.
            }
        }
        if (gameProcessIdentity != null && gameProcessIdentity != current)
        {
            workingDocuments.Clear(); undo.Clear(); redo.Clear();
            efxClipboard = null;
            DisposeFile(document); document = null; bytes = null; baseline = null;
            source = null; cacheSource = null; gameSource = null; savedHash = null;
            actionWorkspace = null; actionWorkingSet = new(); exportPlan = null; revision++;
        }
        gameProcessIdentity = current;
    }

    private int AllocateEntryId() => entryIdHighWater = checked(Math.Max(entryIdHighWater,
        Require().Entries.Select(e => e.index).DefaultIfEmpty(-1).Max()) + 1);

    private void StashDocument()
    {
        if (document == null || bytes == null || baseline == null || source == null) return;
        workingDocuments[documentSessionKey ?? NoteResourceKey()] = new(bytes.ToArray(), baseline.ToArray(), savedHash!,
            undo.ToArray(), redo.ToArray(), entryIdHighWater, attributeIdHighWater);
    }

    private void RestoreEntry(int position)
    {
        var current = Entry(position);
        if (baseline == null) throw new InvalidOperationException("尚无此文件的原始版本。");
        var original = service.Load(baseline, source!);
        try
        {
            var originalPosition = original.Entries.FindIndex(e => e.index == current.index);
            if (originalPosition < 0)
            {
                // A block created during this session has no original counterpart.
                Mutate(() => service.RemoveEntry(Require(), position));
                return;
            }
            var replacement = original.Entries[originalPosition].Clone();
            Mutate(() =>
            {
                var file = Require();
                file.Entries[position] = replacement;
                for (int i = 0; i < file.EffectGroups.Count; i++)
                {
                    var group = file.EffectGroups[i];
                    int expectedCount = i < original.EffectGroups.Count ? (original.EffectGroups[i].efxEntryIndexes ?? []).Count(p => p == originalPosition) : 0;
                    if ((group.efxEntryIndexes ?? []).Count(p => p == position) == expectedCount) continue;
                    var members = (group.efxEntryIndexes ?? []).Where(p => p != position).ToList();
                    if (i < original.EffectGroups.Count)
                    {
                        int count = (original.EffectGroups[i].efxEntryIndexes ?? []).Count(p => p == originalPosition);
                        members.AddRange(Enumerable.Repeat(position, count));
                    }
                    group.efxEntryIndexes = members.ToArray(); group.valueCount = members.Count;
                }
                service.SynchronizeEffectGroups(file);
            });
        }
        finally { DisposeFile(original); }
    }
}
