using System.Numerics;
using Hexa.NET.ImGui;
using Silk.NET.OpenGL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace VfxStudio.App.UI;

internal sealed class UiIconTextures : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<string, uint> _handles = new(StringComparer.OrdinalIgnoreCase);

    public unsafe UiIconTextures(GL gl, string assetsDirectory)
    {
        _gl = gl;
        string iconDirectory = Path.Combine(assetsDirectory, "UI", "Icons");
        if (!Directory.Exists(iconDirectory)) return;
        foreach (string path in Directory.GetFiles(iconDirectory, "*.png"))
        {
            using var image = Image.Load<Rgba32>(path);
            byte[] pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);
            uint handle = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, handle);
            fixed (byte* data = pixels)
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                    (uint)image.Width, (uint)image.Height, 0, PixelFormat.Rgba,
                    PixelType.UnsignedByte, data);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            _handles[Path.GetFileNameWithoutExtension(path)] = handle;
        }
        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    public bool Draw(ImDrawListPtr drawList, string name, Vector2 topLeft, float size)
    {
        if (!_handles.TryGetValue(name, out uint handle)) return false;
        ImTextureRef texture = default;
        texture.TexID = new ImTextureID((nint)handle);
        drawList.AddImage(texture, topLeft, topLeft + new Vector2(size));
        return true;
    }

    public void Dispose()
    {
        foreach (uint handle in _handles.Values) _gl.DeleteTexture(handle);
        _handles.Clear();
    }
}
