// Generated standalone REFramework.NET source. Do not edit this copy directly.
// Regenerate with tools/LiveEditor/CacheDiagnostics/Build-CacheDiagnosticsPlugin.ps1.
// Starts an explicit-request mailbox only. Candidates are diagnostic and never editing input.
using Microsoft.Win32.SafeHandles;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System;

// Source: tools/LiveEditor/CacheDiagnostics/EfxBufferValidator.cs; SHA256 BBBFB3059F255BA44A983DDB89D3B6941F1D3B579592B8E56CC4F8FE3A416294
namespace MotionVfx.CacheDiagnostics
{
    /// <summary>A structural candidate check, never proof of a live resource's identity or provenance.</summary>
    public sealed class EfxBufferValidation
    {
        public bool Valid { get; internal set; }
        public int ExactLength { get; internal set; }
        public string Error { get; internal set; } = "";
        public int EntryCount { get; internal set; }
        public int ActionCount { get; internal set; }
        public int AttributeCount { get; internal set; }
    }

    /// <summary>
    /// Bounded framing validator for the serialized MHWilds EFX format (.efx.5571972).
    /// It examines offset zero only; it neither searches for magic nor reads process memory.
    /// A valid result identifies a structurally framed candidate, not a verified cached file.
    /// Attribute bodies remain opaque and must subsequently pass the complete EFX parser.
    ///
    /// The 48-byte header contains counts and string/group table lengths, NOT a file length
    /// or format-version field. The offset-12 value called "entryLength" by old templates is
    /// only the string table byte length. Names and resource paths are inline, not native pointers.
    /// Actions have 12-byte headers, entries have 16-byte headers. Every Wilds attribute has
    /// type:int32, size:int32, UniqueID:int32, payload; size INCLUDES the four-byte UniqueID.
    /// The two UVAR type words at the end are mandatory even when both are zero.
    /// Source: Core Formats/EfxFile.cs and REE-Lib.Generators/ReeLibGenerator.cs.
    /// </summary>
    public static class EfxBufferValidator
    {
        public const int WildsVersion = 5571972;
        public const int DefaultMaximumBytes = 16 * 1024 * 1024;
        private const int MaximumCount = 65536;
        private const int MaximumAttributes = 200000;
        private const int MaximumStringBytes = 8192;
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Encoding StrictUtf16 = new UnicodeEncoding(false, false, true);

        public static EfxBufferValidation Validate(byte[] bytes, int maximumBytes = DefaultMaximumBytes)
        {
            if (bytes == null) return new EfxBufferValidation { Error = "No candidate bytes were supplied." };
            if (maximumBytes < 56 || maximumBytes > 256 * 1024 * 1024)
                return new EfxBufferValidation { Error = "The candidate bound must be between 56 bytes and 256 MiB." };
            try { return new Scanner(bytes, Math.Min(bytes.Length, maximumBytes)).Read(); }
            catch (Exception ex) when (ex is InvalidDataException || ex is OverflowException || ex is DecoderFallbackException)
            {
                return new EfxBufferValidation { Error = ex.Message };
            }
        }

        public static int Measure(byte[] bytes, int maximumBytes = DefaultMaximumBytes)
        {
            var result = Validate(bytes, maximumBytes);
            if (!result.Valid) throw new InvalidDataException(result.Error);
            return result.ExactLength;
        }

        private sealed class Scanner
        {
            private readonly byte[] bytes;
            private readonly int bound;
            private int position;
            private int attributeCount;

            public Scanner(byte[] bytes, int bound) { this.bytes = bytes; this.bound = bound; }

            private void Require(long count)
            {
                if (count < 0 || count > (long)bound - position)
                    throw new InvalidDataException($"Truncated candidate or size bound exceeded at {position}: need {count} bytes, available {bound - position}.");
            }

            private void Skip(long count) { Require(count); position = checked(position + (int)count); }

            private int Int32()
            {
                Require(4);
                // Explicit little endian, independent of the diagnostic host's byte order.
                int value = bytes[position] | bytes[position + 1] << 8 | bytes[position + 2] << 16 | bytes[position + 3] << 24;
                position += 4;
                return value;
            }

            private int Count(string label)
            {
                int value = Int32();
                if (value < 0 || value > MaximumCount)
                    throw new InvalidDataException($"Invalid {label} count {value} at {position - 4} (limit {MaximumCount}).");
                return value;
            }

            private int ByteLength(string label)
            {
                int value = Int32();
                if (value < 0 || value > bound)
                    throw new InvalidDataException($"Invalid {label} byte length {value} at {position - 4}.");
                return value;
            }

            private void ZeroTerminated(int unit, int regionEnd, int maximumBytes = MaximumStringBytes)
            {
                int start = position;
                int end = (int)Math.Min((long)regionEnd, (long)start + maximumBytes);
                for (; position <= end - unit; position += unit)
                {
                    Require(unit);
                    if (bytes[position] != 0 || (unit == 2 && bytes[position + 1] != 0)) continue;
                    (unit == 1 ? StrictUtf8 : StrictUtf16).GetCharCount(bytes, start, position - start);
                    position += unit;
                    return;
                }
                throw new InvalidDataException($"Unterminated or overlong {(unit == 1 ? "UTF-8" : "UTF-16")} string at {start}.");
            }

            private void BytePrefixedUtf16()
            {
                int count = ByteLength("UVAR string");
                if (count < 2 || count > MaximumStringBytes || (count & 1) != 0)
                    throw new InvalidDataException($"Invalid UVAR UTF-16 byte length {count}.");
                Require(count);
                int end = checked(position + count);
                ZeroTerminated(2, end, count);
                if (position != end)
                    throw new InvalidDataException("UVAR string has an early terminator or inconsistent declared length.");
            }

            private void Attributes(int count)
            {
                Require(checked(count * 12L)); // Type, size, UniqueID are mandatory for every attribute.
                attributeCount = checked(attributeCount + count);
                if (attributeCount > MaximumAttributes) throw new InvalidDataException("Attribute count exceeds the diagnostic limit.");
                for (int i = 0; i < count; i++)
                {
                    int type = Int32();
                    int size = Int32();
                    // The complete parser determines whether a positive type ID is implemented.
                    if (type < 0 || type > 4096 || size < 4)
                        throw new InvalidDataException($"Invalid attribute type/size {type}/{size} at {position - 8}.");
                    Skip(size); // Includes UniqueID; adding another four bytes would be incorrect.
                }
            }

            public EfxBufferValidation Read()
            {
                Require(48);
                if (Int32() != 0x72786665) throw new InvalidDataException("Candidate does not begin with serialized EFX magic (efxr).");
                int dimension = Int32();
                if (dimension < 0 || dimension > 1) throw new InvalidDataException("Invalid EFX dimension.");
                int entries = Count("entry");
                int stringBytes = ByteLength("string table");
                int actions = Count("action");
                int fields = Count("field parameter");
                int expressions = Count("expression parameter");
                int groups = Count("effect group");
                int groupBytes = ByteLength("effect group table");
                int bones = Count("bone");
                int relations = Count("bone relation");
                Count("property binding index"); // No independent data table in the Wilds serializer.

                Require(stringBytes);
                int stringEnd = checked(position + stringBytes);
                long minimumStrings = checked((expressions + (long)bones) * 3 + actions + fields + entries + groups);
                if (minimumStrings > stringBytes) throw new InvalidDataException("String table cannot contain the declared number of names.");
                for (int i = 0; i < expressions + bones; i++)
                {
                    ZeroTerminated(1, stringEnd);
                    ZeroTerminated(2, stringEnd);
                }
                for (int i = 0; i < actions + fields + entries + groups; i++) ZeroTerminated(1, stringEnd);
                if (position != stringEnd) throw new InvalidDataException("String table byte length does not match its names.");

                Skip(checked(expressions * 24L + bones * 8L + relations * 2L));
                for (int i = 0; i < actions; i++)
                {
                    Skip(8); // Action field and name hash.
                    Attributes(Count("action attribute"));
                }

                for (int i = 0; i < fields; i++)
                {
                    Skip(12);
                    int type = Int32();
                    Skip(4);
                    int firstValue = Int32();
                    if (type == 196)
                    {
                        // This special field stores a UTF-16 character-count bound in value_ukn1;
                        // the Core writer uses path.Length + 2 rather than the usual + 1.
                        if (firstValue < 1 || firstValue > MaximumStringBytes / 2)
                            throw new InvalidDataException("Invalid type-196 field string bound.");
                        ZeroTerminated(2, bound, checked(firstValue * 2));
                    }
                    else
                    {
                        Skip(24); // Remaining values including Wilds' extra float: 48-byte base.
                        if (type == 110 || type == 144 || type == 183 || type == 184 || type == 202 || type == 194 || type == 215 || type == 217)
                        {
                            int characters = Count("field path character");
                            if (characters < 1 || characters > MaximumStringBytes / 2)
                                throw new InvalidDataException("Invalid field path character bound.");
                            ZeroTerminated(2, bound, checked(characters * 2));
                        }
                    }
                }

                for (int i = 0; i < entries; i++)
                {
                    Skip(8); // Serialized entry ID and name hash, not a list position and file offset.
                    int assignment = Int32();
                    if (assignment < 0 || assignment > 2) throw new InvalidDataException("Invalid entry assignment.");
                    Attributes(Count("entry attribute"));
                }

                int groupStart = position;
                for (int i = 0; i < groups; i++)
                {
                    Skip(8);
                    int members = Count("group member");
                    Require(checked(members * 4L));
                    for (int j = 0; j < members; j++)
                    {
                        int entryIndex = Int32();
                        if (entryIndex < 0 || entryIndex >= entries) throw new InvalidDataException("Effect group references an invalid entry position.");
                    }
                }
                if (position - groupStart != groupBytes) throw new InvalidDataException("Effect group byte length is inconsistent.");

                int firstUvar = Int32();
                int secondUvar = Int32();
                for (int i = 0; i < 2; i++)
                {
                    int type = i == 0 ? firstUvar : secondUvar;
                    if (type < 0 || type > 2) throw new InvalidDataException("Unsupported UVAR type.");
                    if (type == 2) { BytePrefixedUtf16(); BytePrefixedUtf16(); }
                }
                return new EfxBufferValidation
                {
                    Valid = true, ExactLength = position, EntryCount = entries,
                    ActionCount = actions, AttributeCount = attributeCount
                };
            }
        }
    }
}


