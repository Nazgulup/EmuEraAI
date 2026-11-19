using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Globalization;

namespace MinorShift.Emuera.AI
{
    internal sealed class StoryGameIdentity
    {
        public string Folder="", Title="", Version="", VersionName="", Code="";
        public static string Normalize(string value) => Regex.Replace((value ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant(),@"[^\p{L}\p{N}]","");
        public static string Number(string value) => long.TryParse(value,out long n) && n>=0 ? n.ToString(CultureInfo.InvariantCulture) : "";
        public static string VersionValue(string value) => Number(value).Length>0 ? Number(value) : value.Length<=64 ? Normalize(value) : "";
        public static StoryGameIdentity Parse(string folder,string csv)
        {
            var result=new StoryGameIdentity { Folder=folder ?? "" };
            foreach(string raw in (csv ?? "").Split('\n'))
            {
                string line=raw.Trim().TrimStart('\uFEFF'); if(line.StartsWith(";")) continue;
                int comma=line.IndexOf(','); if(comma<0) continue;
                string key=line.Substring(0,comma).Trim(), value=line.Substring(comma+1).Split(',',';')[0].Trim().Trim('"');
                switch(key)
                {
                    case "タイトル": case "遊戲標題": case "游戏标题": case "標題": case "标题": case "Title": result.Title=value; break;
                    case "バージョン": case "版本": case "遊戲版本": case "游戏版本": case "方案": case "Version": result.Version=VersionValue(value); break;
                    case "バージョン名": case "版本名": case "版本名称": result.VersionName=value; break;
                    case "コード": case "代碼": case "代码": case "Code": result.Code=Number(value); break;
                }
            }
            return result;
        }
        public static string[] Keywords(string folder,string title)
        {
            var result=new HashSet<string>(StringComparer.Ordinal);
            foreach(string value in new[]{folder,title})
            {
                string full=Normalize(value); if(full.Length>=4 && full!="master") result.Add(full);
                foreach(Match m in Regex.Matches(value ?? "",@"era[a-z_]+",RegexOptions.IgnoreCase))
                { string word=Normalize(m.Value); if(word.Length>=5) result.Add(word); }
            }
            return result.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        }
        public static string VersionText(string code) => long.TryParse(code,out long n) ? (n/1000m).ToString("0.###",CultureInfo.InvariantCulture) : code;
        public static string[] FolderVersions(string folder) => Regex.Matches(folder ?? "",@"(?<![\d.])\d+\.\d+(?![\d.])").Cast<Match>().Select(m=>decimal.Parse(m.Value,CultureInfo.InvariantCulture).ToString("0.###",CultureInfo.InvariantCulture)).Distinct().ToArray();
    }

