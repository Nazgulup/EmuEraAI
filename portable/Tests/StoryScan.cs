using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using MinorShift.Emuera.AI;
using MinorShift.Emuera.Runtime.Utils;

internal static class StoryScan
{
    public static void Run(string root)
    {
        Regex.CacheSize=256;
        string workspace=Directory.GetParent(root)!.FullName;
        string output=Path.Combine(root,"story-profiles"); Directory.CreateDirectory(output);
        var inventory=Json.Object(Json.Codec().DeserializeObject(File.ReadAllText(Path.Combine(root,"logs","story-scan","inventory.json"))));
        var games=Json.Array(inventory["games"]);
        var summary=new ConcurrentBag<object>();
        Parallel.ForEach(games,new ParallelOptions { MaxDegreeOfParallelism=4 },item=>
        {
            var row=Json.Object(item); string name=Json.Text(row,"game"), game=Path.GetFullPath(Path.Combine(workspace,name));
            string erb=Directory.GetDirectories(game).Single(p=>Path.GetFileName(p).Equals("ERB",StringComparison.OrdinalIgnoreCase));
            var rules=new List<object>(); int files=0, functions=0, routine=0, story=0, unknown=0; var errors=new List<string>();
            foreach(var path in Directory.EnumerateFiles(erb,"*",SearchOption.AllDirectories).Where(p=>p.EndsWith(".erb",StringComparison.OrdinalIgnoreCase)).OrderBy(p=>p,StringComparer.Ordinal))
            {
                string relative=Path.GetRelativePath(game,path).Replace('\\','/');
                try
                {
                    var info=new FileInfo(path); if(info.Length>32*1024*1024) { errors.Add(relative+": 文件超过 32 MB，保守未分析"); continue; }
                    string text=string.Join("\n",EncodingHandler.ReadAllLinesFromBytes(File.ReadAllBytes(path))); files++;
                    foreach(var f in StoryPolicy.Analyze(relative,text))
                    {
                        functions++; if(f.Kind=="routine") routine++; else if(f.Kind=="story") story++; else unknown++;
                        if(f.Kind!="unknown" || Regex.IsMatch(f.Name,"KOJO|TRAIN_MESSAGE|MESSAGE_COM|口上",RegexOptions.IgnoreCase))
                            rules.Add(new Dictionary<string,object>{{"key",f.Key},{"action","auto"},{"classification",f.Kind},{"reason",f.Reason},{"file",f.File},{"function",f.Name},{"line",f.Start}});
                    }
                }
                catch(Exception ex) { errors.Add(relative+": "+ex.GetType().Name); }
            }
            string filename=StoryPolicy.Digest(name).Substring(0,12)+".json";
            var profile=new Dictionary<string,object>{{"schema",1},{"game",name},{"default","protect-unknown"},{"instructions","action 默认为 auto，保持运行时调用链保护。可将指定函数改为 allow 或 deny 后导入；修改脚本后指纹不同的旧规则不生效。此文件仅包含元数据，不含游戏正文。"},{"rules",rules.ToArray()}};
            string json=Json.Codec().Serialize(profile); File.WriteAllText(Path.Combine(output,filename),json,new UTF8Encoding(false));
            bool fullGame=Directory.EnumerateFiles(Directory.GetDirectories(game).Single(p=>Path.GetFileName(p).Equals("CSV",StringComparison.OrdinalIgnoreCase))).Any(p=>Path.GetFileName(p).Equals("GAMEBASE.CSV",StringComparison.OrdinalIgnoreCase));
            var result=new Dictionary<string,object>{{"game",name},{"type",fullGame ? "game" : "script-package"},{"profile",filename},{"files",files},{"functions",functions},{"routineCandidates",routine},{"storySignals",story},{"unknown",unknown},{"errors",errors.ToArray()},{"templateBytes",Encoding.UTF8.GetByteCount(json)}};
            summary.Add(result); Console.WriteLine(Json.Codec().Serialize(result));
        });
        var sorted=summary.Cast<Dictionary<string,object>>().OrderBy(r=>Json.Text(r,"game"),StringComparer.Ordinal).ToArray();
        File.WriteAllText(Path.Combine(output,"index.json"),Json.Codec().Serialize(new Dictionary<string,object>{{"schema",1},{"games",sorted},{"note","日常候选不等于无条件放行：运行时必须检查完整调用链、事件条件和用户禁止规则。未识别语法默认保留原文。"}}),new UTF8Encoding(false));
        var report=new StringBuilder("ERA 游戏剧情保护扫描（仅本地静态分析）\n\n");
        report.AppendLine("游戏/脚本目录："+sorted.Length+"（含副本和补丁包）；含 GAMEBASE.CSV："+sorted.Count(r=>Json.Text(r,"type")=="game")+"；ERB 文件："+sorted.Sum(r=>Json.Int(r,"files"))+"；函数："+sorted.Sum(r=>Json.Int(r,"functions")));
        report.AppendLine("日常候选："+sorted.Sum(r=>Json.Int(r,"routineCandidates"))+"；剧情信号："+sorted.Sum(r=>Json.Int(r,"storySignals"))+"；不确定："+sorted.Sum(r=>Json.Int(r,"unknown")));
        report.AppendLine("\n所有模板默认 auto：不会因为导入模板而绕过剧情调用保护。打开 index.json 找到游戏对应的模板；要人工修改时，将具体条目的 action 改为 allow/deny，再在加载器的剧情保护页导入。规则绑定函数指纹，不按文件整份放行。\n");
        foreach(var row in sorted) report.AppendLine(Json.Text(row,"game")+" | 日常候选 "+Json.Int(row,"routineCandidates")+" | 剧情信号 "+Json.Int(row,"storySignals")+" | 不确定 "+Json.Int(row,"unknown")+" | 模板 "+Json.Text(row,"profile")+" | 读取异常 "+Json.Array(row["errors"]).Length);
        File.WriteAllText(Path.Combine(output,"扫描报告.txt"),report.ToString(),new UTF8Encoding(true));
        Console.WriteLine("SCAN COMPLETE: "+sorted.Length+" games");
    }
}