// Source: tools/LiveEditor/CacheDiagnostics/ReadOnlyResourceProbe.cs; SHA256 69C275A79206AC23592542F4FE04DAAD78960D92FB0127B37A3A22E5DDC5EBBC
namespace MotionVfx.CacheDiagnostics
{
    public sealed class MemoryRegionDescription
    {
        public string BaseAddress { get; internal set; } = "";
        public string AllocationBase { get; internal set; } = "";
        public ulong RegionSize { get; internal set; }
        public uint State { get; internal set; }
        public uint Protect { get; internal set; }
        public uint Type { get; internal set; }
        public bool Readable { get; internal set; }
        internal ulong Start;
        internal ulong Remaining(ulong address) { return checked(Start + RegionSize - address); }
    }

    public sealed class PointerObservation
    {
        public int NativeOffset { get; internal set; }
        public string Value { get; internal set; } = "";
        public string Status { get; internal set; } = "not_followed";
        public MemoryRegionDescription Region { get; internal set; }
        public string PrefixHex { get; internal set; }
    }

    public sealed class UnauthenticatedEfxCandidate
    {
        public string Address { get; internal set; } = "";
        public string Origin { get; internal set; } = "";
        public MemoryRegionDescription Region { get; internal set; }
        public EfxBufferValidation Framing { get; internal set; }
        public bool StableDoubleRead { get; internal set; }
        public string Status { get; internal set; } = "unauthenticated_candidate_only";
        private byte[] payload;
        internal void SetBytes(byte[] value) { payload = value; }
        public byte[] GetBytes() { return payload == null ? null : (byte[])payload.Clone(); }
    }

    public sealed class ReadOnlyProbeResult
    {
        public string HolderAddress { get; internal set; } = "";
        public string NativeResourceAddress { get; internal set; } = "";
        public string ExecutablePath { get; internal set; } = "";
        public uint RequestedAccess { get; internal set; }
        public string HandleKind { get; internal set; } = "external_read_only_0x410";
        public string[] NativeHexRows { get; internal set; }
        public List<PointerObservation> Qwords { get; } = new List<PointerObservation>();
        public List<UnauthenticatedEfxCandidate> Candidates { get; } = new List<UnauthenticatedEfxCandidate>();
        public bool HolderPointerStable { get; internal set; }
        public StringTableHeaderObservation StringTableHeader { get; internal set; }
        internal ulong NativeAddressValue;
        public string Limitation { get; } = "Only retained holder identity and bounded adjacent structures were observed. No EFX candidate is authenticated or usable for editing.";
    }

    public sealed class StringTableHeaderObservation
    {
        public int NativePointerOffset { get; } = 0x88;
        public int BytesBeforePointer { get; } = 48;
        public string Hypothesis { get; } = "Observed root+0x88 may point at a serialized string table whose 48-byte EFX header immediately precedes it. This is not an established engine layout.";
        public bool Unverified { get; } = true;
        public string TablePointer { get; internal set; }
        public string HeaderAddress { get; internal set; }
        public MemoryRegionDescription Region { get; internal set; }
        public string PrefixHex { get; internal set; }
        public string Status { get; internal set; }
        public bool PointerStable { get; internal set; }
        internal ulong PointerValue;
    }

    /// <summary>
    /// External, read-only Windows diagnostics. No injected code, engine calls, process writes,
    /// remote allocation, suspension, or direct dereferencing of another process's addresses.
    /// OpenProcess requests ONLY PROCESS_QUERY_INFORMATION | PROCESS_VM_READ (0x410).
    /// Every read checks committed readable pages before ReadProcessMemory, and requires a full
    /// successful read. A mapping may change after querying it; RPM failure then rejects the read.
    /// </summary>
    public sealed class ReadOnlyProcessMemory : IDisposable
    {
        public const uint RequestedAccess = 0x0400 | 0x0010;
        public const int MaximumReadBytes = 1024 * 1024;
        private const uint MemCommit = 0x1000, MemPrivate = 0x20000, StillActive = 259;
        private readonly SafeProcessHandle handle;
        public int ProcessId { get; private set; }
        public string ExecutablePath { get; private set; }
        public DateTime CreationTimeUtc { get; private set; }
        public bool UsesCurrentProcessPseudoHandle { get; private set; }
        private bool disposed;

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation64
        {
            public ulong BaseAddress, AllocationBase;
            public uint AllocationProtect;
            public ushort PartitionId, Padding;
            public ulong RegionSize;
            public uint State, Protect, Type, Alignment;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQueryEx(SafeProcessHandle process, IntPtr address, out MemoryBasicInformation64 information, UIntPtr length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, [Out] byte[] buffer, UIntPtr count, out UIntPtr read);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetProcessId(SafeProcessHandle process);

        public ReadOnlyProcessMemory(int processId, string expectedExecutable) : this(processId, expectedExecutable, false) { }

        // A caller already hosted inside a process may use Windows' non-owned pseudo handle.
        // No OpenProcess escalation, duplicate handle, engine API, or cross-process fallback.
        public static ReadOnlyProcessMemory CreateForCurrentProcess(string expectedExecutable)
        { return new ReadOnlyProcessMemory(Environment.ProcessId, expectedExecutable, true); }

        private ReadOnlyProcessMemory(int processId, string expectedExecutable, bool useCurrentProcessPseudoHandle)
        {
            if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) throw new PlatformNotSupportedException("This diagnostic requires 64-bit Windows.");
            if (processId <= 0 || string.IsNullOrWhiteSpace(expectedExecutable)) throw new ArgumentException("A process ID and exact expected executable path are required.");
            UsesCurrentProcessPseudoHandle = useCurrentProcessPseudoHandle;
            handle = useCurrentProcessPseudoHandle ? new SafeProcessHandle(new IntPtr(-1), false) : OpenProcess(RequestedAccess, false, processId);
            if (!useCurrentProcessPseudoHandle && handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Read-only process handle could not be opened.");
            try
            {
                uint openedProcessId = GetProcessId(handle);
                if (openedProcessId == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify the opened process ID.");
                if (openedProcessId != (uint)processId) throw new InvalidOperationException("The opened process ID differs from the discovered process.");
                ProcessId = processId;
                var path = new StringBuilder(32768); int length = path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify executable path.");
                ExecutablePath = Path.GetFullPath(path.ToString());
                if (!string.Equals(ExecutablePath, Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The opened process does not match the exact expected executable.");
                if (!GetProcessTimes(handle, out long creation, out long exitTime, out long kernelTime, out long userTime))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify the opened process creation time.");
                CreationTimeUtc = DateTime.FromFileTimeUtc(creation);
                EnsureIdentity();
            }
            catch { handle.Dispose(); throw; }
        }

        public static string Hex(ulong value) { return "0x" + value.ToString("X16"); }
        public static bool PlausiblePointer(ulong address) { return address >= 0x10000 && address <= 0x00007FFFFFFFFFFF && (address & 7) == 0; }
        public static ulong PointerAt(byte[] bytes, int offset)
        {
            if (offset < 0 || offset > bytes.Length - 8) throw new ArgumentOutOfRangeException(nameof(offset));
            return (ulong)bytes[offset] | (ulong)bytes[offset + 1] << 8 | (ulong)bytes[offset + 2] << 16 |
                (ulong)bytes[offset + 3] << 24 | (ulong)bytes[offset + 4] << 32 | (ulong)bytes[offset + 5] << 40 |
                (ulong)bytes[offset + 6] << 48 | (ulong)bytes[offset + 7] << 56;
        }

        public void EnsureAlive()
        {
            if (disposed) throw new ObjectDisposedException(nameof(ReadOnlyProcessMemory));
            if (!GetExitCodeProcess(handle, out uint code) || code != StillActive) throw new InvalidOperationException("The observed process has exited.");
        }

        public void EnsureIdentity()
        {
            EnsureAlive();
            if (GetProcessId(handle) != (uint)ProcessId) throw new InvalidOperationException("The opened process identity is no longer valid.");
            if (!GetProcessTimes(handle, out long creation, out long exitTime, out long kernelTime, out long userTime))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot revalidate the opened process creation time.");
            if (DateTime.FromFileTimeUtc(creation) != CreationTimeUtc)
                throw new InvalidOperationException("The opened process creation time changed.");
            EnsureAlive();
        }

        public MemoryRegionDescription Query(ulong address)
        {
            EnsureAlive();
            if (address < 0x10000 || address > 0x00007FFFFFFFFFFF) throw new InvalidDataException("Address is outside the allowed user address range.");
            int size = Marshal.SizeOf<MemoryBasicInformation64>();
            if (VirtualQueryEx(handle, new IntPtr(checked((long)address)), out var region, new UIntPtr((uint)size)).ToUInt64() != (ulong)size)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualQueryEx could not describe the complete memory region.");
            if (region.RegionSize == 0 || region.BaseAddress > address || checked(region.BaseAddress + region.RegionSize) <= address)
                throw new InvalidDataException("Invalid memory region boundaries.");
            uint baseProtection = region.Protect & 0xFF;
            bool protectionReadable = baseProtection == 0x02 || baseProtection == 0x04 || baseProtection == 0x08 ||
                baseProtection == 0x20 || baseProtection == 0x40 || baseProtection == 0x80;
            return new MemoryRegionDescription
            {
                Start = region.BaseAddress, BaseAddress = Hex(region.BaseAddress), AllocationBase = Hex(region.AllocationBase),
                RegionSize = region.RegionSize, State = region.State, Protect = region.Protect, Type = region.Type,
                Readable = region.State == MemCommit && (region.Protect & 0x100) == 0 && protectionReadable
            };
        }

        public byte[] ReadExact(ulong address, int count)
        {
            if (count < 1 || count > MaximumReadBytes) throw new ArgumentOutOfRangeException(nameof(count), "Read size must be 1 byte through 1 MiB.");
            if (address > 0x00007FFFFFFFFFFF || checked(address + (ulong)count) > 0x0000800000000000)
                throw new InvalidDataException("Read crosses the allowed address range.");
            var result = new byte[count];
            int done = 0;
            while (done < count)
            {
                ulong current = checked(address + (ulong)done);
                var region = Query(current);
                if (!region.Readable) throw new InvalidDataException("Read refused for uncommitted, protected, guard, or unreadable memory.");
                int chunk = (int)Math.Min((ulong)(count - done), region.Remaining(current));
                var block = new byte[chunk];
                if (!ReadProcessMemory(handle, new IntPtr(checked((long)current)), block, new UIntPtr((uint)chunk), out var read) || read.ToUInt64() != (ulong)chunk)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadProcessMemory did not return the entire checked region.");
                Buffer.BlockCopy(block, 0, result, done, chunk);
                done += chunk;
            }
            return result;
        }

        private static bool IsEfx(byte[] bytes) { return bytes.Length >= 4 && bytes[0] == 0x65 && bytes[1] == 0x66 && bytes[2] == 0x78 && bytes[3] == 0x72; }

        private void Candidate(ReadOnlyProbeResult result, ulong address, string origin, MemoryRegionDescription region, CancellationToken cancellationToken)
        {
            if (!region.Readable || region.Type != MemPrivate) return;
            int length = (int)Math.Min((ulong)MaximumReadBytes, region.Remaining(address));
            if (length < 56) return;
            var candidate = new UnauthenticatedEfxCandidate { Address = Hex(address), Origin = origin, Region = region };
            result.Candidates.Add(candidate);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] bounded = ReadExact(address, length);
                candidate.Framing = EfxBufferValidator.Validate(bounded, MaximumReadBytes);
                if (!candidate.Framing.Valid) { candidate.Status = "framing_rejected"; return; }
                int exactLength = candidate.Framing.ExactLength;
                cancellationToken.ThrowIfCancellationRequested();
                byte[] second = ReadExact(address, exactLength);
                for (int i = 0; i < exactLength; i++)
                    if (bounded[i] != second[i]) { candidate.Status = "content_changed_between_reads"; return; }
                candidate.StableDoubleRead = true;
                candidate.SetBytes(second);
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidDataException || ex is InvalidOperationException)
            {
                candidate.Status = "candidate_read_rejected: " + ex.Message;
            }
        }

        public ReadOnlyProbeResult ProbeRetainedHolder(ulong holderAddress, bool inspectDirectPointers, CancellationToken cancellationToken = default, bool inspectStringTableHeader = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PlausiblePointer(holderAddress)) throw new InvalidDataException("Retained holder address is not a plausible aligned pointer.");
            // Reference evidence, NOT an invented EffectResource payload layout:
            // REFramework shared/sdk/REManagedObject.cpp: runtime_size() returns 0x10;
            // ResourceManager.cpp Resource::create_holder stores Resource* at that wrapper offset.
            byte[] holder = ReadExact(holderAddress, 24);
            ulong nativeAddress = PointerAt(holder, 0x10);
            if (!PlausiblePointer(nativeAddress)) throw new InvalidDataException("Holder's native resource pointer is not plausible.");
            byte[] native = ReadExact(nativeAddress, 512);
            var result = new ReadOnlyProbeResult
            {
                HolderAddress = Hex(holderAddress), NativeResourceAddress = Hex(nativeAddress), ExecutablePath = ExecutablePath,
                RequestedAccess = UsesCurrentProcessPseudoHandle ? 0 : RequestedAccess,
                HandleKind = UsesCurrentProcessPseudoHandle ? "current_process_pseudo_handle_minus_one" : "external_read_only_0x410",
                NativeHexRows = HexRows(nativeAddress, native), NativeAddressValue = nativeAddress
            };
            if (IsEfx(native)) Candidate(result, nativeAddress, "native_resource_start", Query(nativeAddress), cancellationToken);
            var visited = new HashSet<ulong> { nativeAddress, holderAddress };
            for (int offset = 0; offset < 512; offset += 8)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong pointer = PointerAt(native, offset);
                var observation = new PointerObservation { NativeOffset = offset, Value = Hex(pointer) };
                result.Qwords.Add(observation);
                if (!inspectDirectPointers) continue;
                if (!PlausiblePointer(pointer)) { observation.Status = "not_an_aligned_user_pointer"; continue; }
                if (!visited.Add(pointer)) { observation.Status = "duplicate_or_root_pointer"; continue; }
                try
                {
                    var region = Query(pointer); observation.Region = region;
                    if (!region.Readable || region.Type != MemPrivate) { observation.Status = "not_readable_MEM_PRIVATE"; continue; }
                    if (region.Remaining(pointer) < 64) { observation.Status = "insufficient_same_region_prefix"; continue; }
                    byte[] prefix = ReadExact(pointer, 64);
                    observation.PrefixHex = BitConverter.ToString(prefix).Replace('-', ' ');
                    observation.Status = "direct_pointer_prefix_only";
                    if (IsEfx(prefix)) Candidate(result, pointer, "native_qword_0x" + offset.ToString("X"), region, cancellationToken);
                }
                catch (Exception ex) when (ex is Win32Exception || ex is InvalidDataException || ex is InvalidOperationException)
                { observation.Status = "read_rejected: " + ex.Message; }
            }
            if (inspectStringTableHeader)
                InspectStringTableHeader(result, PointerAt(native, 0x88), cancellationToken);
            byte[] after = ReadExact(holderAddress, 24);
            if (PointerAt(after, 0x10) != nativeAddress) throw new InvalidOperationException("Holder changed its native resource pointer during the diagnostic.");
            result.HolderPointerStable = true;
            EnsureAlive();
            return result;
        }

