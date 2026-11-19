using System.Diagnostics;
using System.Security.Cryptography;
using MinorShift.Emuera.AI;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Tests;

internal static class SupplementTests
{
    static int count;
    static void Check(bool value,string label) { if(!value) throw new Exception("FAIL: "+label); count++; Console.WriteLine("PASS: "+label); }
    sealed class Client : IAiClient
    {
        public int Calls;
        public string Last="";
        public Func<Task<string>> Reply=()=>Task.FromResult("旅人停在路口，等待同行者的决定。");
        public Task<string> GenerateAsync(AiSettings settings,Dictionary<string,object> body,CancellationToken token)
        { Last=Json.Codec().Serialize(body); Interlocked.Increment(ref Calls); return Reply(); }
    }
    static async Task Until(Func<bool> predicate)
    {
        var timer=Stopwatch.StartNew();
        while(!predicate()) { if(timer.Elapsed.TotalSeconds>20) throw new TimeoutException("Supplement test condition"); await Task.Delay(20); }
    }
    static string State(LoaderTestHarness h)
    {
        using var stream=new MemoryStream(); var writer=new EraBinaryDataWriter(stream);
        h.VEvaluator.SaveToStreamBinary(writer,"fixed"); writer.Close();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    static string Fixture(string root)
    {
        string game=Path.Combine(root,"portable","Tests","supplement-game");
        Directory.CreateDirectory(Path.Combine(game,"CSV")); Directory.CreateDirectory(Path.Combine(game,"ERB"));
        foreach(var file in Directory.GetFiles(Path.Combine(root,"demo","CSV"))) File.Copy(file,Path.Combine(game,"CSV",Path.GetFileName(file)),true);
        File.WriteAllText(Path.Combine(game,"ERB","SUPPLEMENT.ERB"),"""
@SYSTEM_TITLE
ADDCHARA 0
ADDCHARA 1
MASTER = 0
TARGET = 1
FLAG:0 = 7
PRINTBUTTON "[1] 等待", 1
PRINTBUTTON "[2] 查看地图", 2
PRINTL
$AGAIN
INPUT
FLAG:0 += 1
CALL KOJO_EMPTY
GOTO AGAIN

@KOJO_EMPTY
RETURN 0
""");
        return game;
    }
    static async Task Engine(string game)
    {
        using var h=new LoaderTestHarness(game); await h.InitializeAsync(); h.Console._state.process=h.Process; h.Console.RunEmueraProgram("");
        var awaitInfo=AwaitInfo.Of(h.Console.CurrentRequest,h.Console.State,false);
        string before=State(h);
        var frame=h.Console.AiReadChoiceContext(awaitInfo);
        Check(h.Console.AiReadDialogue()==null && frame?.InsertBeforeLine>=0 && frame.Original.Length==0,"menu with no dialogue produces an insertion candidate, never a fabricated original");
        Check(frame!.Facts.Contains("等待") && frame.Facts.Contains("查看地图") && frame.Facts.Contains("尚未执行"),"candidate uses exact existing options and describes them as unexecuted");
        Check(h.Console.AiReadChoiceContext(awaitInfo)!.EventId==frame.EventId && State(h)==before,"re-reading a choice snapshot is stable and leaves the complete saved state unchanged");
        var client=new Client();
        using var controller=new AiController(new AiSettings { ProtectStory=false, Enabled=true,AutoGenerate=false,UseGameStyle=false,Model="neutral-test" },client);
        controller.SetEvent(new DialogueSnapshot(frame.EventId,frame.Character,frame.User,frame.Facts,frame.Original,frame.Hook));
        await controller.GenerateAsync(false); await controller.GenerateAsync(true); await controller.GenerateAsync(true);
        Check(client.Calls==3 && State(h)==before && client.Last.Contains("选项不是已发生的事实"),"supplement and two regenerations preserve every game variable and constrain the prompt to the current choice state");
        client.Reply=()=>throw new HttpRequestException("controlled offline"); await controller.GenerateAsync(true);
        Check(controller.Text=="" && controller.DisplayMode=="original" && controller.Status.Contains("无原口上") && State(h)==before,"no-original failure leaves text empty and all game variables unchanged");
        controller.RestoreOriginal(); Check(controller.Text=="" && controller.Status.Contains("保留原游戏画面"),"cancel restores an actually empty original without inventing text");
        var target=h.VEvaluator.TARGET; h.VEvaluator.TARGET=-1;
        Check(h.Console.AiReadChoiceContext(awaitInfo)==null,"invalid character never produces a choice supplement"); h.VEvaluator.TARGET=target;
        Check(h.Console.AiReadChoiceContext(AwaitInfo.Of(null,h.Console.State,false))==null,"no active input never produces a choice supplement");
    }
    public static async Task Run(string root)
    {
        string game=Fixture(root); await Engine(game);
        string output=Path.Combine(root,"logs","supplement-host"); Directory.CreateDirectory(output);
        EraCore.Maui.FileSystem.AppDataDirectory=output;
        var settings=new AiSettings { ProtectStory=false, Enabled=true,AutoGenerate=true,UseGameStyle=false,Model="neutral-test" };
        EraCore.Maui.SecureStorage.Default.Read=Task.FromResult<string?>(Json.Codec().Serialize(settings.ToDictionary(true)));
        var client=new Client(); var host=new EraCore.Maui.BridgeHost(game,client);
        try
        {
            host.Start(); await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var session=host.Session!;
            await Until(()=>!host.Js.Turns.IsEmpty);
            Check(host.Controller.Current==null && client.Calls==0,"new supplement feature is opt-in for existing AI settings");
            var initial=Json.Codec().DeserializeObject(System.Text.Json.JsonSerializer.Serialize(session.Output.Snapshot));
            string before=System.Text.Json.JsonSerializer.Serialize(session.Output.Snapshot);
            var pending=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); client.Reply=()=>pending.Task;
            settings.GenerateWithoutOriginal=true; await host.SaveSettings(settings);
            await Until(()=>client.Calls==1);
            Check(AiSettings.Parse(await EraCore.Maui.SecureStorage.Default.GetAsync("ignored") ?? "{}").GenerateWithoutOriginal,"opt-in survives the production save/storage/parse path");
            Check(host.Controller.Current?.Hook=="CHOICES_WITHOUT_DIALOGUE" && host.Controller.Current.Original=="" && host.Controller.Busy,"enabling supplement automatically generates for the current no-dialogue menu");
            Check(host.Js.Messages.Select(x=>Json.Object(Json.Codec().DeserializeObject(x))).Any(m=>Json.Text(m,"type")=="aiState" && Json.Int(m,"insertBeforeLine",-1)>=0 && !Json.Bool(m,"hasOriginal",true) && Json.Array(Json.Get(m,"lineIndices")).Length==0),"production bridge supplies insertion anchor without replacement indices");
            pending.SetResult("旅人停在路口，等待同行者的决定。"); await Until(()=>!host.Controller.Busy);
            Check(host.Controller.DisplayMode=="ai" && System.Text.Json.JsonSerializer.Serialize(session.Output.Snapshot)==before,"successful supplement does not insert lines into the interpreter transcript");
            client.Reply=()=>Task.FromResult("同行者把地图折好，静候下一步安排。"); await host.Command("aiGenerate"); await host.Command("aiGenerate");
            Check(client.Calls==3 && System.Text.Json.JsonSerializer.Serialize(session.Output.Snapshot)==before,"two production bridge regenerations send no game input");
            client.Reply=()=>throw new HttpRequestException("controlled offline"); await host.Command("aiGenerate");
            Check(host.Controller.Text=="" && host.Controller.Status.Contains("无原口上"),"production failure clears the insertion rather than inventing fallback dialogue");
            var late=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); client.Reply=()=>late.Task;
            var old=host.Command("aiGenerate"); await Until(()=>client.Calls==5);
            string oldId=host.Controller.Current!.EventId;
            var next=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); client.Reply=()=>next.Task;
            session.Input.Submit(new InputFrame(Value:"1")); await Until(()=>client.Calls==6);
            Check(host.Controller.Current!.EventId!=oldId && host.Controller.Current.Hook=="CHOICES_WITHOUT_DIALOGUE","a round with an empty KOJO and no new PRINT still generates from the live retained choices");
            late.SetResult("LATE_REPLY_MUST_NOT_APPEAR"); await old;
            Check(host.Controller.Busy && host.Controller.Text=="","late prior-round reply cannot overwrite the new menu's pending generation");
            next.SetResult("旅人看向同行者，耐心等待新的选择。"); await Until(()=>!host.Controller.Busy);
            settings.GenerateWithoutOriginal=false; await host.SaveSettings(settings);
            Check(host.Controller.Current==null && host.Controller.Text=="","disabling supplement removes the current supplemental event immediately");
            await File.WriteAllTextAsync(Path.Combine(output,"bridge-replay.json"),Json.Codec().Serialize(new Dictionary<string,object> { {"initial",initial}, {"events",host.Js.Events.ToArray()} }));
            Check(host.Controller.Log.ReadRecent().Contains("capture.supplement") && host.Controller.Log.ReadRecent().Contains("unchanged-game"),"logs distinguish no-original generation and unchanged-game fallback");
        }
        finally { await host.Stop(); }
        Console.WriteLine("ALL SUPPLEMENT CHECKS PASSED: "+count);
    }
}
