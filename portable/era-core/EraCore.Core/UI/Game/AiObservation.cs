using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameProc
{
    internal sealed partial class Process
    {
        private readonly Dictionary<string,string> _aiAddresses = [];
        private static readonly Regex AddressIdentifier = new("SELF_CALL|MASTER_CALL|CALLNAME|NICKNAME|MASTERNAME|SELF_NAME|一人称|二人称|三人称|人稱|自称|自稱|呼称|呼稱|称呼|稱呼|PRONOUN",RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        internal void AiClearAddresses() => _aiAddresses.Clear();
        internal void AiObserveAddress(string identifier,string value)
        {
            // Observe only a value that the interpreter already evaluated, never call a function here.
            if(_aiAddresses.Count>=64 || string.IsNullOrWhiteSpace(value) || value.Length>64 || value.Contains('\n') || !AddressIdentifier.IsMatch(identifier)) return;
            _aiAddresses[identifier+" / "+value]=value;
        }
        internal DialogueFrame? AiSnapshot(out bool invalidTarget, bool forChoices = false)
        {
            invalidTarget = false;
            string? hook = executionState?.CurrentState is ProcessState current ? current.DialogueScope : null;
            if (forChoices) hook = "CHOICES_WITHOUT_DIALOGUE";
            if (hook == null || vEvaluator == null) return null;
            var data = vEvaluator.VariableData;
            long target = vEvaluator.TARGET, master = vEvaluator.MASTER;
            if (target < 0 || target >= data.CharacterList.Count) { invalidTarget = true; return null; }
            static int Index(VariableCode code) => (int)(code & VariableCode.__LOWERCASE__);
            var character = data.CharacterList[(int)target];
            string name = character.DataString[Index(VariableCode.NAME)] ?? "角色";
            string user = master >= 0 && master < data.CharacterList.Count ? data.CharacterList[(int)master].DataString[Index(VariableCode.NAME)] : "玩家";
            var addresses=new Dictionary<string,string>(_aiAddresses);
            void Names(long index,string role)
            {
                if(index<0 || index>=data.CharacterList.Count) return;
                var strings=data.CharacterList[(int)index].DataString;
                foreach(var code in new[]{VariableCode.NAME,VariableCode.CALLNAME,VariableCode.NICKNAME,VariableCode.MASTERNAME})
                {
                    string? value=strings[Index(code)];
                    if(!string.IsNullOrWhiteSpace(value) && value.Length<=64) addresses[code+":"+role]=value;
                }
                var values=data.CharacterList[(int)index].DataStringArray[Index(VariableCode.CSTR)];
                var labels=vEvaluator.Constant.GetCsvNameList(VariableCode.CSTRNAME);
                for(int i=0;i<Math.Min(values.Length,labels.Length);i++)
                    if(!string.IsNullOrEmpty(labels[i]) && AddressIdentifier.IsMatch(labels[i]) && !string.IsNullOrWhiteSpace(values[i]) && values[i].Length<=64)
                        addresses["CSTR:"+role+":"+labels[i]]=values[i];
            }
            Names(target,"TARGET"); Names(master,"MASTER");
            var facts = new StringBuilder();
            facts.AppendLine(forChoices ? "阶段：等待玩家选择，当前没有捕获到原口上。以下是等待输入时的只读状态；菜单选项尚未执行。" : "阶段：原口上显示时的只读快照；未列出的后续结算不可推测。");
            facts.AppendLine($"口上函数：{hook}；当前角色：{name}；NO={character.DataInteger[Index(VariableCode.NO)]}；TARGET={target}");
            facts.AppendLine(forChoices ? $"玩家：{user}。只描写当前角色的反应或等待，不把任一选项当成已发生的事实，不推测下一步选择或结果。" : $"玩家：{user}；命令编号={vEvaluator.SELECTCOM}");
            void Stats(VariableCode code, VariableCode namesCode, string label)
            {
                var values = character.DataIntegerArray[Index(code)];
                var names = vEvaluator.Constant.GetCsvNameList(namesCode);
                int written = 0;
                for (int i = 0; i < Math.Min(values.Length, names.Length) && written < 16; i++)
                    if (values[i] != 0 && !string.IsNullOrEmpty(names[i])) { facts.AppendLine($"{label}/{names[i]}={values[i]}"); written++; }
            }
            Stats(VariableCode.BASE, VariableCode.BASENAME, "基础状态");
            Stats(VariableCode.ABL, VariableCode.ABLNAME, "能力");
            Stats(VariableCode.TALENT, VariableCode.TALENTNAME, "特征");
            return new DialogueFrame("", name, user ?? "玩家", facts.ToString(), "", hook) { Addresses=addresses, StoryContext=(executionState?.CurrentState as ProcessState)?.AiStoryContext ?? [] };
        }
    }
}

namespace MinorShift.Emuera.GameView
{
    internal sealed partial class EmueraConsole
    {
        private readonly List<ConsoleDisplayLine> _aiLines = [];
        private readonly HashSet<string> _aiStoryContext = [];
        private DialogueFrame? _aiFrame;
        private string _aiSession = Guid.NewGuid().ToString("N");
        private int _aiPrinted, _aiOutsideScope, _aiInvalidTarget, _aiButtons, _aiErrors;
        private string _aiErrorType = "";
        private long _aiTurnSerial;
        internal void AiBeginTurn() { _aiTurnSerial++; _state.process?.AiClearAddresses(); _aiLines.Clear(); _aiStoryContext.Clear(); _aiFrame = null; _aiPrinted = _aiOutsideScope = _aiInvalidTarget = _aiButtons = _aiErrors = 0; _aiErrorType = ""; }
        internal void AiReset() { AiBeginTurn(); _aiSession = Guid.NewGuid().ToString("N"); }
        internal void AiObserveLine(ConsoleDisplayLine line)
        {
            try
            {
                _aiPrinted++;
                if (line.Buttons.Any(button => button.IsButton)) { _aiButtons++; return; }
                bool invalidTarget = false;
                var frame = _state.process?.AiSnapshot(out invalidTarget);
                if (frame == null) { if (invalidTarget) _aiInvalidTarget++; else _aiOutsideScope++; return; }
                if (_aiFrame != null && (_aiFrame.Character != frame.Character || _aiFrame.Hook != frame.Hook)) _aiLines.Clear();
                _aiFrame = frame;
                foreach(var site in frame.StoryContext) { if(_aiStoryContext.Count>=512) { _aiStoryContext.Add("overflow"); break; } _aiStoryContext.Add(site); }
                if (_aiLines.Count < 1024) _aiLines.Add(line);
            }
            catch (Exception ex) { _aiErrors++; _aiErrorType = ex.GetType().Name; }
        }
        internal DialogueFrame? AiReadDialogue()
        {
            try
            {
                if (_aiFrame == null || State is ConsoleState.Error or ConsoleState.Quit) return null;
                var visible = new HashSet<ConsoleDisplayLine>(DisplayLineList);
                var text = new StringBuilder();
                foreach (var line in _aiLines.Where(visible.Contains))
                { if (line.IsLogicalLine && text.Length > 0) text.AppendLine(); text.Append(line.ToString()); }
                string original = text.ToString().Trim();
                if (original.Length == 0) return null;
                string bindings=string.Join("\n",_aiFrame.Addresses.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>p.Key+"="+p.Value));
                string id = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(_aiSession + _aiFrame.Facts + original + bindings + string.Join("\n",_aiStoryContext.OrderBy(x=>x,StringComparer.Ordinal)))));
                return _aiFrame with { EventId = id, Original = original, StoryContext=_aiStoryContext.ToArray(), StoryTurn=_aiTurnSerial, LineIndices = _aiLines.Where(visible.Contains).Select(line => DisplayLineList.IndexOf(line)).ToArray() };
            }
            catch (Exception ex) { _aiErrors++; _aiErrorType = ex.GetType().Name; return null; }
        }
        internal DialogueFrame? AiReadChoiceContext(AwaitInfo awaiting)
        {
            try
            {
                if (State != ConsoleState.WaitInput || awaiting.ended != null || awaiting.wait != null ||
                    awaiting.input?.type is not ("IntValue" or "StrValue" or "AnyValue" or "IntButton" or "StrButton") || _aiErrors > 0 || _aiInvalidTarget > 0) return null;
                var frame = _state.process?.AiSnapshot(out _, forChoices: true);
                if (frame == null) return null;
                var choices = new StringBuilder(); int first = -1, total = 0;
                for (int i = 0; i < DisplayLineList.Count; i++)
                    foreach (var button in DisplayLineList[i].Buttons)
                    {
                        if (!button.IsButton || button.Generation != LastButtonGeneration || button.StrArray.Any(part => part is ConsoleImagePart)) continue;
                        string label = string.Concat(button.StrArray.OfType<ConsoleStyledString>().Select(part => part.Text)).Trim();
                        if (label.Length == 0) continue;
                        if (first < 0) first = i;
                        total++;
                        string value = button.IsInteger ? button.Input.ToString(System.Globalization.CultureInfo.InvariantCulture) : button.Inputs;
                        if (total <= 40) choices.AppendLine($"选项 {value}：{label[..Math.Min(160,label.Length)]}");
                    }
                if (first < 0) return null;
                choices.AppendLine($"可用选项共 {total} 项。以上仅列出前 40 项，属于未执行的选择，不是行动记录。");
                string facts = frame.Facts + choices;
                string id = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"{_aiSession}|choice|{_aiTurnSerial}|{LastButtonGeneration}|{facts}")));
                return frame with { EventId = id, Facts = facts, StoryTurn=_aiTurnSerial, InsertBeforeLine = first };
            }
            catch (Exception ex) { _aiErrors++; _aiErrorType = ex.GetType().Name; return null; }
        }
        internal DialogueCaptureInfo AiCaptureInfo(DialogueFrame? frame)
        {
            string reason = frame != null ? "捕获成功" : State is ConsoleState.Error or ConsoleState.Quit ? "游戏已经停止或发生脚本错误" :
                _aiErrors > 0 ? "口上观察器异常（" + _aiErrorType + "），请导出日志" :
                _aiInvalidTarget > 0 ? "进入口上函数时 TARGET 无效" :
                _aiFrame != null ? "口上文本为空或已被脚本清除" :
                _aiPrinted == 0 ? "本回合没有新的文本输出" :
                _aiButtons == _aiPrinted ? "本回合只输出了按钮" : "当前文本不在受支持的口上函数内";
            return new(reason, _aiPrinted, _aiOutsideScope, _aiInvalidTarget, _aiButtons, _aiErrors, _aiErrorType);
        }
    }
}