        private void InspectStringTableHeader(ReadOnlyProbeResult result, ulong tablePointer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = new StringTableHeaderObservation { PointerValue = tablePointer, TablePointer = Hex(tablePointer), Status = "not_read" };
            result.StringTableHeader = observation;
            EnsureStringTableHeaderPointerStable(result);
            if (!PlausiblePointer(tablePointer) || tablePointer < 0x10000 + 48)
            { observation.Status = "not_a_plausible_table_pointer"; return; }
            ulong headerAddress = checked(tablePointer - 48);
            observation.HeaderAddress = Hex(headerAddress);
            var region = Query(tablePointer);
            observation.Region = region;
            // Do not cross the queried region's start or end, even if the adjacent page
            // happens to be readable. This is one fixed hypothesis, never a backwards scan.
            if (!region.Readable || region.Type != MemPrivate || headerAddress < region.Start || region.Remaining(headerAddress) < 64)
            { observation.Status = "fixed_64_byte_prefix_outside_same_readable_private_region"; return; }
            byte[] prefix = ReadExact(headerAddress, 64);
            observation.PrefixHex = BitConverter.ToString(prefix).Replace('-', ' ');
            EnsureStringTableHeaderPointerStable(result);
            if (!IsEfx(prefix)) { observation.Status = "fixed_prefix_does_not_begin_with_efxr"; return; }
            observation.Status = "efxr_prefix_only_unverified_candidate";
            // The existing candidate reader is permitted only because this exact prefix
            // starts with magic. It validates from offset zero; it never searches a region.
            Candidate(result, headerAddress, "hypothesis_native_qword_0x88_minus_48", region, cancellationToken);
            EnsureStringTableHeaderPointerStable(result);
        }

        public void EnsureStringTableHeaderPointerStable(ReadOnlyProbeResult result)
        {
            if (result.StringTableHeader == null) return;
            byte[] pointer = ReadExact(checked(result.NativeAddressValue + 0x88), 8);
            if (PointerAt(pointer, 0) != result.StringTableHeader.PointerValue)
                throw new InvalidOperationException("Root+0x88 pointer changed during the explicit string-table-header observation.");
            result.StringTableHeader.PointerStable = true;
        }

        private static string[] HexRows(ulong baseAddress, byte[] bytes)
        {
            var result = new string[(bytes.Length + 15) / 16];
            for (int i = 0; i < result.Length; i++)
                result[i] = Hex(checked(baseAddress + (ulong)(i * 16))) + "  " + BitConverter.ToString(bytes, i * 16, Math.Min(16, bytes.Length - i * 16)).Replace('-', ' ');
            return result;
        }

        public void Dispose() { if (!disposed) { disposed = true; handle.Dispose(); } }

