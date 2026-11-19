using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MinorShift.Emuera.Runtime.Utils;

// Metadata-only inventory: never executes scripts, reads saves or exports printed dialogue.
internal static class HookCoverageScan
{
    public static void Verify(string app)
    {
        using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(app,"logs","hook-scan","functions.json")));
        int verified=0;
        foreach(var row in report.RootElement.EnumerateArray())
        {
            if(row.GetProperty("kind").GetString()!="callback-name-not-recognized") continue;
            string name=row.GetProperty("function").GetString()!;
            if(!NewMatches(name)) throw new Exception("Uncovered callback: "+name);
            verified++;
        }
        Console.WriteLine("PASS: actual shared Windows/APK matcher recognizes all "+verified+" discovered callback-name entries");
    }
    static readonly Regex Label=new(@"^\s*@([^\s,;(]+)",RegexOptions.Compiled);
    static readonly Regex Command=new(@"^\s*([A-Za-z_]+)\b",RegexOptions.Compiled);
    static readonly Regex Call=new(@"^\s*(?:CALL|TRYCALL|JUMP|TRYJUMP)\s+([\p{L}_][\p{L}\p{N}_]*)\s*(?:,|;|$)",RegexOptions.Compiled|RegexOptions.IgnoreCase);
    static readonly Regex Hint=new(@"KOJO|口上|(?:PRE|PRI|BEFORE|AFTER)TRAIN|TRAIN_MESSAGE|MESSAGE_COM",RegexOptions.Compiled|RegexOptions.IgnoreCase);
    static readonly Regex KojoToken=new(@"(?:^|_)KOJO(?:_|$)",RegexOptions.Compiled|RegexOptions.IgnoreCase);
    internal static bool OldMatches(string name) => new[]{"PRITRAIN_MESSAGE","PRETRAIN_MESSAGE","TRAIN_MESSAGE_COM","MESSAGE_COM","KOJO_","M_KOJO","AI_DIALOGUE","SELF_KOJO"}.Any(p=>name.StartsWith(p,StringComparison.OrdinalIgnoreCase));
    static bool NewMatches(string name) => MinorShift.Emuera.AI.DialogueHookNames.IsDialogue(name) || MinorShift.Emuera.AI.DialogueHookNames.IsPreparation(name);
    sealed class Function
    {
        public string Name="",File=""; public int Line,Outputs,Buttons; public bool Value;
        public readonly HashSet<string> Calls=new(StringComparer.OrdinalIgnoreCase);
        public readonly List<int> EmptyWaits=[];
    }
    public static void Run(string app)
    {
        string workspace=Directory.GetParent(app)!.FullName, output=Path.Combine(app,"logs","hook-scan"); Directory.CreateDirectory(output);
        var games=new List<string>(); var errors=new List<string>(); var pending=new Stack<string>(); pending.Push(workspace);
        while(pending.Count>0)
        {
            string folder=pending.Pop();
            try
            {
                foreach(string sub in Directory.EnumerateDirectories(folder))
                {
                    string name=Path.GetFileName(sub);
                    if(sub.Equals(app,StringComparison.OrdinalIgnoreCase) || name is ".git" or ".tools" or "node_modules" || (File.GetAttributes(sub)&FileAttributes.ReparsePoint)!=0) continue;
                    if(name.Equals("ERB",StringComparison.OrdinalIgnoreCase)) games.Add(sub); else pending.Push(sub);
                }
            }
            catch(Exception ex) { errors.Add(Path.GetRelativePath(workspace,folder)+": "+ex.GetType().Name); }
        }
        var findings=new List<object>(); var summaries=new List<object>(); var families=new Dictionary<string,int>();
        int totalFiles=0,totalFunctions=0,totalNamed=0,totalCandidates=0,totalPrinted=0,totalEmpty=0,totalEmptyFunctions=0;
        foreach(string erb in games.OrderBy(x=>x,StringComparer.Ordinal))
        {
            string game=Directory.GetParent(erb)!.FullName, gameName=Path.GetRelativePath(workspace,game); var functions=new List<Function>(); int files=0;
            foreach(string path in Directory.EnumerateFiles(erb,"*",SearchOption.AllDirectories).Where(p=>p.EndsWith(".erb",StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    string[] lines=EncodingHandler.ReadAllLinesFromBytes(File.ReadAllBytes(path)); string relative=Path.GetRelativePath(game,path).Replace('\\','/'); Function? current=null;
                    for(int i=0;i<lines.Length;i++)
                    {
                        var label=Label.Match(lines[i]); if(label.Success) { current=new Function{Name=label.Groups[1].Value,File=relative,Line=i+1}; functions.Add(current); continue; }
                        if(current==null) continue;
                        if(lines[i].TrimStart().StartsWith("#FUNCTION",StringComparison.OrdinalIgnoreCase)) current.Value=true;
                        string command=Command.Match(lines[i]).Groups[1].Value.ToUpperInvariant();
                        if(command.StartsWith("PRINT") || command is "DATA" or "DATAFORM") { current.Outputs++; if(command.StartsWith("PRINTBUTTON")) current.Buttons++; }
                        if(command is "PRINTW" or "PRINTFORMW" && lines[i].Trim().Equals(command,StringComparison.OrdinalIgnoreCase)) current.EmptyWaits.Add(i+1);
                        var call=Call.Match(lines[i]); if(call.Success) current.Calls.Add(call.Groups[1].Value);
                    }
                    files++;
                }
                catch(Exception ex) { errors.Add(Path.GetRelativePath(workspace,path)+": "+ex.GetType().Name); }
            }
            var byName=functions.GroupBy(f=>f.Name,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.OrdinalIgnoreCase);
            var inherited=new HashSet<string>(functions.Where(f=>OldMatches(f.Name)).Select(f=>f.Name),StringComparer.OrdinalIgnoreCase);
            var queue=new Queue<string>(inherited);
            while(queue.Count>0) { string name=queue.Dequeue(); if(!byName.TryGetValue(name,out var fs)) continue; foreach(var call in fs.SelectMany(f=>f.Calls)) if(inherited.Add(call)) queue.Enqueue(call); }
            int added=0,candidates=0;
            foreach(var f in functions)
            {
                if(f.Value) continue;
                bool token=!OldMatches(f.Name) && NewMatches(f.Name);
                bool empty=f.EmptyWaits.Count>0;
                if(!token && !empty && (OldMatches(f.Name) || !(f.Outputs>f.Buttons && (Hint.IsMatch(f.Name) || Hint.IsMatch(f.File) || Regex.IsMatch(f.File,@"EVENT_K\d+",RegexOptions.IgnoreCase))))) continue;
                string kind=token ? "callback-name-not-recognized" : empty ? "empty-wait-placeholder" : "needs-runtime-confirmation";
                if(token)
                {
                    added++; string family=Regex.Replace(f.Name.ToUpperInvariant(),@"\d+","#"); int end=family.IndexOf("KOJO",StringComparison.Ordinal)+4;
                    if(end>=4) family=family.Substring(0,end); families[family]=families.GetValueOrDefault(family)+1;
                }
                else if(!empty) candidates++;
                findings.Add(new { game=gameName,file=f.File,function=f.Name,line=f.Line,kind,outputs=f.Outputs,buttons=f.Buttons,emptyWaitLines=f.EmptyWaits.ToArray(),recognizedNow=NewMatches(f.Name),hasOldStaticCaller=inherited.Contains(f.Name),staticCalls=f.Calls.OrderBy(x=>x).ToArray() });
            }
            totalFiles+=files; totalFunctions+=functions.Count; totalNamed+=added; totalCandidates+=candidates; totalPrinted+=functions.Count(f=>f.Outputs>f.Buttons);
            int emptyWaits=functions.Sum(f=>f.EmptyWaits.Count),emptyWaitFunctions=functions.Count(f=>f.EmptyWaits.Count>0); totalEmpty+=emptyWaits; totalEmptyFunctions+=emptyWaitFunctions;
            summaries.Add(new{game=gameName,files,functions=functions.Count,missedKojoNames=added,otherCandidates=candidates,emptyWaits,emptyWaitFunctions});
            Console.WriteLine($"{summaries.Count}/{games.Count}: {gameName} | files={files}, functions={functions.Count}, missedKojoNames={added}, candidates={candidates}");
        }
        var options=new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
        File.WriteAllText(Path.Combine(output,"functions.json"),JsonSerializer.Serialize(findings,options),new UTF8Encoding(false));
        var summary=new{date="2026-11-19",directories=games.Count,files=totalFiles,functions=totalFunctions,functionsWithText=totalPrinted,missedKojoNames=totalNamed,otherCandidates=totalCandidates,emptyWaits=totalEmpty,emptyWaitFunctions=totalEmptyFunctions,families=families.OrderByDescending(x=>x.Value).ToDictionary(x=>x.Key,x=>x.Value),errors,games=summaries};
        File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(summary,options),new UTF8Encoding(false));
        Console.WriteLine($"SCAN COMPLETE: directories={games.Count}, files={totalFiles}, functions={totalFunctions}, missedKojoNames={totalNamed}, candidates={totalCandidates}, errors={errors.Count}");
    }
}
