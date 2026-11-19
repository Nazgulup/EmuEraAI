using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

namespace MinorShift.Emuera.AI
{
    internal sealed class Preset
    {
        public const int MaxFileBytes=32*1024*1024;
        // Settings wrap the raw preset in another JSON string, which may escape each character.
        public const int MaxEnvelopeChars=256*1024*1024;
        // Provider/model limits vary. Validate the wire integer, not a guessed model ceiling.
        public static int ReadTokenLimit(object value,string label)
        {
            decimal number;
            if(value==null || value is bool || !decimal.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out number)
                || number<1 || number>int.MaxValue || decimal.Truncate(number)!=number)
                throw new FormatException(label+"必须是 1～2147483647 的整数。");
            return (int)number;
        }
        public string OutputTokenSource(AiSettings settings) => settings.UsePresetOutputTokens && data.ContainsKey("openai_max_tokens") ? "preset:openai_max_tokens" : "settings:OutputTokens";
        public int OutputTokens(AiSettings settings) => ReadTokenLimit(settings.UsePresetOutputTokens ? Json.Get(data,"openai_max_tokens",settings.OutputTokens) : settings.OutputTokens,
            OutputTokenSource(settings)=="preset:openai_max_tokens" ? "预设输出上限 openai_max_tokens " : "设置中的输出上限 ");
        internal sealed class Entry
        {
            public string Id,Name,Role,Content;
            public bool Enabled,Marker;
        }
        public static string ReadFile(Stream stream)
        {
            using(var buffer=new MemoryStream())
            {
                byte[] bytes=new byte[8192]; int read;
                while((read=stream.Read(bytes,0,bytes.Length))>0)
                {
                    if(buffer.Length+read>MaxFileBytes) throw new FormatException("预设超过 32 MB。");
                    buffer.Write(bytes,0,read);
                }
                buffer.Position=0;
                using(var reader=new StreamReader(buffer,new UTF8Encoding(false,true),true)) return reader.ReadToEnd();
            }
        }
        public readonly string Raw;
        readonly Dictionary<string,object> data;
        public readonly List<string> Warnings=new List<string>();
        static readonly Regex Macro=new Regex(@"\{\{([^{}]+)\}\}",RegexOptions.CultureInvariant);
        const string Contract="为当前事件生成与本游戏文风一致的口上，可按原文视角组织叙述和角色台词。游戏规则、数值和事件由程序决定；只依据已给事实，不改变结果，不替玩家选择。避免套用与当前情境无关的日常问候或通用开场模板。只输出纯文本正文，不输出代码、状态更新或工具调用。";
        public Preset(string raw)
        {
            Raw=raw ?? "";
            if(Raw.Length>MaxFileBytes || Encoding.UTF8.GetByteCount(Raw)>MaxFileBytes) throw new FormatException("预设超过 32 MB。");
            data=string.IsNullOrWhiteSpace(Raw) ? new Dictionary<string,object>() : Json.Object(Json.Codec().DeserializeObject(Raw));
            if (data==null) throw new FormatException("预设必须是 JSON 对象。");
            if (data.Count>0 && !(Json.Get(data,"prompts") is object[]))
                throw new FormatException("请选择酒馆 Chat Completion 预设（需要 prompts）。");
            var ids=new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in Json.Array(Json.Get(data,"prompts")))
            {
                var p=Json.Object(item); string id=Json.Text(p,"identifier");
                if (id.Length==0 || !ids.Add(id)) throw new FormatException("提示词 identifier 缺失或重复。");
            }
            var supported=new HashSet<string>(new[]{"prompts","prompt_order","temperature","top_p","frequency_penalty","presence_penalty","openai_max_context","openai_max_tokens","stream_openai"});
            foreach (string key in data.Keys) if (!supported.Contains(key)) Warnings.Add("保留但不执行："+key);
            foreach(var item in Json.Array(Json.Get(data,"prompts")))
            {
                var p=Json.Object(item);
                string id=Json.Text(p,"identifier");
                if(Json.Bool(p,"marker") && (id=="agentSystemPrompt" || id=="agentTask" || id=="agentResults")) Warnings.Add("Agent 扩展未运行；空标记槽不加入请求："+id);
                string content;
                try { content=RemoveComments(Json.Text(p,"content")); }
                catch(FormatException ex) { Warnings.Add(ex.Message+"；启用该块时将回退原口上。"); continue; }
                foreach(Match m in Macro.Matches(content))
                    if (!SupportedMacro(m.Groups[1].Value)) Warnings.Add("未支持的宏："+m.Value+"；启用该块时将回退原口上。");
                if (Json.Array(Json.Get(p,"injection_trigger")).Length>0) Warnings.Add("生成触发器按 normal/regenerate 处理。");
            }
        }
        Dictionary<string,object> ActiveGroup()
        {
            var groups=Json.Array(Json.Get(data,"prompt_order")).Select(Json.Object).ToList();
            return groups.FirstOrDefault(g=>Json.Int(g,"character_id")==100001) ?? groups.FirstOrDefault(g=>Json.Int(g,"character_id")==100000) ?? groups.FirstOrDefault();
        }
        public List<Entry> Entries()
        {
            var prompts=Json.Array(Json.Get(data,"prompts")).Select(Json.Object).ToList();
            var group=ActiveGroup();
            var order=group==null ? prompts.Select(p=>(object)new Dictionary<string,object>{{"identifier",Json.Text(p,"identifier")},{"enabled",true}}).ToArray() : Json.Array(Json.Get(group,"order"));
            var result=new List<Entry>(); var seen=new HashSet<string>();
            foreach(var entry in order.Concat(prompts.Select(p=>(object)new Dictionary<string,object>{{"identifier",Json.Text(p,"identifier")},{"enabled",false}})))
            {
                var e=Json.Object(entry); string id=Json.Text(e,"identifier");
                if(!seen.Add(id)) continue;
                var p=prompts.FirstOrDefault(x=>Json.Text(x,"identifier")==id);
                if(p==null) continue;
                result.Add(new Entry { Id=id,Name=Json.Text(p,"name",id),Role=Json.Text(p,"role","system"),Content=Json.Text(p,"content"),Marker=Json.Bool(p,"marker"),Enabled=Json.Bool(e,"enabled",true)&&Json.Bool(p,"enabled",true) });
            }
            return result;
        }
        public string ApplyEntries(IList<Entry> entries)
        {
            var prompts=Json.Array(Json.Get(data,"prompts")).Select(Json.Object).ToDictionary(p=>Json.Text(p,"identifier"));
            if(entries.Count!=prompts.Count || entries.Select(e=>e.Id).Distinct().Count()!=entries.Count || entries.Any(e=>!prompts.ContainsKey(e.Id))) throw new FormatException("预设条目列表不完整，请重新加载。");
            var group=ActiveGroup();
            if(group==null) { group=new Dictionary<string,object>{{"character_id",100001}}; data["prompt_order"]=new object[]{group}; }
            var oldOrder=Json.Array(Json.Get(group,"order")).Select(Json.Object).ToList();
            var order=new List<object>();
            foreach(var e in entries)
            {
                var row=oldOrder.FirstOrDefault(x=>Json.Text(x,"identifier")==e.Id) ?? new Dictionary<string,object>{{"identifier",e.Id}};
                row["enabled"]=e.Enabled; prompts[e.Id]["enabled"]=e.Enabled; order.Add(row);
            }
            group["order"]=order.ToArray();
            return Json.Codec().Serialize(data);
        }
        static bool SupportedMacro(string name)
        {
            var match=Regex.Match(name,@"^\s*([a-z_][a-z0-9_]*)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            return new[]{"char","user","description","personality","scenario","persona","mesexamples","worldinfo","original","newline","noop","trim","setvar","getvar","addvar","random","roll","lastusermessage"}.Contains(match.Groups[1].Value.ToLowerInvariant());
        }
        // Remove comments before checking or expanding macros. Their bodies are inert,
        // including nested {{...}} tokens, and the imported/exported JSON stays intact.
        static string RemoveComments(string text)
        {
            text=text ?? "";
            var result=new StringBuilder(); int copied=0,scan=0;
            while(scan<text.Length)
            {
                int start=text.IndexOf("{{",scan,StringComparison.Ordinal); if(start<0) break;
                int name=start+2; while(name<text.Length && char.IsWhiteSpace(text[name])) name++;
                if(name+1>=text.Length || text[name]!='/' || text[name+1]!='/' || (name+2<text.Length && text[name+2]=='/')) { scan=start+2; continue; }
                int end=MacroEnd(text,start);
                if(end<0) throw new FormatException("预设注释宏缺少结束符 }}。");
                if(string.IsNullOrWhiteSpace(text.Substring(name+2,end-name-4)))
                {
                    // A bare // tag opens the documented scoped comment syntax.
                    int depth=1,position=end;
                    while(depth>0)
                    {
                        int next=text.IndexOf("{{",position,StringComparison.Ordinal);
                        if(next<0) throw new FormatException("预设块注释缺少结束标签 {{///}}。");
                        int nextEnd=MacroEnd(text,next);
                        if(nextEnd<0) throw new FormatException("预设块注释缺少结束标签 {{///}}。");
                        string tag=text.Substring(next+2,nextEnd-next-4).Trim();
                        if(tag=="//") depth++; else if(tag=="///") depth--;
                        position=nextEnd;
                    }
                    end=position;
                }
                result.Append(text,copied,start-copied); copied=end; scan=end;
            }
            return copied==0 ? text : result.Append(text,copied,text.Length-copied).ToString();
        }
        static int MacroEnd(string text,int start)
        {
            int depth=1;
            for(int i=start+2;i+1<text.Length;i++)
            {
                if(text[i]=='{' && text[i+1]=='{') { depth++; i++; }
                else if(text[i]=='}' && text[i+1]=='}') { if(--depth==0) return i+2; i++; }
            }
            return -1;
        }
        sealed class Expansion
        {
            readonly DialogueSnapshot snapshot;
            readonly AiSettings settings;
            readonly Dictionary<string,string> variables=new Dictionary<string,string>(StringComparer.Ordinal);
            readonly Random random;
            int operations;
            long variableChars;
            public Expansion(DialogueSnapshot snapshot,AiSettings settings,string raw)
            {
                this.snapshot=snapshot; this.settings=settings;
                // Prompt-only RNG: stable within one event, never touches the ERA RNG.
                uint seed=2166136261;
                unchecked { foreach(char c in raw) seed=(seed^c)*16777619; foreach(char c in snapshot.EventId ?? "") seed=(seed^c)*16777619; }
                random=new Random((int)(seed&0x7fffffff));
            }
            public string Expand(string text) { return ExpandText(RemoveComments(text),0); }
            string ExpandText(string text,int depth)
            {
                if(depth>48) throw new FormatException("预设宏嵌套过深。");
                var output=new StringBuilder(); int cursor=0;
                while(cursor<text.Length)
                {
                    int start=text.IndexOf("{{",cursor,StringComparison.Ordinal);
                    if(start<0) { output.Append(text,cursor,text.Length-cursor); break; }
                    output.Append(text,cursor,start-cursor);
                    int end=MacroEnd(text,start);
                    if(end<0) throw new FormatException("预设宏缺少结束符 }}。");
                    if(++operations>100000) throw new FormatException("预设宏数量过多。");
                    string body=ExpandText(text.Substring(start+2,end-start-4),depth+1);
                    var match=Regex.Match(body,@"^\s*([a-z_][a-z0-9_]*)(.*)$",RegexOptions.IgnoreCase|RegexOptions.Singleline|RegexOptions.CultureInvariant);
                    if(!match.Success) throw new FormatException("预设宏语法无效。");
                    string name=match.Groups[1].Value.ToLowerInvariant(),arg=match.Groups[2].Value.TrimStart();
                    if(arg.StartsWith("::",StringComparison.Ordinal)) arg=arg.Substring(2);
                    else if(arg.StartsWith(":",StringComparison.Ordinal)) arg=arg.Substring(1);
                    if(name=="trim")
                    {
                        while(output.Length>0 && (output[output.Length-1]=='\n' || output[output.Length-1]=='\r')) output.Length--;
                        while(end<text.Length && (text[end]=='\n' || text[end]=='\r')) end++;
                    }
                    else output.Append(Evaluate(name,arg));
                    if(output.Length>MaxFileBytes) throw new FormatException("预设宏展开后超过 32 MB。");
                    cursor=end;
                }
                if(output.Length>MaxFileBytes) throw new FormatException("预设宏展开后超过 32 MB。");
                return output.ToString();
            }
            string Evaluate(string name,string arg)
            {
                switch(name)
                {
                    case "getvar": { string value; return variables.TryGetValue(arg.Trim(),out value) ? value : ""; }
                    case "setvar": case "addvar":
                    {
                        int split=arg.IndexOf("::",StringComparison.Ordinal);
                        if(split<0) throw new FormatException("预设变量宏需要 名称::值。");
                        string key=arg.Substring(0,split).Trim(),value=arg.Substring(split+2);
                        if(key.Length==0) throw new FormatException("预设变量名称为空。");
                        if(name=="addvar")
                        {
                            string old; if(!variables.TryGetValue(key,out old)) old="";
                            double left,right;
                            if(double.TryParse(old.Length==0 ? "0" : old,NumberStyles.Float,CultureInfo.InvariantCulture,out left) && double.TryParse(value.Length==0 ? "0" : value,NumberStyles.Float,CultureInfo.InvariantCulture,out right))
                            {
                                double sum=left+right; if(double.IsInfinity(sum)||double.IsNaN(sum)) throw new FormatException("预设变量计算结果无效。");
                                value=sum.ToString("G",CultureInfo.InvariantCulture);
                            }
                            else value=old+value;
                        }
                        if(value.Length>MaxFileBytes) throw new FormatException("预设变量内容过大。");
                        string previous; variables.TryGetValue(key,out previous);
                        variableChars+=value.Length-(previous ?? "").Length;
                        if(variableChars>MaxFileBytes) throw new FormatException("预设变量内容总量超过 32 MB。");
                        variables[key]=value; return "";
                    }
                    case "random":
                    {
                        var choices=arg.Contains("::") ? arg.Split(new[]{"::"},StringSplitOptions.None) : Regex.Split(arg,@"(?<!\\),").Select(x=>x.Trim().Replace(@"\,",",")).ToArray();
                        return choices[random.Next(choices.Length)];
                    }
                    case "roll":
                    {
                        string formula=arg.Trim(); if(Regex.IsMatch(formula,@"^\d+$")) formula="1d"+formula;
                        var dice=Regex.Match(formula,@"^(\d*)d(\d+)([+-]\d+)?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
                        int count,sides,offset=0;
                        if(!dice.Success || !int.TryParse(dice.Groups[1].Value.Length==0 ? "1" : dice.Groups[1].Value,out count) || !int.TryParse(dice.Groups[2].Value,out sides) || (dice.Groups[3].Success && !int.TryParse(dice.Groups[3].Value,out offset)) || count<1 || count>1000 || sides<1 || sides>1000000) throw new FormatException("预设 roll 支持 NdM 或 NdM±K（最多 1000 个骰子，每个最多 1000000 面）。");
                        long total=offset; for(int i=0;i<count;i++) total+=random.Next(sides)+1;
                        return total.ToString(CultureInfo.InvariantCulture);
                    }
                    case "lastusermessage": return HistoryText(snapshot);
                    case "char": return snapshot.Character; case "user": return snapshot.User;
                    case "description": return settings.CharacterDescription ?? "";
                    case "personality": return settings.Personality ?? "";
                    case "scenario": return (settings.World ?? "")+"\n"+snapshot.Facts;
                    case "worldinfo": return settings.World ?? "";
                    case "original": return snapshot.Addresses.Mask(snapshot.Original);
                    case "newline": return "\n";
                    case "persona": case "mesexamples": case "noop": return "";
                    default: throw new FormatException("预设含未支持的宏："+name);
                }
            }
        }
        static string HistoryText(DialogueSnapshot snapshot) { return "当前事件事实（只读）：\n"+snapshot.Facts+"\n本次原口上参考：\n"+snapshot.Addresses.Mask(snapshot.Original)+"\n请为同一事件生成口上，不推进事件。"+(Regex.IsMatch(snapshot.Hook ?? "",@"^(PRI|PRE)TRAIN_MESSAGE",RegexOptions.IgnoreCase) ? "这是行动开始前的开场段落：沿用当前角色的称呼、关系和态度，以本游戏的语气承接原文；不要写成无关的日常问候，不要提前描写后续行动或结算结果。" : ""); }
        static Dictionary<string,object> Message(string role,string content)
        { return new Dictionary<string,object>{{"role",role},{"content",content}}; }
        sealed class Injection { public string Role,Text; public int Depth,Order,Index; }
        public Dictionary<string,object> Build(DialogueSnapshot snapshot,AiSettings settings,bool regenerate,string previousReply=null,int regenerationAttempt=1)
        {
            var messages=new List<Dictionary<string,object>>();
            var history=new List<Dictionary<string,object>> { Message("user",HistoryText(snapshot)) };
            var expansion=new Expansion(snapshot,settings,Raw);
            var prompts=Json.Array(Json.Get(data,"prompts")).Select(Json.Object).ToDictionary(p=>Json.Text(p,"identifier"));
            var group=ActiveGroup();
            var order=group==null ? prompts.Keys.Select(id=>(object)new Dictionary<string,object>{{"identifier",id},{"enabled",true}}).ToArray() : Json.Array(Json.Get(group,"order"));
            var selected=new List<Tuple<string,string>>(); var injections=new List<Injection>(); bool hasHistory=false;
            long expandedBytes=0;
            foreach(var entry in order)
            {
                var e=Json.Object(entry); if(!Json.Bool(e,"enabled",true)) continue;
                string id=Json.Text(e,"identifier"); Dictionary<string,object> p;
                if(!prompts.TryGetValue(id,out p)) throw new FormatException("预设顺序引用了不存在的块："+id);
                if(!Json.Bool(p,"enabled",true)) continue;
                var triggers=Json.Array(Json.Get(p,"injection_trigger")).Select(Convert.ToString).ToArray();
                if(triggers.Length>0 && !triggers.Contains(regenerate ? "regenerate" : "normal")) continue;
                string role=Json.Text(p,"role","system"),text=Json.Text(p,"content");
                if (Json.Bool(p,"marker"))
                {
                    switch(id)
                    {
                        case "chatHistory": selected.Add(Tuple.Create("history","")); hasHistory=true; continue;
                        case "charDescription": text=settings.CharacterDescription; break;
                        case "charPersonality": text=settings.Personality; break;
                        case "scenario": text=(settings.World ?? "")+"\n"+snapshot.Facts; break;
                        case "worldInfoBefore": text=settings.World; break;
                        case "worldInfoAfter": case "personaDescription": case "dialogueExamples": text=""; break;
                        case "agentSystemPrompt": case "agentTask": case "agentResults":
                            if(!string.IsNullOrWhiteSpace(text)) throw new FormatException("尚未支持含内容的 Agent 标记槽："+id);
                            text=""; break;
                        default: throw new FormatException("未支持的标记槽："+id);
                    }
                }
                if(role!="system" && role!="user" && role!="assistant") throw new FormatException("提示词角色无效。");
                text=expansion.Expand(text); if(string.IsNullOrWhiteSpace(text)) continue;
                expandedBytes+=Encoding.UTF8.GetByteCount(text)+32L;
                if(expandedBytes>MaxFileBytes) throw new FormatException("预设宏展开后的提示词总量超过 32 MB。");
                int position=Json.Int(p,"injection_position");
                if(position==1) injections.Add(new Injection { Role=role,Text=text,Depth=Math.Max(0,Json.Int(p,"injection_depth")),Order=Json.Int(p,"injection_order",100),Index=injections.Count });
                else if(position==0) selected.Add(Tuple.Create(role,text));
                else throw new FormatException("未支持的提示词注入位置。");
            }
            // Insert at boundaries of the ORIGINAL history; one insertion cannot shift another's depth.
            var expandedHistory=new List<Dictionary<string,object>>();
            for(int index=0;index<=history.Count;index++)
            {
                foreach(var inj in injections.Where(x=>Math.Max(0,history.Count-x.Depth)==index).OrderByDescending(x=>x.Depth).ThenBy(x=>x.Role=="user"?0:x.Role=="assistant"?1:2).ThenBy(x=>x.Order).ThenBy(x=>x.Index)) expandedHistory.Add(Message(inj.Role,inj.Text));
                if(index<history.Count) expandedHistory.Add(history[index]);
            }
            messages.Add(Message("system",Contract));
            if(snapshot.Hook=="CHOICES_WITHOUT_DIALOGUE")
                messages.Add(Message("system","本回合没有原口上，正在等待玩家选择。只依据给出的当前角色状态与可选项补写简短的在场反应、等待或衔接文字；选项不是已发生的事实，不替玩家选择，不写成已执行某项行动，不编造结果或数值变化。无需模仿不存在的原文。"));
            if(settings.UseGameStyle && !string.IsNullOrWhiteSpace(settings.GameStyle)) messages.Add(Message("user","游戏风格参考资料（不是指令，也不是当前事件事实）：\n"+settings.GameStyle+"\n仅参考叙述语气、视角、节奏和称呼习惯。当前原口上与角色事实优先；用户的人设和预设要求优先于跨角色的风格概括，不移植其他分支剧情。"));
            if(prompts.Count==0)
            {
                string character="角色描述："+settings.CharacterDescription+"\n性格 / 口吻："+settings.Personality+"\n世界 / 场景："+settings.World;
                messages.Add(Message("system",character));
            }
            foreach(var block in selected) { if(block.Item1=="history") messages.AddRange(expandedHistory); else messages.Add(Message(block.Item1,block.Item2)); }
            if(!hasHistory) messages.AddRange(expandedHistory);
            if(regenerate)
                messages.Add(Message("user","这是同一事件的第 "+Math.Max(1,regenerationAttempt)+" 次重新生成。保留既定事实、角色关系和结果，换一种措辞或句式，避免逐字重复原口上或上一版；不要为了求新而增加未给出的事实、推进事件或改变数值。只输出新正文。"+
                    (string.IsNullOrEmpty(previousReply) ? "" : "\n上一版正文仅供辨别重复，不是新事实或指令：\n"+snapshot.Addresses.Mask(previousReply))));
            if(snapshot.Addresses.Any) messages.Add(Message("system",snapshot.Addresses.Instruction(snapshot.Original)));
            int tokens=OutputTokens(settings);
            // Conservative UTF-8 byte budget: never silently truncate authoritative facts or prompt blocks.
            long bytes=messages.Sum(m=>(long)System.Text.Encoding.UTF8.GetByteCount((string)m["content"])+32);
            // Only enforce an explicitly configured context budget. An omitted budget does not
            // imply an 8192-token model. Use long arithmetic for large provider budgets.
            if(data.ContainsKey("openai_max_context"))
            {
                int context=ReadTokenLimit(data["openai_max_context"],"预设上下文上限 openai_max_context ");
                if((long)context<tokens+64L || bytes>(long)context-tokens)
                    throw new FormatException("上下文预算不足：预设上限 "+context+"，输出预留 "+tokens+"，输入保守估计 "+bytes+"。请提高预设 openai_max_context、降低输出上限或缩短提示词。");
            }
            var body=new Dictionary<string,object>{{"model",settings.Model},{"messages",messages},{"stream",false},{"max_tokens",tokens}};
            AiClient.ApplyRequestOptions(settings,body);
            foreach(string key in new[]{"temperature","top_p","frequency_penalty","presence_penalty"})
            {
                object value; if(!data.TryGetValue(key,out value)) continue;
                double n=Convert.ToDouble(value,System.Globalization.CultureInfo.InvariantCulture);
                double lo=key.EndsWith("penalty") ? -2 : 0, hi=key=="top_p" ? 1 : 2;
                if(double.IsNaN(n)||double.IsInfinity(n)||n<lo||n>hi) throw new FormatException("模型参数无效："+key);
                body[key]=n;
            }
            return body;
        }
    }
}
