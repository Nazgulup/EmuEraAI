using System.Diagnostics;
using System.Security.Cryptography;
using MinorShift.Emuera.AI;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Tests;

internal static class PrefixedHookTests
{
    static int count;
    static void Check(bool yes,string text) { if(!yes) throw new Exception("FAIL: "+text); count++; Console.WriteLine("PASS: "+text); }
    static async Task Until(Func<bool> done) { var watch=Stopwatch.StartNew(); while(!done()) { if(watch.Elapsed.TotalSeconds>25) throw new TimeoutException("prefixed hook"); await Task.Delay(15); } }
    sealed class Client : IAiClient
    {
        public int Calls;
        public string Last="";
        public Func<Task<string>> Reply=()=>Task.FromResult("旅人合起地图，等待下一步安排。");
        public Task<string> GenerateAsync(AiSettings settings,Dictionary<string,object> body,CancellationToken token) { Last=Json.Codec().Serialize(body); Interlocked.Increment(ref Calls); return Reply(); }
    }
    static string State(LoaderTestHarness h)
    { using var stream=new MemoryStream(); var writer=new EraBinaryDataWriter(stream); h.VEvaluator.SaveToStreamBinary(writer,"fixed"); writer.Close(); return Convert.ToHexString(SHA256.HashData(stream.ToArray())); }
    static string Fixture(string root,bool empty=false)
    {
        string game=Path.Combine(root,"portable","Tests",empty ? "empty-hook-game" : "prefixed-hook-game"); Directory.CreateDirectory(Path.Combine(game,"CSV")); Directory.CreateDirectory(Path.Combine(game,"ERB"));
        foreach(string path in Directory.GetFiles(Path.Combine(root,"demo","CSV"))) File.Copy(path,Path.Combine(game,"CSV",Path.GetFileName(path)),true);
        // Neutral reproduction of the reported callback/flag/PRINTFORMW control flow.
        File.WriteAllText(Path.Combine(game,"ERB","EVENT_K9_ダイヤ.ERB"),"""
@SYSTEM_TITLE
ADDCHARA 0
ADDCHARA 1
MASTER = 0
TARGET = 1
SELECTCOM = 0
$AGAIN
CALL DOG_KOJO_9
FLAG:0 += 1
PRINTFORML COUNTER={CFLAG:301}; ACTIONS={FLAG:0}
PRINTL [1] 再次交谈
INPUT
GOTO AGAIN

@DOG_KOJO_9
IF SELECTCOM == 0
    IF CFLAG:301 == 0
        IF MARK:2 >= 2
            PRINTFORMW 旅人确认了首次见面的安排。
        ELSE
            PRINTFORMW 旅人打开地图，等待说明。
        ENDIF
        CFLAG:301 = 1
        RETURN 0
    ELSE
        IF TALENT:TARGET:136 == 1 && (CFLAG:301 <= 6 || FLAG:7 == 2)
            PRINTFORMW 旅人确认第七份行程安排。
            CFLAG:301 = 7
        ELSEIF TALENT:TARGET:76 == 1 && (CFLAG:301 <= 5 || FLAG:7 == 2)
            PRINTFORMW 旅人确认第六份行程安排。
            CFLAG:301 = 6
        ELSEIF TALENT:TARGET:85 == 1 && (CFLAG:301 <= 4 || FLAG:7 == 2)
            PRINTFORMW 旅人确认第五份行程安排。
            CFLAG:301 = 5
        ELSEIF MARK:2 == 3 && (CFLAG:301 <= 3 || FLAG:7 == 2)
            PRINTFORMW 旅人确认第四份行程安排。
            CFLAG:301 = 4
        ELSEIF MARK:2 == 2 && (CFLAG:301 <= 2 || FLAG:7 == 2)
            PRINTFORMW 旅人确认第三份行程安排。
            CFLAG:301 = 3
        ELSEIF MARK:2 <= 1 && (CFLAG:301 <= 1 || FLAG:7 == 2)
            PRINTFORMW 旅人再次确认地图上的路线。
            CFLAG:301 = 2
        ENDIF
        RETURN 0
    ENDIF
ENDIF
RETURN 0
""");
        if(empty)
        {
            string path=Path.Combine(game,"ERB","EVENT_K9_ダイヤ.ERB");
            string script=File.ReadAllText(path).Replace("CALL DOG_KOJO_9","CALL KOJO_MESSAGE_COM_9");
            script=System.Text.RegularExpressions.Regex.Replace(script,@"(?m)^(\s*PRINTFORMW)[^\r\n]*","$1");
            File.WriteAllText(path,script+"\n@KOJO_MESSAGE_COM_9\nCALL DOG_KOJO_9\nRETURN 0\n");
        }
        return game;
    }
    public static async Task Empty(string root,bool before=false)
    {
        string game=Fixture(root,true), output=Path.Combine(root,"logs","prefixed-hooks"); Directory.CreateDirectory(output);
        using(var h=new LoaderTestHarness(game))
        {
            await h.InitializeAsync(); h.Console._state.process=h.Process; h.Console.RunEmueraProgram("");
            var cflag=h.VEvaluator.VariableData.CharacterList[1].DataIntegerArray[(int)(VariableCode.CFLAG&VariableCode.__LOWERCASE__)];
            var frame=h.Console.AiReadDialogue();
            Check(h.Console.IsWaitingEnterKey && cflag[301]==0,"empty PRINTFORMW waits inside a callback called by the original supported parent");
            if(before) { Check(frame==null,"old capture drops the empty PRINTFORMW before policy/API even with a supported parent"); Console.WriteLine("REPRODUCED EMPTY DIALOGUE BUG: "+count); return; }
            Check(frame is {Original.Length:0,LineIndices.Length:>0} && frame.Hook=="DOG_KOJO_9","empty PRINTFORMW has a dialogue event and exact replacement positions");
            var client=new Client(); using var controller=new AiController(new AiSettings {AutoGenerate=false,UseGameStyle=false,GenerateWithoutOriginal=false},client,new AiLog(Path.Combine(output,"empty-engine.log")));
            controller.ConfigureStoryPolicy(new StoryPolicy(()=>["ERB/EVENT_K9_ダイヤ.ERB"],p=>File.ReadAllText(Path.Combine(game,p))));
            controller.SetEvent(new DialogueSnapshot(frame!.EventId,frame.Character,frame.User,frame.Facts,frame.Original,frame.Hook,frame.Addresses,frame.StoryContext,frame.StoryTurn));
            client.Reply=()=>Task.FromResult("旅人摊开地图。「接下来有什么安排？」");
            string saved=State(h); await controller.GenerateAsync(false); await controller.GenerateAsync(true);
            Check(client.Calls==2 && controller.DisplayMode=="ai" && State(h)==saved,"empty callback generation/regeneration preserves all variables and the pending wait");
            Check(client.Last.Contains("以当前角色直接说出口的台词为主体") && !client.Last.Contains("无原文时优先用角色姓名作第三人称叙述") && controller.Text.Contains("「接下来有什么安排？」"),"empty callback requests spoken dialogue without conflicting third-person-only guidance");
            client.Reply=()=>throw new HttpRequestException("controlled offline"); await controller.GenerateAsync(true);
            Check(controller.Text=="" && controller.DisplayMode=="original" && State(h)==saved,"empty fallback stays empty on failure and preserves the wait");
            await controller.SetStoryRuleAsync("deny","function"); int calls=client.Calls; await controller.GenerateAsync(true);
            Check(client.Calls==calls && !controller.CanGenerate,"manual off also blocks empty callbacks");
            h.Console.PressEnterKey(false,"",false); Check(cflag[301]==1,"empty wait continuation commits the first CFLAG assignment exactly once");
            h.Console.PressEnterKey(false,"1",false); var second=h.Console.AiReadDialogue();
            Check(second!=null && second.Original=="" && second.EventId!=frame.EventId && cflag[301]==1,"identical empty text on the next input gets a fresh event without early flag changes");
            h.Console.PressEnterKey(false,"",false); Check(cflag[301]==2,"second empty wait commits its own assignment only after continuation");
            Check(h.Console.AiReadDialogue()==null,"a value-input menu is not mistaken for an empty dialogue wait");
        }
        EraCore.Maui.FileSystem.AppDataDirectory=Path.Combine(output,"empty-host-"+Guid.NewGuid().ToString("N"));
        EraCore.Maui.SecureStorage.Default.Read=Task.FromResult<string?>(Json.Codec().Serialize(new AiSettings {UseGameStyle=false,GenerateWithoutOriginal=false,Model="neutral-empty-regression"}.ToDictionary(true)));
        var pending=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); var fake=new Client {Reply=()=>pending.Task}; var host=new EraCore.Maui.BridgeHost(game,fake);
        try
        {
            host.Start(); await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(60)); await Until(()=>fake.Calls==1 && host.Controller.Busy);
            Check(host.Controller.Current is {Original.Length:0,Hook:"DOG_KOJO_9"},"APK auto-generates the empty callback independently of optional menu supplementation");
            string saved=System.Text.Json.JsonSerializer.Serialize(host.Session!.Output.Snapshot);
            pending.SetResult("旅人整理好地图。「准备好了，下一步怎么走？」"); await Until(()=>!host.Controller.Busy);
            Check(host.Controller.DisplayMode=="ai" && host.Js.Messages.Any(m=>m.Contains("DOG_KOJO_9") && m.Contains("lineIndices")),"APK sends the empty callback result back to the game with source positions");
            Check(fake.Last.Contains("以当前角色直接说出口的台词为主体") && host.Controller.Text.Contains("「准备好了，下一步怎么走？」"),"APK empty callback includes the speech policy and returns spoken text");
            fake.Reply=()=>Task.FromResult("旅人望向路口。「先看看地图如何？」"); await host.Command("aiGenerate"); await host.Command("aiGenerate");
            Check(fake.Calls==3 && System.Text.Json.JsonSerializer.Serialize(host.Session.Output.Snapshot)==saved,"APK empty callback regeneration sends no game input");
            await host.Controller.SetStoryRuleAsync("deny","function"); await host.Command("aiGenerate");
            Check(fake.Calls==3 && host.Controller.DisplayMode=="original","APK manual off restores the empty original");
            File.WriteAllText(Path.Combine(output,"empty-host.log"),host.Controller.Log.ReadRecent());
        }
        finally { await host.Stop(); }
        Console.WriteLine("ALL EMPTY DIALOGUE CHECKS PASSED: "+count);
    }
    public static async Task Run(string root,bool before=false)
    {
        string game=Fixture(root), output=Path.Combine(root,"logs","prefixed-hooks"); Directory.CreateDirectory(output);
        using(var h=new LoaderTestHarness(game))
        {
            await h.InitializeAsync(); h.Console._state.process=h.Process; h.Console.RunEmueraProgram("");
            int index=(int)(VariableCode.CFLAG&VariableCode.__LOWERCASE__);
            var cflag=h.VEvaluator.VariableData.CharacterList[1].DataIntegerArray[index];
            Check(cflag[301]==0 && h.Console.DisplayLineList.Any(l=>l.ToString().Contains("旅人打开地图")),"reported PRINTFORMW/CFLAG:301 shape pauses before flag assignment");
            var frame=h.Console.AiReadDialogue();
            if(before) { Check(frame==null && h.Console.AiCaptureInfo(null).OutsideScope>0,"old APK capture misses DOG_KOJO_9 before any policy/API decision"); Console.WriteLine("REPRODUCED PREFIX BUG: "+count); return; }
            Check(frame?.Hook=="DOG_KOJO_9" && frame.Original.Contains("旅人打开地图") && frame.LineIndices.Length>0,"prefixed callback captures the actual rendered PRINTFORMW with replacement positions");
            Check(frame!.StoryContext.Any(s=>s.StartsWith("DOG_KOJO_9|EVENT_K9_ダイヤ.ERB|")),"Japanese source filename and exact function location are preserved");
            var client=new Client(); using var controller=new AiController(new AiSettings {AutoGenerate=false,UseGameStyle=false},client,new AiLog(Path.Combine(output,"engine-generation.log")));
            controller.ConfigureStoryPolicy(new StoryPolicy(()=>["ERB/EVENT_K9_ダイヤ.ERB"],p=>File.ReadAllText(Path.Combine(game,p))));
            controller.SetEvent(new DialogueSnapshot(frame.EventId,frame.Character,frame.User,frame.Facts,frame.Original,frame.Hook,frame.Addresses,frame.StoryContext,frame.StoryTurn));
            string state=State(h); await controller.GenerateAsync(false); await controller.GenerateAsync(true); await controller.GenerateAsync(true);
            Check(client.Calls==3 && controller.DisplayMode=="ai" && cflag[301]==0 && State(h)==state,"generating and regenerating twice does not advance PRINTFORMW or change any saved variable");
            client.Reply=()=>throw new HttpRequestException("controlled offline"); await controller.GenerateAsync(true);
            Check(controller.Text==frame.Original && State(h)==state,"API failure restores the same prefixed original with full state unchanged");
            await controller.SetStoryRuleAsync("deny","function"); int calls=client.Calls; await controller.GenerateAsync(true);
            Check(client.Calls==calls && !controller.CanGenerate && State(h)==state,"manual function off still blocks the newly recognized callback");
            h.Console.PressEnterKey(false,"",false);
            Check(cflag[301]==1 && h.Console.DisplayLineList.Any(l=>l.ToString().Contains("COUNTER=1; ACTIONS=1")),"only player continuation commits CFLAG:301 and finishes one original action");
            h.Console.PressEnterKey(false,"1",false); var repeat=h.Console.AiReadDialogue();
            Check(repeat?.Hook=="DOG_KOJO_9" && repeat.Original.Contains("再次确认") && cflag[301]==1,"repeat branch is captured while its flag assignment is still pending");
            h.Console.PressEnterKey(false,"",false); Check(cflag[301]==2,"repeat-branch flag assignment executes exactly once after continuation");
        }
        foreach(string name in new[]{"DOG_KOJO_9","COM_KOJO_12","_KOJO_MESSAGE_3","prefix_kojo_1","KOJO","DOG_KOJO","K10_KOJO2","K44_KOJO2_1","REVKOJO_MX_TRAIN_START","SELF_MAEKOJO","EXTRAKOJO2","ROTARKOJO","DUMMYKOJO_DAILYCOM_K1"}) Check(DialogueHookNames.IsDialogue(name),"recognizes delimited callback: "+name);
        foreach(string name in new[]{"DOGKOJO_9","KOJOBAR","SHOW_KOJONAME","SHOW_OPTIONS","KOJOPIC_K33","KOJOTEST_K50","K15_ALLKOJOSET","SET_K15_KOJOLIST","K15_USEDKOJO","KOJO2MENU"}) Check(!DialogueHookNames.IsDialogue(name),"does not broaden to arbitrary names: "+name);
        Check(DialogueHookNames.IsDialogue("K9_MESSAGE_COM_1") && DialogueHookNames.IsDialogue("SYSTXT_TRAIN_MESSAGE_F_COM_1") && DialogueHookNames.IsDialogue("TRAIN_MESSAGE_S_COM0"),"prefixed command-text families discovered in the scan are recognized");
        Check(DialogueHookNames.IsPreparation("K9_PRETRAIN_MESSAGE") && !DialogueHookNames.IsDialogue("UI_MESSAGE_COMPLEX"),"prefixed preparation keeps grouping priority while unrelated message names stay excluded");
        EraCore.Maui.FileSystem.AppDataDirectory=Path.Combine(output,"host-"+Guid.NewGuid().ToString("N"));
        EraCore.Maui.SecureStorage.Default.Read=Task.FromResult<string?>(Json.Codec().Serialize(new AiSettings {UseGameStyle=false,Model="neutral-prefix-regression"}.ToDictionary(true)));
        var pending=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); var fake=new Client {Reply=()=>pending.Task}; var host=new EraCore.Maui.BridgeHost(game,fake);
        try
        {
            host.Start(); await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(60)); await Until(()=>fake.Calls==1 && host.Controller.Busy);
            Check(host.Controller.Current?.Hook=="DOG_KOJO_9" && host.Controller.Text=="","production APK bridge automatically requests AI and hides the prefixed fallback while waiting");
            string state=System.Text.Json.JsonSerializer.Serialize(host.Session!.Output.Snapshot);
            pending.SetResult("旅人合起地图，等待说明。"); await Until(()=>!host.Controller.Busy);
            Check(host.Controller.DisplayMode=="ai" && host.Js.Messages.Any(m=>m.Contains("DOG_KOJO_9") && m.Contains("lineIndices")),"production bridge returns generated text and source positions to the game");
            fake.Reply=()=>Task.FromResult("旅人重新核对了路线。"); await host.Command("aiGenerate"); await host.Command("aiGenerate");
            Check(fake.Calls==3 && System.Text.Json.JsonSerializer.Serialize(host.Session.Output.Snapshot)==state,"production bridge regeneration sends no input and does not advance the wait");
            await host.Controller.SetStoryRuleAsync("deny","function"); await host.Command("aiGenerate");
            Check(fake.Calls==3 && host.Controller.DisplayMode=="original","manual off restores the original through the APK bridge");
            int turns=host.Js.Turns.Count; host.Session.Input.Submit(new InputFrame(Value:"")); await Until(()=>host.Js.Turns.Count>turns);
            Check(System.Text.Json.JsonSerializer.Serialize(host.Session.Output.Snapshot).Contains("COUNTER=1; ACTIONS=1"),"APK continuation performs the original flag update once");
            File.WriteAllText(Path.Combine(output,"host-generation.log"),host.Controller.Log.ReadRecent());
        }
        finally { await host.Stop(); }
        Console.WriteLine("ALL PREFIXED HOOK CHECKS PASSED: "+count);
    }
}
