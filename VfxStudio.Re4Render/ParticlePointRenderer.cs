using System.Numerics;
using Silk.NET.OpenGL;
using Shader = VfxStudio.Renderer.Shaders.Shader;

namespace VfxStudio.Re4Render;

public sealed class ParticlePointRenderer : IDisposable
{
    private readonly GL gl;
    private readonly Shader shader;
    private readonly uint vao, vbo;
    public ParticlePointRenderer(GL gl)
    {
        this.gl = gl;
        shader = new Shader(gl, """
            #version 460 core
            layout(location=0) in vec3 position;
            uniform mat4 vp;
            void main() { gl_Position = vp * vec4(position,1); gl_PointSize = 7; }
            """, """
            #version 460 core
            out vec4 color;
            void main() { if (length(gl_PointCoord - vec2(.5)) > .5) discard; color=vec4(.3,.85,1,1); }
            """);
        vao = gl.GenVertexArray(); vbo = gl.GenBuffer();
        unsafe
        {
            gl.BindVertexArray(vao); gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, (void*)0);
            gl.BindVertexArray(0);
        }
    }
    public unsafe void Render(PreviewFrame frame, Matrix4x4 vp)
    {
        var points = frame.Particles.Select(p => p.Position).Where(p => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)).ToArray();
        gl.Enable(EnableCap.ProgramPointSize);
        shader.Use(); shader.SetUniform("vp", vp);
        gl.BindVertexArray(vao); gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (Vector3* ptr = points)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(points.Length * 12), ptr, BufferUsageARB.StreamDraw);
        gl.DrawArrays(PrimitiveType.Points, 0, (uint)points.Length);
        gl.BindVertexArray(0);
    }
    public void Dispose() { gl.DeleteBuffer(vbo); gl.DeleteVertexArray(vao); shader.Dispose(); }
}

