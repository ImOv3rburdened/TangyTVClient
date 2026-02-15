using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace TangyTV;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "TangyTV";

    private readonly WindowSystem windowSystem = new("TangyTV");
    private readonly TvWindow tvWindow;
    private readonly Config config;
    private readonly SyncClient syncClient;
    private readonly MpvController mpv;

    [PluginService] private static IDalamudPluginInterface Pi { get; set; } = null!;
    [PluginService] private static ICommandManager Commands { get; set; } = null!;
    [PluginService] private static IChatGui Chat { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IGameGui GameGui { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;

    public Plugin()
    {
        config = Pi.GetPluginConfig() as Config ?? new Config();
        config.Initialize(Pi);

        mpv = new MpvController();
        syncClient = new SyncClient(config, Log, Chat);
        tvWindow = new TvWindow(config, syncClient, mpv, ClientState, GameGui, Chat);

        windowSystem.AddWindow(tvWindow);

        Pi.UiBuilder.Draw += DrawUi;
        Pi.UiBuilder.OpenConfigUi += OpenConfigUi;
        Framework.Update += OnFrameworkUpdate;

        RegisterCommands();

        if (config.AutoConnect && !string.IsNullOrWhiteSpace(config.ServerUrl))
            _ = syncClient.ConnectAsync();
    }

    private void RegisterCommands()
    {
        Commands.AddHandler("/tv", new CommandInfo(OnCommand)
        {
            HelpMessage = "TangyTV: /tv [help|connect|disconnect|host|join|leave|play|pause|seek|size|anchor|recenter]"
        });
    }

    private void OnCommand(string command, string args)
    {
        var a = (args ?? "").Trim();

        if (string.IsNullOrWhiteSpace(a))
        {
            tvWindow.IsOpen = !tvWindow.IsOpen;
            return;
        }

        if (a.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            Chat.Print("[TangyTV] Commands:");
            Chat.Print("  /tv                      - toggle window");
            Chat.Print("  /tv help                 - show this help");
            Chat.Print("  /tv connect              - connect to sync server");
            Chat.Print("  /tv disconnect           - disconnect");
            Chat.Print("  /tv host <url>           - create room + push url");
            Chat.Print("  /tv join <roomCode>      - join room");
            Chat.Print("  /tv leave                - leave room");
            Chat.Print("  /tv play | pause         - host: play/pause");
            Chat.Print("  /tv seek <seconds>       - host: seek");
            Chat.Print("  /tv size <w> <h>         - host: set room default size");
            Chat.Print("  /tv anchor here          - set anchor in front of you");
            Chat.Print("  /tv anchor clear         - clear anchor");
            Chat.Print("  /tv recenter             - panic: recenter TV to player");
            return;
        }

        var parts = a.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1] : "";

        switch (cmd)
        {
            case "ui":
                tvWindow.IsOpen = !tvWindow.IsOpen;
                break;

            case "connect":
                _ = syncClient.ConnectAsync();
                break;

            case "disconnect":
                syncClient.Disconnect();
                break;

            case "host":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    Chat.Print("[TangyTV] Usage: /tv host <url>");
                    break;
                }
                _ = syncClient.HostAsync(rest.Trim());
                break;

            case "join":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    Chat.Print("[TangyTV] Usage: /tv join <roomCode>");
                    break;
                }
                _ = syncClient.JoinAsync(rest.Trim());
                break;

            case "leave":
                syncClient.LeaveAsync();
                break;

            case "play":
                _ = syncClient.SetPlayingAsync(true);
                break;

            case "pause":
                _ = syncClient.SetPlayingAsync(false);
                break;

            case "seek":
                if (!double.TryParse(rest.Trim(), out var sec))
                {
                    Chat.Print("[TangyTV] Usage: /tv seek <seconds>");
                    break;
                }
                _ = syncClient.SeekAsync(sec);
                break;

            case "size":
                {
                    var bits = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (bits.Length < 2 || !int.TryParse(bits[0], out var w) || !int.TryParse(bits[1], out var h))
                    {
                        Chat.Print("[TangyTV] Usage: /tv size <w> <h>");
                        break;
                    }

                    _ = syncClient.SetRoomLayoutAsync(w, h);
                    break;
                }

            case "anchor":
                if (rest.Equals("here", StringComparison.OrdinalIgnoreCase))
                    tvWindow.SetAnchorHere();
                else if (rest.Equals("clear", StringComparison.OrdinalIgnoreCase))
                    tvWindow.ClearAnchor();
                else
                    Chat.Print("[TangyTV] Usage: /tv anchor here|clear");
                break;

            case "recenter":
                tvWindow.PanicRecenter();
                break;

            default:
                Chat.Print("[TangyTV] Unknown command. Try /tv help");
                break;
        }
    }

    private void DrawUi()
    {
        windowSystem.Draw();
    }

    private void OpenConfigUi()
    {
        tvWindow.IsOpen = true;
        tvWindow.OpenSettingsTab();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        tvWindow.Tick((float)framework.UpdateDelta.TotalSeconds);
    }

    public void Dispose()
    {
        try { Framework.Update -= OnFrameworkUpdate; } catch { }

        Pi.UiBuilder.Draw -= DrawUi;
        Pi.UiBuilder.OpenConfigUi -= OpenConfigUi;

        Commands.RemoveHandler("/tv");

        tvWindow.Dispose();
        syncClient.Dispose();
        mpv.Dispose();

        windowSystem.RemoveAllWindows();
    }
}
