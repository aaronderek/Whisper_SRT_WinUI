using System.Diagnostics;
using System.IO;
using System.Text;

namespace WhisperSubtitler.Services;

/// <summary>应用内一键安装后端依赖：创建 .venv 并 pip install requirements.txt。</summary>
public static class BackendInstaller
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static async Task<(bool Ok, string? Error)> InstallAsync(
        string backendDir,
        Action<string> onLog,
        Action<string> onStatus,
        CancellationToken ct)
    {
        string requirements = Path.Combine(backendDir, "requirements.txt");
        if (!File.Exists(requirements))
            return (false, LocalizationService.Format("RequirementsMissing", requirements));

        string venvPython = Path.Combine(backendDir, ".venv", "Scripts", "python.exe");

        if (!File.Exists(venvPython))
        {
            string? basePython = BackendLocator.FindBasePython();
            if (basePython is null)
                return (false, LocalizationService.Get("SystemPythonMissing"));

            onStatus(LocalizationService.Get("CreatingVenv"));
            var (venvOk, venvErr) = await RunAsync(
                basePython, new[] { "-m", "venv", Path.Combine(backendDir, ".venv") },
                backendDir, onLog, ct).ConfigureAwait(false);
            if (!venvOk)
                return (false, LocalizationService.Format("CreateVenvFailed", venvErr));
            if (!File.Exists(venvPython))
                return (false, LocalizationService.Get("VenvPythonMissing"));
        }

        onStatus(LocalizationService.Get("UpgradingPip"));
        // pip 自升级失败不致命，继续安装依赖
        await RunAsync(venvPython, new[] { "-m", "pip", "install", "--upgrade", "pip" },
            backendDir, onLog, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        onStatus(LocalizationService.Get("InstallingDependencies"));
        var (ok, err) = await RunAsync(
            venvPython, new[] { "-m", "pip", "install", "-r", "requirements.txt" },
            backendDir, onLog, ct).ConfigureAwait(false);
        if (!ok)
            return (false, LocalizationService.Format("DependencyInstallFailed", err));

        return (true, null);
    }

    private static async Task<(bool Ok, string? Error)> RunAsync(
        string exe, IEnumerable<string> args, string workingDir,
        Action<string> onLog, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            WorkingDirectory = workingDir,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch (Exception ex) { return (false, LocalizationService.Format("ProcessStartFailed", Path.GetFileName(exe), ex.Message)); }

        using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
        });

        string? lastError = null;
        Task stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                lastError = line;
                onLog(line);
            }
        }, CancellationToken.None);

        string? outLine;
        while ((outLine = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (!string.IsNullOrWhiteSpace(outLine))
                onLog(outLine);
        }

        try { await stderrTask.ConfigureAwait(false); } catch { }
        try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }

        ct.ThrowIfCancellationRequested();
        return process.ExitCode == 0
            ? (true, null)
            : (false, lastError ?? LocalizationService.Format("ExitCode", process.ExitCode));
    }
}
