using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using WhisperSubtitler.Models;
using WhisperSubtitler.Services;

namespace WhisperSubtitler.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv", ".wmv", ".m4v", ".mpg", ".mpeg",
        ".ts", ".m2ts", ".3gp", ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma", ".aiff"
    };

    private readonly DispatcherQueue _dispatcher;
    private readonly AppSettings _settings;
    private readonly List<string> _logLines = new();
    private bool _loaded;
    private bool _checking;

    private string? _scriptPath;
    private string? _pythonPath;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _envCts;   // 依赖安装 / 模型下载删除

    public ObservableCollection<JobItem> Jobs { get; } = new();
    public ObservableCollection<ModelOption> Models { get; } = new();
    public ObservableCollection<LanguageOption> Languages { get; } = new();
    public ObservableCollection<string> ComputeTypes { get; } = new()
    {
        "float16", "bfloat16", "float32", "int8_float16"
    };

    [ObservableProperty] private ModelOption? selectedModel;
    [ObservableProperty] private LanguageOption? selectedLanguage;
    [ObservableProperty] private bool translate;
    [ObservableProperty] private string modelNote = "";

    [ObservableProperty] private int outputModeIndex;
    [ObservableProperty] private string? customOutputFolder;

    [ObservableProperty] private string computeType = "float16";
    [ObservableProperty] private double beamSize = 5;
    [ObservableProperty] private double maxLineChars = 42;
    [ObservableProperty] private bool useVad = true;
    [ObservableProperty] private bool wordTimestamps;
    /// <summary>true = 只输出不带时间码的 .txt 文稿（不写 SRT）。</summary>
    [ObservableProperty] private bool transcriptOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(CanEditList))]
    [NotifyPropertyChangedFor(nameof(CanInstallDependencies))]
    private bool isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool backendReady;

    [ObservableProperty] private string backendStatusText = LocalizationService.Get("BackendChecking");
    [ObservableProperty] private string gpuStatusText = "";
    [ObservableProperty] private bool useGpu = true;

    // ---- 设置面板（依赖安装 / 模型管理）状态 ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallDependencies))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool isEnvBusy;
    [ObservableProperty] private string envStatusText = "";
    public ObservableCollection<ModelStatusItem> ModelItems { get; } = new();
    public bool CanInstallDependencies => !IsEnvBusy && !IsRunning;
    [ObservableProperty] private double overallProgress;
    [ObservableProperty] private string currentStatus = LocalizationService.Get("StatusIdle");
    [ObservableProperty] private string logText = "";

    /// <summary>当前生效的后端脚本路径（供"打开安装脚本"等 UI 操作复用，保证与检测一致）。</summary>
    public string? ResolvedScriptPath => _scriptPath;

    // IsEnvBusy：依赖安装/模型操作进行中不允许开始转录（venv 处于不完整状态会报 DLL 加载失败）
    public bool CanStart => BackendReady && !IsRunning && !IsEnvBusy && Jobs.Any(j => j.IsPendingLike);
    public bool CanCancel => IsRunning;
    public bool CanEditList => !IsRunning;
    public bool HasJobs => Jobs.Count > 0;
    public bool CustomOutputEnabled => OutputModeIndex == 1;
    public string UiLanguage => _settings.UiLanguage;

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _settings = SettingsService.Load();

        BuildModelOptions();
        BuildLanguageOptions();
        ApplySettings();

        Jobs.CollectionChanged += OnJobsChanged;
        _loaded = true;
    }

    // ---------- 初始化 ----------

    private void BuildModelOptions()
    {
        Models.Add(new ModelOption { Value = "large-v3", Display = LocalizationService.Get("ModelLargeDisplay"), Note = LocalizationService.Get("ModelLargeNote") });
        Models.Add(new ModelOption { Value = "large-v3-turbo", Display = LocalizationService.Get("ModelTurboDisplay"), Note = LocalizationService.Get("ModelTurboNote") });
        Models.Add(new ModelOption { Value = "distil-large-v3.5", Display = LocalizationService.Get("ModelDistilDisplay"), Note = LocalizationService.Get("ModelDistilNote") });
        Models.Add(new ModelOption { Value = "medium", Display = LocalizationService.Get("ModelMediumDisplay"), Note = LocalizationService.Get("ModelMediumNote") });
    }

    private void BuildLanguageOptions()
    {
        Languages.Add(new LanguageOption { Value = "en", Display = "English" });
        Languages.Add(new LanguageOption { Value = "auto", Display = LocalizationService.Get("LanguageAuto") });
        Languages.Add(new LanguageOption { Value = "zh", Display = "中文" });
        Languages.Add(new LanguageOption { Value = "ja", Display = "日本語" });
        Languages.Add(new LanguageOption { Value = "ko", Display = "한국어" });
        Languages.Add(new LanguageOption { Value = "es", Display = "Español" });
        Languages.Add(new LanguageOption { Value = "fr", Display = "Français" });
        Languages.Add(new LanguageOption { Value = "de", Display = "Deutsch" });
        Languages.Add(new LanguageOption { Value = "ru", Display = "Русский" });
    }

    private void ApplySettings()
    {
        SelectedModel = Models.FirstOrDefault(m => m.Value == _settings.Model) ?? Models[0];
        SelectedLanguage = Languages.FirstOrDefault(l => l.Value == _settings.Language) ?? Languages[0];
        Translate = _settings.Translate;
        OutputModeIndex = _settings.OutputModeIndex;
        CustomOutputFolder = _settings.CustomOutputFolder;
        ComputeType = _settings.ComputeType;
        BeamSize = _settings.BeamSize;
        MaxLineChars = _settings.MaxLineChars;
        UseVad = _settings.UseVad;
        WordTimestamps = _settings.WordTimestamps;
        TranscriptOnly = _settings.TranscriptOnly;
        UseGpu = !string.Equals(_settings.Device, "cpu", StringComparison.OrdinalIgnoreCase);
        ModelNote = SelectedModel?.Note ?? "";
    }

    // ---------- 文件队列 ----------

    public void AddFiles(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(Jobs.Select(j => j.InputPath), StringComparer.OrdinalIgnoreCase);
        foreach (string p in paths)
        {
            if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;
            if (!MediaExtensions.Contains(Path.GetExtension(p))) continue;
            if (!existing.Add(p)) continue;
            Jobs.Add(new JobItem(p));
        }
    }

    [RelayCommand]
    private void RemoveJob(JobItem? job)
    {
        if (job is null || job.Status == JobStatus.Running) return;
        Jobs.Remove(job);
    }

    [RelayCommand]
    private void Clear()
    {
        if (IsRunning) return;
        Jobs.Clear();
        OverallProgress = 0;
        CurrentStatus = LocalizationService.Get("StatusIdle");
    }

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RaiseStateChanged();

    // ---------- 转录 ----------

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning || IsEnvBusy || !BackendReady || _scriptPath is null || _pythonPath is null) return;

        // 固定本批次使用的路径，防止转录期间"重新检测"改写字段导致后续任务用错解释器。
        string scriptPath = _scriptPath;
        string pythonPath = _pythonPath;

        var pending = Jobs.Where(j => j.IsPendingLike).ToList();
        if (pending.Count == 0) return;

        _cts = new CancellationTokenSource();
        IsRunning = true;
        OverallProgress = 0;
        int total = pending.Count;
        int done = 0;

        foreach (var job in pending)
        {
            if (!Jobs.Contains(job)) { done++; continue; }        // 运行中被移除的任务跳过
            if (_cts.IsCancellationRequested) { MarkCanceled(job); done++; continue; }

            job.Status = JobStatus.Running;
            job.Progress = 0;
            job.ErrorText = null;
            job.StatusText = LocalizationService.Get("StatusPreparing");
            CurrentStatus = LocalizationService.Format("StatusTranscribingFile", job.FileName);

            int captured = done;
            var request = BuildRequest(job, pythonPath, scriptPath);

            try
            {
                var result = await BackendRunner.RunAsync(
                    request,
                    onProgress: (cur, tot, pct) => Dispatch(() =>
                    {
                        job.Progress = pct;
                        job.StatusText = LocalizationService.Format("StatusTranscribingProgress", pct);
                        OverallProgress = (captured + pct / 100.0) / total * 100.0;
                    }),
                    onStatus: s => Dispatch(() =>
                    {
                        job.StatusText = s;
                        CurrentStatus = s;
                    }),
                    onLog: s => Dispatch(() => AppendLog(s)),
                    _cts.Token);

                Dispatch(() => ApplyResult(job, result));
            }
            catch (Exception ex)
            {
                Dispatch(() =>
                {
                    job.Status = JobStatus.Failed;
                    job.StatusText = LocalizationService.Get("StatusFailed");
                    job.ErrorText = ex.Message;
                    AppendLog(LocalizationService.Format("LogException", ex.Message));
                });
            }

            done++;
            Dispatch(() => OverallProgress = (double)done / total * 100.0);
        }

        IsRunning = false;
        bool canceled = _cts?.IsCancellationRequested == true;
        _cts?.Dispose();
        _cts = null;
        CurrentStatus = canceled ? LocalizationService.Get("StatusCanceled") : LocalizationService.Get("StatusAllFinished");
        RaiseStateChanged();
    }

    private void ApplyResult(JobItem job, TranscribeResult result)
    {
        if (result.Canceled)
        {
            MarkCanceled(job);
        }
        else if (result.Success)
        {
            job.Status = JobStatus.Completed;
            job.Progress = 100;
            job.OutputPath = result.OutputPath;
            // 文稿模式后端回报的是段落数，字幕模式回报的是字幕条数
            bool transcript = result.Paragraphs > 0;
            job.StatusText = LocalizationService.Format(
                transcript
                    ? (result.SpeedX > 0 ? "StatusCompleteTranscriptSpeed" : "StatusCompleteTranscript")
                    : (result.SpeedX > 0 ? "StatusCompleteSpeed" : "StatusComplete"),
                transcript ? result.Paragraphs : result.Segments,
                result.SpeedX);
        }
        else
        {
            job.Status = JobStatus.Failed;
            job.StatusText = LocalizationService.Get("StatusFailed");
            job.ErrorText = result.Error;
            AppendLog(LocalizationService.Format("LogError", result.Error));
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        CurrentStatus = LocalizationService.Get("StatusCanceling");
    }

    private void MarkCanceled(JobItem job)
    {
        job.Status = JobStatus.Canceled;
        job.StatusText = LocalizationService.Get("StatusCanceled");
    }

    private TranscribeRequest BuildRequest(JobItem job, string pythonPath, string scriptPath)
    {
        // 开关决定产物：文稿模式写同名 .txt（无时间码），否则写 .srt 字幕
        string extension = TranscriptOnly ? ".txt" : ".srt";
        string output = OutputModeIndex == 1 && !string.IsNullOrWhiteSpace(CustomOutputFolder)
            ? Path.Combine(CustomOutputFolder!, Path.GetFileNameWithoutExtension(job.InputPath) + extension)
            : Path.ChangeExtension(job.InputPath, extension);

        return new TranscribeRequest
        {
            PythonPath = pythonPath,
            ScriptPath = scriptPath,
            Input = job.InputPath,
            Output = output,
            Model = SelectedModel?.Value ?? "large-v3",
            Language = SelectedLanguage?.Value ?? "en",
            Task = Translate ? "translate" : "transcribe",
            Device = UseGpu ? "cuda" : "cpu",
            ComputeType = ComputeType,
            BeamSize = (int)Math.Round(BeamSize),
            MaxLineChars = (int)Math.Round(MaxLineChars),
            UseVad = UseVad,
            WordTimestamps = WordTimestamps,
            CpuFallback = _settings.CpuFallback,
            Format = TranscriptOnly ? "txt" : "srt"
        };
    }

    // ---------- 后端检测 ----------

    [RelayCommand]
    private async Task RecheckBackendAsync() => await CheckBackendAsync();

    public async Task CheckBackendAsync()
    {
        if (_checking || IsRunning) return;   // 转录中不允许改写 _scriptPath/_pythonPath
        _checking = true;
        try
        {
        BackendStatusText = LocalizationService.Get("BackendChecking");
        GpuStatusText = "";

        _scriptPath = BackendLocator.FindBackendScript(_settings.BackendScriptOverride);
        if (_scriptPath is null)
        {
            BackendReady = false;
            BackendStatusText = LocalizationService.Get("BackendScriptMissing");
            return;
        }

        string backendDir = Path.GetDirectoryName(_scriptPath)!;
        _pythonPath = BackendLocator.FindPython(backendDir, _settings.PythonPathOverride);
        if (_pythonPath is null)
        {
            BackendReady = false;
            BackendStatusText = LocalizationService.Get("PythonMissing");
            return;
        }

        try
        {
            var result = await BackendRunner.CheckAsync(_pythonPath, _scriptPath, CancellationToken.None);
            if (!result.Ok)
            {
                BackendReady = false;
                BackendStatusText = LocalizationService.Format("DependenciesNotReady", result.Error ?? LocalizationService.Get("Unknown"));
            }
            else if (result.CudaDeviceCount > 0)
            {
                BackendReady = true;
                GpuStatusText = LocalizationService.Format("GpuDevice", result.GpuName ?? "CUDA");
                BackendStatusText = LocalizationService.Format("BackendReady", result.FasterWhisper, result.Ctranslate2);
            }
            else
            {
                BackendReady = true;
                GpuStatusText = LocalizationService.Get("CudaNotDetected");
                BackendStatusText = LocalizationService.Format("BackendReadyCpu", result.FasterWhisper);
            }

            if (BackendReady)
                _ = RefreshModelsAsync();   // 后台刷新设置面板的模型列表
        }
        catch (Exception ex)
        {
            BackendReady = false;
            BackendStatusText = LocalizationService.Format("BackendCheckFailed", ex.Message);
        }
        }
        finally { _checking = false; }
    }

    // ---------- 设置面板：依赖安装 ----------

    [RelayCommand]
    private async Task InstallDependenciesAsync()
    {
        if (IsEnvBusy || IsRunning) return;

        // 后端脚本可能还没定位到（例如首次安装），重新解析一次
        _scriptPath ??= BackendLocator.FindBackendScript(_settings.BackendScriptOverride);
        if (_scriptPath is null)
        {
            EnvStatusText = LocalizationService.Get("InstallBackendMissing");
            return;
        }

        string backendDir = Path.GetDirectoryName(_scriptPath)!;
        _envCts = new CancellationTokenSource();
        IsEnvBusy = true;
        EnvStatusText = LocalizationService.Get("InstallStarting");
        try
        {
            var (ok, error) = await BackendInstaller.InstallAsync(
                backendDir,
                onLog: l => Dispatch(() => AppendLog(l)),
                onStatus: s => Dispatch(() => EnvStatusText = s),
                _envCts.Token);

            if (ok)
            {
                EnvStatusText = LocalizationService.Get("InstallCompleteRechecking");
                await CheckBackendAsync();
                EnvStatusText = BackendReady ? LocalizationService.Get("DependenciesReady") : LocalizationService.Format("InstallCompleteCheckFailed", BackendStatusText);
                if (BackendReady)
                    await RefreshModelsAsync();
            }
            else
            {
                EnvStatusText = error ?? LocalizationService.Get("InstallFailed");
                AppendLog(LocalizationService.Format("LogInstallFailed", error));
            }
        }
        catch (OperationCanceledException)
        {
            EnvStatusText = LocalizationService.Get("InstallCanceled");
        }
        catch (Exception ex)
        {
            EnvStatusText = LocalizationService.Format("InstallException", ex.Message);
            AppendLog(LocalizationService.Format("LogInstallException", ex.Message));
        }
        finally
        {
            _envCts?.Dispose();
            _envCts = null;
            IsEnvBusy = false;
        }
    }

    [RelayCommand]
    private void CancelEnvOp() => _envCts?.Cancel();

    // ---------- 设置面板：模型管理 ----------

    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        if (_pythonPath is null || _scriptPath is null || !BackendReady)
        {
            ModelItems.Clear();
            return;
        }

        var (models, error) = await BackendRunner.GetModelsAsync(_pythonPath, _scriptPath, CancellationToken.None);
        if (models is null)
        {
            AppendLog(LocalizationService.Format("LogModelsFailed", error));
            return;
        }

        ModelItems.Clear();
        foreach (var m in models)
        {
            var option = Models.FirstOrDefault(o => o.Value == m.Name);
            var item = new ModelStatusItem
            {
                Name = m.Name,
                Display = option?.Display ?? m.Name,
                Repo = m.Repo,
                Downloaded = m.Downloaded,
                SizeText = m.Downloaded ? FormatSize(m.SizeBytes) : LocalizationService.Get("NotDownloaded"),
            };
            item.DownloadCommand = new AsyncRelayCommand(() => DownloadModelAsync(item));
            item.DeleteCommand = new AsyncRelayCommand(() => DeleteModelAsync(item));
            ModelItems.Add(item);
        }
    }

    private async Task DownloadModelAsync(ModelStatusItem item)
    {
        if (IsEnvBusy || _pythonPath is null || _scriptPath is null) return;
        _envCts = new CancellationTokenSource();
        IsEnvBusy = true;
        item.Busy = true;
        item.StatusText = LocalizationService.Get("ModelDownloading");
        try
        {
            var (ok, error) = await BackendRunner.RunToolAsync(
                _pythonPath, _scriptPath, new[] { "--download-model", item.Name },
                onStatus: s => Dispatch(() => item.StatusText = s),
                onLog: l => Dispatch(() => AppendLog(l)),
                _envCts.Token);
            item.StatusText = ok ? "" : LocalizationService.Format("ModelDownloadFailed", error);
            if (!ok && error is not null) AppendLog(LocalizationService.Format("LogModelDownload", error));
        }
        finally
        {
            item.Busy = false;
            _envCts?.Dispose();
            _envCts = null;
            IsEnvBusy = false;
            await RefreshModelsAsync();
        }
    }

    private async Task DeleteModelAsync(ModelStatusItem item)
    {
        if (IsEnvBusy || _pythonPath is null || _scriptPath is null) return;
        IsEnvBusy = true;
        item.Busy = true;
        item.StatusText = LocalizationService.Get("ModelDeleting");
        try
        {
            var (ok, error) = await BackendRunner.RunToolAsync(
                _pythonPath, _scriptPath, new[] { "--delete-model", item.Name },
                onStatus: null,
                onLog: l => Dispatch(() => AppendLog(l)),
                CancellationToken.None);
            item.StatusText = ok ? "" : LocalizationService.Format("ModelDeleteFailed", error);
        }
        finally
        {
            item.Busy = false;
            IsEnvBusy = false;
            await RefreshModelsAsync();
        }
    }

    [RelayCommand]
    private void OpenModelCache()
    {
        try
        {
            string hub = Environment.GetEnvironmentVariable("HF_HOME") is { Length: > 0 } hf
                ? Path.Combine(hf, "hub")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                               ".cache", "huggingface", "hub");
            if (Directory.Exists(hub))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(hub) { UseShellExecute = true });
        }
        catch { /* 打不开就算了 */ }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        > 0 => $"{bytes / 1024.0:0.0} KB",
        _ => "0",
    };

    // ---------- 工具 ----------

    private void Dispatch(Action action)
    {
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    private void AppendLog(string line)
    {
        _logLines.Add(line);
        if (_logLines.Count > 300)
            _logLines.RemoveRange(0, _logLines.Count - 300);
        LogText = string.Join(Environment.NewLine, _logLines);
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanEditList));
        OnPropertyChanged(nameof(HasJobs));
    }

    // ---------- 设置持久化 ----------

    private void PersistIfLoaded()
    {
        if (!_loaded) return;
        _settings.Model = SelectedModel?.Value ?? "large-v3";
        _settings.Language = SelectedLanguage?.Value ?? "en";
        _settings.Translate = Translate;
        _settings.OutputModeIndex = OutputModeIndex;
        _settings.CustomOutputFolder = CustomOutputFolder;
        _settings.ComputeType = ComputeType;
        _settings.BeamSize = (int)Math.Round(BeamSize);
        _settings.MaxLineChars = (int)Math.Round(MaxLineChars);
        _settings.UseVad = UseVad;
        _settings.WordTimestamps = WordTimestamps;
        _settings.TranscriptOnly = TranscriptOnly;
        _settings.Device = UseGpu ? "cuda" : "cpu";
        SettingsService.Save(_settings);
    }

    partial void OnSelectedModelChanged(ModelOption? value)
    {
        ModelNote = value?.Note ?? "";
        PersistIfLoaded();
    }

    partial void OnSelectedLanguageChanged(LanguageOption? value) => PersistIfLoaded();
    partial void OnUseGpuChanged(bool value) => PersistIfLoaded();
    partial void OnTranslateChanged(bool value) => PersistIfLoaded();
    partial void OnCustomOutputFolderChanged(string? value) => PersistIfLoaded();
    partial void OnComputeTypeChanged(string value) => PersistIfLoaded();
    partial void OnBeamSizeChanged(double value) => PersistIfLoaded();
    partial void OnMaxLineCharsChanged(double value) => PersistIfLoaded();
    partial void OnUseVadChanged(bool value) => PersistIfLoaded();
    partial void OnWordTimestampsChanged(bool value) => PersistIfLoaded();
    partial void OnTranscriptOnlyChanged(bool value) => PersistIfLoaded();

    partial void OnOutputModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CustomOutputEnabled));
        PersistIfLoaded();
    }
}
