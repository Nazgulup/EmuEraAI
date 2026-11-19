using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace MinorShift.Emuera.AI
{
    internal sealed class StoryFunctionDocument : IDisposable
    {
        readonly StoryPolicy policy;
        readonly string path;
        readonly Action changed;
        readonly CancellationTokenSource cancel=new CancellationTokenSource();
        readonly Dictionary<string,string> notes=new Dictionary<string,string>();
        StoryFunction[] functions=new StoryFunction[0];
        readonly List<string> errors=new List<string>();
        Task work;
        public string Id { get; }=Guid.NewGuid().ToString("N");
        public string State { get; private set; }="idle";
        public string Reason { get; private set; }="尚未建立函数清单";
        public int Done, Total;
        public StoryFunctionDocument(StoryPolicy source,string file,Action notify) { policy=source; path=file; changed=notify; }
        public Task Start()
        {
            lock(notes) return work ?? (work=Task.Run(()=> {
                try
                {
                    State="scanning"; Reason="正在按函数名与脚本结构建立清单"; changed();
                    if(File.Exists(path) && new FileInfo(path).Length<=64*1024*1024)
                        try { foreach(var row in Json.Array(Json.Get(Json.Object(Json.Codec().DeserializeObject(File.ReadAllText(path))),"rules"))) { var item=Json.Object(row); string note=Json.Text(item,"note"); if(note.Length>0) notes[Json.Text(item,"key")]=note; } } catch { /* damaged notes cannot permit AI */ }
                    functions=policy.ReadDocument((done,total)=> { Done=done; Total=total; if(done==total || done%20==0) changed(); },message=>errors.Add(message),()=>cancel.IsCancellationRequested);
                    cancel.Token.ThrowIfCancellationRequested();
                    State="ready"; Reason="已建立 "+functions.Length+" 个函数；默认全部开启 AI，可取消勾选关闭指定剧情/函数"+(errors.Count>0 ? "；"+errors.Count+" 个脚本读取失败，未列出；默认开启 AI" : "");
                    Save(); changed();
                }
                catch(OperationCanceledException) { State="cancelled"; }
                catch(Exception ex) { State="error"; Reason="函数清单未完成："+ex.GetType().Name+"；不影响默认生成，可仅关闭本段"; changed(); }
            }));
        }
        Dictionary<string,object> Row(StoryFunction f) => new Dictionary<string,object>{{"key",f.Key},{"file",f.File},{"function",f.Name},{"line",f.Start},{"classification",f.Kind},{"reason",f.Reason},{"action",policy.RuleAction(f.Key)},{"note",notes.TryGetValue(f.Key,out string note) ? note : ""}};
        public Dictionary<string,object> Page(string query,int page,string kind)
        {
            lock(notes)
            {
                query=(query ?? "").Trim(); if(query.Length>200) query=query.Substring(0,200);
                var filtered=functions.Where(f=>(kind=="" || f.Kind==kind) && (query.Length==0 || f.Name.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0 || f.File.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0)).ToArray();
                page=Math.Max(0,Math.Min(page,Math.Max(0,(filtered.Length-1)/30)));
                return new Dictionary<string,object>{{"type","aiStoryDocument"},{"documentId",Id},{"state",State},{"reason",Reason},{"done",Done},{"total",Total},{"count",filtered.Length},{"page",page},{"query",query},{"kind",kind},{"rows",filtered.Skip(page*30).Take(30).Select(f=>(object)Row(f)).ToArray()}};
            }
        }
        public void Update(object[] edits)
        {
            lock(notes)
            {
                if(State!="ready") throw new InvalidOperationException("请等待函数清单完成");
                if(edits.Length>1000) throw new FormatException("一次最多保存 1000 个函数");
                var valid=new HashSet<string>(functions.Select(f=>f.Key)); var changes=new Dictionary<string,string>(); var nextNotes=new Dictionary<string,string>();
                foreach(var entry in edits)
                {
                    var item=Json.Object(entry); string key=Json.Text(item,"key"), note=Json.Text(item,"note");
                    if(!valid.Contains(key) || note.Length>1000) throw new FormatException("函数来源已变化或备注过长，请重新查看清单");
                    changes[key]=Json.Text(item,"action"); nextNotes[key]=note;
                }
                policy.ApplyRules(changes); foreach(var note in nextNotes) notes[note.Key]=note.Value; Save();
            }
        }
        public string Export()
        {
            lock(notes) { if(State!="ready") throw new InvalidOperationException("请等待函数清单完成"); return Json.Codec().Serialize(new Dictionary<string,object>{{"schema",1},{"default","allow"},{"document","generated-functions"},{"rules",functions.Select(f=>(object)Row(f)).ToArray()},{"readErrors",errors.ToArray()}}); }
        }
        void Save() { Directory.CreateDirectory(Path.GetDirectoryName(path)); string temporary=path+".tmp"; File.WriteAllText(temporary,Export(),new UTF8Encoding(false)); if(File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path); }
        public void Dispose() { cancel.Cancel(); }
    }
}
