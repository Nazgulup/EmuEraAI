using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera.Sub;

namespace MinorShift.Emuera.AI
{
    internal sealed class DesktopStorySession : IDisposable
    {
        readonly AiController controller;
        bool disposed;
        public StoryFunctionDocument Document { get; private set; }
        public DesktopStorySession(AiController target,string gameRoot,string storage)
        {
            controller=target;
            string root=Path.GetFullPath(gameRoot).TrimEnd('\\','/');
            string erb=Directory.EnumerateDirectories(root).FirstOrDefault(p=>Path.GetFileName(p).Equals("ERB",StringComparison.OrdinalIgnoreCase));
            var policy=new StoryPolicy(
                ()=>erb==null ? new string[0] : Directory.EnumerateFiles(erb,"*",SearchOption.AllDirectories).Where(p=>p.EndsWith(".erb",StringComparison.OrdinalIgnoreCase)).Select(p=>p.Substring(root.Length+1).Replace('\\','/')),
                file=>ReadText(Path.Combine(root,file)),
                Path.Combine(storage,"story-rules",StoryPolicy.Digest(root.ToLowerInvariant())+".json"),
                ()=> {
                    string csvDir=Directory.EnumerateDirectories(root).FirstOrDefault(p=>Path.GetFileName(p).Equals("CSV",StringComparison.OrdinalIgnoreCase));
                    string csv=csvDir==null ? null : Directory.EnumerateFiles(csvDir).FirstOrDefault(p=>Path.GetFileName(p).Equals("GAMEBASE.CSV",StringComparison.OrdinalIgnoreCase));
                    using(var bundle=OpenBundle()) return StoryTemplateCatalog.Match(bundle,StoryGameIdentity.Parse(Path.GetFileName(root),csv==null ? "" : ReadText(csv)));
                });
            controller.ConfigureStoryPolicy(policy);
            Document=new StoryFunctionDocument(policy,Path.Combine(storage,"story-documents",StoryPolicy.Digest(root.ToLowerInvariant())+".json"),()=>{});
        }
        internal static Stream OpenBundle()
        { return typeof(DesktopStorySession).Assembly.GetManifestResourceStream("EraAI.StoryTemplates.zip") ?? throw new FileNotFoundException("内置剧情模板缺失"); }
        internal static string ReadText(string path)
        { using(var stream=File.OpenRead(path)) using(var reader=new StreamReader(stream,GameTextEncoding.Detect(stream))) return reader.ReadToEnd(); }
        public async Task StartAsync()
        {
            await controller.PrepareStoryTemplatesAsync();
            if(disposed) return;
            if(controller.StoryTemplate.State!="matched")
            {
                await Document.Start(); if(disposed) return;
                controller.Log.Write("story.document",null,Document.Reason,"state",Document.State,"files",Document.Done);
            }
        }
        public void Dispose() { disposed=true; Document.Dispose(); }
    }
}
