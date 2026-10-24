using Silk.NET.OpenGL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using VfxStudio.Renderer.Shaders;
using Shader = VfxStudio.Renderer.Shaders.Shader;

namespace VfxStudio.Renderer.Framebuffer;

public class HdrBloomPipeline : IDisposable
{
    private readonly GL _gl;
    private int _width;
    private int _height;

    // HDR Scene Framebuffer (Color + Brightness MRT)
    private uint _hdrFbo;
    private uint _sceneColorTex;
    private uint _brightnessTex;
    private uint _depthStencilRbo;

    // Ping-Pong Bloom Blur Framebuffers
    private readonly uint[] _pingPongFbo = new uint[2];
    private readonly uint[] _pingPongColorTex = new uint[2];

    // Final Post-Processed Framebuffer
    private uint _finalFbo;
    public uint FinalColorTexture { get; private set; }

    // Shaders
    private readonly Shader _blurShader;
    private readonly Shader _compositeShader;

    // Fullscreen Quad VAO & VBO
    private uint _quadVao;
    private uint _quadVbo;

    public int Width => _width;
    public int Height => _height;
    public uint SceneColorTexture => _sceneColorTex;
    public uint BrightnessTexture => _brightnessTex;

    public HdrBloomPipeline(GL gl, int width, int height)
    {
        _gl = gl;
        _width = width;
        _height = height;

        _blurShader = new Shader(_gl, ShaderSources.FullscreenQuadVertexShader, ShaderSources.GaussianBlurFragmentShader);
        _compositeShader = new Shader(_gl, ShaderSources.FullscreenQuadVertexShader, ShaderSources.CompositeToneMapFragmentShader);

        SetupFullscreenQuad();
        CreateBuffers(width, height);
    }

