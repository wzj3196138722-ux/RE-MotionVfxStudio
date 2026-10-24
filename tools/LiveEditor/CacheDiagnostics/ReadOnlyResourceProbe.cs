using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

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
