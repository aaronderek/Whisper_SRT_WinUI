using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using WhisperSubtitler.Models;
using WhisperSubtitler.Services;

namespace WhisperSubtitler.ViewModels;

/// <summary>队列中的单个转录任务。</summary>
public partial class JobItem : ObservableObject
{
    public string InputPath { get; }

    public string FileName => Path.GetFileName(InputPath);

    public string FolderName => Path.GetDirectoryName(InputPath) ?? "";

    [ObservableProperty]
    private JobStatus status = JobStatus.Pending;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private string statusText = LocalizationService.Get("StatusWaiting");

    [ObservableProperty]
    private string? outputPath;

    [ObservableProperty]
    private string? errorText;

    public JobItem(string inputPath)
    {
        InputPath = inputPath;
    }

    public bool IsPendingLike =>
        Status is JobStatus.Pending or JobStatus.Failed or JobStatus.Canceled;

    partial void OnStatusChanged(JobStatus value) => OnPropertyChanged(nameof(IsPendingLike));
}