    private unsafe void SetupFullscreenQuad()
    {
        // 2D Quad (X, Y, U, V)
        float[] quadVertices = [
            // Pos        // TexCoords
            -1.0f,  1.0f,  0.0f, 1.0f,
            -1.0f, -1.0f,  0.0f, 0.0f,
             1.0f, -1.0f,  1.0f, 0.0f,

            -1.0f,  1.0f,  0.0f, 1.0f,
             1.0f, -1.0f,  1.0f, 0.0f,
             1.0f,  1.0f,  1.0f, 1.0f
        ];

        _quadVao = _gl.GenVertexArray();
        _quadVbo = _gl.GenBuffer();

        _gl.BindVertexArray(_quadVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);

        unsafe
        {
            fixed (float* v = quadVertices)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quadVertices.Length * sizeof(float)), v, BufferUsageARB.StaticDraw);
            }
        }

        // Pos (location = 0)
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);

        // UV (location = 1)
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));

        _gl.BindVertexArray(0);
    }

    private void CreateBuffers(int width, int height)
    {
        _width = width;
        _height = height;

        // 1. Create HDR MRT FBO
        _hdrFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _hdrFbo);

        // Scene Color Texture (RGBA16F)
        _sceneColorTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _sceneColorTex);
        unsafe
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.Float, null);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _sceneColorTex, 0);

        // Brightness Texture (RGBA16F)
        _brightnessTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _brightnessTex);
        unsafe
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.Float, null);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, _brightnessTex, 0);

        // Depth Stencil Renderbuffer
        _depthStencilRbo = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthStencilRbo);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, (uint)width, (uint)height);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _depthStencilRbo);

        // Direct draw to ColorAttachment0 and ColorAttachment1
        DrawBufferMode[] drawBuffers = [DrawBufferMode.ColorAttachment0, DrawBufferMode.ColorAttachment1];
        _gl.DrawBuffers(drawBuffers);

        if (_gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
        {
            throw new InvalidOperationException("HDR Framebuffer is not complete!");
        }

        // 2. Create Ping-Pong FBOs for Bloom Blur
        for (int i = 0; i < 2; i++)
        {
            _pingPongFbo[i] = _gl.GenFramebuffer();
            _pingPongColorTex[i] = _gl.GenTexture();

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _pingPongFbo[i]);
            _gl.BindTexture(TextureTarget.Texture2D, _pingPongColorTex[i]);
            unsafe
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.Float, null);
            }
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _pingPongColorTex[i], 0);

            if (_gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            {
                throw new InvalidOperationException($"Ping-Pong Framebuffer [{i}] is not complete!");
            }
        }

        // 3. Create Final Color FBO
        _finalFbo = _gl.GenFramebuffer();
        FinalColorTexture = _gl.GenTexture();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _finalFbo);
        _gl.BindTexture(TextureTarget.Texture2D, FinalColorTexture);
        unsafe
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, FinalColorTexture, 0);

        if (_gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
        {
            throw new InvalidOperationException("Final Framebuffer is not complete!");
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void BeginScene(float r = 0.08f, float g = 0.09f, float b = 0.12f, float a = 1.0f)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _hdrFbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);

        DrawBufferMode[] drawBuffers = [DrawBufferMode.ColorAttachment0, DrawBufferMode.ColorAttachment1];
        _gl.DrawBuffers(drawBuffers);

        _gl.ClearColor(r, g, b, a);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
    }

    public void EndSceneAndProcessBloom(int blurPasses = 6, float bloomIntensity = 0.8f, float exposure = 1.0f)
    {
        _gl.Disable(EnableCap.DepthTest);

        // --- 1. Two-pass Gaussian Blur on Brightness Texture ---
        bool horizontal = true;
        bool firstIteration = true;
        _blurShader.Use();

        _gl.BindVertexArray(_quadVao);

        for (int i = 0; i < blurPasses; i++)
        {
            int index = horizontal ? 0 : 1;
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _pingPongFbo[index]);
            _blurShader.SetInt("u_Horizontal", horizontal ? 1 : 0);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, firstIteration ? _brightnessTex : _pingPongColorTex[horizontal ? 1 : 0]);
            _blurShader.SetInt("u_Image", 0);

            _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

            horizontal = !horizontal;
            if (firstIteration) firstIteration = false;
        }

        // --- 2. Final Composite Tone Mapping Pass ---
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _finalFbo);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        _compositeShader.Use();
        _compositeShader.SetFloat("u_BloomIntensity", bloomIntensity);
        _compositeShader.SetFloat("u_Exposure", exposure);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _sceneColorTex);
        _compositeShader.SetInt("u_Scene", 0);

        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _pingPongColorTex[horizontal ? 1 : 0]);
        _compositeShader.SetInt("u_BloomBlur", 1);

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

        _gl.BindVertexArray(0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void SaveScreenshot(string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _finalFbo);
        byte[] pixels = new byte[_width * _height * 4];

        unsafe
        {
            fixed (byte* p = pixels)
            {
                _gl.ReadPixels(0, 0, (uint)_width, (uint)_height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            }
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        // OpenGL texture coordinates have origin (0, 0) at bottom-left, whereas images are top-left.
        // We flip rows when saving with ImageSharp.
        using var image = Image.LoadPixelData<Rgba32>(pixels, _width, _height);
        image.Mutate(x => x.Flip(FlipMode.Vertical));
        image.SaveAsPng(outputPath);
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (_width == width && _height == height)) return;

        DestroyBuffers();
        CreateBuffers(width, height);
    }

    private void DestroyBuffers()
    {
        if (_hdrFbo != 0) { _gl.DeleteFramebuffer(_hdrFbo); _hdrFbo = 0; }
        if (_sceneColorTex != 0) { _gl.DeleteTexture(_sceneColorTex); _sceneColorTex = 0; }
        if (_brightnessTex != 0) { _gl.DeleteTexture(_brightnessTex); _brightnessTex = 0; }
        if (_depthStencilRbo != 0) { _gl.DeleteRenderbuffer(_depthStencilRbo); _depthStencilRbo = 0; }

        for (int i = 0; i < 2; i++)
        {
            if (_pingPongFbo[i] != 0) { _gl.DeleteFramebuffer(_pingPongFbo[i]); _pingPongFbo[i] = 0; }
            if (_pingPongColorTex[i] != 0) { _gl.DeleteTexture(_pingPongColorTex[i]); _pingPongColorTex[i] = 0; }
        }

        if (_finalFbo != 0) { _gl.DeleteFramebuffer(_finalFbo); _finalFbo = 0; }
        if (FinalColorTexture != 0) { _gl.DeleteTexture(FinalColorTexture); FinalColorTexture = 0; }
    }

    public void Dispose()
    {
        DestroyBuffers();

        if (_quadVbo != 0) { _gl.DeleteBuffer(_quadVbo); _quadVbo = 0; }
        if (_quadVao != 0) { _gl.DeleteVertexArray(_quadVao); _quadVao = 0; }

        _blurShader.Dispose();
        _compositeShader.Dispose();
    }
}
