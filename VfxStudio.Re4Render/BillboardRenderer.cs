using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using VfxStudio.Renderer.Textures;
using Shader = VfxStudio.Renderer.Shaders.Shader;

namespace VfxStudio.Re4Render;

[StructLayout(LayoutKind.Sequential, Pack=4, Size=64)]
public struct BillboardInstance
{
    public Vector4 PositionRoll;
    public Vector4 Size;
    public uint Color, Uv, Id, Reserved;
    public Vector4 Extra;

    public static BillboardInstance Decode(ParticleRecord p, PreviewEntryState entry, uint uvFlags)
    {
        var raw = p.Raw.Span;
        float width = FrameDecoder.F32(raw,0x80) * FrameDecoder.F32(raw,0x84) * FrameDecoder.F32(raw,0xe8);
        float height = FrameDecoder.F32(raw,0x80) * FrameDecoder.F32(raw,0x88) * FrameDecoder.F32(raw,0xec);
        float roll = FrameDecoder.F32(raw,0x9c) * ((entry.Flags16c & 0x80) != 0 ? -1 : 1);
        uint rgba = FrameDecoder.U32(raw,0x4c);
        int life = (entry.Flags174 & 0x1000) != 0 ? 255 : p.LifeAlpha;
        float alpha = (rgba >> 24) * (life / 255f) * FrameDecoder.F32(raw,0x50);
        byte a = float.IsFinite(alpha) ? unchecked((byte)(int)alpha) : (byte)0;
        // Only fixed flips are identified; random flip / frame blend remain explicit P3 limitations.
        uint flips = ((uvFlags >> 2) & 3) == 1 ? 1u : 0;
        if (((uvFlags >> 4) & 3) == 1) flips |= 2;
        return new()
        {
            PositionRoll = new(p.Position,(float)(Half)roll), Size = new(width,height,0,0),
            Color = (rgba & 0xffffff) | ((uint)a << 24),
            Uv = ((p.Flags >> 10) & 255) | (((p.Flags >> 18) & 255) << 8) | (flips << 16), Id = p.Id,
            Extra = new(p.Age,0,0,0)
        };
    }
}

public sealed unsafe class BillboardRenderer : IDisposable
{
    private readonly GL gl;
    private readonly Shader shader;
    private readonly uint vao, instances, uvBuffer, sequenceBuffer;
    private readonly GpuTexture[] textures;
    private readonly PreviewDocument document;
    private readonly nint mapped;
    private readonly nint[] fences = new nint[3];
    private readonly List<(int Texture,int First,int Count)> batches = new();
    private const int Capacity = 10000, InstanceBytes = 64, SlotBytes = Capacity * InstanceBytes;
    private int currentSlot = -1;
    private PreviewFrame? uploaded;
    public int DrawCalls { get; private set; }
    /// <summary>Set when the live type block asks for a blend this renderer does not implement;
    /// the entry is then skipped instead of failing the render loop.</summary>
    public string? Unsupported { get; private set; }
    public static bool SupportsBlend(int blend) => blend is 0 or 1;
    public int InstanceCount { get; private set; }
    public double UploadMs { get; private set; }
    public int TextureCount => textures.Length;
    public string TextureDescription => string.Join(", ",textures.Select(t=>$"{t.Width}x{t.Height}"));

    [StructLayout(LayoutKind.Sequential, Pack=4)]
    private struct GpuUv { public Vector4 Rect; public uint Texture, R1, R2, R3; }

