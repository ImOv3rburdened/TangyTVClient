using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace TangyTV;

public sealed class TvWindow : Window, IDisposable
{
    private readonly SyncClient client;
    private readonly Config config;
    private readonly MpvController mpv;

    private bool showHelp = false;
    private bool showSettings = false;

    private Vector2 smoothedPos = Vector2.Zero;
    private bool followInitialized = false;

    private readonly IClientState? clientState;
    private readonly IGameGui? gameGui;
    private readonly IChatGui? chat;

    private readonly Dictionary<string, LocalLayoutOverride> localOverrides = new(StringComparer.OrdinalIgnoreCase);

    private sealed class LocalLayoutOverride
    {
        public bool Enabled;
        public int Width;
        public int Height;
    }

    private string? mpvMediaId = null;
    private bool mpvStartedForMedia = false;

    private long lastTickMs = 0;

    private sealed class ThemeScope : IDisposable
    {
        private readonly int pushedVars;
        private readonly int pushedColors;

        public ThemeScope(Config config)
        {
            pushedVars = 0;
            pushedColors = 0;

            // Style vars (scoped)
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, config.CornerRounding);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8);
            ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 6);

            // Colors (scoped)
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.12f, 0.08f, 0.18f, 0.95f));
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.65f, 0.45f, 0.85f, 0.8f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.75f, 0.55f, 0.95f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.85f, 0.65f, 1f, 1f));

            pushedVars = 3;
            pushedColors = 4;
        }

        public void Dispose()
        {
            if (pushedColors > 0) ImGui.PopStyleColor(pushedColors);
            if (pushedVars > 0) ImGui.PopStyleVar(pushedVars);
        }
    }

    public TvWindow(SyncClient client, Config config, MpvController mpv)
        : base("TangyTV", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.client = client;
        this.config = config;
        this.mpv = mpv;

        Size = new Vector2(440, 360);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.mpv.FileLoaded += () =>
        {
            _ = this.client.SendReadyAsync(this.client.State.MediaId);
        };

        this.client.OnMediaPrepared += () =>
        {
            TryAutoStartMpvIfAllowed();
        };

        this.client.OnJoinConsentNeeded += () => { };
    }

    public TvWindow(Config config, SyncClient client, MpvController mpv, IClientState clientState, IGameGui gameGui, IChatGui chat)
        : this(client, config, mpv)
    {
        this.clientState = clientState;
        this.gameGui = gameGui;
        this.chat = chat;
    }

    public void Dispose() { }

    public void Tick(float dt)
    {
        if (!config.UseMpv) return;
        if (!config.MpvFollowAnchor) return;
        if (!mpv.IsRunning) return;

        UpdateMpvGeometry(dt);
    }

    public void OpenSettingsTab()
    {
        showSettings = true;
        showHelp = false;
    }

    public override void Draw()
    {
        using var _theme = new ThemeScope(config);

        DrawHeader();

        // Service-level stats
        try
        {
            ImGui.TextColored(
                new Vector4(0.75f, 0.95f, 0.85f, 1f),
                $"Service: {client.CurrentConnected} online  •  Lifetime peak: {client.PeakConnected}"
            );
        }
        catch
        {
            
        }

        // Tabs
        if (ImGui.BeginTabBar("##TangyTVTabs"))
        {
            // Room
            if (ImGui.BeginTabItem("Room"))
            {
                DrawConnectionStatus();
                DrawRoomControls();
                DrawPresenceAndReady();
                ImGui.EndTabItem();
            }

            // Media
            if (ImGui.BeginTabItem("Media"))
            {
                DrawMediaControls();
                ImGui.EndTabItem();
            }

            // Layout
            if (ImGui.BeginTabItem("Layout"))
            {
                DrawLayoutControls();
                ImGui.EndTabItem();
            }

            // Placement
            if (ImGui.BeginTabItem("Placement"))
            {
                DrawPlacementControls();
                ImGui.EndTabItem();
            }

            // Settings (auto-select when OpenSettingsTab is called)
            var settingsFlags = showSettings ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("Settings", settingsFlags))
            {
                showSettings = false;
                DrawSettings();
                ImGui.EndTabItem();
            }

            // Help (auto-select if toggled elsewhere)
            var helpFlags = showHelp ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("Help", helpFlags))
            {
                showHelp = false;
                DrawHelp();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        DrawFooter();

        if (client.JoinConsentPending)
            DrawJoinConsentModal();
    }

    private void DrawHeader()
    {
        ImGui.TextColored(new Vector4(0.9f, 0.7f, 1f, 1f), "TangyTV Sync");
        ImGui.Separator();
    }

    private void DrawConnectionStatus()
    {
        ImGui.Text($"Status: {client.ConnectionStatus}");

        if (ImGui.Button("Connect"))
            _ = client.ConnectAsync();

        ImGui.SameLine();

        if (ImGui.Button("Disconnect"))
            client.Disconnect();

        ImGui.Text($"Room: {(client.RoomCode ?? "-")} {(client.IsHost ? "(Host)" : "")}");
    }

    private void DrawRoomControls()
    {
        ImGui.Separator();
        ImGui.Text("Room");

        ImGui.InputText("URL", ref client.DraftUrl, 512);

        var urlOk = UrlHelpers.TryNormalizeStreamingUrl(client.DraftUrl, out var provider, out var normalized, out var warning);

        if (!urlOk)
        {
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.75f, 1f), warning);
        }
        else
        {
            var label = provider == StreamProvider.YouTube ? "YouTube detected" : "Twitch detected";
            ImGui.TextColored(new Vector4(0.75f, 0.95f, 0.85f, 1f), $"{label} ✓");
            ImGui.TextColored(new Vector4(0.75f, 0.85f, 1f, 0.9f), $"Normalized: {normalized}");
        }

        using (ImRaii.Disabled(!urlOk))
        {
            if (ImGui.Button("Host"))
                _ = client.HostAsync(urlOk ? normalized : client.DraftUrl);
        }

        ImGui.InputText("Code", ref client.DraftRoom, 64);
        if (ImGui.Button("Join"))
            _ = client.JoinAsync(client.DraftRoom);

        if (ImGui.Button("Leave"))
            _ = client.LeaveAsync();
    }

    private void DrawMediaControls()
    {
        ImGui.Separator();
        ImGui.Text("Playback");

        using (ImRaii.Disabled(!client.IsHost))
        {
            if (ImGui.Button("Play"))
                _ = client.SetPlayingAsync(true);

            ImGui.SameLine();

            if (ImGui.Button("Pause"))
                _ = client.SetPlayingAsync(false);

            if (ImGui.Button("Seek +10s"))
                _ = client.SeekAsync(client.State.PositionSeconds + 10);

            ImGui.SameLine();

            if (ImGui.Button("Seek -10s"))
                _ = client.SeekAsync(Math.Max(0, client.State.PositionSeconds - 10));
        }

        var urlOk = UrlHelpers.TryNormalizeStreamingUrl(client.DraftUrl, out var provider, out var normalized, out var warning);

        using (ImRaii.Disabled(!urlOk || !client.IsHost))
        {
            if (ImGui.Button("Push URL"))
                _ = client.PushUrlAsync(urlOk ? normalized : client.DraftUrl);
        }

        if (config.UseMpv && mpv.IsRunning)
        {
            ImGui.Separator();
            ImGui.Text("Local MPV");

            if (ImGui.Button("Stop MPV"))
                mpv.Stop();

            ImGui.SameLine();
            if (ImGui.Button("Seek +5s"))
                mpv.SeekRelative(5);

            ImGui.SameLine();
            if (ImGui.Button("Seek -5s"))
                mpv.SeekRelative(-5);
        }
    }

    private void DrawPresenceAndReady()
    {
        ImGui.Separator();

        ImGui.Text($"Users: {client.State.Presence}");
        ImGui.Text($"Ready: {client.State.ReadyCount}/{client.State.ReadyTotal}");

        if (client.State.ReadyTotal > 0 && client.State.ReadyCount < client.State.ReadyTotal)
        {
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.85f, 1f), "Waiting for everyone to be ready...");
        }
    }

    private void DrawLayoutControls()
    {
        ImGui.Separator();
        ImGui.Text("Room Size");

        ImGui.Text($"Host default: {client.State.RoomWidthPx} x {client.State.RoomHeightPx}");

        using (ImRaii.Disabled(!client.IsHost))
        {
            int w = client.State.RoomWidthPx;
            int h = client.State.RoomHeightPx;

            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("W", ref w);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("H", ref h);

            if (ImGui.Button("Set Room Size"))
                _ = client.SetRoomLayoutAsync(w, h);
        }

        var room = client.RoomCode ?? "_no_room";
        var lo = GetLocalOverride(room);

        bool enabled = lo.Enabled;
        if (ImGui.Checkbox("Override locally", ref enabled))
        {
            lo.Enabled = enabled;
            if (lo.Width <= 0) lo.Width = client.State.RoomWidthPx;
            if (lo.Height <= 0) lo.Height = client.State.RoomHeightPx;
        }

        using (ImRaii.Disabled(!lo.Enabled))
        {
            int lw = lo.Width <= 0 ? client.State.RoomWidthPx : lo.Width;
            int lh = lo.Height <= 0 ? client.State.RoomHeightPx : lo.Height;

            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("My W", ref lw)) lo.Width = Math.Clamp(lw, 320, 3840);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("My H", ref lh)) lo.Height = Math.Clamp(lh, 180, 2160);
        }
    }

    private void DrawPlacementControls()
    {
        ImGui.Separator();
        ImGui.Text("Placement");

        bool follow = config.MpvFollowAnchor;
        if (ImGui.Checkbox("Follow Camera", ref follow))
        {
            config.MpvFollowAnchor = follow;
            config.Save();
        }

        bool trueAnchor = config.TrueAnchorOffscreen;
        if (ImGui.Checkbox("True Anchor (allow offscreen)", ref trueAnchor))
        {
            config.TrueAnchorOffscreen = trueAnchor;
            config.Save();
        }

        bool avoid = config.AvoidPlayerBody;
        if (ImGui.Checkbox("Avoid player body", ref avoid))
        {
            config.AvoidPlayerBody = avoid;
            config.Save();
        }

        if (ImGui.Button("Panic Recenter"))
            PanicRecenter();
    }

    private void DrawFooter()
    {
        ImGui.Separator();

        if (ImGui.Button("Help"))
            showHelp = true;

        ImGui.SameLine();

        if (ImGui.Button("Settings"))
            showSettings = true;
    }

    private void DrawHelp()
    {
        ImGui.Separator();
        ImGui.Text("Quick Actions");

        if (ImGui.Button("Connect"))
            _ = client.ConnectAsync();

        var urlOk = UrlHelpers.TryNormalizeStreamingUrl(client.DraftUrl, out var provider, out var normalized, out var warning);
        using (ImRaii.Disabled(!urlOk))
        {
            if (ImGui.Button("Host"))
                _ = client.HostAsync(urlOk ? normalized : client.DraftUrl);
        }

        if (ImGui.Button("Join"))
            _ = client.JoinAsync(client.DraftRoom);

        using (ImRaii.Disabled(!client.IsHost))
        {
            if (ImGui.Button("Play (Host)"))
                _ = client.SetPlayingAsync(true);

            ImGui.SameLine();

            if (ImGui.Button("Pause (Host)"))
                _ = client.SetPlayingAsync(false);
        }

        if (ImGui.Button("Panic Recenter TV"))
            PanicRecenter();
    }

    private void DrawSettings()
    {
        ImGui.Separator();

        var useMpv = config.UseMpv;
        if (ImGui.Checkbox("Use MPV", ref useMpv))
        {
            config.UseMpv = useMpv;
            config.Save();
        }

        var follow = config.MpvFollowAnchor;
        if (ImGui.Checkbox("Follow Camera", ref follow))
        {
            config.MpvFollowAnchor = follow;
            config.Save();
        }

        var hideUi = config.AutoHideUiWhenMpvStarts;
        if (ImGui.Checkbox("Hide UI When Playing", ref hideUi))
        {
            config.AutoHideUiWhenMpvStarts = hideUi;
            config.Save();
        }
    }

    private void DrawJoinConsentModal()
    {
        ImGui.OpenPopup("JoinMedia");

        if (ImGui.BeginPopupModal("JoinMedia", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Open media for this room?");
            ImGui.TextWrapped(client.PendingJoinUrl ?? "");

            if (ImGui.Button("Accept"))
            {
                client.AcknowledgeJoinConsentAccepted();

                TryAutoStartMpvIfAllowed();

                if (config.AutoHideUiWhenMpvStarts)
                    IsOpen = false;

                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();

            if (ImGui.Button("Decline"))
            {
                client.AcknowledgeJoinConsentDeclined();
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private LocalLayoutOverride GetLocalOverride(string room)
    {
        if (string.IsNullOrWhiteSpace(room))
            room = "_no_room";

        if (!localOverrides.TryGetValue(room, out var lo))
        {
            lo = new LocalLayoutOverride();
            localOverrides[room] = lo;
        }

        return lo;
    }

    private (int W, int H) GetEffectiveSize()
    {
        var room = client.RoomCode ?? "_no_room";
        var lo = GetLocalOverride(room);

        if (lo.Enabled)
        {
            var w = lo.Width <= 0 ? client.State.RoomWidthPx : lo.Width;
            var h = lo.Height <= 0 ? client.State.RoomHeightPx : lo.Height;
            return (Math.Clamp(w, 320, 3840), Math.Clamp(h, 180, 2160));
        }

        return (Math.Clamp(client.State.RoomWidthPx, 320, 3840), Math.Clamp(client.State.RoomHeightPx, 180, 2160));
    }

    private void TryAutoStartMpvIfAllowed()
    {
        if (!config.UseMpv) return;
        if (!config.AcceptedThisSession) return;
        if (string.IsNullOrWhiteSpace(client.State.Url)) return;
        if (string.IsNullOrWhiteSpace(client.State.MediaId)) return;

        if (mpvMediaId == null || mpvMediaId != client.State.MediaId)
        {
            mpvMediaId = client.State.MediaId;
            mpvStartedForMedia = false;
        }

        if (!mpvStartedForMedia)
        {
            mpvStartedForMedia = true;

            var (w, h) = GetEffectiveSize();
            _ = mpv.StartOrRestartAsync(
                config.ResolveMpvExePath(),
                config.MpvIpcPipeName,
                client.State.Url!,
                100,
                100,
                w,
                h,
                config.MpvBorderlessOnTop
            );
        }
    }

    public void PanicRecenter()
    {
        followInitialized = false;
        smoothedPos = Vector2.Zero;

        var lp = clientState?.LocalPlayer;
        if (lp != null)
        {
            var forward = lp.Rotation.ToForward();
            var pos = lp.Position + forward * config.AnchorForwardMeters;
            pos.Y += config.AnchorUpMeters;

            config.AnchorWorldX = pos.X;
            config.AnchorWorldY = pos.Y;
            config.AnchorWorldZ = pos.Z;
            config.AnchorEnabled = true;
            config.Save();

            chat?.Print("[TangyTV] Panic recenter: anchor set in front of you.");
        }
        else
        {
            chat?.Print("[TangyTV] Panic recenter: no player yet (anchor smoothing reset).");
        }
    }

    private void UpdateMpvGeometry(float dt)
    {
        if (clientState == null || gameGui == null) return;

        var lp = clientState.LocalPlayer;
        if (lp == null) return;

        Vector3 anchorWorld;
        if (config.AnchorEnabled)
            anchorWorld = new Vector3(config.AnchorWorldX, config.AnchorWorldY, config.AnchorWorldZ);
        else
        {
            var forward = lp.Rotation.ToForward();
            var pos = lp.Position + forward * config.AnchorForwardMeters;
            pos.Y += config.AnchorUpMeters;
            anchorWorld = pos;
        }

        if (!gameGui.WorldToScreen(anchorWorld, out var anchorScreen))
        {
            if (config.TrueAnchorOffscreen)
            {
                var (wOff, hOff) = GetEffectiveSize();
                mpv.SetGeometry(-5000, -5000, wOff, hOff, config.MpvGeometryMinIntervalMs, config.MpvGeometryMinDeltaPx);
            }
            return;
        }

        var (w, h) = GetEffectiveSize();
        var desired = new Vector2(anchorScreen.X - (w / 2f), anchorScreen.Y - (h / 2f));

        if (config.AvoidPlayerBody && gameGui.WorldToScreen(lp.Position, out var playerScreen))
        {
            desired = PickBestAvoidPlacement(desired, playerScreen, w, h);
        }

        if (!config.TrueAnchorOffscreen)
        {
            var vp = ImGui.GetMainViewport();
            desired = ClampToScreen(desired, w, h, vp.Size.X, vp.Size.Y);
        }

        if (!followInitialized)
        {
            smoothedPos = desired;
            followInitialized = true;
        }
        else
        {
            var tau = MathF.Max(0.01f, config.FollowSmoothingTauSeconds);
            var alpha = 1f - MathF.Exp(-MathF.Max(0.001f, dt) / tau);
            smoothedPos = Vector2.Lerp(smoothedPos, desired, alpha);
        }

        mpv.SetGeometry(
            (int)MathF.Round(smoothedPos.X),
            (int)MathF.Round(smoothedPos.Y),
            w,
            h,
            config.MpvGeometryMinIntervalMs,
            config.MpvGeometryMinDeltaPx
        );
    }

    public void SetAnchorHere()
    {
        {
            followInitialized = false;
            smoothedPos = Vector2.Zero;

            var lp = clientState?.LocalPlayer;
            if (lp == null)
            {
                chat?.Print("[TangyTV] Anchor: no player yet.");
                return;
            }

            var forward = lp.Rotation.ToForward();
            var pos = lp.Position + forward * config.AnchorForwardMeters;
            pos.Y += config.AnchorUpMeters;

            config.AnchorWorldX = pos.X;
            config.AnchorWorldY = pos.Y;
            config.AnchorWorldZ = pos.Z;
            config.AnchorEnabled = true;
            config.Save();

            chat?.Print("[TangyTV) Anchor set in front of you.");
        }
    }
        public void ClearAnchor()
    {
        followInitialized = false;
        smoothedPos = Vector2.Zero;

        config.AnchorEnabled = false;
        config.Save();

        chat?.Print("{TangyTV] Anchor cleared.");
    }

    private Vector2 PickBestAvoidPlacement(Vector2 desiredTopLeft, Vector2 playerScreen, int tvW, int tvH)
    {
        var pad = config.AvoidPaddingPx;

        var avoidX = playerScreen.X - (config.AvoidWidthPx / 2f);
        var avoidY = playerScreen.Y - config.AvoidHeadBiasPx;
        var avoidW = config.AvoidWidthPx;
        var avoidH = config.AvoidHeightPx;

        var candidates = new List<Vector2>
        {
            desiredTopLeft,
            new Vector2(avoidX + avoidW + pad, avoidY + 80),
            new Vector2(avoidX - tvW - pad,     avoidY + 80),
            new Vector2(avoidX + 40,            avoidY - tvH - pad),
            new Vector2(avoidX + 40,            avoidY + avoidH + pad),
        };

        var vp = ImGui.GetMainViewport();
        var screenW = vp.Size.X;
        var screenH = vp.Size.Y;

        float bestScore = float.MaxValue;
        Vector2 best = desiredTopLeft;

        foreach (var c in candidates)
        {
            var p = c;

            if (!config.TrueAnchorOffscreen)
                p = ClampToScreen(p, tvW, tvH, screenW, screenH);

            var overlap = OverlapArea(p.X, p.Y, tvW, tvH, avoidX, avoidY, avoidW, avoidH);
            var dist = Vector2.Distance(p, desiredTopLeft);
            var score = overlap * 10000f + dist;

            if (score < bestScore)
            {
                bestScore = score;
                best = p;
            }
        }

        return best;
    }

    private static float OverlapArea(float ax, float ay, float aw, float ah, float bx, float by, float bw, float bh)
    {
        var x1 = MathF.Max(ax, bx);
        var y1 = MathF.Max(ay, by);
        var x2 = MathF.Min(ax + aw, bx + bw);
        var y2 = MathF.Min(ay + ah, by + bh);
        if (x2 <= x1 || y2 <= y1) return 0;
        return (x2 - x1) * (y2 - y1);
    }

    private static Vector2 ClampToScreen(Vector2 p, int w, int h, float screenW, float screenH)
    {
        var maxX = MathF.Max(0f, screenW - w);
        var maxY = MathF.Max(0f, screenH - h);

        var x = Math.Clamp(p.X, 0f, maxX);
        var y = Math.Clamp(p.Y, 0f, maxY);

        return new Vector2(x, y);
    }
}

internal static class RotationExt_TvWindow
{
    public static Vector3 ToForward(this float yawRadians)
        => new((float)Math.Sin(yawRadians), 0f, (float)Math.Cos(yawRadians));
}
