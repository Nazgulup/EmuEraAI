using System.IO.Compression;
using System.Text;
using MinorShift.Emuera.AI;
using MinorShift.Emuera.Input;

internal static class StoryTemplateTests
{
    static int count;
    static void Check(bool yes,string message) { if(!yes) throw new Exception("FAIL: "+message); count++; Console.WriteLine("PASS: "+message); }
    static async Task Until(Func<bool> predicate) { using var timer=new CancellationTokenSource(TimeSpan.FromSeconds(30)); while(!predicate()) await Task.Delay(15,timer.Token); }
    static MemoryStream Bundle(params (string Id,string Folder,string Title,string Version,string Code,string Kind,string Key)[] profiles)
    {
        var output=new MemoryStream();
        using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
        {
            void Write(string name,string text) { using var writer=new StreamWriter(zip.CreateEntry(name).Open(),new UTF8Encoding(false)); writer.Write(text); }
            var rows=new List<object>();
            foreach(var p in profiles)
            {
                string raw=Json.Codec().Serialize(new[]{new[]{p.Key,p.Kind}}); Write("profiles/"+p.Id+".json",raw);
                rows.Add(new Dictionary<string,object>{{"id",p.Id},{"name",p.Folder},{"folder",p.Folder},{"title",p.Title},{"keywords",StoryGameIdentity.Keywords(p.Folder,p.Title)},{"version",p.Version},{"versionName",""},{"code",p.Code},{"automatic",true},{"digest",StoryPolicy.Digest(raw)}});
            }
            Write("catalog.json",Json.Codec().Serialize(new Dictionary<string,object>{{"schema",1},{"profiles",rows}}));
        }
        output.Position=0; return output;
    }
    sealed class Client : IAiClient
    {
        public int Calls;
        public Task<string> GenerateAsync(AiSettings settings,Dictionary<string,object> request,CancellationToken token) { Calls++; return Task.FromResult("旅人收起地图，等待安排。"); }
    }
    public static async Task Run(string root)
    {
        var identity=StoryGameIdentity.Parse("eraJourney0.92",";注释\nタイトル,ERA_JOURNEY\nバージョン,920\nコード,77\nバージョン違い認める,800");
        Check(identity.Title=="ERA_JOURNEY" && identity.Version=="920" && identity.Code=="77","read Japanese GAMEBASE metadata without treating save-compatible minimum as version");
        Check(StoryGameIdentity.Parse("eraJourney", "バージョン,proto").Version=="proto","preserve non-numeric ERA version tags such as proto");
        var localized=StoryGameIdentity.Parse("folder","遊戲標題,ERA_JOURNEY,\n方案,00920,\n代碼,77,");
        Check(localized.Title=="ERA_JOURNEY" && localized.Version=="920" && localized.Code=="77","localized Chinese metadata keys and trailing CSV columns remain compatible");
        string source="@KOJO_DAILY\nPRINTL Calm reply.\nRETURN", key=StoryPolicy.Analyze("ERB/test.erb",source)[0].Key;
        var p=(Id:"111111111111",Folder:"eraJourney0.92",Title:"ERA_JOURNEY",Version:"920",Code:"77",Kind:"routine",Key:key);
        StoryTemplateData Match(StoryGameIdentity g,params (string,string,string,string,string,string,string)[] entries) { using var zip=Bundle(entries.Length>0 ? entries : [p]); return StoryTemplateCatalog.Match(zip,g); }
        Check(Match(identity).Id==p.Id,"name keyword plus exact configured version selects built-in profile");
        Check(Match(new(){Folder="My renamed game",Title=identity.Title,Version="920",Code="77"}).Id==p.Id,"renamed folder still matches the configured game title and version");
        Check(Match(new(){Folder="eraJourney0.92 copied"}).Id==p.Id,"one explicit folder version works when configuration version is absent");
        Check(Match(new(){Folder="eraJourney0.92",Version="930"}).State=="unmatched","configured version conflict cannot be bypassed by directory name");
        Check(Match(new(){Folder="eraJourney"}).State=="unmatched","missing version never silently selects the newest profile");
        Check(Match(new(){Folder="eraJourney0.92 backup0.93"}).State=="unmatched","multiple directory versions are ambiguous");
        Check(Match(new(){Folder="eraJourney0.92",Version="920",Code="99"}).State=="unmatched","different game identifier prevents similarly named game mismatch");
        Check(Match(new(){Folder="eraJourney0.93",Version="proto"},p with {Version="proto"}).State=="unmatched","generic version tags still reject conflicting explicit edition versions");
        var other=p with { Id="222222222222",Folder="eraJourney alternative",Kind="story" };
        Check(Match(new(){Folder="renamed",Title=identity.Title,Version="920",Code="77"},p,other).State=="ambiguous","same name/version with conflicting modified profiles is not guessed");
        Check(Match(identity,p,other).Id==p.Id,"exact edition folder disambiguates equally named and versioned variants");
        Check(Match(new(){Folder="renamed",Title=identity.Title,Version="920",Code="77"},p,other with {Kind="routine"}).State=="matched","identical rule copies can share one verified template");
        var active=Match(identity); var policy=new StoryPolicy(()=>["ERB/test.erb"],_=>source,null,()=>active);
        var site=new[]{"KOJO_DAILY|ERB/test.erb|2|leaf"};
        var d=policy.Evaluate(site); Check(d.Allowed && d.TemplateRule=="routine","selected template is applied only after current function fingerprint verification");
        policy.SetRule(d.RuleKey,"deny"); Check(!policy.Evaluate(site).Allowed && policy.Export().Contains("deny"),"built-in template does not overwrite a user's manual deny");
        var changed=new StoryPolicy(()=>["ERB/test.erb"],_=>source.Replace("Calm reply.","Changed reply."),null,()=>active);
        Check(changed.Evaluate(site).TemplateRule=="","changed source cannot inherit stale template classification");
        var uncertain=Match(identity,p with {Kind="unknown"}); var conservative=new StoryPolicy(()=>["ERB/test.erb"],_=>source,null,()=>uncertain);
        Check(!conservative.Evaluate(site).Allowed,"built-in uncertain classification protects even a routine-named function");
        var story=Match(identity,p with {Kind="story"}); var protectedPolicy=new StoryPolicy(()=>["ERB/test.erb"],_=>source,null,()=>story);
        protectedPolicy.SetRule(key,"allow"); Check(!protectedPolicy.Evaluate(site).Allowed,"built-in story protection remains stronger than a persistent function allow");
        var unavailable=new StoryPolicy(()=>["ERB/test.erb"],_=>source,null,()=>throw new IOException());
        Check(unavailable.Evaluate(site).Allowed && unavailable.Template.State=="error","missing bundle retains existing conservative analysis instead of crashing game");
        using(var damaged=new MemoryStream([1,2,3])) { bool failed=false; try { StoryTemplateCatalog.Match(damaged,identity); } catch(InvalidDataException) { failed=true; } Check(failed,"invalid bundle is rejected"); }

        object[] catalog;
        using(var packaged=ZipFile.OpenRead(StoryBundle.BundlePath(root))) using(var reader=new StreamReader(packaged.GetEntry("catalog.json")!.Open()))
            catalog=Json.Array(Json.Get(Json.Object(Json.Codec().DeserializeObject(reader.ReadToEnd())),"profiles"));
        Check(catalog.Length==182 && catalog.Select(Json.Object).Count(r=>Json.Bool(r,"automatic"))>0,"real bundle includes all 182 templates with complete-game eligibility metadata");
        string workspace=Path.GetDirectoryName(root)!;
        foreach(string folder in new[]{"eraMaouEx","eraTheWorld","eratohoYMAEM_Chinese-master","ERA合集\\ERA红魔馆最新0032基础汉化（已转码）","ERA合集\\eraMeiQ外傳 ver1.882JK漢化版v1.9","eraALK1.027漢化（全立繪）\\eraALK1.027漢化（全立繪）"})
        {
            if(!Directory.Exists(Path.Combine(workspace,folder))) { Console.WriteLine("SKIP optional local game metadata: "+folder); continue; }
            using var zip=File.OpenRead(StoryBundle.BundlePath(root)); var game=StoryGameIdentity.Parse(Path.GetFileName(folder),StoryBundle.Metadata(Path.Combine(workspace,folder)));
            var actual=StoryTemplateCatalog.Match(zip,game);
            Check(actual.State=="matched" && actual.Rules.Count>0,"real installed game selects bundled template: "+folder+" / "+game.Version);
            Console.WriteLine("MATCH: "+actual.Name+" | "+actual.Reason+" | rules="+actual.Rules.Count);
        }

        // Production host: automatic selection before any dialogue, no UI/import command.
        string output=Path.Combine(root,"logs","builtin-host"), gameRoot=Path.Combine(root,"portable","Tests","builtin-game");
        Directory.CreateDirectory(output); Directory.CreateDirectory(Path.Combine(gameRoot,"CSV")); Directory.CreateDirectory(Path.Combine(gameRoot,"ERB"));
        foreach(string file in Directory.GetFiles(Path.Combine(root,"demo","CSV"))) File.Copy(file,Path.Combine(gameRoot,"CSV",Path.GetFileName(file)),true);
        File.WriteAllText(Path.Combine(gameRoot,"CSV","GameBase.csv"),"タイトル,ERA_JOURNEY\nバージョン,920\nコード,77\n");
        string engineSource="@SYSTEM_TITLE\nADDCHARA 0\nADDCHARA 1\nMASTER=0\nTARGET=1\n$MENU\nPRINTL [1] 等待\nINPUT\nCALL KOJO_DAILY\nGOTO MENU\n@KOJO_DAILY\nPRINTL 旅人在路口等待安排。\nRETURN\n";
        File.WriteAllText(Path.Combine(gameRoot,"ERB","test.erb"),engineSource);
        var fn=StoryPolicy.Analyze("ERB/test.erb",engineSource).Single(f=>f.Name=="KOJO_DAILY");
        using(var neutralBundle=Bundle(p with {Key=fn.Key,Kind="unknown"})) File.WriteAllBytes(Path.Combine(output,"story-templates.zip"),neutralBundle.ToArray());
        EraCore.Maui.FileSystem.PackageDirectory=output; EraCore.Maui.FileSystem.AppDataDirectory=Path.Combine(output,"run-"+Guid.NewGuid().ToString("N"));
        EraCore.Maui.SecureStorage.Default.Read=Task.FromResult<string?>(Json.Codec().Serialize(new AiSettings { Enabled=true,AutoGenerate=true,UseGameStyle=false }.ToDictionary(true)));
        var client=new Client(); var host=new EraCore.Maui.BridgeHost(gameRoot,client);
        try
        {
            host.Start(); await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(60)); await Until(()=>host.Controller.StoryTemplate.State=="matched");
            Check(host.Controller.StoryTemplate.Id==p.Id && client.Calls==0,"production host automatically loads package asset during game start without any import action");
            var initial=Json.Codec().DeserializeObject(System.Text.Json.JsonSerializer.Serialize(host.Session!.Output.Snapshot));
            host.Session.Input.Submit(new InputFrame(Value:"1")); await Until(()=>host.Controller.Current!=null && host.Controller.StoryReady);
            Check(!host.Controller.CanGenerate && host.Controller.Story.TemplateRule=="unknown" && client.Calls==0,"production host applies matched template to the actual engine dialogue");
            await host.Controller.SetStoryRuleAsync("allow","event"); string before=System.Text.Json.JsonSerializer.Serialize(host.Session.Output.Snapshot); await host.Command("aiGenerate");
            Check(client.Calls==1 && System.Text.Json.JsonSerializer.Serialize(host.Session.Output.Snapshot)==before,"manual current-event allow still works without game state input");
            Check(host.Js.Messages.Any(x=>x.Contains("storyTemplateName") && x.Contains("eraJourney0.92")) && host.Controller.Log.ReadRecent().Contains("story.template"),"UI state and generation log report the selected template and match reason");
            File.WriteAllText(Path.Combine(output,"generation.log"),host.Controller.Log.ReadRecent());
            File.WriteAllText(Path.Combine(output,"bridge-replay.json"),Json.Codec().Serialize(new Dictionary<string,object>{{"initial",initial},{"events",host.Js.Events.ToArray()}}));
        }
        finally { await host.Stop(); EraCore.Maui.FileSystem.PackageDirectory=Path.GetDirectoryName(StoryBundle.BundlePath(root))!; }
        Console.WriteLine("ALL BUILT-IN TEMPLATE CHECKS PASSED: "+count);
    }
}
