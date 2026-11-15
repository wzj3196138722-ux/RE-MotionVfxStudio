using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using ReeLib;
using ReeLib.Efx.Structs.Basic;
using ReeLib.Efx.Structs.Common;
using VfxStudio.Re4Render;

[assembly:SupportedOSPlatform("windows")]
try
{
string repo=Directory.GetCurrentDirectory();
string exe=Environment.GetEnvironmentVariable("RE4_EXE") ?? @"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe";
string game=Path.GetDirectoryName(exe)!;
string python=Environment.GetEnvironmentVariable("RE4_PYTHON") ?? "python";
string script=Path.Combine(repo,"tools/Re4Native/re4host.py");
using var resources=new Re4ResourceSource(game);
Directory.CreateDirectory("test-output/re4-preview");
var results=new List<object>();
void Check(bool ok,string reason) {if(!ok) throw new Exception(reason);}
Check(Marshal.SizeOf<BillboardInstance>()==64,"GPU instance layout is not 64 bytes");
foreach(string fixture in new[]{"bb","bb2"})
{
    var location=PreviewDocument.Fixture(game,fixture);
    var doc=PreviewDocument.Read(location.Path,location.Offset,resources);
    int expectedCount=fixture=="bb"?1:64;
    Check(doc.Sequences.Count==1 && doc.Sequences[0].Length==expectedCount,"Unexpected actual UVS sequence sizes");
    var info=new ProcessStartInfo(python){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    string code="""
        import sys,json
        sys.path.insert(0,sys.argv[1])
        import emitter as em,re4native as rn
        path,offset,uvs,counts=sys.argv[2],int(sys.argv[3]),sys.argv[4],json.loads(sys.argv[5])
        img=rn.load();em.install_runtime(img)
        em.UVS_COUNTS[uvs.replace('\\','/').lower()]=counts
        e=em.Entry(img,path,offset);m=em.Emitter(img,e);m.play()
        frames=[]
        for tick in range(1,181):
            m.tick(1/60);frames.append(m.state())
        print(json.dumps(frames))
        """;
    foreach(string arg in new[]{"-I","-X","utf8","-c",code,Path.Combine(repo,"tools/Re4Native"),doc.Path,doc.Offset.ToString(),doc.UvsPath,JsonSerializer.Serialize(doc.Sequences.Select(s=>s.Length))})info.ArgumentList.Add(arg);
    info.Environment["RE4_EXE"]=exe;
    using var direct=Process.Start(info)!;
    var err=direct.StandardError.ReadToEndAsync();
    string output=await direct.StandardOutput.ReadToEndAsync();await direct.WaitForExitAsync();
    Check(direct.ExitCode==0,"Reference native run failed: "+await err);
    using var reference=JsonDocument.Parse(output);
    using var client=new NativePreviewClient(python,script,exe);
    await client.LoadAsync(doc.LoadCommand());
    var hashes=new List<string>();
    var seen=new HashSet<uint>();
    int births=0;
    // Expected ranges from the EFX file, independent of the decoder's offsets and formula.
    (float Lo,float Hi) Range(CenterSpread v)=>(v.center-MathF.Abs(v.spread),v.center+MathF.Abs(v.spread));
    (float Lo,float Hi) Product((float Lo,float Hi) a,(float Lo,float Hi) b)
    {
        float[] c=[a.Lo*b.Lo,a.Lo*b.Hi,a.Hi*b.Lo,a.Hi*b.Hi];return (c.Min(),c.Max());
    }
    bool Within(float x,(float Lo,float Hi) r,float tolerance)=>x>=r.Lo-tolerance && x<=r.Hi+tolerance;
    var bb=doc.Billboard;
    var widthAtBirth=Product(Range(bb.SizeScalar),Range(bb.SizeX));
    var heightAtBirth=Product(Range(bb.SizeScalar),Range(bb.SizeY));
    var rollAtBirth=Range(bb.Rotation);
    for(int tick=1;tick<=180;tick++)
    {
        using var frame=await client.FrameAsync(tick);
        var native=reference.RootElement[tick-1].EnumerateArray().Where(p=>(p.GetProperty("flags").GetUInt32()&1)!=0).ToArray();
        Check(frame.Particles.Count==native.Length,"Particle count changed with actual UVS");
        var state=frame.EntryStates!.Single();
        Check(state.Blend==(doc.Billboard.Flags&15) && state.ColorRate==doc.Billboard.ColorRate,"Live type block differs from file constants");
        for(int i=0;i<native.Length;i++)
        {
            var n=native[i];var p=frame.Particles[i];
            var instance=BillboardInstance.Decode(p,state,doc.UvFlags);
            Check(p.Id==n.GetProperty("id").GetUInt32() && p.Age==n.GetProperty("age").GetSingle(),"Birth/age mismatch");
            var size=n.GetProperty("size").EnumerateArray().Select(x=>x.GetSingle()).ToArray();
            Check(instance.Size.X==size[0]*size[1]*FrameDecoder.F32(p.Raw.Span,0xe8) && instance.Size.Y==size[0]*size[2]*FrameDecoder.F32(p.Raw.Span,0xec),"Billboard geometry mismatch");
            Check(instance.PositionRoll.W==(float)(Half)n.GetProperty("rot").GetSingle(),"Roll differs from native writer convention");
            if(seen.Add(p.Id))
            {
                // Birth tick: the type sample is still unmodified by ScaleAnim / RotateAnim (spec §1.5),
                // and the identity emitter matrix has scale 1. Full width = SizeScalar x SizeX, full
                // height = SizeScalar x SizeY, roll = Rotation (radians, half precision).
                births++;
                Check(Within(instance.Size.X,widthAtBirth,1e-5f) && Within(instance.Size.Y,heightAtBirth,1e-5f),
                    $"Birth size {instance.Size.X}x{instance.Size.Y} outside SizeScalar*SizeX {widthAtBirth} / SizeScalar*SizeY {heightAtBirth}");
                Check(Within(instance.PositionRoll.W,rollAtBirth,1e-3f*Math.Max(1,Math.Abs(rollAtBirth.Hi))),
                    $"Birth roll {instance.PositionRoll.W} outside Rotation {rollAtBirth}");
                // Mirrored emitter (E+0x16c bit 7): the Billboard3D instance writer 3d18a60 negates the roll.
                var mirrored=BillboardInstance.Decode(p,state with{Flags16c=state.Flags16c|0x80},doc.UvFlags);
                Check(mirrored.PositionRoll.W==-instance.PositionRoll.W && mirrored.Size==instance.Size,"Mirror must negate only the roll");
            }
            int sequence=(int)(instance.Uv&255),uvFrame=(int)((instance.Uv>>8)&255);
            Check(sequence==n.GetProperty("uv_seq").GetInt32() && uvFrame==n.GetProperty("uv_frame").GetInt32(),"UV decode mismatch");
            Check(sequence<doc.Sequences.Count && uvFrame<doc.Sequences[sequence].Length,"Native UV frame exceeds actual sequence");
        }
        hashes.Add(Convert.ToHexString(SHA256.HashData(frame.Arrays[0].Span)));
    }
    using(var zero=await client.FrameAsync(0))Check(zero.Particles.Count==0,"Tick 0 not empty");
    for(int tick=1;tick<=180;tick++)
    {
        using var frame=await client.FrameAsync(tick);
        Check(Convert.ToHexString(SHA256.HashData(frame.Arrays[0].Span))==hashes[tick-1],"Real-UV replay differs");
    }
    Check(births>0,"No particle birth observed");
    results.Add(new{fixture,uv_frames=expectedCount,ticks=180,births,status="PASS"});
    Console.WriteLine($"PASS {fixture}: actual UVS, independent native baseline, birth geometry/roll against file ranges, mirror, UV bounds, replay");
}
// Test a consequential difference: a real one-frame UV sequence in play-once/kill mode.
{
    var loc=PreviewDocument.Fixture(game,"bb");var doc=PreviewDocument.Read(loc.Path,loc.Offset,resources);
    var uv=doc.Entry.Attributes.OfType<EFXAttributeUVSequence>().Single();
    uv.Flags=(uv.Flags&~3u)|2u;uv.PatternNo.min=0;uv.PatternNo.maxExclusive=1;
    string path=Path.GetFullPath("test-output/re4-preview/uv-once-entry.efx.3539837");
    using(var fs=File.Create(path)) doc.Entry.Write(new FileHandler(fs,path){FileVersion=3539837});
    var once=PreviewDocument.Read(path,0,resources);
    using var client=new NativePreviewClient(python,script,exe);await client.LoadAsync(once.LoadCommand());
    using var birth=await client.FrameAsync(7);
    Check(birth.Particles.Any(p=>p.Id==0),"Expected first particle at tick 7");
    using var next=await client.FrameAsync(8);
    Check((FrameDecoder.U32(next.Particles.Single(p=>p.Id==0).Raw.Span,4)&0x200)!=0,"One-frame UV play-once should request kill at tick 8");
    using var removed=await client.FrameAsync(9);
    Check(!removed.Particles.Any(p=>p.Id==0),"Pending kill must remove first particle at tick 9");
    var sample=birth.Particles.Single(p=>p.Id==0);
    var raw=sample.Raw.ToArray();
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x4c),0xc8112233);
    BitConverter.GetBytes(1f).CopyTo(raw,0x50);
    var test=sample with{LifeAlpha=128,Raw=raw};
    var state=birth.EntryStates!.Single() with{Flags174=0};
    Check(BillboardInstance.Decode(test,state,0).Color>>24==100,"Alpha must truncate to a byte");
    BitConverter.GetBytes(3f).CopyTo(raw,0x50);
    Check(BillboardInstance.Decode(test,state,0).Color>>24==45,"Alpha overflow must wrap as native byte output");
    BitConverter.GetBytes(1f).CopyTo(raw,0x50);
    var corrected=BillboardInstance.Decode(test,state with{Flags174=0x1000},(1u<<2)|(1u<<4));
    Check(corrected.Color>>24==200 && corrected.Uv>>16==3,"Mode 3 life bypass / fixed flips failed");
    Console.WriteLine("PASS real one-frame UV play-once kill timing");
}
File.WriteAllText("test-output/re4-preview/p1-results.json",JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));

}
catch(Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode=1; }
