using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WhisperSubtitler.Services;

public sealed class TranscribeRequest
{
    public required string PythonPath { get; init; }
    public required string ScriptPath { get; init; }
    public required string Input { get; init; }
    public required string Output { get; init; }
    public string Model { get; init; } = "large-v3";
    public string Language { get; init; } = "en";
    public string Task { get; init; } = "transcribe";
    public string Device { get; init; } = "cuda";
    public string ComputeType { get; init; } = "float16";
    public int BeamSize { get; init; } = 5;
    public int MaxLineChars { get; init; } = 42;
    public bool UseVad { get; init; } = true;
    public bool WordTimestamps { get; init; } = false;
    public bool CpuFallback { get; init; } = true;
    /// <summary>输出格式："srt" 字幕（默认）或 "txt" 无时间码纯文本文稿。</summary>
    public string Format { get; init; } = "srt";
}

public sealed record TranscribeResult(
    bool Success, bool Canceled, string? OutputPath,
    int Segments, double Elapsed, double SpeedX, string? Error,
    int Paragraphs = 0);

public sealed record CheckResult(
    bool Ok, int CudaDeviceCount, string? GpuName,
    string? FasterWhisper, string? Ctranslate2, string? Error);

public sealed record ModelCacheInfo(string Name, string Repo, bool Downloaded, long SizeBytes);

