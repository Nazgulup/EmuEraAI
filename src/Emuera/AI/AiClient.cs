using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MinorShift.Emuera.AI
{
    internal interface IAiClient
    {
        Task<string> GenerateAsync(AiSettings settings, Dictionary<string,object> request, CancellationToken cancellation);
    }
    internal sealed class AiClient : IAiClient, IDisposable
    {
        readonly HttpClient http;
        internal AiLog Log { get; set; }
        public AiClient() : this(new HttpClientHandler { AllowAutoRedirect=false }) { }
        internal AiClient(HttpMessageHandler handler)
        {
#if !NET8_0_OR_GREATER
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
            http=new HttpClient(handler) { Timeout=System.Threading.Timeout.InfiniteTimeSpan };
        }
        public static Uri GetEndpoint(string value)
        {
            Uri uri;
            if(!Uri.TryCreate((value ?? "").Trim(),UriKind.Absolute,out uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query))
                throw new FormatException("API 地址无效。");
            if(uri.Scheme!="https" && !(uri.Scheme=="http" && uri.IsLoopback)) throw new FormatException("远程 API 请使用 HTTPS；本地接口可使用 HTTP。");
            string endpoint=uri.AbsoluteUri.TrimEnd('/');
            if(!endpoint.EndsWith("/chat/completions",StringComparison.OrdinalIgnoreCase)) endpoint+="/chat/completions";
            return new Uri(endpoint);
        }
        public async Task<string> GenerateAsync(AiSettings settings,Dictionary<string,object> request,CancellationToken cancellation)
        {
            if(string.IsNullOrWhiteSpace(settings.Model)) throw new FormatException("尚未填写模型名称。");
            var endpoint=GetEndpoint(settings.Endpoint); if(Log!=null) Log.Protect(settings.ApiKey);
            ApplyRequestOptions(settings,request);
            using(var message=new HttpRequestMessage(HttpMethod.Post,endpoint))
            {
                if(!string.IsNullOrWhiteSpace(settings.ApiKey)) message.Headers.Authorization=new AuthenticationHeaderValue("Bearer",settings.ApiKey.Trim());
                message.Content=new StringContent(Json.Codec().Serialize(request),Encoding.UTF8,"application/json");
                if(Log!=null) Log.Write("http.send",settings.TraceId,"发送 Chat Completions 请求","endpoint",endpoint.AbsoluteUri,"model",settings.Model,"maxTokens",Json.Int(request,"max_tokens"),"thinking",Json.Text(Json.Object(Json.Get(request,"thinking")),"type","provider-default"));
                using(var response=await http.SendAsync(message,HttpCompletionOption.ResponseHeadersRead,cancellation).ConfigureAwait(false))
                {
                    System.Collections.Generic.IEnumerable<string> requestIds;
                    string providerId=response.Headers.TryGetValues("x-request-id",out requestIds) ? requestIds.FirstOrDefault() : "";
                    if(Log!=null) Log.Write("http.response",settings.TraceId,"收到 API 响应头","status",(int)response.StatusCode,"providerRequestId",providerId ?? "","contentType",response.Content.Headers.ContentType==null ? "" : response.Content.Headers.ContentType.ToString());
                    if(response.Content.Headers.ContentLength>131072) throw new FormatException("API 回复过大。");
                    using(var stream=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using(var buffer=new MemoryStream())
                    {
                        byte[] bytes=new byte[4096]; int read;
                        while((read=await stream.ReadAsync(bytes,0,bytes.Length,cancellation).ConfigureAwait(false))>0)
                        { if(buffer.Length+read>131072) throw new FormatException("API 回复过大。"); buffer.Write(bytes,0,read); }
                        string raw=Encoding.UTF8.GetString(buffer.ToArray());
                        if(!response.IsSuccessStatusCode)
                        {
                            if(Log!=null) Log.Write("http.error",settings.TraceId,raw,"status",(int)response.StatusCode);
                            throw new InvalidOperationException("API 返回 HTTP "+(int)response.StatusCode+"；错误详情已写入生成日志。");
                        }
                        LogResponse(raw,settings.TraceId);
                        return Parse(raw);
                    }
                }
            }
        }
        internal static void ApplyRequestOptions(AiSettings settings,Dictionary<string,object> request)
        {
            string mode=string.IsNullOrEmpty(settings.ThinkingMode) ? "auto" : settings.ThinkingMode;
            if(mode=="auto") mode=GetEndpoint(settings.Endpoint).Host.Equals("api.deepseek.com",StringComparison.OrdinalIgnoreCase) ? "disabled" : "provider";
            if(mode=="enabled" || mode=="disabled") request["thinking"]=new Dictionary<string,object>{{"type",mode}};
            else if(mode!="provider") throw new FormatException("无效的思考模式设置。");
        }
        void LogResponse(string raw,string trace)
        {
            if(Log==null) return;
            try
            {
                var data=Json.Object(Json.Codec().DeserializeObject(raw)); var choices=Json.Array(Json.Get(data,"choices"));
                var choice=choices.Length==0 ? null : Json.Object(choices[0]); var message=Json.Object(Json.Get(choice,"message"));
                var usage=Json.Object(Json.Get(data,"usage"));
                Log.Write("response.summary",trace,"回复结构摘要；正文见 response.content","choices",choices.Length,"finishReason",Json.Text(choice,"finish_reason"),"contentChars",Json.Text(message,"content").Length,"reasoningChars",Json.Text(message,"reasoning_content",Json.Text(message,"reasoning")).Length,"refused",!string.IsNullOrWhiteSpace(Json.Text(message,"refusal")),"toolCalls",Json.Array(Json.Get(message,"tool_calls")).Length,"promptTokens",Json.Int(usage,"prompt_tokens"),"completionTokens",Json.Int(usage,"completion_tokens"));
                Log.WriteText("response.content",trace,"API 返回的原始正文（校验前，不包含思考过程）",Json.Text(message,"content"));
                if(!string.IsNullOrWhiteSpace(Json.Text(message,"refusal"))) Log.WriteText("response.refusal",trace,"API 返回的 refusal 字段",Json.Text(message,"refusal"));
                if(Json.Get(data,"error")!=null) Log.Write("response.error",trace,Json.Codec().Serialize(Json.Get(data,"error")));
            }
            catch(Exception ex) { Log.Write("response.invalid",trace,"API 返回内容不是预期的 JSON 结构","bytes",Encoding.UTF8.GetByteCount(raw),"errorType",ex.GetType().Name); }
        }
        public static Uri GetModelsEndpoint(string value)
        {
            string endpoint=GetEndpoint(value).AbsoluteUri;
            return new Uri(endpoint.Substring(0,endpoint.Length-"/chat/completions".Length)+"/models");
        }
        public async Task<string[]> ListModelsAsync(AiSettings settings,CancellationToken cancellation)
        {
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using(var message=new HttpRequestMessage(HttpMethod.Get,GetModelsEndpoint(settings.Endpoint)))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(2,Math.Min(180,settings.TimeoutSeconds))));
                if(!string.IsNullOrWhiteSpace(settings.ApiKey)) message.Headers.Authorization=new AuthenticationHeaderValue("Bearer",settings.ApiKey.Trim());
                using(var response=await http.SendAsync(message,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false))
                {
                    if(!response.IsSuccessStatusCode) throw new InvalidOperationException("模型列表返回 HTTP "+(int)response.StatusCode+"；可手动填写模型名称。");
                    const int limit=4*1024*1024;
                    if(response.Content.Headers.ContentLength>limit) throw new FormatException("模型列表过大。");
                    using(var stream=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using(var buffer=new MemoryStream())
                    {
                        byte[] bytes=new byte[8192]; int read;
                        while((read=await stream.ReadAsync(bytes,0,bytes.Length,timeout.Token).ConfigureAwait(false))>0)
                        { if(buffer.Length+read>limit) throw new FormatException("模型列表过大。"); buffer.Write(bytes,0,read); }
                        var data=Json.Object(Json.Codec().DeserializeObject(Encoding.UTF8.GetString(buffer.ToArray())));
                        var models=Json.Array(Json.Get(data,"data")).Select(x=>Json.Text(Json.Object(x),"id")).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
                        if(models.Length==0) throw new FormatException("接口未返回模型列表；可手动填写模型名称。");
                        return models;
                    }
                }
            }
        }
        internal static string Parse(string raw)
        {
            var data=Json.Object(Json.Codec().DeserializeObject(raw));
            var choices=Json.Array(Json.Get(data,"choices"));
            if(choices.Length==0) throw new FormatException("API 没有返回口上。");
            var choice=Json.Object(choices[0]); var message=Json.Object(Json.Get(choice,"message"));
            string finish=Json.Text(choice,"finish_reason");
            if(finish=="length") throw new FormatException("API 达到输出 token 上限，未得到完整口上。请提高最大输出 tokens；思考模型还会消耗推理额度。");
            if(finish!="stop") throw new FormatException("API 回复未完整结束（"+finish+"）。");
            if(!string.IsNullOrWhiteSpace(Json.Text(message,"refusal")) || Json.Array(Json.Get(message,"tool_calls")).Length>0) throw new FormatException("API 未返回可用口上。");
            var content=Json.Get(message,"content") as string;
            if(string.IsNullOrWhiteSpace(content)) throw new FormatException("API 返回空口上"+(!string.IsNullOrWhiteSpace(Json.Text(message,"reasoning_content",Json.Text(message,"reasoning"))) ? "（只有思考内容，没有正文）" : "")+"。");
            content=content.Trim();
            if(content.Length>8000 || content.IndexOf('\0')>=0 || content.Contains("```") || Regex.IsMatch(content,@"(?im)^\s*(FLAG|CFLAG|BASE|ABL|EXP|TFLAG|MONEY)\s*[:=]"))
                throw new FormatException("API 回复不符合纯文本口上格式。");
            return content;
        }
        public void Dispose() { http.Dispose(); }
    }
}
