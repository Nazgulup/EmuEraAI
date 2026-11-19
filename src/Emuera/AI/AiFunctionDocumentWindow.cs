using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MinorShift.Emuera.AI
{
    internal sealed class AiFunctionDocumentWindow : Form
    {
        readonly AiController controller;
        readonly StoryFunctionDocument document;
        readonly TextBox query=new TextBox { Width=260 };
        readonly ComboBox filter=new ComboBox { Width=120,DropDownStyle=ComboBoxStyle.DropDownList };
        readonly Label status=new Label { Dock=DockStyle.Top,Height=72,Padding=new Padding(10) };
        readonly Label details=new Label { Dock=DockStyle.Bottom,Height=57,Padding=new Padding(10),AutoEllipsis=true };
        readonly Label pageLabel=new Label { AutoSize=true,Padding=new Padding(8) };
        readonly Button previous=new Button { Text="上一页",AutoSize=true }, next=new Button { Text="下一页",AutoSize=true }, save=new Button { Text="保存选择（0）",AutoSize=true }, export=new Button { Text="导出函数文档",AutoSize=true };
        readonly DataGridView grid=new DataGridView { Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,BackgroundColor=Color.White };
        readonly Dictionary<string,Dictionary<string,object>> edits=new Dictionary<string,Dictionary<string,object>>();
        readonly Timer timer=new Timer { Interval=500 };
        int page;
        bool loading,saving;
        string lastState="";
        static readonly string[] Modes={"自动（日常优先 AI）","手动优先 AI","保留原文"};
        internal DataGridView FunctionGrid { get { return grid; } }
        internal TextBox SearchBox { get { return query; } }
        public AiFunctionDocumentWindow(AiController controller,StoryFunctionDocument document)
        {
            this.controller=controller; this.document=document;
            Text="本游戏函数清单 · 优先 AI 选择"; Size=new Size(1060,700); MinimumSize=new Size(820,540); StartPosition=FormStartPosition.CenterParent;
            Font=new Font("Microsoft YaHei UI",9);
            var search=new FlowLayoutPanel { Dock=DockStyle.Top,Height=42,Padding=new Padding(8) };
            filter.Items.AddRange(new object[]{"全部函数","明确日常","剧情","不确定"}); filter.SelectedIndex=0;
            var find=new Button { Text="搜索函数",AutoSize=true };
            search.Controls.AddRange(new Control[]{query,filter,find}); find.Click+=(s,e)=> { page=0; RefreshRows(); };
            query.KeyDown+=(s,e)=> { if(e.KeyCode==Keys.Enter) { e.SuppressKeyPress=true; page=0; RefreshRows(); } };
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name="Allow",HeaderText="优先 AI",FillWeight=45 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name="Function",HeaderText="函数名",ReadOnly=true,FillWeight=160 });
            var modes=new DataGridViewComboBoxColumn { Name="Mode",HeaderText="生成规则",FillWeight=125 }; modes.Items.AddRange(Modes); grid.Columns.Add(modes);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name="Kind",HeaderText="分类",ReadOnly=true,FillWeight=65 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name="Source",HeaderText="来源",ReadOnly=true,FillWeight=185 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name="Note",HeaderText="备注（可编辑）",FillWeight=160,MaxInputLength=1000 });
            foreach(DataGridViewColumn column in grid.Columns) column.SortMode=DataGridViewColumnSortMode.NotSortable;
            grid.CurrentCellDirtyStateChanged+=(s,e)=> { if(grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged+=Edited;
            grid.SelectionChanged+=(s,e)=> { var row=grid.CurrentRow; if(row!=null && row.Tag is Dictionary<string,object>) details.Text=Json.Text((Dictionary<string,object>)row.Tag,"reason"); };
            var actions=new FlowLayoutPanel { Dock=DockStyle.Bottom,Height=46,Padding=new Padding(8) };
            actions.Controls.AddRange(new Control[]{previous,pageLabel,next,save,export});
            previous.Click+=(s,e)=> { page--; RefreshRows(); }; next.Click+=(s,e)=> { page++; RefreshRows(); };
            save.Click+=async(s,e)=> { try { await SaveChangesAsync(); } catch(Exception ex) { MessageBox.Show(this,ex.Message,"保存函数选择"); } };
            export.Click+=async(s,e)=> {
                using(var dialog=new SaveFileDialog { Filter="函数清单 JSON|*.json",FileName="era-function-template.json",RestoreDirectory=true })
                    if(dialog.ShowDialog(this)==DialogResult.OK) try { string raw=await Task.Run(()=>document.Export()); File.WriteAllText(dialog.FileName,raw,new UTF8Encoding(false)); } catch(Exception ex) { MessageBox.Show(this,ex.Message,"导出失败"); }
            };
            var hint=new Label { Dock=DockStyle.Bottom,Height=55,Padding=new Padding(10),Text="勾选后优先 AI；取消勾选保留原文。自动模式只允许明确日常，剧情调用仍保护。\n启用 AI 自动生成时，保存会生成当前允许的口上，不重复执行游戏。备注只用于整理。" };
            Controls.Add(grid); Controls.Add(details); Controls.Add(hint); Controls.Add(actions); Controls.Add(search); Controls.Add(status);
            timer.Tick+=(s,e)=>RefreshStatus();
            Shown+=async(s,e)=> { timer.Start(); RefreshStatus(); await document.Start(); if(!IsDisposed) RefreshRows(); };
            FormClosed+=(s,e)=>timer.Dispose();
        }
        void RefreshStatus()
        {
            if(IsDisposed) return;
            status.Text="内置模板："+(controller.StoryTemplate.Name.Length>0 ? controller.StoryTemplate.Name : "未套用")+"  ·  "+controller.StoryTemplate.Reason+"\n"+document.Reason+(document.State=="scanning" ? "  "+document.Done+"/"+document.Total : "");
            if(document.State=="ready" && lastState!="ready") RefreshRows(); lastState=document.State;
            save.Enabled=document.State=="ready" && edits.Count>0 && !saving; export.Enabled=document.State=="ready" && !saving;
        }
        internal void RefreshRows()
        {
            if(IsDisposed) return;
            grid.EndEdit(); string kind=new[]{"","routine","story","unknown"}[filter.SelectedIndex];
            var data=document.Page(query.Text,page,kind); page=Json.Int(data,"page"); int count=Json.Int(data,"count");
            loading=true;
            try
            {
                grid.Rows.Clear();
                foreach(var item in Json.Array(data["rows"]))
                {
                    var source=Json.Object(item); string key=Json.Text(source,"key"); Dictionary<string,object> draft;
                    var values=edits.TryGetValue(key,out draft) ? draft : source; string action=Json.Text(values,"action"), classification=Json.Text(source,"classification");
                    int i=grid.Rows.Add(action=="allow" || (action=="auto" && classification=="routine"),Json.Text(source,"function"),Modes[action=="allow" ? 1 : action=="deny" ? 2 : 0],classification=="routine" ? "日常候选" : classification=="story" ? "剧情保护" : "不确定",Json.Text(source,"file")+":"+Json.Int(source,"line"),Json.Text(values,"note"));
                    grid.Rows[i].Tag=source;
                }
            }
            finally { loading=false; }
            pageLabel.Text="第 "+(page+1)+" 页 · "+count+" 个函数"; previous.Enabled=page>0; next.Enabled=(page+1)*30<count;
            save.Text="保存选择（"+edits.Count+"）"; grid.Enabled=document.State=="ready" && !saving;
        }
        void Edited(object sender,DataGridViewCellEventArgs e)
        {
            if(loading || e.RowIndex<0 || e.ColumnIndex<0) return;
            var row=grid.Rows[e.RowIndex]; var source=row.Tag as Dictionary<string,object>; if(source==null) return;
            string action; int mode=Array.IndexOf(Modes,Convert.ToString(row.Cells["Mode"].Value));
            if(e.ColumnIndex==0) action=Convert.ToBoolean(row.Cells["Allow"].Value) ? "allow" : "deny";
            else action=mode==1 ? "allow" : mode==2 ? "deny" : "auto";
            loading=true;
            row.Cells["Mode"].Value=Modes[action=="allow" ? 1 : action=="deny" ? 2 : 0];
            row.Cells["Allow"].Value=action=="allow" || (action=="auto" && Json.Text(source,"classification")=="routine");
            loading=false;
            string key=Json.Text(source,"key"); edits[key]=new Dictionary<string,object>{{"key",key},{"action",action},{"note",Convert.ToString(row.Cells["Note"].Value)}};
            save.Text="保存选择（"+edits.Count+"）"; save.Enabled=!saving;
        }
        internal async Task SaveChangesAsync()
        {
            if(saving || edits.Count==0) return;
            grid.EndEdit(); saving=true; grid.Enabled=false; save.Enabled=false;
            try
            {
                var changes=edits.Values.Cast<object>().ToArray();
                await Task.Run(()=>document.Update(changes));
                controller.Log.Write("story.document-edit",null,"已保存电脑端函数选择；允许的当前口上优先 AI","changes",changes.Length);
                await controller.RefreshStoryRulesAsync(); edits.Clear();
                if(!IsDisposed) { RefreshRows(); details.Text="已保存。允许的当前口上优先 AI；游戏数值和原脚本不重复执行。"; }
            }
            finally { saving=false; if(!IsDisposed) { grid.Enabled=true; RefreshStatus(); } }
        }
    }
}
