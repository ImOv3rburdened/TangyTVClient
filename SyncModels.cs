namespace TangyTV;

public sealed class TvState
{
    public string? Url { get; set; }
    public bool IsPlaying { get; set; }
    public double PositionSeconds { get; set; }
    public long ServerTimeMs { get; set; }

    public string? MediaId { get; set; }
    public int Presence { get; set; }
    public int ReadyCount { get; set; }
    public int ReadyTotal { get; set; }

    // Room layout
    public int RoomWidthPx { get; set; } = 720;
    public int RoomHeightPx { get; set; } = 405;
}

public sealed class WsEnvelope
{
    public string Type { get; set; } = "";
    public string? Room { get; set; }

    // Media / sync
    public string? MediaId { get; set; }
    public string? Url { get; set; }
    public bool? IsPlaying { get; set; }
    public double? PositionSeconds { get; set; }
    public long? ServerTimeMs { get; set; }

    // Presence / readiness
    public int? Presence { get; set; }
    public int? ReadyCount { get; set; }
    public int? ReadyTotal { get; set; }

    // Room layout
    public int? WidthPx { get; set; }
    public int? HeightPx { get; set; }

    // Server stats
    public long? CurrentConnected { get; set; }
    public long? PeakConnected { get; set; }

    public string? Message { get; set; }
}
