using System.Numerics;
using Silk.NET.OpenGL;
using VfxStudio.Renderer.Shaders;
using Shader = VfxStudio.Renderer.Shaders.Shader;

namespace VfxStudio.App.UI;

internal sealed class ViewportGridRenderer : IDisposable
{
    private const string VertexSource = """
        #version 460 core
        layout(location = 0) in vec3 a_Position;
        uniform mat4 u_View;
        uniform mat4 u_Projection;
        out vec3 v_World;
        void main() {
            v_World = a_Position;
            gl_Position = u_Projection * u_View * vec4(a_Position, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 460 core
        layout(location = 0) out vec4 FragColor;
        layout(location = 1) out vec4 BrightColor;
        in vec3 v_World;
        void main() {
            float fade = clamp(1.0 - length(v_World.xz) / 8.0, 0.0, 1.0);
            FragColor = vec4(0.18, 0.25, 0.40, 0.10 * fade);
            BrightColor = vec4(0.0);
        }
        """;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _vertexCount;

    public unsafe ViewportGridRenderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        var vertices = new List<float>();
        for (int step = -20; step <= 20; step++)
        {
            float coordinate = step * 0.5f;
            vertices.AddRange([coordinate, -0.002f, -10f, coordinate, -0.002f, 10f]);
            vertices.AddRange([-10f, -0.002f, coordinate, 10f, -0.002f, coordinate]);
        }
        _vertexCount = (uint)(vertices.Count / 3);
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        float[] data = vertices.ToArray();
        fixed (float* ptr = data)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        gl.BindVertexArray(0);
    }

    public void Render(Matrix4x4 view, Matrix4x4 projection)
    {
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _shader.Use();
        _shader.SetUniform("u_View", view);
        _shader.SetUniform("u_Projection", projection);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Lines, 0, _vertexCount);
        _gl.BindVertexArray(0);
        _gl.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }
}
