using System.Diagnostics;
using MinorShift.Emuera.AI;
using MinorShift.Emuera.Input;

internal static class StoryTests
{
    static int count;
    static void Check(bool yes,string text) { if(!yes) throw new Exception("FAIL: "+text); count++; Console.WriteLine("PASS: "+text); }
    static async Task Until(Func<bool> check) { var watch=Stopwatch.StartNew(); while(!check()) { if(watch.Elapsed.TotalSeconds>25) throw new TimeoutException("manual off condition"); await Task.Delay(15); } }
    sealed class Client : IAiClient
    {
        public int Calls;
        public Func<Task<string>> Reply=()=>Task.FromResult("旅人收起地图，继续等待安排。");
        public Task<string> GenerateAsync(AiSettings s,Dictionary<string,object> body,CancellationToken token) { Interlocked.Increment(ref Calls); return Reply(); }
    }
    static string Site(string name,int line=2,string kind="leaf",string file="ERB/test.erb") => name+"|"+file+"|"+line+"|"+kind;
    public static async Task Run(string root)
    {
        Check(new AiSettings().Enabled && new AiSettings().AutoGenerate && AiSettings.Parse("{}").Enabled,"new and missing settings enable automatic AI");
        Check(!AiSettings.Parse("{\"Enabled\":false}").Enabled,"explicit global AI off remains off on upgrade");
        var texts=new Dictionary<string,string> { ["ERB/test.erb"]="@EVENTTRAIN\nCALL KOJO_DAILY\nRETURN\n@KOJO_DAILY\nPRINTL A calm response.\nRETURN\n@STORY_EVENT\nCALL KOJO_DAILY\nRETURN\n@KOJO_MISC\nPRINTL Unknown context.\nRETURN\n@KOJO_DAILY_FLAG\nIF CFLAG:0\nPRINTL Conditional response.\nENDIF\nRETURN" };
        bool templateRead=false;
        var policy=new StoryPolicy(()=>texts.Keys,p=>texts[p],null,()=>{templateRead=true; throw new IOException("removed");});
        policy.PrepareTemplates();
        Check(policy.Evaluate([Site("EVENTTRAIN",2,"parent"),Site("KOJO_DAILY",5)]).Allowed && !templateRead,"templates are never opened, daily dialogue defaults on");
        Check(policy.Evaluate([Site("STORY_EVENT",8,"parent"),Site("KOJO_DAILY",5)]).Allowed,"story-named parents no longer auto-block");
        Check(policy.Evaluate([Site("KOJO_MISC",11)]).Allowed,"unclassified dialogue defaults on");
        Check(policy.Evaluate([Site("KOJO_DAILY_FLAG",15)]).Allowed,"event-flag branches no longer auto-block");
        Check(policy.Evaluate([]).Allowed && policy.Evaluate(["overflow"]).Allowed,"missing or truncated source context defaults on");
        var unknown=policy.Evaluate([Site("KOJO_MISC",11)]); policy.SetRule(unknown.RuleKey,"deny");
        Check(!policy.Evaluate([Site("KOJO_MISC",11)]).Allowed,"manual function off blocks dialogue");
        string exported=policy.Export(); var restored=new StoryPolicy(()=>texts.Keys,p=>texts[p]); restored.Import(exported);
        Check(!restored.Evaluate([Site("KOJO_MISC",11)]).Allowed,"manual off survives export and reload");
        texts["ERB/test.erb"]=texts["ERB/test.erb"].Replace("Unknown context.","Changed source.");
        var changed=new StoryPolicy(()=>texts.Keys,p=>texts[p]); changed.Import(exported); var updated=changed.Evaluate([Site("KOJO_MISC",11)]);
        Check(!updated.Allowed && changed.RuleAction(updated.RuleKey)=="deny","manual off stays attached to the same function after a source update");
        changed.SetRule(updated.RuleKey,"auto"); Check(changed.Evaluate([Site("KOJO_MISC",11)]).Allowed,"restore default removes old fingerprint disables");
        var parent=policy.Evaluate([Site("STORY_EVENT",8)]); policy.SetRule(parent.RuleKey,"deny");
        Check(!policy.Evaluate([Site("STORY_EVENT",8,"parent"),Site("KOJO_DAILY",5)]).Allowed,"manually disabling a story function also blocks its nested dialogue");
        Check(!policy.Evaluate(["overflow",Site("STORY_EVENT",8,"parent"),Site("KOJO_DAILY",5)]).Allowed,"an incomplete site cannot hide a later explicit disable");
        var broken=new StoryPolicy(()=>new[]{"ERB/test.erb"},_=>throw new IOException("unreadable")); broken.Import(policy.Export());
        Check(!broken.Evaluate([Site("STORY_EVENT",8)]).Allowed,"known manual disable is honored even if the source cannot be read");
        Check(broken.Evaluate([Site("KOJO_DAILY",5)]).Allowed,"source read failure without a manual disable does not block default AI");
        using(var readGate=new ManualResetEventSlim(false))
        {
            var client=new Client(); using var cancelled=new AiController(new AiSettings { UseGameStyle=false },client);
            cancelled.ConfigureStoryPolicy(new StoryPolicy(()=>texts.Keys,p=>{readGate.Wait(); return texts[p];}));
            cancelled.SetEvent(new DialogueSnapshot("cancel-check","旅人","玩家","","原文","KOJO_DAILY",null,[Site("KOJO_DAILY",5)]));
            var pending=cancelled.GenerateAsync(true); cancelled.RestoreOriginal(); readGate.Set(); await pending; await Until(()=>cancelled.StoryReady);
            Check(client.Calls==0 && cancelled.CanGenerate && cancelled.Text=="原文","cancel during source lookup prevents a delayed request");
        }
        string game=Path.Combine(root,"portable","Tests","story-game"); Directory.CreateDirectory(Path.Combine(game,"CSV")); Directory.CreateDirectory(Path.Combine(game,"ERB"));
        foreach(var file in Directory.GetFiles(Path.Combine(root,"demo","CSV"))) File.Copy(file,Path.Combine(game,"CSV",Path.GetFileName(file)),true);
        File.WriteAllText(Path.Combine(game,"ERB","DIALOGUE_CASES.ERB"),"""
@SYSTEM_TITLE
ADDCHARA 0
ADDCHARA 1
MASTER = 0
TARGET = 1
$MENU
PRINTL [1] 日常 [2] 故事 [3] 未知 [4] 混合 [5] 事件标记 [6] 空函数
INPUT
SELECTCASE RESULT
CASE 1
CALL KOJO_DAILY
CASE 2
CALL STORY_EVENT
CASE 3
CALL KOJO_MISC
CASE 4
CALL PRITRAIN_MESSAGE
CASE 5
CALL GATED_HELPER
CASE 6
CALL KOJO_EMPTY
ENDSELECT
GOTO MENU
@KOJO_DAILY
PRINTL 旅人停在路口，等待安排。
RETURN
@STORY_EVENT
CALL KOJO_DAILY
RETURN
@KOJO_MISC
PRINTL 旅人望向远处的路口。
RETURN
@PRITRAIN_MESSAGE
CALL KOJO_DAILY
CALL STORY_EVENT
RETURN
@KOJO_EMPTY
RETURN
@GATED_HELPER
CFLAG:TARGET:10 = 1
IF CFLAG:TARGET:10
CALL KOJO_DAILY
ENDIF
RETURN
""");
        string output=Path.Combine(root,"logs","manual-off-host"); Directory.CreateDirectory(output);
        EraCore.Maui.FileSystem.AppDataDirectory=Path.Combine(output,"run-"+Guid.NewGuid().ToString("N"));
        EraCore.Maui.FileSystem.PackageDirectory=Path.Combine(output,"no-template-assets");
        var settings=new AiSettings { GenerateWithoutOriginal=true,UseGameStyle=false,Model="controlled-manual-off" };
        EraCore.Maui.SecureStorage.Default.Read=Task.FromResult<string?>(Json.Codec().Serialize(settings.ToDictionary(true)));
        var fake=new Client(); var host=new EraCore.Maui.BridgeHost(game,fake);
        async Task Input(string value) { int turns=host.Js.Turns.Count; host.Session!.Input.Submit(new InputFrame(Value:value)); await Until(()=>host.Js.Turns.Count>turns && host.Controller.StoryReady && !host.Controller.Busy); await Task.Delay(80); }
        try
        {
            host.Start(); await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(60)); await Until(()=>host.Controller.StoryReady && !host.Controller.Busy && !host.Js.Turns.IsEmpty); await Task.Delay(80);
            var initial=Json.Codec().DeserializeObject(System.Text.Json.JsonSerializer.Serialize(host.Session!.Output.Snapshot));
            Check(host.Controller.CanGenerate && fake.Calls>0,"actual host defaults to automatic AI including eligible menu supplements, with no packaged template");
            int calls=fake.Calls; await Input("1"); Check(fake.Calls==calls+1 && host.Controller.DisplayMode=="ai","actual daily dialogue auto-generates once");
            calls=fake.Calls; await Input("2"); Check(fake.Calls==calls+1 && host.Controller.DisplayMode=="ai","actual nested story dialogue auto-generates by default");
            string before=System.Text.Json.JsonSerializer.Serialize(host.Session.Output.Snapshot); calls=fake.Calls;
            await host.Controller.SetStoryRuleAsync("deny","event"); await host.Command("aiGenerate");
            Check(fake.Calls==calls && !host.Controller.CanGenerate && host.Controller.Text==host.Controller.Current!.Original,"manual event off restores original and blocks direct regenerate");
            await host.Command(new Dictionary<string,object>{{"type","aiStoryRule"},{"scope","event"},{"action","allow"},{"eventId",host.Controller.Current!.EventId},{"storyTurn",host.Controller.Current.StoryTurn-1}});
            Check(!host.Controller.CanGenerate && fake.Calls==calls && host.Controller.Log.ReadRecent().Contains("story.command-rejected"),"stale UI cannot re-enable a different event");
            await host.Controller.SetStoryRuleAsync("allow","event"); await host.Command("aiGenerate"); await host.Command("aiGenerate");
            Check(fake.Calls==calls+2 && System.Text.Json.JsonSerializer.Serialize(host.Session.Output.Snapshot)==before,"re-enabling and regenerating changes only presentation, not original engine output");
            await host.Controller.SetStoryRuleAsync("deny","event"); calls=fake.Calls; await Input("2");
            Check(fake.Calls==calls+1 && host.Controller.CanGenerate,"per-event off expires on the next input even when the dialogue repeats");
            calls=fake.Calls; await Input("3"); Check(fake.Calls==calls+1 && host.Controller.CanGenerate,"actual generic KOJO auto-generates without a whitelist");
            await host.Controller.SetStoryRuleAsync("deny","function"); calls=fake.Calls; await host.Controller.SetStoryRuleAsync("allow","event"); await host.Command("aiGenerate");
            Check(fake.Calls==calls && !host.Controller.CanGenerate,"saved function off takes precedence over a temporary event on");
            await Input("3"); Check(fake.Calls==calls && !host.Controller.CanGenerate,"function off persists on repeated visits");
            await host.Controller.SetStoryRuleAsync("auto","function"); await host.Command("aiGenerate"); Check(fake.Calls==calls+1 && host.Controller.CanGenerate,"restoring default re-enables the function");
            calls=fake.Calls; await Input("4"); Check(fake.Calls==calls+1,"mixed story/daily block is no longer auto-blocked");
            calls=fake.Calls; await Input("5"); Check(fake.Calls==calls+1,"actual CFLAG branch is no longer auto-blocked");
            before=System.Text.Json.JsonSerializer.Serialize(host.Session.Output.Snapshot);
            fake.Reply=()=>throw new HttpRequestException("controlled offline"); await host.Command("aiGenerate");
            Check(host.Controller.DisplayMode=="original" && host.Controller.Text==host.Controller.Current!.Original && System.Text.Json.JsonSerializer.Serialize(host.Session.Output.Snapshot)==before,"network failure restores original with unchanged engine output");
            var pending=new TaskCompletionSource<string>(); fake.Reply=()=>pending.Task; var work=host.Command("aiGenerate"); await Until(()=>host.Controller.Busy);
            await host.Controller.SetStoryRuleAsync("deny","event"); pending.SetResult("LATE RESPONSE"); await work;
            Check(host.Controller.DisplayMode=="original" && host.Controller.Text!= "LATE RESPONSE","manual off cancels in-flight AI and rejects late replies");
            fake.Reply=()=>Task.FromResult("旅人等待下一步安排。"); calls=fake.Calls; await Input("6");
            Check(fake.Calls==calls+1 && host.Controller.Current?.Original=="" && host.Controller.CanGenerate,"empty function with retained options can auto-supplement");
            await host.Controller.SetStoryRuleAsync("deny","function"); calls=fake.Calls; await host.Command("aiGenerate"); await Input("6");
            Check(fake.Calls==calls && !host.Controller.CanGenerate && host.Controller.Current?.Original=="","manual function off blocks supplements as well as ordinary dialogue");
            Check(host.Controller.Log.ReadRecent().Contains("default-allow") && host.Controller.Log.ReadRecent().Contains("manual-deny") && host.Controller.Log.ReadRecent().Contains("story.mode"),"logs distinguish default on, manual function off, and no-template mode");
            File.WriteAllText(Path.Combine(output,"generation.log"),host.Controller.Log.ReadRecent());
            File.WriteAllText(Path.Combine(output,"bridge-replay.json"),Json.Codec().Serialize(new Dictionary<string,object>{{"initial",initial},{"events",host.Js.Events.ToArray()}}));
        }
        finally { await host.Stop(); }
        Console.WriteLine("ALL STORY CHECKS PASSED: "+count);
    }
}
