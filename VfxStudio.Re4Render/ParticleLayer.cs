using Silk.NET.OpenGL;
using Shader=VfxStudio.Renderer.Shaders.Shader;

namespace VfxStudio.Re4Render;

/// <summary>Premultiplied particle layer. Color is filtered on upsample; opaque depth is copied
/// with nearest filtering so particles still test against the scene at reduced resolution.</summary>
public sealed unsafe class ParticleLayer : IDisposable
{
    private readonly GL gl;
    private readonly Shader composite;
    private uint fbo, color, bright, depth, vao;
    private int width,height;
    private int scene;
    public int Divisor { get; private set; }=1;
    public ParticleLayer(GL gl)
    {
        this.gl=gl;vao=gl.GenVertexArray();
        composite=new Shader(gl,"""
            #version 460 core
            out vec2 uv;
            void main() {
                vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);
                uv=p;gl_Position=vec4(p*2-1,0,1);
            }
            ""","""
            #version 460 core
            in vec2 uv;
            uniform sampler2D sceneColor, sceneBright;
            layout(location=0) out vec4 color;
            layout(location=1) out vec4 bright;
            void main() { color=texture(sceneColor,uv);bright=texture(sceneBright,uv); }
            """);
    }
    private uint Texture(int w,int h)
    {
        uint t=gl.GenTexture();gl.BindTexture(TextureTarget.Texture2D,t);
        gl.TexImage2D(TextureTarget.Texture2D,0,InternalFormat.Rgba16f,(uint)w,(uint)h,0,PixelFormat.Rgba,PixelType.Float,null);
        gl.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)GLEnum.ClampToEdge);
        return t;
    }
    public void Begin(int fullWidth,int fullHeight,int divisor)
    {
        Divisor=divisor;
        scene=gl.GetInteger(GetPName.DrawFramebufferBinding);
        int w=Math.Max(1,(fullWidth+divisor-1)/divisor),h=Math.Max(1,(fullHeight+divisor-1)/divisor);
        if(w!=width || h!=height)
        {
            ReleaseTargets();width=w;height=h;
            fbo=gl.GenFramebuffer();gl.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
            color=Texture(w,h);bright=Texture(w,h);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,color,0);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment1,TextureTarget.Texture2D,bright,0);
            depth=gl.GenRenderbuffer();gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer,depth);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer,InternalFormat.Depth24Stencil8,(uint)w,(uint)h);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,FramebufferAttachment.DepthStencilAttachment,RenderbufferTarget.Renderbuffer,depth);
            DrawBufferMode[] buffers=[DrawBufferMode.ColorAttachment0,DrawBufferMode.ColorAttachment1];gl.DrawBuffers(buffers);
            if(gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer)!=GLEnum.FramebufferComplete) throw new InvalidOperationException("Particle layer framebuffer is incomplete");
        }
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer,(uint)scene);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer,fbo);
        gl.BlitFramebuffer(0,0,fullWidth,fullHeight,0,0,width,height,ClearBufferMask.DepthBufferBit,BlitFramebufferFilter.Nearest);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
        gl.Viewport(0,0,(uint)width,(uint)height);
        gl.ClearColor(0,0,0,0);gl.Clear(ClearBufferMask.ColorBufferBit);
    }
    public void End(int fullWidth,int fullHeight,int blend)
    {
        gl.BindFramebuffer(FramebufferTarget.Framebuffer,(uint)scene);
        gl.Viewport(0,0,(uint)fullWidth,(uint)fullHeight);
        gl.Disable(EnableCap.DepthTest);gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.One,blend==0?BlendingFactor.OneMinusSrcAlpha:BlendingFactor.One);
        composite.Use();composite.SetUniform("sceneColor",0);composite.SetUniform("sceneBright",1);
        gl.ActiveTexture(TextureUnit.Texture0);gl.BindTexture(TextureTarget.Texture2D,color);
        gl.ActiveTexture(TextureUnit.Texture1);gl.BindTexture(TextureTarget.Texture2D,bright);
        gl.BindVertexArray(vao);gl.DrawArrays(PrimitiveType.Triangles,0,3);gl.BindVertexArray(0);
        gl.Disable(EnableCap.Blend);gl.Enable(EnableCap.DepthTest);
    }
    private void ReleaseTargets()
    {
        gl.DeleteTexture(color);gl.DeleteTexture(bright);gl.DeleteRenderbuffer(depth);gl.DeleteFramebuffer(fbo);
        color=bright=depth=fbo=0;
    }
    public void Dispose() {ReleaseTargets();gl.DeleteVertexArray(vao);composite.Dispose();}
}