        /// <summary>Only this diagnostic process's own pinned managed fixture is inspected.</summary>
        public static string[] RunSelfFixture()
        {
            var checks = new List<string>();
            var fixture = new byte[4096];
            for (int i = 0; i < fixture.Length; i++) fixture[i] = (byte)((i * 17 + 23) & 255);
            var pin = GCHandle.Alloc(fixture, GCHandleType.Pinned);
            try
            {
                ulong address = checked((ulong)pin.AddrOfPinnedObject().ToInt64());
                using (var reader = new ReadOnlyProcessMemory(Environment.ProcessId, Environment.ProcessPath))
                {
                    byte[] actual = reader.ReadExact(address, fixture.Length);
                    for (int i = 0; i < fixture.Length; i++) if (fixture[i] != actual[i]) throw new Exception("Self fixture read mismatch.");
                    checks.Add("ReadProcessMemory matches all 4096 self-fixture bytes");
                    if (!reader.Query(address).Readable) throw new Exception("Self fixture unexpectedly unreadable.");
                    checks.Add("VirtualQueryEx confirms committed readable fixture pages");
                    using (var process = System.Diagnostics.Process.GetCurrentProcess())
                        if (reader.CreationTimeUtc != process.StartTime.ToUniversalTime()) throw new Exception("Opened handle process creation time differs.");
                    checks.Add("Creation time belongs to the opened handle and matches the self process");
                    reader.EnsureIdentity();
                    if (reader.ProcessId != Environment.ProcessId) throw new Exception("Opened handle PID differs from the self process.");
                    checks.Add("Same-handle PID and creation time revalidation succeeds without module enumeration");
                    bool rejected = false;
                    try { reader.ReadExact(1, 24); } catch (InvalidDataException) { rejected = true; }
                    if (!rejected) throw new Exception("Invalid pointer was accepted.");
                    checks.Add("Invalid pointer rejected before process memory copying");
                    rejected = false;
                    try { reader.ReadExact(address, MaximumReadBytes + 1); } catch (ArgumentOutOfRangeException) { rejected = true; }
                    if (!rejected) throw new Exception("Oversized read was accepted.");
                    checks.Add("Reads above 1 MiB are rejected");
                    // Synthetic holder/native/child layout entirely inside the already pinned
                    // owned fixture. No EFX magic is used, so the region candidate path cannot
                    // inspect bytes beyond this owned fixture during the self test.
                    Array.Clear(fixture, 0, fixture.Length);
                    ulong nativeAddress = checked(address + 128), childAddress = checked(address + 1024);
                    Buffer.BlockCopy(BitConverter.GetBytes(nativeAddress), 0, fixture, 16, 8);
                    Buffer.BlockCopy(BitConverter.GetBytes(childAddress), 0, fixture, 128 + 24, 8);
                    Buffer.BlockCopy(BitConverter.GetBytes(0x10000UL), 0, fixture, 1024 + 16, 8);
                    fixture[1024 + 63] = 0xAB;
                    var rootsOnly = reader.ProbeRetainedHolder(address, false);
                    if (rootsOnly.NativeResourceAddress != Hex(nativeAddress) || !rootsOnly.HolderPointerStable ||
                        rootsOnly.Qwords.Count != 64 || rootsOnly.NativeHexRows.Length != 32 || rootsOnly.Candidates.Count != 0 ||
                        rootsOnly.Qwords[3].Status != "not_followed") throw new Exception("Root-only synthetic holder probe failed.");
                    checks.Add("Synthetic retained holder resolves +0x10 and reports exactly 512 native bytes / 64 qwords");
                    var direct = reader.ProbeRetainedHolder(address, true);
                    int followed = 0;
                    foreach (var observation in direct.Qwords)
                        if (observation.Status == "direct_pointer_prefix_only") followed++;
                    if (followed != 1 || direct.Candidates.Count != 0 || direct.Qwords[3].Value != Hex(childAddress) ||
                        direct.Qwords[3].PrefixHex.Length != 191 || !direct.Qwords[3].PrefixHex.EndsWith("AB", StringComparison.Ordinal))
                        throw new Exception("Direct-child synthetic holder probe exceeded or missed its bounded prefix.");
                    checks.Add("Optional direct-pointer probe reads one 64-byte child prefix without following its nested pointer");
                }
                bool wrongPathRejected = false;
                try { using (var wrong = new ReadOnlyProcessMemory(Environment.ProcessId, Path.Combine(Path.GetTempPath(), "not-the-current-executable.exe"))) { } }
                catch (InvalidOperationException) { wrongPathRejected = true; }
                if (!wrongPathRejected) throw new Exception("Wrong executable identity was accepted.");
                checks.Add("Opened process executable must match the expected full path");
                checks.Add("Requested process access is exactly QUERY_INFORMATION | VM_READ (0x410)");
                return checks.ToArray();
            }
            finally { pin.Free(); }
        }
    }
}


// Source: tools/LiveEditor/CacheDiagnostics/NativeTypeMetadataProbe.cs; SHA256 4B26080CE763ECED02C7CAB73F080BE89D3FBB738CC16CCA768E65DF8E237A66
namespace MotionVfx.CacheDiagnostics
{
    public sealed class NativeMethodMetadata
    {
        public int Index { get; internal set; }
        public string Name { get; internal set; }
        public string DescriptorAddress { get; internal set; }
        public string RawDescriptorSlot18 { get; internal set; }
        public string FunctionAddress { get; internal set; }
        public string FunctionPageKind { get; internal set; }
        public int ParameterCount { get; internal set; }
        public string ParametersAddress { get; internal set; }
        public string Limitation { get; } = "Descriptor +0x18 is only a raw unproven slot: current-game observations do not validate the older function-pointer layout. No method address is authenticated or followed. Parameter objects and return-type name were not read; no function was called.";
    }

    public sealed class NativePropertyMetadata
    {
        public int Index { get; internal set; }
        public string Name { get; internal set; }
        public string TypeName { get; internal set; }
        public string DescriptorAddress { get; internal set; }
        public string FunctionAddress { get; internal set; }
        public string FunctionPageKind { get; internal set; }
        public string GetterPrefixHex { get; internal set; }
        public string GetterPrefixStatus { get; internal set; } = "not_requested_for_this_property";
        public bool GetterPrefixUnverified { get; } = true;
    }

    public sealed class NativeFieldListMetadata
    {
        public string Address { get; internal set; }
        public string NextAddress { get; internal set; }
        public string MethodsAddress { get; internal set; }
        public int MethodCount { get; internal set; }
        public int MethodCapacity { get; internal set; }
        public string VariablesAddress { get; internal set; }
        public string DeserializerAddress { get; internal set; }
        public string DeserializerPageKind { get; internal set; }
        public int PropertyCount { get; internal set; }
        public int PropertyCapacity { get; internal set; }
        public List<NativeMethodMetadata> Methods { get; } = new List<NativeMethodMetadata>();
        public List<NativePropertyMetadata> Properties { get; } = new List<NativePropertyMetadata>();
    }

    public sealed class NativeTypeMetadata
    {
        public string Name { get; internal set; }
        public string Address { get; internal set; }
        public uint Size { get; internal set; }
        public uint ClassIndex { get; internal set; }
        public uint Crc { get; internal set; }
        public string ParentAddress { get; internal set; }
        public string ParentNameIfAlsoSelected { get; internal set; }
        public string FieldsAddress { get; internal set; }
        public string ClassInfoAddress { get; internal set; }
        public NativeFieldListMetadata FirstFieldList { get; internal set; }
        public List<string> Notes { get; } = new List<string>();
    }

    public sealed class NativeTypeMetadataResult
    {
        public string Status { get; internal set; } = "metadata_only_not_a_resource_layout";
        public string TypeListAddress { get; internal set; }
        public string TypeListDataAddress { get; internal set; }
        public int DeclaredSize { get; internal set; }
        public int AllocatedSlots { get; internal set; }
        public int ScannedSlots { get; internal set; }
        public int UnreadableOrInvalidTypes { get; internal set; }
        public long ReadBytes { get; internal set; }
        public bool TableAndSelectedMetadataStable { get; internal set; }
        public List<NativeTypeMetadata> Types { get; } = new List<NativeTypeMetadata>();
        public List<string> MissingExactNames { get; } = new List<string>();
        public string LayoutBasis { get; } = "REFramework shared/sdk/ReClass_Internal_MHWILDS.hpp TypeList (16-byte header), REVariableList (0x18), VariableDescriptor; RETypeLayouts.hpp reclass_mhwilds::REType (0x68); types/REReflection.hpp REFieldList (0x34); REType.cpp get_method_desc proves array -> top -> holder -> descriptor. The old FunctionDescriptor +0x18 function-pointer interpretation is not validated for the current game.";
        public string Limitation { get; } = "Only static native type metadata was read. No getter, method, deserializer, or virtual function was called. No Resource instance layout, EFX bytes, or serialization capability is authenticated. The caller must verify this TypeList address belongs to the current process before calling.";
    }

    /// <summary>
    /// Reads only a caller-verified MHWilds TypeList, bounded names, and the three exact
    /// selected types' immediate REFieldList descriptors. It does not walk parent/next
    /// field lists, parameter arrays, vtables, classInfo, or resource instances.
    /// Only the two observed EffectResource EffectData properties may include a fixed,
    /// unverified 16-byte getter prefix from readable executable image memory; never called.
    /// </summary>
    public static class NativeTypeMetadataProbe
    {
        // The observed MHWilds table has 147453 allocated slots; retain a finite
        // ceiling with modest headroom, rather than the framework's broad 9999999 heuristic.
        public const int MaximumTypeSlots = 163840;
        public const int MaximumNameBytes = 128;
        public const int MaximumDirectMethods = 128;
        public const int MaximumDirectProperties = 128;
        public const int MaximumParameters = 16;
        public const int PropertyGetterPrefixBytes = 16;
        public const long MaximumTotalReadBytes = 40L * 1024 * 1024;
        public const int MaximumElapsedSeconds = 30;
        private static readonly string[] WantedNames = { "via.effect.EffectResource", "via.Resource", "via.effect.detail.EffectData" };
        private static readonly HashSet<string> WantedNameSet = new HashSet<string>(WantedNames, StringComparer.Ordinal);

        private sealed class Budget
        {
            internal readonly ReadOnlyProcessMemory Memory;
            internal readonly CancellationToken Token;
            internal readonly Stopwatch Clock = Stopwatch.StartNew();
            internal long Bytes;
            internal Budget(ReadOnlyProcessMemory memory, CancellationToken token) { Memory = memory; Token = token; }
            internal void Check()
            {
                Token.ThrowIfCancellationRequested();
                if (Clock.Elapsed > TimeSpan.FromSeconds(MaximumElapsedSeconds)) throw new TimeoutException("Native type metadata scan exceeded its bounded duration.");
            }
            internal byte[] Read(ulong address, int count)
            {
                Check();
                if (count <= 0 || Bytes > MaximumTotalReadBytes - count) throw new InvalidOperationException("Native type metadata read budget exceeded.");
                Bytes += count;
                var value = new byte[count];
                int completed = 0;
                while (completed < count)
                {
                    Check();
                    int length = Math.Min(count - completed, ReadOnlyProcessMemory.MaximumReadBytes);
                    byte[] chunk = Memory.ReadExact(checked(address + (ulong)completed), length);
                    Buffer.BlockCopy(chunk, 0, value, completed, length);
                    completed += length;
                }
                Check();
                return value;
            }
        }

