using System.Text.Json.Nodes;
namespace VfxStudio.LiveEditor;
internal static class SessionDocumentTests
{
    internal static void Run(Options options, string output, Action<bool,string> check)
    {
        string root = Path.Combine(output,"session-documents"); Directory.CreateDirectory(root);
        string original = Directory.EnumerateFiles(options.Assets,"*.efx.5571972",SearchOption.AllDirectories).First();
        string a=Path.Combine(root,"a.efx.5571972"), b=Path.Combine(root,"b.efx.5571972");
        File.Copy(original,a); File.Copy(original,b);
        byte[] disk=File.ReadAllBytes(a);
        using var engine=new EditorEngine(options with {Mailbox=Path.Combine(root,"mailbox"),Output=root});
        long revision=0;int seq=0;
        JsonObject Call(string action,JsonObject? data=null)
        {
            data??=new();data["id"]="session-"+ ++seq;data["action"]=action;data["revision"]=revision;
            var r=engine.Handle(data);check(r["ok"]!.GetValue<bool>(),action+": "+r["error"]);
            revision=r["revision"]!.GetValue<long>();return r;
        }
        var first=Call("open",new(){["path"]=a});
        int count=first["document"]!["entries"]!.AsArray().Count;
        check(count>1,"Restore test contains multiple real blocks");
        string firstName=first["document"]!["entries"]![0]!["name"]!.GetValue<string>();
        JsonObject Target(int i)=>new(){["kind"]="entry",["index"]=i};
        Call("set",new(){["target"]=Target(0),["path"]="name",["value"]="changed-first"});
        Call("set",new(){["target"]=Target(1),["path"]="name",["value"]="changed-second"});
        Call("open",new(){["path"]=b});Call("add_entry",new(){["name"]="B-only"});
        var reopened=Call("open",new(){["path"]=a});
        check(reopened["document"]!["entries"]![0]!["name"]!.GetValue<string>()=="changed-first","Switching files retains committed edits");
        var restored=Call("restore_entry",new(){["index"]=0});
        check(restored["document"]!["entries"]![0]!["name"]!.GetValue<string>()==firstName &&
            restored["document"]!["entries"]![1]!["name"]!.GetValue<string>()=="changed-second","Single-block restoration preserves every other block");
        Call("undo");
        check(Call("status")["document"]!["entries"]![0]!["name"]!.GetValue<string>()=="changed-first","Block restoration is undoable");
        Call("save",new(){["path"]=Path.Combine(root,"export.efx.5571972")});
        restored=Call("restore_entry",new(){["index"]=0});
        check(restored["document"]!["entries"]![0]!["name"]!.GetValue<string>()==firstName,"Export never changes the original block baseline");
        Call("add_entry",new(){["name"]="temporary"});
        restored=Call("restore_entry",new(){["index"]=count});
        check(restored["document"]!["entries"]!.AsArray().Count==count,"Restoring a session-created block removes only that block");
        reopened=Call("open",new(){["path"]=b});
        check(reopened["document"]!["entries"]!.AsArray().Last()!["name"]!.GetValue<string>()=="B-only","Second file keeps its independent working state");
        check(disk.AsSpan().SequenceEqual(File.ReadAllBytes(a)),"Session edits never write the imported file");
    }
}
