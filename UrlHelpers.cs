namespace TangyTV;

public enum StreamProvider
{
    None = 0,
    YouTube = 1,
    Twitch = 2,
}

public static class UrlHelpers
{
    public static bool TryNormalizeStreamingUrl(
        string? input,
        out StreamProvider provider,
        out string normalized,
        out string warning)
    {
        provider = StreamProvider.None;
        normalized = "";
        warning = "";

        if (string.IsNullOrWhiteSpace(input))
        {
            warning = "Paste a YouTube or Twitch link.";
            return false;
        }

        var raw = input.Trim();

        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "https://" + raw;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            warning = "That doesn’t look like a valid URL.";
            return false;
        }

        var host = (uri.Host ?? "").ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        if (host.StartsWith("m.")) host = host[2..];

        if (host == "youtu.be")
        {
            var id = uri.AbsolutePath.Trim('/');
            if (!string.IsNullOrWhiteSpace(id))
            {
                provider = StreamProvider.YouTube;
                normalized = $"https://www.youtube.com/watch?v={id}";
                return true;
            }

            warning = "That YouTube short link is missing a video id.";
            return false;
        }

        if (host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            var path = uri.AbsolutePath.Trim('/');

            var v = GetQueryParam(uri.Query, "v");
            if (!string.IsNullOrWhiteSpace(v))
            {
                provider = StreamProvider.YouTube;
                normalized = $"https://www.youtube.com/watch?v={v}";
                return true;
            }

            if (path.StartsWith("shorts/", StringComparison.OrdinalIgnoreCase))
            {
                var id = path["shorts/".Length..].Split('/', '?', '#')[0];
                if (!string.IsNullOrWhiteSpace(id))
                {
                    provider = StreamProvider.YouTube;
                    normalized = $"https://www.youtube.com/watch?v={id}";
                    return true;
                }
            }

            if (path.StartsWith("live/", StringComparison.OrdinalIgnoreCase))
            {
                var id = path["live/".Length..].Split('/', '?', '#')[0];
                if (!string.IsNullOrWhiteSpace(id))
                {
                    provider = StreamProvider.YouTube;
                    normalized = $"https://www.youtube.com/watch?v={id}";
                    return true;
                }
            }

            warning = "That YouTube link format wasn’t recognized. Try a watch/shorts/live link.";
            return false;
        }

        if (host.EndsWith("twitch.tv", StringComparison.OrdinalIgnoreCase))
        {
            var path = uri.AbsolutePath.Trim('/');
            if (string.IsNullOrWhiteSpace(path))
            {
                warning = "That Twitch link is missing a channel or video id.";
                return false;
            }

            provider = StreamProvider.Twitch;
            normalized = "https://www.twitch.tv/" + path;
            return true;
        }

        if (host.EndsWith("clips.twitch.tv", StringComparison.OrdinalIgnoreCase))
        {
            var slug = uri.AbsolutePath.Trim('/');
            if (string.IsNullOrWhiteSpace(slug))
            {
                warning = "That Twitch clip link is missing a clip id.";
                return false;
            }

            provider = StreamProvider.Twitch;
            normalized = "https://clips.twitch.tv/" + slug;
            return true;
        }

        warning = "Only YouTube or Twitch links are supported.";
        return false;
    }

    public static string AddStartTime(string url, double? seconds)
    {
        if (string.IsNullOrWhiteSpace(url) || seconds is null) return url;

        var sec = (int)Math.Floor(Math.Max(0, seconds.Value));
        if (sec <= 0) return url;

        if (url.Contains("t=", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("start=", StringComparison.OrdinalIgnoreCase))
            return url;

        var join = url.Contains('?') ? "&" : "?";

        if (url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
            return url + join + "t=" + sec;

        return url + join + "start=" + sec;
    }

    public static bool UrlEquals(string? a, string? b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? GetQueryParam(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(key))
            return null;

        var q = query;
        if (q.Length > 0 && q[0] == '?') q = q[1..];

        var parts = q.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 0) continue;

            var k = Uri.UnescapeDataString(kv[0] ?? "");
            if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                continue;

            var v = kv.Length > 1 ? kv[1] : "";
            return Uri.UnescapeDataString(v ?? "");
        }

        return null;
    }

    public static bool IsSupportedStreamingUrl(string? input)
        => TryNormalizeStreamingUrl(input, out _, out _, out _);

    public static string TryNormalizeStreamingUrlOrOriginal(string input, out bool ok, out string warning)
    {
        ok = TryNormalizeStreamingUrl(input, out _, out var norm, out warning);
        return ok ? norm : input;
    }
}