        private sealed class SelectedSnapshot
        {
            internal ulong Address, NameAddress, FieldsAddress;
            internal byte[] TypeBytes, NameBytes, FieldBytes;
            internal NativeTypeMetadata Value;
            internal readonly List<MetadataRead> DescriptorReads = new List<MetadataRead>();
        }

        private sealed class MetadataRead
        {
            internal ulong Address;
            internal byte[] Bytes;
            internal bool ExecutableImagePrefix;
        }

        private static string Hex(ulong value) { return ReadOnlyProcessMemory.Hex(value); }
        private static ulong Ptr(byte[] bytes, int offset) { return ReadOnlyProcessMemory.PointerAt(bytes, offset); }
        private static bool ReadFailure(Exception error) { return error is InvalidDataException || error is Win32Exception || error is ArgumentException || error is OverflowException; }
        private static bool Same(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        // Names may be unaligned and terminate near the end of a committed page.
        // Stop at the first NUL without reading any inaccessible following page.
        private static byte[] ReadAsciiName(Budget budget, ulong address)
        {
            if (address < 0x10000 || address > 0x00007FFFFFFFFFFF) throw new InvalidDataException("Invalid native type name pointer.");
            var result = new List<byte>(MaximumNameBytes);
            while (result.Count < MaximumNameBytes)
            {
                budget.Check();
                ulong current = checked(address + (ulong)result.Count);
                var region = budget.Memory.Query(current);
                if (!region.Readable) throw new InvalidDataException("Native type name is not readable.");
                int count = (int)Math.Min((ulong)(MaximumNameBytes - result.Count), region.Remaining(current));
                var bytes = budget.Read(current, count);
                foreach (byte value in bytes)
                {
                    result.Add(value);
                    if (value == 0)
                    {
                        if (result.Count == 1) throw new InvalidDataException("Empty native type name.");
                        return result.ToArray();
                    }
                    if (value < 0x20 || value > 0x7E) throw new InvalidDataException("Native type name is not bounded printable ASCII.");
                }
            }
            throw new InvalidDataException("Native type name has no terminator within 128 bytes.");
        }

        private static byte[] TrackRead(Budget budget, SelectedSnapshot snapshot, ulong address, int count)
        {
            if (!ReadOnlyProcessMemory.PlausiblePointer(address)) throw new InvalidDataException("Invalid direct reflection metadata pointer: " + Hex(address));
            byte[] bytes = budget.Read(address, count);
            snapshot.DescriptorReads.Add(new MetadataRead { Address = address, Bytes = bytes });
            return bytes;
        }

        private static string TrackName(Budget budget, SelectedSnapshot snapshot, ulong address)
        {
            if (address == 0) return null;
            byte[] bytes = ReadAsciiName(budget, address);
            snapshot.DescriptorReads.Add(new MetadataRead { Address = address, Bytes = bytes });
            return Encoding.ASCII.GetString(bytes, 0, bytes.Length - 1);
        }

        private static string FunctionPageKind(Budget budget, ulong address)
        {
            if (address == 0) return "absent";
            try
            {
                budget.Check();
                var region = budget.Memory.Query(address);
                uint protection = region.Protect & 0xFF;
                bool executable = protection == 0x20 || protection == 0x40 || protection == 0x80;
                return region.Readable && executable && region.Type == 0x1000000
                    ? "readable_executable_image_address_only" : "not_a_verified_readable_executable_image";
            }
            catch (Exception error) when (ReadFailure(error)) { return "unreadable_address_only"; }
        }

        private static bool IsReadableExecutableImagePrefix(Budget budget, ulong address)
        {
            budget.Check();
            var region = budget.Memory.Query(address);
            uint protection = region.Protect & 0xFF;
            bool executable = protection == 0x20 || protection == 0x40 || protection == 0x80;
            return region.Readable && executable && region.Type == 0x1000000 && region.Remaining(address) >= PropertyGetterPrefixBytes;
        }

        private static void ObserveEffectDataGetter(Budget budget, SelectedSnapshot snapshot, NativePropertyMetadata property, ulong address)
        {
            if (snapshot.Value.Name != "via.effect.EffectResource" || property.TypeName != "via.effect.detail.EffectData" ||
                (property.Name != "EffectData" && property.Name != "EffectDataPtr")) return;
            property.GetterPrefixStatus = "not_read_not_a_bounded_readable_executable_image";
            try
            {
                if (!IsReadableExecutableImagePrefix(budget, address)) return;
                byte[] prefix = budget.Read(address, PropertyGetterPrefixBytes);
                snapshot.DescriptorReads.Add(new MetadataRead { Address = address, Bytes = prefix, ExecutableImagePrefix = true });
                property.GetterPrefixHex = BitConverter.ToString(prefix).Replace('-', ' ');
                property.GetterPrefixStatus = "unverified_16_byte_prefix_only_never_called";
            }
            catch (Exception error) when (ReadFailure(error))
            { property.GetterPrefixStatus = "not_read_image_prefix_unavailable"; }
        }

        private static void DirectDescriptors(Budget budget, SelectedSnapshot snapshot, NativeFieldListMetadata metadata, byte[] fields)
        {
            int count = metadata.MethodCount;
            if (count < 0 || count > MaximumDirectMethods || metadata.MethodCapacity < count)
                throw new InvalidDataException("Direct reflection method counts rejected: count=" + count + ", capacity=" + metadata.MethodCapacity + ", maximum=" + MaximumDirectMethods);
            if (count > 0)
            {
                byte[] methods = TrackRead(budget, snapshot, Ptr(fields, 0x10), count * 8);
                for (int index = 0; index < count; index++)
                {
                    budget.Check();
                    ulong top = Ptr(methods, index * 8);
                    if (top == 0) continue;
                    ulong holder = Ptr(TrackRead(budget, snapshot, top, 8), 0);
                    if (holder == 0) continue;
                    ulong descriptor = Ptr(TrackRead(budget, snapshot, holder, 8), 0);
                    if (descriptor == 0) continue;
                    // The current game's +0x18 values include small integers rather
                    // than function pointers. Preserve this slot only as unproven data;
                    // do not query, follow, or execute it. Return-type offsets also disagree.
                    byte[] method = TrackRead(budget, snapshot, descriptor, 0x20);
                    int parameters = BitConverter.ToInt32(method, 0x14);
                    if (parameters < 0 || parameters > MaximumParameters)
                        throw new InvalidDataException("Direct method parameter count rejected at " + Hex(descriptor) + ": count=" + parameters + ", maximum=" + MaximumParameters);
                    string name = TrackName(budget, snapshot, Ptr(method, 0));
                    if (name == null) continue;
                    metadata.Methods.Add(new NativeMethodMetadata { Index = index, Name = name,
                        DescriptorAddress = Hex(descriptor), RawDescriptorSlot18 = Hex(Ptr(method, 0x18)), FunctionAddress = null,
                        FunctionPageKind = "unproven_descriptor_slot_not_a_callable_address",
                        ParameterCount = parameters, ParametersAddress = Hex(Ptr(method, 8)) });
                }
            }
            ulong variables = Ptr(fields, 0x20);
            if (variables == 0) return;
            byte[] variableHeader = TrackRead(budget, snapshot, variables, 0x18);
            int propertyCount = BitConverter.ToInt32(variableHeader, 0x10), capacity = BitConverter.ToInt32(variableHeader, 0x14);
            metadata.PropertyCount = propertyCount; metadata.PropertyCapacity = capacity;
            if (propertyCount < 0 || propertyCount > MaximumDirectProperties || capacity < propertyCount)
                throw new InvalidDataException("Direct reflection property counts rejected: count=" + propertyCount + ", capacity=" + capacity + ", maximum=" + MaximumDirectProperties);
            if (propertyCount == 0) return;
            byte[] properties = TrackRead(budget, snapshot, Ptr(variableHeader, 8), propertyCount * 8);
            for (int index = 0; index < propertyCount; index++)
            {
                budget.Check();
                ulong descriptor = Ptr(properties, index * 8);
                if (descriptor == 0) continue;
                byte[] property = TrackRead(budget, snapshot, descriptor, 0x28);
                string name = TrackName(budget, snapshot, Ptr(property, 0));
                if (name == null) continue;
                ulong function = Ptr(property, 0x10);
                var value = new NativePropertyMetadata { Index = index, Name = name,
                    DescriptorAddress = Hex(descriptor), FunctionAddress = Hex(function), FunctionPageKind = FunctionPageKind(budget, function),
                    TypeName = TrackName(budget, snapshot, Ptr(property, 0x20)) };
                ObserveEffectDataGetter(budget, snapshot, value, function);
                metadata.Properties.Add(value);
            }
        }

        private static SelectedSnapshot Selected(Budget budget, ulong address, byte[] type, byte[] nameBytes, string name)
        {
            ulong fields = Ptr(type, 0x58);
            var value = new NativeTypeMetadata
            {
                Name = name, Address = Hex(address), Size = BitConverter.ToUInt32(type, 0x30),
                ClassIndex = BitConverter.ToUInt32(type, 8), Crc = BitConverter.ToUInt32(type, 0x2C),
                ParentAddress = Hex(Ptr(type, 0x40)), FieldsAddress = Hex(fields), ClassInfoAddress = Hex(Ptr(type, 0x60))
            };
            var snapshot = new SelectedSnapshot { Address = address, NameAddress = Ptr(type, 0x20), FieldsAddress = fields,
                TypeBytes = type, NameBytes = nameBytes, Value = value };
            if (fields == 0) value.Notes.Add("No direct REFieldList; no parent or next list was traversed.");
            else
            {
                if (!ReadOnlyProcessMemory.PlausiblePointer(fields)) throw new InvalidDataException("Selected native type has an invalid REFieldList pointer.");
                snapshot.FieldBytes = budget.Read(fields, 0x34);
                byte[] bytes = snapshot.FieldBytes;
                ulong deserializer = Ptr(bytes, 0x28);
                var metadata = new NativeFieldListMetadata
                {
                    Address = Hex(fields), NextAddress = Hex(Ptr(bytes, 8)), MethodsAddress = Hex(Ptr(bytes, 0x10)),
                    MethodCount = BitConverter.ToInt32(bytes, 0x18), MethodCapacity = BitConverter.ToInt32(bytes, 0x1C),
                    VariablesAddress = Hex(Ptr(bytes, 0x20)), DeserializerAddress = Hex(deserializer),
                    DeserializerPageKind = FunctionPageKind(budget, deserializer)
                };
                DirectDescriptors(budget, snapshot, metadata, bytes);
                value.FirstFieldList = metadata;
                value.Notes.Add("Only descriptors on the immediate field list were read; next lists and parameter objects were not traversed. Names and function addresses do not prove an EFX loader or serializer.");
            }
            return snapshot;
        }

        public static NativeTypeMetadataResult Read(ReadOnlyProcessMemory memory, ulong verifiedTypeListAddress, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (memory == null) throw new ArgumentNullException(nameof(memory));
            if (!memory.UsesCurrentProcessPseudoHandle || memory.ProcessId != Environment.ProcessId)
                throw new InvalidOperationException("Native type metadata diagnostics require the existing current-process pseudo handle.");
            if (!ReadOnlyProcessMemory.PlausiblePointer(verifiedTypeListAddress)) throw new ArgumentException("Invalid verified TypeList address.", nameof(verifiedTypeListAddress));
            var budget = new Budget(memory, cancellationToken);
            budget.Check(); memory.EnsureIdentity();
            byte[] header = budget.Read(verifiedTypeListAddress, 16);
            ulong data = Ptr(header, 0);
            int size = BitConverter.ToInt32(header, 8), allocated = BitConverter.ToInt32(header, 12);
            if (size < 0 || allocated < size || allocated < 0 || allocated > MaximumTypeSlots)
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "TypeList counts violate the bounded native table layout: size={0}, allocated={1}, maximumSlots={2}, typeList={3}, data={4}, header16Hex={5}.",
                    size, allocated, MaximumTypeSlots, Hex(verifiedTypeListAddress), Hex(data), BitConverter.ToString(header).Replace('-', ' ')));
            if (allocated != 0 && !ReadOnlyProcessMemory.PlausiblePointer(data)) throw new InvalidDataException("Invalid TypeList data pointer.");
            byte[] table = allocated == 0 ? Array.Empty<byte>() : budget.Read(data, checked(allocated * 8));
            var result = new NativeTypeMetadataResult { TypeListAddress = Hex(verifiedTypeListAddress), TypeListDataAddress = Hex(data), DeclaredSize = size, AllocatedSlots = allocated };
            var selected = new Dictionary<string, SelectedSnapshot>(StringComparer.Ordinal);
            var seenAddresses = new HashSet<ulong>();
            for (int index = 0; index < allocated; index++)
            {
                budget.Check(); result.ScannedSlots++;
                ulong address = Ptr(table, index * 8);
                if (address == 0 || !seenAddresses.Add(address)) continue;
                byte[] type, nameBytes; string name;
                try
                {
                    if (!ReadOnlyProcessMemory.PlausiblePointer(address)) throw new InvalidDataException("Invalid native type pointer.");
                    type = budget.Read(address, 0x68);
                    nameBytes = ReadAsciiName(budget, Ptr(type, 0x20));
                    name = Encoding.ASCII.GetString(nameBytes, 0, nameBytes.Length - 1);
                }
                catch (Exception error) when (ReadFailure(error)) { result.UnreadableOrInvalidTypes++; continue; }
                if (!WantedNameSet.Contains(name)) continue;
                if (selected.ContainsKey(name)) throw new InvalidDataException("Ambiguous duplicate exact native type: " + name);
                selected.Add(name, Selected(budget, address, type, nameBytes, name));
            }

