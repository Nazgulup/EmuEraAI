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
                Path.Combine(storage,"story-rules",StoryPolicy.Digest(root.ToLowerInvariant())+".json"));
            controller.ConfigureStoryPolicy(policy);
            Document=new StoryFunctionDocument(policy,Path.Combine(storage,"story-documents",StoryPolicy.Digest(root.ToLowerInvariant())+".json"),()=>{});
        }
        internal static string ReadText(string path)
        { using(var stream=File.OpenRead(path)) using(var reader=new StreamReader(stream,GameTextEncoding.Detect(stream))) return reader.ReadToEnd(); }
        public async Task StartAsync()
        {
            if(disposed) return;
            await Document.Start(); if(disposed) return;
            controller.Log.Write("story.document",null,Document.Reason,"state",Document.State,"files",Document.Done);
        }
        public void Dispose() { disposed=true; Document.Dispose(); }
    }
}
