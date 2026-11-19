using System.Collections.Concurrent;
using System.Diagnostics;
using MinorShift.Emuera.AI;
using MinorShift.Emuera.Files;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Sessions;

// Compile the production Start and AI bridge partials, replacing only platform
// storage/dispatcher/share services and session composition (no Android device).
namespace EraCore.Maui
{
    internal sealed partial class BridgeHost
    {
        private readonly TestDispatcher _dispatcher = new();
        private readonly TestJsBridge _jsBridge = new();
        private readonly GameTree? _gameTree;
        private readonly IAiClient _client;
        private bool _started, _disposed, _readyReceived;
        private Task? _pump;
        internal readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CoreSession? Session;
        internal int SessionStarts;
        internal bool Ready => _readyReceived;
        internal AiController Controller => _aiController ?? throw new Exception("AI was not initialized");
        internal StoryFunctionDocument Document => _storyDocument ?? throw new Exception("Document not initialized");
        internal TestJsBridge Js => _jsBridge;
        internal BridgeHost(string root, IAiClient client) { _gameTree = new(root, new FileSystemGameDirAccessor()); _client = client; }
        private IAiClient CreateAiClient() => _client;
        private void PushLayoutMessage() { }
        private void PushConfigMessage() { }
        private void ShowFatalError(Exception ex) => Started.TrySetException(ex);
        private async Task StartHostedSessionAsync()
        {
            SessionStarts++;
            Session = new CoreSession(new CoreSessionOptions(_gameTree!.Root, new FileSystemGameDirAccessor()) { FullDiffOnFirstTurn = true });
            var result = await Session.StartAsync();
            if (!result.Succeeded) throw new Exception(result.ErrorCode + ": " + result.ErrorMessage);
            var subscription = Session.Output.SubscribeTurns();
            _pump = Task.Run(async () => {
                try { await foreach (var turn in subscription.Turns!.ReadAllAsync()) OnTurnFromGame(turn); }
                finally { subscription.Dispose(); }
            });
            Started.TrySetResult(true);
        }
        internal Task Command(string type) => HandleAiMessageAsync("{\"type\":\"" + type + "\"}");
        internal Task Command(Dictionary<string,object> message) => HandleAiMessageAsync(Json.Codec().Serialize(message));
        internal Task Translate(string id, params string[] texts) => HandleAiMessageAsync(Json.Codec().Serialize(new Dictionary<string,object> { {"type","aiTranslate"}, {"requestId",id}, {"texts",texts} }));
        internal Task SaveSettings(AiSettings settings) => HandleAiMessageAsync(Json.Codec().Serialize(new Dictionary<string,object> { {"type","aiSave"}, {"settings",settings.ToDictionary(true)} }));
        internal async Task Stop()
        {
            _disposed = true; CancelTranslation(); _storyDocument?.Dispose(); _aiController?.Dispose();
            if (Session != null) { await Session.StopAsync(); Session.Dispose(); }
            if (_pump != null) await _pump;
        }
    }
    internal sealed class TestDispatcher
    {
        public bool IsDispatchRequired => false;
        public void Dispatch(Action action) => action();
    }
    internal sealed class TestJsBridge
    {
        public readonly ConcurrentQueue<string> Messages = new(), Turns = new();
        public readonly ConcurrentQueue<Dictionary<string, object>> Events = new();
        public void PostMessage(string value) { Messages.Enqueue(value); Events.Enqueue(new() { {"kind","message"}, {"data",Json.Codec().DeserializeObject(value)} }); }
        public void PostTurn(string value) { Turns.Enqueue(value); Events.Enqueue(new() { {"kind","turn"}, {"data",Json.Codec().DeserializeObject(value)} }); }
    }
    internal static class FileSystem
    {
        public static string AppDataDirectory { get; set; } = "";
        public static string CacheDirectory => AppDataDirectory;
        public static string PackageDirectory { get; set; } = "";
        public static Task<Stream> OpenAppPackageFileAsync(string name) => Task.FromResult<Stream>(File.OpenRead(Path.Combine(PackageDirectory,name)));
    }
    internal sealed class SecureStorage
    {
        public static SecureStorage Default { get; } = new();
        public Task<string?> Read = Task.FromResult<string?>(null);
        public int Reads;
        public Task<string?> GetAsync(string key) { Reads++; return Read; }
        public Task SetAsync(string key, string value) { Read = Task.FromResult<string?>(value); return Task.CompletedTask; }
    }
    internal sealed class PickOptions { public string PickerTitle { get; set; } = ""; }
    internal sealed class FilePicker
    {
        public static FilePicker Default { get; } = new();
        public Task<TestFile?> PickAsync(PickOptions options) => throw new NotSupportedException();
    }
    internal sealed class TestFile { public Task<Stream> OpenReadAsync() => throw new NotSupportedException(); }
    internal sealed class ShareFile { public ShareFile(string path, string type) { } }
    internal sealed class ShareFileRequest { public string Title { get; set; } = ""; public ShareFile? File { get; set; } }
    internal sealed class Share
    {
        public static Share Default { get; } = new();
        public Task RequestAsync(ShareFileRequest request) => throw new NotSupportedException();
    }
}

