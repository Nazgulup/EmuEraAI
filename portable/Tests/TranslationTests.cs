using System.Diagnostics;
using System.Net;
using System.Text;
using MinorShift.Emuera.AI;

internal static class TranslationTests
{
    static int count;
    static void Check(bool value,string label) { if(!value) throw new Exception("FAIL: "+label); count++; Console.WriteLine("PASS: "+label); }
    static string Reply(params string[] text) => Json.Codec().Serialize(new Dictionary<string,object> { {"translations",text.Select((t,i)=>(object)new Dictionary<string,object>{{"id",i},{"text",t}}).ToArray()} });
    sealed class Handler : HttpMessageHandler
    {
        internal string Body="", Finish="stop";
        internal string Text=Reply("清晨的风吹拂着庭院。","[1] 查看地图","[2] 等待");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct)
        {
            Body=await req.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content=new StringContent(Json.Codec().Serialize(new Dictionary<string,object> { {"choices",new object[]{new Dictionary<string,object>{{"finish_reason",Finish},{"message",new Dictionary<string,object>{{"content",Text}}}}}} }),Encoding.UTF8,"application/json") };
        }
    }
    sealed class Client : IAiClient
    {
        internal int Calls;
        internal CancellationToken LastToken;
        internal Func<Task<string>> Response=()=>Task.FromResult(Reply("清晨的风吹拂着庭院。","[1] 查看地图","[2] 等待"));
        public Task<string> GenerateAsync(AiSettings s,Dictionary<string,object> body,CancellationToken ct) { LastToken=ct; Interlocked.Increment(ref Calls); return Response(); }
    }
    static async Task Until(Func<bool> predicate)
    {
        var timer=Stopwatch.StartNew();
        while(!predicate()) { if(timer.Elapsed.TotalSeconds>20) throw new TimeoutException("Translation host condition"); await Task.Delay(20); }
    }
    static Dictionary<string,object> Message(EraCore.Maui.BridgeHost host,string id) => host.Js.Messages.Select(x=>Json.Object(Json.Codec().DeserializeObject(x))).Last(m=>Json.Text(m,"type")=="aiTranslation" && Json.Text(m,"requestId")==id);

    public static async Task Run(string root)
    {
        string[] source=["朝の風が庭を吹き抜けた。","[1] 地図を見る","[2] 待つ"];
        var settings=new AiSettings { TranslationEnabled=true,Enabled=false,UseGameStyle=false,Model="controlled-translation",Endpoint="https://api.deepseek.com/v1",TranslationTokens=2048,PresetJson="{\"prompts\":[{\"content\":\"DO_NOT_USE_ROLEPLAY_PRESET\"}]}" };
        var loaded=AiSettings.Parse(Json.Codec().Serialize(settings.ToDictionary(true)));
        Check(loaded.TranslationEnabled && loaded.TranslationLanguage=="zh-CN" && loaded.TranslationTokens==2048 && !loaded.Enabled,"translation preferences persist independently of dialogue generation");
        using(var handler=new Handler())
        using(var client=new AiClient(handler))
        {
            var result=await Translation.TranslateAsync(client,settings,source,CancellationToken.None);
            Check(result.SequenceEqual(new[]{"清晨的风吹拂着庭院。","[1] 查看地图","[2] 等待"}),"real HTTP parser translates neutral Japanese body and options, preserving numeric labels");
            var body=Json.Object(Json.Codec().DeserializeObject(handler.Body));
            Check(Json.Int(body,"max_tokens")==2048 && Json.Text(body,"model")==settings.Model && !handler.Body.Contains("DO_NOT_USE_ROLEPLAY_PRESET") && Json.Text(Json.Object(body["thinking"]),"type")=="disabled","translation uses its own output budget and prompt plus the configured model and thinking mode");
            handler.Finish="length";
            try { await Translation.TranslateAsync(client,settings,source,CancellationToken.None); throw new Exception("accepted truncated reply"); }
            catch(FormatException ex) { Check(ex.Message.Contains("token"),"truncated output is rejected before any partial translation is applied"); }
        }
        Check(Translation.Parse("{\"translations\":[{\"id\":1,\"text\":\"[2] 等待\"},{\"id\":0,\"text\":\"[1] 查看地图\"}]}",source[1..]).SequenceEqual(new[]{"[1] 查看地图","[2] 等待"}),"reordered translation entries are restored by stable IDs");
        foreach(var invalid in new[] { Reply("[9] 地图"), Reply("[1] 地图\n另一行"), "{\"translations\":[]}", "{\"translations\":[{\"id\":2,\"text\":\"[1] 地图\"}]}", "{\"translations\":[{\"id\":0,\"text\":\"\"}]}" })
        {
            try { Translation.Parse(invalid,[source[1]]); throw new Exception("accepted invalid translation"); }
            catch(FormatException) { }
        }
        Check(true,"changed option numbers, new lines, missing/out-of-range entries and empty output all retain source text");
        try { Translation.Parse(Reply("数值 10"),["値 {value} 10"]); throw new Exception("accepted missing placeholder"); }
        catch(FormatException) { Check(true,"format placeholders cannot be removed by translation"); }

        string game=Path.Combine(root,"portable","Tests","translation-game");
        Directory.CreateDirectory(Path.Combine(game,"CSV")); Directory.CreateDirectory(Path.Combine(game,"ERB"));
        foreach(var file in Directory.GetFiles(Path.Combine(root,"demo","CSV"))) File.Copy(file,Path.Combine(game,"CSV",Path.GetFileName(file)),true);
        File.WriteAllText(Path.Combine(game,"ERB","TRANSLATION.ERB"),"@SYSTEM_TITLE\nPRINTL 朝の風が庭を吹き抜けた。\nPRINTBUTTON \"[1] 地図を見る\", 1\nPRINTBUTTON \"[2] 待つ\", 2\nPRINTL\n$AGAIN\nINPUT\nGOTO AGAIN\n");
        string output=Path.Combine(root,"logs","translation-host"); Directory.CreateDirectory(output); EraCore.Maui.FileSystem.AppDataDirectory=output;
        settings.PresetJson=""; EraCore.Maui.SecureStorage.Default.Read=Task.FromResult<string?>(Json.Codec().Serialize(settings.ToDictionary(true)));
        var fake=new Client(); var host=new EraCore.Maui.BridgeHost(game,fake); bool stopped=false;
        try
        {
            host.Start(); await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(60)); await Until(()=>!host.Js.Turns.IsEmpty);
            string before=System.Text.Json.JsonSerializer.Serialize(host.Session!.Output.Snapshot);
            var initial=Json.Codec().DeserializeObject(before);
            await host.Translate("body-and-options",source);
            Check(fake.Calls==1 && Json.Array(Json.Get(Message(host,"body-and-options"),"translations")).Length==3,"production bridge translates without requiring dialogue PRINT hooks or enabling AI dialogue");
            Check(System.Text.Json.JsonSerializer.Serialize(host.Session.Output.Snapshot)==before,"translation leaves the complete interpreter display and option values unchanged");
            fake.Response=()=>throw new HttpRequestException("controlled offline"); await host.Translate("offline",source);
            Check(Json.Text(Message(host,"offline"),"error").Contains("原文") && Json.Array(Json.Get(Message(host,"offline"),"translations")).Length==0,"offline translation returns a source fallback with an actionable status");
            var late=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); fake.Response=()=>late.Task;
            var pending=host.Translate("old-settings",source); var token=fake.LastToken;
            settings.TranslationLanguage="en"; await host.SaveSettings(settings); late.SetResult(Reply("清晨的风吹拂着庭院。","[1] 查看地图","[2] 等待")); await pending;
            Check(token.IsCancellationRequested && !host.Js.Messages.Any(x=>x.Contains("old-settings") && x.Contains("清晨")),"saving another language cancels pending translation and prevents late accepted text");
            settings.TranslationEnabled=false; await host.SaveSettings(settings); int calls=fake.Calls; await host.Translate("disabled",source);
            Check(fake.Calls==calls && Json.Text(Message(host,"disabled"),"error").Length>0,"disabled translation does not call an API");
            settings.TranslationEnabled=true; settings.TranslationLanguage="zh-CN"; await host.SaveSettings(settings);
            late=new(TaskCreationOptions.RunContinuationsAsynchronously); fake.Response=()=>late.Task;
            pending=host.Translate("exit-pending",source); token=fake.LastToken;
            await host.Stop(); stopped=true; late.SetResult(Reply("清晨的风吹拂着庭院。","[1] 查看地图","[2] 等待")); await pending;
            Check(token.IsCancellationRequested && !host.Js.Messages.Any(x=>x.Contains("exit-pending")),"exiting a game cancels its translation and cannot publish into the next game");
            await File.WriteAllTextAsync(Path.Combine(output,"bridge-replay.json"),Json.Codec().Serialize(new Dictionary<string,object>{{"initial",initial},{"events",host.Js.Events.ToArray()}}));
        }
        finally { if(!stopped) await host.Stop(); }
        Console.WriteLine("ALL TRANSLATION CHECKS PASSED: "+count);
    }
}
