using System.Text.Json;
using VfxStudio.Re4Preview;
using VfxStudio.Re4Render;

try
{
    void Check(bool ok,string message) {if(!ok)throw new Exception(message);}
    string root=Path.Combine(Directory.GetCurrentDirectory(),"test-output","re4-latency-tests-"+Guid.NewGuid().ToString("N"));
    var recorder=new PreviewLatencyRecorder(root);
    recorder.Configure(new{test="publication and reply ordering"});
    var target=new OfflinePreviewManifest(1,"doc",2,"unused","hash",0,0,0,"unused",true);
    PreviewEditorState State(OfflinePreviewManifest m)=>new(m.Revision,true,true,false,m,null,null,null,null);
    int waits=0;
    void Draw(OfflinePreviewManifest m)=>recorder.Rendered(m.Key,45,false,()=>waits++);
    void Prepare(OfflinePreviewManifest m)
    {recorder.LoadStarted(m.Key);recorder.Loaded(m.Key,30);recorder.FrameReady(m.Key);}

    // An unrelated publication or an old frame cannot end this edit's measurement.
    recorder.Begin("external_watch","set_fields",45,false,expectedDocument:"doc",expectedRevision:2);
    var unrelated=target with{DocumentId="other"};
    recorder.Published(unrelated);Prepare(unrelated);Draw(unrelated);
    var later=target with{Revision=3};
    recorder.Published(later);Prepare(later);Draw(later);
    Check(waits==0 && recorder.Count==0,"Unrelated revision was recorded as the edited frame");

    // First matching GPU frame wins, even if its mailbox reply arrives later.
    recorder.Published(target);Prepare(target);Draw(target);Draw(target);
    Check(waits==1 && recorder.IsActive && recorder.Count==0,"GPU-before-reply lost its timestamp or repeated its GPU wait");
    Thread.Sleep(2);
    recorder.Responded(State(target));
    Check(recorder.Count==1 && !recorder.IsActive && recorder.LastOutcome=="completed","Late reply did not complete the measured revision");
    using(var row=JsonDocument.Parse(File.ReadLines(Path.Combine(root,"latency.jsonl")).Last()))
    {
        var s=row.RootElement;
        Check(s.GetProperty("gpuCompleteMs").GetDouble()<s.GetProperty("responseMs").GetDouble(),"Reply time overwrote the earlier GPU completion");
        Check(s.GetProperty("manifestObservedMs").GetDouble()<=s.GetProperty("loadStartedMs").GetDouble(),"Load preceded its matched publication");
    }

    // Normal window edits still support reply-before-load, with no expected publication.
    recorder.Begin("window","undo",45,false);
    recorder.Responded(State(target));Prepare(target);Draw(unrelated);Draw(target);
    Check(waits==2 && recorder.Count==2 && !recorder.IsActive,"Reply-before-GPU path did not retain revision matching");

    recorder.Begin("external_watch","set_fields",45,false,expectedDocument:"doc",expectedRevision:2);
    recorder.Published(target);Prepare(target);Draw(target);recorder.Responded(State(later));
    Check(recorder.LastOutcome=="response_revision_mismatch","Conflicting reply was counted as success");
    recorder.Begin("window","redo",45,false);
    recorder.Responded(State(target) with{PublicationError="test publication failure"});
    Check(recorder.LastOutcome=="publication_failed","Publication failure was counted as success");
    recorder.WriteSummary();
    Console.WriteLine("PASS unrelated/stale revision rejection, GPU-before-reply, single GPU wait, reply-before-GPU, conflicting reply and publication failure");
}
catch(Exception ex) {Console.Error.WriteLine(ex);Environment.ExitCode=1;}