            // Comparing the whole pointer table detects mutation outside the selected
            // slots too. A stable header alone does not establish a stable type table.
            if (!Same(header, budget.Read(verifiedTypeListAddress, 16)))
                throw new InvalidDataException("TypeList changed during metadata capture; all results rejected.");
            foreach (var entry in selected.Values)
            {
                if (!Same(entry.TypeBytes, budget.Read(entry.Address, 0x68)) ||
                    !Same(entry.NameBytes, ReadAsciiName(budget, entry.NameAddress)) ||
                    (entry.FieldBytes != null && !Same(entry.FieldBytes, budget.Read(entry.FieldsAddress, 0x34))))
                    throw new InvalidDataException("Selected native metadata changed during capture; all results rejected.");
                foreach (var read in entry.DescriptorReads)
                {
                    if (read.ExecutableImagePrefix && !IsReadableExecutableImagePrefix(budget, read.Address))
                        throw new InvalidDataException("Selected getter image mapping changed during capture; all results rejected.");
                    if (!Same(read.Bytes, budget.Read(read.Address, read.Bytes.Length)))
                        throw new InvalidDataException("Direct reflection descriptor, array, link, name, or getter prefix changed during capture; all results rejected.");
                }
            }
            // Check the complete table and its header again after selected metadata reads.
            if ((allocated != 0 && !Same(table, budget.Read(data, allocated * 8))) ||
                !Same(header, budget.Read(verifiedTypeListAddress, 16)))
                throw new InvalidDataException("TypeList changed during final validation.");
            budget.Check(); memory.EnsureIdentity();
            foreach (string name in WantedNames)
            {
                if (!selected.TryGetValue(name, out var entry)) { result.MissingExactNames.Add(name); continue; }
                foreach (var parent in selected.Values)
                    if (entry.Value.ParentAddress == parent.Value.Address) entry.Value.ParentNameIfAlsoSelected = parent.Value.Name;
                result.Types.Add(entry.Value);
            }
            result.TableAndSelectedMetadataStable = true;
            result.ReadBytes = budget.Bytes;
            return result;
        }
    }
}


// Source: tools/LiveEditor/CacheDiagnostics/CacheProbeMailbox.cs; SHA256 3E30D43AE702753C5A8A5519CD3AE9DBFFEED24B7E3F8BB1E98810AB96F2BEC0
namespace MotionVfx.CacheDiagnostics
{
    // This entry point never resolves a game object or performs a probe automatically.
    // It starts a cancellable file mailbox. Only an explicit, new, validated request can read.
    public static class MotionVfxCacheDiagnostics
    {
        private static CacheProbeMailbox mailbox;

        [REFrameworkNET.Attributes.PluginEntryPoint]
        public static void Main()
        {
            string processPath = null, currentDirectory = null, gameRoot = null;
            try
            {
                currentDirectory = Environment.CurrentDirectory;
                processPath = Environment.ProcessPath;
                gameRoot = FindStartupRoot(processPath, currentDirectory);
                if (gameRoot == null) throw new InvalidOperationException("Cannot locate a verified game directory containing MonsterHunterWilds.exe from ProcessPath or CurrentDirectory.");
                CacheProbeMailbox.WriteStartupStatus(gameRoot, "starting", processPath, currentDirectory, null);
                string expectedExecutable = Path.Combine(gameRoot, "MonsterHunterWilds.exe");
                // Establish identity through Windows itself if Environment.ProcessPath is
                // unavailable in the embedded host. These are process metadata queries only;
                // startup never reads a holder or native resource buffer.
                using (var identity = ReadOnlyProcessMemory.CreateForCurrentProcess(expectedExecutable))
                {
                    identity.EnsureIdentity();
                    mailbox = new CacheProbeMailbox(gameRoot, identity.ExecutablePath);
                }
                mailbox.Start();
            }
            catch (Exception error)
            {
                try
                {
                    currentDirectory ??= Environment.CurrentDirectory;
                    gameRoot ??= FindStartupRoot(processPath, currentDirectory);
                    if (gameRoot != null) CacheProbeMailbox.WriteStartupStatus(gameRoot, "startup_failed", processPath, currentDirectory, error);
                    else Console.Error.WriteLine("[MotionVfxCacheDiagnostics] Startup failed: " + error);
                }
                catch (Exception statusError) { Console.Error.WriteLine("[MotionVfxCacheDiagnostics] Startup/status failure: " + error + "; " + statusError); }
            }
        }

