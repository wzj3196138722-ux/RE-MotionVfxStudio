using System.Buffers.Binary;
using System.Buffers;
using System.Numerics;

namespace VfxStudio.Re4Render;

public readonly record struct ParticleRecord(int Entry, int Slot, uint Id, uint Flags, Vector3 Position, float Age, byte LifeAlpha, ReadOnlyMemory<byte> Raw);
/// <param name="First">Position of this entry's first live particle in <see cref="PreviewFrame.Particles"/>.</param>
/// <param name="Count">Number of live particles of this entry; they follow <paramref name="First"/> contiguously.</param>
/// <param name="Type">The entry's Type attribute id: 23 Billboard3D, 63 Polygon, 21 Billboard2D.</param>
public sealed record PreviewEntryState(int Index, uint Flags16c, uint Flags174, byte[] TypeBlock, int First = 0, int Count = 0, int Type = 23)
{
    /// <summary>Ribbon point pool (engine records of <see cref="FrameDecoder.PointStride"/> bytes,
    /// slot * <see cref="StripPointsPerParticle"/>), empty for other types.</summary>
    public ReadOnlyMemory<byte> StripPoints { get; init; }
    public int StripPointsPerParticle { get; init; }
    /// <summary>Per-slot ribbon state records (<see cref="FrameDecoder.RibbonStateStride"/> bytes; +0x18 = valid points).</summary>
    public ReadOnlyMemory<byte> StripStates { get; init; }
    /// <summary>Valid points of a particle's ribbon; 0 when the entry has no ribbon data.</summary>
    public int ValidPoints(int slot)
    {
        int at = slot * FrameDecoder.RibbonStateStride + 0x18;
        if (StripPointsPerParticle == 0 || at + 4 > StripStates.Length) return 0;
        return (int)Math.Min(FrameDecoder.U32(StripStates.Span, at), (uint)StripPointsPerParticle);
    }
    public ReadOnlySpan<byte> PointsOf(int slot) =>
        StripPoints.Span.Slice(slot * StripPointsPerParticle * FrameDecoder.PointStride, StripPointsPerParticle * FrameDecoder.PointStride);
    /// <summary>Where Polygon3DConstant starts in the type block: +0x54 for Billboard3D (builder 3d18580,
    /// uploader 3e0ebe0), +0x58 for Polygon. Billboard2D does not hold this layout (-1).</summary>
    /// <summary>Polygon3DConstant in the type block: located by the file's ColorRate (block + offset + 0x10c)
    /// and the blend / TextureFilter words next to it.</summary>
    public int ConstantOffset => Type switch { 23 => 0x54, 63 => 0x58, 31 or 32 or 34 => 0x50, 70 => 0x48, _ => -1 };
    public int Blend => ConstantOffset < 0 ? -1 : (int)((FrameDecoder.U32(TypeBlock,ConstantOffset+0x44) >> 11) & 15);
    public float ColorRate => ConstantOffset < 0 ? float.NaN : FrameDecoder.F32(TypeBlock,ConstantOffset+0x10c);
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
    /// <summary>Frame protocol v3: entry = 16 u32, 16 f32 world matrix, 8 u32 ribbon fields.</summary>
    public const int HeaderSize = 256, SlotHeaderSize = 64, EntrySize = 160, Stride = 0x140, PointStride = 0x54, RibbonStateStride = 0x68, ProtocolVersion = 3;
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
            int first=alive;
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
                // +0x28 is emitter-local; world = local * P, the particle's reference matrix at +0x100
                // (RUNTIME 2.1; it carries Transform3D and ParentOptions). A record without a valid P
                // (w != 1) falls back to the entry's world matrix.
                pos = F32(raw, 0x13c) == 1f
                    ? Vector3.Transform(pos, new Matrix4x4(F32(raw,0x100), F32(raw,0x104), F32(raw,0x108), F32(raw,0x10c),
                        F32(raw,0x110), F32(raw,0x114), F32(raw,0x118), F32(raw,0x11c), F32(raw,0x120), F32(raw,0x124), F32(raw,0x128), F32(raw,0x12c),
                        F32(raw,0x130), F32(raw,0x134), F32(raw,0x138), F32(raw,0x13c)))
                    : Vector3.Transform(pos, m);
                particles[alive++]=new(index, slot, U32(raw, 0x18), flags, pos, F32(raw, 0x58), (byte)((flags >> 2) & 255), bytes.AsMemory(offset + slot * stride, stride));
            }
            // Ribbon fields (protocol v3): points offset/bytes/stride/per-particle, states offset/bytes/stride.
            int stripOffset = checked((int)U32(b,e+128)), stripBytes = checked((int)U32(b,e+132)), perParticle = checked((int)U32(b,e+140));
            int stateOffset = checked((int)U32(b,e+144)), stateBytes = checked((int)U32(b,e+148));
            ReadOnlyMemory<byte> strip = default, stripStates = default;
            if (stripBytes != 0)
            {
                if (U32(b,e+136) != PointStride || U32(b,e+152) != RibbonStateStride || perParticle < 2 ||
                    (long)n * perParticle * PointStride != stripBytes || (long)n * RibbonStateStride != stateBytes ||
                    stripOffset < blockOffset + blockLength || (long)stripOffset + stripBytes > b.Length ||
                    stateOffset < stripOffset + stripBytes || (long)stateOffset + stateBytes > b.Length)
                    throw new InvalidDataException("Invalid ribbon point range");
                strip = bytes.AsMemory(stripOffset, stripBytes);
                stripStates = bytes.AsMemory(stateOffset, stateBytes);
            }
            states.Add(new(index,U32(b,e+28),U32(b,e+36),b.Slice(blockOffset,blockLength).ToArray(),first,alive-first,checked((int)U32(b,e+4)))
                { StripPoints = strip, StripStates = stripStates, StripPointsPerParticle = stripBytes == 0 ? 0 : perParticle });
        }
        return new(checked((int)U32(b, 0)), BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(b.Slice(8,8))),
            F32(b,16), new ArraySegment<ParticleRecord>(particles,0,alive), arrays, sim, restore, total, EntryStates:states)
            { Storage=new FrameStorage(pooledPacket?bytes:null,particles) };
        }
        catch { ArrayPool<ParticleRecord>.Shared.Return(particles,clearArray:true); throw; }
    }
}
