using System.Diagnostics;
using System.Runtime.InteropServices;
using ReeLib;
using ReeLib.DDS;
using ReeLib.Tex;
using Silk.NET.OpenGL;

namespace VfxStudio.Renderer.Textures;

public class GpuTexture : IDisposable
{
    private readonly GL _gl;
    public uint Handle { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsCompressed { get; private set; }

    [DllImport("GDeflateWrapper.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe bool gdeflate_get_uncompressed_size(byte* inData, ulong inSize, out ulong outSize);

    [DllImport("GDeflateWrapper.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe bool gdeflate_decompress(byte* outData, ulong outSize, byte* inData, ulong inSize, uint numWorkers);

    public GpuTexture(GL gl)
    {
        _gl = gl;
        Handle = _gl.GenTexture();
    }

    private static (int bw, int bh, int bpb)? GetBlockFormatInfo(DxgiFormat fmt) => fmt switch
    {
        DxgiFormat.BC1_UNORM or DxgiFormat.BC1_UNORM_SRGB or DxgiFormat.BC4_UNORM or DxgiFormat.BC4_SNORM => (4, 4, 8),
        DxgiFormat.BC2_UNORM or DxgiFormat.BC2_UNORM_SRGB or
        DxgiFormat.BC3_UNORM or DxgiFormat.BC3_UNORM_SRGB or
        DxgiFormat.BC5_UNORM or DxgiFormat.BC5_UNORM or
        DxgiFormat.BC6H_UF16 or DxgiFormat.BC6H_SF16 or
        DxgiFormat.BC7_UNORM or DxgiFormat.BC7_UNORM_SRGB => (4, 4, 16),
        DxgiFormat.R8G8B8A8_UNORM or DxgiFormat.R8G8B8A8_UNORM_SRGB or
        DxgiFormat.B8G8R8A8_UNORM or DxgiFormat.B8G8R8A8_UNORM_SRGB => (1, 1, 4),
        DxgiFormat.R8G8_UNORM => (1, 1, 2),
        DxgiFormat.R8_UNORM => (1, 1, 1),
        DxgiFormat.R16G16B16A16_FLOAT => (1, 1, 8),
        _ => null
    };

    private static unsafe byte[]? TryGDeflateDecompress(ReadOnlySpan<byte> compressed)
    {
        if (compressed.Length < 2) return null;
        try
        {
            fixed (byte* inPtr = compressed)
            {
                if (!gdeflate_get_uncompressed_size(inPtr, (ulong)compressed.Length, out ulong outSize) || outSize == 0 || outSize > 128 * 1024 * 1024)
                {
                    return null;
                }
                byte[] output = new byte[outSize];
                fixed (byte* outPtr = output)
                {
                    if (!gdeflate_decompress(outPtr, outSize, inPtr, (ulong)compressed.Length, 4))
                    {
                        return null;
                    }
                }
                return output;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GpuTexture] GDeflateWrapper error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Decodes a Monster Hunter Wilds .tex.241106027 file (with GDeflate decompression and Capcom scanline padding stripping)
    /// into an OpenGL 2D texture, porting mhws_efx_editor-0.3.0/blender_efx_re/tex_image.py::extract_mip0.
    /// </summary>
    public static GpuTexture? CreateFromTexBytes(GL gl, byte[] data, string filePath = "")
    {
        if (data == null || data.Length < 40) return null;
        uint magic = BitConverter.ToUInt32(data, 0);
        if (magic != TexFile.Magic) return null;

        int version = BitConverter.ToInt32(data, 4);
        if (filePath.EndsWith(".241106027", StringComparison.OrdinalIgnoreCase))
        {
            version = 241106027;
        }

        int width = BitConverter.ToUInt16(data, 8);
        int height = BitConverter.ToUInt16(data, 10);
        int depth = BitConverter.ToUInt16(data, 12);
        if (width <= 0 || height <= 0 || depth > 1) return null;

        int off = 14;
        int imageCount, mipCount;
        if (version > 11 && version != 190820018)
        {
            imageCount = data[off++];
            int mipHeaderSize = data[off++];
            mipCount = mipHeaderSize / 16;
        }
        else
        {
            mipCount = data[off++];
            imageCount = data[off++];
        }

        if (imageCount <= 0 || mipCount <= 0) return null;

        DxgiFormat format = (DxgiFormat)BitConverter.ToInt32(data, off);
        off += 4;
        off += 12; // swizzleControl(4), cubemapMarker(4), flags(4)
        if (version > 27 && version != 190820018)
        {
            off += 8;
        }

        var fmtInfo = GetBlockFormatInfo(format);
        if (fmtInfo == null) return null;
        var (bw, bh, bpb) = fmtInfo.Value;

        int mipTableStart = off;
        int n = mipCount * imageCount;
        int imageHeaderTableStart = mipTableStart + n * 16;
        int imageDataStart = imageHeaderTableStart + n * 8;

        if (mipTableStart + 16 > data.Length) return null;

        ulong mip0Offset = BitConverter.ToUInt64(data, mipTableStart);
        int scanlineLength = BitConverter.ToInt32(data, mipTableStart + 8);
        int uncompSize = BitConverter.ToInt32(data, mipTableStart + 12);

        byte[]? payload = null;

        if (imageDataStart <= data.Length && imageHeaderTableStart + 8 <= data.Length)
        {
            int imgSize = BitConverter.ToInt32(data, imageHeaderTableStart);
            int imgOffset = BitConverter.ToInt32(data, imageHeaderTableStart + 4);
            int start = imageDataStart + imgOffset;

            if (imgSize > 0 && start >= 0 && start + imgSize <= data.Length)
            {
                var rawSlice = new ReadOnlySpan<byte>(data, start, imgSize);
                if (rawSlice.Length >= 2 && rawSlice[0] == 0x04 && rawSlice[1] == 0xFB)
                {
                    payload = TryGDeflateDecompress(rawSlice);
                }
                else if (version != 241106027)
                {
                    payload = rawSlice.ToArray();
                }
            }
        }

        if (payload == null && (int)mip0Offset >= 0 && (int)mip0Offset + uncompSize <= data.Length && uncompSize > 0)
        {
            var rawSlice = new ReadOnlySpan<byte>(data, (int)mip0Offset, uncompSize);
            if (rawSlice.Length >= 2 && rawSlice[0] == 0x04 && rawSlice[1] == 0xFB)
            {
                payload = TryGDeflateDecompress(rawSlice);
            }
            else
            {
                payload = rawSlice.ToArray();
            }
        }

        if (payload == null || payload.Length == 0) return null;

        // Match payload against mip dimensions (supports both full/streaming mips and non-streaming base stubs)
        int matchedW = width;
        int matchedH = height;
        byte[]? compactBytes = null;

        for (int shift = 0; shift < 12; shift++)
        {
            int mw = Math.Max(1, width >> shift);
            int mh = Math.Max(1, height >> shift);
            int lineBytes = ((mw + bw - 1) / bw) * bpb;
            int rows = (mh + bh - 1) / bh;
            int compact = lineBytes * rows;

            if (payload.Length == compact)
            {
                matchedW = mw;
                matchedH = mh;
                compactBytes = payload;
                break;
            }

            if (scanlineLength >= lineBytes && payload.Length >= scanlineLength * rows &&
                (shift == 0 || payload.Length == scanlineLength * rows))
            {
                byte[] stripped = new byte[compact];
                for (int r = 0; r < rows; r++)
                {
                    System.Buffer.BlockCopy(payload, r * scanlineLength, stripped, r * lineBytes, lineBytes);
                }
                matchedW = mw;
                matchedH = mh;
                compactBytes = stripped;
                break;
            }
        }

        if (compactBytes == null) return null;

        var gpuTex = new GpuTexture(gl)
        {
            Width = matchedW,
            Height = matchedH
        };

        gl.BindTexture(TextureTarget.Texture2D, gpuTex.Handle);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

        var internalCompressed = MapToCompressedInternalFormat(format);
        if (internalCompressed.HasValue)
        {
            gpuTex.IsCompressed = true;
            unsafe
            {
                fixed (byte* ptr = compactBytes)
                {
                    gl.CompressedTexImage2D(
                        TextureTarget.Texture2D,
                        0,
                        internalCompressed.Value,
                        (uint)matchedW,
                        (uint)matchedH,
                        0,
                        (uint)compactBytes.Length,
                        ptr
                    );
                }
            }

            if (format is DxgiFormat.BC4_UNORM or DxgiFormat.BC4_SNORM)
            {
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleR, (int)GLEnum.One);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleG, (int)GLEnum.One);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleB, (int)GLEnum.One);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleA, (int)GLEnum.Red);
            }
        }
        else
        {
            gpuTex.IsCompressed = false;
            var (pixelFormat, pixelType) = MapToPixelFormat(format);
            unsafe
            {
                fixed (byte* ptr = compactBytes)
                {
                    gl.TexImage2D(
                        TextureTarget.Texture2D,
                        0,
                        InternalFormat.Rgba8,
                        (uint)matchedW,
                        (uint)matchedH,
                        0,
                        pixelFormat,
                        pixelType,
                        ptr
                    );
                }
            }
        }

        gl.BindTexture(TextureTarget.Texture2D, 0);
        return gpuTex;
    }

    public static GpuTexture CreateFromTexFile(GL gl, TexFile tex)
    {
        var gpuTex = new GpuTexture(gl)
        {
            Width = tex.Header.width,
            Height = tex.Header.height
        };

        gl.BindTexture(TextureTarget.Texture2D, gpuTex.Handle);

        // Configure wrapping and filtering
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

        var format = tex.Header.format;
        var internalCompressed = MapToCompressedInternalFormat(format);

        if (internalCompressed.HasValue && tex.Mips.Count > 0)
        {
            gpuTex.IsCompressed = true;
            int uploadedMips = 0;
            for (int mip = 0; mip < tex.Header.mipCount && mip < tex.Mips.Count; mip++)
            {
                var mipData = tex.GetMipMapData(mip);
                if (mipData.data.Length == 0) break;

                unsafe
                {
                    fixed (byte* ptr = mipData.data)
                    {
                        gl.CompressedTexImage2D(
                            TextureTarget.Texture2D,
                            mip,
                            internalCompressed.Value,
                            mipData.width,
                            mipData.height,
                            0,
                            (uint)mipData.data.Length,
                            ptr
                        );
                    }
                }
                uploadedMips++;
            }

            if (uploadedMips <= 1)
            {
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            }
            else
            {
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, uploadedMips - 1);
            }

            // Single-channel BC4 textures store mask intensity in Red channel; swizzle to (1,1,1,R) for clean tinting + alpha
            if (format is DxgiFormat.BC4_UNORM or DxgiFormat.BC4_SNORM)
            {
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleR, (int)GLEnum.One);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleG, (int)GLEnum.One);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleB, (int)GLEnum.One);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleA, (int)GLEnum.Red);
            }
        }
        else if (tex.Mips.Count > 0)
        {
            // Uncompressed fallback
            gpuTex.IsCompressed = false;
            var mipData = tex.GetMipMapData(0);
            var (pixelFormat, pixelType) = MapToPixelFormat(format);

            unsafe
            {
                fixed (byte* ptr = mipData.data)
                {
                    gl.TexImage2D(
                        TextureTarget.Texture2D,
                        0,
                        InternalFormat.Rgba8,
                        mipData.width,
                        mipData.height,
                        0,
                        pixelFormat,
                        pixelType,
                        ptr
                    );
                }
            }
            gl.GenerateMipmap(TextureTarget.Texture2D);
        }

        gl.BindTexture(TextureTarget.Texture2D, 0);
        return gpuTex;
    }

    public static GpuTexture CreateSolidColor(GL gl, byte r, byte g, byte b, byte a)
    {
        var gpuTex = new GpuTexture(gl)
        {
            Width = 1,
            Height = 1,
            IsCompressed = false
        };

        gl.BindTexture(TextureTarget.Texture2D, gpuTex.Handle);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

        byte[] pixel = [r, g, b, a];
        unsafe
        {
            fixed (byte* ptr = pixel)
            {
                gl.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    InternalFormat.Rgba8,
                    1,
                    1,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    ptr
                );
            }
        }

        gl.BindTexture(TextureTarget.Texture2D, 0);
        return gpuTex;
    }

    private static InternalFormat? MapToCompressedInternalFormat(DxgiFormat format)
    {
        return format switch
        {
            // S3TC DXT1 (BC1)
            DxgiFormat.BC1_UNORM => (InternalFormat)0x83F1,      // GL_COMPRESSED_RGBA_S3TC_DXT1_EXT
            DxgiFormat.BC1_UNORM_SRGB => (InternalFormat)0x8C4C, // GL_COMPRESSED_SRGB_ALPHA_S3TC_DXT1_EXT
            // S3TC DXT3 (BC2)
            DxgiFormat.BC2_UNORM => (InternalFormat)0x83F2,      // GL_COMPRESSED_RGBA_S3TC_DXT3_EXT
            DxgiFormat.BC2_UNORM_SRGB => (InternalFormat)0x8C4E, // GL_COMPRESSED_SRGB_ALPHA_S3TC_DXT3_EXT
            // S3TC DXT5 (BC3)
            DxgiFormat.BC3_UNORM => (InternalFormat)0x83F3,      // GL_COMPRESSED_RGBA_S3TC_DXT5_EXT
            DxgiFormat.BC3_UNORM_SRGB => (InternalFormat)0x8C4F, // GL_COMPRESSED_SRGB_ALPHA_S3TC_DXT5_EXT
            // RGTC (BC4 / BC5)
            DxgiFormat.BC4_UNORM => (InternalFormat)0x8DBB,      // GL_COMPRESSED_RED_RGTC1
            DxgiFormat.BC4_SNORM => (InternalFormat)0x8DBC,      // GL_COMPRESSED_SIGNED_RED_RGTC1
            DxgiFormat.BC5_UNORM => (InternalFormat)0x8DBD,      // GL_COMPRESSED_RG_RGTC2
            DxgiFormat.BC5_SNORM => (InternalFormat)0x8DBE,      // GL_COMPRESSED_SIGNED_RG_RGTC2
            // BPTC (BC7)
            DxgiFormat.BC7_UNORM => (InternalFormat)0x8E8C,      // GL_COMPRESSED_RGBA_BPTC_UNORM
            DxgiFormat.BC7_UNORM_SRGB => (InternalFormat)0x8E8D, // GL_COMPRESSED_SRGB_ALPHA_BPTC_UNORM
            _ => null
        };
    }

    private static (PixelFormat format, PixelType type) MapToPixelFormat(DxgiFormat format)
    {
        return format switch
        {
            DxgiFormat.B8G8R8A8_UNORM or DxgiFormat.B8G8R8A8_UNORM_SRGB => (PixelFormat.Bgra, PixelType.UnsignedByte),
            _ => (PixelFormat.Rgba, PixelType.UnsignedByte)
        };
    }

    public void Bind(TextureUnit unit = TextureUnit.Texture0)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.Texture2D, Handle);
    }

    public void Unbind(TextureUnit unit = TextureUnit.Texture0)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            _gl.DeleteTexture(Handle);
            Handle = 0;
        }
    }
}
