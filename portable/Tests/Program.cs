using System.Security.Cryptography;
using MinorShift.Emuera.AI;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Tests;
using MinorShift.Emuera.Runtime.Utils;

static class Tests
{
    static int count;
    static void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
    sealed class Client : IAiClient
    {
        public int Calls;
        public string Last="";
        public Func<CancellationToken,Task<string>> Reply = _ => Task.FromResult("生成的文字");
        public Task<string> GenerateAsync(AiSettings s, Dictionary<string,object> request, CancellationToken token) { Calls++; Last=Json.Codec().Serialize(request); return Reply(token); }
    }
    static string State(LoaderTestHarness h)
    {
        string file=Path.Combine(h.TempRoot,"state-test.bin");
        using(var stream=File.Create(file)) { var writer=new EraBinaryDataWriter(stream); h.VEvaluator.SaveToStreamBinary(writer,"fixed"); writer.Close(); }
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    }
    static async Task Main(string[] args)
    {
        string root=Path.GetFullPath(args[0]);
        EraCore.Maui.FileSystem.PackageDirectory=Path.GetDirectoryName(StoryBundle.BundlePath(root))!;
        if(args.Length > 1 && args[1] == "--bundle-story") { StoryBundle.Build(root); return; }
        if(args.Length > 1 && args[1] == "--match-story") { StoryBundle.VerifyMatches(root); return; }
        if(args.Length > 1 && args[1] == "--function-document") { await FunctionDocumentTests.Run(root); return; }
        if(args.Length > 1 && args[1] == "--scan-story") { StoryScan.Run(root); return; }
        if(args.Length > 1 && args[1] == "--story") { await StoryTests.Run(root); return; }
        if(args.Length > 1 && args[1] == "--host") { await HostAiTests.Run(root); return; }
        if(args.Length > 1 && args[1].StartsWith("--colors")) { await ColorCompatibilityTests.Run(root,args[1]=="--colors-before"); return; }
        if(args.Length > 1 && args[1] == "--supplement") { await SupplementTests.Run(root); return; }
        if(args.Length > 1 && args[1] == "--translation") { await TranslationTests.Run(root); return; }
        if(args.Length > 1 && args[1] == "--addresses") { await AddressTests.Run(root); return; }
        var budgetSettings=AiSettings.Parse("{\"OutputTokens\":2048,\"Endpoint\":\"https://api.deepseek.com/v1\"}");
        var budgetSnapshot=new DialogueSnapshot("budget","Lin","Player","before action","Greeting","PRITRAIN_MESSAGE");
        var budgetPreset=new Preset("{\"prompts\":[],\"openai_max_tokens\":400}");
        var budgetBody=budgetPreset.Build(budgetSnapshot,budgetSettings,false);
        Check(Json.Int(budgetBody,"max_tokens")==2048 && Json.Text(Json.Object(budgetBody["thinking"]),"type")=="disabled","portable request uses UI token budget and disables DeepSeek thinking automatically");
        budgetSettings.UsePresetOutputTokens=true; budgetSettings.ThinkingMode="enabled";
        var roundtrip=AiSettings.Parse(Json.Codec().Serialize(budgetSettings.ToDictionary(false)));
        Check(roundtrip.UsePresetOutputTokens && roundtrip.UseGameStyle && roundtrip.ThinkingMode=="enabled" && Json.Int(budgetPreset.Build(budgetSnapshot,roundtrip,false),"max_tokens")==400,"portable style and reasoning preferences persist and affect the request");
        var largeBudget=AiSettings.Parse("{\"OutputTokens\":32768}");
        var largeRoundtrip=AiSettings.Parse(Json.Codec().Serialize(largeBudget.ToDictionary(false)));
        Check(largeRoundtrip.OutputTokens==32768 && Json.Int(new Preset("").Build(budgetSnapshot,largeRoundtrip,false),"max_tokens")==32768,
            "32768 UI output tokens survive settings roundtrip and request without an invented context ceiling");
        var largePreset=new Preset("{\"prompts\":[],\"openai_max_tokens\":65536,\"openai_max_context\":131072}");
        Check(Json.Int(largePreset.Build(budgetSnapshot,largeBudget,false),"max_tokens")==32768,"UI budget overrides preset when preset output is not selected");
        largeBudget.UsePresetOutputTokens=true;
        Check(Json.Int(largePreset.Build(budgetSnapshot,largeBudget,false),"max_tokens")==65536 && largePreset.OutputTokenSource(largeBudget)=="preset:openai_max_tokens",
            "65536 selected preset tokens reach the request unchanged");
        Check(new Preset("").OutputTokens(largeBudget)==32768 && new Preset("").OutputTokenSource(largeBudget)=="settings:OutputTokens",
            "missing preset output budget uses saved settings and reports the true source");
        Check(AiSettings.Parse("{\"OutputTokens\":1}").OutputTokens==1,"positive output budgets below 16 are preserved");
        foreach(string invalid in new[]{"0","-1","1.5","2147483648","null","true","\"invalid\""})
        {
            bool settingRejected=false,presetRejected=false;
            try { AiSettings.Parse("{\"OutputTokens\":"+invalid+"}"); } catch(FormatException ex) { settingRejected=ex.Message.Contains("设置中的输出上限"); }
            try { new Preset("{\"prompts\":[],\"openai_max_tokens\":"+invalid+"}").Build(budgetSnapshot,largeBudget,false); } catch(FormatException ex) { presetRejected=ex.Message.Contains("openai_max_tokens"); }
            Check(settingRejected && presetRejected,"invalid output limit rejected with its source, without rounding or clamping: "+invalid);
        }
        bool explicitBudgetRejected=false,overflowRejected=false;
        try { new Preset("{\"prompts\":[],\"openai_max_context\":8192}").Build(budgetSnapshot,largeBudget,false); }
        catch(FormatException ex) { explicitBudgetRejected=ex.Message.Contains("8192") && ex.Message.Contains("32768") && ex.Message.Contains("上下文预算不足"); }
        Check(explicitBudgetRejected,"explicit insufficient context identifies both budgets without lowering output silently");
        largeBudget.OutputTokens=int.MaxValue;
        try { new Preset("{\"prompts\":[],\"openai_max_context\":2147483647}").Build(budgetSnapshot,largeBudget,false); }
        catch(FormatException ex) { overflowRejected=ex.Message.Contains("上下文预算不足"); }
        Check(overflowRejected,"context reservation arithmetic cannot overflow at integer wire limit");
        var corpus=GameStyleCorpus.Read("neutral",new[]{"ERB/KOJO_TEST.ERB"},_=>new MemoryStream(System.Text.Encoding.UTF8.GetBytes("@KOJO_TEST\nPRINTL 旅人は地図を広げた。\nFLAG:0 += 1")),bytes=>string.Join("\n",EncodingHandler.ReadAllLinesFromBytes(bytes)));
        Check(corpus.Lines==1 && !corpus.Samples.Contains("FLAG"),"portable style scanner extracts Japanese text without executing script assignments");
        string macroRaw=Json.Codec().Serialize(new Dictionary<string,object> { {"prompts",new object[]{
            new Dictionary<string,object>{{"identifier","a"},{"content","{{// notes\n{{unsupported}} }}{{setvar::FLAG::9}}"}},
            new Dictionary<string,object>{{"identifier","b"},{"content","A\n{{trim}}\n{{getvar::FLAG}}{{random::B::B}}{{roll:2d1}}"}}
        }} });
        var macroPreset=new Preset(macroRaw);
        var macroSnapshot=new DialogueSnapshot("macro-test","Lin","Player","done","Original","KOJO_TEST");
        var macroSettings=new AiSettings { ProtectStory=false, Model="test" };
        var macroMessages=(List<Dictionary<string,object>>)macroPreset.Build(macroSnapshot,macroSettings,false)["messages"];
        Check(macroPreset.Warnings.Count==0 && (string)macroMessages[1]["content"]=="A9B2","portable comments, trim, prompt variables and independent randomness compose correctly");
        Check(new Preset(macroPreset.ApplyEntries(macroPreset.Entries())).Entries()[0].Content.Contains("{{// notes"),"portable export retains source comments");
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        byte[] header=System.Text.Encoding.GetEncoding(936).GetBytes("; 列表位置缓存\n#DIM LIST_POS\n");
        Check(EncodingHandler.ReadAllLinesFromBytes(header)[1]=="#DIM LIST_POS" && EncodingHandler.DetectEncoding(header).CodePage==936,"portable mixed GBK header preserves the declaration");
        using(var stream=new MemoryStream(header)) Check(EncodingHandler.DetectEncoding(stream).CodePage==936 && stream.Position==0,"portable stream detection preserves position");
        string bigPreset="{\"metadata\":\""+new string('文',1200000)+"\",\"prompts\":[{\"identifier\":\"a\",\"content\":\"FIRST\"},{\"identifier\":\"b\",\"content\":\"SECOND\"}]}";
        using(var stream=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bigPreset))) Check(Preset.ReadFile(stream)==bigPreset,"portable import accepts UTF-8 presets above 2 MB");
        var bigSettings=new AiSettings { ProtectStory=false, PresetJson=bigPreset };
        Check(AiSettings.Parse(Json.Codec().Serialize(bigSettings.ToDictionary(true))).PresetJson==bigPreset,"portable settings roundtrip handles escaped multi-megabyte presets");
        var big=new Preset(bigPreset); var entries=big.Entries(); entries[0].Enabled=false; entries.Reverse();
        var selected=new Preset(big.ApplyEntries(entries));
        string request=Json.Codec().Serialize(selected.Build(new DialogueSnapshot("test","Lin","Player","done","Original","KOJO_TEST"),new AiSettings { ProtectStory=false, Model="test" },false));
        Check(request.Contains("SECOND") && !request.Contains("FIRST") && selected.Entries()[0].Id=="b","portable edited selection determines the actual request");
        string game=Path.Combine(root,"portable","Tests","game");
        Directory.CreateDirectory(game);
        foreach(string file in Directory.GetFiles(Path.Combine(root,"demo"),"*",SearchOption.AllDirectories))
        {
            string dest=Path.Combine(game,Path.GetRelativePath(Path.Combine(root,"demo"),file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file,dest,true);
        }
        using var h=new LoaderTestHarness(game);
        await h.InitializeAsync();
        h.Console._state.process=h.Process; h.Console.RunEmueraProgram("");
        Check(!h.Console.IsGameExited,"portable engine loads the demo");
        h.Console.RunEmueraProgram("1");
        var f=h.Console.AiReadDialogue();
        Check(f!=null && f.Original.Contains("林澄"),"portable KOJO observation captures original text");
        var frame=f!;
        long[] flags=h.VEvaluator.VariableData.DataIntegerArray[(int)(VariableCode.FLAG & VariableCode.__LOWERCASE__)];
        Check(flags[0]==1 && flags[1]==1,"game and original dialogue each execute once");
        var settings=new AiSettings { ProtectStory=false, Enabled=true,AutoGenerate=false,Model="test",PresetJson=File.ReadAllText(Path.Combine(root,"presets","ERA-dialogue.json")) };
        var client=new Client(); using var c=new AiController(settings,client);
        c.SetEvent(new DialogueSnapshot(frame.EventId,frame.Character,frame.User,frame.Facts,frame.Original,frame.Hook));
        string before=State(h); long lines=h.Console.LineCount;
        _=macroPreset.Build(macroSnapshot,macroSettings,false); _=macroPreset.Build(macroSnapshot,macroSettings,true);
        Check(State(h)==before,"preset variable and random macros leave the complete engine save/RNG state unchanged");
        await c.GenerateAsync(false); await c.GenerateAsync(true); await c.GenerateAsync(true);
        Check(client.Calls==3 && c.Text=="生成的文字","shared preset and generation pipeline works");
        Check(client.Last.Contains("第 2 次重新生成") && client.Last.Contains("上一版正文") && client.Last.Contains("生成的文字"),"portable regeneration includes attempt and previous response without accumulating history");
        var compareRows=c.Log.ReadRecent().Split(Environment.NewLine).Select(x=>Json.Object(Json.Codec().DeserializeObject(x))).Where(x=>Json.Text(x,"stage")=="dialogue.compare").ToArray();
        Check(Json.Bool(compareRows.Last(),"exactPrevious") && c.Status.Contains("上一版"),"portable log and status explicitly flag repeated AI text");
        Check(c.Log.ReadRecent().Contains("dialogue.original") && c.Log.ReadRecent().Contains("dialogue.result"),"portable log includes original and accepted AI body");
        Check(State(h)==before && h.Console.LineCount==lines,"regenerations preserve complete portable engine save state and LINECOUNT");
        client.Reply=_=>throw new HttpRequestException("offline"); await c.GenerateAsync(true);
        Check(c.Text==frame.Original && State(h)==before,"offline fallback preserves original and engine state");
        var delayed=new TaskCompletionSource<string>(); client.Reply=_=>delayed.Task;
        Task stale=c.GenerateAsync(true); c.Reset(); delayed.SetResult("late"); await stale;
        Check(c.Current==null && c.Text=="","stale reply after reset is discarded");
        h.Console.RunEmueraProgram("4"); var redraw=h.Console.AiReadDialogue();
        Check(redraw!=null && redraw.Original==frame.Original && redraw.EventId!=frame.EventId && flags[0]==1 && flags[1]==1,"redraw preserves text and settlement while a different call chain invalidates the old event identity");
        Check(h.VEvaluator.SaveTo(8,"roundtrip"),"portable save writes");
        flags[0]=99; Check(h.VEvaluator.LoadFrom(8),"portable save loads");
        Check(flags[0]==1 && h.Console.AiReadDialogue()==null,"load restores game and invalidates AI capture");
        h.Console.RunEmueraProgram("5"); var pretrain=h.Console.AiReadDialogue();
        Check(pretrain?.Hook=="PRITRAIN_MESSAGE" && pretrain.LineIndices.Length>0 && flags[2]==1,"portable pre-training dialogue captures its nested output and presentation indices once");
        var waiting=new TaskCompletionSource<string>(); client.Reply=_=>waiting.Task;
        c.SetEvent(new DialogueSnapshot(pretrain!.EventId,pretrain.Character,pretrain.User,pretrain.Facts,pretrain.Original,pretrain.Hook));
        Task generation=c.GenerateAsync(false);
        Check(c.Busy && c.Text=="" && c.DisplayMode=="pending","portable AI-first waiting hides fallback text");
        waiting.SetException(new HttpRequestException("portable offline test")); await generation;
        Check(c.Text==pretrain.Original && c.DisplayMode=="original" && c.Log.ReadRecent().Contains("request.failed"),"portable failure reveals original and records diagnostics");
        Console.WriteLine("ALL PASSED: "+count);
    }
}
