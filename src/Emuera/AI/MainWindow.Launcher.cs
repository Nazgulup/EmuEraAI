using System;
using System.Windows.Forms;

namespace MinorShift.Emuera
{
    internal sealed partial class MainWindow
    {
        internal Func<bool> ConfirmReturnToSelection;
        void InitializeLauncherMenu()
        {
            ConfirmReturnToSelection=()=>MessageBox.Show(this,"退出当前游戏并返回游戏选择？\n未保存的进度将丢失，请先在游戏内保存。", "返回游戏选择",MessageBoxButtons.OKCancel,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.OK;
            var back=new ToolStripMenuItem("返回游戏选择") { Name="returnToSelection" };
            back.Click+=(s,e)=>Close(); menuStrip.Items.Add(back);
            exitToolStripMenuItem.Text="返回游戏选择(&X)";
        }
        internal bool PrepareGameClose(CloseReason reason)
        {
            if(Program.LoaderParentProcessId<=0 || Program.Reboot || reason!=CloseReason.UserClosing) return true;
            if(!ConfirmReturnToSelection()) return false;
            Program.ReturnToGameSelection=true;
            if(aiController!=null) aiController.Log.Write("game.return-to-selection",null,"退出当前游戏，取消旧请求并返回游戏选择");
            return true;
        }
    }
}
