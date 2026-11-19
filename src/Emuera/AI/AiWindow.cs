using System;
using System.Drawing;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MinorShift.Emuera.AI
{
    internal sealed class AiWindow : Form
    {
        readonly AiController controller;
        readonly string settingsPath;
        readonly StoryFunctionDocument document;
        readonly TextBox text=new TextBox { Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,BackColor=Color.FromArgb(27,31,39),ForeColor=Color.FromArgb(235,237,240),BorderStyle=BorderStyle.None,Font=new Font("Microsoft YaHei UI",12) };
        readonly Label status=new Label { Dock=DockStyle.Bottom,Height=54,Padding=new Padding(12,5,12,5),ForeColor=Color.Silver };
        readonly Label title=new Label { Dock=DockStyle.Top,Height=58,Padding=new Padding(12,8,12,4),ForeColor=Color.White };
        readonly Button generate=new Button { Text="重新生成",AutoSize=true };
        public AiWindow(AiController controller,string settingsPath,StoryFunctionDocument document=null)
        {
            this.controller=controller; this.settingsPath=settingsPath; this.document=document;
            Text="Emuera AI · 口上"; Size=new Size(600,560); MinimumSize=new Size(450,360);
            StartPosition=FormStartPosition.CenterParent; BackColor=Color.FromArgb(27,31,39);
            var actions=new FlowLayoutPanel { Dock=DockStyle.Bottom,Height=90,Padding=new Padding(8),BackColor=SystemColors.Control };
            var original=new Button { Text="恢复原口上 / 取消",AutoSize=true };
            var settings=new Button { Text="API 与预设",AutoSize=true };
            var preview=new Button { Text="查看请求",AutoSize=true };
            var logs=new Button { Text="生成日志",AutoSize=true }; var probe=new Button { Text="测试已保存 API",AutoSize=true };
            var style=new Button { Text="游戏风格",AutoSize=true };
            var story=new Button { Text="剧情保护",AutoSize=true };
            actions.Controls.AddRange(new Control[]{generate,original,settings,preview,logs,probe,style,story});
            var functions=new Button { Text="函数清单 / 编辑选择",AutoSize=true,Enabled=document!=null };
            actions.Controls.Add(functions);
            functions.Click+=(s,e)=> { using(var form=new AiFunctionDocumentWindow(controller,document)) form.ShowDialog(this); };
            story.Click+=(s,e)=>ShowStoryRules();
            style.Click+=(s,e)=>ShowText("当前游戏的风格摘要",controller.StyleReport);
            Controls.Add(text); Controls.Add(title); Controls.Add(status); Controls.Add(actions);
            generate.Click+=async (s,e)=>await controller.GenerateAsync(true);
            original.Click+=(s,e)=>controller.RestoreOriginal();
            logs.Click+=(s,e)=>ShowLog();
            probe.Click+=async (s,e)=> { probe.Enabled=false; try { MessageBox.Show(this,await controller.ProbeAsync(),"API 诊断"); ShowLog(); } finally { if(!IsDisposed) probe.Enabled=true; } };
            settings.Click+=(s,e)=> { using(var dialog=new AiSettingsWindow(controller.Settings,settingsPath)) if(dialog.ShowDialog(this)==DialogResult.OK) controller.SetSettings(dialog.Value); };
            preview.Click+=(s,e)=> {
                if(controller.Current==null) return;
                try { string json=Json.Codec().Serialize(controller.PreviewRequest()); ShowText("将发送的请求（不含密钥）",json); }
                catch(Exception ex) { MessageBox.Show(this,ex.Message,"请求检查"); }
            };
            controller.Changed+=RefreshContent; FormClosed+=(s,e)=>controller.Changed-=RefreshContent;
            FormClosing+=(s,e)=> { if(e.CloseReason==CloseReason.UserClosing) { e.Cancel=true; Hide(); } };
            RefreshContent();
        }
        void ShowLog()
        {
            using(var f=new Form { Text="AI 口上生成日志（密钥已脱敏）",Size=new Size(920,600),StartPosition=FormStartPosition.CenterParent })
            {
                var view=new TextBox { Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Both,WordWrap=false,Text=controller.Log.ReadRecent() };
                var bar=new FlowLayoutPanel { Dock=DockStyle.Bottom,Height=45 };
                var refresh=new Button { Text="刷新",AutoSize=true }; var export=new Button { Text="导出日志",AutoSize=true };
                bar.Controls.AddRange(new Control[]{refresh,export});
                f.Controls.Add(view); f.Controls.Add(new Label { Dock=DockStyle.Top,Height=42,Text="最近 400 条记录；包含原口上、AI 正文及重复比较，长文本按 part 分段；密钥已脱敏。\n日志文件："+(controller.Log.FilePath ?? "仅内存") }); f.Controls.Add(bar);
                refresh.Click+=(s,e)=>view.Text=controller.Log.ReadRecent();
                export.Click+=(s,e)=> { using(var dialog=new SaveFileDialog { Filter="日志|*.log",FileName="ai-generation.log",RestoreDirectory=true }) if(dialog.ShowDialog(f)==DialogResult.OK) try { controller.Log.Export(dialog.FileName); } catch(Exception ex) { MessageBox.Show(f,ex.Message,"导出失败"); } };
                f.ShowDialog(this);
            }
        }
        void ShowText(string caption,string value)
        {
            using(var f=new Form { Text=caption,Size=new Size(760,560),StartPosition=FormStartPosition.CenterParent })
            { f.Controls.Add(new TextBox { Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Both,Text=value }); f.ShowDialog(this); }
        }
        void RefreshContent()
        {
            if(IsDisposed) return;
            if(InvokeRequired) { BeginInvoke((Action)RefreshContent); return; }
            text.Text=controller.Busy ? "AI 口上生成中…" : controller.Text; status.Text=controller.Status;
            title.Text=controller.Current==null ? "最近一次口上\n在游戏中触发对话后显示" : controller.Current.Character+"  ·  "+controller.Current.Hook+"\n原脚本处理游戏数值，AI 只生成文字";
            generate.Enabled=controller.CanGenerate && !controller.Busy;
        }
        void ShowStoryRules()
        {
            using(var form=new Form { Text="本游戏剧情保护",Size=new Size(680,430),StartPosition=FormStartPosition.CenterParent })
            {
                var reason=new TextBox { Multiline=true,ReadOnly=true,Dock=DockStyle.Top,Height=120,ScrollBars=ScrollBars.Vertical };
                string displayedEvent=""; long displayedTurn=0;
                var actions=new FlowLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(12),AutoScroll=true };
                form.Controls.Add(actions); form.Controls.Add(new Label { Dock=DockStyle.Bottom,Height=82,Padding=new Padding(10),Text="默认保护剧情和不确定文本。允许后点击主面板的重新生成。\n函数规则只属于本游戏和脚本指纹；剧情调用仍保护。本段允许只影响当前事件，不覆盖永久禁止。导入模板不修改游戏脚本或存档。" }); form.Controls.Add(reason);
                Action refresh=()=> { if(!form.IsDisposed) { displayedEvent=controller.Current==null ? "" : controller.Current.EventId; displayedTurn=controller.Current==null ? 0 : controller.Current.StoryTurn; reason.Text="内置模板："+(controller.StoryTemplate.Name.Length>0 ? controller.StoryTemplate.Name : "未套用")+"\r\n"+controller.StoryTemplate.Reason+"\r\n"+(controller.CanGenerate ? "允许 AI：" : "保留原文：")+controller.Story.Reason+"\r\n"+controller.Story.Location; } };
                foreach(string scope in new[]{"event","function"}) foreach(string action in new[]{"allow","deny","auto"})
                {
                    string selectedScope=scope, selectedAction=action;
                    var button=new Button { AutoSize=true,Text=(scope=="event" ? "仅本段" : "本游戏此函数")+(action=="allow" ? "允许" : action=="deny" ? "禁止" : "恢复自动") };
                    button.Click+=async (s,e)=> { button.Enabled=false; try { if(controller.Current==null || controller.Current.EventId!=displayedEvent || controller.Current.StoryTurn!=displayedTurn) { refresh(); throw new InvalidOperationException("段落已经变化，请核对当前判断后再操作。"); } await controller.SetStoryRuleAsync(selectedAction,selectedScope); refresh(); } catch(Exception ex) { MessageBox.Show(form,ex.Message,"剧情规则"); } finally { if(!button.IsDisposed) button.Enabled=true; } }; actions.Controls.Add(button);
                }
                var import=new Button { Text="导入剧情规则模板",AutoSize=true }; var export=new Button { Text="导出本游戏规则",AutoSize=true }; actions.Controls.Add(import); actions.Controls.Add(export);
                import.Click+=async(s,e)=> { using(var dialog=new OpenFileDialog { Filter="剧情规则 JSON|*.json",RestoreDirectory=true }) if(dialog.ShowDialog(form)==DialogResult.OK) try { string json; using(var stream=File.OpenRead(dialog.FileName)) json=Preset.ReadFile(stream); await controller.ImportStoryRulesAsync(json); refresh(); } catch(Exception ex) { MessageBox.Show(form,ex.Message,"导入失败"); } };
                export.Click+=(s,e)=> { using(var dialog=new SaveFileDialog { Filter="剧情规则 JSON|*.json",FileName="era-story-rules.json",RestoreDirectory=true }) if(dialog.ShowDialog(form)==DialogResult.OK) try { File.WriteAllText(dialog.FileName,controller.ExportStoryRules(),new UTF8Encoding(false)); } catch(Exception ex) { MessageBox.Show(form,ex.Message,"导出失败"); } };
                refresh(); form.ShowDialog(this);
            }
        }
    }

    internal sealed class AiSettingsWindow : Form
    {
        public AiSettings Value { get; private set; }
        readonly string path;
        readonly CheckBox enabled=new CheckBox { Text="启用 AI",AutoSize=true };
        readonly CheckBox auto=new CheckBox { Text="有新口上时自动生成",AutoSize=true };
        readonly CheckBox styleEnabled=new CheckBox { Text="加载时总结游戏风格（首次向 API 发送有限口上样本，之后使用缓存）",AutoSize=true };
        readonly CheckBox presetTokens=new CheckBox { Text="使用预设中的输出上限（否则以下方设置为准）",AutoSize=true };
        readonly ComboBox thinking=new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList };
        readonly TextBox endpoint=new TextBox(), key=new TextBox { UseSystemPasswordChar=true };
        readonly ComboBox model=new ComboBox { DropDownStyle=ComboBoxStyle.DropDown,AutoCompleteMode=AutoCompleteMode.SuggestAppend,AutoCompleteSource=AutoCompleteSource.ListItems };
        readonly Button refreshModels=new Button { Text="刷新模型列表",AutoSize=true,Dock=DockStyle.Right };
        readonly Label modelStatus=new Label { AutoSize=true };
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        readonly NumericUpDown timeout=new NumericUpDown { Minimum=2,Maximum=180 }, tokens=new NumericUpDown { Minimum=16,Maximum=8192 };
        readonly TextBox description=new TextBox { Multiline=true },personality=new TextBox { Multiline=true },world=new TextBox { Multiline=true },preset=new TextBox { Multiline=true,ScrollBars=ScrollBars.Both,WordWrap=false,MaxLength=Preset.MaxEnvelopeChars };
        readonly Label compatibility=new Label { AutoSize=true,MaximumSize=new Size(690,0) };
        readonly DataGridView entries=new DataGridView { Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AllowUserToResizeRows=false,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill };
        readonly TextBox entryText=new TextBox { Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical };
        List<Preset.Entry> entryValues=new List<Preset.Entry>();
        bool loadingEntries;
        public AiSettingsWindow(AiSettings settings,string path)
        {
            Value=settings.Copy(); this.path=path; Text="API、酒馆预设与角色设定"; Size=new Size(780,740); MinimumSize=new Size(600,560); StartPosition=FormStartPosition.CenterParent;
            var tabs=new TabControl { Dock=DockStyle.Fill };
            TabPage connection=new TabPage("连接"), prompts=new TabPage("酒馆预设"), character=new TabPage("角色与场景");
            connection.AutoScroll=true;
            tabs.TabPages.AddRange(new[]{connection,prompts,character});
            var rows=new TableLayoutPanel { Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Padding=new Padding(14) };
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,150)); rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            var modelRow=new Panel(); model.Dock=DockStyle.Fill; modelRow.Controls.Add(model); modelRow.Controls.Add(refreshModels);
            AddRow(rows,"开关",enabled,38); AddRow(rows,"生成方式",auto,38); AddRow(rows,"API 模式",new Label { Text="OpenAI 兼容 · Chat Completions",TextAlign=ContentAlignment.MiddleLeft },38); AddRow(rows,"API 基础地址",endpoint,38); AddRow(rows,"API Key",key,38); AddRow(rows,"模型",modelRow,40); AddRow(rows,"列表状态",modelStatus,56); AddRow(rows,"超时（秒）",timeout,38); AddRow(rows,"最大输出 tokens",tokens,38);
            AddRow(rows,"说明",new Label { AutoSize=true,Text="支持 OpenAI 兼容 Chat Completions 接口。\n密钥由 Windows 当前用户加密保存，不包含在导出的预设中。\n超时、断网、错误或取消时使用原口上。" },90); connection.Controls.Add(rows);
            thinking.Items.AddRange(new object[]{"自动（DeepSeek 关闭思考）","跟随服务商默认","关闭思考（thinking 参数）","开启思考（thinking 参数）"});
            AddRow(rows,"输出上限来源",presetTokens,38); AddRow(rows,"思考模式",thinking,38); AddRow(rows,"游戏风格",styleEnabled,56);
            AddRow(rows,"思考与额度",new Label { AutoSize=true,Text="思考可能用完输出额度而没有正文。开启思考时请增大输出 tokens 与超时。\n自动模式仅为 DeepSeek 官方接口关闭思考；其他接口跟随默认。" },65);
            var presetActions=new FlowLayoutPanel { Dock=DockStyle.Top,Height=46 };
            var import=new Button { Text="导入 JSON",AutoSize=true }; var export=new Button { Text="导出 JSON",AutoSize=true }; var check=new Button { Text="检查兼容性",AutoSize=true };
            presetActions.Controls.AddRange(new Control[]{import,export,check});
            var presetTabs=new TabControl { Dock=DockStyle.Fill };
            TabPage entryPage=new TabPage("选择条目"), rawPage=new TabPage("原始 JSON"); presetTabs.TabPages.AddRange(new[]{entryPage,rawPage});
            preset.Dock=DockStyle.Fill; rawPage.Controls.Add(preset);
            entries.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText="启用",Width=55,AutoSizeMode=DataGridViewAutoSizeColumnMode.None });
            entries.Columns.Add(new DataGridViewTextBoxColumn { HeaderText="预设条目",ReadOnly=true,FillWeight=75 });
            entries.Columns.Add(new DataGridViewTextBoxColumn { HeaderText="角色 / 类型",ReadOnly=true,FillWeight=25 });
            var split=new SplitContainer { Size=new Size(700,420),Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterDistance=210 };
            split.Panel1.Controls.Add(entries); split.Panel2.Controls.Add(entryText);
            var editActions=new FlowLayoutPanel { Dock=DockStyle.Top,Height=42 };
            Button up=new Button { Text="上移" }, down=new Button { Text="下移" }, all=new Button { Text="全选" }, none=new Button { Text="全不选" };
            editActions.Controls.AddRange(new Control[]{up,down,all,none}); entryPage.Controls.Add(split); entryPage.Controls.Add(editActions);
            compatibility.Dock=DockStyle.Bottom; compatibility.Padding=new Padding(8); compatibility.Text="支持最大 32 MB 的 Chat Completion JSON。勾选参与生成的条目；保存后生效。";
            prompts.Controls.Add(presetTabs); prompts.Controls.Add(presetActions); prompts.Controls.Add(compatibility);
            var charRows=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(14) };
            charRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100)); charRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            AddRow(charRows,"角色描述",description,150); AddRow(charRows,"性格 / 口吻",personality,130); AddRow(charRows,"世界 / 场景",world,150); character.Controls.Add(charRows);
            var bottom=new FlowLayoutPanel { Dock=DockStyle.Bottom,Height=50,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(8) };
            var save=new Button { Text="保存",AutoSize=true }; var cancel=new Button { Text="取消",DialogResult=DialogResult.Cancel,AutoSize=true };
            bottom.Controls.Add(save); bottom.Controls.Add(cancel); Controls.Add(tabs); Controls.Add(bottom); CancelButton=cancel;
            enabled.Checked=settings.Enabled; auto.Checked=settings.AutoGenerate; endpoint.Text=settings.Endpoint; model.Text=settings.Model; key.Text=settings.ApiKey;
            styleEnabled.Checked=settings.UseGameStyle; presetTokens.Checked=settings.UsePresetOutputTokens;
            thinking.SelectedIndex=Math.Max(0,Array.IndexOf(new[]{"auto","provider","disabled","enabled"},settings.ThinkingMode));
            timeout.Value=Math.Max(2,Math.Min(180,settings.TimeoutSeconds)); tokens.Value=Math.Max(16,Math.Min(8192,settings.OutputTokens));
            preset.Text=settings.PresetJson; description.Text=settings.CharacterDescription; personality.Text=settings.Personality; world.Text=settings.World;
            import.Click+=(s,e)=> { using(var d=new OpenFileDialog { Filter="JSON 预设|*.json",RestoreDirectory=true }) if(d.ShowDialog(this)==DialogResult.OK) try { string raw; using(var stream=File.OpenRead(d.FileName)) raw=Preset.ReadFile(stream); new Preset(raw); preset.Text=raw; ReloadEntries(); Check(); } catch(Exception ex) { MessageBox.Show(this,ex.Message,"导入失败"); } };
            export.Click+=(s,e)=> { using(var d=new SaveFileDialog { Filter="JSON 预设|*.json",FileName="era-preset.json",RestoreDirectory=true }) if(d.ShowDialog(this)==DialogResult.OK) try { new Preset(preset.Text); File.WriteAllText(d.FileName,string.IsNullOrWhiteSpace(preset.Text)?"{}":preset.Text,new UTF8Encoding(false)); } catch(Exception ex) { MessageBox.Show(this,ex.Message,"导出失败"); } };
            check.Click+=(s,e)=>Check();
            presetTabs.Selecting+=(s,e)=> { if(e.TabPage==entryPage) ReloadEntries(); };
            entries.CurrentCellDirtyStateChanged+=(s,e)=> { if(entries.IsCurrentCellDirty) entries.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            entries.CellValueChanged+=(s,e)=> { if(!loadingEntries && e.RowIndex>=0 && e.ColumnIndex==0) { entryValues[e.RowIndex].Enabled=Convert.ToBoolean(entries.Rows[e.RowIndex].Cells[0].Value); ApplyEntries(); } };
            entries.CurrentCellChanged+=(s,e)=>UpdateEntryText();
            up.Click+=(s,e)=>MoveEntry(-1); down.Click+=(s,e)=>MoveEntry(1);
            all.Click+=(s,e)=>SetAll(true); none.Click+=(s,e)=>SetAll(false);
            refreshModels.Click+=async (s,e)=> {
                refreshModels.Enabled=false; modelStatus.Text="正在读取 /models…";
                string address=endpoint.Text,apiKey=key.Text;
                try {
                    using(var client=new AiClient()) {
                        var list=await client.ListModelsAsync(new AiSettings { Endpoint=address,ApiKey=apiKey,TimeoutSeconds=(int)timeout.Value },lifetime.Token);
                        if(IsDisposed || lifetime.IsCancellationRequested) return;
                        if(address!=endpoint.Text || apiKey!=key.Text) { modelStatus.Text="连接信息已改变，请重新刷新。"; return; }
                        string selected=model.Text; model.Items.Clear(); model.Items.AddRange(list); model.Text=selected;
                        modelStatus.Text="已加载 "+list.Length+" 个模型，可下拉选择或手动输入。";
                    }
                } catch(Exception ex) { if(!IsDisposed && !lifetime.IsCancellationRequested) modelStatus.Text=ex is OperationCanceledException ? "读取超时；可手动填写模型名称。" : ex is FormatException || ex is InvalidOperationException ? ex.Message : "无法读取模型列表；请检查连接，或手动填写。"; }
                finally { if(!IsDisposed) refreshModels.Enabled=true; }
            };
            endpoint.TextChanged+=(s,e)=> { model.Items.Clear(); modelStatus.Text="地址改变后，请刷新模型列表。"; };
            key.TextChanged+=(s,e)=> { model.Items.Clear(); modelStatus.Text="密钥改变后，请刷新模型列表。"; };
            FormClosed+=(s,e)=>lifetime.Cancel();
            ReloadEntries();
            save.Click+=(s,e)=> {
                try {
                    new Preset(preset.Text); if(enabled.Checked) { AiClient.GetEndpoint(endpoint.Text); if(string.IsNullOrWhiteSpace(model.Text)) throw new Exception("请填写模型名称。"); }
                    Value=new AiSettings { Enabled=enabled.Checked,AutoGenerate=auto.Checked,Endpoint=endpoint.Text.Trim(),Model=model.Text.Trim(),ApiKey=key.Text.Trim(),TimeoutSeconds=(int)timeout.Value,OutputTokens=(int)tokens.Value,PresetJson=preset.Text,CharacterDescription=description.Text,Personality=personality.Text,World=world.Text };
                    Value.UseGameStyle=styleEnabled.Checked; Value.UsePresetOutputTokens=presetTokens.Checked; Value.ThinkingMode=new[]{"auto","provider","disabled","enabled"}[thinking.SelectedIndex];
                    Value.Save(path); DialogResult=DialogResult.OK; Close();
                } catch(Exception ex) { MessageBox.Show(this,ex.Message,"无法保存"); }
            };
        }
        void ReloadEntries(int selected=0)
        {
            loadingEntries=true;
            try {
                entryValues=new Preset(preset.Text).Entries(); entries.Rows.Clear();
                foreach(var e in entryValues) entries.Rows.Add(e.Enabled,e.Name,e.Role+(e.Marker ? " / 标记槽" : ""));
                if(entryValues.Count>0) { entries.CurrentCell=entries.Rows[Math.Min(selected,entryValues.Count-1)].Cells[1]; UpdateEntryText(); }
                else entryText.Text="空白预设使用内置提示。导入后可在此选择条目。";
            } catch(Exception ex) { entryValues.Clear(); entries.Rows.Clear(); entryText.Text=ex.Message; }
            finally { loadingEntries=false; }
        }
        void UpdateEntryText()
        {
            int i=entries.CurrentCell==null ? -1 : entries.CurrentCell.RowIndex;
            entryText.Text=i<0 || i>=entryValues.Count ? "" : entryValues[i].Id+Environment.NewLine+(entryValues[i].Marker ? "[动态标记槽]"+Environment.NewLine : "")+entryValues[i].Content;
        }
        void ApplyEntries() { try { preset.Text=new Preset(preset.Text).ApplyEntries(entryValues); } catch(Exception ex) { compatibility.Text=ex.Message; } }
        void MoveEntry(int delta)
        {
            if(entries.CurrentCell==null) return;
            int from=entries.CurrentCell.RowIndex,to=from+delta; if(to<0 || to>=entryValues.Count) return;
            var value=entryValues[from]; entryValues.RemoveAt(from); entryValues.Insert(to,value); ApplyEntries(); ReloadEntries(to);
        }
        void SetAll(bool enabled) { foreach(var e in entryValues) e.Enabled=enabled; if(entryValues.Count>0) { ApplyEntries(); ReloadEntries(); } }
        void Check() { try { var p=new Preset(preset.Text); compatibility.Text=p.Warnings.Count==0 ? "结构检查通过；可在口上面板查看实际请求。" : string.Join("\n",p.Warnings.GetRange(0,Math.Min(8,p.Warnings.Count))); } catch(Exception ex) { compatibility.Text=ex.Message; } }
        static void AddRow(TableLayoutPanel panel,string label,Control control,int height)
        {
            int row=panel.RowCount++; panel.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            panel.Controls.Add(new Label { Text=label,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft },0,row);
            control.Dock=DockStyle.Fill; control.Margin=new Padding(4,5,4,5); panel.Controls.Add(control,1,row);
        }
    }
}
