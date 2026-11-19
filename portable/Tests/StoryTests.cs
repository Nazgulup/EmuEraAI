using System.Diagnostics;
using MinorShift.Emuera.AI;
using MinorShift.Emuera.Input;

internal static class StoryTests
{
    static int count;
    static void Check(bool yes,string text) { if(!yes) throw new Exception("FAIL: "+text); count++; Console.WriteLine("PASS: "+text); }
    static async Task Until(Func<bool> check) { var watch=Stopwatch.StartNew(); while(!check()) { if(watch.Elapsed.TotalSeconds>25) throw new TimeoutException("story condition"); await Task.Delay(15); } }
    sealed class Client : IAiClient
    {
        public int Calls;
        public Func<Task<string>> Reply=()=>Task.FromResult("旅人收起地图，继续等待安排。");
        public Task<string> GenerateAsync(AiSettings s,Dictionary<string,object> body,CancellationToken token) { Interlocked.Increment(ref Calls); return Reply(); }
    }
    static string Site(string name,int line=2,string kind="leaf",string file="ERB/test.erb") => name+"|"+file+"|"+line+"|"+kind;
    public static async Task Run(string root)
    {
        Check(new AiSettings().ProtectStory && AiSettings.Parse("{}").ProtectStory,"new and existing settings default to story protection");
        var texts=new Dictionary<string,string> { ["ERB/test.erb"]="@EVENTTRAIN\nCALL KOJO_DAILY\nRETURN\n@KOJO_DAILY\nPRINTL A calm response.\nRETURN\n@STORY_EVENT\nCALL KOJO_DAILY\nRETURN\n@KOJO_MISC\nPRINTL Unknown context.\nRETURN\n@KOJO_DAILY_FLAG\nIF CFLAG:0\nPRINTL Conditional response.\nENDIF\nRETURN" };
        var policy=new StoryPolicy(()=>texts.Keys,p=>texts[p]);
        var routine=policy.Evaluate([Site("EVENTTRAIN",2,"parent"),Site("KOJO_DAILY",5)]);
        Check(routine.Allowed && routine.Kind=="routine","known daily leaf with a verified dispatcher is allowed");
        Check(!policy.Evaluate([Site("STORY_EVENT",8,"parent"),Site("KOJO_DAILY",5)]).Allowed,"a daily-named leaf inside a story caller stays protected");
        Check(!policy.Evaluate([Site("KOJO_MISC",11)]).Allowed,"generic KOJO naming alone is insufficient");
        Check(!policy.Evaluate([Site("KOJO_DAILY_FLAG",15)]).Allowed,"event-flag branches stay unknown/protected despite daily naming");
        Check(!policy.Evaluate([Site("KOJO_DAILY",5),Site("STORY_EVENT",8,"parent")]).Allowed,"mixed capture keeps the strongest protection instead of the last line's decision");
        Check(!policy.Evaluate([]).Allowed && !policy.Evaluate(["overflow"]).Allowed,"missing or truncated call context fails closed");
        var unknown=policy.Evaluate([Site("KOJO_MISC",11)]); policy.SetRule(unknown.RuleKey,"allow");
        Check(policy.Evaluate([Site("KOJO_MISC",11)]).Allowed,"explicit function allow enables an otherwise unknown routine");
        Check(!policy.Evaluate([Site("STORY_EVENT",8,"parent"),Site("KOJO_MISC",11)]).Allowed,"function allow cannot override a story parent");
        policy.SetRule(routine.RuleKey,"deny");
        Check(policy.Evaluate([Site("KOJO_DAILY",5)]).Kind=="manual-deny","saved deny overrides automatic routine eligibility");
        string exported=policy.Export();
        var restored=new StoryPolicy(()=>texts.Keys,p=>texts[p]); restored.Import(exported);
        Check(restored.Evaluate([Site("KOJO_DAILY",5)]).Kind=="manual-deny","export/import roundtrip retains exact function rules");
        texts["ERB/test.erb"]=texts["ERB/test.erb"].Replace("Unknown context.","Changed source.");
        var updated=new StoryPolicy(()=>texts.Keys,p=>texts[p]); updated.Import(exported);
        Check(!updated.Evaluate([Site("KOJO_MISC",11)]).Allowed,"source edits invalidate a previous function allow fingerprint");
        var ambiguous=new StoryPolicy(()=>new[]{"ERB/a/test.erb","ERB/b/test.erb"},p=>texts["ERB/test.erb"]);
        Check(!ambiguous.Evaluate([Site("KOJO_DAILY",5,"leaf","test.erb")]).Allowed,"duplicate basenames are not guessed");

        using(var readGate=new ManualResetEventSlim(false))
        {
            var cancelledClient=new Client();
            using var cancelled=new AiController(new AiSettings { Enabled=true,AutoGenerate=true,UseGameStyle=false },cancelledClient);
            cancelled.ConfigureStoryPolicy(new StoryPolicy(()=>texts.Keys,p=>{readGate.Wait(); return texts[p];}));
            cancelled.SetEvent(new DialogueSnapshot("cancel-check","旅人","玩家","","原文","KOJO_DAILY",null,[Site("KOJO_DAILY",5)]));
            var pending=cancelled.GenerateAsync(true); cancelled.RestoreOriginal(); readGate.Set(); await pending;
            await Until(()=>cancelled.StoryReady);
            Check(cancelledClient.Calls==0 && cancelled.CanGenerate && cancelled.Text=="原文" && cancelled.DisplayMode=="original","cancel during source analysis prevents delayed generation and leaves a usable original view");
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
        string output=Path.Combine(root,"logs","story-host"); Directory.CreateDirectory(output);
        EraCore.Maui.FileSystem.AppDataDirectory=Path.Combine(output,"run-"+Guid.NewGuid().ToString("N").Substring(0,8));
        var settings=new AiSettings { Enabled=true,AutoGenerate=true,GenerateWithoutOriginal=true,UseGameStyle=false,Model="controlled-story-test" };
        EraCore.Maui.SecureStorage.Default.Read=Task.FromResult<string?>(Json.Codec().Serialize(settings.ToDictionary(true)));
        var fake=new Client(); var host=new EraCore.Maui.BridgeHost(game,fake);
        async Task Input(string value) { int turns=host.Js.Turns.Count; host.Session!.Input.Submit(new InputFrame(Value:value)); await Until(()=>host.Js.Turns.Count>turns && host.Controller.StoryReady && !host.Controller.Busy); await Task.Delay(80); }
        try
        {
            host.Start(); await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(60)); await Until(()=>host.Controller.StoryReady && !host.Js.Turns.IsEmpty);
            var initial=Json.Codec().DeserializeObject(System.Text.Json.JsonSerializer.Serialize(host.Session!.Output.Snapshot));
            Check(fake.Calls==0 && !host.Controller.CanGenerate,"no-original menu supplement is protected by default, not exempt from the story gate");
            await Input("1"); await Until(()=>fake.Calls==1 && !host.Controller.Busy);
            Check(host.Controller.DisplayMode=="ai" && host.Controller.Story.Kind=="routine","actual engine normal call chain automatically generates once");
            await Input("2"); Check(fake.Calls==1 && host.Controller.DisplayMode=="original" && host.Controller.Story.Kind=="story","actual nested story call leaves original text and sends no request");
            await host.Command("aiGenerate"); Check(fake.Calls==1,"direct regenerate command cannot bypass protection");
            await host.Command(new Dictionary<string,object>{{"type","aiStoryRule"},{"scope","event"},{"action","allow"},{"eventId",host.Controller.Current!.EventId},{"storyTurn",host.Controller.Current.StoryTurn-1}});
            Check(!host.Controller.CanGenerate && fake.Calls==1 && host.Controller.Log.ReadRecent().Contains("story.command-rejected"),"a stale UI allow action cannot permit a newer event");
            await host.Controller.SetStoryRuleAsync("allow","event"); string before=System.Text.Json.JsonSerializer.Serialize(host.Session.Output.Snapshot);
            await host.Command("aiGenerate"); await host.Command("aiGenerate");
            Check(fake.Calls==3 && host.Controller.DisplayMode=="ai" && System.Text.Json.JsonSerializer.Serialize(host.Session.Output.Snapshot)==before,"explicit event allow permits regeneration without rerunning or changing the original engine output");
            await Input("2"); Check(fake.Calls==3 && !host.Controller.CanGenerate,"event allow expires on the next event even when text/call chain repeat");
            await Input("3"); Check(!host.Controller.CanGenerate && host.Controller.Story.Kind=="unknown","actual generic KOJO stays unknown");
            await host.Controller.SetStoryRuleAsync("allow","function"); await host.Command("aiGenerate");
            Check(fake.Calls==4 && host.Controller.CanGenerate,"per-game function allow is usable through the production controller");
            await host.Controller.SetStoryRuleAsync("deny","function"); await host.Controller.SetStoryRuleAsync("allow","event"); await host.Command("aiGenerate");
            Check(fake.Calls==4 && !host.Controller.CanGenerate,"persistent deny takes precedence over event allow");
            await Input("4"); Check(fake.Calls==4 && host.Controller.Story.Kind=="story","actual mixed block retains earlier nested story evidence");
            await Input("5"); Check(fake.Calls==4 && host.Controller.Story.Kind=="story","actual parent call-site inside CFLAG branch is protected");
            await Input("6"); Check(fake.Calls==4 && host.Controller.Current?.Original=="" && !host.Controller.CanGenerate,"empty KOJO plus retained options does not bypass protection");
            Check(host.Controller.Log.ReadRecent().Contains("story.decision") && host.Controller.Log.ReadRecent().Contains("request.protected") && host.Controller.Log.ReadRecent().Contains("DIALOGUE_CASES.ERB",StringComparison.OrdinalIgnoreCase),"logs explain decisions, source positions and blocked manual generation");
            await File.WriteAllTextAsync(Path.Combine(output,"bridge-replay.json"),Json.Codec().Serialize(new Dictionary<string,object>{{"initial",initial},{"events",host.Js.Events.ToArray()}}));
            File.WriteAllText(Path.Combine(output,"generation.log"),host.Controller.Log.ReadRecent());
        }
        finally { await host.Stop(); }
        Console.WriteLine("ALL STORY CHECKS PASSED: "+count);
    }
}
