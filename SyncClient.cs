using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace TangyTV;

public sealed class SyncClient : IDisposable
{
    private readonly Config config;
    private readonly IPluginLog log;
    private readonly IChatGui chat;

    private ClientWebSocket? ws;
    private CancellationTokenSource? cts;
    private Task? loopTask;
    private Task? statsTask;

    public string ConnectionStatus { get; private set; } = "Disconnected";
    public string? RoomCode { get; private set; }

    public bool IsHost { get; private set; } = false;

    public TvState State { get; } = new();
    public string DraftUrl = "";
    public string DraftRoom = "";

    public string? PendingJoinUrl { get; private set; }
    public double PendingJoinStartSeconds { get; private set; }
    public bool JoinConsentPending => !string.IsNullOrWhiteSpace(PendingJoinUrl);

    public long CurrentConnected { get; private set; }
    public long PeakConnected { get; private set; }

    private string? lastSeenMediaId = null;
    private long lastStateApplyMs = 0;

    public event Action? OnJoinConsentNeeded;
    public event Action? OnMediaPrepared;
    public event Action? OnPresenceChanged;
    public event Action? OnReadinessChanged;
    public event Action? OnLayoutChanged;

    public SyncClient(Config config, IPluginLog log, IChatGui chat)
    {
        this.config = config;
        this.log = log;
        this.chat = chat;
    }

    public async Task ConnectAsync()
    {
        if (ws != null && ws.State == WebSocketState.Open)
        {
            chat.Print("[TangyTV] Already connected.");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            chat.PrintError("[TangyTV] ServerUrl is empty (Settings tab).");
            return;
        }

        Disconnect();

        ws = new ClientWebSocket();
        cts = new CancellationTokenSource();

        try
        {
            ConnectionStatus = "Connecting...";
            await ws.ConnectAsync(new Uri(config.ServerUrl), cts.Token);
            ConnectionStatus = "Connected";
            chat.Print("[TangyTV] Connected.");

            loopTask = Task.Run(() => ReceiveLoop(ws, cts.Token));
            statsTask = Task.Run(() => StatsLoop(cts.Token));

            _ = SendAsync(new WsEnvelope { Type = "stats" });
        }
        catch (Exception ex)
        {
            ConnectionStatus = "Disconnected";
            chat.PrintError($"[TangyTV] Connect failed: {ex.Message}");
            Disconnect();
        }
    }

    public void Disconnect()
    {
        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
        cts = null;

        loopTask = null;
        statsTask = null;

        if (ws != null)
        {
            try { ws.Abort(); ws.Dispose(); } catch { }
            ws = null;
        }

        RoomCode = null;
        ConnectionStatus = "Disconnected";

        IsHost = false;

        PendingJoinUrl = null;
        PendingJoinStartSeconds = 0;

        lastSeenMediaId = null;
        lastStateApplyMs = 0;

        State.Url = null;
        State.MediaId = null;
        State.IsPlaying = false;
        State.PositionSeconds = 0;
        State.ServerTimeMs = 0;
        State.Presence = 0;
        State.ReadyCount = 0;
        State.ReadyTotal = 0;

        State.RoomWidthPx = config.DefaultRoomWidthPx;
        State.RoomHeightPx = config.DefaultRoomHeightPx;
    }

    public async Task HostAsync(string url)
    {
        await EnsureConnected();
        if (ws == null) return;

        if (!UrlHelpers.TryNormalizeStreamingUrl(url, out var provider, out var normalized, out var warning))
        {
            chat.PrintError("[TangyTV] " + warning);
            return;
        }

        DraftUrl = normalized;

        await SendAsync(new WsEnvelope
        {
            Type = "host",
            Url = normalized,
            WidthPx = config.DefaultRoomWidthPx,
            HeightPx = config.DefaultRoomHeightPx
        });
    }

    public async Task JoinAsync(string room)
    {
        await EnsureConnected();
        if (ws == null) return;

        DraftRoom = room;
        await SendAsync(new WsEnvelope { Type = "join", Room = room });
    }

    public async Task LeaveAsync()
    {
        if (ws == null) return;
        await SendAsync(new WsEnvelope { Type = "leave" });
        RoomCode = null;

        IsHost = false;

        PendingJoinUrl = null;
        PendingJoinStartSeconds = 0;

        lastSeenMediaId = null;
        State.MediaId = null;
        State.Url = null;
        State.IsPlaying = false;
        State.PositionSeconds = 0;
        State.ServerTimeMs = 0;
        State.ReadyCount = 0;
        State.ReadyTotal = 0;
    }

