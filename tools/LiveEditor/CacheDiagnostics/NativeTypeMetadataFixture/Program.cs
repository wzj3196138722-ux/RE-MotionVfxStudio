using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MotionVfx.CacheDiagnostics;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static T Reject<T>(Action action, string message) where T : Exception
    {
        checks++;
        try { action(); } catch (T error) { return error; }
        throw new Exception(message);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly List<IntPtr> allocations = new List<IntPtr>();
        internal ulong Resource, Effect, EffectData, Table, Header, EffectFields;
        internal ulong Allocate(int count)
        {
            IntPtr value = Marshal.AllocHGlobal(Math.Max(count, 256)); allocations.Add(value);
            Marshal.Copy(new byte[Math.Max(count, 256)], 0, value, Math.Max(count, 256));
            return unchecked((ulong)value.ToInt64());
        }
        internal void Write(ulong address, int offset, ulong value) { Marshal.WriteInt64(new IntPtr(checked((long)address)), offset, unchecked((long)value)); }
        internal void Int(ulong address, int offset, int value) { Marshal.WriteInt32(new IntPtr(checked((long)address)), offset, value); }
        internal ulong Type(string name, uint size, ulong parent = 0, ulong fields = 0)
        {
            ulong nameAddress = Name(name);
            ulong address = Allocate(0x68); Write(address, 0x20, nameAddress); Int(address, 0x30, (int)size);
            Write(address, 0x40, parent); Write(address, 0x58, fields); Int(address, 0x2C, 12345); return address;
        }
        internal ulong Name(string value)
        {
            byte[] ascii = Encoding.ASCII.GetBytes(value + "\0");
            ulong address = Allocate(Math.Max(ascii.Length, 256)); Marshal.Copy(ascii, 0, new IntPtr((long)address), ascii.Length); return address;
        }
        internal List<ulong> Methods(ulong fields, int count, string prefix)
        {
            var descriptors = new List<ulong>();
            ulong table = Allocate(Math.Max(count * 8, 8)); Write(fields, 0x10, table); Int(fields, 0x18, count); Int(fields, 0x1C, count);
            for (int index = 0; index < count; index++)
            {
                ulong top = Allocate(8), holder = Allocate(0x20), descriptor = Allocate(0x38);
                Write(table, index * 8, top); Write(top, 0, holder); Write(holder, 0, descriptor);
                Write(descriptor, 0, Name(prefix + index)); Write(descriptor, 8, 0x66666660);
                Int(descriptor, 0x14, index % 17); Write(descriptor, 0x18, 0x88888881);
                // Conflicting return-type locations must never be dereferenced.
                Write(descriptor, 0x28, 0x99999990); Write(descriptor, 0x30, 0xAAAAAAAA);
                descriptors.Add(descriptor);
            }
            return descriptors;
        }
        internal ulong Properties(ulong fields, int count)
        {
            ulong header = Allocate(0x18), array = Allocate(Math.Max(count * 8, 8));
            Write(fields, 0x20, header); Write(header, 8, array); Int(header, 0x10, count); Int(header, 0x14, count);
            for (int index = 0; index < count; index++)
            {
                ulong descriptor = Allocate(0x48); Write(array, index * 8, descriptor);
                Write(descriptor, 0, Name("FixtureProperty" + index)); Write(descriptor, 0x10, 0xBBBBBBB1);
                Write(descriptor, 0x20, index == 0 ? 0UL : Name("via.FixtureType"));
            }
            return header;
        }
        internal ulong Property(ulong fields, string name, string typeName, ulong function)
        {
            ulong header = Properties(fields, 1);
            ulong array = unchecked((ulong)Marshal.ReadInt64(new IntPtr((long)header), 8));
            ulong descriptor = unchecked((ulong)Marshal.ReadInt64(new IntPtr((long)array)));
            Write(descriptor, 0, Name(name)); Write(descriptor, 0x20, Name(typeName)); Write(descriptor, 0x10, function);
            return descriptor;
        }
        internal Fixture()
        {
            Resource = Type("via.Resource", 0x58);
            EffectFields = Allocate(0x34);
            // The intentionally invalid next address must never be followed.
            Write(EffectFields, 8, 0x55555550); Methods(EffectFields, 3, "FixtureMethod"); Properties(EffectFields, 2);
            Int(EffectFields, 0x1C, 8);
            Effect = Type("via.effect.EffectResource", 0x158, Resource, EffectFields);
            // Arbitrary synthetic size: the probe must report metadata, not infer it
            // from EffectResource.Size - Resource.Size or a guessed instance layout.
            EffectData = Type("via.effect.detail.EffectData", 0x108);
            Table = Allocate(5 * 8); Write(Table, 0, Type("via.effect.EffectResourceHolder", 24));
            Write(Table, 8, Resource); Write(Table, 16, Effect); Write(Table, 24, Effect); Write(Table, 32, EffectData);
            Header = Allocate(16); Write(Header, 0, Table); Int(Header, 8, 4); Int(Header, 12, 5);
        }
        public void Dispose() { foreach (var address in allocations) Marshal.FreeHGlobal(address); }
    }

    public static int Main()
    {
        using var memory = ReadOnlyProcessMemory.CreateForCurrentProcess(Environment.ProcessPath);
        using (var fixture = new Fixture())
        {
            var result = NativeTypeMetadataProbe.Read(memory, fixture.Header);
            Check(result.TableAndSelectedMetadataStable, "Stable table was rejected.");
            Check(result.Types.Count == 3 && result.MissingExactNames.Count == 0, "Three exact types were not found.");
            Check(result.ScannedSlots == 5 && result.AllocatedSlots == 5, "Allocated slots were not scanned.");
            Check(result.Types[0].Name == "via.effect.EffectResource" && result.Types[0].Size == 0x158, "Effect metadata differs.");
            Check(result.Types[0].ParentNameIfAlsoSelected == "via.Resource", "Selected parent was not associated.");
            Check(result.Types[0].FirstFieldList.MethodCount == 3 && result.Types[0].FirstFieldList.MethodCapacity == 8, "Field-list header differs.");
            Check(result.Types[0].FirstFieldList.NextAddress == ReadOnlyProcessMemory.Hex(0x55555550), "Next address was not retained as data.");
            Check(result.Types[0].FirstFieldList.DeserializerPageKind == "absent", "Absent deserializer was invented.");
            Check(result.Types[1].FirstFieldList == null, "Null field list was traversed.");
            Check(result.Types[2].Name == "via.effect.detail.EffectData" && result.Types[2].Size == 0x108, "EffectData metadata was not read independently.");
            Check(result.ReadBytes > 0 && result.ReadBytes < 16384, "Unexpected unbounded reads.");
            Check(result.Types[0].FirstFieldList.Methods.Count == 3 && result.Types[0].FirstFieldList.Methods[2].Name == "FixtureMethod2", "Three-level method pointers were not resolved.");
            Check(result.Types[0].FirstFieldList.Methods[2].ParameterCount == 2 && result.Types[0].FirstFieldList.Methods[2].RawDescriptorSlot18 == ReadOnlyProcessMemory.Hex(0x88888881), "Raw method descriptor prefix differs.");
            Check(result.Types[0].FirstFieldList.Methods[2].FunctionAddress == null && result.Types[0].FirstFieldList.Methods[2].FunctionPageKind == "unproven_descriptor_slot_not_a_callable_address", "Unproven method slot was promoted to a function address.");
            Check(result.Types[0].FirstFieldList.Properties.Count == 2 && result.Types[0].FirstFieldList.Properties[1].TypeName == "via.FixtureType", "Property descriptor was not resolved.");
            Check(result.Types[0].FirstFieldList.Properties[0].TypeName == null, "Absent native property type name was invented.");
            fixture.Write(fixture.Table, 32, fixture.Type("via.effect.EffectResource", 0x170));
            Reject<InvalidDataException>(() => NativeTypeMetadataProbe.Read(memory, fixture.Header), "Duplicate exact type accepted.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Write(fixture.Table, 16, 0x10000); fixture.Write(fixture.Table, 24, 0);
            var result = NativeTypeMetadataProbe.Read(memory, fixture.Header);
            Check(result.Types.Count == 2 && result.Types[0].Name == "via.Resource" && result.Types[1].Name == "via.effect.detail.EffectData", "Unreadable type was accepted.");
            Check(result.MissingExactNames.Count == 1 && result.MissingExactNames[0] == "via.effect.EffectResource", "Holder name was mistaken for resource type.");
            Check(result.UnreadableOrInvalidTypes == 1, "Unreadable type was not counted.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Write(fixture.Table, 16, fixture.Type("via.effect.EffectResource" + new string('x', 128), 0x160)); fixture.Write(fixture.Table, 24, 0);
            var result = NativeTypeMetadataProbe.Read(memory, fixture.Header);
            Check(result.Types.Count == 2 && result.UnreadableOrInvalidTypes == 1, "Unterminated bounded name was accepted.");
            fixture.Int(fixture.Header, 12, NativeTypeMetadataProbe.MaximumTypeSlots + 1);
            var tooLarge = Reject<InvalidDataException>(() => NativeTypeMetadataProbe.Read(memory, fixture.Header), "Oversized native type table accepted.");
            Check(tooLarge.Message.Contains("size=4, allocated=163841, maximumSlots=163840", StringComparison.Ordinal), "Rejected counts were omitted.");
            Check(tooLarge.Message.Contains("data=" + ReadOnlyProcessMemory.Hex(fixture.Table), StringComparison.Ordinal), "Rejected data pointer was omitted.");
            Check(tooLarge.Message.Contains("header16Hex=", StringComparison.Ordinal) && tooLarge.Message.Contains("04 00 00 00 01 80 02 00", StringComparison.Ordinal), "Original 16-byte header was omitted.");
            fixture.Int(fixture.Header, 12, 1); fixture.Int(fixture.Header, 8, 2);
            var inverted = Reject<InvalidDataException>(() => NativeTypeMetadataProbe.Read(memory, fixture.Header), "Size above capacity accepted.");
            Check(inverted.Message.Contains("size=2, allocated=1, maximumSlots=163840", StringComparison.Ordinal), "Inverted size diagnostics differ.");
            fixture.Int(fixture.Header, 12, 5); fixture.Int(fixture.Header, 8, 4);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Reject<OperationCanceledException>(() => NativeTypeMetadataProbe.Read(memory, fixture.Header, cancelled.Token), "Cancellation ignored.");
        }
        using (var fixture = new Fixture())
        {
            ulong ordinaryData = fixture.Allocate(NativeTypeMetadataProbe.PropertyGetterPrefixBytes);
            ulong descriptor = fixture.Property(fixture.EffectFields, "EffectData", "via.effect.detail.EffectData", ordinaryData);
            var result = NativeTypeMetadataProbe.Read(memory, fixture.Header);
            var property = result.Types[0].FirstFieldList.Properties[0];
            Check(property.GetterPrefixHex == null && property.GetterPrefixStatus == "not_read_not_a_bounded_readable_executable_image", "Non-executable private data was read as a getter code prefix.");
            Check(property.GetterPrefixUnverified && result.TableAndSelectedMetadataStable, "Rejected getter was represented as verified or invalidated ordinary metadata.");
            fixture.Write(descriptor, 0, fixture.Name("EffectDataPtr"));
            property = NativeTypeMetadataProbe.Read(memory, fixture.Header).Types[0].FirstFieldList.Properties[0];
            Check(property.GetterPrefixHex == null && property.GetterPrefixStatus == "not_read_not_a_bounded_readable_executable_image", "EffectDataPtr did not enforce the image-code boundary.");
            fixture.Write(descriptor, 0, fixture.Name("OtherProperty"));
            property = NativeTypeMetadataProbe.Read(memory, fixture.Header).Types[0].FirstFieldList.Properties[0];
            Check(property.GetterPrefixStatus == "not_requested_for_this_property" && property.GetterPrefixHex == null, "An unrelated property's code was requested.");
            fixture.Write(descriptor, 0, fixture.Name("EffectData")); fixture.Write(descriptor, 0x20, fixture.Name("via.OtherData"));
            property = NativeTypeMetadataProbe.Read(memory, fixture.Header).Types[0].FirstFieldList.Properties[0];
            Check(property.GetterPrefixStatus == "not_requested_for_this_property" && property.GetterPrefixHex == null, "A property with a different type requested code bytes.");
            fixture.Write(fixture.Table, 32, fixture.Type("via.effect.detail.EffectDataHolder", 0x108));
            result = NativeTypeMetadataProbe.Read(memory, fixture.Header);
            Check(result.MissingExactNames.Count == 1 && result.MissingExactNames[0] == "via.effect.detail.EffectData", "EffectDataHolder was mistaken for EffectData.");
        }
        using (var fixture = new Fixture())
        {
            // Counts and native type sizes match the observed 0.1.6 metadata; names
            // and callable addresses are deliberately synthetic and never invoked.
            var methods = fixture.Methods(fixture.EffectFields, 6, "EffectFixture");
            ulong baseFields = fixture.Allocate(0x34); fixture.Write(fixture.Resource, 0x58, baseFields);
            fixture.Methods(baseFields, 44, "ResourceFixture"); fixture.Properties(baseFields, 10);
            var result = NativeTypeMetadataProbe.Read(memory, fixture.Header);
            Check(result.Types[0].Size == 344 && result.Types[1].Size == 88, "Observed native sizes differ.");
            Check(result.Types[0].FirstFieldList.Methods.Count == 6 && result.Types[1].FirstFieldList.Methods.Count == 44, "Observed method counts were not captured.");
            Check(result.Types[1].FirstFieldList.Methods[43].Name == "ResourceFixture43", "Last base method was missed.");
            Check(result.Types[1].FirstFieldList.Properties.Count == 10 && result.TableAndSelectedMetadataStable, "Direct properties were not fully captured and checked.");
            fixture.Int(methods[0], 0x14, 17);
            Reject<InvalidDataException>(() => NativeTypeMetadataProbe.Read(memory, fixture.Header), "Excessive method parameter count accepted.");
            fixture.Int(methods[0], 0x14, 0); fixture.Int(fixture.EffectFields, 0x18, 129); fixture.Int(fixture.EffectFields, 0x1C, 129);
            Reject<InvalidDataException>(() => NativeTypeMetadataProbe.Read(memory, fixture.Header), "Excessive direct method count accepted.");
            fixture.Int(fixture.EffectFields, 0x18, 6); fixture.Int(fixture.EffectFields, 0x1C, 6);
            ulong variables = fixture.Properties(fixture.EffectFields, 1); fixture.Int(variables, 0x10, 129); fixture.Int(variables, 0x14, 129);
            Reject<InvalidDataException>(() => NativeTypeMetadataProbe.Read(memory, fixture.Header), "Excessive property count accepted.");
            fixture.Int(variables, 0x10, 1); fixture.Int(variables, 0x14, 1);
            fixture.Write(methods[0], 0, fixture.Name(new string('x', 128)));
            Reject<InvalidDataException>(() => NativeTypeMetadataProbe.Read(memory, fixture.Header), "Unbounded method name accepted.");
        }
        using (var fixture = new Fixture())
        {
            const int liveObservedSlots = 147453;
            fixture.Table = fixture.Allocate(liveObservedSlots * 8);
            fixture.Write(fixture.Table, 0, fixture.Resource);
            fixture.Write(fixture.Table, 8, fixture.EffectData);
            // The selected effect type is beyond the first 1 MiB read. All intervening
            // slots are null, so this tests chunk boundaries without synthetic busywork.
            fixture.Write(fixture.Table, (liveObservedSlots - 1) * 8, fixture.Effect);
            fixture.Write(fixture.Header, 0, fixture.Table);
            fixture.Int(fixture.Header, 8, 120685); fixture.Int(fixture.Header, 12, liveObservedSlots);
            var result = NativeTypeMetadataProbe.Read(memory, fixture.Header);
            Check(result.ScannedSlots == liveObservedSlots && result.AllocatedSlots == liveObservedSlots, "Large table was not fully scanned.");
            Check(result.Types.Count == 3 && result.Types[0].Name == "via.effect.EffectResource", "Type beyond the first MiB was missed.");
            Check(result.TableAndSelectedMetadataStable, "Large table failed stable full-table reread.");
            Check(result.ReadBytes >= 2L * liveObservedSlots * 8 && result.ReadBytes < NativeTypeMetadataProbe.MaximumTotalReadBytes, "Full large table was not read twice within budget.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Int(fixture.Header, 8, 0); fixture.Int(fixture.Header, 12, 0); fixture.Write(fixture.Header, 0, 0);
            var result = NativeTypeMetadataProbe.Read(memory, fixture.Header);
            Check(result.Types.Count == 0 && result.MissingExactNames.Count == 3 && result.ScannedSlots == 0, "Empty table was misrepresented.");
        }
        Console.WriteLine("PASS NativeTypeMetadataFixture: " + checks + " checks; synthetic self-process allocations only; no engine calls.");
        return 0;
    }
}