/// <summary>启动 Python 后端子进程，解析其 stdout 上的 JSON 进度协议。</summary>
public static class BackendRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static async Task<TranscribeResult> RunAsync(
        TranscribeRequest req,
        Action<double, double, double>? onProgress,
        Action<string>? onStatus,
        Action<string>? onLog,
        CancellationToken ct)
    {
        var psi = CreateStartInfo(req.PythonPath, req.ScriptPath);
        psi.ArgumentList.Add("--input"); psi.ArgumentList.Add(req.Input);
        psi.ArgumentList.Add("--output"); psi.ArgumentList.Add(req.Output);
        // 后端的默认格式就是 srt：默认路径不传 --format，便于兼容尚未升级的旧后端脚本
        if (!string.Equals(req.Format, "srt", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("--format"); psi.ArgumentList.Add(req.Format);
        }
        psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(req.Model);
        psi.ArgumentList.Add("--language"); psi.ArgumentList.Add(req.Language);
        psi.ArgumentList.Add("--task"); psi.ArgumentList.Add(req.Task);
        psi.ArgumentList.Add("--device"); psi.ArgumentList.Add(req.Device);
        psi.ArgumentList.Add("--compute-type"); psi.ArgumentList.Add(req.ComputeType);
        psi.ArgumentList.Add("--beam-size"); psi.ArgumentList.Add(req.BeamSize.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--max-line-chars"); psi.ArgumentList.Add(req.MaxLineChars.ToString(CultureInfo.InvariantCulture));
        if (!req.UseVad) psi.ArgumentList.Add("--no-vad");
        if (req.WordTimestamps) psi.ArgumentList.Add("--word-timestamps");
        if (req.CpuFallback) psi.ArgumentList.Add("--cpu-fallback");

        using var process = new Process { StartInfo = psi };

        string? errorMessage = null;
        string? outputPath = null;
        int segments = 0;
        int paragraphs = 0;
        double elapsed = 0, speed = 0;
        bool doneSeen = false;

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new TranscribeResult(false, false, null, 0, 0, 0, LocalizationService.Format("PythonBackendStartFailed", ex.Message));
        }

        using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* 进程可能已退出 */ }
        });

        Task stderrTask = PumpStderrAsync(process.StandardError, onLog);

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(trimmed); }
            catch { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl))
                    continue;

                switch (typeEl.GetString())
                {
                    case "progress":
                        onProgress?.Invoke(GetDouble(root, "current"), GetDouble(root, "total"), GetDouble(root, "pct"));
                        break;

                    case "status":
                    case "cpu_fallback":
                        string? msg = GetString(root, "message");
                        if (msg is not null) onStatus?.Invoke(msg);
                        break;

                    case "info":
                        string lang = GetString(root, "language") ?? "?";
                        double dur = GetDouble(root, "duration");
                        onStatus?.Invoke(LocalizationService.Format("DetectedLanguage", lang, FormatDuration(dur)));
                        break;

                    case "done":
                        doneSeen = true;
                        outputPath = GetString(root, "output");
                        segments = (int)GetDouble(root, "segments");
                        paragraphs = (int)GetDouble(root, "paragraphs");
                        elapsed = GetDouble(root, "elapsed");
                        speed = GetDouble(root, "speed_x");
                        break;

                    case "error":
                        errorMessage = GetString(root, "message") ?? LocalizationService.Get("Unknown");
                        string? tb = GetString(root, "traceback");
                        if (tb is not null) onLog?.Invoke(tb);
                        break;
                }
            }
        }

        await stderrTask.ConfigureAwait(false);
        try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* 已被 kill */ }

        if (ct.IsCancellationRequested)
            return new TranscribeResult(false, true, null, 0, 0, 0, null);

        if (errorMessage is not null)
            return new TranscribeResult(false, false, null, 0, 0, 0, errorMessage);

        if (doneSeen)
            return new TranscribeResult(true, false, outputPath, segments, elapsed, speed, null, paragraphs);

        int code = SafeExitCode(process);
        return new TranscribeResult(false, false, null, 0, 0, 0, LocalizationService.Format("BackendExitedNoDone", code));
    }

    public static async Task<CheckResult> CheckAsync(string pythonPath, string scriptPath, CancellationToken ct)
    {
        var psi = CreateStartInfo(pythonPath, scriptPath);
        psi.ArgumentList.Add("--check");

        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch (Exception ex)
        {
            return new CheckResult(false, 0, null, null, null, LocalizationService.Format("PythonStartFailed", ex.Message));
        }

        using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
        });

        CheckResult? result = null;
        var stderrLines = new List<string>();
        Task stderrTask = PumpStderrAsync(process.StandardError, l =>
        {
            lock (stderrLines)
            {
                if (stderrLines.Count < 50) stderrLines.Add(l);
            }
        });

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                continue;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var t) && t.GetString() == "check")
                {
                    result = new CheckResult(
                        root.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                        (int)GetDouble(root, "cuda_device_count"),
                        GetString(root, "gpu_name"),
                        GetString(root, "faster_whisper"),
                        GetString(root, "ctranslate2"),
                        GetString(root, "error"));
                }
            }
            catch { }
        }

        try { await stderrTask.ConfigureAwait(false); } catch { }
        try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { }

        if (result is not null)
            return result;

        string detail;
        lock (stderrLines)
        {
            detail = stderrLines.Count > 0
                ? " " + string.Join(" | ", stderrLines.TakeLast(3))
                : "";
        }
        return new CheckResult(false, 0, null, null, null,
            LocalizationService.Format("BackendNoCheckResult", SafeExitCode(process), detail));
    }

    /// <summary>查询各模型的本地缓存状态（transcribe.py --models-status）。</summary>
    public static async Task<(IReadOnlyList<ModelCacheInfo>? Models, string? Error)> GetModelsAsync(
        string pythonPath, string scriptPath, CancellationToken ct)
    {
        List<ModelCacheInfo>? models = null;
        string? error = null;
        var (_, runError) = await RunToolCoreAsync(pythonPath, scriptPath, new[] { "--models-status" },
            onJson: root =>
            {
                switch (GetString(root, "type"))
                {
                    case "models" when root.TryGetProperty("items", out var items):
                        models = new List<ModelCacheInfo>();
                        foreach (var it in items.EnumerateArray())
                        {
                            models.Add(new ModelCacheInfo(
                                GetString(it, "name") ?? "",
                                GetString(it, "repo") ?? "",
                                it.TryGetProperty("downloaded", out var d) && d.GetBoolean(),
                                (long)GetDouble(it, "size_bytes")));
                        }
                        break;
                    case "error":
                        error = GetString(root, "message");
                        break;
                }
            }, null, null, ct).ConfigureAwait(false);

        return (models, error ?? runError ?? (models is null ? LocalizationService.Get("BackendNoModelList") : null));
    }

    /// <summary>运行模型下载/删除等工具型子命令，解析 done/error/status 协议。</summary>
    public static async Task<(bool Ok, string? Error)> RunToolAsync(
        string pythonPath, string scriptPath, string[] args,
        Action<string>? onStatus, Action<string>? onLog, CancellationToken ct)
    {
        bool done = false;
        string? error = null;
        var (exitOk, runError) = await RunToolCoreAsync(pythonPath, scriptPath, args,
            onJson: root =>
            {
                switch (GetString(root, "type"))
                {
                    case "done": done = true; break;
                    case "error": error = GetString(root, "message"); break;
                    case "status":
                        string? msg = GetString(root, "message");
                        if (msg is not null) onStatus?.Invoke(msg);
                        break;
                }
            }, onLog, null, ct).ConfigureAwait(false);

        if (ct.IsCancellationRequested) return (false, LocalizationService.Get("Canceled"));
        if (error is not null) return (false, error);
        if (done) return (true, null);
        return (false, runError ?? (exitOk ? LocalizationService.Get("BackendNoDone") : LocalizationService.Get("BackendExited")));
    }

    private static async Task<(bool ExitOk, string? Error)> RunToolCoreAsync(
        string pythonPath, string scriptPath, string[] args,
        Action<System.Text.Json.JsonElement>? onJson,
        Action<string>? onLog, Action<string>? onStderrTail, CancellationToken ct)
    {
        var psi = CreateStartInfo(pythonPath, scriptPath);
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch (Exception ex) { return (false, LocalizationService.Format("PythonStartFailed", ex.Message)); }

        using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
        });

        string? lastStderr = null;
        Task stderrTask = PumpStderrAsync(process.StandardError, l => { lastStderr = l; onLog?.Invoke(l); });

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                onJson?.Invoke(doc.RootElement);
            }
            catch { }
        }

        try { await stderrTask.ConfigureAwait(false); } catch { }
        try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }

        if (SafeExitCode(process) != 0 && lastStderr is not null)
            onStderrTail?.Invoke(lastStderr);
        return (SafeExitCode(process) == 0,
            SafeExitCode(process) == 0 ? null : lastStderr);
    }

    // ---- helpers ----

    private static ProcessStartInfo CreateStartInfo(string python, string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            WorkingDirectory = Path.GetDirectoryName(script) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add(script);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        return psi;
    }

    private static async Task PumpStderrAsync(StreamReader reader, Action<string>? onLog)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
                onLog?.Invoke(line);
        }
    }

    private static double GetDouble(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : 0.0;

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }
}
