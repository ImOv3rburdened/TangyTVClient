using System;

namespace TangyTV;

public static class StreamingUrl
{
    public static bool TryNormalize(string input, out StreamProvider provider, out string normalized, out string warning)
        => UrlHelpers.TryNormalizeStreamingUrl(input, out provider, out normalized, out warning);
}
