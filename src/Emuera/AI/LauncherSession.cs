using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace MinorShift.Emuera.AI
{
    // Keep the picker alive outside the interpreter process. Legacy engine caches
    // include static config/macro paths, so a different game always gets a fresh process.
    internal static class LauncherSession
    {
        internal const int ReturnToPicker = 20;
        internal static void Run(string[] initial,Func<string[]> pick,Func<string[],int> play)
        {
            var next=initial;
            while(true)
            {
                if(next.Length==0) { next=pick(); if(next==null) return; }
                if(play(next)!=ReturnToPicker) return;
                next=new string[0];
            }
        }
        internal static string[] Pick()
        {
            using(var picker=new GameLauncher())
            {
                if(picker.ShowDialog()!=DialogResult.OK) return null;
                return new[]{"--game",picker.SelectedDirectory,"--encoding",picker.SelectedEncoding};
            }
        }
        internal static int Play(string[] args)
        {
            try
            {
                var childArgs=new[]{"--engine-session",Process.GetCurrentProcess().Id.ToString()}.Concat(args);
                var info=new ProcessStartInfo(Application.ExecutablePath,string.Join(" ",childArgs.Select(Quote))) {
                    UseShellExecute=false,WorkingDirectory=AppDomain.CurrentDomain.BaseDirectory
                };
                using(var child=Process.Start(info))
                {
                    if(child==null) throw new IOException("未能创建游戏进程。");
                    child.WaitForExit(); return child.ExitCode;
                }
            }
            catch(Exception ex) { MessageBox.Show("无法打开游戏："+ex.Message,"游戏加载器"); return ReturnToPicker; }
        }
        // Windows argv quoting, including directories ending with a backslash.
        internal static string Quote(string value)
        {
            var b=new StringBuilder("\""); int slashes=0;
            foreach(char c in value)
            {
                if(c=='\\') { slashes++; continue; }
                b.Append('\\',c=='\"' ? slashes*2+1 : slashes); slashes=0; b.Append(c);
            }
            b.Append('\\',slashes*2); return b.Append('"').ToString();
        }
    }
}
