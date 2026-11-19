using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Generic;

namespace MinorShift.Emuera.AI
{
    // Holds strings/settings only. Regeneration has no callback into the interpreter.
    internal sealed class AiController : IDisposable
    {
        readonly IAiClient client;
        CancellationTokenSource request;
        long revision;
        bool suspended,disposed;
        GameStyleService gameStyle;
        string lastReply,lastRequestHash;
        int regenerationAttempt;
        StoryPolicy storyPolicy;
        Task storyCheck=Task.FromResult(0);
        string storyOnce="";
        public StoryDecision Story { get; private set; }=new StoryDecision();
        public bool StoryReady { get; private set; }
        public bool CanGenerate { get { return Current!=null && StoryReady && Story.Allowed; } }
        public void ConfigureStoryPolicy(StoryPolicy policy) { storyPolicy=policy; Log.Write("story.mode",null,"模板已取消；默认开启 AI，手动关闭优先","default","allow"); }
        public StoryTemplateData StoryTemplate { get { return storyPolicy==null ? new StoryTemplateData() : storyPolicy.Template; } }
        public async Task PrepareStoryTemplatesAsync()
        {
            var policy=storyPolicy; if(policy==null) return;
            await Task.Run(()=>policy.PrepareTemplates());
            if(disposed || storyPolicy!=policy) return;
            var t=policy.Template;
            Log.Write("story.template",null,t.Reason,"state",t.State,"templateId",t.Id,"templateName",t.Name,"version",t.Version,"rules",t.Rules.Count);
            Notify();
        }
        public string ExportStoryRules() { return storyPolicy==null ? "" : storyPolicy.Export(); }
        public async Task RefreshStoryRulesAsync()
        { Stop(); storyOnce=""; StartStoryCheck(true); await storyCheck; }
        public async Task ImportStoryRulesAsync(string json)
        { if(storyPolicy==null) throw new InvalidOperationException("游戏规则尚未就绪。"); storyPolicy.Import(json); Stop(); storyOnce=""; StartStoryCheck(false); await storyCheck; }
        public async Task SetStoryRuleAsync(string action,string scope)
        {
            if(Current==null) throw new InvalidOperationException("请先触发一段口上或可补写的菜单。");
            if(action!="allow" && action!="deny" && action!="auto") throw new FormatException("无效的规则操作。");
            var snapshot=Current; await storyCheck; if(Current!=snapshot || disposed) return;
            if(scope=="function") { if(storyPolicy==null) throw new InvalidOperationException("游戏规则尚未就绪。"); storyPolicy.SetRule(Story.RuleKey,action); storyOnce=""; }
            else if(scope=="event") storyOnce=action=="auto" ? "" : action;
            else throw new FormatException("无效的规则范围。");
            Stop(); Log.Write("story.override",null,"用户修改生成权限","scope",scope,"action",action,"eventId",snapshot.EventId,"location",Story.Location);
            StartStoryCheck(false); await storyCheck;
        }
        void StartStoryCheck(bool auto)
        {
            var snapshot=Current; long ticket=revision; StoryReady=false;
            if(snapshot==null) { Story=new StoryDecision(); storyCheck=Task.FromResult(0); return; }
            Story=new StoryDecision { Reason="正在核对手动关闭记录" };
            storyCheck=CheckStoryAsync(snapshot,ticket);
            if(auto) { var pending=AutoAfterStoryAsync(snapshot,ticket,storyCheck); }
        }
        async Task CheckStoryAsync(DialogueSnapshot snapshot,long ticket)
        {
            StoryDecision result;
            if(storyPolicy==null) result=new StoryDecision { Allowed=true,Kind="default-allow",Reason="默认开启 AI；尚无函数关闭记录" };
            else result=await Task.Run(()=>storyPolicy.Evaluate(snapshot.StoryContext));
            if(disposed || Current!=snapshot || ticket!=revision) return;
            if(storyOnce=="deny") { result.Allowed=false; result.Kind="event-deny"; result.Reason="用户禁止本段生成"; }
            else if(storyOnce=="allow" && result.Kind!="manual-deny") { result.Allowed=true; result.Kind="event-allow"; result.Reason="用户仅允许当前事件；下一事件重新判断"; }
            Story=result; StoryReady=true;
            bool waiting=Settings.Enabled && result.Allowed && DisplayMode=="waiting";
            Text=waiting ? "" : snapshot.Original;
            DisplayMode=waiting ? "waiting" : "original";
            Status=(result.Allowed ? "允许 AI：" : "手动关闭：")+result.Reason;
            Log.Write("story.decision",null,Status,"kind",result.Kind,"allowed",result.Allowed,"eventId",snapshot.EventId,"hook",snapshot.Hook,"location",result.Location,"ruleKey",result.RuleKey,"context",string.Join("\n",snapshot.StoryContext),"supplement",snapshot.Original.Length==0,"default","allow","templates","removed");
            Notify();
        }
        async Task AutoAfterStoryAsync(DialogueSnapshot snapshot,long ticket,Task check)
        { await check; if(!disposed && Current==snapshot && ticket==revision && Settings.Enabled && Settings.AutoGenerate && CanGenerate && !Busy) await GenerateAsync(false); }
        public AiSettings Settings { get; private set; }
        public DialogueSnapshot Current { get; private set; }
        public string Text { get; private set; }
        public string Status { get; private set; }
        public bool Busy { get; private set; }
        public string DisplayMode { get; private set; }
        public AiLog Log { get; private set; }
        public string StyleReport { get { return gameStyle==null ? "尚未加载游戏风格。" : gameStyle.Report; } }
        public AiSettings PreviewSettings { get { var value=Settings.Copy(); value.GameStyle=Settings.UseGameStyle && gameStyle!=null ? gameStyle.Result : ""; return value; } }
        public Dictionary<string,object> PreviewRequest() { return new Preset(Settings.PresetJson).Build(Current,PreviewSettings,true,lastReply,regenerationAttempt+1); }
        public void ConfigureGameStyle(Func<GameStyleCorpus> loader,string cache)
        { if(gameStyle!=null) gameStyle.Dispose(); gameStyle=new GameStyleService(loader,cache,client,Log,Notify); }
        public event Action Changed;
        public AiController(AiSettings settings,IAiClient client,AiLog log=null)
        {
            Settings=settings; this.client=client; Text=""; Status="等待游戏口上"; DisplayMode="empty";
            Log=log ?? new AiLog(); Log.Protect(settings.ApiKey);
            var transport=client as AiClient; if(transport!=null) transport.Log=Log;
            Log.Write("settings.loaded",null,"AI 设置已加载","enabled",settings.Enabled,"auto",settings.AutoGenerate,"model",settings.Model,"timeoutSeconds",settings.TimeoutSeconds,"outputTokens",settings.OutputTokens,"hasKey",!string.IsNullOrWhiteSpace(settings.ApiKey));
        }
        void Notify() { if(!disposed && !suspended && Changed!=null) Changed(); }
        void Stop() { revision++; if(request!=null) { request.Cancel(); request=null; } Busy=false; }
        public void Suspend() { suspended=true; }
        public void SetEvent(DialogueSnapshot snapshot)
        {
            suspended=false;
            if(snapshot!=null && Current!=null && snapshot.EventId==Current.EventId && snapshot.StoryTurn==Current.StoryTurn)
            {
                Notify();
                return;
            }
            Stop(); lastReply=lastRequestHash=null; regenerationAttempt=0; storyOnce=""; Current=snapshot; Text=snapshot==null || Settings.Enabled ? "" : snapshot.Original;
            DisplayMode=snapshot==null ? "empty" : Settings.Enabled ? "waiting" : "original";
            Status=snapshot==null ? "没有捕获到口上；可查看生成日志" : Settings.Enabled ? "AI 优先；等待生成" : "AI 未启用，使用原口上";
            Log.Write(snapshot==null ? "event.empty" : "event.captured",null,Status,"hook",snapshot==null ? "" : snapshot.Hook,"originalChars",snapshot==null ? 0 : snapshot.Original.Length);
            Notify(); StartStoryCheck(true);
        }
        public void SetSettings(AiSettings settings)
        { Stop(); Settings=settings.Copy(); Log.Protect(settings.ApiKey); if(Current!=null) Text=Current.Original; DisplayMode=Current==null ? "empty" : "original"; Status="设置已更新；可点击重新生成"; Log.Write("settings.updated",null,Status,"enabled",settings.Enabled,"auto",settings.AutoGenerate,"model",settings.Model); Notify(); StartStoryCheck(false); }
        public void RestoreOriginal()
        { Stop(); Text=Current==null ? "" : Current.Original; DisplayMode=Current==null ? "empty" : "original"; Status=Current!=null && Current.Original.Length==0 ? "已取消补写，保留原游戏画面" : "已取消 AI 并恢复原口上"; Log.Write("fallback.manual",null,Status); Notify(); if(!StoryReady) StartStoryCheck(false); }
        public void Reset()
        { Stop(); lastReply=lastRequestHash=null; regenerationAttempt=0; storyOnce=""; StoryReady=false; Story=new StoryDecision(); suspended=false; Current=null; Text=""; DisplayMode="empty"; Status="等待游戏口上"; Log.Write("event.reset",null,"游戏重置/读档，旧请求失效"); Notify(); }
        public async Task GenerateAsync(bool regenerate)
        {
            if(disposed || suspended || Current==null) { Log.Write("request.skipped",null,"未发出请求：没有当前口上或引擎正在执行","suspended",suspended,"hasEvent",Current!=null); return; }
            var expected=Current; long requestedRevision=revision; await storyCheck;
            if(disposed || suspended || Current!=expected || requestedRevision!=revision || Busy) return;
            if(!CanGenerate) { Text=Current.Original; DisplayMode="original"; Status="手动关闭："+Story.Reason; Log.Write("request.protected",null,Status,"eventId",Current.EventId,"kind",Story.Kind); Notify(); return; }
            Stop(); var snapshot=Current; long ticket=revision;
            string previous=lastReply; int attempt=regenerate ? ++regenerationAttempt : 0;
            var settings=Settings.Copy(); var source=new CancellationTokenSource(); request=source;
            settings.TraceId=Guid.NewGuid().ToString("N").Substring(0,12); Log.Protect(settings.ApiKey); var elapsed=Stopwatch.StartNew();
            Busy=true; Text=""; DisplayMode="pending"; Status=snapshot.Original.Length==0 ? "正在补写口上…本回合无原文，失败时保留游戏画面" : "AI 口上生成中…确认失败后才显示原口上";
            Log.Write("request.start",settings.TraceId,"开始生成口上","regenerate",regenerate,"hook",snapshot.Hook,"model",settings.Model,"timeoutSeconds",settings.TimeoutSeconds); Notify();
            Log.WriteText("dialogue.original",settings.TraceId,"本次对应的缓存原口上",snapshot.Original);
            if(previous!=null) Log.WriteText("dialogue.previous",settings.TraceId,"同一事件上一版已接受的 AI 正文",previous);
            try
            {
                if(!settings.Enabled) throw new InvalidOperationException("AI 未启用。");
                if(gameStyle!=null && settings.UseGameStyle)
                {
                    var style=gameStyle.PrepareAsync(settings);
                    await Task.WhenAny(style,Task.Delay(Timeout.Infinite,source.Token));
                    source.Token.ThrowIfCancellationRequested();
                    settings.GameStyle=await style;
                }
                source.Token.ThrowIfCancellationRequested();
                source.CancelAfter(TimeSpan.FromSeconds(Math.Max(2,Math.Min(180,settings.TimeoutSeconds))));
                var preset=new Preset(settings.PresetJson);
                var body=preset.Build(snapshot,settings,regenerate,regenerate ? previous : null,attempt);
                lastRequestHash=DialogueDiagnostics.Request(Log,settings.TraceId,body,lastRequestHash,regenerate,attempt,regenerate && previous!=null);
                Log.Write("preset.ready",settings.TraceId,"预设组装成功","messages",((List<Dictionary<string,object>>)body["messages"]).Count,"settingsOutputTokens",settings.OutputTokens,"outputTokenSource",preset.OutputTokenSource(settings),"effectiveOutputTokens",Json.Int(body,"max_tokens"),"gameStyleChars",(settings.GameStyle ?? "").Length);
                string result=await client.GenerateAsync(settings,body,source.Token);
                source.Token.ThrowIfCancellationRequested();
                if(ticket!=revision || disposed || Current!=snapshot) { Log.Write("request.stale",settings.TraceId,"游戏已进入其他事件，忽略旧回复"); return; }
                if(string.IsNullOrWhiteSpace(result)) throw new FormatException("API 返回空口上。");
                Log.WriteText("dialogue.beforeAddressRestore",settings.TraceId,"称呼还原前的 AI 正文",result);
                result=snapshot.Addresses.Restore(result,snapshot.Original);
                DialogueDiagnostics.Compare(Log,settings.TraceId,snapshot,result,previous);
                lastReply=result;
                Text=result; DisplayMode="ai"; Status="AI 口上 · 可重新生成，游戏数值不变";
                bool copied=DialogueDiagnostics.Equivalent(snapshot.Original,result), repeated=DialogueDiagnostics.Equivalent(previous,result);
                if(copied || repeated) Status="AI 回复与"+(copied && repeated ? "原口上及上一版" : copied ? "原口上" : "上一版")+"相同（忽略空白）；对比已写入日志。";
                Log.Write("request.success",settings.TraceId,"AI 口上已显示","elapsedMs",elapsed.ElapsedMilliseconds,"replyChars",result.Length);
            }
            catch(Exception ex)
            {
                if(ticket!=revision || disposed || Current!=snapshot) { Log.Write("request.cancelled",settings.TraceId,"旧请求已取消或过期","elapsedMs",elapsed.ElapsedMilliseconds); return; }
                Text=snapshot.Original; DisplayMode="original";
                string fallback=snapshot.Original.Length==0 ? "本回合无原口上，保留游戏画面。" : "使用原口上。";
                Status=(ex is OperationCanceledException ? "生成超时或已取消。" :
                    ex is FormatException || ex is InvalidOperationException ? ex.Message+" " : "网络或接口异常。")+fallback;
                Log.Write("request.failed",settings.TraceId,ex.ToString(),"elapsedMs",elapsed.ElapsedMilliseconds,"fallback",snapshot.Original.Length==0 ? "unchanged-game" : "cached-original");
                Status+=" 日志编号："+settings.TraceId;
            }
            finally
            {
                if(ticket==revision && !disposed) { request=null; Busy=false; Notify(); }
                source.Dispose();
            }
        }
        public async Task<string> ProbeAsync()
        {
            var settings=Settings.Copy(); settings.TraceId=Guid.NewGuid().ToString("N").Substring(0,12); Log.Protect(settings.ApiKey);
            Log.Write("probe.start",settings.TraceId,"使用已保存连接发送中性测试，不含游戏或预设正文","model",settings.Model);
            using(var source=new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(2,Math.Min(180,settings.TimeoutSeconds)))))
            {
                try
                {
                    var body=new Dictionary<string,object>{{"model",settings.Model},{"messages",new[]{new Dictionary<string,object>{{"role","user"},{"content","Reply with only the word OK. This is a connection test."}}}},{"stream",false},{"max_tokens",Preset.ReadTokenLimit(settings.OutputTokens,"设置中的输出上限 ")}};
                    string reply=await client.GenerateAsync(settings,body,source.Token);
                    Log.Write("probe.success",settings.TraceId,"API 测试成功","replyChars",reply.Length); return "API 测试成功；详细过程见生成日志。";
                }
                catch(Exception ex) { Log.Write("probe.failed",settings.TraceId,ex.ToString()); return "API 测试失败，请查看生成日志。编号："+settings.TraceId; }
            }
        }
        public void Dispose() { if(disposed) return; Stop(); disposed=true; if(gameStyle!=null) gameStyle.Dispose(); var d=client as IDisposable; if(d!=null) d.Dispose(); }
    }
}
