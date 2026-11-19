using System.Text;
using System.Text.Json;
using MinorShift.Emuera.AI;
using MinorShift.Emuera.Output;
using AiJson = MinorShift.Emuera.AI.Json;

namespace EraCore.Maui;

internal sealed partial class BridgeHost
{
    private const string AiSettingsKey = "eraai.settings.v1";
    private AiController? _aiController;
    private Task? _aiInitialization;
    private DialogueFrame? _aiPendingFrame;
    private DialogueFrame? _aiOriginalFrame, _aiSupplementFrame;
    private long _aiPendingRevision;
    private bool _aiStyleConfigured;
    private DialogueCaptureInfo? _aiCapture;
    private CancellationTokenSource? _translationWork;
    private int _translationVersion;
    private StoryFunctionDocument? _storyDocument;

    private async Task BuildStoryDocumentAsync()
    {
        var document=_storyDocument; if(document==null || _disposed) return;
        await document.Start(); if(_disposed) return;
        _aiController?.Log.Write("story.document",null,document.Reason,"state",document.State,"files",document.Done);
    }
    private void PushStoryDocumentProgress()
    {
        var document=_storyDocument; if(document==null) return;
        SendAi(new Dictionary<string,object>{{"type","aiStoryDocumentProgress"},{"state",document.State},{"reason",document.Reason},{"done",document.Done},{"total",document.Total}});
    }

    private void CancelTranslation() { _translationWork?.Cancel(); _translationWork = null; }

