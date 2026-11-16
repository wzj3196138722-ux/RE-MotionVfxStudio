using Silk.NET.OpenGL;
using VfxStudio.Renderer.Shaders;
using Shader = VfxStudio.Renderer.Shaders.Shader;

namespace VfxStudio.Re4Render;

/// <summary>The preview's HDR target: RGBA16F colour and brightness, bloom, ACES composite (same
/// curve and passes as VfxStudio.Renderer's HdrBloomPipeline). Unlike that shared pipeline, depth is
/// a texture, and <see cref="CopyOpaqueDepth"/> keeps a copy that particle shaders sample for soft
/// particles while depth testing continues against the attachment.</summary>
public sealed unsafe class PreviewHdrPipeline : IDisposable
{
    private readonly GL gl;
    private readonly Shader blur, composite;
    private uint quadVao, quadVbo, hdrFbo, sceneColor, bright, depth, depthCopyFbo, finalFbo;
    private readonly uint[] pingFbo = new uint[2], pingTex = new uint[2];
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint FinalColorTexture { get; private set; }
    /// <summary>Opaque-scene depth as of the last <see cref="CopyOpaqueDepth"/>.</summary>
    public uint OpaqueDepthTexture { get; private set; }

    public PreviewHdrPipeline(GL gl, int width, int height)
    {
        this.gl = gl;
        blur = new Shader(gl, ShaderSources.FullscreenQuadVertexShader, ShaderSources.GaussianBlurFragmentShader);
        composite = new Shader(gl, ShaderSources.FullscreenQuadVertexShader, ShaderSources.CompositeToneMapFragmentShader);
        float[] quad = [-1, 1, 0, 1, -1, -1, 0, 0, 1, -1, 1, 0, -1, 1, 0, 1, 1, -1, 1, 0, 1, 1, 1, 1];
        quadVao = gl.GenVertexArray(); quadVbo = gl.GenBuffer();
        gl.BindVertexArray(quadVao); gl.BindBuffer(BufferTargetARB.ArrayBuffer, quadVbo);
        fixed (float* v = quad) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * 4), v, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 16, (void*)0);
        gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 16, (void*)8);
        gl.BindVertexArray(0);
        Create(width, height);
    }

    private uint ColorTexture(int w, int h, InternalFormat format, PixelFormat pf, PixelType type)
    {
        uint t = gl.GenTexture(); gl.BindTexture(TextureTarget.Texture2D, t);
        gl.TexImage2D(TextureTarget.Texture2D, 0, format, (uint)w, (uint)h, 0, pf, type, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return t;
    }

    private uint DepthTexture(int w, int h)
    {
        uint t = gl.GenTexture(); gl.BindTexture(TextureTarget.Texture2D, t);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Depth24Stencil8, (uint)w, (uint)h, 0, PixelFormat.DepthStencil, PixelType.UnsignedInt248, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return t;
    }

    private static void Check(GL gl, string name)
    {
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete) throw new InvalidOperationException(name + " framebuffer is incomplete");
    }

    private void Create(int w, int h)
    {
        Width = w; Height = h;
        hdrFbo = gl.GenFramebuffer(); gl.BindFramebuffer(FramebufferTarget.Framebuffer, hdrFbo);
        sceneColor = ColorTexture(w, h, InternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.Float);
        bright = ColorTexture(w, h, InternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.Float);
        depth = DepthTexture(w, h);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, sceneColor, 0);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, bright, 0);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, TextureTarget.Texture2D, depth, 0);
        DrawBufferMode[] both = [DrawBufferMode.ColorAttachment0, DrawBufferMode.ColorAttachment1];
        gl.DrawBuffers(both);
        Check(gl, "HDR");
        depthCopyFbo = gl.GenFramebuffer(); gl.BindFramebuffer(FramebufferTarget.Framebuffer, depthCopyFbo);
        OpaqueDepthTexture = DepthTexture(w, h);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, TextureTarget.Texture2D, OpaqueDepthTexture, 0);
        gl.DrawBuffer(DrawBufferMode.None); gl.ReadBuffer(ReadBufferMode.None);
        Check(gl, "Depth copy");
        for (int i = 0; i < 2; i++)
        {
            pingFbo[i] = gl.GenFramebuffer(); gl.BindFramebuffer(FramebufferTarget.Framebuffer, pingFbo[i]);
            pingTex[i] = ColorTexture(w, h, InternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.Float);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, pingTex[i], 0);
            Check(gl, "Bloom");
        }
        finalFbo = gl.GenFramebuffer(); gl.BindFramebuffer(FramebufferTarget.Framebuffer, finalFbo);
        FinalColorTexture = ColorTexture(w, h, InternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, FinalColorTexture, 0);
        Check(gl, "Final");
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void Destroy()
    {
        if (sceneCopy != 0) { gl.DeleteFramebuffer(sceneCopyFbo); gl.DeleteTexture(sceneCopy); sceneCopy = 0; sceneCopyFbo = 0; }
        gl.DeleteFramebuffer(hdrFbo); gl.DeleteFramebuffer(depthCopyFbo); gl.DeleteFramebuffer(finalFbo);
        gl.DeleteTexture(sceneColor); gl.DeleteTexture(bright); gl.DeleteTexture(depth); gl.DeleteTexture(OpaqueDepthTexture); gl.DeleteTexture(FinalColorTexture);
        for (int i = 0; i < 2; i++) { gl.DeleteFramebuffer(pingFbo[i]); gl.DeleteTexture(pingTex[i]); }
    }

    public void Resize(int w, int h)
    {
        if (w <= 0 || h <= 0 || (w == Width && h == Height)) return;
        Destroy(); Create(w, h);
    }

    public void BeginScene(float r, float g, float b, float a)
    {
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, hdrFbo);
        gl.Viewport(0, 0, (uint)Width, (uint)Height);
        DrawBufferMode[] both = [DrawBufferMode.ColorAttachment0, DrawBufferMode.ColorAttachment1];
        gl.DrawBuffers(both);
        gl.ClearColor(r, g, b, a);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        gl.Enable(EnableCap.DepthTest); gl.DepthFunc(DepthFunction.Lequal);
    }

    private uint sceneCopyFbo, sceneCopy;

    /// <summary>Copies the scene colour drawn so far (for Distortion) and returns the copy; leaves the HDR target bound.</summary>
    public uint CaptureSceneColor()
    {
        if (sceneCopy == 0)
        {
            sceneCopyFbo = gl.GenFramebuffer(); gl.BindFramebuffer(FramebufferTarget.Framebuffer, sceneCopyFbo);
            sceneCopy = ColorTexture(Width, Height, InternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.Float);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, sceneCopy, 0);
            Check(gl, "Scene copy");
        }
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, hdrFbo);
        gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, sceneCopyFbo);
        gl.BlitFramebuffer(0, 0, Width, Height, 0, 0, Width, Height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, hdrFbo);
        DrawBufferMode[] both = [DrawBufferMode.ColorAttachment0, DrawBufferMode.ColorAttachment1];
        gl.DrawBuffers(both);
        return sceneCopy;
    }

    /// <summary>Call after the opaque pass and before particles; leaves the HDR target bound.</summary>
    public void CopyOpaqueDepth()
    {
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, hdrFbo);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, depthCopyFbo);
        gl.BlitFramebuffer(0, 0, Width, Height, 0, 0, Width, Height, ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, hdrFbo);
    }

    public void EndSceneAndProcessBloom(int blurPasses = 6, float bloomIntensity = .8f, float exposure = 1)
    {
        gl.Disable(EnableCap.DepthTest);
        bool horizontal = true, first = true;
        blur.Use(); gl.BindVertexArray(quadVao);
        for (int i = 0; i < blurPasses; i++)
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, pingFbo[horizontal ? 0 : 1]);
            blur.SetInt("u_Horizontal", horizontal ? 1 : 0);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, first ? bright : pingTex[horizontal ? 1 : 0]);
            blur.SetInt("u_Image", 0);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            horizontal = !horizontal; first = false;
        }
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, finalFbo);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        composite.Use();
        composite.SetFloat("u_BloomIntensity", bloomIntensity); composite.SetFloat("u_Exposure", exposure);
        gl.ActiveTexture(TextureUnit.Texture0); gl.BindTexture(TextureTarget.Texture2D, sceneColor); composite.SetInt("u_Scene", 0);
        gl.ActiveTexture(TextureUnit.Texture1); gl.BindTexture(TextureTarget.Texture2D, pingTex[horizontal ? 1 : 0]); composite.SetInt("u_BloomBlur", 1);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindVertexArray(0); gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Dispose()
    {
        Destroy();
        gl.DeleteBuffer(quadVbo); gl.DeleteVertexArray(quadVao);
        blur.Dispose(); composite.Dispose();
    }
}