    public async Task SetPlayingAsync(bool playing)
    {
        if (ws == null) return;
        await SendAsync(new WsEnvelope { Type = "play", IsPlaying = playing });
    }

    public async Task SeekAsync(double seconds)
    {
        if (ws == null) return;
        await SendAsync(new WsEnvelope { Type = "seek", PositionSeconds = seconds });
    }

    public async Task PushUrlAsync(string url)
    {
        if (ws == null) return;

        if (!UrlHelpers.TryNormalizeStreamingUrl(url, out var provider, out var normalized, out var warning))
        {
            chat.PrintError("[TangyTV] " + warning);
            return;
        }

        DraftUrl = normalized;

        await SendAsync(new WsEnvelope { Type = "push_url", Url = normalized });
    }

    public async Task SendReadyAsync(string? mediaId)
    {
        if (ws == null) return;
        if (string.IsNullOrWhiteSpace(mediaId)) return;
        await SendAsync(new WsEnvelope { Type = "ready", MediaId = mediaId });
    }

    public async Task SetRoomLayoutAsync(int widthPx, int heightPx)
    {
        if (ws == null) return;

        widthPx = Math.Clamp(widthPx, 320, 3840);
        heightPx = Math.Clamp(heightPx, 180, 2160);

        await SendAsync(new WsEnvelope
        {
            Type = "layout",
            WidthPx = widthPx,
            HeightPx = heightPx
        });
    }

    public void AcknowledgeJoinConsentAccepted()
    {
        config.AcceptedThisSession = true;
        config.LastOpenedUrlThisSession = PendingJoinUrl;
        config.Save();

        PendingJoinUrl = null;
        PendingJoinStartSeconds = 0;
    }

    public void AcknowledgeJoinConsentDeclined()
    {
        PendingJoinUrl = null;
        PendingJoinStartSeconds = 0;
    }

    private async Task EnsureConnected()
    {
        if (ws == null || ws.State != WebSocketState.Open)
            await ConnectAsync();
    }

