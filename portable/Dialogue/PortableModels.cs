using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MinorShift.Emuera.AI
{
    internal sealed class DialogueSnapshot
    {
        public readonly string EventId, Character, User, Facts, Original, Hook;
        public readonly AddressTerms Addresses;
        public readonly string[] StoryContext;
        public readonly long StoryTurn;
        public DialogueSnapshot(string eventId,string character,string user,string facts,string original,string hook,IDictionary<string,string> addresses=null,string[] storyContext=null,long storyTurn=0)
        { EventId=eventId; Character=character; User=user; Facts=facts; Original=original; Hook=hook; Addresses=new AddressTerms(addresses); StoryContext=storyContext ?? new string[0]; StoryTurn=storyTurn; }
    }
    internal sealed class AiSettings
    {
        internal string TraceId;
        public bool Enabled { get; set; }
        public bool AutoGenerate { get; set; }=true;
        public bool ProtectStory { get; set; }=true;
        public bool GenerateWithoutOriginal { get; set; }
        public bool TranslationEnabled { get; set; }
        public string TranslationLanguage { get; set; }="zh-CN";
        public int TranslationTokens { get; set; }=2048;
        public string Endpoint { get; set; }="https://api.openai.com/v1";
        public string Model { get; set; }="";
        public string ApiKey { get; set; }="";
        public int TimeoutSeconds { get; set; }=20;
        public int OutputTokens { get; set; }=400;
        public string PresetJson { get; set; }="";
        public string CharacterDescription { get; set; }="";
        public string Personality { get; set; }="";
        public string World { get; set; }="";
        public bool UseGameStyle { get; set; }=true;
        public bool UsePresetOutputTokens { get; set; }
        public string ThinkingMode { get; set; }="auto";
        public string GameStyle { get; set; }="";
        public AiSettings Copy() => (AiSettings)MemberwiseClone();
        public Dictionary<string,object> ToDictionary(bool includeKey) => new Dictionary<string,object> {
            {"Enabled",Enabled},{"ProtectStory",ProtectStory},{"AutoGenerate",AutoGenerate},{"GenerateWithoutOriginal",GenerateWithoutOriginal},{"Endpoint",Endpoint},{"Model",Model},{"ApiKey",includeKey ? ApiKey : ""},
            {"TimeoutSeconds",TimeoutSeconds},{"OutputTokens",OutputTokens},{"PresetJson",PresetJson},{"CharacterDescription",CharacterDescription},{"Personality",Personality},{"World",World},{"UseGameStyle",UseGameStyle},{"UsePresetOutputTokens",UsePresetOutputTokens},{"ThinkingMode",ThinkingMode},
            {"TranslationEnabled",TranslationEnabled},{"TranslationLanguage",TranslationLanguage},{"TranslationTokens",TranslationTokens}
        };
        public static AiSettings Parse(string raw)
        {
            var d=Json.Object(Json.Codec().DeserializeObject(raw));
            if(d==null) throw new FormatException("设置格式不正确。");
            var supplement=Json.Bool(d,"GenerateWithoutOriginal");
            string language=Json.Text(d,"TranslationLanguage","zh-CN"); Translation.LanguageName(language);
            return new AiSettings { ProtectStory=Json.Bool(d,"ProtectStory",true), TranslationEnabled=Json.Bool(d,"TranslationEnabled"), TranslationLanguage=language, TranslationTokens=Preset.ReadTokenLimit(Json.Get(d,"TranslationTokens",2048),"翻译输出上限 "), GenerateWithoutOriginal=supplement, Enabled=Json.Bool(d,"Enabled"), AutoGenerate=Json.Bool(d,"AutoGenerate",true), Endpoint=Json.Text(d,"Endpoint","https://api.openai.com/v1"),Model=Json.Text(d,"Model"),ApiKey=Json.Text(d,"ApiKey"),TimeoutSeconds=Math.Clamp(Json.Int(d,"TimeoutSeconds",20),2,180),OutputTokens=Preset.ReadTokenLimit(Json.Get(d,"OutputTokens",400),"设置中的输出上限 "),PresetJson=Json.Text(d,"PresetJson"),CharacterDescription=Json.Text(d,"CharacterDescription"),Personality=Json.Text(d,"Personality"),World=Json.Text(d,"World"),UseGameStyle=Json.Bool(d,"UseGameStyle",true),UsePresetOutputTokens=Json.Bool(d,"UsePresetOutputTokens"),ThinkingMode=Json.Text(d,"ThinkingMode","auto") };
        }
    }
    // Matches the desktop serializer's dictionaries/arrays. Explicit writing also supports trimming.
    internal static class Json
    {
        public static CodecImpl Codec()=>new CodecImpl();
        public static Dictionary<string,object> Object(object value)=>value as Dictionary<string,object>;
        public static object Get(Dictionary<string,object> d,string key,object fallback=null) { object value; return d!=null && d.TryGetValue(key,out value) ? value : fallback; }
        public static string Text(Dictionary<string,object> d,string key,string fallback="")=>Convert.ToString(Get(d,key,fallback),System.Globalization.CultureInfo.InvariantCulture);
        public static int Int(Dictionary<string,object> d,string key,int fallback=0)=>Convert.ToInt32(Get(d,key,fallback));
        public static bool Bool(Dictionary<string,object> d,string key,bool fallback=false)=>Convert.ToBoolean(Get(d,key,fallback));
        public static object[] Array(object value)=>value as object[] ?? System.Array.Empty<object>();
        internal sealed class CodecImpl
        {
            public object DeserializeObject(string raw)
            {
                if(raw.Length>Preset.MaxEnvelopeChars) throw new FormatException("JSON 设置数据过大。");
                using var doc=JsonDocument.Parse(raw,new JsonDocumentOptions { MaxDepth=48 });
                return Read(doc.RootElement);
            }
            static object Read(JsonElement e)=>e.ValueKind switch {
                JsonValueKind.Object=>e.EnumerateObject().ToDictionary(p=>p.Name,p=>Read(p.Value)),
                JsonValueKind.Array=>e.EnumerateArray().Select(Read).ToArray(),
                JsonValueKind.String=>e.GetString(), JsonValueKind.Number=>e.TryGetInt64(out long n) ? (object)n : e.GetDouble(),
                JsonValueKind.True=>true,JsonValueKind.False=>false,_=>null
            };
            public string Serialize(object value)
            {
                using var buffer=new MemoryStream();
                using(var writer=new Utf8JsonWriter(buffer,new JsonWriterOptions { Encoder=System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) })) { Write(writer,value); writer.Flush(); }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
            static void Write(Utf8JsonWriter w,object value)
            {
                switch(value)
                {
                    case null: w.WriteNullValue(); break;
                    case string s: w.WriteStringValue(s); break;
                    case bool b: w.WriteBooleanValue(b); break;
                    case int n: w.WriteNumberValue(n); break;
                    case long n: w.WriteNumberValue(n); break;
                    case double n: w.WriteNumberValue(n); break;
                    case decimal n: w.WriteNumberValue(n); break;
                    case IDictionary<string,object> d: w.WriteStartObject(); foreach(var pair in d) { w.WritePropertyName(pair.Key); Write(w,pair.Value); } w.WriteEndObject(); break;
                    case IEnumerable list: w.WriteStartArray(); foreach(var item in list) Write(w,item); w.WriteEndArray(); break;
                    default: throw new FormatException("不支持的 JSON 数据类型。");
                }
            }
        }
    }
}
