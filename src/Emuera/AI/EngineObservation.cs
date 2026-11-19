using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MinorShift.Emuera.AI;
using MinorShift.Emuera.GameData.Variable;

namespace MinorShift.Emuera.GameProc
{
    internal sealed partial class ProcessState
    {
        internal string[] AiStoryContext
        {
            get
            {
                var result=new List<string>();
                for(int i=0;i<functionList.Count;i++)
                {
                    var f=functionList[i]; var line=i+1<functionList.Count ? functionList[i+1].ReturnAddress : CurrentLine;
                    var position=line==null ? null : line.Position;
                    result.Add(f.FunctionName+"|"+(f.TopLabel.Position==null ? "" : f.TopLabel.Position.Filename)+"|"+(position==null ? 0 : position.LineNo)+"|"+(i+1==functionList.Count ? "leaf" : "parent"));
                }
                return result.ToArray();
            }
        }
        internal string AiDialogueScope
        {
            get
            {
                for(int i=0;i<functionList.Count;i++)
                {
                    string name=functionList[i].FunctionName;
                    // Keep all preparation subroutines together, including nested KOJO calls.
                    if(name.StartsWith("PRITRAIN_MESSAGE",StringComparison.OrdinalIgnoreCase) || name.StartsWith("PRETRAIN_MESSAGE",StringComparison.OrdinalIgnoreCase)) return name;
                }
                for(int i=functionList.Count-1;i>=0;i--)
                {
                    string name=functionList[i].FunctionName;
                    // Retain the leaf scope for existing KOJO redraws and cached event identity.
                    if(name.StartsWith("TRAIN_MESSAGE_COM",StringComparison.OrdinalIgnoreCase) || name.StartsWith("MESSAGE_COM",StringComparison.OrdinalIgnoreCase) || name.StartsWith("KOJO_",StringComparison.OrdinalIgnoreCase) || name.StartsWith("M_KOJO",StringComparison.OrdinalIgnoreCase) || name.StartsWith("AI_DIALOGUE",StringComparison.OrdinalIgnoreCase) || name.StartsWith("SELF_KOJO",StringComparison.OrdinalIgnoreCase)) return name;
                }
                return null;
            }
        }
    }
    internal sealed partial class Process
    {
        readonly Dictionary<string,string> aiAddresses=new Dictionary<string,string>();
        static readonly Regex AddressIdentifier=new Regex("SELF_CALL|MASTER_CALL|CALLNAME|NICKNAME|MASTERNAME|SELF_NAME|一人称|二人称|三人称|人稱|自称|自稱|呼称|呼稱|称呼|稱呼|PRONOUN",RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        internal void AiClearAddresses() { aiAddresses.Clear(); }
        internal void AiObserveAddress(string identifier,string value)
        {
            if(aiAddresses.Count>=64 || string.IsNullOrWhiteSpace(value) || value.Length>64 || value.Contains("\n") || !AddressIdentifier.IsMatch(identifier)) return;
            aiAddresses[identifier+" / "+value]=value;
        }
        void AiAddressNames(Dictionary<string,string> addresses,long index,string role)
        {
            var data=vEvaluator.VariableData;
            if(index<0 || index>=data.CharacterList.Count) return;
            var c=data.CharacterList[(int)index];
            foreach(var code in new[]{VariableCode.NAME,VariableCode.CALLNAME,VariableCode.NICKNAME,VariableCode.MASTERNAME})
            {
                string value=c.DataString[Index(code)];
                if(!string.IsNullOrWhiteSpace(value) && value.Length<=64) addresses[code+":"+role]=value;
            }
            var values=c.DataStringArray[Index(VariableCode.CSTR)]; var labels=vEvaluator.Constant.GetCsvNameList(VariableCode.CSTRNAME);
            for(int i=0;i<Math.Min(values.Length,labels.Length);i++)
                if(!string.IsNullOrEmpty(labels[i]) && AddressIdentifier.IsMatch(labels[i]) && !string.IsNullOrWhiteSpace(values[i]) && values[i].Length<=64) addresses["CSTR:"+role+":"+labels[i]]=values[i];
        }
        internal string AiScope { get { return state==null ? null : state.AiDialogueScope; } }
        static int Index(VariableCode code) { return (int)(code & VariableCode.__LOWERCASE__); }
        internal DialogueSnapshot AiSnapshot(string hook)
        {
            if(vEvaluator==null) return null;
            var data=vEvaluator.VariableData;
            long target=vEvaluator.TARGET,master=vEvaluator.MASTER;
            if(target<0 || target>=data.CharacterList.Count) return null;
            var character=data.CharacterList[(int)target];
            string name=character.DataString[Index(VariableCode.NAME)] ?? "角色";
            string user=master>=0 && master<data.CharacterList.Count ? data.CharacterList[(int)master].DataString[Index(VariableCode.NAME)] : "玩家";
            var facts=new StringBuilder();
            facts.AppendLine("游戏："+System.IO.Path.GetFileName(Program.ExeDir.TrimEnd('\\','/')));
            facts.AppendLine("阶段：原口上显示时的状态快照；未列出的后续结算不可推测。");
            facts.AppendLine("口上函数："+hook);
            facts.AppendLine("当前角色："+name+"；NO="+character.DataInteger[Index(VariableCode.NO)]+"；TARGET="+target);
            facts.AppendLine("玩家："+user+"；命令编号="+vEvaluator.SELECTCOM);
            AppendStats(facts,character,VariableCode.BASE,VariableCode.BASENAME,"基础状态");
            AppendStats(facts,character,VariableCode.ABL,VariableCode.ABLNAME,"能力");
            AppendStats(facts,character,VariableCode.TALENT,VariableCode.TALENTNAME,"特征");
            var addresses=new Dictionary<string,string>(aiAddresses); AiAddressNames(addresses,target,"TARGET"); AiAddressNames(addresses,master,"MASTER");
            return new DialogueSnapshot("",name,user ?? "玩家",facts.ToString(),"",hook,addresses,state.AiStoryContext);
        }
        void AppendStats(StringBuilder b,CharacterData character,VariableCode code,VariableCode namesCode,string title)
        {
            var values=character.DataIntegerArray[Index(code)]; var names=vEvaluator.Constant.GetCsvNameList(namesCode); int written=0;
            for(int i=0;i<Math.Min(values.Length,names.Length) && written<16;i++)
                if(values[i]!=0 && !string.IsNullOrEmpty(names[i])) { b.AppendLine(title+" / "+names[i]+"="+values[i]); written++; }
        }
    }
}

namespace MinorShift.Emuera.GameView
{
    internal sealed partial class EmueraConsole
    {
        internal AiController Ai { get; private set; }
        readonly List<ConsoleDisplayLine> aiLines=new List<ConsoleDisplayLine>();
        long aiStoryTurn;
        readonly HashSet<string> aiStoryContext=new HashSet<string>();
        DialogueSnapshot aiSnapshot;
        List<ConsoleDisplayLine> aiPresentedLines=new List<ConsoleDisplayLine>();
        string aiPresentedEvent;
        bool aiWasPending;
        int aiSeenLines,aiNoScope,aiNoTarget,aiButtons;
        string aiSession=Guid.NewGuid().ToString("N");
        internal void AttachAi(AiController controller) { if(Ai!=null) Ai.Changed-=AiRefreshPresentation; Ai=controller; if(Ai!=null) Ai.Changed+=AiRefreshPresentation; }
        void AiRefreshPresentation()
        {
            if(window.IsDisposed) return;
            if(window.InvokeRequired) { window.BeginInvoke((Action)AiRefreshPresentation); return; }
            bool same=Ai.Current!=null && Ai.Current.EventId==aiPresentedEvent;
            if(!Ai.Settings.Enabled) foreach(var line in displayLineList) line.AiSuppressed=false;
            if(same) foreach(var line in aiPresentedLines) line.AiSuppressed=Ai.Settings.Enabled && Ai.DisplayMode!="original";
            else if(aiWasPending) foreach(var line in aiPresentedLines) line.AiSuppressed=false;
            aiWasPending=same && (Ai.DisplayMode=="pending" || Ai.DisplayMode=="waiting");
            AiUpdateTranscript();
            RefreshStrings(true);
        }
        internal void AiBeginTurn() { aiStoryTurn++; if(emuera!=null) emuera.AiClearAddresses(); aiLines.Clear(); aiStoryContext.Clear(); aiSnapshot=null; aiSeenLines=aiNoScope=aiNoTarget=aiButtons=0; if(Ai!=null) Ai.Suspend(); }
        internal void AiReset() { aiSession=Guid.NewGuid().ToString("N"); foreach(var line in displayLineList) line.AiSuppressed=false; aiTranscript.Clear(); aiLines.Clear(); aiPresentedLines.Clear(); aiSnapshot=null; if(Ai!=null) Ai.Reset(); }
        void AiObserveLine(ConsoleDisplayLine line)
        {
            if(Ai==null || emuera==null || state==ConsoleState.Error) return;
            aiSeenLines++;
            try
            {
                string scope=emuera.AiScope;
                if(scope==null) { aiNoScope++; return; }
                if(line.Buttons.Any(b=>b.IsButton)) { aiButtons++; return; }
                var snapshot=emuera.AiSnapshot(scope); if(snapshot==null) { aiNoTarget++; return; }
                if(aiSnapshot!=null && (snapshot.Character!=aiSnapshot.Character || snapshot.Hook!=aiSnapshot.Hook)) { foreach(var old in aiLines) old.AiSuppressed=false; aiLines.Clear(); }
                foreach(var site in snapshot.StoryContext) { if(aiStoryContext.Count>=512) { aiStoryContext.Add("overflow"); break; } aiStoryContext.Add(site); }
                aiSnapshot=snapshot; if(aiLines.Count<1024) { aiLines.Add(line); line.AiSuppressed=Ai.Settings.Enabled; }
            }
            catch { /* Observation must never prevent the original game from running. */ }
        }
        internal void AiEndTurn()
        {
            if(Ai==null) return;
            try
            {
                if(aiSnapshot==null || state==ConsoleState.Error) { foreach(var line in aiLines) line.AiSuppressed=false; Ai.Log.Write("capture.empty",null,"本次未捕获可生成口上","printedLines",aiSeenLines,"outsideDialogueScope",aiNoScope,"invalidTarget",aiNoTarget,"buttonLines",aiButtons,"engineState",state.ToString()); Ai.SetEvent(null); return; }
                var builder=new StringBuilder();
                var visible=new HashSet<ConsoleDisplayLine>(displayLineList);
                foreach(var line in aiLines.Where(visible.Contains))
                { if(line.IsLogicalLine && builder.Length>0) builder.AppendLine(); builder.Append(line.ToString()); }
                string original=builder.ToString().Trim();
                if(original.Length==0) { foreach(var line in aiLines) line.AiSuppressed=false; Ai.Log.Write("capture.empty",null,"口上输出已被脚本清除或为空"); Ai.SetEvent(null); return; }
                string key;
                string bindings=string.Join("\n",aiSnapshot.AddressValues.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>p.Key+"="+p.Value));
                using(var sha=SHA256.Create()) key=Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(aiSession+aiSnapshot.Facts+original+bindings+string.Join("\n",aiStoryContext.OrderBy(x=>x,StringComparer.Ordinal)))));
                bool same=aiPresentedEvent==key;
                if(!same && aiWasPending) foreach(var old in aiPresentedLines) old.AiSuppressed=false;
                aiPresentedLines=(same ? aiPresentedLines.Concat(aiLines) : aiLines).Where(visible.Contains).Distinct().ToList(); aiPresentedEvent=key;
                AiAddTranscript(key,aiLines);
                Ai.Log.Write("capture.ready",null,"捕获到口上，备用原文仅在后台保留","hook",aiSnapshot.Hook,"lines",aiPresentedLines.Count,"originalChars",original.Length);
                Ai.SetEvent(new DialogueSnapshot(key,aiSnapshot.Character,aiSnapshot.User,aiSnapshot.Facts,original,aiSnapshot.Hook,aiSnapshot.AddressValues,aiStoryContext.ToArray(),aiStoryTurn));
            }
            catch(Exception ex) { foreach(var line in aiLines) line.AiSuppressed=false; Ai.Log.Write("capture.failed",null,ex.ToString()); Ai.Reset(); }
            finally { aiLines.Clear(); aiSnapshot=null; }
        }
    }
}
