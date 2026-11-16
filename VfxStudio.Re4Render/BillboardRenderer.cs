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

    /// <summary>Polygon orientation from particle +0x9c..+0xa4 and RotationOrder ([p+4] &amp; 7), as dfae10
    /// builds it: (x, y, z, w), the axis rotations multiplied in the reverse of the order's name
    /// (XYZ: qZ·qY·qX). Checked against the native function by tools/Re4Native/exp_polygon_quat.py.</summary>
    public static Quaternion PolygonRotation(float x, float y, float z, uint order)
    {
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, x);
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, y);
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, z);
        // System.Numerics: a * b is the Hamilton product a·b.
        return (order & 7) switch
        {
            0 => qz * qy * qx,   // XYZ
            1 => qx * qz * qy,   // YZX
            2 => qy * qx * qz,   // ZXY
            3 => qx * qy * qz,   // ZYX
            4 => qz * qx * qy,   // YXZ
            5 => qy * qz * qx,   // XZY
            _ => Quaternion.Identity
        };
    }

    public static BillboardInstance Decode(ParticleRecord p, PreviewEntryState entry, uint uvFlags, PreviewTypeKind kind = PreviewTypeKind.Billboard3D)
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
        var q = kind == PreviewTypeKind.Polygon
            ? PolygonRotation(FrameDecoder.F32(raw,0x9c), FrameDecoder.F32(raw,0xa0), FrameDecoder.F32(raw,0xa4), FrameDecoder.U32(raw,4))
            : Quaternion.Identity;
        return new()
        {
            // Size.zw and Extra.zw carry the Polygon quaternion (x, y | z, w).
            PositionRoll = new(p.Position,(float)(Half)roll), Size = new(width,height,q.X,q.Y),
            Color = (rgba & 0xffffff) | ((uint)a << 24),
            Uv = ((p.Flags >> 10) & 255) | (((p.Flags >> 18) & 255) << 8) | (flips << 16), Id = p.Id,
            Extra = new(p.Age,p.LifeAlpha/255f,q.Z,q.W)
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
    /// <summary>Position of this entry in the frame's entry table (the load order).</summary>
    public int Table { get; }
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
    public static bool SupportsBlend(int blend) => blend is >= 0 and <= 4;
    public int InstanceCount { get; private set; }
    public double UploadMs { get; private set; }
    public int TextureCount => textures.Length;
    public string TextureDescription => string.Join(", ",textures.Select(t=>$"{t.Width}x{t.Height}"));

    internal const string VertexSource = "#version 460 core\n" + AppearanceShaderLibrary.Block + AppearanceShaderLibrary.Functions + """
        struct Instance { vec4 posRoll; vec4 size; uvec4 data; vec4 extra; };
        struct Frame { vec4 rect; uvec4 data; };
        layout(std430,binding=0) readonly buffer Instances { Instance particles[]; };
        layout(std430,binding=1) readonly buffer Frames { Frame frames[]; };
        layout(std430,binding=2) readonly buffer Sequences { uvec2 ranges[]; };
        uniform mat4 vp;
        uniform vec3 cameraRight, cameraUp, cameraPos;
        uniform int sequenceCount;
        uniform int typeKind;          // 0 Billboard3D, 1 Polygon, 2 Billboard2D, 3 ribbon segment, 4 sheet segment (PolygonTrail)
        uniform vec2 screenSize;       // pixels, for Billboard2D
        // One ribbon segment: two engine points (world position + V, tangent + half width) and
        // (colour at each end, UV sequence | frame << 8, life byte).
        struct Segment { vec4 a; vec4 b; vec4 c; vec4 d; uvec4 e; };
        layout(std430,binding=4) readonly buffer Segments { Segment segs[]; };
        out vec2 uv;
        out vec4 tint;
        out float viewDepth;
        flat out uint acBytes;
        out vec3 lightIn;          // light reaching a lit particle (1 when unlit)
        out vec2 uvPrevious;       // FrameBlend: same corner in the previous frame
        flat out float frameWeight; // FrameBlend: acc / T (1 shows the current frame only)
        void ribbon() {
            Segment g = segs[gl_BaseInstance + gl_InstanceID];
            float side = float(gl_VertexID & 1);
            bool tail = ((gl_VertexID >> 1) & 1) != 0;
            vec3 P = tail ? g.b.xyz : g.a.xyz;
            float V = tail ? g.b.w : g.a.w;
            vec3 pos;
            if (typeKind == 4) {
                // PolygonTrail: the engine's sheet column, base (c/d = tip) to tip; no widening.
                pos = mix(P, tail ? g.d.xyz : g.c.xyz, side);
            } else {
                vec3 T = tail ? g.d.xyz : g.c.xyz;
                float halfWidth = tail ? g.d.w : g.c.w;
                if (dot(T,T) < 1e-12) T = g.b.xyz - g.a.xyz;
                // RENDER-HOST 5.4 (compromise for CPU ribbons): widen across view direction x tangent.
                vec3 w = cross(normalize(P - cameraPos), T);
                w = dot(w,w) < 1e-12 ? cameraRight : normalize(w);
                pos = P + (side * 2.0 - 1.0) * halfWidth * w;
            }
            vec4 clip = vp * vec4(pos,1);
            float factor = 1.0;
            if (hasFlag(1)) factor *= fadeDepth((vp * vec4(P,1)).w, fadeDepthP);
            if (hasFlag(2)) factor *= fadeAngle(coneDir.xyz, emitterPos.xyz, P, cameraPos, modes.z, fadeAngleP.x, fadeAngleP.y, fadeAngleP.z);
            if (factor < CULL) { gl_Position=vec4(2.0,2.0,2.0,1.0); uv=vec2(0); tint=vec4(0); viewDepth=0; acBytes=0u; lightIn=vec3(1); uvPrevious=vec2(0); frameWeight=1.0; return; }
            vec3 n = normalize(cameraPos - P);
            if (typeKind == 4) {
                vec3 c = cross(g.c.xyz - g.a.xyz, g.b.xyz - g.a.xyz);
                if (dot(c,c) > 1e-12) { c = normalize(c); n = dot(c, n) < 0.0 ? -c : c; }
            }
            lightIn = hasFlag(2048) ? previewLight(n) : vec3(1.0);
            uvPrevious = vec2(0); frameWeight = 1.0;
            if (hasFlag(64)) {
                vec4 biased = vp * vec4(pos + normalize(cameraPos - pos) * (-shading.y), 1);
                clip.z = biased.z / biased.w * clip.w;
            }
            gl_Position = clip;
            viewDepth = clip.w;
            uint s = min(g.e.z & 255u, uint(sequenceCount-1));
            uvec2 range = ranges[s];
            uint f = min((g.e.z >> 8) & 255u, range.y-1);
            vec4 rect = frames[range.x+f].rect;
            uv = mix(rect.xy, rect.zw, vec2(side, V));
            tint = unpackUnorm4x8(tail ? g.e.y : g.e.x);
            tint.a *= factor;
            acBytes = uint(acInfo.x);
            if (((acBytes >> 24) & 3u) == 3u) acBytes = (acBytes & 0xff00ffffu) | (g.e.w << 16);
        }
        void main() {
            if (typeKind >= 3) { ribbon(); return; }
            Instance p = particles[gl_BaseInstance + gl_InstanceID];
            uint s = min(p.data.y & 255u, uint(sequenceCount-1));
            uvec2 range = ranges[s];
            uint f = min((p.data.y >> 8) & 255u, range.y-1);
            vec4 rect = frames[range.x+f].rect;
            vec2 corner = vec2(gl_VertexID & 1, (gl_VertexID >> 1) & 1);
            vec2 xy = (corner * 2 - 1) * vec2(.5,-.5) * p.size.xy;
            vec3 pos;
            vec4 clip;
            if (typeKind == 1) {
                // Polygon: a world-oriented quad in its local XY plane, rotated by the particle quaternion.
                vec4 q = vec4(p.size.zw, p.extra.zw);
                vec3 local = vec3(xy, 0.0);
                pos = p.posRoll.xyz + local + 2.0 * cross(q.xyz, cross(q.xyz, local) + q.w * local);
                clip = vp * vec4(pos,1);
            } else if (typeKind == 2) {
                // Billboard2D (compromise): position and size in pixels of a 1080-line screen, origin at
                // the screen centre, +Y up; drawn in front of everything.
                float c=cos(p.posRoll.w), sn=sin(p.posRoll.w);
                xy = mat2(c,sn,-sn,c) * xy;
                float k = screenSize.y / 1080.0;
                vec2 pixel = (p.posRoll.xy + xy) * k;
                pos = vec3(0.0);
                clip = vec4(pixel / (screenSize * 0.5), 0.0, 1.0);
            } else {
                float c=cos(p.posRoll.w), sn=sin(p.posRoll.w);
                xy = mat2(c,sn,-sn,c) * xy;
                pos=p.posRoll.xyz + cameraRight*xy.x + cameraUp*xy.y;
                clip=vp * vec4(pos,1);
            }
            // RUNTIME 4.6: depth fade uses the particle centre's clip w; angle fades take the larger term.
            float factor=1.0;
            if (hasFlag(1)) factor *= fadeDepth((vp * vec4(p.posRoll.xyz,1)).w, fadeDepthP);
            if (hasFlag(2)) factor *= fadeAngle(coneDir.xyz, emitterPos.xyz, p.posRoll.xyz, cameraPos, modes.z, fadeAngleP.x, fadeAngleP.y, fadeAngleP.z);
            if (factor < CULL) { gl_Position=vec4(2.0,2.0,2.0,1.0); uv=vec2(0); tint=vec4(0); viewDepth=0; acBytes=0u; lightIn=vec3(1); uvPrevious=vec2(0); frameWeight=1.0; return; }
            if (hasFlag(2048) && typeKind != 2) {
                vec3 toCamera = normalize(cameraPos - pos);
                vec3 n = toCamera;
                if (typeKind == 1) {
                    // Polygon: its local +Z rotated by the particle quaternion, turned towards the camera.
                    vec4 q = vec4(p.size.zw, p.extra.zw);
                    vec3 z = vec3(0.0, 0.0, 1.0);
                    n = normalize(z + 2.0 * cross(q.xyz, cross(q.xyz, z) + q.w * z));
                    if (dot(n, toCamera) < 0.0) n = -n;
                }
                lightIn = previewLight(n);
            } else lightIn = vec3(1.0);
            if (hasFlag(64) && typeKind != 2) {
                // Depth test only: the point moved DepthBias metres along the view ray (negative = nearer).
                vec4 biased = vp * vec4(pos + normalize(cameraPos - pos) * (-shading.y), 1);
                clip.z = biased.z / biased.w * clip.w;
            }
            gl_Position=clip;
            viewDepth=clip.w;
            uint flips=p.data.y >> 16;
            if ((flips & 1u)!=0) corner.x=1-corner.x;
            if ((flips & 2u)!=0) corner.y=1-corner.y;
            uv=mix(rect.xy,rect.zw,corner);
            // data.w: previous frame index (low 8 bits) and the blend weight acc/T (high 16 bits).
            frameWeight = 1.0; uvPrevious = uv;
            if (hasFlag(4096)) {
                uint prevF = min(p.data.w & 255u, range.y-1);
                vec4 prevRect = frames[range.x+prevF].rect;
                uvPrevious = mix(prevRect.xy, prevRect.zw, corner);
                frameWeight = float(p.data.w >> 16) / 65535.0;
            }
            tint=unpackUnorm4x8(p.data.x);
            tint.a*=factor;
            acBytes=uint(acInfo.x);
            if (((acBytes >> 24) & 3u) == 3u) acBytes=(acBytes & 0xff00ffffu) | (uint(p.extra.y*255.0+0.5) << 16);
        }
        """;
    internal const string FragmentSource = "#version 460 core\n" + AppearanceShaderLibrary.Block + AppearanceShaderLibrary.Functions + """
        in vec2 uv;
        in vec4 tint;
        in float viewDepth;
        flat in uint acBytes;
        in vec3 lightIn;
        in vec2 uvPrevious;
        flat in float frameWeight;
        uniform sampler2D particleTexture;
        uniform sampler2D sceneDepth;
        uniform vec2 viewportSize;
        layout(location=0) out vec4 color;
        layout(location=1) out vec4 bright;
        void main() {
            vec4 texel=texture(particleTexture,uv);
            // FrameBlend (UVSequence Flags bit 10): previous frame -> current frame by acc / T.
            if (hasFlag(4096) && frameWeight < 1.0) texel = mix(texture(particleTexture,uvPrevious), texel, frameWeight);
            particleLight = lightIn;
            float soft=1.0;
            if (hasFlag(32)) {
                float z=texture(sceneDepth, gl_FragCoord.xy / viewportSize).r * 2.0 - 1.0;
                float n=viewP.y, fa=viewP.z;
                float sceneView = 2.0*n*fa / (fa + n - z*(fa - n));
                soft=clamp((sceneView - viewDepth) / shading.x, 0.0, 1.0);
            }
            vec4 c=shade(texel, tint, soft, acBytes);
            if (modes.y == 4) { color=c; bright=c; return; }      // multiplicative: scale bloom too
            if (c.a < CULL) discard;
            color=c;
            float brightness=dot(c.rgb,vec3(.2126,.7152,.0722));
            bright=vec4(brightness>1 ? c.rgb : vec3(0),c.a);
        }
        """;
    // False when the shader and textures belong to a BillboardGpuCache shared by many renderers.
    private readonly bool owned;
    private readonly uint entryBuffer;
    // Ribbon segments, rebuilt on every upload (sizes vary with the trail lengths).
    private uint segmentBuffer;
    private float[] segmentData = [];
    private int segmentCount;
    private const int SegmentFloats = 20;
    public EntryAppearance Appearance { get; }
    /// <summary>Emitter position used by FadeByAngle: the file's Transform3D position (the emitter
    /// world matrix is identity in this preview).</summary>
    public Vector3 EmitterPosition { get; }

    [StructLayout(LayoutKind.Sequential, Pack=4)]
    private struct GpuUv { public Vector4 Rect; public uint Texture, R1, R2, R3; }

    public BillboardRenderer(GL gl, PreviewDocument document, int table = 0, BillboardGpuCache? cache = null)
    {
        this.gl=gl; this.document=document; Table=table;
        Appearance=EntryAppearance.From(document.Entry);
        EmitterPosition=document.Entry.Attributes.OfType<ReeLib.Efx.Structs.Transforms.EFXAttributeTransform3D>().FirstOrDefault()?.LocalPosition ?? Vector3.Zero;
        owned = cache == null;
        shader = cache?.Shader ?? new Shader(gl, VertexSource, FragmentSource);
        textures = new GpuTexture[document.Textures.Count];
        try
        {
            for (int i=0;i<textures.Length;i++)
            {
                var t=document.Textures[i];
                textures[i]=cache?.Texture(t) ?? GpuTexture.CreateFromTexBytes(gl,t.Bytes,t.Path+".143221013") ?? throw new NotSupportedException("Unsupported TEX: "+t.Path);
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
            if(document.IsRibbon) segmentBuffer=gl.GenBuffer();
            entryBuffer=gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.UniformBuffer,entryBuffer);
            gl.BufferData(BufferTargetARB.UniformBuffer,(nuint)EntryUniforms.Std140Bytes,null,BufferUsageARB.DynamicDraw);
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

    public PreviewTypeKind Kind => document.Kind;
    public int Blend(PreviewFrame frame) => ColorHeader.From(State(frame), document).Blend;

    public PreviewEntryState State(PreviewFrame frame)
    {
        var states=frame.EntryStates ?? throw new InvalidDataException("Missing native type block");
        if(Table>=states.Count) throw new InvalidDataException($"Frame has no entry at load position {Table}");
        var state=states[Table];
        if(state.Index!=document.Entry.index) throw new InvalidDataException($"Frame entry {state.Index} at load position {Table} does not match entry {document.Entry.index}");
        return state;
    }

    private Matrix4x4 uploadedView;
    private bool uploadedSorted;
    private readonly List<(float Depth,int Index)> order = new();

    /// <summary>Ribbon segments from the engine's point records: +0x00 world position, +0x18 tangent,
    /// +0x40 V, +0x44 point colour RGBA8, +0x48 point size factor. Width = U*X*scale of the particle
    /// (same convention as Billboard3D) times the point size; colour = particle colour x point colour.</summary>
    private void UploadRibbon(PreviewFrame frame)
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();
        var state=State(frame);
        batches.Clear();
        int needed=0;
        for(int i=state.First;i<state.First+state.Count;i++) needed+=Math.Max(0,state.ValidPoints(frame.Particles[i].Slot)-1);
        if(segmentData.Length<needed*SegmentFloats) segmentData=new float[Math.Max(needed*SegmentFloats,SegmentFloats*64)];
        var words=MemoryMarshal.Cast<float,uint>(segmentData.AsSpan());
        int count=0;
        static uint Mul(uint a,uint b)
        {
            uint r=0;
            for(int k=0;k<32;k+=8) r|=(uint)Math.Round(((a>>k)&255)*((b>>k)&255)/255.0)<<k;
            return r;
        }
        for(int i=state.First;i<state.First+state.Count;i++)
        {
            var p=frame.Particles[i];
            int valid=state.ValidPoints(p.Slot);
            if(valid<2) continue;
            var points=state.PointsOf(p.Slot);
            var item=BillboardInstance.Decode(p,state,document.UvFlags,document.Kind);
            if(!float.IsFinite(item.Size.X)) continue;
            int s=Math.Min((int)(item.Uv&255),document.Sequences.Count-1);
            var seq=document.Sequences[s];
            int f=Math.Min((int)((item.Uv>>8)&255),seq.Length-1);
            int texture=seq[f].Texture;
            uint life=(uint)Math.Round(item.Extra.Y*255);
            for(int k=0;k<valid-1;k++)
            {
                var r0=points.Slice(k*FrameDecoder.PointStride,FrameDecoder.PointStride);
                var r1=points.Slice((k+1)*FrameDecoder.PointStride,FrameDecoder.PointStride);
                var o=segmentData.AsSpan(count*SegmentFloats,SegmentFloats);
                bool finite=true;
                for(int c=0;c<3;c++)
                {
                    o[c]=FrameDecoder.F32(r0,4*c); o[4+c]=FrameDecoder.F32(r1,4*c);
                    o[8+c]=FrameDecoder.F32(r0,0x18+4*c); o[12+c]=FrameDecoder.F32(r1,0x18+4*c);
                    finite&=float.IsFinite(o[c]) && float.IsFinite(o[4+c]);
                }
                if(document.IsSheet)
                    for(int c=8;c<15;c++) finite&=c is 11 || float.IsFinite(o[c]);   // tips (exported in the tangent slot)
                if(!finite) continue;
                o[3]=FrameDecoder.F32(r0,0x40); o[7]=FrameDecoder.F32(r1,0x40);
                if(document.IsSheet) { o[11]=0; o[15]=0; }
                else { o[11]=.5f*item.Size.X*FrameDecoder.F32(r0,0x48); o[15]=.5f*item.Size.X*FrameDecoder.F32(r1,0x48); }
                var w=words.Slice(count*SegmentFloats+16,4);
                w[0]=Mul(item.Color,FrameDecoder.U32(r0,0x44)); w[1]=Mul(item.Color,FrameDecoder.U32(r1,0x44));
                w[2]=(item.Uv&255)|(((item.Uv>>8)&255)<<8); w[3]=life;
                if(batches.Count==0 || batches[^1].Texture!=texture) batches.Add((texture,count,1));
                else { var batch=batches[^1]; batches[^1]=(batch.Texture,batch.First,batch.Count+1); }
                count++;
            }
        }
        segmentCount=count; InstanceCount=count;
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer,segmentBuffer);
        fixed(float* ptr=segmentData) gl.BufferData(BufferTargetARB.ShaderStorageBuffer,(nuint)(Math.Max(1,count)*SegmentFloats*4),ptr,BufferUsageARB.StreamDraw);
        uploaded=frame;
        UploadMs=watch.Elapsed.TotalMilliseconds;
    }

    private void Upload(PreviewFrame frame, Matrix4x4 view, bool sort)
    {
        if(document.IsRibbon) { UploadRibbon(frame); return; }
        var watch=System.Diagnostics.Stopwatch.StartNew();
        currentSlot=(currentSlot+1)%3;
        if(fences[currentSlot]!=0)
        {
            var result=gl.ClientWaitSync(fences[currentSlot],SyncObjectMask.Bit,1_000_000_000);
            if(result is GLEnum.TimeoutExpired or GLEnum.WaitFailed) throw new TimeoutException("GPU instance buffer is still in use");
            gl.DeleteSync(fences[currentSlot]); fences[currentSlot]=0;
        }
        var state=State(frame);
        if(state.Count>Capacity) throw new InvalidDataException("Instance capacity exceeded");
        batches.Clear();
        BillboardInstance* destination=(BillboardInstance*)(mapped+currentSlot*SlotBytes);
        int count=0;
        order.Clear();
        for(int i=state.First;i<state.First+state.Count;i++)
            order.Add((sort ? Vector3.Transform(frame.Particles[i].Position,view).Z : 0, i));
        // View space looks down -Z: the most negative Z is farthest and is drawn first.
        if(sort) order.Sort((a,b)=>a.Depth.CompareTo(b.Depth));
        foreach(var (_,i) in order)
        {
            var p=frame.Particles[i];
            var item=BillboardInstance.Decode(p,state,document.UvFlags,document.Kind);
            if(!float.IsFinite(item.PositionRoll.X) || !float.IsFinite(item.PositionRoll.Y) || !float.IsFinite(item.PositionRoll.Z) ||
                !float.IsFinite(item.PositionRoll.W) || !float.IsFinite(item.Size.X) || !float.IsFinite(item.Size.Y)) continue;
            int s=Math.Min((int)(item.Uv&255),document.Sequences.Count-1);
            var seq=document.Sequences[s];
            int f=Math.Min((int)((item.Uv>>8)&255),seq.Length-1);
            int texture=seq[f].Texture;
            if(document.FrameBlend) item.Reserved=FrameBlendWord(p,f,seq.Length);
            destination[count]=item;
            if(batches.Count==0 || batches[^1].Texture!=texture) batches.Add((texture,count,1));
            else { var batch=batches[^1]; batches[^1]=(batch.Texture,batch.First,batch.Count+1); }
            count++;
        }
        InstanceCount=count;
        uploaded=frame; uploadedView=view; uploadedSorted=sort;
        UploadMs=watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>FrameBlend data for one particle (UVSEQ 3d1e5d0): the previous frame is frame - 1 (wrapping)
    /// when playing forward and frame + 1 when playing backward ([p+4] bit 7); the weight acc/T is at
    /// +0xe4. Packed as previous frame | weight (16-bit unorm) &lt;&lt; 16.</summary>
    public static uint FrameBlendWord(ParticleRecord p, int frame, int count)
    {
        var raw=p.Raw.Span;
        bool backward=(FrameDecoder.U32(raw,4) & 0x80)!=0;
        int previous=count<=1 ? frame : backward ? (frame+1)%count : (frame-1+count)%count;
        float weight=FrameDecoder.F32(raw,0xe4);
        if(!float.IsFinite(weight)) weight=1;
        return (uint)previous | (uint)Math.Round(Math.Clamp(weight,0f,1f)*65535)<<16;
    }

    public void Render(PreviewFrame frame, Matrix4x4 view, Matrix4x4 projection) => Render(frame, view, projection, PreviewRenderContext.Default);

    public void Render(PreviewFrame frame, Matrix4x4 view, Matrix4x4 projection, PreviewRenderContext context)
    {
        var config=context.Config;
        bool sort=config.SortWithinEntry && !document.IsRibbon;
        if(!ReferenceEquals(frame,uploaded) || sort!=uploadedSorted || sort && view!=uploadedView) Upload(frame,view,sort);
        var state=State(frame);
        var header=ColorHeader.From(state,document);
        DrawCalls=0;
        if(!SupportsBlend(header.Blend)) { Unsupported=$"混合方式 {header.Blend} 尚未实现，本帧未绘制。"; return; }
        Unsupported=null;
        var uniforms=EntryUniforms.Build(header,Appearance,config,EmitterPosition);
        uniforms.SetTexelChannel(state,config);
        // FrameBlend needs the per-particle data written in Upload, which ribbons do not carry.
        if(document.IsRibbon || !document.FrameBlend) uniforms.Flags&=~EntryUniforms.FrameBlendBit;
        // Screen-space quads have no view depth: no fades, soft particles or depth bias.
        if(document.Kind==PreviewTypeKind.Billboard2D)
            uniforms.Flags&=~(EntryUniforms.FadeDepthBit|EntryUniforms.FadeAngleBit|EntryUniforms.SoftBit|EntryUniforms.DepthBiasBit);
        uniforms.Exposure=context.Exposure; uniforms.Near=context.Near; uniforms.Far=context.Far;
        if(context.SceneDepth==0) uniforms.Flags&=~EntryUniforms.SoftBit;
        var bytes=uniforms.ToStd140();
        gl.BindBuffer(BufferTargetARB.UniformBuffer,entryBuffer);
        fixed(byte* ptr=bytes) gl.BufferSubData(BufferTargetARB.UniformBuffer,0,(nuint)bytes.Length,ptr);
        gl.BindBufferBase(BufferTargetARB.UniformBuffer,3,entryBuffer);
        gl.Disable(EnableCap.CullFace); gl.Enable(EnableCap.DepthTest); gl.DepthMask(false);
        gl.Enable(EnableCap.Blend);
        var pair=config.Blends[header.Blend];
        gl.BlendFuncSeparate(Gl(pair.Source),Gl(pair.Destination),BlendingFactor.One,BlendingFactor.OneMinusSrcAlpha);
        Matrix4x4.Invert(view,out var inverse);
        shader.Use();shader.SetUniform("vp",view*projection);
        shader.SetUniform("cameraRight",new Vector3(view.M11,view.M21,view.M31));
        shader.SetUniform("cameraUp",new Vector3(view.M12,view.M22,view.M32));
        shader.SetUniform("cameraPos",new Vector3(inverse.M41,inverse.M42,inverse.M43));
        shader.SetUniform("sequenceCount",document.Sequences.Count);
        shader.SetUniform("particleTexture",0);shader.SetUniform("sceneDepth",1);
        Span<int> viewport=stackalloc int[4];gl.GetInteger(GetPName.Viewport,viewport);
        shader.SetUniform("viewportSize",new Vector2(Math.Max(1,viewport[2]),Math.Max(1,viewport[3])));
        shader.SetUniform("screenSize",new Vector2(Math.Max(1,viewport[2]),Math.Max(1,viewport[3])));
        shader.SetUniform("typeKind",document.IsSheet ? 4 : document.IsRibbon ? 3 : (int)document.Kind);
        gl.ActiveTexture(TextureUnit.Texture1);gl.BindTexture(TextureTarget.Texture2D,context.SceneDepth);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindVertexArray(vao);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer,0,instances);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer,1,uvBuffer);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer,2,sequenceBuffer);
        if(document.IsRibbon)
        {
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer,4,segmentBuffer);
            foreach(var batch in batches)
            {
                textures[batch.Texture].Bind();
                gl.DrawArraysInstancedBaseInstance(PrimitiveType.TriangleStrip,0,4,(uint)batch.Count,(uint)batch.First);
                DrawCalls++;
            }
            gl.BindVertexArray(0); gl.Disable(EnableCap.Blend);gl.DepthMask(true);
            return;
        }
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

    private static BlendingFactor Gl(BlendFactor f) => f switch
    {
        BlendFactor.Zero=>BlendingFactor.Zero, BlendFactor.One=>BlendingFactor.One, BlendFactor.SrcAlpha=>BlendingFactor.SrcAlpha,
        BlendFactor.OneMinusSrcAlpha=>BlendingFactor.OneMinusSrcAlpha, BlendFactor.DstColor=>BlendingFactor.DstColor,
        BlendFactor.OneMinusDstColor=>BlendingFactor.OneMinusDstColor, BlendFactor.SrcColor=>BlendingFactor.SrcColor,
        _=>BlendingFactor.OneMinusSrcColor
    };

    public void Dispose()
    {
        foreach(var fence in fences) if(fence!=0) gl.DeleteSync(fence);
        if(mapped!=0) { gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer,instances);gl.UnmapBuffer(BufferTargetARB.ShaderStorageBuffer); }
        if(segmentBuffer!=0) gl.DeleteBuffer(segmentBuffer);
        gl.DeleteBuffer(instances);gl.DeleteBuffer(entryBuffer);gl.DeleteBuffer(uvBuffer);gl.DeleteBuffer(sequenceBuffer);gl.DeleteVertexArray(vao);
        if(owned) { foreach(var t in textures) t?.Dispose(); shader.Dispose(); }
    }
}

