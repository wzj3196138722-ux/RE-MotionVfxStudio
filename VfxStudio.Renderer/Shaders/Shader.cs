using System.Numerics;
using Silk.NET.OpenGL;

namespace VfxStudio.Renderer.Shaders;

public class Shader : IDisposable
{
    private readonly GL _gl;
    public uint Handle { get; private set; }
    private readonly Dictionary<string, int> _uniformLocations = new();

    public Shader(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;
        uint vertex = CompileShader(ShaderType.VertexShader, vertexSource);
        uint fragment = CompileShader(ShaderType.FragmentShader, fragmentSource);

        Handle = _gl.CreateProgram();
        _gl.AttachShader(Handle, vertex);
        _gl.AttachShader(Handle, fragment);
        _gl.LinkProgram(Handle);

        _gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out int status);
        if (status == 0)
        {
            string info = _gl.GetProgramInfoLog(Handle);
            throw new InvalidOperationException($"Program linking failed: {info}");
        }

        _gl.DetachShader(Handle, vertex);
        _gl.DetachShader(Handle, fragment);
        _gl.DeleteShader(vertex);
        _gl.DeleteShader(fragment);
    }

    private uint CompileShader(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);

        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
        if (status == 0)
        {
            string info = _gl.GetShaderInfoLog(shader);
            _gl.DeleteShader(shader);
            throw new InvalidOperationException($"{type} compilation failed: {info}\nSource:\n{source}");
        }

        return shader;
    }

    public void Use()
    {
        _gl.UseProgram(Handle);
    }

    public int GetUniformLocation(string name)
    {
        if (_uniformLocations.TryGetValue(name, out int location))
        {
            return location;
        }

        location = _gl.GetUniformLocation(Handle, name);
        _uniformLocations[name] = location;
        return location;
    }

    public void SetUniform(string name, int value)
    {
        int loc = GetUniformLocation(name);
        if (loc != -1) _gl.Uniform1(loc, value);
    }

    public void SetUniform(string name, float value)
    {
        int loc = GetUniformLocation(name);
        if (loc != -1) _gl.Uniform1(loc, value);
    }

    public void SetUniform(string name, Vector2 value)
    {
        int loc = GetUniformLocation(name);
        if (loc != -1) _gl.Uniform2(loc, value.X, value.Y);
    }

    public void SetUniform(string name, Vector3 value)
    {
        int loc = GetUniformLocation(name);
        if (loc != -1) _gl.Uniform3(loc, value.X, value.Y, value.Z);
    }

    public void SetUniform(string name, Vector4 value)
    {
        int loc = GetUniformLocation(name);
        if (loc != -1) _gl.Uniform4(loc, value.X, value.Y, value.Z, value.W);
    }

    public unsafe void SetUniform(string name, Matrix4x4 matrix, bool transpose = false)
    {
        int loc = GetUniformLocation(name);
        if (loc != -1)
        {
            _gl.UniformMatrix4(loc, 1, transpose, (float*)&matrix);
        }
    }

    public void SetInt(string name, int value) => SetUniform(name, value);
    public void SetFloat(string name, float value) => SetUniform(name, value);
    public void SetMatrix4(string name, Matrix4x4 matrix, bool transpose = false) => SetUniform(name, matrix, transpose);

    public void Dispose()
    {
        if (Handle != 0)
        {
            _gl.DeleteProgram(Handle);
            Handle = 0;
        }
    }
}
