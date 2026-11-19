using System;
using System.IO;
using System.Windows.Forms;
using System.Linq;
using MinorShift.Emuera.Sub;
using MinorShift.Emuera.AI;

namespace MinorShift.Emuera
{
    internal sealed partial class MainWindow
    {
        AiController aiController;
        AiWindow aiWindow;
        DesktopStorySession aiStorySession;
        void InitializeAi()
        {
            string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ai","settings.json");
            string warning; var settings=AiSettings.Load(path,out warning);
            aiController=new AiController(settings,new AiClient(),new AiLog(Path.Combine(Path.GetDirectoryName(path),"logs","generation.log"))); console.AttachAi(aiController);
            string root=Program.ExeDir.TrimEnd('\\','/');
            aiStorySession=new DesktopStorySession(aiController,root,Path.GetDirectoryName(path));
            aiController.ConfigureGameStyle(()=>GameStyleCorpus.Read(Path.GetFileName(root),
                GameStyleCorpus.Enumerate(
                    dir=>Directory.EnumerateDirectories(Path.Combine(root,dir)).Where(p=>(File.GetAttributes(p)&FileAttributes.ReparsePoint)==0).Select(p=>p.Substring(root.Length+1).Replace('\\','/')),
                    dir=>Directory.EnumerateFiles(Path.Combine(root,dir)).Where(p=>(File.GetAttributes(p)&FileAttributes.ReparsePoint)==0).Select(p=>p.Substring(root.Length+1).Replace('\\','/'))),
                file=>File.OpenRead(Path.Combine(root,file)),
                bytes=> { using(var stream=new MemoryStream(bytes)) using(var reader=new StreamReader(stream,GameTextEncoding.Detect(stream))) return reader.ReadToEnd(); }),Path.Combine(Path.GetDirectoryName(path),"styles"));
            aiWindow=new AiWindow(aiController,path,aiStorySession.Document);
            var prepareStory=aiStorySession.StartAsync();
            FormClosed+=(s,e)=>aiStorySession.Dispose();
            var menu=new ToolStripMenuItem("AI 口上");
            menu.Click+=(s,e)=> { aiWindow.Show(this); aiWindow.BringToFront(); };
            menuStrip.Items.Add(menu);
            Shown+=(s,e)=> { if(warning.Length>0) MessageBox.Show(this,warning,"AI 设置"); };
        }
    }
}