/// <summary>Shader and textures shared by every BillboardRenderer of a window. Textures are keyed by
/// their resolved source (loose path or PAK entry); game resources do not change during a session.</summary>
public sealed class BillboardGpuCache(GL gl) : IDisposable
{
    private readonly Dictionary<string,GpuTexture> textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,string> failures = new(StringComparer.OrdinalIgnoreCase);
    private Shader? shader;
    public Shader Shader => shader ??= new Shader(gl, BillboardRenderer.VertexSource, BillboardRenderer.FragmentSource);
    public int TextureCount => textures.Count;

    public GpuTexture Texture(PreviewTexture texture)
    {
        if(textures.TryGetValue(texture.Source,out var hit)) return hit;
        if(failures.TryGetValue(texture.Source,out var known)) throw new NotSupportedException(known);
        try
        {
            var created=GpuTexture.CreateFromTexBytes(gl,texture.Bytes,texture.Path+".143221013") ?? throw new NotSupportedException("Unsupported TEX: "+texture.Path);
            textures[texture.Source]=created;
            return created;
        }
        catch(Exception ex) when(ex is NotSupportedException or ArgumentException or IndexOutOfRangeException or OverflowException or InvalidDataException)
        {
            failures[texture.Source]=ex.Message;
            throw;
        }
    }

    public void Dispose()
    {
        foreach(var t in textures.Values) t.Dispose();
        textures.Clear();
        shader?.Dispose();
    }
}

/// <summary>Frame-wide inputs of a billboard draw.</summary>
public sealed record PreviewRenderContext(CompositionConfig Config, float Exposure = 1, float Near = .05f, float Far = 100, uint SceneDepth = 0)
{
    public static readonly PreviewRenderContext Default = new(new CompositionConfig());
}
