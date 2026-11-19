using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MinorShift.Emuera.AI
{
    // No engine object or mutable variable array may cross this boundary.
    internal sealed class DialogueSnapshot
    {
        public readonly string EventId, Character, User, Facts, Original, Hook;
        public readonly AddressTerms Addresses;
        public readonly IDictionary<string,string> AddressValues;
        public readonly string[] StoryContext;
        public readonly long StoryTurn;
        public DialogueSnapshot(string eventId, string character, string user, string facts, string original, string hook, IDictionary<string,string> addresses=null,string[] storyContext=null,long storyTurn=0)
        { EventId=eventId; Character=character; User=user; Facts=facts; Original=original; Hook=hook; AddressValues=addresses==null ? new Dictionary<string,string>() : new Dictionary<string,string>(addresses); Addresses=new AddressTerms(AddressValues); StoryContext=storyContext ?? new string[0]; StoryTurn=storyTurn; }
    }

    internal sealed class AiSettings
    {
        internal string TraceId;
        public bool Enabled { get; set; }
        public bool AutoGenerate { get; set; }
        public bool ProtectStory { get; set; }
        public string Endpoint { get; set; }
        public string Model { get; set; }
        public string ProtectedKey { get; set; }
        [ScriptIgnore] public string ApiKey { get; set; }
        public int TimeoutSeconds { get; set; }
        public int OutputTokens { get; set; }
        public string PresetJson { get; set; }
        public string CharacterDescription { get; set; }
        public string Personality { get; set; }
        public string World { get; set; }
        public bool UseGameStyle { get; set; }
        public bool UsePresetOutputTokens { get; set; }
        public string ThinkingMode { get; set; }
        [ScriptIgnore] public string GameStyle { get; set; }
        public AiSettings()
        {
            Endpoint="https://api.openai.com/v1"; Model=""; ApiKey=""; ProtectedKey="";
            TimeoutSeconds=20; OutputTokens=400; Enabled=true; AutoGenerate=true; ProtectStory=false; PresetJson="";
            CharacterDescription=""; Personality=""; World=""; UseGameStyle=true; ThinkingMode="auto"; GameStyle="";
        }
        public AiSettings Copy() { return (AiSettings)MemberwiseClone(); }
        public static AiSettings Load(string path, out string warning)
        {
            warning="";
            try
            {
                if (!File.Exists(path)) return new AiSettings();
                var result=Json.Codec().Deserialize<AiSettings>(File.ReadAllText(path,Encoding.UTF8));
                if (result==null) throw new FormatException();
                if (!string.IsNullOrEmpty(result.ProtectedKey))
                    result.ApiKey=Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(result.ProtectedKey),null,DataProtectionScope.CurrentUser));
                return result;
            }
            catch { warning="AI 设置无法读取，已关闭 AI 并使用原口上。"; return new AiSettings { Enabled=false }; }
        }
        public void Save(string path)
        {
            var copy=Copy();
            copy.ProtectedKey=string.IsNullOrEmpty(ApiKey) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(ApiKey),null,DataProtectionScope.CurrentUser));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary=path+".tmp";
            File.WriteAllText(temporary,Json.Codec().Serialize(copy),new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
        }
    }

    internal static class Json
    {
        public static JavaScriptSerializer Codec() { return new JavaScriptSerializer { MaxJsonLength=Preset.MaxEnvelopeChars, RecursionLimit=48 }; }
        public static Dictionary<string,object> Object(object value) { return value as Dictionary<string,object>; }
        public static object Get(Dictionary<string,object> d,string key,object fallback=null) { object v; return d!=null && d.TryGetValue(key,out v) ? v : fallback; }
        public static string Text(Dictionary<string,object> d,string key,string fallback="") { return Convert.ToString(Get(d,key,fallback),System.Globalization.CultureInfo.InvariantCulture); }
        public static int Int(Dictionary<string,object> d,string key,int fallback=0) { return Convert.ToInt32(Get(d,key,fallback)); }
        public static bool Bool(Dictionary<string,object> d,string key,bool fallback=false) { return Convert.ToBoolean(Get(d,key,fallback)); }
        public static object[] Array(object value) { return value as object[] ?? new object[0]; }
    }
}
