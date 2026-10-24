using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

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
