using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Numerics;

namespace TangyTV;

[Serializable]
public sealed class Config : IPluginConfiguration
{
    public int Version { get; set; } = 8;

    public string ServerUrl { get; set; } = "ws://173.208.169.194/ws";
    public bool AutoConnect { get; set; } = true;

    // Overlay defaults
    public bool ShowOverlay { get; set; } = true;
    public float TvWidthPx { get; set; } = 720f;
    public float TvHeightPx { get; set; } = 405f;
    public float OverlayOpacity { get; set; } = 0.72f;
    public float CornerRounding { get; set; } = 12f;

    // Anchor
    public bool AnchorEnabled { get; set; } = false;
    public float AnchorWorldX { get; set; }
    public float AnchorWorldY { get; set; }
    public float AnchorWorldZ { get; set; }
    public float AnchorForwardMeters { get; set; } = 2.2f;
    public float AnchorUpMeters { get; set; } = 1.2f;

    public float AnchorOffsetX { get; set; } = 320f;
    public float AnchorOffsetY { get; set; } = -220f;

    public bool TrueAnchorEnabled { get; set; } = true;

    // Avoid covering the player
    public bool AvoidPlayerBodyEnabled { get; set; } = true;
    public float AvoidRectWidthPx { get; set; } = 260f;
    public float AvoidRectHeightPx { get; set; } = 520f;
    public float AvoidRectUpBiasPx { get; set; } = 420f;
    public float AvoidPadPx { get; set; } = 24f;

    // Consent/Safety
    public bool AskOnJoinBeforeOpening { get; set; } = true;
    public bool AllowAutoOpenIfAcceptedOnceThisSession { get; set; } = true;

    [JsonIgnore] public bool AcceptedThisSession { get; set; } = false;
    [JsonIgnore] public string? LastOpenedUrlThisSession { get; set; }
    [JsonIgnore] public string? AcceptedRoomCodeThisSession { get; set; }

    // MPV
    public bool UseMpv { get; set; } = true;
    public string MpvExe { get; set; } = "";
    public bool MpvBorderlessOnTop { get; set; } = true;
    public bool MpvFollowAnchor { get; set; } = true;
    public bool HideOverlayWhileMpvRunning { get; set; } = true;
    public bool AutoHideUiWhenMpvStarts { get; set; } = true;

    // Smoothing/Throttles
    public float FollowSmoothingTauSeconds { get; set; } = 0.08f;
    public int MpvGeometryMinIntervalMs { get; set; } = 90;
    public int MpvGeometryMinDeltaPx { get; set; } = 2;

    public string MpvIpcPipeName { get; set; } = "TangyTV_mpv_ipc";

    // One-time first run consent gate for bundled binaries
    public bool BundledBinariesAccepted { get; set; } = false;

    [JsonIgnore] private IDalamudPluginInterface? pi;
    [JsonIgnore] public string PluginDir { get; private set; } = "";

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        pi = pluginInterface;
        PluginDir = pi.AssemblyLocation.Directory?.FullName ?? "";
    }

    public void Save() => pi?.SavePluginConfig(this);

    public Vector3 AnchorPos() => new(AnchorWorldX, AnchorWorldY, AnchorWorldZ);

    public string ResolveMpvExePath()
    {
        // User override
        if (!string.IsNullOrWhiteSpace(MpvExe))
        {
            if (Path.IsPathRooted(MpvExe))
                return MpvExe;

            if (!string.IsNullOrWhiteSpace(PluginDir))
                return Path.Combine(PluginDir, MpvExe);

            return MpvExe;
        }

        // Bundled
        if (!string.IsNullOrWhiteSpace(PluginDir))
        {
            var bundled = Path.Combine(PluginDir, "mpv", "mpv.exe");
            if (File.Exists(bundled))
                return bundled;
        }

        // Fallback
        return "mpv";
    }

    public string BundledMpvPath()
        => string.IsNullOrWhiteSpace(PluginDir) ? "mpv\\mpv.exe" : Path.Combine(PluginDir, "mpv", "mpv.exe");

    public bool HasBundledMpv()
    {
        try { return File.Exists(BundledMpvPath()); }
        catch { return false; }
    }

    public int DefaultRoomWidthPx { get; set; } = 720;
    public int DefaultRoomHeightPx { get; set; } = 405;


    [JsonIgnore]
    public bool TrueAnchorOffscreen
    {
        get => TrueAnchorEnabled;
        set => TrueAnchorEnabled = value;
    }

    [JsonIgnore]
    public bool AvoidPlayerBody
    {
        get => AvoidPlayerBodyEnabled;
        set => AvoidPlayerBodyEnabled = value;
    }

    [JsonIgnore]
    public int AvoidWidthPx
    {
        get => (int)MathF.Round(AvoidRectWidthPx);
        set => AvoidRectWidthPx = Math.Clamp(value, 80, 1200);
    }

    [JsonIgnore]
    public int AvoidHeightPx
    {
        get => (int)MathF.Round(AvoidRectHeightPx);
        set => AvoidRectHeightPx = Math.Clamp(value, 80, 2000);
    }

    [JsonIgnore]
    public int AvoidHeadBiasPx
    {
        get => (int)MathF.Round(AvoidRectUpBiasPx);
        set => AvoidRectUpBiasPx = Math.Clamp(value, 0, 2000);
    }

    [JsonIgnore]
    public int AvoidPaddingPx
    {
        get => (int)MathF.Round(AvoidPadPx);
        set => AvoidPadPx = Math.Clamp(value, 0, 400);
    }
}
