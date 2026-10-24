using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MotionVfx.CacheDiagnostics
{
    public sealed class GetterCodeObservation
    {
        public string Property { get; internal set; }
        public string ReportedFunctionAddress { get; internal set; }
        public string Rva { get; internal set; }
        public long FileOffset { get; internal set; }
        public string Section { get; internal set; }
        public string Hex { get; internal set; }
        public object InferredLayout { get; internal set; }
    }

    public sealed class ExecutablePropertyCodeReport
    {
        public string Status { get; internal set; } = "disk_code_only_not_authenticated";
        public string ExecutablePath { get; internal set; }
        public string MetadataReportPath { get; internal set; }
        public string MetadataReportSha256 { get; internal set; }
        public string ImageBase { get; internal set; }
        public long LengthBefore { get; internal set; }
        public long LengthAfter { get; internal set; }
        public string ModifiedUtcBefore { get; internal set; }
        public string ModifiedUtcAfter { get; internal set; }
        public string Sha256Before { get; internal set; }
        public string Sha256After { get; internal set; }
        public bool FileStable { get; internal set; }
        public int UniqueCodeReads { get; internal set; }
        public int BytesPerGetter { get; } = 64;
        public List<GetterCodeObservation> Getters { get; } = new List<GetterCodeObservation>();
        public bool RuntimeCodeVerified { get; } = false;
        public bool UsableForExecution { get; } = false;
        public bool UsableForEditing { get; } = false;
        public string AddressMappingAssumption { get; } = "Reported FunctionAddress is mapped relative to the disk PE preferred ImageBase. The actual loaded module base / ASLR relocation has not been verified.";
        public string Limitation { get; } = "These are bytes from an executable FILE, not live process code. Even an exact recognized instruction sequence is only an inferred layout; runtime code and calling convention must be independently verified. No code is executed and no file is modified.";
    }

    /// <summary>Offline PE metadata and fixed code-prefix reads only. No process or native API calls.</summary>
    public static class ExecutablePropertyCodeReader
    {
        public static readonly string[] DefaultProperties = {
            "via.effect.EffectResource.EffectData", "via.effect.EffectResource.EffectDataPtr"
        };
        private static readonly byte[] ExactGetter = { 0x48, 0x83, 0xC2, 0x58, 0x49, 0x89, 0x10, 0xC3 };
        private static string Hex(ulong number) => "0x" + number.ToString("X16");

        public static ExecutablePropertyCodeReport Read(string executablePath, string metadataReportPath, string[] propertyWhitelist = null)
        {
            if (!Path.IsPathFullyQualified(executablePath)) throw new InvalidDataException("Executable path must be absolute.");
            executablePath = Path.GetFullPath(executablePath);
            metadataReportPath = Path.GetFullPath(metadataReportPath);
            string[] wanted = propertyWhitelist ?? DefaultProperties;
            if (wanted.Length == 0 || wanted.Length > 16 || wanted.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 256) || wanted.Distinct(StringComparer.Ordinal).Count() != wanted.Length)
                throw new InvalidDataException("Provide 1-16 unique complete type.property names in the whitelist.");
            var wantedSet = new HashSet<string>(wanted, StringComparer.Ordinal);
            byte[] metadataBytes;
            using (var metadata = new FileStream(metadataReportPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (metadata.Length < 1 || metadata.Length > 8 * 1024 * 1024) throw new InvalidDataException("Metadata report exceeds the 8 MiB bound or is empty.");
                metadataBytes = new byte[(int)metadata.Length]; metadata.ReadExactly(metadataBytes);
            }
            var addresses = new Dictionary<string, ulong>(StringComparer.Ordinal);
            using (var document = JsonDocument.Parse(metadataBytes))
            {
                var root = document.RootElement;
                if (root.GetProperty("status").GetString() != "native_type_metadata_complete_not_authenticated")
                    throw new InvalidDataException("Expected a completed native type metadata diagnostic report.");
                foreach (var type in root.GetProperty("metadata").GetProperty("Types").EnumerateArray())
                {
                    string typeName = type.GetProperty("Name").GetString();
                    if (!type.TryGetProperty("FirstFieldList", out var fields) || fields.ValueKind != JsonValueKind.Object ||
                        !fields.TryGetProperty("Properties", out var properties) || properties.ValueKind != JsonValueKind.Array) continue;
                    foreach (var property in properties.EnumerateArray())
                    {
                        string name = typeName + "." + property.GetProperty("Name").GetString();
                        if (!wantedSet.Contains(name)) continue;
                        string address = property.GetProperty("FunctionAddress").GetString();
                        if (address == null || !address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                            !ulong.TryParse(address.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value) || value == 0)
                            throw new InvalidDataException("Selected property has no exact nonzero function address: " + name);
                        if (!addresses.TryAdd(name, value)) throw new InvalidDataException("Selected property appears more than once: " + name);
                    }
                }
            }
            foreach (string name in wanted) if (!addresses.ContainsKey(name)) throw new InvalidDataException("Whitelisted property was not present in the metadata report: " + name);
            var report = new ExecutablePropertyCodeReport {
                ExecutablePath = executablePath, MetadataReportPath = metadataReportPath,
                MetadataReportSha256 = Convert.ToHexString(SHA256.HashData(metadataBytes))
            };
            // Retain one read-only file handle throughout hashing, PE parsing, and code reads.
            // ShareRead prevents new writers/deleters during this observation. Existing engine
            // memory mappings are unaffected; no executable or game process is opened for write.
            using var file = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            report.LengthBefore = file.Length;
            DateTime beforeTime = File.GetLastWriteTimeUtc(executablePath);
            report.ModifiedUtcBefore = beforeTime.ToString("O");
            report.Sha256Before = HashFile(file);
            file.Position = 0;
            using (var pe = new PEReader(file, PEStreamOptions.LeaveOpen))
            {
                var headers = pe.PEHeaders;
                if (headers.PEHeader == null || headers.PEHeader.Magic != PEMagic.PE32Plus || headers.CoffHeader.Machine != Machine.Amd64)
                    throw new InvalidDataException("Expected an AMD64 PE32+ executable image.");
                ulong imageBase = headers.PEHeader.ImageBase;
                report.ImageBase = Hex(imageBase);
                var bytesByAddress = new Dictionary<ulong, byte[]>();
                foreach (string name in wanted)
                {
                    ulong address = addresses[name];
                    if (address < imageBase || address - imageBase > int.MaxValue)
                        throw new InvalidDataException("Function address cannot map to this PE preferred image base: " + name);
                    long rva = checked((long)(address - imageBase));
                    if (rva + 64 > headers.PEHeader.SizeOfImage) throw new InvalidDataException("Function prefix lies outside SizeOfImage: " + name);
                    var matching = headers.SectionHeaders.Where(section => rva >= section.VirtualAddress &&
                        rva + 64 <= (long)section.VirtualAddress + section.VirtualSize).ToArray();
                    if (matching.Length != 1) throw new InvalidDataException("Function prefix is not wholly within exactly one virtual section: " + name);
                    var selected = matching[0];
                    if ((selected.SectionCharacteristics & SectionCharacteristics.MemExecute) == 0)
                        throw new InvalidDataException("Selected function is outside an executable section: " + name);
                    long delta = checked(rva - selected.VirtualAddress);
                    if (delta + 64 > selected.SizeOfRawData || selected.PointerToRawData < 0)
                        throw new InvalidDataException("Function prefix extends beyond section raw data: " + name);
                    long offset = checked((long)selected.PointerToRawData + delta);
                    if (offset < headers.PEHeader.SizeOfHeaders || offset + 64 > file.Length)
                        throw new InvalidDataException("Function prefix extends outside the executable file: " + name);
                    if (!bytesByAddress.TryGetValue(address, out byte[] code))
                    {
                        // PEReader supplies verified headers/section mapping. Read only the
                        // requested 64 file bytes, avoiding materializing an entire large section.
                        code = new byte[64]; file.Position = offset; file.ReadExactly(code);
                        bytesByAddress.Add(address, code); report.UniqueCodeReads++;
                    }
                    report.Getters.Add(new GetterCodeObservation {
                        Property = name, ReportedFunctionAddress = Hex(address), Rva = "0x" + rva.ToString("X"),
                        FileOffset = offset, Section = selected.Name, Hex = BitConverter.ToString(code).Replace('-', ' '),
                        InferredLayout = DecodeExactKnownSequence(code)
                    });
                }
            }
            report.Sha256After = HashFile(file);
            report.LengthAfter = file.Length;
            DateTime afterTime = File.GetLastWriteTimeUtc(executablePath);
            report.ModifiedUtcAfter = afterTime.ToString("O");
            report.FileStable = report.LengthBefore == report.LengthAfter && beforeTime == afterTime && report.Sha256Before == report.Sha256After;
            if (!report.FileStable) throw new InvalidDataException("Executable length, timestamp, or hash changed during the observation.");
            return report;
        }

        private static string HashFile(FileStream file) { file.Position = 0; return Convert.ToHexString(SHA256.HashData(file)); }

        private static object DecodeExactKnownSequence(byte[] bytes)
        {
            if (!bytes.AsSpan(0, ExactGetter.Length).SequenceEqual(ExactGetter)) return null;
            return new {
                evidence = "exact_first_8_disk_bytes_only", exactHex = "48 83 C2 58 49 89 10 C3",
                instructions = new[] { "add rdx, 0x58", "mov qword ptr [r8], rdx", "ret" },
                memberOffset = 88, inferredStorage = "address_of_embedded_member_if_RDX_is_resource_receiver",
                inference = "If RDX contains the native resource base, this wrapper returns base+0x58 through R8. It does not load a pointer stored at +0x58.",
                receiverCallingConventionVerified = false, runtimeCodeVerified = false, usableForExecution = false
            };
        }

        public static string[] RunSelfFixture(string outputRoot)
        {
            Directory.CreateDirectory(outputRoot);
            string root = Path.Combine(outputRoot, "pe-getter-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string exe = Path.Combine(root, "fixture.exe"), metadata = Path.Combine(root, "metadata.json");
            var checks = new List<string>();
            byte[] original = MakeFixturePe();
            WriteMetadata(metadata, "0x0000000140001020"); File.WriteAllBytes(exe, original);
            var result = Read(exe, metadata);
            Check(result.Getters.Count == 2 && result.UniqueCodeReads == 1, "Only whitelisted properties are read; shared getter address is read once", checks);
            Check(result.Getters.All(g => g.FileOffset == 0x220 && g.Hex.StartsWith("48 83 C2 58 49 89 10 C3") && g.Hex.Length == 191), "PE ImageBase/RVA/section mapping yields the exact 64-byte getter prefix", checks);
            Check(result.Getters.All(g => g.InferredLayout != null), "Exact known eight-byte sequence receives a conditional layout inference", checks);
            Check(result.FileStable && result.Sha256Before == result.Sha256After && File.ReadAllBytes(exe).AsSpan().SequenceEqual(original), "Source length/time/hash remain unchanged", checks);
            Check(!result.RuntimeCodeVerified && !result.UsableForExecution && !result.UsableForEditing, "Disk code never gains runtime, execution, or editing certification", checks);
            byte[] changed = (byte[])original.Clone(); changed[0x223] = 0x60; File.WriteAllBytes(exe, changed);
            Check(Read(exe, metadata).Getters.All(g => g.InferredLayout == null), "Near-match instruction bytes remain raw hex without guessed decoding", checks);
            changed = (byte[])original.Clone(); WriteU16(changed, 0x84, 0x14c); RejectFixture(exe, metadata, changed, "Non-AMD64 PE is rejected", checks);
            changed = (byte[])original.Clone(); WriteU16(changed, 0x98, 0x10b); RejectFixture(exe, metadata, changed, "Non-PE32+ image is rejected", checks);
            changed = (byte[])original.Clone(); WriteU32(changed, 0x188 + 36, 0x40000040); RejectFixture(exe, metadata, changed, "Non-executable section is rejected", checks);
            changed = (byte[])original.Clone(); WriteU32(changed, 0x188 + 8, 0x30); RejectFixture(exe, metadata, changed, "Getter crossing virtual section end is rejected", checks);
            changed = (byte[])original.Clone(); WriteU32(changed, 0x188 + 16, 0x30); RejectFixture(exe, metadata, changed, "Getter crossing raw section end is rejected", checks);
            File.WriteAllBytes(exe, original); WriteMetadata(metadata, "0x000000013FFFFFF0"); RejectFixture(exe, metadata, original, "Address below preferred ImageBase is rejected", checks);
            WriteMetadata(metadata, "0x0000000140001020"); bool missing = false;
            try { Read(exe, metadata, new[] { "via.Resource.Path" }); } catch (InvalidDataException) { missing = true; }
            Check(missing, "Missing whitelisted property is rejected instead of reading an arbitrary address", checks);
            return checks.ToArray();
        }

        private static void Check(bool result, string message, List<string> checks) { if (!result) throw new Exception(message); checks.Add(message); }
        private static void RejectFixture(string exe, string metadata, byte[] bytes, string message, List<string> checks)
        {
            File.WriteAllBytes(exe, bytes); bool rejected = false;
            try { Read(exe, metadata); } catch (Exception error) when (error is InvalidDataException || error is BadImageFormatException) { rejected = true; }
            Check(rejected, message, checks);
        }
        private static void WriteMetadata(string path, string address)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { status = "native_type_metadata_complete_not_authenticated", metadata = new {
                Types = new[] { new { Name = "via.effect.EffectResource", FirstFieldList = new { Properties = new[] {
                    new { Name = "EffectData", FunctionAddress = address }, new { Name = "EffectDataPtr", FunctionAddress = address }
                } } } }
            } }));
        }
        private static void WriteU16(byte[] bytes, int offset, ushort value) { BitConverter.GetBytes(value).CopyTo(bytes, offset); }
        private static void WriteU32(byte[] bytes, int offset, uint value) { BitConverter.GetBytes(value).CopyTo(bytes, offset); }
        private static byte[] MakeFixturePe()
        {
            var bytes = new byte[1024]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; WriteU32(bytes, 0x3c, 0x80);
            bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E'; WriteU16(bytes, 0x84, 0x8664); WriteU16(bytes, 0x86, 1);
            WriteU16(bytes, 0x94, 0xF0); WriteU16(bytes, 0x96, 0x22); WriteU16(bytes, 0x98, 0x20b);
            WriteU32(bytes, 0x98 + 4, 0x200); WriteU32(bytes, 0x98 + 16, 0x1020); WriteU32(bytes, 0x98 + 20, 0x1000);
            BitConverter.GetBytes(0x140000000UL).CopyTo(bytes, 0x98 + 24);
            WriteU32(bytes, 0x98 + 32, 0x1000); WriteU32(bytes, 0x98 + 36, 0x200);
            WriteU16(bytes, 0x98 + 40, 6); WriteU16(bytes, 0x98 + 48, 6);
            WriteU32(bytes, 0x98 + 56, 0x2000); WriteU32(bytes, 0x98 + 60, 0x200); WriteU16(bytes, 0x98 + 68, 3);
            WriteU32(bytes, 0x98 + 108, 16); Encoding.ASCII.GetBytes(".text").CopyTo(bytes, 0x188);
            WriteU32(bytes, 0x188 + 8, 0x200); WriteU32(bytes, 0x188 + 12, 0x1000); WriteU32(bytes, 0x188 + 16, 0x200);
            WriteU32(bytes, 0x188 + 20, 0x200); WriteU32(bytes, 0x188 + 36, 0x60000020);
            ExactGetter.CopyTo(bytes, 0x220); return bytes;
        }
    }
}
