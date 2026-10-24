using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace VfxStudio.LiveEditor;

internal static class ActionPreviewPublisher
{
    internal static JsonObject Publish(Options options, long revision, JsonObject proof, ActionSlotWorkspace.PreviewFile[] files)
    {
        if(options.GameRoot==null)throw new InvalidOperationException("未配置游戏目录。");
        if(files.Count(f=>f.Kind=="motlist")!=1 || !files.Any(f=>f.Kind=="pfb") || files.Any(f=>f.Kind is not ("motlist" or "pfb")))
            throw new InvalidOperationException("预览必须包含一份动作表及其对应的全部 PFB。");
        string id=Guid.NewGuid().ToString("N");
        string logical="art/MotionVfxLive/ActionPreview/"+id;
        string gameRoot=Path.GetFullPath(options.GameRoot);
        string directory=Path.GetFullPath(Path.Combine(gameRoot,"natives","stm",logical.Replace('/',Path.DirectorySeparatorChar)));
        // These files are new private revisions, never replacements at the
        // original MOTLIST/PFB paths. No live objects are changed by publishing.
        for(var dir=new DirectoryInfo(directory);dir!=null;dir=dir.Parent)
        {
            if(dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("动作预览目录不能使用符号链接或目录联接。");
            if(dir.FullName.Equals(gameRoot,StringComparison.OrdinalIgnoreCase))break;
        }
        if(Directory.Exists(directory))throw new IOException("预览版本目录已存在。");
        Directory.CreateDirectory(directory);
        var resources=new JsonArray();
        for(int i=0;i<files.Length;i++)
        {
            var item=files[i];string path=logical+"/resource_"+i+"."+item.Kind;
            string native=Path.Combine(directory,"resource_"+i+"."+item.Kind+(item.Kind=="motlist"?".992":".18"));
            using(var stream=new FileStream(native,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {stream.Write(item.Content);stream.Flush(true);}
            string hash=Convert.ToHexString(SHA256.HashData(item.Content));
            if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(native)))!=hash)throw new IOException("动作预览文件回读校验失败。");
            resources.Add(new JsonObject{["kind"]=item.Kind,["originalPath"]=item.OriginalPath,["path"]=path,
                ["sha256"]=hash,["bytes"]=item.Content.LongLength,["provider"]=item.Provider?.DeepClone()});
        }
        var plan=new JsonObject{["schema"]=1,["id"]=id,["revision"]=revision,["source"]=proof.DeepClone(),["resources"]=resources};
        Host.AtomicJson(Path.Combine(directory,"preview.json"),plan);
        return plan;
    }
}
