using System.Text;
using ReeLib;
using ReeLib.Common;
using ReeLib.Uvs;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

internal static class PrecacheTests
{
    internal static int RunCommand(Options options)
    {
        int count = 0;
        string output = Path.Combine(options.Output, "precache-" + Guid.NewGuid().ToString("N"));
        try
        {
            Run(options, output, (ok, label) => { if (!ok) throw new InvalidOperationException(label); count++; Console.WriteLine("PASS " + label); });
            Console.WriteLine($"PASS: {count} precache checks; no game calls. {output}");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    internal static void Run(Options options, string output, Action<bool, string> check)
    {
        string root = Path.Combine(output, "precache-tests");
        var settings = options with { Mailbox = Path.Combine(root, "mailbox"), GameRoot = Path.Combine(root, "game") };
        Directory.CreateDirectory(settings.GameRoot);
        var cache = new PrecacheStore(settings);
        void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); } catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or InvalidOperationException) { rejected = true; }
            check(rejected, label);
        }
        foreach (string bad in new[] { "../x.tex", "C:/x.tex", "art/../x.tex", "art/CON.tex", "art//x.tex", "art/x.tex/../" })
            Reject(() => PrecacheStore.Normalize(bad), "Precache rejects unsafe virtual path " + bad);
        check(PrecacheStore.Normalize(@"ART\中文\光.tex.241106027") == "art/中文/光.tex", "Precache accepts Unicode aliases independent of source location");
        string realTex = Directory.EnumerateFiles(options.Assets, "*.tex.*", SearchOption.AllDirectories).First();
        string tex = Path.Combine(root, "source", "natives", "stm", "art", "fixture", "flare.tex.241106027");
        Directory.CreateDirectory(Path.GetDirectoryName(tex)!);
        File.Copy(realTex, tex);
        byte[] original = File.ReadAllBytes(tex);
        var texture = cache.Import(tex, "art/MyLibrary/虹光.tex");
        check(texture.Alias != PrecacheStore.OriginPath(tex) && texture.Revision == 1, "Precache public alias differs from physical source path");
        check(cache.Read(texture).SequenceEqual(original), "Precache snapshot is byte exact");
        Reject(() => cache.Import(tex, texture.Alias), "Duplicate aliases never overwrite an existing asset");
        Reject(() => cache.Import(tex, "art/MyLibrary/wrong.uvs"), "Alias extension must match the actual resource type");
        string invalid = Path.Combine(root, "broken.tex.241106027");
        File.WriteAllBytes(invalid, Encoding.UTF8.GetBytes("not a game texture"));
        Reject(() => cache.Import(invalid, null), "Renaming an arbitrary file to TEX is rejected");
        string uvs = Path.Combine(root, "source", "sequence.uvs.7");
        using (var handler = new FileHandler(uvs))
        using (var file = new UvsFile(handler))
        {
            file.Textures.Add(new TextureBlock { path = "art/fixture/flare.tex" });
            file.Sequences.Add(new SequenceBlock { patterns = [new UvsPattern { right = 1, bottom = 1, textureIndex = 0 }] });
            check(file.Write(), "Precache fixture writes a real UVS sequence");
            handler.Save();
        }
        var sequence = cache.Import(uvs, "art/MyLibrary/sequence.uvs");
        check(sequence.Dependencies.Contains("art/fixture/flare.tex"), "UVS dependency discovery preserves its full logical identity");
        var mapping = cache.Materialize();
        string GameFile(PrecacheStore.Entry e, Dictionary<string, string> map) => Path.Combine(settings.GameRoot, "natives", "stm", map[e.Alias].Replace('/', Path.DirectorySeparatorChar) + "." + e.Version);
        using (var handler = new FileHandler(GameFile(sequence, mapping)))
        using (var file = new UvsFile(handler))
        {
            check(file.Read() && file.Textures.Single().path == mapping[texture.Alias], "Imported UVS resolves its original full-path TEX dependency to an immutable cache revision");
            check(file.Sequences.Single().patterns.Single().right == 1, "UVS reference rewrite preserves sequence geometry");
        }
        string oldParent = mapping[sequence.Alias];
        var refreshed = cache.Reload(texture.Id);
        check(refreshed.Id == texture.Id && refreshed.Alias == texture.Alias && refreshed.Revision == 2, "Hot reload preserves alias and advances resource generation");
        var next = cache.Materialize();
        check(next[sequence.Alias] != oldParent && next[texture.Alias] != mapping[texture.Alias], "Changing a TEX invalidates the dependent UVS engine path");
        check(File.Exists(GameFile(sequence, mapping)), "Hot reload retains previous files for already-live references");
        check(File.ReadAllBytes(tex).SequenceEqual(original), "Import and hot reload never modify the original external file");
        check(new PrecacheStore(settings).List().Length == 2, "Precache survives backend restart");

        // Use a real EFX path field and verify that an identically named Entry is not rewritten.
        var service = new EfxEditorService();
        bool exercised = false;
        foreach (string sample in Directory.EnumerateFiles(options.Assets, "*.efx.5571972", SearchOption.AllDirectories))
        {
            using var file = service.Load(File.ReadAllBytes(sample), sample);
            var field = ContentInspector.Describe(file, file.Header.Version).FirstOrDefault(f =>
                f.ValueType == typeof(string) && f.Set != null && f.Description.Path.Split('.').Last().Contains("path", StringComparison.OrdinalIgnoreCase));
            if (field == null || file.Entries.Count == 0) continue;
            field.Set!(sequence.Alias);
            file.Entries[0].name = sequence.Alias;
            byte[] bytes = service.SaveToBytes(file);
            byte[] resolved = PrecacheStore.RewriteEfx(bytes, sample, next);
            using var parsed = service.Load(resolved, sample);
            var after = ContentInspector.Describe(parsed, parsed.Header.Version).Single(f => f.Description.Path == field.Description.Path);
            check(after.Description.Value == next[sequence.Alias], "Publishing EFX resolves virtual resource paths");
            check(parsed.Entries[0].name == sequence.Alias, "Resource path resolution does not rename an Entry");
            check(service.SaveToBytes(parsed).SequenceEqual(resolved), "Resolved EFX round-trip is byte stable");
            cache.Reload(texture.Id);
            var newest = cache.Materialize();
            byte[] loadedAgain = PrecacheStore.RewriteEfx(resolved, sample, newest);
            using var reopened = service.Load(loadedAgain, sample);
            check(ContentInspector.Describe(reopened, reopened.Header.Version).Single(f => f.Description.Path == field.Description.Path).Description.Value == newest[sequence.Alias],
                "Reopened exported EFX follows an owned resource ID to the latest dependency revision");
            exercised = true;
            break;
        }
        check(exercised, "A real EFX resource-path field was exercised");

        string realMdf = Directory.EnumerateFiles(options.Assets, "*.mdf2.*", SearchOption.AllDirectories).First();
        string mdfCopy = Path.Combine(root, Path.GetFileName(realMdf));
        using (var handler = new FileHandler(realMdf))
        using (var file = new MdfFile(handler))
        {
            check(file.Read(), "Real MDF is parsed for dependency rewrite validation");
            file.Materials.SelectMany(m => m.Textures).First().texPath = texture.Alias;
            using var target = new FileHandler(new MemoryStream(), mdfCopy);
            check(file.WriteTo(target, false), "MDF fixture retains a complete material with an imported TEX");
            target.Save(mdfCopy);
        }
        var material = cache.Import(mdfCopy, null);
        string mesh = Directory.EnumerateFiles(options.Assets, "*.mesh.*", SearchOption.AllDirectories).First();
        var geometry = cache.Import(mesh, null);
        string pfb = Directory.EnumerateFiles(options.Assets, "*.pfb.18", SearchOption.AllDirectories).First();
        var prefab = cache.Import(pfb, null);
        string motlist = Directory.EnumerateFiles(options.Assets, "*.motlist.*", SearchOption.AllDirectories).First();
        var motions = cache.Import(motlist, null);
        var all = cache.Materialize();
        using (var handler = new FileHandler(GameFile(material, all)))
        using (var file = new MdfFile(handler))
            check(file.Read() && file.Materials.SelectMany(m => m.Textures).First().texPath == all[texture.Alias], "MDF texture slot resolves to the latest imported TEX revision");
        foreach (var e in new[] { material, geometry, prefab, motions })
            check(File.Exists(GameFile(e, all)), "Precache materializes actual " + e.Kind + " content");
        var parser = new RszFileOption(GameName.mhwilds, RszParser.GetInstance(Path.Combine(options.Assets, "Lists", "rsz_pfb_classes.json")));
        using (var source = new FileHandler(pfb))
        using (var file = new PfbFile(parser, source))
        {
            check(file.Read(), "Real PFB loads against the Wilds schema");
            string dependency = file.ResourceInfoList.Select(r => r.Path).First(p => p.EndsWith(".efx", StringComparison.OrdinalIgnoreCase));
            string dependencyFile = Path.Combine(root, "dependency", "natives", "stm", dependency.Replace('/', Path.DirectorySeparatorChar) + ".5571972");
            Directory.CreateDirectory(Path.GetDirectoryName(dependencyFile)!);
            File.Copy(Directory.EnumerateFiles(options.Assets, "*.efx.5571972", SearchOption.AllDirectories).First(), dependencyFile);
            var linked = cache.Import(dependencyFile, "art/MyLibrary/prefab_child.efx");
            var mapped = cache.Materialize();
            using var verify = new FileHandler(GameFile(prefab, mapped));
            using var reread = new PfbFile(parser, verify);
            check(reread.Read() && reread.ResourceInfoList.Any(r => r.Path == mapped[linked.Alias]), "PFB resource table follows the imported EFX dependency");
            check(reread.RSZ.InstanceList.Count == file.RSZ.InstanceList.Count, "PFB dependency rewrite preserves the complete instance graph");
        }
        string blob = Path.Combine(settings.Mailbox, "precache", texture.Blob);
        File.WriteAllText(blob, "tampered");
        Reject(() => cache.Read(texture), "Changed cache content is rejected by its original SHA256");
    }
}
