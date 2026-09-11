using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WhisperSubtitler.Services;
using WhisperSubtitler.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace WhisperSubtitler;

public sealed partial class MainWindow : Window
{
    private bool _initializingLanguageSelection;
    private static readonly string[] MediaFilters =
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv", ".wmv", ".m4v", ".mpg", ".mpeg",
        ".ts", ".m2ts", ".3gp", ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma", ".aiff"
    };

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        // 先创建 ViewModel，再在 XAML 初始化后设为根布局的 DataContext。
        ViewModel = new MainViewModel(DispatcherQueue.GetForCurrentThread());

        this.InitializeComponent();

        RootGrid.DataContext = ViewModel;
        Title = LocalizationService.Get("WindowTitle");
        InitializeLanguageSelection();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        this.SystemBackdrop = new MicaBackdrop();

        try
        {
            // AppWindow.Resize 使用物理像素，XAML 布局使用 DIP，必须按显示器缩放换算。
            nint hwnd = WindowNative.GetWindowHandle(this);
            double scale = GetDpiForWindow(hwnd) / 96.0;
            this.AppWindow?.Resize(new Windows.Graphics.SizeInt32(
                (int)(1080 * scale), (int)(760 * scale)));

            string icoPath = Path.Combine(AppContext.BaseDirectory, "Asset", "AppIcon.ico");
            if (File.Exists(icoPath))
                this.AppWindow?.SetIcon(icoPath);
        }
        catch { /* 某些环境下 AppWindow 可能不可用 */ }

        RootGrid.Loaded += OnRootLoaded;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.CheckBackendAsync();
    }

    // ---------- 选择文件 ----------

    private async void OnBrowseFilesClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.VideosLibrary
        };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        foreach (string ext in MediaFilters)
            picker.FileTypeFilter.Add(ext);

        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
            ViewModel.AddFiles(files.Select(f => f.Path));
    }

    private async void OnBrowseOutputFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            ViewModel.CustomOutputFolder = folder.Path;
    }

    // ---------- 拖放 ----------

    private void OnDropZoneDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (e.DragUIOverride is not null)
            {
                e.DragUIOverride.Caption = LocalizationService.Get("DragAddToQueue");
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsGlyphVisible = true;
            }
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void OnDropZoneDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.OfType<StorageFile>().Select(f => f.Path).ToList();
            if (paths.Count > 0)
                ViewModel.AddFiles(paths);
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ---------- 任务项操作 ----------

    private void OnRemoveJobClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsRunning) return;   // 转录进行中不允许移除
        if ((sender as FrameworkElement)?.DataContext is JobItem job)
            ViewModel.RemoveJobCommand.Execute(job);
    }

    private void OnOpenJobOutputClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is JobItem job &&
            !string.IsNullOrEmpty(job.OutputPath) && File.Exists(job.OutputPath))
        {
            // explorer 只认「/select,"路径"」这种引号写在参数内部的字符串形式：
            // 用 ArgumentList 会被 .NET 把整个参数加引号，explorer 解析不了会退化成打开「文档」。
            // Windows 文件名不允许半角引号，这里再剥离一次作为纵深防御，避免注入额外开关。
            string safePath = job.OutputPath.Replace("\"", "");
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{safePath}\"")
                {
                    UseShellExecute = true
                });
            }
            catch { /* 某些环境下资源管理器不可用时忽略 */ }
        }
    }

    private async void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        if (SettingsDialog.XamlRoot is null)
            SettingsDialog.XamlRoot = RootGrid.XamlRoot;
        if (ViewModel.BackendReady && ViewModel.ModelItems.Count == 0)
            _ = ViewModel.RefreshModelsCommand.ExecuteAsync(null);
        try { await SettingsDialog.ShowAsync(); }
        catch { /* 重复打开等场景忽略 */ }
    }

    private void InitializeLanguageSelection()
    {
        _initializingLanguageSelection = true;
        foreach (var item in AppLanguageComboBox.Items.OfType<Microsoft.UI.Xaml.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), ViewModel.UiLanguage, StringComparison.OrdinalIgnoreCase))
            {
                AppLanguageComboBox.SelectedItem = item;
                break;
            }
        }
        if (AppLanguageComboBox.SelectedIndex < 0)
            AppLanguageComboBox.SelectedIndex = 0;
        _initializingLanguageSelection = false;
    }

    private void OnAppLanguageSelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (_initializingLanguageSelection ||
            AppLanguageComboBox.SelectedItem is not Microsoft.UI.Xaml.Controls.ComboBoxItem item ||
            item.Tag is not string language ||
            string.Equals(language, ViewModel.UiLanguage, StringComparison.OrdinalIgnoreCase))
            return;

        App.ChangeLanguage(language);
    }

    private void OnOpenSetupClick(object sender, RoutedEventArgs e)
    {
        // 与后端检测使用同一路径（含用户覆盖），避免 setup 装到检测永远不看的目录。
        string? script = ViewModel.ResolvedScriptPath ?? BackendLocator.FindBackendScript(null);
        string? dir = script is not null ? Path.GetDirectoryName(script) : null;
        if (dir is null) return;

        string bat = Path.Combine(dir, "setup.bat");
        var psi = File.Exists(bat)
            ? new ProcessStartInfo(bat) { UseShellExecute = true, WorkingDirectory = dir }
            : new ProcessStartInfo(dir) { UseShellExecute = true };
        try { Process.Start(psi); } catch { /* 用户可手动打开 */ }
    }
}
