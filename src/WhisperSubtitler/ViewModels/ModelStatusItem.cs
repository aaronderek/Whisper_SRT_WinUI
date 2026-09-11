using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WhisperSubtitler.ViewModels;

/// <summary>设置面板里"模型管理"列表的一行：某个模型的本地缓存状态与操作。</summary>
public partial class ModelStatusItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Display { get; init; }

    [ObservableProperty] private string repo = "";
    [ObservableProperty] private bool downloaded;
    [ObservableProperty] private string sizeText = "";
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private bool busy;

    // 由 MainViewModel 注入，闭包捕获本条目
    public IAsyncRelayCommand? DownloadCommand { get; set; }
    public IAsyncRelayCommand? DeleteCommand { get; set; }
}
