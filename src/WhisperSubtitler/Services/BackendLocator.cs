using System.IO;

namespace WhisperSubtitler.Services;

/// <summary>定位 Python 后端脚本与虚拟环境解释器。</summary>
public static class BackendLocator
{
    /// <summary>
    /// 查找 backend\transcribe.py。顺序：显式覆盖 -> 从程序目录向上逐级查找 backend\transcribe.py。
    /// </summary>
    public static string? FindBackendScript(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (File.Exists(overridePath) &&
                Path.GetFileName(overridePath).Equals("transcribe.py", StringComparison.OrdinalIgnoreCase))
                return overridePath;

            string candidate = Path.Combine(overridePath, "transcribe.py");
            if (File.Exists(candidate))
                return candidate;
        }

        // MSIX 安装目录只读：把随包携带的后端安装文件同步到用户可写目录。
        string bundled = Path.Combine(AppContext.BaseDirectory, "backend", "transcribe.py");
        if (File.Exists(bundled))
        {
            string? materialized = MaterializeBundledBackend(Path.GetDirectoryName(bundled)!);
            return materialized ?? bundled;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir.FullName, "backend", "transcribe.py");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }


        // 允许开发目录已移除后继续复用安装版曾创建的后端目录。
        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperSubtitler", "backend", "transcribe.py");
        if (File.Exists(local))
            return local;

        return null;
    }

    private static string? MaterializeBundledBackend(string bundledDir)
    {
        try
        {
            string localDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WhisperSubtitler", "backend");
            Directory.CreateDirectory(localDir);

            foreach (string name in new[] { "transcribe.py", "requirements.txt", "setup.bat", "setup.ps1" })
            {
                string source = Path.Combine(bundledDir, name);
                if (File.Exists(source))
                    File.Copy(source, Path.Combine(localDir, name), overwrite: true);
            }

            string script = Path.Combine(localDir, "transcribe.py");
            return File.Exists(script) ? script : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 查找 Python 解释器。顺序：显式覆盖 -> backend\.venv\Scripts\python.exe -> PATH 中的 python(3).exe。
    /// </summary>
    public static string? FindPython(string backendDir, string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        string venv = Path.Combine(backendDir, ".venv", "Scripts", "python.exe");
        if (File.Exists(venv))
            return venv;

        return SearchOnPath("python.exe") ?? SearchOnPath("python3.exe");
    }

    /// <summary>查找系统级（非 venv）Python，用于创建虚拟环境。已过滤商店占位 stub。</summary>
    public static string? FindBasePython()
        => SearchOnPath("python.exe") ?? SearchOnPath("python3.exe");

    private static string? SearchOnPath(string exe)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (string part in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                string full = Path.Combine(part.Trim(), exe);
                if (File.Exists(full) && !IsStoreAliasStub(full))
                    return full;
            }
            catch
            {
                // 忽略无效路径项
            }
        }
        return null;
    }

    /// <summary>
    /// 识别 %LOCALAPPDATA%\Microsoft\WindowsApps 下的商店占位 stub（0 字节 reparse point）。
    /// 未安装 Python 时它也叫 python.exe，运行只会弹出 Microsoft Store 且不输出任何内容。
    /// </summary>
    private static bool IsStoreAliasStub(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null &&
                dir.TrimEnd(Path.DirectorySeparatorChar)
                   .EndsWith(Path.Combine("Microsoft", "WindowsApps"), StringComparison.OrdinalIgnoreCase))
            {
                var info = new FileInfo(path);
                return info.Length == 0 || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
            }
        }
        catch
        {
            // 读取属性失败时按可疑处理，避免选中不可用的解释器
            return true;
        }
        return false;
    }
}
