using System.Security.Cryptography;
using MinorShift.Emuera.AI;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Tests;

internal static class AddressTests
{
    static int count;
    static void Check(bool value,string label) { if(!value) throw new Exception("FAIL: "+label); count++; Console.WriteLine("PASS: "+label); }
    sealed class Client : IAiClient
    {
        internal string Reply="", Body="";
        public Task<string> GenerateAsync(AiSettings s,Dictionary<string,object> body,CancellationToken ct) { Body=Json.Codec().Serialize(body); return Task.FromResult(Reply); }
    }
    static string State(LoaderTestHarness h)
    {
        using var stream=new MemoryStream(); var writer=new EraBinaryDataWriter(stream);
        h.VEvaluator.SaveToStreamBinary(writer,"fixed"); writer.Close(); return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    public static async Task Run(string root)
    {
        string game=Path.Combine(root,"portable","Tests","address-game"); Directory.CreateDirectory(Path.Combine(game,"CSV")); Directory.CreateDirectory(Path.Combine(game,"ERB"));
        foreach(var file in Directory.GetFiles(Path.Combine(root,"demo","CSV"))) File.Copy(file,Path.Combine(game,"CSV",Path.GetFileName(file)),true);
        File.WriteAllText(Path.Combine(game,"ERB","ADDRESS.ERB"),"""
@SYSTEM_TITLE
ADDCHARA 0
ADDCHARA 1
MASTER = 0
TARGET = 1
CALLNAME:TARGET = 琳琳
MASTERNAME:TARGET = 阁下
CSTR:TARGET:60 = 本小姐
CALL KOJO_ADDRESS
PRINTBUTTON "[1] 等待", 1
PRINTL
$AGAIN
INPUT
GOTO AGAIN

@KOJO_ADDRESS
PRINTFORML %CALLNAME:TARGET%说：「%SELF_CALL(TARGET)%请%MASTERNAME:TARGET%看地图。」
RETURN 0

@SELF_CALL, ARG
#FUNCTIONS
FLAG:2 += 1
RETURNF CSTR:ARG:60
""");
        using var h=new LoaderTestHarness(game); await h.InitializeAsync(); h.Console._state.process=h.Process; h.Console.RunEmueraProgram("");
        var frame=h.Console.AiReadDialogue() ?? throw new Exception("No address fixture dialogue: "+string.Join("\n",h.Console.DisplayLineList.Select(l=>l.ToString())));
        Check(frame.Addresses.Any(p=>p.Key.StartsWith("SELF_CALL") && p.Value=="本小姐") && frame.Addresses["CALLNAME:TARGET"]=="琳琳" && frame.Addresses["MASTERNAME:TARGET"]=="阁下","actual PRINTFORM captures resolved SELF_CALL plus role-specific CALLNAME and MASTERNAME");
        Check(h.VEvaluator.VariableData.DataIntegerArray[(int)(VariableCode.FLAG & VariableCode.__LOWERCASE__)][2]==1,"the script SELF_CALL ran exactly once during its original PRINTFORM");
        string before=State(h);
        var snapshot=new DialogueSnapshot(frame.EventId,frame.Character,frame.User,frame.Facts,frame.Original,frame.Hook,frame.Addresses);
        var client=new Client(); using var controller=new AiController(new AiSettings { ProtectStory=false, Enabled=true,AutoGenerate=false,UseGameStyle=false,Model="controlled" },client,new AiLog(Path.Combine(root,"logs","address-generation.log")));
        controller.SetEvent(snapshot);
        client.Reply=snapshot.Addresses.Mask("琳琳说：「本小姐带来了地图，阁下请过目。」");
        await controller.GenerateAsync(false);
        Check(controller.DisplayMode=="ai" && controller.Text=="琳琳说：「本小姐带来了地图，阁下请过目。」" && !controller.Text.Contains("ERA_ADDR"),"AI placeholders are restored by the program to exact original address strings");
        Check(client.Body.Contains("ERA_ADDR") && client.Body.Contains("SELF_CALL") && client.Body.Contains("优先于预设"),"request contains authoritative variable bindings and masked source text");
        await controller.GenerateAsync(true); await controller.GenerateAsync(true);
        Check(State(h)==before && h.VEvaluator.VariableData.DataIntegerArray[(int)(VariableCode.FLAG & VariableCode.__LOWERCASE__)][2]==1,"repeated generation preserves the complete saved state and never re-evaluates SELF_CALL");
        client.Reply="琳琳说：「我带来了地图，你请过目。」"; await controller.GenerateAsync(true);
        Check(controller.DisplayMode=="original" && controller.Text==frame.Original && controller.Status.Contains("称呼变量"),"changing 本小姐/阁下 into 我/你 rejects the reply and restores original dialogue");
        client.Reply=snapshot.Addresses.Mask("琳琳说：「本小姐带来了地图，阁下请过目。」")+"她等待答复。"; await controller.GenerateAsync(true);
        Check(controller.DisplayMode=="original" && controller.Status.Contains("人称代词"),"an unbound new third-person pronoun is not silently substituted for the game's address terms");
        client.Reply="{ERA_ADDR_unknown}"; await controller.GenerateAsync(true);
        Check(controller.DisplayMode=="original","unknown address placeholders cannot leak into game display");
        var source=new[]{"琳琳说：「本小姐请阁下看地图。」"};
        client.Reply=Json.Codec().Serialize(new Dictionary<string,object>{{"translations",new object[]{new Dictionary<string,object>{{"id",0},{"text",snapshot.Addresses.Mask("琳琳说：「本小姐请阁下查看地图。」")}}}}});
        var translated=await Translation.TranslateAsync(client,new AiSettings { ProtectStory=false, Model="controlled" },source,CancellationToken.None,snapshot.Addresses);
        Check(translated[0]=="琳琳说：「本小姐请阁下查看地图。」" && State(h)==before,"translation also preserves address placeholders without touching game variables");
        controller.SetEvent(new DialogueSnapshot("empty-speech",frame.Character,frame.User,frame.Facts,"",frame.Hook,frame.Addresses));
        client.Reply=snapshot.Addresses.Mask("「本小姐带来了地图，阁下请过目。」"); await controller.GenerateAsync(false);
        Check(controller.DisplayMode=="ai" && controller.Text=="「本小姐带来了地图，阁下请过目。」" && State(h)==before,"empty-source speech restores exact known self/player address variables without re-evaluation");
        var nameOnly=new Dictionary<string,string>{{"NAME:TARGET","琳琳"}};
        controller.SetEvent(new DialogueSnapshot("empty-no-pronouns","琳琳","Player",frame.Facts,"",frame.Hook,nameOnly));
        client.Reply="「接下来往哪边走？」"; await controller.GenerateAsync(false);
        Check(controller.DisplayMode=="ai" && controller.Text==client.Reply && State(h)==before,"speech with omitted subjects is allowed when the game has supplied no self/player pronouns");
        client.Reply="「我准备好了，你呢？」"; await controller.GenerateAsync(true);
        Check(controller.DisplayMode=="original" && controller.Text=="" && controller.Status.Contains("人称代词") && State(h)==before,"adding unbound pronouns to empty-source speech still falls back without changing game state");
        Console.WriteLine("ALL ADDRESS CHECKS PASSED: "+count);
    }
}