    public BillboardRenderer(GL gl, PreviewDocument document)
    {
        this.gl=gl; this.document=document;
        shader = new Shader(gl, """
            #version 460 core
            struct Instance { vec4 posRoll; vec4 size; uvec4 data; vec4 extra; };
            struct Frame { vec4 rect; uvec4 data; };
            layout(std430,binding=0) readonly buffer Instances { Instance particles[]; };
            layout(std430,binding=1) readonly buffer Frames { Frame frames[]; };
            layout(std430,binding=2) readonly buffer Sequences { uvec2 ranges[]; };
            uniform mat4 vp;
            uniform vec3 cameraRight, cameraUp;
            uniform int sequenceCount;
            out vec2 uv;
            out vec4 tint;
            void main() {
                Instance p = particles[gl_BaseInstance + gl_InstanceID];
                uint s = min(p.data.y & 255u, uint(sequenceCount-1));
                uvec2 range = ranges[s];
                uint f = min((p.data.y >> 8) & 255u, range.y-1);
                vec4 rect = frames[range.x+f].rect;
                vec2 corner = vec2(gl_VertexID & 1, (gl_VertexID >> 1) & 1);
                vec2 xy = (corner * 2 - 1) * vec2(.5,-.5) * p.size.xy;
                float c=cos(p.posRoll.w), sn=sin(p.posRoll.w);
                xy = mat2(c,sn,-sn,c) * xy;
                vec3 pos=p.posRoll.xyz + cameraRight*xy.x + cameraUp*xy.y;
                gl_Position=vp * vec4(pos,1);
                uint flips=p.data.y >> 16;
                if ((flips & 1u)!=0) corner.x=1-corner.x;
                if ((flips & 2u)!=0) corner.y=1-corner.y;
                uv=mix(rect.xy,rect.zw,corner);
                tint=unpackUnorm4x8(p.data.x);
            }
            """, """
            #version 460 core
            in vec2 uv;
            in vec4 tint;
            uniform sampler2D particleTexture;
            uniform float colorRate;
            layout(location=0) out vec4 color;
            layout(location=1) out vec4 bright;
            void main() {
                vec4 texel=texture(particleTexture,uv);
                color=vec4(tint.rgb * texel.rgb * colorRate, tint.a * texel.a);
                float brightness=dot(color.rgb,vec3(.2126,.7152,.0722));
                bright=vec4(brightness>1 ? color.rgb : vec3(0),color.a);
            }
            """);
        textures = new GpuTexture[document.Textures.Count];
        try
        {
            for (int i=0;i<textures.Length;i++)
            {
                var t=document.Textures[i];
                textures[i]=GpuTexture.CreateFromTexBytes(gl,t.Bytes,t.Path+".143221013") ?? throw new NotSupportedException("Unsupported TEX: "+t.Path);
            }
            vao=gl.GenVertexArray();
            instances=gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer,instances);
            gl.BufferStorage(GLEnum.ShaderStorageBuffer,(nuint)(SlotBytes*3),null,
                (uint)(BufferStorageMask.MapWriteBit | BufferStorageMask.MapPersistentBit | BufferStorageMask.MapCoherentBit));
            mapped=(nint)gl.MapBufferRange(BufferTargetARB.ShaderStorageBuffer,0,SlotBytes*3,
                MapBufferAccessMask.WriteBit | MapBufferAccessMask.PersistentBit | MapBufferAccessMask.CoherentBit);
            if(mapped==0) throw new InvalidOperationException("Persistent instance mapping failed");
            var frames=new List<GpuUv>(); var ranges=new List<uint>();
            foreach(var seq in document.Sequences)
            {
                ranges.Add((uint)frames.Count); ranges.Add((uint)seq.Length);
                frames.AddRange(seq.Select(f=>new GpuUv { Rect=f.Rect,Texture=(uint)f.Texture }));
            }
            uvBuffer=gl.GenBuffer(); sequenceBuffer=gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer,uvBuffer);
            var frameArray=frames.ToArray();
            fixed(GpuUv* ptr=frameArray) gl.BufferData(BufferTargetARB.ShaderStorageBuffer,(nuint)(frameArray.Length*32),ptr,BufferUsageARB.StaticDraw);
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer,sequenceBuffer);
            var rangeArray=ranges.ToArray();
            fixed(uint* ptr=rangeArray) gl.BufferData(BufferTargetARB.ShaderStorageBuffer,(nuint)(rangeArray.Length*4),ptr,BufferUsageARB.StaticDraw);
        }
        catch { Dispose(); throw; }
    }

    private void Upload(PreviewFrame frame)
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();
        currentSlot=(currentSlot+1)%3;
        if(fences[currentSlot]!=0)
        {
            var result=gl.ClientWaitSync(fences[currentSlot],SyncObjectMask.Bit,1_000_000_000);
            if(result is GLEnum.TimeoutExpired or GLEnum.WaitFailed) throw new TimeoutException("GPU instance buffer is still in use");
            gl.DeleteSync(fences[currentSlot]); fences[currentSlot]=0;
        }
        var state=frame.EntryStates?.Single() ?? throw new InvalidDataException("Missing native type block");
        if(frame.Particles.Count>Capacity) throw new InvalidDataException("Instance capacity exceeded");
        batches.Clear();
        BillboardInstance* destination=(BillboardInstance*)(mapped+currentSlot*SlotBytes);
        int count=0;
        foreach(var p in frame.Particles)
        {
            var item=BillboardInstance.Decode(p,state,document.UvFlags);
            if(!float.IsFinite(item.PositionRoll.X) || !float.IsFinite(item.PositionRoll.Y) || !float.IsFinite(item.PositionRoll.Z) ||
                !float.IsFinite(item.PositionRoll.W) || !float.IsFinite(item.Size.X) || !float.IsFinite(item.Size.Y)) continue;
            int s=Math.Min((int)(item.Uv&255),document.Sequences.Count-1);
            var seq=document.Sequences[s];
            int f=Math.Min((int)((item.Uv>>8)&255),seq.Length-1);
            int texture=seq[f].Texture;
            destination[count]=item;
            if(batches.Count==0 || batches[^1].Texture!=texture) batches.Add((texture,count,1));
            else { var batch=batches[^1]; batches[^1]=(batch.Texture,batch.First,batch.Count+1); }
            count++;
        }
        InstanceCount=count;
        uploaded=frame;
        UploadMs=watch.Elapsed.TotalMilliseconds;
    }

    public void Render(PreviewFrame frame, Matrix4x4 view, Matrix4x4 projection)
    {
        if(!ReferenceEquals(frame,uploaded)) Upload(frame);
        var state=frame.EntryStates!.Single();
        DrawCalls=0;
        if(!SupportsBlend(state.Blend)) { Unsupported=$"混合方式 {state.Blend} 尚未实现，本帧未绘制。"; return; }
        Unsupported=null;
        gl.Disable(EnableCap.CullFace); gl.Enable(EnableCap.DepthTest); gl.DepthMask(false);
        gl.Enable(EnableCap.Blend);
        gl.BlendFuncSeparate(BlendingFactor.SrcAlpha,state.Blend==0?BlendingFactor.OneMinusSrcAlpha:BlendingFactor.One,BlendingFactor.One,BlendingFactor.OneMinusSrcAlpha);
        shader.Use();shader.SetUniform("vp",view*projection);
        shader.SetUniform("cameraRight",new Vector3(view.M11,view.M21,view.M31));
        shader.SetUniform("cameraUp",new Vector3(view.M12,view.M22,view.M32));
        shader.SetUniform("sequenceCount",document.Sequences.Count);
        shader.SetUniform("colorRate",state.ColorRate);shader.SetUniform("particleTexture",0);
        gl.BindVertexArray(vao);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer,0,instances);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer,1,uvBuffer);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer,2,sequenceBuffer);
        foreach(var batch in batches)
        {
            textures[batch.Texture].Bind();
            gl.DrawArraysInstancedBaseInstance(PrimitiveType.TriangleStrip,0,4,(uint)batch.Count,(uint)(currentSlot*Capacity+batch.First));
            DrawCalls++;
        }
        // Refresh the fence even when repeatedly drawing the same paused frame.
        if(fences[currentSlot]!=0) gl.DeleteSync(fences[currentSlot]);
        fences[currentSlot]=gl.FenceSync(SyncCondition.SyncGpuCommandsComplete,SyncBehaviorFlags.None);
        gl.BindVertexArray(0); gl.Disable(EnableCap.Blend);gl.DepthMask(true);
    }

    public void Dispose()
    {
        foreach(var fence in fences) if(fence!=0) gl.DeleteSync(fence);
        if(mapped!=0) { gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer,instances);gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer); }
        gl.DeleteBuffer(instances);gl.DeleteBuffer(uvBuffer);gl.DeleteBuffer(sequenceBuffer);gl.DeleteVertexArray(vao);
        foreach(var t in textures) t?.Dispose();
        shader.Dispose();
    }
}
