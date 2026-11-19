using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using System.IO;

namespace MinorShift._Library
{
	public static class Sys
	{
		static Sys()
		{
			ExePath = Application.ExecutablePath;
			ExeDir = Path.GetDirectoryName(ExePath) + Path.DirectorySeparatorChar;
			ExeName = Path.GetFileName(ExePath);
		}

		/// <summary>
		/// 実行ファイルのパス
		/// </summary>
		public static readonly string ExePath;

		/// <summary>
		/// 実行ファイルのディレクトリ。最後にパスセパレータを付けたstring
		/// </summary>
		public static readonly string ExeDir;

		/// <summary>
		/// 実行ファイルの名前。ディレクトリなし
		/// </summary>
		public static readonly string ExeName;

		/// <summary>
		/// 2重起動防止。既に同名exeが実行されているならばtrueを返す
		/// </summary>
		/// <returns></returns>
		public static bool PrevInstance(int loaderParentProcessId=0)
		{
            using(var current=System.Diagnostics.Process.GetCurrentProcess())
            {
                bool found=false;
                foreach(var process in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
                    using(process) if(process.Id!=current.Id && process.Id!=loaderParentProcessId) found=true;
                return found;
            }

		}
	}
}