        private static string FindStartupRoot(string processPath, string currentDirectory)
        {
            if (!string.IsNullOrWhiteSpace(processPath) && string.Equals(Path.GetFileName(processPath), "MonsterHunterWilds.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(processPath))
                return Path.GetDirectoryName(Path.GetFullPath(processPath));
            if (!string.IsNullOrWhiteSpace(currentDirectory) && File.Exists(Path.Combine(currentDirectory, "MonsterHunterWilds.exe")))
                return Path.GetFullPath(currentDirectory);
            return null;
        }

        [REFrameworkNET.Attributes.PluginExitPoint]
        public static void OnUnload()
        {
            var previous = mailbox;
            mailbox = null;
            // Cancellation wakes the poll immediately; never wait indefinitely on a game thread.
            previous?.Stop();
        }
    }

    public sealed class CacheProbeMailbox
    {
        public const string Version = "0.1.8";
        private readonly string gameRootDirectory, directory, expectedExecutable, requestPath, responsePath, statusPath;
        private readonly string candidateDirectory;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly HashSet<string> handledIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> handledHashes = new HashSet<string>(StringComparer.Ordinal);
        private readonly DateTime startedAt = DateTime.UtcNow;
        private Thread thread;
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        internal CacheProbeMailbox(string gameRoot, string processExecutable)
        {
            gameRootDirectory = Path.GetFullPath(gameRoot);
            directory = Path.Combine(gameRootDirectory, "reframework", "data", "MotionVfxLive");
            expectedExecutable = Path.GetFullPath(processExecutable);
            requestPath = Path.Combine(directory, "cache-probe-request.json");
            responsePath = Path.Combine(directory, "cache-probe-response.json");
            statusPath = Path.Combine(directory, "cache-probe-plugin-status.json");
            candidateDirectory = Path.Combine(directory, "cache-diagnostics");
        }

        public void Start()
        {
            if (thread != null) throw new InvalidOperationException("Mailbox is already started.");
            Directory.CreateDirectory(directory);
            // Even a valid old request is inert after loading/reloading the plugin.
            if (File.Exists(requestPath))
            {
                try
                {
                    byte[] previous = ReadBoundedFile(requestPath, 32768);
                    handledHashes.Add(Convert.ToHexString(SHA256.HashData(previous)));
                    using var document = JsonDocument.Parse(previous);
                    if (document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        handledIds.Add(id.GetString());
                }
                catch { /* Existing incomplete requests are also rejected by startup mtime. */ }
            }
            WriteStatus(true, "waiting_for_explicit_request");
            thread = new Thread(Listen) { IsBackground = true, Name = "MotionVfx cache diagnostics mailbox" };
            thread.Start();
        }

        public void Stop()
        {
            cancellation.Cancel();
            bool stopped = thread == null || thread.Join(100);
            // A rare outstanding OS read remains on the background thread and exits at its
            // next cancellation check. No main-thread retry, abort, or unbounded join.
            try { WriteStatus(false, stopped ? "stopped" : "stopping"); } catch { }
        }

        private void WriteStatus(bool ready, string state)
        {
            WriteAtomic(statusPath, new {
                version = Version, ready, state, processId = Environment.ProcessId,
                executablePath = expectedExecutable, startedAtUtc = startedAt.ToString("O"),
                environmentProcessPath = Environment.ProcessPath, environmentCurrentDirectory = Environment.CurrentDirectory,
                observedAtUtc = DateTime.UtcNow.ToString("O"), automaticProbes = false,
                nativeProcessWrites = false, candidateDirectory
            });
        }

        internal static void WriteStartupStatus(string gameRoot, string state, string processPath, string currentDirectory, Exception error)
        {
            if (!File.Exists(Path.Combine(gameRoot, "MonsterHunterWilds.exe"))) throw new InvalidDataException("Startup diagnostics require an existing game executable in the target directory.");
            string directory = Path.Combine(gameRoot, "reframework", "data", "MotionVfxLive");
            Directory.CreateDirectory(directory);
            WriteAtomic(Path.Combine(directory, "cache-probe-plugin-status.json"), new {
                ready = false, version = Version, state, processId = Environment.ProcessId,
                observedAtUtc = DateTime.UtcNow.ToString("O"), environmentProcessPath = processPath,
                environmentCurrentDirectory = currentDirectory, diagnosticGameRoot = gameRoot,
                error = error?.GetBaseException().Message, exception = error?.ToString(), automaticProbes = false, nativeProcessWrites = false
            });
        }

        private void Listen()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    PollOnce();
                    cancellation.Token.WaitHandle.WaitOne(200);
                }
            }
            finally { try { WriteStatus(false, "stopped"); } catch { } }
        }

        private void PollOnce()
        {
            string id = null;
            try
            {
                if (!File.Exists(requestPath) || File.GetLastWriteTimeUtc(requestPath) <= startedAt) return;
                byte[] requestBytes = ReadBoundedFile(requestPath, 32768);
                string hash = Convert.ToHexString(SHA256.HashData(requestBytes));
                if (handledHashes.Contains(hash)) return;
                if (handledHashes.Count >= 256) throw new InvalidOperationException("Diagnostic request limit reached; reload the component before another request.");
                handledHashes.Add(hash);
                using var request = JsonDocument.Parse(requestBytes);
                id = RequiredString(request.RootElement, "id");
                if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,96}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Request id must contain 1-96 letters, digits, underscore or hyphen.");
                if (!handledIds.Add(id)) return;
                var response = ProcessRequest(request.RootElement, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                WriteAtomic(responsePath, response);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!cancellation.IsCancellationRequested)
                    try { WriteAtomic(responsePath, ErrorResponse(id, error)); } catch { }
            }
        }

        private static object ErrorResponse(string id, Exception error) => new {
            id, status = "rejected", version = Version, processId = Environment.ProcessId,
            error = error.GetBaseException().Message, observedAtUtc = DateTime.UtcNow.ToString("O"),
            authenticatedCacheFile = false, usableForEditing = false, unverified = true,
            nativeProcessWrites = false, candidateFiles = Array.Empty<object>()
        };

