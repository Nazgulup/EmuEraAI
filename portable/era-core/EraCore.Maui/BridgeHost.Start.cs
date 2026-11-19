namespace EraCore.Maui;

internal sealed partial class BridgeHost
{
    // Both ready and MainPage's game-picker/reload path enter here. A rebuilt host
    // never receives another ready message, so AI setup must belong to Start itself.
    internal void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _readyReceived = true;
        _ = StartWithAiAsync();
    }

    private async Task StartWithAiAsync()
    {
        try
        {
            await InitializeAiAsync();
            if (_disposed) return;
            ConfigureAiGameStyle();
            _aiController?.Log.Write("bridge.session-start", null, "AI 已就绪，开始游戏会话", "initialized", _aiController != null);
            PushLayoutMessage();
            PushConfigMessage();
            await Task.Run(StartHostedSessionAsync);
        }
        catch (Exception ex)
        {
            if (!_disposed) ShowFatalError(ex);
        }
    }
}
