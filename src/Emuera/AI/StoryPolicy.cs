using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MinorShift.Emuera.AI
{
    internal sealed class StoryDecision
    {
        public bool Allowed;
        public string Kind="unknown", Reason="没有足够证据确认是日常口上", RuleKey="", Location="";
        public string TemplateRule="";
    }
    internal sealed class StoryTemplateData
    {
        public string Id="", Name="", Version="", State="none", Reason="未配置内置模板";
        public readonly Dictionary<string,string> Rules=new Dictionary<string,string>(StringComparer.Ordinal);
    }
    internal sealed class StoryFunction
    {
        public string File, Name, Hash, Kind, Reason;
        public int Start, End;
        public bool HasEventFlags;
        public readonly HashSet<int> RepeatingInputLines=new HashSet<int>();
        public readonly HashSet<int> GatedLines=new HashSet<int>();
        public string Key { get { return File.ToLowerInvariant()+"|"+Name.ToUpperInvariant()+"|"+Hash; } }
    }
    // Local conservative source analysis. No script execution, AI request or save access.
    internal sealed class StoryPolicy
    {
        readonly Func<IEnumerable<string>> listFiles;
        readonly Func<string,string> readText;
        readonly string rulesPath;
        readonly Dictionary<string,StoryFunction[]> cache=new Dictionary<string,StoryFunction[]>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,string> rules=new Dictionary<string,string>(StringComparer.Ordinal);
        string[] paths;
        
        public StoryTemplateData Template { get; private set; }=new StoryTemplateData { State="removed", Reason="已取消模板；默认开启 AI，只按手动关闭记录保留原文" };
        public string Warning="";
        static readonly Regex Label=new Regex(@"^\s*@([^\s,;(]+)");
        static readonly Regex StoryName=new Regex(@"STORY|SCENARIO|SCENE|ENDING|EPILOG|PROLOG|CONFESS|FLASHBACK|FIRST(?:_|$)|EVENT_(?:FIRST|END|LOAD)|M_KOJO_EVENT|MESSAGE_SUB_EVENT|告白|结局|結局|回想|剧情|劇情|初対面|初登場|初次|初见|初見|エンディング|シナリオ|回忆|回憶|主线|主線",RegexOptions.IgnoreCase);
        static readonly Regex RoutineName=new Regex(@"^(?:(?:M_)?KOJO_MESSAGE_COM_K\d+_\d+(?:_\d+)?|KOJO_\d+_TRAIN_(?:MESSAGE_[SF]_COM|TEXT_COM)_\d+|TRAIN_MESSAGE_COM\d+|MESSAGE_COM\d+|KOJO_ACT_K\d+_\d+|KOJO_(?:COM|DAILY|REACTION|TALK)(?:_\w+)?|M_KOJO_K\d+_COM\d+|COM_KOJO_\d+|SELF_KOJO_\d+|PRITRAIN_MESSAGE(?:_\w+)?|PRETRAIN_MESSAGE(?:_\w+)?)$",RegexOptions.IgnoreCase);
        static readonly Regex Infrastructure=new Regex(@"^(?:SYSTEM_[A-Z0-9_]+|EVENTTRAIN|EVENTCOM|EVENTCOMEND|USERCOM|USERSHOP|SHOW_[A-Z0-9_]+|TRAIN(?:_[A-Z0-9_]+)?|COM\d+|COMABLE\d+|KOJO_MESSAGE_COM|KOJO_MESSAGE_COM_K\d+|KOJO_MESSAGE_COM_K\d+_\d+|KOJO_COM|KOJO_DAILY|KOJO_REACTION|KOJO_TALK|PRITRAIN_MESSAGE|PRETRAIN_MESSAGE)$",RegexOptions.IgnoreCase);
        static readonly Regex Flags=new Regex(@"\b(?:CFLAG|FLAG|TFLAG|GLOBAL|PALAMLV|STORY|EVENT|QUEST|SCENARIO)\b|初回|初次|一度|一回|首次|未読|既読|好感度イベント",RegexOptions.IgnoreCase);
        static readonly Regex HardCommand=new Regex(@"^(?:BEGIN\s+(?:FIRST|AFTERTRAIN|TURNEND)|ADDCHARA|DELCHARA|SWAPCHARA|RESTART|QUIT|THROW|SAVEDATA|LOADDATA|SAVEGLOBAL|LOADGLOBAL|RESETDATA)\b",RegexOptions.IgnoreCase);
        static readonly Regex Control=new Regex(@"^(?:IF|ELSEIF|SIF|SELECTCASE|CASE|WHILE|FOR|REPEAT)\b",RegexOptions.IgnoreCase);
        static readonly Regex SafeStatement=new Regex(@"^(?:PRINT[A-Z_]*|DATAFORM|DATA|DATALIST|ENDLIST|PRINTDATA[A-Z]*|ENDDATA|IF|ELSEIF|ELSE|ENDIF|SIF|SELECTCASE|CASE|CASEELSE|ENDSELECT|RETURNF?|LOCAL[A-Z]*|DRAWLINE|CUSTOMDRAWLINE|SETCOLOR[A-Z]*|SETBGCOLOR[A-Z]*|RESETCOLOR|RESETBGCOLOR|FONT[A-Z]*|ALIGNMENT|SETFONT|WAIT|WAITANYKEY|TIMES|REPEAT|REND|FOR|NEXT|WHILE|WEND|BREAK|CONTINUE)\b",RegexOptions.IgnoreCase);
        public StoryPolicy(Func<IEnumerable<string>> files,Func<string,string> read,string path=null,Func<StoryTemplateData> templates=null)
        {
            listFiles=files; readText=read; rulesPath=path;
            if(path!=null && File.Exists(path)) try { Import(File.ReadAllText(path),false); } catch { Warning="本游戏手动开关无法读取，使用默认开启。"; }
        }
        // Kept for old callers; templates are never read or applied.
        public void PrepareTemplates() { }
        public static string Normalize(string path) { return (path ?? "").Replace('\\','/').TrimStart('/'); }
        public static IEnumerable<string> Enumerate(Func<string,IEnumerable<string>> dirs,Func<string,IEnumerable<string>> files)
        {
            var queue=new Queue<string>(dirs("").Where(p=>p.Equals("ERB",StringComparison.OrdinalIgnoreCase)));
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while(queue.Count>0)
            {
                var dir=queue.Dequeue(); if(!seen.Add(dir)) continue;
                if(seen.Count>100000) throw new FormatException("目录数量过多，保守保留");
                foreach(var file in files(dir)) if(file.EndsWith(".erb",StringComparison.OrdinalIgnoreCase)) yield return file;
                foreach(var child in dirs(dir)) queue.Enqueue(child);
            }
        }
        public static string Digest(string text) { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-","").ToLowerInvariant(); }
        public static StoryFunction[] Analyze(string path,string text)
        {
            string[] lines=text.Replace("\r\n","\n").Replace('\r','\n').TrimEnd('\n').Split('\n');
            var result=new List<StoryFunction>();
            for(int i=0;i<lines.Length;i++)
            {
                var label=Label.Match(lines[i]); if(!label.Success) continue;
                int start=i, end=i+1; while(end<lines.Length && !Label.IsMatch(lines[end])) end++;
                var f=new StoryFunction { File=Normalize(path),Name=label.Groups[1].Value,Start=start+1,End=end,Kind="unknown",Reason="函数未确认为可重复的日常口上",Hash=Digest(string.Join("\n",lines.Skip(start).Take(end-start))) };
                bool story=StoryName.IsMatch(f.Name) || StoryName.IsMatch(f.File), unsafeCode=false, flags=false; int prints=0, chars=0;
                var gates=new Stack<bool>(); bool nextGate=false;
                for(int row=start+1;row<end;row++)
                {
                    string line=lines[row].Trim(); int comment=line.IndexOf(';'); if(comment>=0) line=line.Substring(0,comment).TrimEnd();
                    if(line.Length==0 || line.StartsWith("#")) continue;
                    bool previousGate=nextGate; nextGate=false;
                    bool condition=Control.IsMatch(line), risky=condition && Flags.IsMatch(line);
                    if(Regex.IsMatch(line,@"^(?:ENDIF|ENDSELECT|WEND|NEXT|REND)\b",RegexOptions.IgnoreCase)) { if(gates.Count>0) gates.Pop(); }
                    else if(Regex.IsMatch(line,@"^(?:IF|SELECTCASE|WHILE|FOR|REPEAT)\b",RegexOptions.IgnoreCase)) gates.Push(risky || gates.Any(x=>x));
                    else if(Regex.IsMatch(line,@"^ELSEIF\b",RegexOptions.IgnoreCase) && gates.Count>0) { bool old=gates.Pop(); gates.Push(old || risky); }
                    if(Regex.IsMatch(line,@"^SIF\b",RegexOptions.IgnoreCase)) nextGate=risky || gates.Any(x=>x);
                    if(previousGate || risky || gates.Any(x=>x)) f.GatedLines.Add(row+1);
                    if(HardCommand.IsMatch(line)) story=true;
                    if(Flags.IsMatch(line) && !Regex.IsMatch(line,@"^(?:PRINT|DATA)",RegexOptions.IgnoreCase)) flags=true;
                    if(!SafeStatement.IsMatch(line) || Regex.IsMatch(line,@"^(?:PRINTBUTTON|INPUT|CALL|TRYCALL|JUMP|TRYJUMP|GOTO|CALLFORM|JUMPFORM)",RegexOptions.IgnoreCase)) unsafeCode=true;
                    if(Regex.IsMatch(line,@"^(?:PRINT|DATA)",RegexOptions.IgnoreCase)) { prints++; chars+=line.Length; }
                    // Inline ERB user functions may hide event handling. Only known address/string helpers are exempt.
                    foreach(Match method in Regex.Matches(line,@"([\p{L}_][\p{L}\p{N}_]*)\s*\("))
                        if(!Regex.IsMatch(method.Groups[1].Value,@"^(?:SELF_CALL(?:_FIRST)?|STRLENS?|STRFORM|UNICODE|TOSTR|MAX|MIN|ABS|GETPALAMLV|GETEXPLV)$",RegexOptions.IgnoreCase)) unsafeCode=true;
                }
                if(story) { f.Kind="story"; f.Reason="剧情名称或角色/存档/流程切换指令"; }
                else if(flags) f.Reason="含事件标记或条件分支，不能证明是纯日常口上";
                else if(unsafeCode) f.Reason="含调用、跳转、选项、未知语法或状态写入，需要人工确认";
                else if(prints>40 || chars>2400) f.Reason="长段落或大量输出，保守保留";
                else if(RoutineName.IsMatch(f.Name)) { f.Kind="routine"; f.Reason="日常回调命名且正文仅含已识别的输出/局部控制，无事件标记与外部调用"; }
                f.HasEventFlags=flags;
                // A title function often contains both a one-time prologue and a menu loop.
                // Only positions after INPUT and before a verified backward GOTO are dispatch sites.
                var loopLabels=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase); int inputLine=0;
                for(int row=start+1;row<end;row++)
                {
                    string line=lines[row].Trim();
                    if(line.StartsWith("$")) loopLabels[line.Substring(1).Trim()]=row+1;
                    if(Regex.IsMatch(line,@"^INPUT(?:S|INT|STR)?\b",RegexOptions.IgnoreCase)) inputLine=row+1;
                    var go=Regex.Match(line,@"^GOTO\s+([^\s;]+)\s*(?:;.*)?$",RegexOptions.IgnoreCase); int labelLine;
                    if(go.Success && loopLabels.TryGetValue(go.Groups[1].Value,out labelLine) && inputLine>labelLine)
                        for(int n=inputLine+1;n<=row+1;n++) f.RepeatingInputLines.Add(n);
                }
                result.Add(f); i=end-1;
            }
            return result.ToArray();
        }
        StoryFunction Resolve(string name,string path)
        {
            if(paths==null) paths=listFiles().Select(Normalize).Where(p=>p.EndsWith(".erb",StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(100001).ToArray();
            if(paths.Length>100000) throw new FormatException("脚本文件过多，保守保留");
            path=Normalize(path); var matches=paths.Where(p=>p.Equals(path,StringComparison.OrdinalIgnoreCase) || p.EndsWith("/"+path,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(matches.Length!=1) throw new FormatException("脚本路径缺失或重名，无法精确确认");
            string actual=matches[0]; StoryFunction[] functions;
            if(!cache.TryGetValue(actual,out functions)) { string source=readText(actual); if(source.Length>16000000) throw new FormatException("脚本过大，保守保留"); cache[actual]=functions=Analyze(actual,source); }
            var found=functions.Where(f=>f.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(found.Length!=1) throw new FormatException("函数缺失、重复或使用未识别声明，保守保留");
            return found[0];
        }
        static string FunctionPrefix(string key) { return key.Substring(0,key.LastIndexOf('|')+1); }
        bool Denied(string prefix) { return rules.Any(r=>r.Value=="deny" && r.Key.StartsWith(prefix,StringComparison.Ordinal)); }
        public StoryDecision Evaluate(string[] context)
        {
            lock(cache)
            {
                var decision=new StoryDecision { Allowed=true,Kind="default-allow",Reason="默认开启 AI；未命中手动关闭记录" };
                var leaves=new List<StoryFunction>(); var problems=new List<string>();
                foreach(string site in (context ?? new string[0]).Distinct())
                {
                    var parts=site.Split('|'); int line;
                    if(parts.Length!=4 || !int.TryParse(parts[2],out line)) { problems.Add("调用位置不完整"); continue; }
                    // Explicit disables are attached to game/file/function and survive source edits.
                    string prefix=Normalize(parts[1]).ToLowerInvariant()+"|"+parts[0].ToUpperInvariant()+"|";
                    if(Denied(prefix)) { decision.Allowed=false; decision.Kind="manual-deny"; decision.Reason="命中本游戏手动关闭记录（含父调用）"; decision.Location=parts[1]+":"+line; }
                    try
                    {
                        var f=Resolve(parts[0],parts[1]);
                        if(parts[3]=="leaf") leaves.Add(f);
                        if(Denied(FunctionPrefix(f.Key))) { decision.Allowed=false; decision.Kind="manual-deny"; decision.Reason="命中本游戏手动关闭记录（含父调用）"; decision.Location=f.File+":"+line; }
                    }
                    catch(Exception ex) { problems.Add(ex.Message); }
                }
                leaves=leaves.GroupBy(f=>f.Key).Select(g=>g.First()).ToList();
                decision.RuleKey=leaves.Count==1 ? leaves[0].Key : "";
                if(decision.Location.Length==0 && leaves.Count>0) decision.Location=leaves[0].File+":"+leaves[0].Start+" @"+leaves[0].Name;
                if(decision.Allowed && leaves.Count>0 && leaves.All(f=>RuleAction(f.Key)=="allow")) { decision.Kind="manual-allow"; decision.Reason="用户已开启此函数的 AI"; }
                if(decision.Allowed && (context==null || context.Length==0 || problems.Count>0)) decision.Reason+="；来源信息不完整，仍按默认开启，可仅关闭本段";
                if(problems.Count>0) decision.Reason+="（"+string.Join("；",problems.Distinct().Take(3))+"）";
                return decision;
            }
        }
        public void SetRule(string key,string action)
        {
            if(string.IsNullOrEmpty(key)) throw new FormatException("本段涉及多个函数或来源不明，请使用仅本段允许/禁止。");
            if(action!="allow" && action!="deny" && action!="auto") throw new FormatException("规则操作无效。");
            ApplyRules(new Dictionary<string,string>{{key,action}});
        }
        public string RuleAction(string key) { lock(cache) { string action; return Denied(FunctionPrefix(key)) ? "deny" : rules.TryGetValue(key,out action) ? action : "auto"; } }
        public void ApplyRules(Dictionary<string,string> edits)
        {
            lock(cache)
            {
                foreach(var edit in edits) if(!Regex.IsMatch(edit.Key,@"^[^|]+\|[^|]+\|[a-f0-9]{64}$") || (edit.Value!="auto" && edit.Value!="allow" && edit.Value!="deny")) throw new FormatException("函数规则无效");
                var old=new Dictionary<string,string>(rules);
                foreach(var edit in edits) { string prefix=FunctionPrefix(edit.Key); foreach(string oldKey in rules.Keys.Where(k=>k.StartsWith(prefix,StringComparison.Ordinal)).ToArray()) rules.Remove(oldKey); if(edit.Value!="auto") rules[edit.Key]=edit.Value; }
                try { Save(); } catch { rules.Clear(); foreach(var r in old) rules[r.Key]=r.Value; throw; }
            }
        }
        public StoryFunction[] ReadDocument(Action<int,int> progress,Action<string> error,Func<bool> cancelled)
        {
            string[] files;
            lock(cache) { if(paths==null) paths=listFiles().Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).Take(100001).ToArray(); files=paths; }
            if(files.Length>100000) throw new FormatException("脚本数量过多");
            var result=new List<StoryFunction>();
            for(int i=0;i<files.Length;i++)
            {
                if(cancelled()) throw new OperationCanceledException();
                try
                {
                    lock(cache)
                    {
                        StoryFunction[] functions;
                        if(!cache.TryGetValue(files[i],out functions)) { string text=readText(files[i]); if(text.Length>16000000) throw new FormatException("脚本过大"); cache[files[i]]=functions=Analyze(files[i],text); }
                        result.AddRange(functions);
                    }
                }
                catch(Exception ex) { error(files[i]+"："+ex.GetType().Name); }
                progress(i+1,files.Length);
            }
            return result.GroupBy(f=>f.Key).Select(g=>g.First()).OrderBy(f=>f.File,StringComparer.OrdinalIgnoreCase).ThenBy(f=>f.Start).ToArray();
        }
        public string Export()
        {
            lock(cache) return Json.Codec().Serialize(new Dictionary<string,object>{{"schema",1},{"default","allow"},{"rules",rules.Select(r=>(object)new Dictionary<string,object>{{"key",r.Key},{"action",r.Value}}).ToArray()}});
        }
        public void Import(string json,bool save=true)
        {
            if(json.Length>32*1024*1024) throw new FormatException("剧情规则超过 32 MB。");
            var root=Json.Object(Json.Codec().DeserializeObject(json)); if(Json.Int(root,"schema")!=1) throw new FormatException("不支持的剧情规则版本。");
            var next=new Dictionary<string,string>();
            foreach(var row in Json.Array(Json.Get(root,"rules")))
            {
                var item=Json.Object(row); string key=Json.Text(item,"key"), action=Json.Text(item,"action");
                if(action=="auto") continue;
                if((action!="allow" && action!="deny") || !Regex.IsMatch(key,@"^[^|]+\|[^|]+\|[a-f0-9]{64}$") || key.Length>2048) throw new FormatException("规则必须使用具体函数和有效的脚本指纹。");
                if(next.ContainsKey(key) && next[key]!=action) throw new FormatException("同一函数存在冲突规则。");
                next[key]=action;
            }
            lock(cache)
            {
                var old=new Dictionary<string,string>(rules);
                rules.Clear(); foreach(var row in next) rules[row.Key]=row.Value;
                try { if(save) Save(); } catch { rules.Clear(); foreach(var row in old) rules[row.Key]=row.Value; throw; }
            }
        }
        void Save()
        {
            if(rulesPath==null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(rulesPath));
            string temp=rulesPath+".tmp"; File.WriteAllText(temp,Export(),new UTF8Encoding(false));
            if(File.Exists(rulesPath)) File.Replace(temp,rulesPath,null); else File.Move(temp,rulesPath);
        }
    }
}
