using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Basic;
using VfxStudio.Core.Services;

namespace VfxStudio.LiveEditor;

internal static class PlayEfxSerializationTests
{
    internal static void Run(Options options, string output, Action<bool, string> check)
    {
        string fixture = Path.Combine(Path.GetDirectoryName(options.Assets)!, "tools/LiveEditor/fixtures/game-install-it02-sub021.efx.5571972");
        byte[] original = File.ReadAllBytes(fixture);
        var service = new EfxEditorService();
        var file = service.Load(original, fixture);
        try
        {
            check(original.AsSpan().SequenceEqual(service.SaveToBytes(file)),
                "Installed 38-entry EFX survives a no-op save byte-for-byte, including PlayEfx byte count");
            var path = ((EFXAttributePlayEfx)file.Actions[0].Attributes[0]).efxPath!;
            check(BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(0x280)) == Encoding.Unicode.GetByteCount(path + "\0"),
                "Independent original PAK bytes prove PlayEfx length is UTF-16 bytes including terminator");
        }
        finally { file.FileHandler.Dispose(); }

        foreach (string text in new[] { "", "Art/VFX/test.efx", "中文路径/火焰.efx", "火焰🔥.efx" })
        {
            using var stream = new MemoryStream();
            using var writer = new FileHandler(stream, "test.efx.5571972");
            var attribute = new EFXAttributePlayEfx { Version = EfxVersion.MHWilds, efxPath = text };
            attribute.Write(writer);
            byte[] data = stream.ToArray();
            check(BinaryPrimitives.ReadInt32LittleEndian(data) == Encoding.Unicode.GetByteCount(text + "\0") &&
                data.AsSpan(4).SequenceEqual(Encoding.Unicode.GetBytes(text + "\0")),
                $"PlayEfx writes exact UTF-16 wire bytes: {text}");
            using var input = new FileHandler(new MemoryStream(data), "test.efx.5571972");
            var read = new EFXAttributePlayEfx { Version = EfxVersion.MHWilds };
            read.Read(input);
            check(read.efxPath == text, "PlayEfx preserves empty, ASCII, Chinese and surrogate-pair text");
        }

        // Reproduce the reported rev_4_2 file: preserve the user's two color
        // byte edits, repair only the writer's erroneous 98 -> 49 length.
        byte[] edited = original.ToArray();
        edited[0x4ab4] = 100; edited[0x4ab5] = 100;
        byte[] legacy = edited.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(legacy.AsSpan(0x280), 49);
        string root = Path.Combine(output, "play-efx");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "game"));
        string checkpoint = Path.Combine(root, "legacy-edited.efx.5571972");
        File.WriteAllBytes(checkpoint, legacy);
        using var engine = new EditorEngine(options with { Mailbox = Path.Combine(root, "mailbox"), Output = root, GameRoot = Path.Combine(root, "game") });
        var opened = engine.Handle(new() { ["id"] = "open", ["action"] = "open", ["path"] = checkpoint });
        check(opened["ok"]!.GetValue<bool>(), "Previous editor checkpoint with the wrong character count can still open");
        long revision = opened["revision"]!.GetValue<long>();
        var published = engine.Handle(new() { ["id"] = "publish", ["action"] = "publish", ["revision"] = revision });
        check(published["ok"]!.GetValue<bool>() &&
            File.ReadAllBytes(published["outputPath"]!.GetValue<string>()).AsSpan().SequenceEqual(edited),
            "Publish repairs the malformed length while preserving every byte of the user's color edit");
        var changed = engine.Handle(new()
        {
            ["id"] = "edit", ["action"] = "set", ["revision"] = revision,
            ["target"] = new JsonObject { ["kind"] = "action", ["index"] = 0, ["attributeIndex"] = 0 },
            ["path"] = "efxPath", ["value"] = "Art/VFX/中文/火焰🔥.efx"
        });
        check(changed["ok"]!.GetValue<bool>(), "Recovered document supports subsequent parameter edits");
        var undone = engine.Handle(new() { ["id"] = "undo", ["action"] = "undo", ["revision"] = changed["revision"]!.GetValue<long>() });
        var saved = engine.Handle(new() { ["id"] = "save", ["action"] = "save", ["revision"] = undone["revision"]!.GetValue<long>() });
        check(saved["ok"]!.GetValue<bool>() &&
            File.ReadAllBytes(saved["outputPath"]!.GetValue<string>()).AsSpan().SequenceEqual(edited),
            "Undo/save preserves recovered user edits and the correct engine-compatible length");
    }
}