    // APK contains compressed metadata only. Never writes the user's manual rules.
    internal static class StoryTemplateCatalog
    {
        sealed class Candidate
        {
            public Dictionary<string,object> Row;
            public int Score;
            public string VersionReason;
        }
        public static StoryTemplateData Match(Stream archive,StoryGameIdentity game)
        {
            using(var seekable=new MemoryStream())
            {
            archive.CopyTo(seekable); seekable.Position=0;
            using(var zip=new ZipArchive(seekable,ZipArchiveMode.Read))
            {
            string Read(string name,int max)
            {
                var entry=zip.GetEntry(name) ?? throw new FormatException("内置模板缺失");
                if(entry.Length>max) throw new FormatException("内置模板过大");
                using(var reader=new StreamReader(entry.Open(),Encoding.UTF8)) return reader.ReadToEnd();
            }
            var catalog=Json.Object(Json.Codec().DeserializeObject(Read("catalog.json",2*1024*1024)));
            if(Json.Int(catalog,"schema")!=1) throw new FormatException("内置模板版本无效");
            var candidates=new List<Candidate>();
            string title=StoryGameIdentity.Normalize(game.Title), folder=StoryGameIdentity.Normalize(game.Folder);
            bool nameSeen=false, versionSeen=false;
            foreach(var item in Json.Array(Json.Get(catalog,"profiles")))
            {
                var row=Json.Object(item); if(!Json.Bool(row,"automatic") && !Json.Bool(row,"game")) continue;
                var keywords=Json.Array(Json.Get(row,"keywords")).Select(x=>Convert.ToString(x)).Where(x=>x.Length>=4).ToArray();
                int score=keywords.Where(k=>title.Contains(k) || folder.Contains(k)).Select(k=>k.Length).DefaultIfEmpty(0).Max();
                if(score==0) continue; nameSeen=true;
                if(!Json.Bool(row,"automatic")) continue;
                string version=Json.Text(row,"version"), nameVersion=StoryGameIdentity.Normalize(Json.Text(row,"versionName"));
                var gameFolderVersions=StoryGameIdentity.FolderVersions(game.Folder);
                var profileFolderVersions=StoryGameIdentity.FolderVersions(Json.Text(row,"folder"));
                string versionReason;
                if(game.Version.Length>0)
                {
                    if(version!=game.Version || (nameVersion.Length>0 && game.VersionName.Length>0 && nameVersion!=StoryGameIdentity.Normalize(game.VersionName))) continue;
                    if(!long.TryParse(version,out _) && gameFolderVersions.Length>0 && profileFolderVersions.Length>0 && !gameFolderVersions.SequenceEqual(profileFolderVersions)) continue;
                    versionReason="游戏配置版本 "+StoryGameIdentity.VersionText(version)+"（"+version+"）一致";
                }
                else
                {
                    var versions=gameFolderVersions;
                    string expected=long.TryParse(version,out _) ? StoryGameIdentity.VersionText(version) : profileFolderVersions.Length==1 ? profileFolderVersions[0] : "";
                    if(expected.Length==0 || versions.Length!=1 || versions[0]!=expected) continue;
                    versionReason="目录中的明确版本 "+versions[0]+" 一致（缺少配置版本）";
                }
                versionSeen=true;
                string code=Json.Text(row,"code");
                if(game.Code.Length>0 && game.Code!="0" && code.Length>0 && code!="0" && game.Code!=code) continue;
                if(StoryGameIdentity.Normalize(Json.Text(row,"title"))==title && title.Length>0) score+=1000;
                if(StoryGameIdentity.Normalize(Json.Text(row,"folder"))==folder && folder.Length>0) score+=2000;
                if(nameVersion.Length>0 && nameVersion==StoryGameIdentity.Normalize(game.VersionName)) score+=500;
                candidates.Add(new Candidate { Row=row,Score=score,VersionReason=versionReason });
            }
            if(candidates.Count==0) return new StoryTemplateData { State="unmatched", Reason=!nameSeen ? "没有匹配游戏名称关键词的内置模板，继续本地保守判断" : !versionSeen ? "游戏关键词匹配，但版本缺失或不符；不跨版本套用，继续本地保守判断" : "游戏标识不一致，继续本地保守判断" };
            int best=candidates.Max(c=>c.Score); var winners=candidates.Where(c=>c.Score==best).ToArray();
            if(winners.Select(c=>Json.Text(c.Row,"digest")).Distinct().Count()!=1)
                return new StoryTemplateData { State="ambiguous",Reason="同一关键词与版本对应多个不同修改版，无法唯一确认；继续本地保守判断" };
            var winner=winners.OrderBy(c=>Json.Text(c.Row,"id"),StringComparer.Ordinal).First(); var selected=winner.Row;
            string id=Json.Text(selected,"id"); if(!Regex.IsMatch(id,@"^[a-f0-9]{12}$")) throw new FormatException("模板标识无效");
            string raw=Read("profiles/"+id+".json",32*1024*1024);
            if(StoryPolicy.Digest(raw)!=Json.Text(selected,"digest")) throw new FormatException("内置模板校验失败");
            var result=new StoryTemplateData { Id=id,Name=Json.Text(selected,"name"),Version=Json.Text(selected,"version"),State="matched",Reason="名称关键词匹配；"+winner.VersionReason+"；逐函数核对脚本指纹，手动规则保留" };
            foreach(var item in Json.Array(Json.Codec().DeserializeObject(raw)))
            {
                var pair=Json.Array(item); if(pair.Length!=2) throw new FormatException("模板条目无效");
                string key=Convert.ToString(pair[0]), kind=Convert.ToString(pair[1]);
                if(!Regex.IsMatch(key,@"^[^|]+\|[^|]+\|[a-f0-9]{64}$") || (kind!="routine" && kind!="story" && kind!="unknown")) throw new FormatException("模板规则无效");
                result.Rules.Add(key,kind);
            }
            return result;
            }
            }
        }
    }
}