    private async Task TranslateAsync(JsonElement message)
    {
        string requestId = message.GetProperty("requestId").GetString() ?? "";
        if (requestId.Length > 80) return;
        CancelTranslation();
        using var cancel = new CancellationTokenSource(); _translationWork = cancel;
        var settings = _aiController!.Settings.Copy();
        settings.TraceId = "tr-" + Guid.NewGuid().ToString("N")[..10];
        var log = _aiController.Log;
        try
        {
            if (!settings.TranslationEnabled || !_started || _disposed) throw new InvalidOperationException("自动翻译未启用。");
            cancel.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds,2,180)));
            var texts = message.GetProperty("texts").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            log.Write("translation.start",settings.TraceId,"翻译当前显示文字","language",settings.TranslationLanguage,"items",texts.Length);
            var client = CreateAiClient();
            try
            {
                if(client is AiClient transport) transport.Log = log;
                var addresses = new AddressTerms((_aiOriginalFrame ?? _aiSupplementFrame)?.Addresses);
                var translated = await Translation.TranslateAsync(client,settings,texts,cancel.Token,addresses);
                if(_disposed || cancel.IsCancellationRequested) return;
                SendAi(new Dictionary<string,object>{{"type","aiTranslation"},{"requestId",requestId},{"translations",translated},{"error",""}});
                log.Write("translation.success",settings.TraceId,"翻译完成，原文与游戏输入值保留","items",translated.Length);
            }
            finally { (client as IDisposable)?.Dispose(); }
        }
        catch(Exception ex)
        {
            log.Write("translation.failed",settings.TraceId,ex.ToString(),"fallback","source-text");
            if(!_disposed) SendAi(new Dictionary<string,object>{{"type","aiTranslation"},{"requestId",requestId},{"translations",Array.Empty<string>()},{"error",ex is FormatException || ex is InvalidOperationException ? ex.Message.Replace("口上","译文").Replace("最大输出 tokens","翻译输出 tokens") : "翻译未完成，继续显示原文；可重试。"}});
        }
        finally { if(ReferenceEquals(_translationWork,cancel)) _translationWork = null; }
    }

    private Task InitializeAiAsync() => _aiInitialization ??= LoadAiAsync();

    private async Task LoadAiAsync()
    {
        var settings = new AiSettings();
        string warning = "";
        try
        {
            string? raw = await SecureStorage.Default.GetAsync(AiSettingsKey);
            if (!string.IsNullOrWhiteSpace(raw)) settings = AiSettings.Parse(raw);
        }
        catch { settings.Enabled=false; warning = "AI 设置无法读取，已关闭 AI 并使用原口上。"; }
        if (_disposed) return;
        _aiController = new AiController(settings, CreateAiClient(), new AiLog(Path.Combine(FileSystem.AppDataDirectory, "ai", "logs", "generation.log")));
        _aiController.Changed += PushAiState;
        ConfigureAiGameStyle();
        ApplyAiFrame();
        PushAiSettings();
        if (warning.Length > 0) SendAi("aiNotice", "text", warning);
    }

    private void ConfigureAiGameStyle()
    {
        // A host rebuilt for the selection screen may still carry the old root.
        // Only a started game can sample it or send a style request.
        if(!_started || _disposed || _aiStyleConfigured || _aiController == null || _gameTree is not { } tree) return;
        _aiStyleConfigured = true;
        var policy=new StoryPolicy(
            () => StoryPolicy.Enumerate(dir=>tree.ListDirectories(dir),dir=>tree.ListFiles(dir)),
            path => { using var stream=tree.OpenRead(path) ?? throw new FileNotFoundException("脚本不可读取",path); using var buffer=new MemoryStream(); stream.CopyTo(buffer); return string.Join("\n",MinorShift.Emuera.Runtime.Utils.EncodingHandler.ReadAllLinesFromBytes(buffer.ToArray())); },
            Path.Combine(FileSystem.AppDataDirectory,"ai","story-rules",StoryPolicy.Digest(tree.Root)+".json"));
        _aiController.ConfigureStoryPolicy(policy);
        _storyDocument=new StoryFunctionDocument(policy,Path.Combine(FileSystem.AppDataDirectory,"ai","story-documents",StoryPolicy.Digest(tree.Root)+".json"),PushStoryDocumentProgress);
        _ = BuildStoryDocumentAsync();
        _aiController.ConfigureGameStyle(
            () => GameStyleCorpus.Read(tree.Name,GameStyleCorpus.Enumerate(dir => tree.ListDirectories(dir),dir => tree.ListFiles(dir)),
                path => tree.OpenRead(path), bytes => string.Join("\n",MinorShift.Emuera.Runtime.Utils.EncodingHandler.ReadAllLinesFromBytes(bytes))),
            Path.Combine(FileSystem.AppDataDirectory,"ai","styles"));
    }

    private void OnTurnFromGame(Turn turn)
    {
        _dispatcher.Dispatch(() => { if (_disposed) return; ObserveAiTurn(turn); _jsBridge.PostTurn(turn.Wire); });
    }

    private void ObserveAiTurn(Turn turn)
    {
        if (_disposed) return;
        _aiOriginalFrame = turn.Dialogue;
        _aiSupplementFrame = turn.Supplement;
        _aiPendingRevision = turn.Record.revision;
        _aiCapture = turn.Capture;
        _aiController?.Log.Write(turn.Dialogue == null ? "capture.empty" : "capture.ready", null,
            turn.Dialogue == null ? _aiCapture?.Reason ?? "本回合没有口上观察信息" : "捕获到口上，备用原文在后台保留",
            "hook", turn.Dialogue?.Hook ?? "", "displayRevision", _aiPendingRevision,
            "printedLines", _aiCapture?.PrintedLines ?? 0, "outsideDialogueScope", _aiCapture?.OutsideScope ?? 0,
            "invalidTarget", _aiCapture?.InvalidTarget ?? 0, "buttonLines", _aiCapture?.ButtonLines ?? 0,
            "observerErrors", _aiCapture?.ObserverErrors ?? 0, "observerErrorType", _aiCapture?.ErrorType ?? "");
        ApplyAiFrame();
    }

    private void ApplyAiFrame()
    {
        var f = _aiOriginalFrame ?? (_aiController?.Settings.GenerateWithoutOriginal == true ? _aiSupplementFrame : null);
        _aiPendingFrame = f;
        if (f?.InsertBeforeLine != null)
            _aiController?.Log.Write("capture.supplement", null, "没有原口上；使用角色与当前选项的只读快照补写", "eventId", f.EventId, "insertBeforeLine", f.InsertBeforeLine.Value, "displayRevision", _aiPendingRevision);
        _aiController?.SetEvent(f == null ? null : new DialogueSnapshot(f.EventId, f.Character, f.User, f.Facts, f.Original, f.Hook, f.Addresses, f.StoryContext, f.StoryTurn));
    }

    private void PushAiState()
    {
        var c = _aiController;
        if (c == null || _disposed) return;
        SendAi(new Dictionary<string, object> {
            {"type", "aiState"}, {"text", c.Text}, {"status", c.Current == null && _aiCapture != null ? "未捕获口上：" + _aiCapture.Reason : c.Status}, {"busy", c.Busy},
            {"hasEvent", c.Current != null}, {"character", c.Current?.Character ?? ""}, {"hasOriginal", !string.IsNullOrEmpty(c.Current?.Original)},
            {"mode", c.DisplayMode}, {"enabled", c.Settings.Enabled}, {"supplementEnabled", c.Settings.GenerateWithoutOriginal}, {"eventId", c.Current?.EventId ?? ""},
            {"canGenerate",c.CanGenerate},{"storyReady",c.StoryReady},{"storyKind",c.Story.Kind},{"storyReason",c.Story.Reason},{"storyLocation",c.Story.Location},{"canSetFunctionRule",c.StoryReady && c.Story.RuleKey.Length>0},{"storyTurn",c.Current?.StoryTurn ?? 0},
            {"lineIndices", _aiPendingFrame?.LineIndices ?? []}, {"insertBeforeLine", _aiPendingFrame?.InsertBeforeLine ?? -1}, {"displayRevision", _aiPendingRevision}
        });
    }

    private void PushAiSettings()
    {
        if (_aiController == null) return;
        SendAi(new Dictionary<string, object> { {"type", "aiSettings"}, {"settings", _aiController.Settings.ToDictionary(false)}, {"hasKey", _aiController.Settings.ApiKey.Length > 0}, {"translationVersion",_translationVersion} });
        PushAiState();
    }

    private void SendAi(string type, string key, string value) => SendAi(new Dictionary<string, object> { {"type", type}, {key, value} });
    private void SendAi(Dictionary<string, object> message)
    {
        if (_disposed) return;
        string raw = AiJson.Codec().Serialize(message);
        if (!_dispatcher.IsDispatchRequired) _jsBridge.PostMessage(raw);
        else _dispatcher.Dispatch(() => { if (!_disposed) _jsBridge.PostMessage(raw); });
    }

    // Whitelist of dialogue-only actions. No interpreter, input queue, variable or save writer calls.
    private async Task HandleAiMessageAsync(string raw)
    {
        try
        {
            await InitializeAiAsync();
            var c = _aiController;
            if (c == null || _disposed) return;
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string? type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "aiGet": PushAiSettings(); break;
                case "aiTranslate": await TranslateAsync(root); break;
                case "aiGenerate": await c.GenerateAsync(true); break;
                case "aiStoryDocument":
                    if(_storyDocument==null) throw new InvalidOperationException("请先加载游戏");
                    _ = BuildStoryDocumentAsync();
                    SendAi(_storyDocument.Page(root.TryGetProperty("query",out var query) ? query.GetString() ?? "" : "",root.TryGetProperty("page",out var page) ? page.GetInt32() : 0,root.TryGetProperty("kind",out var kind) ? kind.GetString() ?? "" : "")); break;
                case "aiStoryDocumentSave":
                    if(_storyDocument==null) throw new InvalidOperationException("函数清单尚未就绪");
                    if(!root.TryGetProperty("documentId",out var documentId) || documentId.GetString()!=_storyDocument.Id) throw new InvalidOperationException("游戏或函数清单已变化，请重新打开清单");
                    var changes=AiJson.Array(AiJson.Codec().DeserializeObject(root.GetProperty("edits").GetRawText()));
                    await Task.Run(()=>_storyDocument.Update(changes));
                    if(_disposed) return;
                    c.Log.Write("story.document-edit",null,"已保存函数选择；允许的当前段落优先 AI，不重新执行游戏","changes",changes.Length);
                    await c.RefreshStoryRulesAsync();
                    SendAi("aiStoryDocumentSaved","text","已保存本游戏函数选择；允许的当前段落会优先生成 AI 口上。"); break;
                case "aiStoryDocumentExport":
                    if(_storyDocument==null) throw new InvalidOperationException("函数清单尚未就绪");
                    string documentText=await Task.Run(()=>_storyDocument.Export());
                    string documentPath=Path.Combine(FileSystem.CacheDirectory,"era-function-switches.json");
                    await File.WriteAllTextAsync(documentPath,documentText);
                    await Share.Default.RequestAsync(new ShareFileRequest { Title="导出可编辑函数清单",File=new ShareFile(documentPath,"application/json") }); break;
                case "aiStoryRule":
                    if(c.Current==null || !root.TryGetProperty("eventId",out var ruleEvent) || ruleEvent.GetString()!=c.Current.EventId || !root.TryGetProperty("storyTurn",out var ruleTurn) || ruleTurn.GetInt64()!=c.Current.StoryTurn)
                    { c.Log.Write("story.command-rejected",null,"段落已经变化，忽略旧界面的规则请求"); throw new InvalidOperationException("段落已经变化，请核对当前判断后再操作。"); }
                    await c.SetStoryRuleAsync(root.GetProperty("action").GetString() ?? "",root.GetProperty("scope").GetString() ?? ""); break;
                case "aiStoryImport":
                    var ruleFile=await FilePicker.Default.PickAsync(new PickOptions { PickerTitle="导入本游戏函数开关" });
                    if(ruleFile!=null) { using var ruleStream=await ruleFile.OpenReadAsync(); string ruleText=Preset.ReadFile(ruleStream); await c.ImportStoryRulesAsync(ruleText); SendAi("aiNotice","text","已导入本游戏函数开关；关闭记录按文件与函数保存。"); }
                    break;
                case "aiStoryExport":
                    string storyPath=Path.Combine(FileSystem.CacheDirectory,"era-story-rules.json"); File.WriteAllText(storyPath,c.ExportStoryRules());
                    await Share.Default.RequestAsync(new ShareFileRequest { Title="导出本游戏函数开关",File=new ShareFile(storyPath,"application/json") }); break;
                case "aiRestore": c.RestoreOriginal(); break;
                case "aiLog": SendAi("aiLog", "text", c.Log.ReadRecent()); break;
                case "aiStyle": SendAi("aiStyle", "text", c.StyleReport); break;
                case "aiProbe":
                    SendAi("aiNotice", "text", await c.ProbeAsync());
                    SendAi("aiLog", "text", c.Log.ReadRecent());
                    break;
                case "aiExportLog":
                    string logPath = Path.Combine(FileSystem.CacheDirectory, "ai-generation.log");
                    c.Log.Export(logPath);
                    await Share.Default.RequestAsync(new ShareFileRequest { Title = "导出 AI 生成日志", File = new ShareFile(logPath, "text/plain") });
                    break;
                case "aiModels":
                    string requestId = root.GetProperty("requestId").GetString() ?? "";
                    try
                    {
                        var connection = AiSettings.Parse(root.GetProperty("settings").GetRawText());
                        if (connection.ApiKey.Length == 0 && !(root.TryGetProperty("clearKey", out var clearModelKey) && clearModelKey.GetBoolean())) connection.ApiKey = c.Settings.ApiKey;
                        using var client = new AiClient();
                        string[] models = await client.ListModelsAsync(connection, CancellationToken.None);
                        SendAi(new Dictionary<string, object> { {"type", "aiModels"}, {"requestId", requestId}, {"models", models}, {"error", ""} });
                    }
                    catch (Exception ex)
                    {
                        string error = ex is FormatException || ex is InvalidOperationException ? ex.Message : "无法读取模型列表，请检查连接或手动填写模型名称。";
                        SendAi(new Dictionary<string, object> { {"type", "aiModels"}, {"requestId", requestId}, {"models", Array.Empty<string>()}, {"error", error} });
                    }
                    break;
                case "aiSave":
                    var settings = AiSettings.Parse(root.GetProperty("settings").GetRawText());
                    if (settings.ApiKey.Length == 0 && !(root.TryGetProperty("clearKey", out var clear) && clear.GetBoolean())) settings.ApiKey = c.Settings.ApiKey;
                    _ = new Preset(settings.PresetJson).OutputTokens(settings);
                    if (settings.Enabled || settings.TranslationEnabled)
                    {
                        _ = AiClient.GetEndpoint(settings.Endpoint);
                        if (string.IsNullOrWhiteSpace(settings.Model)) throw new FormatException("请填写模型名称。");
                    }
                    await SecureStorage.Default.SetAsync(AiSettingsKey, AiJson.Codec().Serialize(settings.ToDictionary(true)));
                    if (_disposed) return;
                    CancelTranslation(); _translationVersion++;
                    c.SetSettings(settings);
                    ApplyAiFrame();
                    PushAiSettings();
                    SendAi("aiNotice", "text", "设置已保存。可点击重新生成，或在游戏中触发下一段口上。");
                    break;
                case "aiCheck":
                    var preset = new Preset(root.GetProperty("preset").GetString());
                    SendAi("aiNotice", "text", preset.Warnings.Count == 0 ? "预设结构检查通过。可查看实际请求确认。" : string.Join("\n", preset.Warnings.Take(12)));
                    break;
                case "aiPreview":
                    if (c.Current != null) SendAi("aiPreview", "text", AiJson.Codec().Serialize(c.PreviewRequest()));
                    else SendAi("aiNotice", "text", "请先在游戏中触发一段口上。");
                    break;
                case "aiImport":
                    var picked = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "导入酒馆 Chat Completion JSON" });
                    if (picked == null) return;
                    using (var stream = await picked.OpenReadAsync())
                    {
                        string contents = await Task.Run(() => { string value = Preset.ReadFile(stream); _ = new Preset(value); return value; });
                        SendAi("aiPresetImported", "text", contents);
                    }
                    break;
                case "aiExport":
                    string value = root.GetProperty("preset").GetString() ?? "{}";
                    _ = new Preset(value);
                    string path = Path.Combine(FileSystem.CacheDirectory, "era-dialogue-preset.json");
                    await File.WriteAllTextAsync(path, value.Length == 0 ? "{}" : value, Encoding.UTF8);
                    await Share.Default.RequestAsync(new ShareFileRequest { Title = "导出酒馆预设", File = new ShareFile(path, "application/json") });
                    break;
            }
        }
        catch (Exception ex)
        {
            SendAi("aiNotice", "text", ex is FormatException ? ex.Message : "操作未完成，请检查设置或文件。原游戏和原口上保持可用。");
        }
    }
}
