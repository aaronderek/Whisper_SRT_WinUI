namespace WhisperSubtitler.Models;

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Canceled
}

/// <summary>Whisper 模型下拉项。</summary>
public sealed class ModelOption
{
    public string Display { get; init; } = "";
    public string Value { get; init; } = "";
    public string Note { get; init; } = "";
    public override string ToString() => Display;
}

/// <summary>语言下拉项。</summary>
public sealed class LanguageOption
{
    public string Display { get; init; } = "";
    public string Value { get; init; } = "";
    public override string ToString() => Display;
}

/// <summary>持久化到磁盘的应用设置。</summary>
public sealed class AppSettings
{
    public string UiLanguage { get; set; } = "system";
    public string Model { get; set; } = "large-v3";
    public string Language { get; set; } = "en";
    public bool Translate { get; set; } = false;
    public int OutputModeIndex { get; set; } = 0; // 0 = 与源文件同目录, 1 = 自定义
    public string? CustomOutputFolder { get; set; }
    public string ComputeType { get; set; } = "float16";
    public int BeamSize { get; set; } = 5;
    public int MaxLineChars { get; set; } = 42;
    public bool UseVad { get; set; } = true;
    public bool WordTimestamps { get; set; } = false;
    public bool CpuFallback { get; set; } = true;
    /// <summary>true = 只输出不带时间码的 .txt 文稿，false = 输出 .srt 字幕。</summary>
    public bool TranscriptOnly { get; set; } = false;
    public string Device { get; set; } = "cuda";   // cuda / cpu

    // 后端定位覆盖（可选，留空则自动探测）
    public string? BackendScriptOverride { get; set; }
    public string? PythonPathOverride { get; set; }
}