internal static class HostAiTests
{
    static int count;
    static void Check(bool value, string text) { if (!value) throw new Exception("FAIL: " + text); count++; Console.WriteLine("PASS: " + text); }
    sealed class Client : IAiClient
    {
        public int Calls;
        public readonly ConcurrentQueue<int> Budgets = new();
        public Func<Task<string>> Reply = () => Task.FromResult("林澄收起地图，准备按照商定的路线出发。");
        public Task<string> GenerateAsync(AiSettings settings, Dictionary<string, object> request, CancellationToken ct) { Budgets.Enqueue(Json.Int(request,"max_tokens")); Interlocked.Increment(ref Calls); return Reply(); }
    }
    static async Task Until(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done()) { if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("host test condition"); await Task.Delay(20); }
    }
    public static async Task Run(string root)
    {
        string game = Path.Combine(root, "portable", "Tests", "host-game"); Directory.CreateDirectory(game);
        foreach (string file in Directory.GetFiles(Path.Combine(root, "demo"), "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(game, Path.GetRelativePath(Path.Combine(root, "demo"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file, dest, true);
        }
        string scriptPath = Path.Combine(game,"ERB","DEMO.ERB");
        string script = File.ReadAllText(scriptPath).Replace("CASE 9", "CASE 6\n    CALL AI_DIALOGUE_INVALID_TARGET\nCASE 7\n    PRINTL 普通菜单文字，不应触发口上。\nCASE 9");
        File.WriteAllText(scriptPath, script + "\n@AI_DIALOGUE_INVALID_TARGET\nTARGET = -1\nPRINTL 中性诊断文字\nTARGET = 1\nRETURN\n");
        string output = Path.Combine(root, "logs", "android-ai-host"); Directory.CreateDirectory(output);
        EraCore.Maui.FileSystem.AppDataDirectory = output;
        var settings = new AiSettings { ProtectStory=false, Enabled = true, AutoGenerate = true, UseGameStyle = false, Model = "neutral-test", OutputTokens = 32768,
            UsePresetOutputTokens = true, PresetJson = "{\"prompts\":[],\"openai_max_tokens\":65536,\"openai_max_context\":131072}" };
        string saved = Json.Codec().Serialize(settings.ToDictionary(true));
        var store = EraCore.Maui.SecureStorage.Default;
        var storage = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Read = storage.Task; store.Reads = 0;
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { Reply = () => response.Task };
        var host = new EraCore.Maui.BridgeHost(game, client);
        try
        {
            host.Start(); host.Start();
            Check(host.SessionStarts == 0 && store.Reads == 1, "game-picker Start waits for saved AI settings; repeated Start is idempotent");
            storage.SetResult(saved);
            await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Check(host.Ready && host.SessionStarts == 1 && host.Controller.Settings.Enabled, "rebuilt host starts with AI enabled without another ready or aiGet message");
            var session = host.Session!;
            var initial = Json.Codec().DeserializeObject(System.Text.Json.JsonSerializer.Serialize(session.Output.Snapshot));
            session.Input.Submit(new InputFrame(Value: "5"));
            await Until(() => client.Calls == 1);
            await Until(() => !host.Js.Turns.IsEmpty);
            Check(host.Controller.Current?.Hook == "PRITRAIN_MESSAGE" && host.Controller.Busy && host.Controller.Text == "", "real lazy-loaded session captures nested opening and automatically requests AI before displaying fallback");
            Check(client.Budgets.Single()==65536, "selected high-output preset reaches automatic opening generation without the old 8192 rejection");
            var pending = host.Js.Messages.Select(x => Json.Object(Json.Codec().DeserializeObject(x))).Last(x => Json.Text(x,"type") == "aiState" && Json.Text(x,"mode") == "pending");
            Check(Json.Text(pending,"eventId").Length > 0 && pending["lineIndices"] is object[] positions && positions.Length > 0 && pending.ContainsKey("displayRevision"), "production bridge delivers event, presentation positions and revision to the game page");
            string before = System.Text.Json.JsonSerializer.Serialize(session.Output.Snapshot);
            response.SetResult("林澄收起地图，准备按照商定的路线出发。");
            await Until(() => !host.Controller.Busy && host.Controller.DisplayMode == "ai");
            Check(host.Js.Messages.Any(x => x.Contains("林澄收起地图") && x.Contains("\"mode\":\"ai\"")), "AI reply is emitted through the production bridge for main-transcript rendering");
            client.Reply = () => Task.FromResult("出发前，林澄再次确认了地图上的路线。");
            await host.Command("aiGenerate"); await host.Command("aiGenerate");
            Check(client.Calls == 3 && System.Text.Json.JsonSerializer.Serialize(session.Output.Snapshot) == before, "regeneration sends no interpreter input and preserves the full game display snapshot");
            client.Reply = () => throw new HttpRequestException("offline test");
            await host.Command("aiGenerate");
            Check(host.Controller.DisplayMode == "original" && host.Controller.Text == host.Controller.Current!.Original, "host-side failed generation restores the cached original");
            client.Reply = () => Task.FromResult("林澄合上书，向旅人点头致意。");
            session.Input.Submit(new InputFrame(Value: "1"));
            await Until(() => client.Calls == 5 && !host.Controller.Busy);
            Check(host.Controller.Current?.Hook == "KOJO_DEMO_TEXT" && host.Controller.DisplayMode == "ai", "normal dialogue is also captured through the hosted session/output hub");
            var log = host.Controller.Log.ReadRecent();
            Check(log.Contains("bridge.session-start") && log.Contains("capture.ready") && log.Contains("request.success") && log.Contains("printedLines"), "diagnostic log covers initialization, capture and generation instead of only an OK probe");
            await File.WriteAllTextAsync(Path.Combine(output,"bridge-replay.json"), Json.Codec().Serialize(new Dictionary<string, object> {
                {"messages",host.Js.Messages.Select(x=>Json.Codec().DeserializeObject(x)).ToArray()},
                {"turns",host.Js.Turns.Select(x=>Json.Codec().DeserializeObject(x)).ToArray()},
                {"initial",initial}, {"events",host.Js.Events.ToArray()}
            }));
            session.Input.Submit(new InputFrame(Value: "6"));
            await Until(() => host.Controller.Current == null);
            Check(host.Js.Messages.Any(x => x.Contains("TARGET 无效")) && client.Calls == 5, "invalid TARGET is diagnosed without guessing a character or generating dialogue");
            int turns = host.Js.Turns.Count;
            session.Input.Submit(new InputFrame(Value: "7"));
            await Until(() => host.Js.Turns.Count > turns);
            Check(host.Js.Messages.Any(x => x.Contains("当前文本不在受支持的口上函数内")) && client.Calls == 5, "ordinary menu output reports missing dialogue scope and never triggers AI");
        }
        finally { await host.Stop(); }
        var replacement = new EraCore.Maui.BridgeHost(game, new Client());
        try { replacement.Start(); await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(60)); Check(store.Reads == 2 && replacement.Controller.Settings.Enabled, "returning to selection and choosing again reloads AI settings for the next host"); }
        finally { await replacement.Stop(); }
        storage = new(TaskCreationOptions.RunContinuationsAsynchronously); store.Read = storage.Task;
        var closed = new EraCore.Maui.BridgeHost(game, new Client());
        closed.Start(); await closed.Stop(); storage.SetResult(saved);
        await Task.Delay(50);
        Check(closed.SessionStarts == 0, "closing during settings load cannot start an orphan game session");
        Console.WriteLine("ALL HOST CHECKS PASSED: " + count);
    }
}