    private async Task SendAsync(WsEnvelope env)
    {
        if (ws == null) return;

        var json = JsonConvert.SerializeObject(env);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts?.Token ?? CancellationToken.None);
    }

    private async Task ReceiveLoop(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];

        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(buffer, token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                HandleMessage(sb.ToString());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.Error(ex, "ReceiveLoop error");
            chat.PrintError($"[TangyTV] Connection lost: {ex.Message}");
        }
        finally
        {
            ConnectionStatus = "Disconnected";
            try { socket.Dispose(); } catch { }
        }
    }

    private async Task StatsLoop(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (!token.IsCancellationRequested)
            {
                await timer.WaitForNextTickAsync(token);
                if (ws == null || ws.State != WebSocketState.Open) continue;

                await SendAsync(new WsEnvelope { Type = "stats" });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.Error(ex, "StatsLoop error");
        }
    }

    private void HandleMessage(string json)
    {
        WsEnvelope? env;
        try
        {
            env = JsonConvert.DeserializeObject<WsEnvelope>(json);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Bad JSON from server: {0}", json);
            return;
        }

        if (env == null) return;

        switch (env.Type)
        {
            case "ping":
                _ = SendAsync(new WsEnvelope { Type = "pong" });
                return;

            case "hosted":
                RoomCode = env.Room;
                IsHost = true;
                chat.Print($"[TangyTV] Hosting room: {RoomCode}");

                if (env.WidthPx != null) State.RoomWidthPx = env.WidthPx.Value;
                if (env.HeightPx != null) State.RoomHeightPx = env.HeightPx.Value;
                break;

            case "joined":
                RoomCode = env.Room;
                IsHost = false;
                chat.Print($"[TangyTV] Joined room: {RoomCode}");

                if (env.WidthPx != null) State.RoomWidthPx = env.WidthPx.Value;
                if (env.HeightPx != null) State.RoomHeightPx = env.HeightPx.Value;
                break;

            case "prepare":
                ApplyPrepare(env);
                break;

            case "state":
                ApplyState(env);
                break;

            case "presence":
                ApplyPresence(env);
                break;

            case "readiness":
                ApplyReadiness(env);
                break;

            case "stats":
                ApplyStats(env);
                break;

            case "layout":
                ApplyLayout(env);
                break;

            case "error":
                chat.PrintError($"[TangyTV] Server error: {env.Message}");
                break;
        }

        if (RoomCode != null && JoinConsentPending)
            OnJoinConsentNeeded?.Invoke();
    }

    private void ApplyPresence(WsEnvelope env)
    {
        if (env.Presence != null)
        {
            State.Presence = env.Presence.Value;
            OnPresenceChanged?.Invoke();
        }
    }

    private void ApplyReadiness(WsEnvelope env)
    {
        if (env.ReadyCount != null) State.ReadyCount = env.ReadyCount.Value;
        if (env.ReadyTotal != null) State.ReadyTotal = env.ReadyTotal.Value;
        if (env.Presence != null) State.Presence = env.Presence.Value;
        OnReadinessChanged?.Invoke();
    }

    private void ApplyPrepare(WsEnvelope env)
    {
        if (env.Url != null) State.Url = env.Url;
        if (env.MediaId != null) State.MediaId = env.MediaId;
        if (env.PositionSeconds != null) State.PositionSeconds = env.PositionSeconds.Value;
        if (env.ServerTimeMs != null) State.ServerTimeMs = env.ServerTimeMs.Value;

        if (env.WidthPx != null) State.RoomWidthPx = env.WidthPx.Value;
        if (env.HeightPx != null) State.RoomHeightPx = env.HeightPx.Value;

        State.IsPlaying = false;

        if (lastSeenMediaId == null)
        {
            lastSeenMediaId = State.MediaId;
            PendingJoinUrl = State.Url;
            PendingJoinStartSeconds = State.PositionSeconds;
            OnJoinConsentNeeded?.Invoke();
        }
        else
        {
            lastSeenMediaId = State.MediaId;
            OnMediaPrepared?.Invoke();
        }
    }

    private void ApplyLayout(WsEnvelope env)
    {
        var changed = false;
        if (env.WidthPx != null)
        {
            State.RoomWidthPx = env.WidthPx.Value;
            changed = true;
        }
        if (env.HeightPx != null)
        {
            State.RoomHeightPx = env.HeightPx.Value;
            changed = true;
        }

        if (changed)
            OnLayoutChanged?.Invoke();
    }

    private void ApplyStats(WsEnvelope env)
    {
        if (env.CurrentConnected != null) CurrentConnected = env.CurrentConnected.Value;
        if (env.PeakConnected != null) PeakConnected = env.PeakConnected.Value;

        if ((env.CurrentConnected == null || env.PeakConnected == null) && !string.IsNullOrWhiteSpace(env.Message))
        {
            var msg = env.Message!;
            TryParseStat(msg, "current", out var current);
            TryParseStat(msg, "peak", out var peak);
            if (current != null) CurrentConnected = current.Value;
            if (peak != null) PeakConnected = peak.Value;
        }
    }

    private static bool TryParseStat(string msg, string key, out long? value)
    {
        value = null;
        var idx = msg.IndexOf(key + "=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        idx += key.Length + 1;
        var end = idx;
        while (end < msg.Length && char.IsDigit(msg[end])) end++;
        if (end == idx) return false;
        if (long.TryParse(msg.Substring(idx, end - idx), out var v))
        {
            value = v;
            return true;
        }
        return false;
    }

    private void ApplyState(WsEnvelope env)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var isImportant = env.Url != null || env.IsPlaying != null;
        if (!isImportant && now - lastStateApplyMs < 50)
            return;
        lastStateApplyMs = now;

        if (env.Url != null) State.Url = env.Url;
        if (env.MediaId != null) State.MediaId = env.MediaId;
        if (env.IsPlaying != null) State.IsPlaying = env.IsPlaying.Value;
        if (env.PositionSeconds != null) State.PositionSeconds = env.PositionSeconds.Value;
        if (env.ServerTimeMs != null) State.ServerTimeMs = env.ServerTimeMs.Value;

        if (env.WidthPx != null) State.RoomWidthPx = env.WidthPx.Value;
        if (env.HeightPx != null) State.RoomHeightPx = env.HeightPx.Value;

        if (lastSeenMediaId == null && !string.IsNullOrWhiteSpace(State.MediaId) && !string.IsNullOrWhiteSpace(State.Url))
        {
            lastSeenMediaId = State.MediaId;
            PendingJoinUrl = State.Url;
            PendingJoinStartSeconds = State.PositionSeconds;
            OnJoinConsentNeeded?.Invoke();
        }
    }

    public void Dispose() => Disconnect();
}
