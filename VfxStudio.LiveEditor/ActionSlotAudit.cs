using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections;
using ReeLib;
using ReeLib.Common;
using ReeLib.Mot;
using ReeLib.Motlist;
using ReeLib.Pfb;
namespace VfxStudio.LiveEditor;
internal static class ActionSlotAudit
{
    // Offline readback audit. Reads installed sources; never writes into the game.
    internal static int Run(Options options, string proofPath)
    {
        try
        {
            Directory.CreateDirectory(options.Output);
            var proof = JsonNode.Parse(File.ReadAllText(proofPath))!.AsObject();
            var session = new ActionSlotWorkspace.WorkingSet();
            var workspace = new ActionSlotWorkspace(options with { SelfTest = true }, proof, proof["auditPrefabIndex"]!.GetValue<int>(),session);
            var info = JsonSerializer.SerializeToNode(workspace.Info(), Host.Json)!;
            File.WriteAllText(Path.Combine(options.Output, "before.json"), info.ToJsonString());
            int container = proof["auditContainer"]?.GetValue<int>() ?? 10;
            var template = info["templates"]!.AsArray().First(t => t!["container"]!.GetValue<int>() == container)!;
            JsonObject Request(long revision)=>new JsonObject { ["actionRevision"] = revision, ["templateKey"] = template["key"]!.GetValue<string>(),
                ["resourcePath"] = "Art/VFX/EffectEditor/Weapon/it02/11_it02_000.efx", ["groupName"] = "",
                ["jointName"] = "", ["startFrame"] = 3f, ["endFrame"] = 20f };
            workspace.Add(Request(0));
            var secondProof=(JsonObject)proof.DeepClone(); secondProof["motionId"]=310;
            var second=new ActionSlotWorkspace(options with { SelfTest=true },secondProof,proof["auditPrefabIndex"]!.GetValue<int>(),session);
            second.Add(Request(1));
            var firstMotion=workspace.Export()[0].Content;
            if (!firstMotion.AsSpan().SequenceEqual(second.Export()[0].Content)) throw new Exception("Shared MOTLIST edits diverged between actions.");
            try {workspace.Add(Request(0));throw new Exception("Stale revision accepted.");}
            catch(InvalidOperationException) { }
            if (!firstMotion.AsSpan().SequenceEqual(workspace.Export()[0].Content)) throw new Exception("Rejected edit changed MOTLIST.");
            workspace.Add(Request(2));
            var reopened=new ActionSlotWorkspace(options with { SelfTest=true },proof,proof["auditPrefabIndex"]!.GetValue<int>(),session);
            var reopenedInfo=JsonSerializer.SerializeToNode(reopened.Info(),Host.Json)!;
            if(reopenedInfo["added"]!.AsArray().Count!=2) throw new Exception("Switching actions lost slot edits.");
            var templates=reopenedInfo["templates"]!.AsArray();
            if(templates.Count!=info["templates"]!.AsArray().Count+3) throw new Exception("Shared PFB lost additions.");
            // Serialization/audit-only bank ID, never used against a game.
            proof["bankId"]=proof["bankId"]?.DeepClone() ?? JsonValue.Create(20);
            var preview=reopened.PreparePreview(3,proof);
            if(preview.Length!=2 || !preview[0].Content.AsSpan().SequenceEqual(workspace.Export()[0].Content))
                throw new Exception("Preview lost accumulated motion edits.");
            var previewOptions=options with {GameRoot=Path.Combine(options.Output,"isolated-preview-game")};
            var first=ActionPreviewPublisher.Publish(previewOptions,3,proof,preview);
            var secondPreview=ActionPreviewPublisher.Publish(previewOptions,3,proof,preview);
            if(first["id"]!.GetValue<string>()==secondPreview["id"]!.GetValue<string>())throw new Exception("Preview reused a cached resource path.");
            foreach(var item in first["resources"]!.AsArray())
            {
                string logical=item!["path"]!.GetValue<string>();
                string native=Path.Combine(previewOptions.GameRoot!,"natives","stm",logical.Replace('/',Path.DirectorySeparatorChar)+
                    (item["kind"]!.GetValue<string>()=="motlist"?".992":".18"));
                if(!File.Exists(native))throw new Exception("Preview file missing.");
                string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(native)));
                if(hash!=item["sha256"]!.GetValue<string>())throw new Exception("Preview readback hash differs.");
            }
            try {reopened.PreparePreview(2,proof);throw new Exception("Stale preview revision accepted.");}catch(InvalidOperationException){}
            var missingProvider=(JsonObject)proof.DeepClone();missingProvider["prefabs"]=new JsonArray();
            try {reopened.PreparePreview(3,missingProvider);throw new Exception("Missing runtime PFB accepted.");}catch(InvalidOperationException){}
            File.WriteAllText(Path.Combine(options.Output,"preview-audit.json"),first.ToJsonString());
            if (preview[1].Provider?["expectedElements"]?.AsArray().Count!=3) throw new Exception("Preview did not include every new provider ID.");
            // Independently exercise character + weapon dependencies in the
            // same bank. These identities are audit fixtures, never live calls.
            var pairedProof=(JsonObject)proof.DeepClone();
            var pairedPrefabs=pairedProof["prefabs"]!.AsArray();
            bool firstIsRoot=pairedPrefabs[proof["auditPrefabIndex"]!.GetValue<int>()]!["externalIndex"]!.GetValue<int>()<0;
            string otherPath=firstIsRoot?"Art/VFX/EffectProvider/Weapon/it02/epvr_it02.pfb":"Art/VFX/EffectProvider/Player/pl_cm/epvr_hm_cm.pfb";
            int otherIndex=Enumerable.Range(0,pairedPrefabs.Count).FirstOrDefault(i=>pairedPrefabs[i]?["path"]?.GetValue<string>()==otherPath,-1);
            if(otherIndex<0) {
                otherIndex=pairedPrefabs.Count;
                pairedPrefabs.Add(new JsonObject{["proof"]="live_prefab_path",["path"]=otherPath,
                    ["dataContainerId"]=firstIsRoot?2:0,["externalIndex"]=firstIsRoot?0:-1,
                    ["prefabIdentity"]="offline-paired-prefab",["ownerIdentity"]="offline-manager"});
            }
            var character=new ActionSlotWorkspace(options with {SelfTest=true},pairedProof,otherIndex,session);
            var characterInfo=JsonSerializer.SerializeToNode(character.Info(),Host.Json)!;
            var rootRequest=Request(3);rootRequest["templateKey"]=characterInfo["templates"]![0]!["key"]!.DeepClone();
            character.Add(rootRequest);
            var pairedPreview=workspace.PreparePreview(4,pairedProof);
            if(pairedPreview.Length!=3 || pairedPreview.Count(p=>p.Kind=="pfb")!=2)throw new Exception("A modified character or weapon PFB was omitted.");
            var outputs=workspace.Export();
            if(outputs.Length!=3 || outputs.Select(o=>o.Key).Distinct().Count()!=3)throw new Exception("Paired export cannot select separate destinations for each required file.");
            var paired=ActionPreviewPublisher.Publish(previewOptions,4,pairedProof,pairedPreview);
            File.WriteAllText(Path.Combine(options.Output,"paired-preview-audit.json"),paired.ToJsonString());
            foreach (var item in workspace.Export()) File.WriteAllBytes(Path.Combine(options.Output, item.Name), item.Content);
            File.WriteAllText(Path.Combine(options.Output, "after.json"), JsonSerializer.Serialize(workspace.Info(), Host.Json));
            string firstSlot=JsonSerializer.SerializeToNode(workspace.Info(),Host.Json)!["added"]![0]!["id"]!.GetValue<string>();
            workspace.Remove(4,firstSlot);
            if(session.Edits.Count!=3 || session.Edits.Any(e=>e.Id==firstSlot))throw new Exception("Deleting a slot lost survivors or retained the deleted ID.");
            var retainedPreview=workspace.PreparePreview(5,pairedProof);
            if(retainedPreview.Length!=3 || JsonSerializer.SerializeToNode(second.Info(),Host.Json)!["added"]!.AsArray().Count!=1)
                throw new Exception("Deleting one action's slot changed another action or dropped a PFB dependency.");
            var afterDelete=session.Files.ToDictionary(p=>p.Key,p=>p.Value.ToArray());
            try {workspace.Remove(4,firstSlot);throw new Exception("Stale deletion accepted.");}catch(InvalidOperationException){}
            if(afterDelete.Any(p=>!p.Value.AsSpan().SequenceEqual(session.Files[p.Key])))throw new Exception("Rejected deletion modified bytes.");
            workspace.Reset(5);
            if(session.Edits.Count!=0 || session.PreviewProviders.Count!=0 || session.PreviewElements.Count!=0 ||
                session.Additions.Any(p=>p.Value.Count!=0) || session.Baselines.Any(p=>!p.Value.AsSpan().SequenceEqual(session.Files[p.Key])))
                throw new Exception("Reset did not restore every original byte or retained added metadata.");
            File.WriteAllText(Path.Combine(options.Output,"slot-deletion-audit.json"),JsonSerializer.Serialize(new {
                deletedSlot=firstSlot,preservedOtherActions=true,preservedOtherPrefabs=true,staleDeleteRejected=true,
                resetExactOriginalBytes=true,remainingSlots=session.Edits.Count,revision=session.Revision},Host.Json));
            // Deliberately non-native sentinel proves the writer uses the
            // supplied named enum value. This audit never loads it in-game.
            var lifetimeProof=(JsonObject)proof.DeepClone();
            lifetimeProof["effectEndTypes"]=new JsonObject{["Kill"]=proof["killEndType"]!.DeepClone(),["Invalid"]=-12345};
            var lifetimeSet=new ActionSlotWorkspace.WorkingSet();
            var independent=new ActionSlotWorkspace(options with {SelfTest=true},lifetimeProof,proof["auditPrefabIndex"]!.GetValue<int>(),lifetimeSet);
            var independentRequest=Request(0);independentRequest["lifetimeMode"]="independent";
            foreach(float invalid in new[]{0f,-1f,float.NaN,float.PositiveInfinity})
            {
                independentRequest["lifetimeFrames"]=invalid;
                try {independent.Add(independentRequest);throw new Exception("Invalid lifetime accepted.");}catch(ArgumentException){}
                if(lifetimeSet.Revision!=0 || lifetimeSet.Edits.Count!=0)throw new Exception("Invalid lifetime changed the workspace.");
            }
            independentRequest["lifetimeFrames"]=120f;independent.Add(independentRequest);
            var lifeEdit=lifetimeSet.Edits.Single();
            var lifeTrack=lifeEdit.Clip.ClipEntry.Tracks.Single(t=>t.Name=="via.effect.script.VFXRangeTrack");
            if(Convert.ToSingle(lifeEdit.Element.GetFieldValue("LifeFrame"))!=120f ||
                Convert.ToInt32(((ReeLib.Clip.NoHermiteKey)lifeTrack.Properties.Single(p=>p.Info.FunctionName=="EndType").Keys![0]).Value)!=-12345 ||
                ((ReeLib.Clip.BoolKey)lifeTrack.Properties.Single(p=>p.Info.FunctionName=="IsRequestEndOfMotion").Keys![0]).value!=0)
                throw new Exception("Independent duration remained coupled to the motion window.");
            independent.Add(Request(1));
            var added=JsonSerializer.SerializeToNode(independent.Info(),Host.Json)!["added"]!.AsArray();
            independent.Remove(2,added[1]!["id"]!.GetValue<string>());
            if(lifetimeSet.Edits.Count!=1 || lifetimeSet.Edits[0].Id!=lifeEdit.Id)throw new Exception("Rebuild lost independent lifetime metadata.");
            independent.Reset(3);
            if(lifetimeSet.Baselines.Any(p=>!p.Value.AsSpan().SequenceEqual(lifetimeSet.Files[p.Key])))throw new Exception("Independent reset retained extra bytes.");
            File.WriteAllText(Path.Combine(options.Output,"slot-lifetime-audit.json"),JsonSerializer.Serialize(new {
                independentDuration=120,triggerStart=3,triggerEnd=20,enumFixtureNotForGame=-12345,
                invalidDurationsRejected=true,serializedReadbackPassed=true,deleteSurvivorPreserved=true,resetExactOriginalBytes=true},Host.Json));
            AuditAttachments(options,proof,Request);
            Console.WriteLine("Action/PFB audit passed: " + options.Output); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void AuditAttachments(Options options,JsonObject original,Func<long,JsonObject> request)
    {
        // Serialization fixtures use the current-game observed weapon child ID
        // 1030 and named relation values. The MOTLIST/PFB still come from the
        // supplied source proof; these synthetic identities are never applied.
        var proof=(JsonObject)original.DeepClone();
        proof["attachmentRelationTypes"]=new JsonObject{["FollowParent"]=0,["InitializationParent"]=1};
        proof["attachmentTargets"]=new JsonArray(
            new JsonObject{["key"]="audit-character",["kind"]="character",["relationChildId"]=-1,
                ["parentDegree"]=0,["gameObjectIdentity"]="offline-character",["proof"]="live_relation_child",
                ["joints"]=new JsonArray("L_Foot","R_Foot")},
            new JsonObject{["key"]="audit-weapon",["kind"]="weapon",["relationChildId"]=1030,
                ["parentDegree"]=0,["gameObjectIdentity"]="offline-weapon",["proof"]="live_relation_child",
                ["joints"]=new JsonArray("VFX_Attack","blade")});
        var set=new ActionSlotWorkspace.WorkingSet();
        var workspace=new ActionSlotWorkspace(options with {SelfTest=true},proof,proof["auditPrefabIndex"]!.GetValue<int>(),set);
        var checks=new List<object>();
        string schema=Path.Combine(options.Assets,"Lists","rsz_pfb_classes.json");
        if(!File.Exists(schema))schema=Path.Combine(AppContext.BaseDirectory,"formats","rsz_pfb_classes.json");
        var rsz=new RszFileOption(GameName.mhwilds,RszParser.GetInstance(schema));
        IEnumerable<PfbGameObject> Walk(IEnumerable<PfbGameObject> roots)
        {foreach(var go in roots){yield return go;foreach(var child in Walk(go.Children))yield return child;}}
        void VerifyReadback(ActionSlotWorkspace.SlotEdit edit,int child,string joint,int relation)
        {
            var outputs=workspace.Export();
            var pfbOutput=outputs.Single(p=>p.Key=="pfb");
            using var pfb=new PfbFile(rsz,new FileHandler(new MemoryStream(pfbOutput.Content),pfbOutput.Name));
            if(!pfb.Read())throw new Exception("Attachment PFB cannot be reopened.");
            pfb.SetupGameObjects();
            var provider=Walk(pfb.GameObjects).Single(g=>g.GetHierarchyPath()==edit.ProviderPath)
                .Components.Single(c=>c.RszClass.name=="via.effect.script.EPVStandardData");
            var element=((IList)provider.GetFieldValue("Elements")!).Cast<RszInstance>()
                .Single(e=>Convert.ToInt32(e.GetFieldValue("ID"))==edit.ElementId);
            if(Convert.ToInt32(element.GetFieldValue("RelationChildId"))!=child ||
                Convert.ToInt32(element.GetFieldValue("ParentDegree"))!=0 ||
                Convert.ToInt32(element.GetFieldValue("RelationType"))!=relation ||
                element.GetFieldValue("JointName")?.ToString()!=joint ||
                Convert.ToBoolean(element.GetFieldValue("IsLanding")) || Convert.ToBoolean(element.GetFieldValue("IsUseTerrainNormal")))
                throw new Exception("Serialized attachment identity, mode, joint or landing override differs.");
            var motionOutput=outputs.Single(p=>p.Key=="motion");
            using var motion=new MotlistFile(new FileHandler(new MemoryStream(motionOutput.Content),motionOutput.Name));
            if(!motion.Read())throw new Exception("Attachment MOTLIST cannot be reopened.");
            var track=motion.Motions.Single(m=>m.motNumber==edit.MotionId).OverrideClips.Last().ClipEntry.Tracks
                .Single(t=>t.Name=="via.effect.script.VFXRangeTrack");
            int container=Convert.ToInt32(((ReeLib.Clip.NoHermiteKey)track.Properties.Single(p=>p.Info.FunctionName=="DataContainerId").Keys![0]).Value);
            if(container!=proof["prefabs"]![proof["auditPrefabIndex"]!.GetValue<int>()]!["dataContainerId"]!.GetValue<int>())
                throw new Exception("RelationChildId leaked into the motion provider selector.");
            checks.Add(new {kind="serializedReadback",target=child==-1?"character":"weapon",relationChildId=child,joint,
                relationType=relation,parentDegree=0,dataContainerId=container,landingDisabled=true});
        }
        void RejectUnchanged(string name,JsonObject invalid)
        {
            var bytes=set.Files.ToDictionary(p=>p.Key,p=>p.Value.ToArray());
            long revision=set.Revision;int edits=set.Edits.Count;
            string additions=JsonSerializer.Serialize(set.Additions,Host.Json);
            bool rejected=false;
            try {workspace.Add(invalid);}catch(Exception ex)when(ex is ArgumentException or InvalidOperationException or InvalidDataException){rejected=true;}
            if(!rejected)throw new Exception("Invalid attachment accepted: "+name);
            if(set.Revision!=revision || set.Edits.Count!=edits || set.Files.Count!=bytes.Count ||
                bytes.Any(p=>!p.Value.AsSpan().SequenceEqual(set.Files[p.Key])) || additions!=JsonSerializer.Serialize(set.Additions,Host.Json))
                throw new Exception("Rejected attachment changed file bytes or revision: "+name);
            checks.Add(new {kind="rejectedWithoutMutation",name});
        }
        foreach(var spec in new[]{("audit-character",-1,"L_Foot","initial",1),("audit-character",-1,"R_Foot","follow",0),
            ("audit-weapon",1030,"VFX_Attack","initial",1),("audit-weapon",1030,"blade","follow",0),("audit-weapon",1030,"","follow",0)})
        {
            var next=request(set.Revision);next["attachmentTargetKey"]=spec.Item1;next["jointName"]=spec.Item3;next["attachmentMode"]=spec.Item4;
            workspace.Add(next);VerifyReadback(set.Edits.Last(),spec.Item2,spec.Item3,spec.Item5);
        }
        var missing=request(set.Revision);missing["attachmentTargetKey"]="stale-or-unknown-object";
        RejectUnchanged("unknown target",missing);
        var wrongCharacter=request(set.Revision);wrongCharacter["attachmentTargetKey"]="audit-character";wrongCharacter["jointName"]="VFX_Attack";
        RejectUnchanged("weapon joint on character",wrongCharacter);
        var wrongWeapon=request(set.Revision);wrongWeapon["attachmentTargetKey"]="audit-weapon";wrongWeapon["jointName"]="L_Foot";
        RejectUnchanged("character joint on weapon",wrongWeapon);
        var unknownJoint=request(set.Revision);unknownJoint["attachmentTargetKey"]="audit-weapon";unknownJoint["jointName"]="nonexistent_joint";
        RejectUnchanged("unknown joint",unknownJoint);
        var unknownMode=request(set.Revision);unknownMode["attachmentTargetKey"]="audit-weapon";unknownMode["attachmentMode"]="unknown";
        RejectUnchanged("unknown relation mode",unknownMode);
        // Removing an earlier entry must replay later cloned metadata without
        // falling back to the template's RelationChildId or relation mode.
        workspace.Remove(set.Revision,set.Edits[0].Id);
        VerifyReadback(set.Edits.Last(),1030,"",0);
        workspace.Reset(set.Revision);
        if(set.Baselines.Any(p=>!p.Value.AsSpan().SequenceEqual(set.Files[p.Key])) || set.Edits.Count!=0)
            throw new Exception("Attachment reset retained added PFB or motion bytes.");
        File.WriteAllText(Path.Combine(options.Output,"slot-attachment-audit.json"),JsonSerializer.Serialize(new {
            fixtureOnly=true,liveResourceChanges=false,sourceMotion=proof["bankPath"]?.GetValue<string>(),
            serializedChecks=checks,deleteSurvivorPreserved=true,resetExactOriginalBytes=true},Host.Json));
    }
}
