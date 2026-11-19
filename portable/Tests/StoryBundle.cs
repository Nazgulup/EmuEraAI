using System.IO.Compression;
using System.Text;
using MinorShift.Emuera.AI;
using MinorShift.Emuera.Runtime.Utils;

internal static class StoryBundle
{
    public static string Metadata(string game)
    {
        var csv=Directory.GetDirectories(game).FirstOrDefault(d=>Path.GetFileName(d).Equals("CSV",StringComparison.OrdinalIgnoreCase));
        var path=csv==null ? null : Directory.GetFiles(csv).FirstOrDefault(p=>Path.GetFileName(p).Equals("GAMEBASE.CSV",StringComparison.OrdinalIgnoreCase));
        return path==null ? "" : string.Join("\n",EncodingHandler.ReadAllLinesFromBytes(File.ReadAllBytes(path)));
    }
    public static string BundlePath(string root) => Path.Combine(root,"portable","era-core","EraCore.Maui","Resources","Raw","story-templates.zip");
    public static void VerifyMatches(string root)
    {
        var catalog=Json.Array(Json.Codec().DeserializeObject(File.ReadAllText(Path.Combine(root,"logs","builtin-template-catalog.json"))));
        var results=new List<object>();
        foreach(var row in catalog.Select(Json.Object).Where(r=>Json.Bool(r,"automatic")))
        {
            string name=Json.Text(row,"name"), game=Path.Combine(Path.GetDirectoryName(root)!,name);
            using var stream=File.OpenRead(BundlePath(root)); var match=StoryTemplateCatalog.Match(stream,StoryGameIdentity.Parse(Path.GetFileName(game),Metadata(game)));
            results.Add(new Dictionary<string,object>{{"game",name},{"state",match.State},{"selected",match.Name},{"version",match.Version},{"reason",match.Reason},{"rules",match.Rules.Count}});
            Console.WriteLine(match.State+" | "+name+" | "+match.Name);
        }
        File.WriteAllText(Path.Combine(root,"logs","builtin-match-report.json"),Json.Codec().Serialize(results));
        Console.WriteLine("MATCH AUDIT: "+results.Count+" games; "+results.Select(Json.Object).Count(r=>Json.Text(r,"state")=="matched")+" matched; remainder conservative fallback");
    }
    public static void Build(string root)
    {
        string profiles=Path.Combine(root,"story-profiles"), workspace=Path.GetDirectoryName(root)!;
        var index=Json.Object(Json.Codec().DeserializeObject(File.ReadAllText(Path.Combine(profiles,"index.json"))));
        var catalog=new List<object>(); string output=BundlePath(root); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using(var zip=ZipFile.Open(output+".new",ZipArchiveMode.Create))
        {
            void Write(string name,string content) { var entry=zip.CreateEntry(name,CompressionLevel.Optimal); entry.LastWriteTime=new DateTimeOffset(2026,11,19,0,0,0,TimeSpan.Zero); using var writer=new StreamWriter(entry.Open(),new UTF8Encoding(false)); writer.Write(content); }
            foreach(var item in Json.Array(index["games"]))
            {
                var row=Json.Object(item); string relative=Json.Text(row,"game"), game=Path.Combine(workspace,relative), filename=Json.Text(row,"profile"), id=Path.GetFileNameWithoutExtension(filename);
                var identity=StoryGameIdentity.Parse(Path.GetFileName(game),Metadata(game));
                var source=Json.Object(Json.Codec().DeserializeObject(File.ReadAllText(Path.Combine(profiles,filename))));
                var entries=Json.Array(source["rules"]).Select(Json.Object).GroupBy(r=>Json.Text(r,"key")).OrderBy(g=>g.Key,StringComparer.Ordinal)
                    .Select(g=>(object)new[]{g.Key,g.Select(r=>Json.Text(r,"classification")).OrderBy(k=>k=="story" ? 0 : k=="unknown" ? 1 : 2).First()}).ToArray();
                string raw=Json.Codec().Serialize(entries); Write("profiles/"+id+".json",raw);
                catalog.Add(new Dictionary<string,object>{{"id",id},{"name",relative},{"folder",identity.Folder},{"title",identity.Title},{"version",identity.Version},{"versionName",identity.VersionName},{"code",identity.Code},{"keywords",StoryGameIdentity.Keywords(identity.Folder,identity.Title)},{"game",Json.Text(row,"type")=="game"},{"automatic",Json.Text(row,"type")=="game" && identity.Version.Length>0},{"digest",StoryPolicy.Digest(raw)},{"rules",entries.Length}});
            }
            Write("catalog.json",Json.Codec().Serialize(new Dictionary<string,object>{{"schema",1},{"revision","2026-11-19.1"},{"profiles",catalog.ToArray()}}));
        }
        File.Move(output+".new",output,true);
        Console.WriteLine("BUNDLE: "+catalog.Count+" templates, "+new FileInfo(output).Length+" bytes; metadata only, no allow overrides");
        File.WriteAllText(Path.Combine(root,"logs","builtin-template-catalog.json"),Json.Codec().Serialize(catalog));
    }
}
