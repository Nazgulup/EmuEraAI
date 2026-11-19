using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace MinorShift.Emuera.GameView
{
    internal sealed partial class EmueraConsole
    {
        // A view of the transcript, never part of the interpreter's output buffer.
        // Original lines/buttons remain available to LINECOUNT, CLEARLINE and saves.
        sealed class AiTranscriptBlock
        {
            internal string EventId, Mode = "waiting", Text = "", LayoutText;
            internal int Width;
            internal readonly HashSet<ConsoleDisplayLine> Originals = new HashSet<ConsoleDisplayLine>();
            internal ConsoleDisplayLine[] Layout;
        }
        readonly List<AiTranscriptBlock> aiTranscript = new List<AiTranscriptBlock>();
        List<ConsoleDisplayLine> aiScrollLayout;

        void AiAddTranscript(string eventId, IEnumerable<ConsoleDisplayLine> captured)
        {
            var selected = new HashSet<ConsoleDisplayLine>(captured);
            var registered = new HashSet<ConsoleDisplayLine>(aiTranscript.SelectMany(b => b.Originals));
            AiTranscriptBlock block = null;
            foreach (var line in displayLineList)
            {
                if (!selected.Contains(line) || registered.Contains(line)) continue;
                if (block == null) { block = new AiTranscriptBlock { EventId = eventId }; aiTranscript.Add(block); }
                block.Originals.Add(line);
            }
        }

        void AiUpdateTranscript()
        {
            foreach (var block in aiTranscript)
            {
                string mode = block.Mode, text = block.Text;
                if (Ai.Current != null && Ai.Current.EventId == block.EventId)
                { mode = Ai.DisplayMode; text = Ai.Text; }
                else if (mode == "pending" || mode == "waiting") { mode = "original"; text = ""; }
                if (block.Mode != mode || block.Text != text)
                {
                    block.Mode = mode; block.Text = text;
                    Ai.Log.Write("presentation.main", null, "游戏正文口上显示已更新", "mode", mode, "replyChars", text.Length);
                }
                foreach (var line in block.Originals)
                    line.AiSuppressed = Ai.Settings.Enabled && mode != "original" && mode != "empty";
            }
        }

        internal List<ConsoleDisplayLine> AiDisplayLines()
        {
            var visible = new HashSet<ConsoleDisplayLine>(displayLineList);
            foreach (var block in aiTranscript) block.Originals.IntersectWith(visible);
            aiTranscript.RemoveAll(b => b.Originals.Count == 0);
            if (Ai == null || !Ai.Settings.Enabled) return displayLineList;
            var replacements = new Dictionary<ConsoleDisplayLine, AiTranscriptBlock>();
            foreach (var block in aiTranscript)
                if (block.Mode != "original" && block.Mode != "empty")
                    foreach (var line in block.Originals) replacements[line] = block;
            var result = new List<ConsoleDisplayLine>();
            var emitted = new HashSet<AiTranscriptBlock>();
            foreach (var line in displayLineList)
            {
                AiTranscriptBlock block;
                if (!replacements.TryGetValue(line, out block)) { if (!line.AiSuppressed) result.Add(line); continue; }
                if (!emitted.Add(block)) continue;
                string text = block.Mode == "ai" ? block.Text : block.Mode == "pending"
                    ? "【AI 口上生成中…】" : "【等待 AI 口上；可在“AI 口上”菜单中生成或恢复原文】";
                if (block.Layout == null || block.LayoutText != text || block.Width != Config.DrawableWidth)
                {
                    var layout = new List<ConsoleDisplayLine>();
                    var buffer = new PrintStringBuffer(this);
                    var style = new StringStyle(Config.ForeColor, FontStyle.Regular, Config.FontName);
                    foreach (string paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                    {
                        if (paragraph.Length == 0) layout.Add(new ConsoleDisplayLine(new ConsoleButtonString[0], true, false));
                        else { buffer.AppendPlainText(paragraph, style); layout.AddRange(buffer.Flush(stringMeasure, false)); }
                    }
                    block.Layout = layout.ToArray(); block.LayoutText = text; block.Width = Config.DrawableWidth;
                }
                result.AddRange(block.Layout);
            }
            return result;
        }
    }
}
