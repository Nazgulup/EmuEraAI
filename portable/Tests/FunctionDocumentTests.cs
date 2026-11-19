using MinorShift.Emuera.AI;
using MinorShift.Emuera.Input;

internal static class FunctionDocumentTests
{
    static int count;
    static void Check(bool yes,string text) { if(!yes) throw new Exception("FAIL: "+text); count++; Console.WriteLine("PASS: "+text); }
    static async Task Until(Func<bool> condition) { using var timer=new CancellationTokenSource(TimeSpan.FromSeconds(30)); while(!condition()) await Task.Delay(15,timer.Token); }
    sealed class Client : IAiClient
    {
        public int Calls;
        public Task<string> GenerateAsync(AiSettings settings,Dictionary<string,object> request,CancellationToken token) { Calls++; return Task.FromResult("旅人合起地图，等待下一步安排。"); }
    }
    public static async Task Run(string root)
    {
        string output=Path.Combine(root,"logs","function-document"), run=Path.Combine(output,"run-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        string source="@KOJO_DAILY\nPRINTL Calm dialogue.\nRETURN\n@KOJO_MISC\nPRINTL Unclassified dialogue.\nRETURN\n@STORY_GATE\nPRINTL Fixed plot.\nRETURN\n"+string.Join("\n",Enumerable.Range(0,65).Select(i=>"@HELPER_"+i+"\nRETURN\n"));
        string rules=Path.Combine(run,"rules.json"), file=Path.Combine(run,"document.json");
        var policy=new StoryPolicy(()=>["ERB/local.erb"],_=>source,rules);
        using var doc=new StoryFunctionDocument(policy,file,()=>{}); await doc.Start();
        Check(doc.State=="ready" && File.Exists(file),"function document is created locally without executing script or calling AI");
        var page=doc.Page("",0,""); Check(Json.Int(page,"count")==68 && Json.Array(page["rows"]).Length==30 && Json.Int(doc.Page("",99,""),"page")==2,"large function documents use bounded 30-entry pages");
        var daily=Json.Object(Json.Array(doc.Page("KOJO_DAILY",0,"")["rows"]).Single());
        Check(Json.Text(daily,"classification")=="routine" && Json.Text(daily,"action")=="auto","clear daily functions default to automatic AI eligibility");
        var unknown=Json.Object(Json.Array(doc.Page("KOJO_MISC",0,"unknown")["rows"]).Single());
        Check(Json.Text(unknown,"action")=="auto" && !policy.Evaluate(["KOJO_MISC|ERB/local.erb|5|leaf"]).Allowed,"unknown functions are editable but not silently allowed");
        string key=Json.Text(unknown,"key");
        doc.Update([new Dictionary<string,object>{{"key",key},{"action","allow"},{"note","玩家确认可改写"}}]);
        Check(policy.Evaluate(["KOJO_MISC|ERB/local.erb|5|leaf"]).Allowed && doc.Export().Contains("玩家确认可改写"),"edited selection and user note persist in an importable document");
        var loadedPolicy=new StoryPolicy(()=>["ERB/local.erb"],_=>source,rules);
        using(var loaded=new StoryFunctionDocument(loadedPolicy,file,()=>{})) { await loaded.Start(); var row=Json.Object(Json.Array(loaded.Page("KOJO_MISC",0,"")["rows"]).Single()); Check(Json.Text(row,"action")=="allow" && Json.Text(row,"note")=="玩家确认可改写","reopening the game keeps selections and editable notes"); }
        bool invalid=false; try { doc.Update([new Dictionary<string,object>{{"key","fake"},{"action","allow"}}]); } catch(FormatException) { invalid=true; }
        Check(invalid,"unlisted function keys cannot be authorized through document edits");
        source=source.Replace("Unclassified dialogue.","Changed dialogue.");
        var changedPolicy=new StoryPolicy(()=>["ERB/local.erb"],_=>source,rules);
        using(var changed=new StoryFunctionDocument(changedPolicy,Path.Combine(run,"changed.json"),()=>{})) { await changed.Start(); Check(!changedPolicy.Evaluate(["KOJO_MISC|ERB/local.erb|5|leaf"]).Allowed,"source updates do not reuse old function permissions"); }
        Check(!doc.Export().Contains("Calm dialogue.") && !doc.Export().Contains("Fixed plot."),"export includes function metadata and choices rather than game dialogue body");
        using(var broken=new StoryFunctionDocument(new StoryPolicy(()=>["missing.erb"],_=>throw new IOException()),Path.Combine(run,"broken.json"),()=>{})) { await broken.Start(); Check(broken.State=="ready" && broken.Reason.Contains("读取失败") && Json.Int(broken.Page("",0,""),"count")==0,"unreadable scripts are reported without inventing function permissions"); }

        string game=Path.Combine(root,"portable","Tests","function-document-game"); Directory.CreateDirectory(Path.Combine(game,"CSV")); Directory.CreateDirectory(Path.Combine(game,"ERB"));
        foreach(var path in Directory.GetFiles(Path.Combine(root,"demo","CSV"))) File.Copy(path,Path.Combine(game,"CSV",Path.GetFileName(path)),true);
        File.WriteAllText(Path.Combine(game,"ERB","DOCUMENT.ERB"),"@SYSTEM_TITLE\nADDCHARA 0\nADDCHARA 1\nMASTER=0\nTARGET=1\n$MENU\nPRINTL [1] 等待\nINPUT\nCALL KOJO_MISC\nGOTO MENU\n@KOJO_MISC\nPRINTL 旅人在路口等待。\nRETURN\n");
        EraCore.Maui.FileSystem.AppDataDirectory=Path.Combine(run,"host");
        EraCore.Maui.SecureStorage.Default.Read=Task.FromResult<string?>(Json.Codec().Serialize(new AiSettings {Enabled=true,AutoGenerate=true,UseGameStyle=false}.ToDictionary(true)));
        var client=new Client(); var host=new EraCore.Maui.BridgeHost(game,client);
        try
        {
            host.Start(); await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(60)); await Until(()=>host.Document.State=="ready");
            Check(host.Controller.StoryTemplate.State=="unmatched" && Json.Int(host.Document.Page("",0,""),"count")==2,"unmatched game automatically gets a function document before opening the editor");
            var initial=Json.Codec().DeserializeObject(System.Text.Json.JsonSerializer.Serialize(host.Session!.Output.Snapshot));
            host.Session.Input.Submit(new InputFrame(Value:"1")); await Until(()=>host.Controller.Current!=null && host.Controller.StoryReady);
            Check(client.Calls==0 && !host.Controller.CanGenerate,"unselected uncertain dialogue remains original");
            await host.Command(new Dictionary<string,object>{{"type","aiStoryDocument"},{"query","KOJO_MISC"},{"page",0},{"kind",""}});
            var row=Json.Object(Json.Array(host.Document.Page("KOJO_MISC",0,"")["rows"]).Single()); string functionKey=Json.Text(row,"key");
            Dictionary<string,object> Edit(string id,string action) => new(){{"type","aiStoryDocumentSave"},{"documentId",id},{"edits",new object[]{new Dictionary<string,object>{{"key",functionKey},{"action",action},{"note","用户选择"}}}}};
            await host.Command(Edit("old-document","allow")); Check(client.Calls==0 && !host.Controller.CanGenerate,"stale editor from another game cannot apply a function selection");
            string before=System.Text.Json.JsonSerializer.Serialize(host.Session.Output.Snapshot);
            await host.Command(Edit(host.Document.Id,"allow")); await Until(()=>client.Calls==1 && !host.Controller.Busy);
            Check(host.Controller.DisplayMode=="ai" && System.Text.Json.JsonSerializer.Serialize(host.Session.Output.Snapshot)==before,"saving an allowed function automatically prioritizes AI without replaying the game action");
            await host.Command(Edit(host.Document.Id,"deny")); await host.Command("aiGenerate");
            Check(client.Calls==1 && host.Controller.DisplayMode=="original" && System.Text.Json.JsonSerializer.Serialize(host.Session.Output.Snapshot)==before,"unchecking a function immediately restores original and blocks regeneration without numeric changes");
            Check(host.Controller.Log.ReadRecent().Contains("story.document-edit") && host.Controller.Log.ReadRecent().Contains("story.document"),"document creation and selection decisions are logged");
            File.WriteAllText(Path.Combine(output,"generation.log"),host.Controller.Log.ReadRecent());
            File.WriteAllText(Path.Combine(output,"example-function-template.json"),host.Document.Export());
            File.WriteAllText(Path.Combine(output,"bridge-replay.json"),Json.Codec().Serialize(new Dictionary<string,object>{{"initial",initial},{"events",host.Js.Events.ToArray()}}));
        }
        finally { await host.Stop(); }
        Console.WriteLine("ALL FUNCTION DOCUMENT CHECKS PASSED: "+count);
    }
}
