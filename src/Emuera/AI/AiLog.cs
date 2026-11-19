using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MinorShift.Emuera.AI
{
    internal sealed class AiLog
    {
        readonly object gate=new object();
        readonly Queue<string> recent=new Queue<string>();
        readonly HashSet<string> secrets=new HashSet<string>(StringComparer.Ordinal);
        public string FilePath { get; private set; }
        public AiLog(string path=null)
        {
            FilePath=path;
            try { if(path!=null && File.Exists(path)) foreach(string line in File.ReadLines(path)) { recent.Enqueue(line); if(recent.Count>400) recent.Dequeue(); } } catch { }
        }
        public void Protect(string secret) { if(!string.IsNullOrWhiteSpace(secret)) lock(gate) secrets.Add(secret); }
        string Clean(string value,int limit=4096)
        {
            if(value==null) return "";
            foreach(string secret in secrets) value=value.Replace(secret,"[REDACTED]");
            value=Regex.Replace(value,@"(?i)(Bearer\s+)[^\s""<>]+","$1[REDACTED]");
            value=Regex.Replace(value,@"(?i)((?:api[_-]?key|access[_-]?token|authorization)[""']?\s*[:=]\s*[""']?)[^\s""',}]+","$1[REDACTED]");
            return value.Length>limit ? value.Substring(0,limit)+"…[截断]" : value;
        }
        // Redact before splitting so a credential crossing a chunk boundary cannot leak.
        // Long dialogue is retained in numbered records instead of silently truncated.
        public void WriteText(string stage,string trace,string message,string value)
        {
            lock(gate)
            {
                value=value ?? ""; int characters=value.Length;
                string clean=Clean(value,int.MaxValue);
                var chunks=new List<string>();
                for(int offset=0;offset<clean.Length;)
                {
                    int length=Math.Min(3500,clean.Length-offset);
                    if(offset+length<clean.Length && char.IsHighSurrogate(clean[offset+length-1]) && char.IsLowSurrogate(clean[offset+length])) length--;
                    chunks.Add(clean.Substring(offset,length)); offset+=length;
                }
                if(chunks.Count==0) chunks.Add("");
                for(int i=0;i<chunks.Count;i++)
                    Write(stage,trace,message,"part",i+1,"parts",chunks.Count,"characters",characters,"text",chunks[i]);
            }
        }
        public void Write(string stage,string trace,string message,params object[] fields)
        {
            lock(gate)
            {
                var row=new Dictionary<string,object>{{"time",DateTimeOffset.Now.ToString("O")},{"request",trace ?? ""},{"stage",stage},{"message",Clean(message)}};
                for(int i=0;i+1<fields.Length;i+=2) row[Convert.ToString(fields[i])]=fields[i+1] is string ? Clean((string)fields[i+1]) : fields[i+1];
                string line=Json.Codec().Serialize(row); recent.Enqueue(line); if(recent.Count>400) recent.Dequeue();
                try
                {
                    if(FilePath==null) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    if(File.Exists(FilePath) && new FileInfo(FilePath).Length>4*1024*1024) File.Copy(FilePath,FilePath+".previous",true);
                    if(File.Exists(FilePath) && new FileInfo(FilePath).Length>4*1024*1024) File.WriteAllText(FilePath,"",new UTF8Encoding(false));
                    File.AppendAllText(FilePath,line+Environment.NewLine,new UTF8Encoding(false));
                }
                catch { /* Diagnostics never interrupt the interpreter or fallback. */ }
            }
        }
        public string ReadRecent() { lock(gate) return string.Join(Environment.NewLine,recent.ToArray()); }
        public void Export(string path) { File.WriteAllText(path,ReadRecent(),new UTF8Encoding(false)); }
    }
}
