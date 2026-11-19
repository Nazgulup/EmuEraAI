using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MinorShift.Emuera;
using MinorShift.Emuera.AI;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Sub;

internal static class Tests
{
    static int count;
    static void Check(bool value,string name) { if(!value) throw new Exception("FAIL: "+name); count++; Console.WriteLine("PASS: "+name); }
    static AiSettings Settings() { return new AiSettings { ProtectStory=false, Enabled=true,AutoGenerate=false,Model="test",Endpoint="http://localhost:17777/v1" }; }
    static DialogueSnapshot Snapshot(string id="event-1") { return new DialogueSnapshot(id,"Lin","Player","Action=1; outcome=done","Original dialogue","KOJO_TEST"); }
    sealed class Client : IAiClient
    {
        public int Calls; public string Last; public Func<CancellationToken,Task<string>> Reply=ct=>Task.FromResult("Generated dialogue");
        public Task<string> GenerateAsync(AiSettings s,Dictionary<string,object> body,CancellationToken ct) { Calls++; Last=Json.Codec().Serialize(body); return Reply(ct); }
    }
    sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode Code=HttpStatusCode.OK; public string Body=""; public bool Delay;
        public string Request,Authorization; public Uri Address; public HttpMethod Method;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,CancellationToken cancellation)
        {
            Address=message.RequestUri; Method=message.Method; Authorization=message.Headers.Authorization==null ? "" : message.Headers.Authorization.ToString(); Request=message.Content==null ? "" : await message.Content.ReadAsStringAsync();
            if(Delay) await Task.Delay(10000,cancellation);
            return new HttpResponseMessage(Code) { Content=new StringContent(Body) };
        }
    }
    static string Response(string content,string finish="stop") { return Json.Codec().Serialize(new { choices=new[]{new { finish_reason=finish,message=new { content=content } }} }); }
    static async Task Core(string root)
    {
        var client=new Client(); var settings=Settings();
        using(var controller=new AiController(settings,client))
        {
            var snapshot=Snapshot(); controller.SetEvent(snapshot);
            await controller.GenerateAsync(false); string first=client.Last;
            await controller.GenerateAsync(true);
            Check(client.Calls==2 && first!=client.Last && client.Last.Contains("Generated dialogue") && client.Last.Contains(snapshot.Facts) && controller.Current==snapshot,"regenerate changes its instruction and includes the previous reply while preserving immutable facts");
            controller.RestoreOriginal(); Check(controller.Text==snapshot.Original,"restore exact original");
            client.Reply=ct=>{ throw new HttpRequestException("offline"); };
            await controller.GenerateAsync(true); Check(controller.Text==snapshot.Original && !controller.Busy,"network failure falls back");
            var pending=new TaskCompletionSource<string>(); client.Reply=ct=>pending.Task;
            Task stale=controller.GenerateAsync(true); controller.SetEvent(Snapshot("event-2")); pending.SetResult("STALE"); await stale;
            Check(controller.Text=="" && controller.DisplayMode=="waiting" && controller.Current.EventId=="event-2","late response cannot overwrite another event or reveal its fallback early");
            pending=new TaskCompletionSource<string>(); client.Reply=ct=>pending.Task;
            stale=controller.GenerateAsync(true); controller.RestoreOriginal(); pending.SetResult("CANCELLED"); await stale;
            Check(controller.Text=="Original dialogue","cancel ignores provider that disregards cancellation");
            client.Reply=ct=>Task.FromResult("ok"); controller.Suspend(); int calls=client.Calls; await controller.GenerateAsync(true);
            Check(client.Calls==calls,"cannot regenerate while interpreter is running");
            controller.SetEvent(Snapshot("event-2"));
        }
        foreach(var test in new[]{Tuple.Create(200,Response("Hello"),true),Tuple.Create(401,"{}",false),Tuple.Create(429,"{}",false),Tuple.Create(500,"{}",false),Tuple.Create(200,"bad json",false),Tuple.Create(200,Response(""),false),Tuple.Create(200,Response("partial","length"),false),Tuple.Create(200,Response("FLAG:0 = 9"),false)})
        {
            var handler=new Handler { Code=(HttpStatusCode)test.Item1,Body=test.Item2 };
            using(var transport=new AiClient(handler)) using(var controller=new AiController(settings,transport))
            { controller.SetEvent(Snapshot()); await controller.GenerateAsync(false); Check((controller.Text=="Hello")==test.Item3 && (test.Item3 || controller.Text=="Original dialogue"),"HTTP/response fallback "+test.Item1+" "+test.Item2.Substring(0,Math.Min(25,test.Item2.Length))); Check(handler.Address.AbsolutePath=="/v1/chat/completions","endpoint normalized"); }
        }
        var slow=new Handler { Delay=true }; var timeoutSettings=Settings(); timeoutSettings.TimeoutSeconds=2;
        using(var c=new AiController(timeoutSettings,new AiClient(slow))) { c.SetEvent(Snapshot()); await c.GenerateAsync(false); Check(c.Text=="Original dialogue" && !c.Busy,"timeout falls back"); }
        var preset=new Preset(File.ReadAllText(Path.Combine(root,"presets","ERA-dialogue.json"),Encoding.UTF8));
        string requestJson=Json.Codec().Serialize(preset.Build(Snapshot(),settings,false));
        Check(requestJson.Contains("Lin") && !requestJson.Contains("{{char}}"),"SillyTavern preset + macros");
        string custom="{\"prompts\":[{\"identifier\":\"off\",\"content\":\"HIDDEN\"},{\"identifier\":\"a\",\"content\":\"AFTER\",\"role\":\"system\",\"injection_position\":1,\"injection_depth\":0},{\"identifier\":\"b\",\"content\":\"BEFORE\",\"role\":\"user\",\"injection_position\":1,\"injection_depth\":1}],\"prompt_order\":[{\"character_id\":100001,\"order\":[{\"identifier\":\"off\",\"enabled\":false},{\"identifier\":\"a\",\"enabled\":true},{\"identifier\":\"b\",\"enabled\":true}]}]}";
        var body=new Preset(custom).Build(Snapshot(),settings,false);
        var messages=(List<Dictionary<string,object>>)body["messages"];
        Check(messages.Count==4 && (string)messages[1]["content"]=="BEFORE" && (string)messages[3]["content"]=="AFTER","disabled blocks and depth injection");
        bool rejected=false; try { new Preset("{\"prompts\":[{\"identifier\":\"a\",\"content\":\"{{setglobalvar::FLAG::9}}\"}]}").Build(Snapshot(),settings,false); } catch(FormatException) { rejected=true; }
        Check(rejected,"unsupported global state macros rejected");
        MacroCompatibility();
        var characterSettings=Settings(); characterSettings.CharacterDescription="CHARACTER_DETAIL"; characterSettings.Personality="CHARACTER_VOICE";
        string defaults=Json.Codec().Serialize(new Preset("").Build(Snapshot(),characterSettings,false));
        Check(defaults.Contains("CHARACTER_DETAIL") && defaults.Contains("CHARACTER_VOICE"),"default prompt includes character settings");
        using(var autoController=new AiController(new AiSettings { ProtectStory=false, Enabled=true,AutoGenerate=true,Model="test" },new Client()))
        {
            var original=Snapshot(); autoController.SetEvent(original); string reply=autoController.Text;
            autoController.Suspend(); autoController.SetEvent(Snapshot());
            Check(autoController.Current==original && autoController.Text==reply,"redraw preserves the same immutable event and reply");
        }
        string config=Path.Combine(root,"tests","work-settings.json"); settings.ApiKey="test-only-key"; settings.Save(config); string warning;
        var loaded=AiSettings.Load(config,out warning); Check(!File.ReadAllText(config).Contains("test-only-key") && loaded.ApiKey==settings.ApiKey,"API key encrypted at rest");
        await ModelAndPresetTests(root);
        await PriorityAndLogTests(root);
        await StyleAndBudgetTests(root);
    }
    static async Task StyleAndBudgetTests(string root)
    {
        var settings=Settings(); settings.Endpoint="https://api.deepseek.com/v1";
        settings.OutputTokens=2048;
        var preset=new Preset("{\"prompts\":[],\"openai_max_tokens\":400}");
        var body=preset.Build(Snapshot(),settings,false);
        Check(Json.Int(body,"max_tokens")==2048 && Json.Text(Json.Object(body["thinking"]),"type")=="disabled","DeepSeek auto disables thinking and UI output budget is authoritative");
        settings.UsePresetOutputTokens=true;
        Check(Json.Int(preset.Build(Snapshot(),settings,false),"max_tokens")==400,"explicit preset output budget is honored");
        settings.ThinkingMode="enabled";
        Check(Json.Text(Json.Object(preset.Build(Snapshot(),settings,false)["thinking"]),"type")=="enabled","explicit thinking mode overrides automatic mode");
        settings.ThinkingMode="auto"; settings.Endpoint="https://example.invalid/v1";
        Check(!preset.Build(Snapshot(),settings,false).ContainsKey("thinking"),"other providers receive no automatic vendor-specific field");
        settings.ThinkingMode="provider"; settings.Endpoint="https://api.deepseek.com/v1";
        Check(!preset.Build(Snapshot(),settings,false).ContainsKey("thinking"),"provider-default mode can retain provider behavior");
        var files=new Dictionary<string,byte[]> {
            {"CSV/GameBase.csv",Encoding.UTF8.GetBytes("TITLE,Neutral Journey")},
            {"ERB/KOJO_A.ERB",Encoding.GetEncoding(932).GetBytes("@KOJO_A\nFLAG:0 += 99\nPRINTL 「お帰りなさい。お茶を用意しました」\nPRINTL [1] Menu entry\nPRINTL %CALLNAME:TARGET%は窓の外を見た。")},
            {"sav/Save00.sav",Encoding.UTF8.GetBytes("private save never read")}
        };
        var paths=GameStyleCorpus.Enumerate(dir=>dir=="" ? new[]{"ERB","CSV","sav"} : new string[0],dir=>files.Keys.Where(p=>p.StartsWith(dir+"/"))).ToArray();
        var corpus=GameStyleCorpus.Read("test",paths,p=>new MemoryStream(files[p]),bytes=> { using(var stream=new MemoryStream(bytes)) using(var reader=new StreamReader(stream,GameTextEncoding.Detect(stream))) return reader.ReadToEnd(); });
        Check(paths.Length==2 && corpus.Lines==2 && corpus.Samples.Contains("お帰り") && !corpus.Samples.Contains("FLAG") && !corpus.Samples.Contains("Menu entry") && corpus.Samples.Contains("〈动态字段〉"),"style scanner reads Japanese dialogue, excludes saves/menu/code and never evaluates macros");
        var changed=GameStyleCorpus.Read("different",paths,p=>new MemoryStream(files[p]),bytes=>Encoding.UTF8.GetString(bytes));
        Check(changed.Fingerprint!=corpus.Fingerprint,"style cache fingerprint changes with decoded source content");
        string cache=Path.Combine(root,"tests","style-cache-"+Guid.NewGuid().ToString("N"));
        var fake=new Client { Reply=ct=>Task.FromResult("简短对话与第三人称叙述交替，称呼沿用当前原文。") };
        settings=Settings();
        using(var service=new GameStyleService(()=>corpus,cache,fake,new AiLog(),()=>{})) {
            string summary=await service.PrepareAsync(settings);
            Check(fake.Calls==1 && summary.Contains("第三人称") && fake.Last.Contains("不续写故事"),"load-time API request summarizes writing style only");
            await service.PrepareAsync(settings);
            Check(fake.Calls==1,"repeated generations reuse the same style task");
        }
        using(var service=new GameStyleService(()=>corpus,cache,fake,new AiLog(),()=>{})) { await service.PrepareAsync(settings); Check(fake.Calls==1,"style summary survives reload through fingerprinted cache"); }
        fake.Reply=ct=>{ throw new HttpRequestException("style offline"); };
        using(var service=new GameStyleService(()=>corpus,null,fake,new AiLog(),()=>{})) { string text=await service.PrepareAsync(settings); Check(text==corpus.LocalSummary,"failed style analysis falls back to local summary"); }
        settings.GameStyle="STYLE-REFERENCE";
        var opening=new DialogueSnapshot("start","Lin","Player","before action","Original greeting","PRITRAIN_MESSAGE");
        string request=Json.Codec().Serialize(new Preset("").Build(opening,settings,false));
        Check(request.Contains("STYLE-REFERENCE") && request.Contains("行动开始前") && request.Contains("不是当前事件事实"),"opening prompt uses style as reference and remains at the pre-action stage");
        settings.UseGameStyle=false;
        Check(!Json.Codec().Serialize(new Preset("").Build(opening,settings,false)).Contains("STYLE-REFERENCE"),"style analysis can be disabled");
    }
    static async Task PriorityAndLogTests(string root)
    {
        var pending=new TaskCompletionSource<string>(); var fake=new Client { Reply=ct=>pending.Task }; var settings=Settings(); settings.AutoGenerate=true;
        using(var controller=new AiController(settings,fake)) {
            controller.SetEvent(Snapshot());
            Check(controller.Busy && controller.Text=="" && controller.DisplayMode=="pending","automatic AI generation does not flash the original while pending");
            pending.SetException(new HttpRequestException("offline diagnostic"));
            for(int i=0;i<100 && controller.Busy;i++) await Task.Delay(10);
            Check(controller.DisplayMode=="original" && controller.Text==Snapshot().Original,"original appears only after definitive AI failure");
            Check(controller.Log.ReadRecent().Contains("request.failed") && controller.Log.ReadRecent().Contains("offline diagnostic"),"generation log explains the fallback cause");
        }
        string logPath=Path.Combine(root,"tests","diagnostic.log");
        var log=new AiLog(logPath); settings=Settings(); settings.ApiKey="secret-should-never-appear";
        using(var controller=new AiController(settings,new AiClient(new Handler { Code=HttpStatusCode.Unauthorized,Body="{\"error\":{\"message\":\"invalid key secret-should-never-appear\"}}" }),log)) {
            controller.SetEvent(Snapshot()); await controller.GenerateAsync(false);
            string records=log.ReadRecent();
            Check(records.Contains("http.error") && records.Contains("401") && records.Contains("invalid key") && !records.Contains(settings.ApiKey),"HTTP error details are logged with the API key redacted");
            Check(records.Contains(Snapshot().Original) && !records.Contains(Snapshot().Facts),"diagnostics record cached original but omit complete prompt and game facts");
            string exported=Path.Combine(root,"tests","diagnostic-export.log"); log.Export(exported);
            Check(File.ReadAllText(exported)==records && File.ReadAllText(logPath).Contains("request.failed"),"generation logs persist and can be exported");
        }
        await DialogueLogTests(root);
    }
    static Dictionary<string,object>[] LogRows(AiLog log,string stage)
    { return log.ReadRecent().Split(new[]{Environment.NewLine},StringSplitOptions.RemoveEmptyEntries).Select(line=>Json.Object(Json.Codec().DeserializeObject(line))).Where(row=>Json.Text(row,"stage")==stage).ToArray(); }
    static async Task DialogueLogTests(string root)
    {
        var settings=Settings(); settings.ApiKey="secret-in-dialogue";
        string path=Path.Combine(root,"tests","dialogue-comparison.log"); File.WriteAllText(path,"");
        var log=new AiLog(path); var handler=new Handler { Body=Response("  Original dialogue\r\n") };
        using(var controller=new AiController(settings,new AiClient(handler),log)) {
            controller.SetEvent(Snapshot()); await controller.GenerateAsync(false);
            Check(Json.Text(LogRows(log,"response.content").Last(),"text")=="  Original dialogue\r\n","log retains exact API content before trimming or validation");
            Check(Json.Bool(LogRows(log,"dialogue.compare").Last(),"exactOriginal") && controller.Status.Contains("原口上"),"copied original is identified explicitly in log and status");
            await controller.GenerateAsync(true);
            Check(Json.Bool(LogRows(log,"dialogue.compare").Last(),"exactPrevious") && controller.Status.Contains("上一版"),"repeated regeneration is compared against prior accepted AI reply");
            Check(!Json.Bool(LogRows(log,"request.fingerprint").Last(),"sameAsPreviousRequest") && handler.Request.Contains("上一版正文"),"regeneration sends a changed request with previous text, not a cached display");
            string nextPreview=Json.Codec().Serialize(controller.PreviewRequest());
            handler.Body=Response("Different wording"); await controller.GenerateAsync(true);
            Check(handler.Request==nextPreview && !Json.Bool(LogRows(log,"dialogue.compare").Last(),"exactOriginal") && !Json.Bool(LogRows(log,"dialogue.compare").Last(),"exactPrevious"),"preview matches next regeneration and distinct text is distinguished from copies");
            handler.Body=Response("Original\n dialogue"); await controller.GenerateAsync(true);
            var comparison=LogRows(log,"dialogue.compare").Last();
            Check(!Json.Bool(comparison,"exactOriginal") && Json.Bool(comparison,"whitespaceEquivalentOriginal"),"whitespace-only changes are identified separately from exact copies");
            handler.Body=Response("visible truncated content","length"); await controller.GenerateAsync(true);
            Check(controller.DisplayMode=="original" && Json.Text(LogRows(log,"response.content").Last(),"text")=="visible truncated content","rejected or truncated response is logged before fallback");
            controller.SetEvent(Snapshot("new-event")); handler.Body=Response("New reply"); await controller.GenerateAsync(true);
            Check(!Json.Bool(LogRows(log,"dialogue.compare").Last(),"hasPrevious") && !handler.Request.Contains("Different wording"),"previous reply and attempt state do not leak into another event");
        }
        string longText=new string('日',3499)+"🙂"+new string('文',3500)+"secret-in-dialogue"+new string('末',900);
        log.WriteText("test.full-text","long","long text",longText);
        string joined=string.Concat(LogRows(log,"test.full-text").Select(row=>Json.Text(row,"text")));
        Check(joined==longText.Replace(settings.ApiKey,"[REDACTED]") && !File.ReadAllText(path).Contains(settings.ApiKey),"full long CJK/emoji dialogue survives segmented logging and secret redaction");
        string exported=Path.Combine(root,"tests","dialogue-comparison-export.log"); log.Export(exported);
        Check(File.ReadAllText(exported)==log.ReadRecent(),"body and comparison records survive export together");
        var budget=Settings(); budget.PresetJson="{\"openai_max_context\":2048}";
        bool exceeded=false; try { new Preset(budget.PresetJson).Build(Snapshot(),budget,true,new string('文',2000),1); } catch(FormatException) { exceeded=true; }
        Check(exceeded,"regeneration reference is included in the context budget check");
    }
    static async Task ApiProbe(string root,string settingsPath)
    {
        string warning; var settings=AiSettings.Load(settingsPath,out warning);
        if(warning.Length>0) throw new Exception(warning);
        var log=new AiLog(Path.Combine(root,"dist","ai","logs","generation.log"));
        using(var controller=new AiController(settings,new AiClient(),log)) Console.WriteLine(await controller.ProbeAsync());
        Console.WriteLine("Log: "+log.FilePath);
    }
    static Preset Prompt(params string[] blocks)
    {
        return new Preset(Json.Codec().Serialize(new Dictionary<string,object> { {"prompts",blocks.Select((content,i)=>(object)new Dictionary<string,object>{{"identifier","block-"+i},{"content",content}}).ToArray()} }));
    }
    static List<Dictionary<string,object>> Messages(Preset preset) { return (List<Dictionary<string,object>>)preset.Build(Snapshot(),Settings(),false)["messages"]; }
    static void MacroCompatibility()
    {
        foreach(var item in new[]{
            Tuple.Create("A{{// 2026/10/17 - b20\r\n一点点针对v4.1的重构}}B {{char}}","AB Lin"),
            Tuple.Create("A{{ // note {{setvar::FLAG::99}} {{unknown}} }}B","AB"),
            Tuple.Create("A{{ // }}\nnotes {{unknown}} {{ // }}nested{{ /// }}\n{{ /// }}B","AB"),
            Tuple.Create("A\r\n\n{{ trim }}\n\r\nB","AB")
        }) {
            var p=Prompt(item.Item1); var m=Messages(p);
            Check(p.Warnings.Count==0 && (string)m[1]["content"]==item.Item2,"comment/trim compatibility preserves surrounding text");
            Check(new Preset(p.ApplyEntries(p.Entries())).Entries()[0].Content==item.Item1,"preset editing/export preserves source comments verbatim");
        }
        Check(Messages(Prompt("{{// notes only}} ")).Count==2,"comment-only blocks emit no API message");
        string agents=Json.Codec().Serialize(new Dictionary<string,object>{{"prompts",new[]{"agentSystemPrompt","agentTask","agentResults"}.Select(id=>(object)new Dictionary<string,object>{{"identifier",id},{"marker",true},{"content",""}}).ToArray()}});
        Check(Messages(new Preset(agents)).Count==2,"empty agent extension markers do not block ordinary dialogue");
        bool agentRejected=false; try { Messages(new Preset(agents.Replace("\"content\":\"\"","\"content\":\"required instructions\""))); } catch(FormatException) { agentRejected=true; }
        Check(agentRejected,"nonempty agent extension instructions are not silently discarded");
        var variables=Prompt("{{setvar::FLAG::9}}{{setvar::voice::{{char}}}}","{{addvar::FLAG::2}}{{getvar::FLAG}} / {{getvar::voice}}");
        Check((string)Messages(variables)[1]["content"]=="11 / Lin","local preset variables expand nested macros across ordered blocks");
        Check(Messages(Prompt("{{getvar::FLAG}}")).Count==2,"preset variables never leak to another request");
        var random=Prompt("{{random::A::B}}/{{roll:2d1+3}}/{{roll 1}}/{{random:a\\,b}}");
        string first=(string)Messages(random)[1]["content"];
        Check((first=="A/5/1/a,b" || first=="B/5/1/a,b") && first==(string)Messages(random)[1]["content"],"prompt-only randomness is stable for regeneration and supports dice/comma syntax");
        Check(((string)Messages(Prompt("{{lastusermessage}}"))[1]["content"]).Contains(Snapshot().Facts),"lastusermessage refers to the current read-only event");
        foreach(string invalid in new[]{"{{// unclosed", "{{ // }}unclosed block", "{{unsupported}}", "{{roll:999999d6}}"}) {
            bool failed=false; try { Messages(Prompt(invalid)); } catch(FormatException) { failed=true; }
            Check(failed,"malformed/unsupported macros still fail explicitly");
        }
    }
    static void SavedPresetSmoke(string path)
    {
        // Deserialize without loading/unprotecting the API key; never send this request.
        var settings=Json.Codec().Deserialize<AiSettings>(File.ReadAllText(path,Encoding.UTF8));
        var preset=new Preset(settings.PresetJson);
        foreach(bool regenerate in new[]{false,true}) {
            var request=preset.Build(Snapshot(),settings,regenerate);
            Check(((List<Dictionary<string,object>>)request["messages"]).Count>=2,"saved preset compiles offline without exposing its contents (regenerate="+regenerate+")");
        }
    }
    static async Task ModelAndPresetTests(string root)
    {
        var settings=Settings(); settings.ApiKey="model-list-test-key";
        var handler=new Handler { Body="{\"data\":[{\"id\":\"z-model\"},{\"id\":\"a-model\"},{\"id\":\"z-model\"},{\"id\":\"\"}]}" };
        using(var client=new AiClient(handler)) {
            var models=await client.ListModelsAsync(settings,CancellationToken.None);
            Check(models.SequenceEqual(new[]{"a-model","z-model"}) && handler.Method==HttpMethod.Get && handler.Address.AbsolutePath=="/v1/models" && handler.Authorization=="Bearer model-list-test-key" && handler.Request=="","model listing uses authenticated GET and deduplicates IDs");
        }
        Check(AiClient.GetModelsEndpoint("https://example.com/proxy/v1/chat/completions/").AbsolutePath=="/proxy/v1/models","model list accepts a full completion endpoint");
        foreach(var failure in new[]{Tuple.Create(401,"{}"),Tuple.Create(404,"{}"),Tuple.Create(302,"{}"),Tuple.Create(200,"not json"),Tuple.Create(200,"{\"data\":[]}")}) {
            bool failed=false; using(var client=new AiClient(new Handler { Code=(HttpStatusCode)failure.Item1,Body=failure.Item2 })) try { await client.ListModelsAsync(settings,CancellationToken.None); } catch(Exception) { failed=true; }
            Check(failed,"model listing handles unavailable/invalid response "+failure.Item1+" "+failure.Item2);
        }
        using(var cancellation=new CancellationTokenSource(30)) using(var client=new AiClient(new Handler { Delay=true })) {
            bool cancelled=false; try { await client.ListModelsAsync(settings,cancellation.Token); } catch(OperationCanceledException) { cancelled=true; }
            Check(cancelled,"model listing cancels independently of the interpreter");
        }
        var data=new Dictionary<string,object> {
            {"extra",new Dictionary<string,object>{{"large",new string('文',1200000)}}},
            {"prompts",new object[]{new Dictionary<string,object>{{"identifier","a"},{"name","First"},{"content","FIRST"}},new Dictionary<string,object>{{"identifier","b"},{"content","SECOND"}},new Dictionary<string,object>{{"identifier","off"},{"content","{{unsupported}}"}}}},
            {"prompt_order",new object[]{
                new Dictionary<string,object> {
                    {"character_id",100001},
                    {"order",new object[]{new Dictionary<string,object>{{"identifier","a"},{"enabled",true},{"extraOrder",42}},new Dictionary<string,object>{{"identifier","b"},{"enabled",true}}}}
                },
                new Dictionary<string,object>{{"character_id",7},{"order",new object[0]}}
            }}
        };
        string raw=Json.Codec().Serialize(data);
        using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(raw))) raw=Preset.ReadFile(stream);
        Check(Encoding.UTF8.GetByteCount(raw)>2*1024*1024 && new Preset(raw).Entries().Count==3,"imports a multi-megabyte UTF-8 preset including unknown fields");
        var preset=new Preset(raw); var entries=preset.Entries();
        Check(!entries[2].Enabled,"unlisted prompt stays available but disabled");
        var first=entries[0]; entries.RemoveAt(0); entries.Insert(1,first);
        string edited=preset.ApplyEntries(entries);
        var messages=(List<Dictionary<string,object>>)new Preset(edited).Build(Snapshot(),settings,false)["messages"];
        Check((string)messages[1]["content"]=="SECOND" && (string)messages[2]["content"]=="FIRST","entry order controls the actual request");
        entries[0].Enabled=false; edited=new Preset(edited).ApplyEntries(entries);
        string request=Json.Codec().Serialize(new Preset(edited).Build(Snapshot(),settings,false));
        Check(!request.Contains("SECOND") && !request.Contains("unsupported") && request.Contains("FIRST"),"unchecked entries are not expanded or sent to the model");
        var exported=Json.Object(Json.Codec().DeserializeObject(edited));
        Check(Json.Text(Json.Object(Json.Get(exported,"extra")),"large").Length==1200000 && Json.Array(Json.Get(exported,"prompt_order")).Length==2 && edited.Contains("extraOrder"),"entry edits preserve unknown data, order metadata and other profiles");
        settings.PresetJson=edited;
        string path=Path.Combine(root,"tests","large-settings.json"); settings.Save(path); string warning;
        var loaded=AiSettings.Load(path,out warning);
        Check(warning=="" && loaded.PresetJson==edited && loaded.ApiKey==settings.ApiKey,"large preset and encrypted key survive settings save/reload");
    }
    static void SetProgram(string key,object value) { typeof(Program).GetProperty(key,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).SetValue(null,value,null); }
    static void ReaderCompatibility(string root)
    {
        GameTextEncoding.Select("auto");
        string folder=Path.Combine(root,"tests","encoding"); Directory.CreateDirectory(folder);
        string text=";测试\n{\nLOCAL:0 = 12,\n34,\n56\n}\nPRINTL 完成\n";
        foreach(var encoding in new Encoding[]{new UTF8Encoding(false),new UTF8Encoding(true),Encoding.Unicode,Encoding.BigEndianUnicode,Encoding.UTF32,new UTF32Encoding(true,true)})
        {
            string file=Path.Combine(folder,"unicode-"+encoding.CodePage+"-"+encoding.GetPreamble().Length+".ERB");
            File.WriteAllText(file,text,encoding);
            using(var reader=new EraStreamReader(false))
            {
                Check(reader.Open(file),"open Unicode script "+Path.GetFileName(file));
                var lines=new List<string>(); string line; while((line=reader.ReadLine())!=null) lines.Add(line);
                Check(lines.Count==7 && lines[0]==";测试" && lines[1]=="{" && lines[6]=="PRINTL 完成","Unicode decoding preserves text and physical newlines "+Path.GetFileName(file));
            }
            using(var reader=new EraStreamReader(false))
            {
                reader.Open(file); var logical=reader.ReadEnabledLine();
                Check(logical.RowString.Trim()=="LOCAL:0 = 12, 34, 56" && reader.LineNo==2,"multiline assignment remains one statement "+Path.GetFileName(file));
                Check(reader.ReadEnabledLine().RowString=="PRINTL 完成" && reader.LineNo==7 && reader.ReadEnabledLine()==null,"line numbers after continuation remain physical "+Path.GetFileName(file));
            }
        }
        Encoding previous=Config.Encode;
        try
        {
            foreach(int codepage in new[]{932,936})
            {
                Config.Encode=Encoding.GetEncoding(codepage);
                string textLegacy=new string(' ',9000)+"\nPRINTL "+(codepage==932 ? "日本語テスト、ｶﾀｶﾅ、表、ソ、①" : "中文测试")+"\n";
                string file=Path.Combine(folder,"legacy-"+codepage+".ERB"); File.WriteAllText(file,textLegacy,Config.Encode);
                using(var reader=new EraStreamReader(false))
                { reader.Open(file); Check(reader.ReadLine()==new string(' ',9000) && reader.ReadLine()==textLegacy.Split('\n')[1] && reader.ReadLine()==null,"configured legacy encoding survives an ASCII prefix beyond the detection buffer "+codepage); }
            }
        } finally { Config.Encode=previous; }
        string header=Path.Combine(folder,"mixed-gbk.ERH");
        File.WriteAllText(header,"; 列表位置缓存\n#DIM LIST_POS\n#DIM PREV_LIST_POS\n",Encoding.GetEncoding(936));
        using(var reader=new EraStreamReader(false))
        { reader.Open(header); Check(reader.ReadEnabledLine().RowString=="#DIM LIST_POS" && reader.ReadEnabledLine().RowString=="#DIM PREV_LIST_POS","automatic GBK detection preserves declarations after Chinese comments"); }
        try
        {
            string ambiguous=Path.Combine(folder,"ambiguous.ERB");
            File.WriteAllBytes(ambiguous,new byte[]{0xc2,0xa2,0x0a});
            GameTextEncoding.Select("shift-jis");
            using(var reader=new EraStreamReader(false))
            { reader.Open(ambiguous); Check(reader.ReadLine()=="ﾂ｢","explicit CP932 preserves halfwidth kana even when bytes are also valid UTF-8"); }
            foreach(var choice in new[]{Tuple.Create("euc-jp",51932),Tuple.Create("iso-2022-jp",50220)})
            {
                GameTextEncoding.Select(choice.Item1);
                string file=Path.Combine(folder,choice.Item1+".ERB");
                File.WriteAllText(file,"PRINTL 日本語テスト\n",Encoding.GetEncoding(choice.Item2));
                using(var reader=new EraStreamReader(false))
                { reader.Open(file); Check(reader.ReadLine()=="PRINTL 日本語テスト" && reader.ReadLine()==null,"explicit Japanese encoding "+choice.Item1); }
            }
        } finally { GameTextEncoding.Select("auto"); }
        foreach(string invalid in new[]{"}\n","{\nLOCAL=1\n"})
        {
            string file=Path.Combine(folder,"invalid.ERB"); File.WriteAllText(file,invalid,new UTF8Encoding(false));
            bool rejected=false;
            using(var reader=new EraStreamReader(false))
            { reader.Open(file); try { reader.ReadEnabledLine(); } catch(CodeEE) { rejected=true; } }
            Check(rejected,"invalid continuation is still rejected");
        }
    }
    static void StartupSmoke(string root,string game)
    {
        game=Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        string scratch=Path.GetFullPath(Path.Combine(root,"tests"))+Path.DirectorySeparatorChar;
        if(!game.StartsWith(scratch,StringComparison.OrdinalIgnoreCase)) throw new Exception("Smoke test requires a disposable game copy under tests.");
        GameTextEncoding.Select("auto");
        SetProgram("ExeDir",game); SetProgram("CsvDir",game+"CSV\\"); SetProgram("ErbDir",game+"ERB\\"); SetProgram("DatDir",game+"dat\\"); SetProgram("ContentDir",game+"resources\\"); SetProgram("ExeName","EmueraAI.Tests");
        ConfigData.Instance.LoadConfig(); Application.EnableVisualStyles();
        using(var window=new MainWindow())
        {
            ((System.Windows.Forms.Timer)typeof(MainWindow).GetField("timer",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window)).Stop();
            window.ShowInTaskbar=false; window.Opacity=0; window.Show();
            var console=(EmueraConsole)typeof(MainWindow).GetField("console",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);
            console.Initialize();
            for(int i=0;i<20 && !console.IsError && console.IsWaitingEnterKey;i++) console.PressEnterKey(false,"",false);
            var output=new StringBuilder(); console.GetDisplayStrings(output);
            File.WriteAllText(Path.Combine(root,"logs","game-startup.txt"),output.ToString(),Encoding.UTF8);
            Check(!console.IsError,"real game scripts load without a fatal parser error");
            Check(new[]{"LIST_POS","PREV_LIST_POS","PREV_PAGE","KAI_LIST"}.All(id=>GlobalStatic.IdentifierDictionary.GetVariableToken(id,null,false)!=null),"real mixed-encoding game registers list header variables");
            Check((ConsoleState)typeof(EmueraConsole).GetField("state",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(console)==ConsoleState.WaitInput && !console.IsWaitingEnterKey,"real game reaches its interactive menu");
            if(File.Exists(Path.Combine(game,"ERB","_AI_TARGET_TEST.ERB"))) Check(output.ToString().Contains("TARGET_SELECTOR_OK"),"original SELECT_TARGET runs through LIST_POS assignment and returns for an empty list");
            Directory.CreateDirectory(Path.Combine(root,"tests","screenshots"));
            Render(window,Path.Combine(root,"tests","screenshots","game-startup.png"));
            window.Close();
        }
    }
    static string StateHash()
    {
        string path=Path.Combine(Program.ExeDir,"state-check.bin");
        using(var stream=new FileStream(path,FileMode.Create,FileAccess.Write))
        { var writer=new EraBinaryDataWriter(stream); GlobalStatic.VEvaluator.SaveToStreamBinary(writer,"test"); writer.Close(); }
        using(var sha=SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));
    }
    static void Pump(Task task)
    { var until=DateTime.UtcNow.AddSeconds(15); while(!task.IsCompleted && DateTime.UtcNow<until) { Application.DoEvents(); Thread.Sleep(10); } if(!task.IsCompleted) throw new Exception("Test timed out"); task.GetAwaiter().GetResult(); }
    static void Integration(string root)
    {
        using(var webp=new WebPWrapper.WebP()) using(var pixel=new System.Drawing.Bitmap(2,2))
        { byte[] encoded=webp.EncodeLossless(pixel); using(var decoded=webp.Decode(encoded)) Check(decoded.Width==2 && decoded.Height==2,"original WebP native library roundtrip"); }
        string game=Path.Combine(root,"tests","game")+Path.DirectorySeparatorChar; Directory.CreateDirectory(game);
        foreach(string file in Directory.GetFiles(Path.Combine(root,"demo"),"*",SearchOption.AllDirectories))
        { string relative=file.Substring(Path.Combine(root,"demo").Length+1); string dest=Path.Combine(game,relative); Directory.CreateDirectory(Path.GetDirectoryName(dest)); File.Copy(file,dest,true); }
        string addressScript=Path.Combine(game,"ERB","DEMO.ERB");
        File.WriteAllText(addressScript,File.ReadAllText(addressScript).Replace("CASE 9","CASE 8\n    CALL AI_DIALOGUE_ADDRESS\nCASE 9")+"\n@AI_DIALOGUE_ADDRESS\nCALLNAME:TARGET = 琳琳\nMASTERNAME:TARGET = 阁下\nCSTR:TARGET:60 = 本小姐\nPRINTFORML %CALLNAME:TARGET%说：「%SELF_CALL(TARGET)%请%MASTERNAME:TARGET%看地图。」\nRETURN\n\n@SELF_CALL, ARG\n#FUNCTIONS\nFLAG:3 += 1\nRETURNF CSTR:ARG:60\n",Encoding.UTF8);
        File.WriteAllText(addressScript,File.ReadAllText(addressScript).Replace("CASE 9","CASE 6\n    CALL KOJO_DAILY\nCASE 7\n    CALL STORY_GATE\nCASE 9")+"\n@KOJO_DAILY\nPRINTL 旅人安静等待安排。\nRETURN\n@STORY_GATE\nCALL KOJO_DAILY\nRETURN\n",Encoding.UTF8);
        string locator=Path.Combine(game,"original.exe"); File.WriteAllText(locator,"locator-only fixture");
        Check(GameLocator.Resolve(locator)==game,"selecting original exe resolves sibling game files without executing it");
        GameEncodingPreferences.Save(game,"shift-jis");
        using(var launcher=new GameLauncher())
        {
            ((TextBox)typeof(GameLauncher).GetField("path",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(launcher)).Text=locator;
            var selected=(ComboBox)typeof(GameLauncher).GetField("encoding",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(launcher);
            Check(selected.SelectedIndex==Array.IndexOf(GameTextEncoding.Ids,"shift-jis") && GameEncodingPreferences.Get(game.ToUpperInvariant())=="shift-jis","launcher remembers a game's encoding when reopened through its exe or directory");
        }
        SetProgram("ExeDir",game); SetProgram("CsvDir",game+"CSV\\"); SetProgram("ErbDir",game+"ERB\\"); SetProgram("DatDir",game+"dat\\"); SetProgram("ContentDir",game+"resources\\"); SetProgram("ExeName","EmueraAI.Tests");
        ConfigData.Instance.LoadConfig(); Application.EnableVisualStyles();
        using(var window=new MainWindow())
        {
            ((System.Windows.Forms.Timer)typeof(MainWindow).GetField("timer",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window)).Stop();
            window.ShowInTaskbar=false; window.Opacity=0; window.Show();
            var console=(EmueraConsole)typeof(MainWindow).GetField("console",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);
            var fake=new Client { Reply=ct=>Task.FromResult("林澄：Generated dialogue") }; using(var controller=new AiController(Settings(),fake))
            {
                console.AttachAi(controller); console.Initialize();
                Check(!console.IsError,"demo loads in actual Emuera interpreter");
                console.PressEnterKey(false,"1",false);
                if(controller.Current==null) { var log=new StringBuilder(); console.GetDisplayStrings(log); Console.WriteLine(log.ToString()); }
                Check(controller.Current!=null,"actual KOJO output captured");
                var flags=GlobalStatic.VariableData.DataIntegerArray[(int)(VariableCode.FLAG & VariableCode.__LOWERCASE__)];
                Check(flags[0]==1 && flags[1]==1,"game action and original dialogue ran once");
                string state=StateHash(); long lines=console.LineCount;
                controller.ConfigureGameStyle(()=>new GameStyleCorpus { LocalSummary="Neutral dialogue style",Samples="A brief greeting before the journey.",Fingerprint="neutral-integration",Files=1,Lines=1 },null);
                var shown=(List<ConsoleDisplayLine>)typeof(EmueraConsole).GetField("displayLineList",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(console);
                Check(shown.Any(line=>line.AiSuppressed) && controller.Text=="","original console dialogue is hidden while AI is waiting");
                Check(console.AiDisplayLines().Any(line=>line.ToString().Contains("等待 AI")),"waiting placeholder appears inside the game transcript");
                var promptOnly=Prompt("{{setvar::FLAG::900}}{{addvar::FLAG::10}}{{getvar::FLAG}}/{{random::A::B}}/{{roll:1d6}}");
                promptOnly.Build(controller.Current,Settings(),false); promptOnly.Build(controller.Current,Settings(),true);
                Check(StateHash()==state,"preset variables named FLAG and prompt randomness leave all engine state unchanged");
                Pump(controller.GenerateAsync(false)); Pump(controller.GenerateAsync(true)); Pump(controller.GenerateAsync(true));
                Check(console.AiDisplayLines().Any(line=>line.ToString()=="林澄：Generated dialogue"),"successful AI reply is present in the main game transcript");
                Check(!console.AiDisplayLines().Any(line=>line.AiSuppressed),"main transcript excludes the replaced original lines");
                var menuLines=shown.Where(line=>line.Buttons.Any(b=>b.IsButton)).ToArray();
                Check(menuLines.All(line=>console.AiDisplayLines().Contains(line)),"AI layout retains the actual menu line and button identities");
                string longReply="林澄："+string.Concat(Enumerable.Repeat("春の光。普通的开场白。",120))+"\n[777] <b>literal text</b>";
                fake.Reply=ct=>Task.FromResult(longReply); Pump(controller.GenerateAsync(true));
                var aiRows=console.AiDisplayLines().Where(line=>!shown.Contains(line)).ToArray();
                Check(aiRows.Length>10 && string.Concat(aiRows.Select(line=>line.ToString()))==longReply.Replace("\n",""),"long CJK reply wraps completely into the main transcript");
                Check(aiRows.All(line=>line.Buttons.All(b=>!b.IsButton)) && !console.AiDisplayLines().Any(line=>line.ToString()=="林澄：Generated dialogue"),"regeneration replaces text and cannot create game buttons from markup");
                var layout=console.AiDisplayLines(); var menu=menuLines.Last(); var button=menu.Buttons.First(b=>b.IsButton);
                console.MoveMouse(new System.Drawing.Point(button.PointX+2,window.MainPicBox.Height-(layout.Count-layout.IndexOf(menu))*Config.LineHeight+Config.FontSize/2));
                Check(console.SelectingButton==button,"menu hit testing uses the projected rows after long AI text");
                Check(window.ScrollBar.Maximum==layout.Count && window.ScrollBar.Value==layout.Count,"scrollbar follows the actual AI transcript length");
                Check(StateHash()==state && console.LineCount==lines && flags[0]==1 && flags[1]==1,"three AI generations preserve complete serialized game state and LINECOUNT");
                Check(controller.StyleReport.Contains("Neutral dialogue style") && fake.Last.Contains("游戏风格参考资料"),"style analysis joins the request without changing the complete interpreter state");
                fake.Reply=ct=>{ throw new HttpRequestException(); }; Pump(controller.GenerateAsync(true));
                Check(controller.Text==controller.Current.Original && StateHash()==state,"failure fallback does not run original script twice");
                Check(!shown.Any(line=>line.AiSuppressed),"confirmed failure reveals original console lines without replacing them");
                Check(console.AiDisplayLines().SequenceEqual(shown),"failure restores the exact original main transcript");
                var deferred=new TaskCompletionSource<string>(); fake.Reply=ct=>deferred.Task;
                var originalEvent=controller.Current; var previousLines=shown.ToArray(); Task pendingRedraw=controller.GenerateAsync(true);
                console.PressEnterKey(false,"4",false);
                Check(controller.Current!=originalEvent && !controller.Busy && !previousLines.Any(line=>line.AiSuppressed),"a different call chain invalidates the pending reply even when the visible original is identical");
                controller.RestoreOriginal();
                deferred.SetException(new HttpRequestException("redraw fallback test")); Pump(pendingRedraw);
                Check(!shown.Any(line=>line.AiSuppressed) && flags[0]==1 && flags[1]==1,"failure restores every redraw without repeating settlement");
                RenderWindows(root,controller,window);
                Check(GlobalStatic.VEvaluator.SaveTo(7,"roundtrip"),"original save writer succeeds");
                flags[0]=99; Check(GlobalStatic.VEvaluator.LoadFrom(7),"original save reader succeeds");
                Check(flags[0]==1 && controller.Current==null,"load restores state and invalidates AI event");
                fake.Reply=ct=>Task.FromResult("林澄：AI pre-training greeting");
                var pretrainSettings=Settings(); pretrainSettings.AutoGenerate=true; controller.SetSettings(pretrainSettings);
                console.PressEnterKey(false,"5",false);
                Check(controller.Current!=null && controller.Current.Hook=="PRITRAIN_MESSAGE" && controller.Text=="林澄：AI pre-training greeting","pre-training nested dialogue triggers automatic AI generation");
                Check(flags[2]==1 && controller.Log.ReadRecent().Contains("PRITRAIN_MESSAGE"),"pre-training original script runs once and its trigger is logged");
                Check(console.AiDisplayLines().Any(line=>line.ToString()=="林澄：AI pre-training greeting"),"pre-training response returns directly to the game transcript");
                Directory.CreateDirectory(Path.Combine(root,"tests","screenshots"));
                Render(window,Path.Combine(root,"tests","screenshots","game-inline-ai.png"));
                controller.SetEvent(null);
                Check(console.AiDisplayLines().Any(line=>line.ToString()=="林澄：AI pre-training greeting"),"completed AI dialogue remains in game history when the current event ends");
                var disabled=Settings(); disabled.Enabled=false; controller.SetSettings(disabled);
                Check(console.AiDisplayLines().SequenceEqual(shown),"disabling AI restores all original transcript lines");
                console.AiReset(); controller.SetSettings(Settings());
                Check(console.AiDisplayLines().SequenceEqual(shown),"reset clears AI history without altering engine lines");
                string beforeProbe=StateHash(); Pump(controller.ProbeAsync());
                Check(StateHash()==beforeProbe,"API diagnostic probe never changes gameplay state");
                console.PressEnterKey(false,"8",false);
                Check(controller.Current.Addresses.Mask(controller.Current.Original).Contains("ERA_ADDR") && controller.Current.AddressValues.Any(p=>p.Key.StartsWith("SELF_CALL") && p.Value=="本小姐"),"Windows captures real SELF_CALL results and role-specific address variables");
                string addressState=StateHash();
                fake.Reply=ct=>Task.FromResult(controller.Current.Addresses.Mask("琳琳说：「本小姐带来地图，阁下请看。」"));
                Pump(controller.GenerateAsync(false)); Pump(controller.GenerateAsync(true));
                Check(controller.DisplayMode=="ai" && controller.Text=="琳琳说：「本小姐带来地图，阁下请看。」" && flags[3]==1 && StateHash()==addressState,"Windows restores address variables and regenerates without evaluating SELF_CALL twice");
                fake.Reply=ct=>Task.FromResult("琳琳说：「我带来地图，你请看。」"); Pump(controller.GenerateAsync(true));
                Check(controller.DisplayMode=="original" && controller.Text==controller.Current.Original && StateHash()==addressState,"Windows rejects changed personal pronouns and falls back without state changes");
                controller.ConfigureStoryPolicy(new StoryPolicy(()=>Directory.EnumerateFiles(Path.Combine(game,"ERB"),"*.ERB",SearchOption.AllDirectories).Select(p=>p.Substring(game.Length).Replace('\\','/')),p=>File.ReadAllText(Path.Combine(game,p))));
                var protectedSettings=Settings(); protectedSettings.ProtectStory=true; protectedSettings.UseGameStyle=false; controller.SetSettings(protectedSettings);
                console.PressEnterKey(false,"6",false); Pump(WaitStory(controller));
                int beforeStoryCalls=fake.Calls; Pump(controller.GenerateAsync(true));
                Check(!controller.CanGenerate && fake.Calls==beforeStoryCalls && controller.DisplayMode=="original","Windows preserves uncertain text with event flags in the caller, including direct regenerate");
                Pump(controller.SetStoryRuleAsync("allow","function")); fake.Reply=ct=>Task.FromResult("旅人收起地图，等待安排。"); string storyState=StateHash(); Pump(controller.GenerateAsync(true));
                Check(controller.DisplayMode=="ai" && StateHash()==storyState,"Windows function allow can generate without changing saved game state");
                console.PressEnterKey(false,"7",false); Pump(WaitStory(controller));
                Check(!controller.CanGenerate && controller.Story.Kind=="story","Windows full call chain protects a daily function inside a story despite its saved allow");
                Pump(controller.SetStoryRuleAsync("allow","event")); storyState=StateHash(); Pump(controller.GenerateAsync(true));
                Check(controller.DisplayMode=="ai" && StateHash()==storyState,"Windows explicit event allow is presentation-only");
                console.PressEnterKey(false,"7",false); Pump(WaitStory(controller));
                Check(!controller.CanGenerate && controller.Log.ReadRecent().Contains("story.decision"),"Windows event allow expires on repeated input and records the protection reason");
                DesktopTemplateChecks(root,game,console,controller,fake);
                controller.SetSettings(Settings());
                LauncherCloseChecks(window,console,controller,fake);
            }
            window.Close();
        }
    }
    static async Task WaitStory(AiController controller)
    { var until=DateTime.UtcNow.AddSeconds(10); while(!controller.StoryReady && DateTime.UtcNow<until) await Task.Delay(10); if(!controller.StoryReady) throw new Exception("Story check timed out"); }
    static void DesktopTemplateChecks(string root,string game,EmueraConsole console,AiController controller,Client client)
    {
        foreach(var identity in new[]{new StoryGameIdentity { Folder="eraMaouEx",Version="920" },new StoryGameIdentity { Folder="eraTheWorld",Version="proto" },new StoryGameIdentity { Folder="eratohoYMAEM_Chinese-master",Version="4287" }})
            using(var bundle=DesktopStorySession.OpenBundle())
            { var match=StoryTemplateCatalog.Match(bundle,identity); Check(match.State=="matched" && match.Rules.Count>0,"Windows embedded templates auto-match keyword/version: "+identity.Folder); }
        using(var bundle=DesktopStorySession.OpenBundle()) Check(StoryTemplateCatalog.Match(bundle,new StoryGameIdentity { Folder="eraMaouEx",Version="999999" }).State=="unmatched","Windows version mismatch stays conservative instead of guessing a template");
        string storage=Path.Combine(root,"logs","desktop-templates","run-"+Guid.NewGuid().ToString("N"));
        using(var session=new DesktopStorySession(controller,game,storage))
        {
            Pump(session.StartAsync()); Check(controller.StoryTemplate.State=="unmatched" && session.Document.State=="ready","Windows unmatched game automatically creates its editable function document");
            var settings=Settings(); settings.ProtectStory=true; settings.AutoGenerate=true; settings.UseGameStyle=false; controller.SetSettings(settings);
            console.PressEnterKey(false,"6",false); Pump(WaitStory(controller));
            Check(!controller.CanGenerate,"document selection does not automatically permit an uncertain caller");
            string before=StateHash(); int calls=client.Calls; client.Reply=ct=>Task.FromResult("旅人收起地图，等待安排。");
            using(var editor=new AiFunctionDocumentWindow(controller,session.Document))
            {
                editor.ShowInTaskbar=false; editor.Show(); Application.DoEvents();
                editor.SearchBox.Text="KOJO_DAILY"; editor.RefreshRows();
                Check(editor.FunctionGrid.Rows.Count==1,"Windows editor filters functions by name");
                var row=editor.FunctionGrid.Rows[0]; row.Cells["Allow"].Value=false; row.Cells["Allow"].Value=true; row.Cells["Note"].Value="已确认的日常口上";
                Pump(editor.SaveChangesAsync()); Pump(Task.Delay(100));
                Check(client.Calls==calls+1 && controller.DisplayMode=="ai" && StateHash()==before,"checking and saving a Windows function prioritizes AI without changing the full saved game state");
                string output=Path.Combine(root,"tests","screenshots"); Directory.CreateDirectory(output);
                using(var image=new System.Drawing.Bitmap(editor.Width,editor.Height)) { editor.DrawToBitmap(image,new System.Drawing.Rectangle(0,0,editor.Width,editor.Height)); image.Save(Path.Combine(output,"windows-function-document.png")); }
                row=editor.FunctionGrid.Rows[0]; row.Cells["Allow"].Value=false; Pump(editor.SaveChangesAsync()); Pump(controller.GenerateAsync(true));
                Check(client.Calls==calls+1 && controller.DisplayMode=="original" && StateHash()==before,"unchecking restores the Windows original and prevents further generation without replaying script");
                string json=session.Document.Export();
                Check(json.Contains("已确认的日常口上") && json.Contains("deny"),"Windows editable notes and function selections export as a compatible JSON document");
                File.WriteAllText(Path.Combine(root,"logs","desktop-templates","example-document.json"),json,Encoding.UTF8);
                editor.Close();
            }
            using(var other=new AiController(Settings(),new Client())) using(var reopened=new DesktopStorySession(other,game,storage))
            { Pump(reopened.StartAsync()); var saved=Json.Object(Json.Array(reopened.Document.Page("KOJO_DAILY",0,"")["rows"]).Single()); Check(Json.Text(saved,"action")=="deny" && Json.Text(saved,"note")=="已确认的日常口上","Windows per-game choices and notes persist when the game is reopened"); }
            Check(controller.Log.ReadRecent().Contains("story.template") && controller.Log.ReadRecent().Contains("story.document-edit"),"Windows log includes template matching and document editing reasons");
            File.WriteAllText(Path.Combine(root,"logs","desktop-templates","generation.log"),controller.Log.ReadRecent(),Encoding.UTF8);
        }
    }
    static void LauncherCloseChecks(MainWindow window,EmueraConsole console,AiController controller,Client client)
    {
        var pending=new TaskCompletionSource<string>(); CancellationToken token=default(CancellationToken);
        client.Reply=ct=> { token=ct; return pending.Task; }; controller.SetEvent(Snapshot("close-test"));
        Task generation=controller.GenerateAsync(true); string before=StateHash(); int confirmations=0;
        Program.LoaderParentProcessId=12345; Program.ReturnToGameSelection=false;
        try
        {
            window.ConfirmReturnToSelection=()=> { confirmations++; return false; };
            window.Close();
            Check(!window.IsDisposed && controller.Busy && !token.IsCancellationRequested && !Program.ReturnToGameSelection,"cancelling window X preserves the running game and its pending AI request");
            var menu=window.Controls.OfType<MenuStrip>().Single().Items.OfType<ToolStripMenuItem>().Single(item=>item.Name=="returnToSelection");
            menu.PerformClick();
            Check(confirmations==2 && !window.IsDisposed && StateHash()==before,"return-to-selection menu shares the X confirmation and cancellation path");
            Program.Reboot=true;
            Check(window.PrepareGameClose(CloseReason.UserClosing) && confirmations==2 && !Program.ReturnToGameSelection,"in-game reboot does not return to the picker or add an exit confirmation");
            Program.Reboot=false;
            Check(window.PrepareGameClose(CloseReason.WindowsShutDown) && confirmations==2 && !Program.ReturnToGameSelection,"system shutdown does not reopen the picker");
            window.ConfirmReturnToSelection=()=>true; window.Close();
            Check(window.IsDisposed && Program.ReturnToGameSelection && token.IsCancellationRequested && !controller.Busy,"confirmed window X marks picker return and cancels AI before disposal");
            pending.SetResult("LATE OLD GAME REPLY"); Pump(generation);
            Check(controller.Text!="LATE OLD GAME REPLY" && StateHash()==before,"late response after closing cannot display or mutate the old game");
        }
        finally { Program.LoaderParentProcessId=0; Program.ReturnToGameSelection=false; Program.Reboot=false; }
    }
    static void LauncherLoopChecks(string root)
    {
        int selections=0,plays=0;
        string[] first={"--game","game A"}, second={"--game","game B","--encoding","shift-jis"};
        LauncherSession.Run(first,()=>++selections==1 ? second : null,args=> {
            Check(object.ReferenceEquals(args,plays==0 ? first : second),"launcher passes each selected game and encoding to a fresh session");
            plays++; return LauncherSession.ReturnToPicker;
        });
        Check(selections==2 && plays==2,"closing two game sessions returns to selection; closing the picker ends the loader");
        selections=0;
        LauncherSession.Run(first,()=> { selections++; return null; },args=>0);
        Check(selections==0,"non-user session exit does not reopen selection during shutdown");
        string path=Path.Combine(root,"tests","launcher-argv.json");
        var values=new[]{"E:\\日本 ゲーム\\","quoted\"name","", "E:\\two spaces\\"};
        var all=new[]{"--argv-probe",path,System.Diagnostics.Process.GetCurrentProcess().Id.ToString()}.Concat(values);
        var start=new System.Diagnostics.ProcessStartInfo(Assembly.GetExecutingAssembly().Location,string.Join(" ",all.Select(LauncherSession.Quote))) { UseShellExecute=false,CreateNoWindow=true };
        using(var child=System.Diagnostics.Process.Start(start)) { Check(child.WaitForExit(10000) && child.ExitCode==LauncherSession.ReturnToPicker,"fresh process returns the loader lifecycle exit code"); }
        var probe=Json.Object(Json.Codec().DeserializeObject(File.ReadAllText(path)));
        Check(Json.Array(Json.Get(probe,"args")).Cast<string>().SequenceEqual(values),"child argument quoting preserves Japanese paths, spaces, quotes and trailing backslashes");
        Check(Json.Bool(probe,"withParent") && !Json.Bool(probe,"excludingParent"),"single-instance games ignore their own waiting picker process");
    }
    static void Render(Form form,string path)
    {
        form.ShowInTaskbar=false; form.Opacity=0; form.Show(); form.PerformLayout(); Application.DoEvents();
        using(var bitmap=new System.Drawing.Bitmap(form.Width,form.Height))
        { form.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,form.Width,form.Height)); bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png); }
    }
    static void RenderWindows(string root,AiController controller,Form game)
    {
        string directory=Path.Combine(root,"tests","screenshots"); Directory.CreateDirectory(directory);
        Render(game,Path.Combine(directory,"game.png"));
        using(var launcher=new GameLauncher()) Render(launcher,Path.Combine(directory,"launcher.png"));
        using(var panel=new AiWindow(controller,Path.Combine(root,"tests","ui-settings.json"))) Render(panel,Path.Combine(directory,"dialogue.png"));
        var shownSettings=controller.Settings.Copy(); shownSettings.PresetJson=File.ReadAllText(Path.Combine(root,"presets","ERA-dialogue.json"));
        using(var settings=new AiSettingsWindow(shownSettings,Path.Combine(root,"tests","ui-settings.json")))
        {
            Render(settings,Path.Combine(directory,"settings-connection.png"));
            var tabs=settings.Controls.OfType<TabControl>().Single();
            tabs.SelectedIndex=1; Render(settings,Path.Combine(directory,"settings-preset.png"));
            var grid=(DataGridView)typeof(AiSettingsWindow).GetField("entries",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(settings);
            Check(((TextBox)typeof(AiSettingsWindow).GetField("entryText",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(settings)).Text.Contains("{{char}}"),"desktop selected entry shows its actual content");
            grid.Rows[0].Cells[0].Value=false;
            string edited=((TextBox)typeof(AiSettingsWindow).GetField("preset",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(settings)).Text;
            Check(!new Preset(edited).Entries()[0].Enabled,"desktop preset checkbox updates the saved JSON selection");
            tabs.SelectedIndex=2; Render(settings,Path.Combine(directory,"settings-character.png"));
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            if(args.Length>=3 && args[0]=="--argv-probe") { File.WriteAllText(args[1],Json.Codec().Serialize(new { args=args.Skip(3).ToArray(),withParent=MinorShift._Library.Sys.PrevInstance(),excludingParent=MinorShift._Library.Sys.PrevInstance(int.Parse(args[2])) })); return LauncherSession.ReturnToPicker; }
            if(args.Length>=3 && args[1]=="--game-smoke") StartupSmoke(args[0],args[2]);
            else if(args.Length>=3 && args[1]=="--preset-smoke") SavedPresetSmoke(args[2]);
            else if(args.Length>=3 && args[1]=="--api-probe") ApiProbe(args[0],args[2]).GetAwaiter().GetResult();
            else { ReaderCompatibility(args[0]); Core(args[0]).GetAwaiter().GetResult(); LauncherLoopChecks(args[0]); Integration(args[0]); }
            Console.WriteLine("ALL PASSED: "+count); return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
