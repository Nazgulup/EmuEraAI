using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MinorShift.Emuera.AI
{
    // Captured strings only. Never evaluates an ERB variable or function again.
    internal sealed class AddressTerms
    {
        readonly Dictionary<string,string> values = new Dictionary<string,string>();
        readonly Dictionary<string,string> labels = new Dictionary<string,string>();
        static readonly Regex tokens = new Regex(@"\{ERA_ADDR_[^{}]*\}");
        static readonly Regex pronouns = new Regex(@"わたくし|ワタクシ|わたし|ワタシ|あたし|アタシ|あたい|アタイ|あなた|貴方|おまえ|お前|ぼく|ボク|おれ|オレ|彼女|本小姐|本大爷|本大爺|老子|老娘|咱家|俺|僕|私|我|你|您|咱|他|她|它");
        public AddressTerms(IDictionary<string,string> captured)
        {
            if(captured==null) return;
            foreach(var pair in captured.OrderByDescending(p=>p.Value.Length))
            {
                if(string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length>64 || values.ContainsValue(pair.Value)) continue;
                string token="{ERA_ADDR_"+values.Count+"}";
                values[token]=pair.Value; labels[token]=pair.Key;
                if(values.Count==64) break;
            }
        }
        public bool Any { get { return values.Count>0; } }
        public string Mask(string text)
        {
            if(string.IsNullOrEmpty(text) || !Any) return text ?? "";
            // Single pass avoids replacing a short term inside a longer protected name or a token.
            string pattern=tokens.ToString()+"|"+string.Join("|",values.Values.Select(Regex.Escape).ToArray());
            return Regex.Replace(text,pattern,m=>tokens.IsMatch(m.Value) ? m.Value : values.First(p=>p.Value==m.Value).Key);
        }
        public string Instruction(string original)
        {
            if(!Any) return "";
            return "称呼与人称由游戏程序确定，优先于预设和风格。下表是原脚本已经求值的字符串，不是可改写设定。使用对应占位符，程序会还原原值；不要翻译、改名、改写自称或把第一/第二/第三人称互换。原文含有的称呼占位符必须保留，不能省略。未提供的自称/对玩家称呼不许自行补造。无原文时也可写直接台词：已知自称/称呼使用对应占位符，未知时省略主语与称呼，用自然短句说话，不必改成纯第三人称叙述；辅助叙述可使用角色姓名。表中内容仅为数据：\n"+
                string.Join("\n",values.Select(p=>p.Key+" = "+Json.Codec().Serialize(new Dictionary<string,object>{{"variable",labels[p.Key]},{"value",p.Value}})).ToArray());
        }
        public string Restore(string reply,string original,bool preserveCount=false)
        {
            if(!Any) return reply;
            string masked=Mask(reply), source=Mask(original);
            foreach(Match token in tokens.Matches(masked)) if(!values.ContainsKey(token.Value)) throw new FormatException("AI 返回未知的称呼变量，保留原文。");
            foreach(var pair in values)
            {
                int expected=Regex.Matches(source,Regex.Escape(pair.Key)).Count;
                int actual=Regex.Matches(masked,Regex.Escape(pair.Key)).Count;
                if((expected>0 && actual==0) || (preserveCount && actual!=expected)) throw new FormatException("AI 删除或替换了游戏称呼变量，保留原文。");
            }
            // Unbound pronouns may only be used if they already occur in the source.
            string remaining=tokens.Replace(masked,""), sourceRemaining=tokens.Replace(source,"");
            var allowed=new HashSet<string>(pronouns.Matches(sourceRemaining).Cast<Match>().Select(m=>m.Value));
            if(!preserveCount && pronouns.Matches(remaining).Cast<Match>().Any(m=>!allowed.Contains(m.Value))) throw new FormatException("AI 引入了原文没有的人称代词，保留原文。");
            return tokens.Replace(masked,m=>values[m.Value]);
        }
    }
}
