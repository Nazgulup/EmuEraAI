using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MinorShift.Emuera.Sub;
using System.Collections.Generic;

namespace MinorShift.Emuera.AI
{
    internal static class GameLocator
    {
        internal static string Resolve(string selection)
        {
            string path=Path.GetFullPath((selection ?? "").Trim().Trim('"'));
            if(File.Exists(path))
            {
                if(!Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase)) throw new FormatException("请选择原游戏的 .exe 或游戏目录。");
                path=Path.GetDirectoryName(path);
            }
            if(!Directory.Exists(Path.Combine(path,"CSV")) || !Directory.Exists(Path.Combine(path,"ERB")))
                throw new FormatException("未找到 CSV 和 ERB 文件夹。请选择 Emuera 游戏根目录中的 exe；仅有 exe 或 Electron 游戏不能加载。");
            return path.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        }
    }

    internal sealed class GameLauncher : Form
    {
        public string SelectedDirectory { get; private set; }
        public string SelectedEncoding { get; private set; }
        readonly TextBox path=new TextBox { Dock=DockStyle.Fill };
        readonly ComboBox encoding=new ComboBox { Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList };
        readonly ListBox recent=new ListBox { Dock=DockStyle.Fill,HorizontalScrollbar=true,IntegralHeight=false };
        readonly Label info=new Label { Dock=DockStyle.Bottom,Height=64,Padding=new Padding(12),ForeColor=Color.FromArgb(70,80,95) };
        readonly string history=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ai","games.json");
        public GameLauncher()
        {
            Text="ERA AI 游戏加载器"; Size=new Size(760,490); MinimumSize=new Size(620,390); StartPosition=FormStartPosition.CenterScreen;
            Font=new Font("Microsoft YaHei UI",10); BackColor=Color.FromArgb(245,247,250); AllowDrop=true;
            var heading=new Label { Dock=DockStyle.Top,Height=82,Padding=new Padding(18,14,12,8),Text="打开已有 ERA 游戏\n选择原游戏 exe 或包含 CSV、ERB 的目录",Font=new Font("Microsoft YaHei UI",13) };
            var row=new TableLayoutPanel { Dock=DockStyle.Top,Height=45,ColumnCount=2,Padding=new Padding(12,5,12,5) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,90)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            row.Controls.Add(new Label { Text="游戏路径",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft }); row.Controls.Add(path);
            var encodingRow=new TableLayoutPanel { Dock=DockStyle.Top,Height=45,ColumnCount=2,Padding=new Padding(12,5,12,5) };
            encodingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,90)); encodingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            encoding.Items.AddRange(GameTextEncoding.Labels); encoding.SelectedIndex=0;
            encodingRow.Controls.Add(new Label { Text="文本编码",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft }); encodingRow.Controls.Add(encoding);
            path.TextChanged+=(s,e)=> { encoding.SelectedIndex=Array.IndexOf(GameTextEncoding.Ids,GameEncodingPreferences.Get(path.Text)); };
            var buttons=new FlowLayoutPanel { Dock=DockStyle.Top,Height=50,Padding=new Padding(12,5,0,5) };
            var exe=new Button { Text="选择原始 exe",AutoSize=true }; var folder=new Button { Text="选择目录",AutoSize=true }; var demo=new Button { Text="运行演示",AutoSize=true }; var start=new Button { Text="加载游戏",AutoSize=true };
            buttons.Controls.AddRange(new Control[]{exe,folder,demo,start});
            var list=new Panel { Dock=DockStyle.Fill,Padding=new Padding(16,8,16,0) }; list.Controls.Add(recent); list.Controls.Add(new Label { Text="最近加载（双击打开）",Dock=DockStyle.Top,Height=30 });
            info.Text="使用内置 Emuera 引擎读取游戏文件。原 exe 用于定位目录。\n游戏运行后，从顶部“AI 口上”配置 API 和酒馆预设。";
            Controls.Add(list); Controls.Add(info); Controls.Add(buttons); Controls.Add(encodingRow); Controls.Add(row); Controls.Add(heading);
            exe.Click+=(s,e)=> { using(var d=new OpenFileDialog { Filter="ERA 游戏程序|*.exe",Title="选择原游戏目录中的 Emuera exe",RestoreDirectory=true }) if(d.ShowDialog(this)==DialogResult.OK) path.Text=d.FileName; };
            folder.Click+=(s,e)=> { using(var d=new FolderBrowserDialog { Description="选择包含 CSV 与 ERB 的游戏目录",ShowNewFolderButton=false }) if(d.ShowDialog(this)==DialogResult.OK) path.Text=d.SelectedPath; };
            demo.Click+=(s,e)=> { path.Text=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"demo"); StartGame(); };
            start.Click+=(s,e)=>StartGame(); AcceptButton=start;
            recent.SelectedIndexChanged+=(s,e)=> { if(recent.SelectedItem!=null) path.Text=recent.SelectedItem.ToString(); };
            recent.DoubleClick+=(s,e)=>StartGame();
            DragEnter+=(s,e)=> { if(e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect=DragDropEffects.Copy; };
            DragDrop+=(s,e)=> { var files=e.Data.GetData(DataFormats.FileDrop) as string[]; if(files!=null && files.Length>0) path.Text=files[0]; };
            try { foreach(var item in Json.Array(Json.Codec().DeserializeObject(File.ReadAllText(history))).OfType<string>()) recent.Items.Add(item); } catch { }
        }
        void StartGame()
        {
            try
            {
                SelectedDirectory=GameLocator.Resolve(path.Text);
                SelectedEncoding=GameTextEncoding.Ids[Math.Max(0,encoding.SelectedIndex)];
                try {
                    var entries=new[]{SelectedDirectory}.Concat(recent.Items.Cast<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
                    Directory.CreateDirectory(Path.GetDirectoryName(history)); File.WriteAllText(history,Json.Codec().Serialize(entries));
                    GameEncodingPreferences.Save(SelectedDirectory,SelectedEncoding);
                } catch { }
                DialogResult=DialogResult.OK; Close();
            }
            catch(Exception ex) { info.Text=ex.Message; }
        }
    }

    internal static class GameEncodingPreferences
    {
        static string FilePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ai","game-encodings.json"); } }
        static string Key(string selection)
        {
            string value=Path.GetFullPath((selection ?? "").Trim().Trim('"'));
            if (File.Exists(value) && Path.GetExtension(value).Equals(".exe",StringComparison.OrdinalIgnoreCase)) value=Path.GetDirectoryName(value);
            return value.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        }
        static Dictionary<string,string> Read()
        {
            var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            try {
                var entries=Json.Object(Json.Codec().DeserializeObject(File.ReadAllText(FilePath)));
                if(entries!=null) foreach(var pair in entries) if(Array.IndexOf(GameTextEncoding.Ids,pair.Value as string)>=0) result[pair.Key]=(string)pair.Value;
            } catch { }
            return result;
        }
        internal static string Get(string selection)
        {
            try { string value; return Read().TryGetValue(Key(selection),out value) ? value : "auto"; }
            catch { return "auto"; }
        }
        internal static void Save(string directory,string encoding)
        {
            var entries=Read(); entries[Key(directory)]=encoding;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath,Json.Codec().Serialize(entries));
        }
    }
}
