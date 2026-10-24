using System.Security.Cryptography;
using ReeLib;
using ReeLib.Common;
using ReeLib.Efx;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

// Isolated-process regression: CurrentDirectory is process-global.
internal static class MemorySerializationTests
{
    internal static string Run(string project, string output)
    {
        project = Path.GetFullPath(project); output = Path.GetFullPath(output);
        string run = Path.Combine(output, "memory-write-" + Guid.NewGuid().ToString("N"));
        string legacyDirectory = Path.Combine(run, "legacy-reference"), pureDirectory = Path.Combine(run, "memory-only");
        Directory.CreateDirectory(legacyDirectory); Directory.CreateDirectory(pureDirectory);
        string previousDirectory = Environment.CurrentDirectory;
        var tests = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            tests.Add(name);
        }
        var service = new EfxEditorService();
        string[] fixtures = ["weapon/it03/11_it03_005.efx.5571972", "common/guide/11_guide_200.efx.5571972"];
        try
        {
            // Every intentional old-style write is confined to this private directory.
            Environment.CurrentDirectory = pureDirectory;
            foreach (string relative in fixtures)
            {
                string fixture = Path.Combine(project, "Assets/VfxShared/natives/stm/art/vfx/effecteditor", relative);
                byte[] source = File.ReadAllBytes(fixture), hash = SHA256.HashData(source);
                byte[] reference;
                using (var original = service.Load(source, fixture))
                using (var stream = new MemoryStream())
                using (var handler = new FileHandler(stream, Path.Combine(legacyDirectory, Path.GetFileName(fixture))))
                {
                    service.SynchronizeEffectGroups(original);
                    Check(original.WriteTo(handler), relative + ": legacy serialization succeeds");
                    reference = stream.ToArray();
                    Check(File.ReadAllBytes(handler.FilePath!).AsSpan().SequenceEqual(reference), relative + ": default WriteTo writes the same bytes to disk (confirmed cause)");
                }
                using var model = service.Load(source, fixture);
                var originalHandler = model.FileHandler;
                byte[] actual = service.SaveToBytes(model);
                Check(actual.AsSpan().SequenceEqual(reference), relative + ": memory-only bytes exactly match original serialization");
                Check(ReferenceEquals(model.FileHandler, originalHandler), relative + ": source handler preserved");
                Check(!Directory.EnumerateFileSystemEntries(pureDirectory).Any(), relative + ": serialization creates no cwd files");
                using (var reopened = service.Load(actual, fixture))
                {
                    Check(reopened.Header.Version == model.Header.Version && reopened.Entries.Count == model.Entries.Count && reopened.Actions.Count == model.Actions.Count,
                        relative + ": header, root entries and Actions reopen unchanged");
                    Check(service.SaveToBytes(reopened).AsSpan().SequenceEqual(actual), relative + ": strict read/write round trip remains byte-identical");
                }
                string outputPath = Path.Combine(pureDirectory, "output.efx." + (int)model.Header.Version);
                byte[] sentinel = "USER FILE: must never be touched by SaveToBytes"u8.ToArray();
                File.WriteAllBytes(outputPath, sentinel);
                using (var locked = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Check(service.SaveToBytes(model).AsSpan().SequenceEqual(reference), relative + ": exclusive lock on same-named file cannot interfere with memory serialization");
                    var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                    {
                        var workerService = new EfxEditorService();
                        for (int i = 0; i < 4; i++)
                        {
                            using var worker = workerService.Load(source, fixture);
                            byte[] serialized = workerService.SaveToBytes(worker);
                            if (!serialized.AsSpan().SequenceEqual(reference)) return false;
                            using var reopened = workerService.Load(serialized, fixture);
                            if (!workerService.SaveToBytes(reopened).AsSpan().SequenceEqual(reference)) return false;
                        }
                        return true;
                    })).ToArray();
                    Task.WaitAll(tasks);
                    Check(tasks.All(t => t.Result), relative + ": 8 parallel workers / 32 serializations and rereads are byte-identical with the output file locked");
                }
                Check(File.ReadAllBytes(outputPath).AsSpan().SequenceEqual(sentinel), relative + ": existing same-named user file is unchanged");
                // Only the known sentinel created above, inside this test's private directory.
                File.Delete(outputPath);
                Check(SHA256.HashData(File.ReadAllBytes(fixture)).AsSpan().SequenceEqual(hash), relative + ": source fixture is unchanged");
            }
            using (var failed = new FailingEfx())
            {
                var original = failed.FileHandler;
                bool rejected = false;
                try { service.SaveToBytes(failed); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "Writer false result rejects partial bytes");
                Check(ReferenceEquals(failed.FileHandler, original), "Failed write restores original handler");
                Check(!Directory.EnumerateFileSystemEntries(pureDirectory).Any(), "Failed write creates no cwd files");
            }
            Host.AtomicJson(Path.Combine(output, "memory-serialization-regression.json"), new { ok = true, passed = tests.Count, tests, run, liveGameTested = false });
            return $"PASS: {tests.Count} memory-only serialization checks.";
        }
        catch (Exception ex)
        {
            Host.AtomicJson(Path.Combine(output, "memory-serialization-regression.json"), new { ok = false, passed = tests.Count, tests, run, error = ex.ToString() });
            throw;
        }
        finally { Environment.CurrentDirectory = previousDirectory; }
    }

    private sealed class FailingEfx() : EfxFile(new FileHandler(new MemoryStream(), "source.efx.5571972"))
    {
        protected override bool DoWrite()
        {
            FileHandler.Stream.WriteByte(42);
            return false;
        }
    }
}
