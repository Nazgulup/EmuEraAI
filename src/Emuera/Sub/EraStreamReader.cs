using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

namespace MinorShift.Emuera.Sub
{
	internal sealed class EraStreamReader : IDisposable
	{
		public EraStreamReader(bool useRename)
		{
			this.useRename = useRename;
		}

		string filepath;
		string filename;
		bool useRename = false;
		int curNo = 0;
		int nextNo = 0;
		StreamReader reader;
		FileStream stream;

		public bool Open(string path)
		{
			return Open(path, Path.GetFileName(path));
		}

		public bool Open(string path, string name)
		{
			//そんなお行儀の悪いことはしていない
			//if (disposed)
			//    throw new ExeEE("破棄したオブジェクトを再利用しようとした");
			//if ((reader != null) || (stream != null) || (filepath != null))
			//    throw new ExeEE("使用中のオブジェクトを別用途に再利用しようとした");
			filepath = path;
			filename = name;
			nextNo = 0;
			curNo = 0;
			try
			{
				stream = new FileStream(filepath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
				reader = new StreamReader(stream, GameTextEncoding.Detect(stream));
			}
			catch
			{
				this.Dispose();
				return false;
			}
			return true;
		}

		public string ReadLine()
		{
			nextNo++;
			curNo = nextNo;
			return reader.ReadLine();
		}

		/// <summary>
		/// 次の有効な行を読む。LexicalAnalyzer経由でConfigを参照するのでConfig完成までつかわないこと。
		/// </summary>
		public StringStream ReadEnabledLine()
		{
			string line = null;
			StringStream st = null;
			curNo = nextNo;
			while (true)
			{
				line = reader.ReadLine();
				curNo++;
				nextNo++;
				if (line == null)
					return null;
				if (line.Length == 0)
					continue;

				if (useRename && (line.IndexOf("[[") >= 0) && (line.IndexOf("]]") >= 0))
				{
					foreach (KeyValuePair<string, string> pair in ParserMediator.RenameDic)
						line = line.Replace(pair.Key, pair.Value);
				}
				st = new StringStream(line);
				LexicalAnalyzer.SkipWhiteSpace(st);
				if (st.EOS)
					continue;
				if (st.Current == '}')
					throw new CodeEE("予期しない行連結終端記号'}'が見つかりました", new ScriptPosition(filename, curNo, line));
				if (st.Current == '{')
				{
					if (line.Trim() != "{")
						throw new CodeEE("行連結始端記号'{'の行に'{'以外の文字を含めることはできません", new ScriptPosition(filename, curNo, line));
					break;
				}
				return st;
			}
			//curNoはこの後加算しない(始端記号の行を行番号とする)
			StringBuilder b = new StringBuilder();
			while (true)
			{
				line = reader.ReadLine();
				nextNo++;
				if (line == null)
				{
					throw new CodeEE("行連結始端記号'{'が使われましたが終端記号'}'が見つかりません", new ScriptPosition(filename, curNo, line));
				}

				if (useRename && (line.IndexOf("[[") >= 0) && (line.IndexOf("]]") >= 0))
				{
					foreach (KeyValuePair<string, string> pair in ParserMediator.RenameDic)
						line = line.Replace(pair.Key, pair.Value);
				}
				string test = line.TrimStart();
				if (test.Length > 0)
				{
					if (test[0] == '}')
					{
						if (test.Trim() != "}")
							throw new CodeEE("行連結終端記号'}'の行に'}'以外の文字を含めることはできません", new ScriptPosition(filename, nextNo, line));
						break;
					}
                    //行連結文字なら1字でないとおかしい、というか、こうしないとFORMの数値変数処理が誤爆する。
                    //{
                    //A}
                    //みたいなどうしようもないコードは知ったこっちゃない
					if (test[0] == '{' && test.Length == 1)
						throw new CodeEE("予期しない行連結始端記号'{'が見つかりました", new ScriptPosition(filename, nextNo, line));
				}
				b.Append(line);
				b.Append(" ");
			}
			st = new StringStream(b.ToString());
			LexicalAnalyzer.SkipWhiteSpace(st);
			return st;
		}

		/// <summary>
		/// 直前に読んだ行の行番号
		/// </summary>
		public int LineNo
		{ get { return curNo; } }
		public string Filename
		{
			get
			{
				return filename;
			}
		}
		//public string Filepath
		//{
		//    get
		//    {
		//        return filepath;
		//    }
		//}

		public void Close() { this.Dispose(); }
		bool disposed = false;
		#region IDisposable メンバ

		public void Dispose()
		{
			if (disposed)
				return;
			if (reader != null)
				reader.Close();
			else if (stream != null)
				stream.Close();
			filepath = null;
			filename = null;
			reader = null;
			stream = null;
			disposed = true;
		}

		#endregion
	}

    // Per-file Unicode detection with the original legacy encoding as fallback.
    // Reading with the wrong multibyte decoder can consume a newline after a comment.
    internal static class GameTextEncoding
    {
        internal static readonly string[] Ids = { "auto", "shift-jis", "utf-8", "utf-16", "utf-16be", "gbk", "big5", "euc-jp", "iso-2022-jp" };
        internal static readonly string[] Labels = { "自动（Unicode / 日文 / 中文）", "日文 Shift-JIS / CP932", "UTF-8", "UTF-16 小端", "UTF-16 大端", "简体中文 GBK", "繁体中文 Big5", "日文 EUC-JP", "日文 ISO-2022-JP" };
        static Encoding forced;
        internal static void Select(string id)
        {
            switch ((id ?? "auto").ToLowerInvariant())
            {
                case "auto": forced = null; break;
                case "shift-jis": forced = Encoding.GetEncoding(932); break;
                case "utf-8": forced = new UTF8Encoding(false, true); break;
                case "utf-16": forced = Encoding.Unicode; break;
                case "utf-16be": forced = Encoding.BigEndianUnicode; break;
                case "gbk": forced = Encoding.GetEncoding(936); break;
                case "big5": forced = Encoding.GetEncoding(950); break;
                case "euc-jp": forced = Encoding.GetEncoding(51932); break;
                case "iso-2022-jp": forced = Encoding.GetEncoding(50220); break;
                default: throw new FormatException("不支持的文本编码：" + id);
            }
        }
        internal static Encoding Detect(Stream input)
        {
            if (forced != null) return forced;
            long position = input.Position;
            try
            {
                // Validate the entire file, including characters split across buffers.
                // StreamReader continues to honor UTF-8/UTF-16/UTF-32 BOMs.
                using (var probe = new StreamReader(input, new UTF8Encoding(false, true), true, 4096, true))
                {
                    var buffer = new char[4096];
                    while (probe.Read(buffer, 0, buffer.Length) > 0) { }
                    return probe.CurrentEncoding;
                }
            }
            catch (DecoderFallbackException)
            {
                // Mixed translated games can contain UTF-8 ERB and GBK ERH files.
                // Only accept legacy candidates after full strict validation: CP932's
                // replacement fallback can swallow LF and hide a following #DIM.
                foreach(int codepage in new[]{Config.Encode.CodePage,936,950})
                {
                    input.Position=position;
                    var encoding=Encoding.GetEncoding(codepage,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
                    try
                    {
                        using(var probe=new StreamReader(input,encoding,false,4096,true))
                        { var buffer=new char[4096]; while(probe.Read(buffer,0,buffer.Length)>0) { } }
                        return encoding;
                    }
                    catch(DecoderFallbackException) { }
                }
                return Encoding.GetEncoding(Config.Encode.CodePage,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
            }
            finally { input.Position = position; }
        }
    }
}
