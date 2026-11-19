using System;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Collections.Generic;

namespace MinorShift.Emuera.AI
{
    internal static class DialogueDiagnostics
    {
        internal static string Hash(string text)
        { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))).Replace("-","").ToLowerInvariant(); }
        internal static bool Equivalent(string a,string b)
        { return a!=null && b!=null && string.Equals(new string(a.Where(c=>!char.IsWhiteSpace(c)).ToArray()),new string(b.Where(c=>!char.IsWhiteSpace(c)).ToArray()),StringComparison.Ordinal); }
        static int Difference(string a,string b)
        {
            if(a==null || b==null) return -1;
            int n=Math.Min(a.Length,b.Length); for(int i=0;i<n;i++) if(a[i]!=b[i]) return i;
            return a.Length==b.Length ? -1 : n;
        }
        internal static void Compare(AiLog log,string trace,DialogueSnapshot snapshot,string result,string previous)
        {
            log.WriteText("dialogue.result",trace,"通过校验、交给显示层的 AI 正文",result);
            log.Write("dialogue.compare",trace,"AI 正文与原口上、上一版的逐字比较（空白等价仅忽略空白字符）",
                "eventId",snapshot.EventId,"hook",snapshot.Hook,"originalChars",snapshot.Original.Length,"replyChars",result.Length,
                "exactOriginal",string.Equals(snapshot.Original,result,StringComparison.Ordinal),"whitespaceEquivalentOriginal",Equivalent(snapshot.Original,result),
                "firstDifferenceOriginal",Difference(snapshot.Original,result),"hasPrevious",previous!=null,"previousChars",previous==null ? 0 : previous.Length,
                "exactPrevious",previous!=null && string.Equals(previous,result,StringComparison.Ordinal),"whitespaceEquivalentPrevious",Equivalent(previous,result),
                "firstDifferencePrevious",Difference(previous,result),"originalSha256",Hash(snapshot.Original),"replySha256",Hash(result));
        }
        internal static string Request(AiLog log,string trace,Dictionary<string,object> body,string previousHash,bool regenerate,int attempt,bool hasPrevious)
        {
            string hash=Hash(Json.Codec().Serialize(body));
            log.Write("request.fingerprint",trace,"实际请求指纹与采样参数；不记录完整提示词或密钥",
                "sha256",hash,"hasPreviousRequest",previousHash!=null,"sameAsPreviousRequest",previousHash!=null && hash==previousHash,
                "regenerate",regenerate,"regenerationAttempt",attempt,"hasPreviousReply",hasPrevious,
                "temperature",Json.Get(body,"temperature"),"topP",Json.Get(body,"top_p"),"frequencyPenalty",Json.Get(body,"frequency_penalty"),"presencePenalty",Json.Get(body,"presence_penalty"));
            return hash;
        }
    }
}