        internal object ProcessRequest(JsonElement request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string id = RequiredString(request, "id");
            if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,96}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid request id.");
            string action = RequiredString(request, "action");
            if (action != "probe" && action != "native_type_metadata") throw new InvalidDataException("Only explicit probe or native_type_metadata actions are supported.");
            string logical = NormalizeEfx(RequiredString(request, "logicalResourcePath"));
            bool direct = request.TryGetProperty("inspectDirectPointers", out var directValue) && directValue.GetBoolean();
            bool inspectHeader = request.TryGetProperty("inspectStringTableHeader", out var headerValue) && headerValue.GetBoolean();
            var before = ReadSource(request, logical);
            token.ThrowIfCancellationRequested();
            // All reads use -1, the current-process pseudo handle, with checked pages and RPM.
            // This is NOT an engine object API and it never dereferences or writes native memory.
            using var reader = ReadOnlyProcessMemory.CreateForCurrentProcess(expectedExecutable);
            if (DateTimeOffset.FromUnixTimeSeconds(before.LoadedAt).UtcDateTime < reader.CreationTimeUtc.AddSeconds(-1))
                throw new InvalidDataException("UI source predates the current game process.");
            reader.EnsureIdentity();
            if (action == "native_type_metadata")
                return ProcessNativeTypeMetadata(request, logical, before, reader, token);
            var result = reader.ProbeRetainedHolder(before.Holder, direct, token, inspectHeader);
            token.ThrowIfCancellationRequested();
            var after = ReadSource(request, logical);
            if (!before.SameIdentity(after)) throw new InvalidDataException("Retained source, UI lifetime, or scene changed while reading.");
            byte[] holderAgain = reader.ReadExact(after.Holder, 24);
            if (ReadOnlyProcessMemory.Hex(ReadOnlyProcessMemory.PointerAt(holderAgain, 16)) != result.NativeResourceAddress)
                throw new InvalidDataException("Retained holder native pointer changed after source revalidation.");
            reader.EnsureStringTableHeaderPointerStable(result);
            reader.EnsureIdentity();
            token.ThrowIfCancellationRequested();
            var files = new List<object>();
            foreach (var candidate in result.Candidates)
            {
                token.ThrowIfCancellationRequested();
                byte[] bytes = candidate.GetBytes();
                if (bytes == null || !candidate.StableDoubleRead || candidate.Framing == null || !candidate.Framing.Valid) continue;
                Directory.CreateDirectory(candidateDirectory);
                string path = Path.Combine(candidateDirectory, "cache-candidate-" + id + "-" + files.Count + ".unauthenticated.bin");
                // Never overwrite any earlier diagnostic with the same name, even after reload.
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) output.Write(bytes);
                files.Add(new { path, bytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                    address = candidate.Address, unverified = true, status = "UNAUTHENTICATED_DIAGNOSTIC_ONLY_NOT_FOR_EDITING" });
            }
            return new {
                id, status = "diagnostic_complete_not_authenticated", version = Version, processId = reader.ProcessId,
                processStartedAtUtc = reader.CreationTimeUtc.ToString("O"), observedAtUtc = DateTime.UtcNow.ToString("O"),
                logicalResourcePath = logical, before = before.Descriptor, after = after.Descriptor,
                authenticatedCacheFile = false, usableForEditing = false, unverified = true, nativeProcessWrites = false,
                directPointerDepth = direct ? 1 : 0, inspectStringTableHeader = inspectHeader, rawNativeBinarySaved = false, result, candidateFiles = files,
                holderLayoutBasis = "REFramework runtime_size()=0x10 and Resource::create_holder; not an EffectResource payload layout."
            };
        }

        private object ProcessNativeTypeMetadata(JsonElement request, string logical, Source before, ReadOnlyProcessMemory reader, CancellationToken token)
        {
            if (request.TryGetProperty("typeListAddress", out var arbitraryAddress))
                throw new InvalidDataException("TypeList addresses cannot be supplied by requests; only the current-process startup log is accepted.");
            token.ThrowIfCancellationRequested();
            string logPath = Path.Combine(gameRootDirectory, "re2_framework_log.txt");
            var evidence = ReadTypeListLogEvidence(logPath, reader.CreationTimeUtc);
            byte[] holderBefore = reader.ReadExact(before.Holder, 24);
            ulong nativeBefore = ReadOnlyProcessMemory.PointerAt(holderBefore, 16);
            if (!ReadOnlyProcessMemory.PlausiblePointer(nativeBefore)) throw new InvalidDataException("Retained holder native resource pointer is invalid.");
            var metadata = NativeTypeMetadataProbe.Read(reader, evidence.AddressValue, token);
            token.ThrowIfCancellationRequested();
            var after = ReadSource(request, logical);
            if (!before.SameIdentity(after)) throw new InvalidDataException("Retained source, UI lifetime, or scene changed during metadata observation.");
            if (ReadOnlyProcessMemory.PointerAt(reader.ReadExact(after.Holder, 24), 16) != nativeBefore)
                throw new InvalidDataException("Retained holder native resource pointer changed during metadata observation.");
            var afterEvidence = ReadTypeListLogEvidence(logPath, reader.CreationTimeUtc);
            if (afterEvidence.AddressValue != evidence.AddressValue || afterEvidence.ObservedUtc != evidence.ObservedUtc)
                throw new InvalidDataException("Current-process TypeList startup evidence changed while reading.");
            reader.EnsureIdentity();
            token.ThrowIfCancellationRequested();
            return new {
                id = RequiredString(request, "id"), status = "native_type_metadata_complete_not_authenticated", version = Version,
                processId = reader.ProcessId, processStartedAtUtc = reader.CreationTimeUtc.ToString("O"), observedAtUtc = DateTime.UtcNow.ToString("O"),
                logicalResourcePath = logical, before = before.Descriptor, after = after.Descriptor, typeListEvidence = evidence, metadata,
                authenticatedCacheFile = false, usableForEditing = false, unverified = true, nativeProcessWrites = false,
                candidateFiles = Array.Empty<object>(), rawNativeBinarySaved = false
            };
        }

        internal sealed class TypeListLogEvidence
        {
            public string LogPath { get; internal set; }
            public string Address { get; internal set; }
            public string LogTimestampLocal { get; internal set; }
            public string LogTimestampUtc { get; internal set; }
            public string LocalTimeZone { get; internal set; }
            public int PrefixBytesRead { get; internal set; }
            public int MaximumPrefixBytes { get; } = 2 * 1024 * 1024;
            public string PrefixSha256 { get; internal set; }
            public bool BelongsAfterCurrentProcessStart { get; } = true;
            public bool Unverified { get; } = true;
            internal ulong AddressValue;
            internal DateTime ObservedUtc;
        }

        internal static TypeListLogEvidence ReadTypeListLogEvidence(string logPath, DateTime processCreatedUtc)
        {
            const int maximum = 2 * 1024 * 1024;
            byte[] prefix;
            using (var input = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int length = (int)Math.Min(input.Length, maximum);
                if (length == 0) throw new InvalidDataException("Game startup log is empty.");
                prefix = new byte[length]; int total = 0;
                while (total < length)
                {
                    int read = input.Read(prefix, total, length - total);
                    if (read == 0) throw new InvalidDataException("Startup log changed during its bounded read.");
                    total += read;
                }
            }
            // REFramework log timestamps are LOCAL wall-clock time, not UTC. Ignore old-run
            // records and reject DST-ambiguous times instead of guessing which process owns them.
            var pattern = new Regex(@"^.*?\[(?<stamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\].*?\bFound TypeList: (?:0x)?(?<address>[0-9a-fA-F]{1,16})\b", RegexOptions.CultureInvariant);
            TypeListLogEvidence found = null;
            string text = Encoding.UTF8.GetString(prefix);
            int finalNewline = text.LastIndexOf('\n');
            if (finalNewline < 0) throw new InvalidDataException("The bounded startup-log prefix contains no complete line.");
            using var lines = new StringReader(text.Substring(0, finalNewline + 1));
            string line;
            while ((line = lines.ReadLine()) != null)
            {
                var match = pattern.Match(line);
                if (!match.Success) continue;
                DateTime local = DateTime.SpecifyKind(DateTime.ParseExact(match.Groups["stamp"].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
                if (TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local))
                    throw new InvalidDataException("TypeList log time is invalid or ambiguous in the local time zone.");
                DateTime utc = TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local);
                if (utc < processCreatedUtc || utc > DateTime.UtcNow.AddSeconds(2)) continue;
                ulong address = ulong.Parse(match.Groups["address"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                if (!ReadOnlyProcessMemory.PlausiblePointer(address)) throw new InvalidDataException("Startup log TypeList address is invalid.");
                if (found != null && found.AddressValue != address) throw new InvalidDataException("Startup log has conflicting TypeList addresses for the current process.");
                if (found != null) continue;
                found = new TypeListLogEvidence {
                    LogPath = Path.GetFullPath(logPath), Address = ReadOnlyProcessMemory.Hex(address), AddressValue = address,
                    LogTimestampLocal = match.Groups["stamp"].Value, LogTimestampUtc = utc.ToString("O"), ObservedUtc = utc,
                    LocalTimeZone = TimeZoneInfo.Local.Id, PrefixBytesRead = prefix.Length,
                    PrefixSha256 = Convert.ToHexString(SHA256.HashData(prefix))
                };
            }
            if (found == null) throw new InvalidDataException("No TypeList startup record after this process's creation was found within the log's first 2 MiB.");
            return found;
        }

        private sealed class Source
        {
            internal string SourceId, EventId, ActualPath, SharedTarget;
            internal long Epoch, LoadedAt;
            internal ulong Holder;
            internal JsonElement Descriptor;
            internal bool SameIdentity(Source other) => SourceId == other.SourceId && EventId == other.EventId &&
                ActualPath == other.ActualPath && SharedTarget == other.SharedTarget && Epoch == other.Epoch &&
                LoadedAt == other.LoadedAt && Holder == other.Holder;
        }

        private Source ReadSource(JsonElement request, string logical)
        {
            string path = Path.Combine(directory, "ui-status.json");
            DateTime modified = File.GetLastWriteTimeUtc(path);
            using var document = JsonDocument.Parse(ReadBoundedFile(path, 8 * 1024 * 1024));
            var ui = document.RootElement;
            double age = (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(RequiredLong(ui, "observedAt"))).TotalSeconds;
            if (age < -2 || age > 8 || (DateTime.UtcNow - modified).TotalSeconds > 8) throw new InvalidDataException("UI source is not fresh (maximum 8 seconds).");
            if (!IsTrue(ui, "loaded") || !ui.TryGetProperty("captureActor", out var actor) || !IsTrue(actor, "available"))
                throw new InvalidDataException("No usable capture record/source: local capture actor is unavailable.");
            if (!ui.TryGetProperty("captureResources", out var resources) || resources.ValueKind != JsonValueKind.Array || resources.GetArrayLength() == 0)
                throw new InvalidDataException("No usable capture record/source: capture list is empty or cleared.");
            JsonElement matched = default;
            int count = 0;
            foreach (var record in resources.EnumerateArray())
            {
                if (NormalizeEfx(RequiredString(record, "canonicalPath")) != logical) continue;
                matched = record; count++;
            }
            if (count != 1) throw new InvalidDataException("Expected one matching current capture record.");
            if (!IsTrue(matched, "localConfirmed") || !matched.TryGetProperty("latestSource", out var source) || source.ValueKind != JsonValueKind.Object ||
                !IsTrue(source, "localConfirmed") || RequiredString(source, "sourceKind") != "actual_effect_player_holder" || RequiredString(source, "availability") != "retained_observation")
                throw new InvalidDataException("No locally confirmed, retained actual EffectPlayer holder source.");
            var value = new Source {
                SourceId = RequiredString(source, "sourceId"), EventId = RequiredString(source, "eventId"),
                ActualPath = RequiredString(source, "actualPath"), SharedTarget = RequiredString(source, "sharedTargetPath"),
                Epoch = RequiredLong(source, "sceneEpoch"), LoadedAt = RequiredLong(ui, "loadedAt"),
                Holder = ParseAddress(RequiredString(source, "actualHolderIdentity")), Descriptor = source.Clone()
            };
            NormalizeEfx(value.ActualPath); // generated rev_N paths are intentionally permitted
            if (value.Epoch < 1 || value.Epoch != RequiredLong(actor, "sceneEpoch") || NormalizeEfx(value.SharedTarget) != logical ||
                value.SourceId != RequiredString(request, "sourceId") || value.Holder != ParseAddress(RequiredString(request, "holderIdentity")) ||
                value.Epoch != RequiredLong(request, "sceneEpoch") || value.LoadedAt != RequiredLong(request, "loadedAt"))
                throw new InvalidDataException("Request no longer matches the retained source, path, holder, scene, or UI lifetime.");
            if (request.TryGetProperty("eventId", out var expectedEvent) && expectedEvent.GetString() != value.EventId)
                throw new InvalidDataException("Request event identity differs from the actual retained event.");
            if (request.TryGetProperty("expectedActualPath", out var expectedPath) && NormalizeEfx(expectedPath.GetString()) != NormalizeEfx(value.ActualPath))
                throw new InvalidDataException("Request actual resource path differs from the retained observation.");
            return value;
        }

        private static bool IsTrue(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.True;

        private static string RequiredString(JsonElement value, string property)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var result) || result.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(result.GetString()))
                throw new InvalidDataException("Missing or invalid required text field: " + property);
            return result.GetString();
        }

        private static long RequiredLong(JsonElement value, string property)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var result) || !result.TryGetInt64(out long number))
                throw new InvalidDataException("Missing or invalid required integer field: " + property);
            return number;
        }

        private static ulong ParseAddress(string text)
        {
            ulong value;
            bool valid = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ulong.TryParse(text.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value) :
                ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
            if (!valid || !ReadOnlyProcessMemory.PlausiblePointer(value)) throw new InvalidDataException("Holder identity is not an exact aligned address.");
            return value;
        }

        internal static string NormalizeEfx(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Missing logical EFX path.");
            string path = value.Trim().Replace('\\', '/');
            if (path.StartsWith("@", StringComparison.Ordinal)) path = path.Substring(1).TrimStart('/');
            if (path.StartsWith("natives/stm/", StringComparison.OrdinalIgnoreCase)) path = path.Substring(12);
            path = Regex.Replace(path, @"(?i)\.efx\.\d+$", ".efx");
            if (!path.EndsWith(".efx", StringComparison.OrdinalIgnoreCase) || path.Any(c => c < 32 || ":*?\"<>|".IndexOf(c) >= 0) ||
                path.Split('/').Any(p => p == "" || p == "." || p == "..")) throw new InvalidDataException("EFX path must be complete and game relative.");
            return path.ToLowerInvariant();
        }

        private static byte[] ReadBoundedFile(string path, int maximum)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length < 1 || file.Length > maximum) throw new InvalidDataException("File is empty or exceeds the diagnostic size limit.");
            using var bytes = new MemoryStream();
            byte[] block = new byte[8192]; int count;
            while ((count = file.Read(block, 0, Math.Min(block.Length, maximum + 1 - (int)bytes.Length))) != 0)
            {
                bytes.Write(block, 0, count);
                if (bytes.Length > maximum) throw new InvalidDataException("File grew beyond its diagnostic bound.");
            }
            return bytes.ToArray();
        }

        private static void WriteAtomic(string path, object value)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
    }
}
