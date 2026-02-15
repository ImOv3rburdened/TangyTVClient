using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMJIFarmManagement;

namespace TangyTV;

public sealed class MpvController : IDisposable
{
    private Process? proc;
    private NamedPipeClientStream? pipe;
    private StreamWriter? writer;
    private StreamReader? reader;
    private CancellationTokenSource? readCts;
    private Task? readTask;

    private int requestId = 0;

    private long lastGeomMs = 0;
    private int lastX = int.MinValue;
    private int lastY = int.MinValue;
    private int lastW = -1;
    private int lastH = -1;

    private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject?>> pendingReplies = new();

    public event Action? FileLoaded;

    public bool IsRunning => proc != null && !proc.HasExited && pipe != null && pipe.IsConnected;

    public async Task<bool> StartOrRestartAsync(
        string mpvExe,
        string ipcPipeName,
        string url,
        int x,
        int y,
        int w,
        int h,
        bool borderlessOnTop)
    {
        try
        {
            Stop();

            var ipcPath = $@"\\.\pipe\{ipcPipeName}";
            var geometry = $"{w}x{h}+{x}+{y}";

            var args =
                "--force-window=yes " +
                $"--input-ipc-server=\"{ipcPath}\" " +
                (borderlessOnTop ? "--no-border --ontop " : "") +
                "--ytdl=yes " +
                "--cache=yes " +
                "--cache-pause=no " +
                "--demuxer-readahead-secs=20 " +
                "--demuxer-max-bytes=150MiB " +
                "--keep-open=yes " +
                $"--geometry=\"{geometry}\" " +
                $"\"{url}\"";

            var mpvDir = "";
            try
            {
                if (Path.IsPathRooted(mpvExe))
                    mpvDir = Path.GetDirectoryName(mpvExe) ?? "";
            }
            catch { }

            proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = mpvExe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = string.IsNullOrWhiteSpace(mpvDir) ? Environment.CurrentDirectory : mpvDir,
                },
                EnableRaisingEvents = true
            };

            if (!string.IsNullOrWhiteSpace(mpvDir))
            {
                var existingPath = proc.StartInfo.EnvironmentVariables["PATH"] ?? Environment.GetEnvironmentVariable("PATH") ?? "";
                proc.StartInfo.EnvironmentVariables["PATH"] = mpvDir + Path.PathSeparator + existingPath;
            }

            if (!proc.Start())
            {
                Stop();
                return false;
            }

            var connected = await ConnectPipeAsync(ipcPipeName, TimeSpan.FromSeconds(4));
            if (!connected)
                return true;

            _ = RequestEventsAsync();
            return true;
        }
        catch
        {
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        try { readCts?.Cancel(); } catch { }

        try
        {
            writer?.Dispose();
            reader?.Dispose();
            pipe?.Dispose();
        }
        catch { }

        writer = null;
        reader = null;
        pipe = null;

        try
        {
            if (proc != null && !proc.HasExited)
            {
                try { proc.Kill(true); } catch { }
            }
        }
        catch { }

        try { proc?.Dispose(); } catch { }
        proc = null;

        try
        {
            readTask = null;
            readCts?.Dispose();
            readCts = null;
        }
        catch { }

        foreach (var kv in pendingReplies)
            kv.Value.TrySetResult(null);

        pendingReplies.Clear();

        lastGeomMs = 0;
        lastX = int.MinValue;
        lastY = int.MinValue;
        lastW = -1;
        lastH = -1;
    }

    private async Task<bool> ConnectPipeAsync(string pipeName, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;

        while (DateTime.UtcNow - start < timeout)
        {
            try
            {
                pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(250);

                writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                reader = new StreamReader(pipe, new UTF8Encoding(false));

                readCts = new CancellationTokenSource();
                readTask = Task.Run(() => ReadLoop(readCts.Token));

                return true;
            }
            catch
            {
                try { pipe?.Dispose(); } catch { }
                pipe = null;
                writer = null;
                reader = null;
                await Task.Delay(100);
            }
        }

        return false;
    }

    private async Task ReadLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && reader != null)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;

                JObject? obj = null;
                try { obj = JObject.Parse(line); } catch { }
                if (obj == null) continue;

                var rid = obj.Value<int?>("request_id");
                if (rid != null && pendingReplies.TryRemove(rid.Value, out var tcs))
                    tcs.TrySetResult(obj);

                var evt = obj.Value<string>("event");
                if (string.Equals(evt, "file-loaded", StringComparison.OrdinalIgnoreCase))
                    FileLoaded?.Invoke();
            }
        }
        catch { }
    }

    private void SendJson(string jsonLine)
    {
        try { writer?.WriteLine(jsonLine); }
        catch { }
    }

    private int NextId() => Interlocked.Increment(ref requestId);

    private Task<JObject?> CommandAsync(string json)
    {
        var id = NextId();
        var tcs = new TaskCompletionSource<JObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingReplies[id] = tcs;

        if (!json.Contains("\"request_id\"", StringComparison.Ordinal))
        {
            if (json.EndsWith("}"))
                json = json[..^1] + $",\"request_id\":{id}}}";
        }

        SendJson(json);
        return tcs.Task;
    }

    private Task RequestEventsAsync()
        => CommandAsync("{\"command\":[\"enable_event\",\"file-loaded\"]}");

    private static string Escape(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public void LoadUrl(string url)
        => SendJson($"{{\"command\":[\"loadfile\",\"{Escape(url)}\",\"replace\"]}}");

    public void SetPause(bool paused)
        => SendJson($"{{\"command\":[\"set_property\",\"pause\",{(paused ? "true" : "false")}]}}");

    public void SeekAbsolute(double seconds)
        => SendJson($"{{\"command\":[\"seek\",{seconds.ToString(CultureInfo.InvariantCulture)},\"absolute\"]}}");

    public void SeekRelative(double seconds)
        => SendJson($"{{\"command\":[\"seek\",{seconds.ToString(CultureInfo.InvariantCulture)},\"relative\"]}}");

    public void StopPlayback()
        => SendJson("{\"command\":[\"stop\"]}");

    public void SetGeometry(int x, int y, int w, int h, int minIntervalMs, int minDeltaPx)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - lastGeomMs < minIntervalMs) return;

        var dx = Math.Abs(x - lastX);
        var dy = Math.Abs(y - lastY);
        var dw = Math.Abs(w - lastW);
        var dh = Math.Abs(h - lastH);

        if (dx < minDeltaPx && dy < minDeltaPx && dw == 0 && dh == 0)
            return;

        lastGeomMs = now;
        lastX = x; lastY = y; lastW = w; lastH = h;

        var geom = $"{w}x{h}+{x}+{y}";
        SendJson($"{{\"command\":[\"set_property\",\"geometry\",\"{Escape(geom)}\"]}}");
    }

    public void SetGeometry(int x, int y, int w, int h, int minIntervalMs)
        => SetGeometry(x, y, w, h, minIntervalMs, 2);

    public void Dispose() => Stop();
}
