using System.Buffers.Binary;
using System.Buffers;
using System.Numerics;

namespace VfxStudio.Re4Render;

public readonly record struct ParticleRecord(int Entry, int Slot, uint Id, uint Flags, Vector3 Position, float Age, byte LifeAlpha, ReadOnlyMemory<byte> Raw);
public sealed record PreviewEntryState(int Index, uint Flags16c, uint Flags174, byte[] TypeBlock)
{
    // Type builder 3d18580: Polygon3DConstant starts at block+0x54; uploader 3e0ebe0.
    public int Blend => (int)((FrameDecoder.U32(TypeBlock,0x98) >> 11) & 15);
    public float ColorRate => FrameDecoder.F32(TypeBlock,0x160);
}
public sealed record PreviewFrame(int Tick, double Time, float Dt, IReadOnlyList<ParticleRecord> Particles, ReadOnlyMemory<byte>[] Arrays,
    double SimulationMs, double RestoreMs, double TotalMs, double DecodeMs = 0, IReadOnlyList<PreviewEntryState>? EntryStates = null) : IDisposable
{
    internal FrameStorage? Storage { get; init; }
    public void Dispose() => Storage?.Dispose();
}
internal sealed class FrameStorage(byte[]? packet, ParticleRecord[] particles) : IDisposable
{
    private int disposed;
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0) return;
        if(packet!=null) ArrayPool<byte>.Shared.Return(packet);
        ArrayPool<ParticleRecord>.Shared.Return(particles,clearArray:true);
    }
}

public static class FrameDecoder
{
    public const int HeaderSize = 256, SlotHeaderSize = 64, EntrySize = 128, Stride = 0x140;
    public static uint U32(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(offset, 4));
    public static float F32(ReadOnlySpan<byte> b, int offset) => BitConverter.Int32BitsToSingle((int)U32(b, offset));
    public static PreviewFrame Decode(byte[] bytes, double sim, double restore, double total, bool pooledPacket=false, int byteCount=-1)
    {
        var b = bytes.AsSpan(0,byteCount<0?bytes.Length:byteCount);
        if (b.Length < SlotHeaderSize) throw new InvalidDataException("Truncated frame");
        int count = checked((int)U32(b, 4));
        if (count is < 1 or > 64 || SlotHeaderSize + count * EntrySize > b.Length) throw new InvalidDataException("Invalid entry table");
        int capacity=0;
        for(int i=0;i<count;i++) capacity=checked(capacity+(int)U32(b,SlotHeaderSize+i*EntrySize+8));
        if(capacity>640000) throw new InvalidDataException("Particle capacity exceeds protocol limit");
        var particles=ArrayPool<ParticleRecord>.Shared.Rent(Math.Max(1,capacity));
        int alive=0;
        var arrays = new ReadOnlyMemory<byte>[count];
        var states = new List<PreviewEntryState>();
        try
        {
        for (int i = 0; i < count; i++)
        {
            int e = SlotHeaderSize + i * EntrySize;
            int index = checked((int)U32(b, e));
            int n = checked((int)U32(b, e + 8));
            int stride = checked((int)U32(b, e + 12));
            int offset = checked((int)U32(b, e + 40)), length = checked((int)U32(b, e + 44));
            if (stride != Stride || n < 0 || (long)n * stride != length || offset < SlotHeaderSize + count * EntrySize || (long)offset + length > b.Length)
                throw new InvalidDataException("Invalid particle range");
            arrays[i] = bytes.AsMemory(offset,length);
            int blockOffset = checked((int)U32(b,e+56)), blockLength = checked((int)U32(b,e+60));
            if (blockLength != 0x2f8 || blockOffset < offset + length || (long)blockOffset + blockLength > b.Length)
                throw new InvalidDataException("Invalid Billboard3D block range");
            states.Add(new(index,U32(b,e+28),U32(b,e+36),b.Slice(blockOffset,blockLength).ToArray()));
            var m = new Matrix4x4(F32(b,e+64), F32(b,e+68), F32(b,e+72), F32(b,e+76),
                F32(b,e+80), F32(b,e+84), F32(b,e+88), F32(b,e+92),
                F32(b,e+96), F32(b,e+100), F32(b,e+104), F32(b,e+108),
                F32(b,e+112), F32(b,e+116), F32(b,e+120), F32(b,e+124));
            for (int slot = 0; slot < n; slot++)
            {
                var raw = b.Slice(offset + slot * stride, stride);
                uint flags = U32(raw, 0);
                if ((flags & 1) == 0) continue;
                // 3dfd040 adds +0x34 only when [p+4] & 0x1000 and its caller passes r8b = 1 (ribbon
                // sampling). [p+0] bit 12 is part of the UV sequence number and must not move the particle.
                var pos = new Vector3(F32(raw, 0x28), F32(raw, 0x2c), F32(raw, 0x30));
                pos = Vector3.Transform(pos, m); // P0: static emitter; ParentOptions reference matrices are P5.
                particles[alive++]=new(index, slot, U32(raw, 0x18), flags, pos, F32(raw, 0x58), (byte)((flags >> 2) & 255), bytes.AsMemory(offset + slot * stride, stride));
            }
        }
        return new(checked((int)U32(b, 0)), BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(b.Slice(8,8))),
            F32(b,16), new ArraySegment<ParticleRecord>(particles,0,alive), arrays, sim, restore, total, EntryStates:states)
            { Storage=new FrameStorage(pooledPacket?bytes:null,particles) };
        }
        catch { ArrayPool<ParticleRecord>.Shared.Return(particles,clearArray:true); throw; }
    }
}
