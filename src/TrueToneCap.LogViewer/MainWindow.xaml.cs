// TrueToneCap.LogViewer/MainWindow.xaml.cs
// 主窗口: 文件列表 + 日志实时查看 (tail) + 过滤/搜索/清理

using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TrueToneCap.LogViewer.Services;

namespace TrueToneCap.LogViewer;

/// <summary>日志文件列表项。</summary>
public sealed class LogFileItem
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public long Size { get; init; }
    public DateTime LastWrite { get; init; }
    public string Meta => $"{LastWrite:MM-dd HH:mm:ss} · {FormatSize(Size)}";

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F2} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F2} KB",
        _ => $"{bytes} B",
    };
}

public sealed partial class MainWindow : Window
{
    /// <summary>自动换行开关 (x:Bind)。</summary>
    public static TextWrapping MessageWrap => s_wrapEnabled ? TextWrapping.Wrap : TextWrapping.NoWrap;
    private static bool s_wrapEnabled = true;

    /// <summary>x:Bind 视图模型。</summary>
    public ObservableCollection<ParsedLogEntry> Entries { get; } = [];

    private string _logDir = "";
    private string _currentFile = "";
    private long _fileOffset;
    private readonly System.Threading.Timer _tailTimer;
    private int _tailRunning;
    private long _totalSize;

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = Entries; // 代码设置 (避免 Window x:Bind 兼容问题)

        // 系统标题栏 + Mica 背景 (现代化)
        Title = "TTC 日志查看器";
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var winId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(winId);
            appWindow.Title = "TTC 日志查看器";
            appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));

            // Mica 背景
            var presenter = appWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
            presenter?.SetBorderAndTitleBar(true, false);
        }
        catch { }

        // 默认目录: 程序目录下的 log/, 或父目录的 log/
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
        foreach (var c in new[]
        {
            Path.Combine(exeDir, "log"),
            Path.Combine(Path.GetDirectoryName(exeDir) ?? "", "log"),
        })
        {
            if (Directory.Exists(c)) { SetLogDirectory(c); break; }
        }
        if (_logDir.Length == 0)
            SetLogDirectory(Path.Combine(exeDir, "log"));

        // tail 定时器 (300ms)
        _tailTimer = new System.Threading.Timer(_ => TailTick(), null, 500, 300);
        Closed += (_, _) => _tailTimer.Dispose();
    }

    private void SetLogDirectory(string dir)
    {
        _logDir = dir;
        DirTxt.Text = $"日志目录: {_logDir}";
        RefreshFiles();
    }

    private void RefreshFiles()
    {
        var items = new List<LogFileItem>();
        long total = 0;
        if (Directory.Exists(_logDir))
        {
            foreach (var f in Directory.GetFiles(_logDir, "app_*.log"))
            {
                try
                {
                    var fi = new FileInfo(f);
                    total += fi.Length;
                    items.Add(new LogFileItem
                    {
                        Name = fi.Name,
                        FullPath = fi.FullName,
                        Size = fi.Length,
                        LastWrite = fi.LastWriteTime,
                    });
                }
                catch { }
            }
        }
        _totalSize = total;
        items.Sort((a, b) => b.LastWrite.CompareTo(a.LastWrite));

        var selected = FileList.SelectedItem as LogFileItem;
        FileList.ItemsSource = items;
        if (items.Count > 0)
        {
            var keep = items.FirstOrDefault(i => i.FullPath == selected?.FullPath) ?? items[0];
            FileList.SelectedItem = keep;
        }
        SizeTxt.Text = $"总大小: {LogFileItem.FormatSize(_totalSize)}";
    }

    private void OnFileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not LogFileItem item) return;
        OpenFile(item);
    }

    private void OpenFile(LogFileItem item)
    {
        _currentFile = item.FullPath;
        _fileOffset = 0;
        Entries.Clear();
        CountTxt.Text = "0 条";

        // 大文件从尾部截取最多 8MB
        if (item.Size > 8 * 1024 * 1024)
            _fileOffset = item.Size - 8 * 1024 * 1024;

        TailRead();
    }

    private void TailTick()
    {
        if (Interlocked.Exchange(ref _tailRunning, 1) != 0) return;
        try
        {
            if (_currentFile.Length == 0) return;

            var fi = new FileInfo(_currentFile);
            if (!fi.Exists)
            {
                DispatcherQueue.TryEnqueue(RefreshFiles);
                return;
            }
            if (fi.Length < _fileOffset) // 文件被截断/轮转 → 重开
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (FileList.SelectedItem is LogFileItem sel && sel.FullPath == _currentFile)
                        OpenFile(sel);
                    else RefreshFiles();
                });
                return;
            }
            if (fi.Length == _fileOffset) return;

            using var fs = new FileStream(_currentFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(_fileOffset, SeekOrigin.Begin);
            var buf = new byte[fi.Length - _fileOffset];
            int read = fs.Read(buf, 0, buf.Length);
            _fileOffset += read;
            if (read == 0) return;

            var text = System.Text.Encoding.UTF8.GetString(buf, 0, read);
            var lines = text.Split('\n');
            var parsed = new List<ParsedLogEntry>(lines.Length);

            ParsedLogEntry? pending = null;
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;

                var entry = LogParser.ParseLine(line);
                if (entry is not null)
                {
                    pending = entry;
                    parsed.Add(entry);
                }
                else if (LogParser.IsDetailLine(line) && pending is not null)
                {
                    pending.Details.Add(line.TrimStart());
                }
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                bool shouldAuto = AutoScrollSwitch.IsOn;
                foreach (var p in parsed)
                {
                    if (PassFilter(p)) Entries.Add(p);
                }
                CountTxt.Text = $"{Entries.Count} 条";
                if (shouldAuto && Entries.Count > 0)
                {
                    LogList.ScrollIntoView(Entries[^1]);
                }
            });
        }
        catch { }
        finally { Interlocked.Exchange(ref _tailRunning, 0); }
    }

    /// <summary>从文件头读取 (首次打开时也调用 TailRead 补全)。</summary>
    private void TailRead() => TailTick();

    private bool PassFilter(ParsedLogEntry e)
    {
        // 级别过滤
        if (LevelFilterCbo.SelectedIndex > 0)
        {
            var want = LevelFilterCbo.SelectedIndex switch
            {
                1 => LogLevel.Error,
                2 => LogLevel.Warning,
                3 => LogLevel.Info,
                4 => LogLevel.Debug,
                _ => LogLevel.Unknown,
            };
            if (e.Level != want) return false;
        }
        // 搜索
        var q = SearchTxt.Text;
        if (q.Length > 0)
        {
            if (!e.Message.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !e.Tag.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !e.Caller.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !e.Category.Contains(q, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        // 从当前文件重新应用过滤: 简单方式 — 重新加载当前文件
        if (FileList.SelectedItem is LogFileItem item && item.FullPath == _currentFile)
        {
            _fileOffset = 0;
            Entries.Clear();
            CountTxt.Text = "0 条";
            TailRead();
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshFiles();

    private void OnOpenDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");
            _ = PickFolderAsync(picker);
        }
        catch { }
    }

    private async System.Threading.Tasks.Task PickFolderAsync(Windows.Storage.Pickers.FolderPicker picker)
    {
        try
        {
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) SetLogDirectory(folder.Path);
        }
        catch { }
    }

    private void OnClearOldClick(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_logDir)) return;
        var cutoff = DateTime.Now.AddDays(-30);
        int deleted = 0;
        foreach (var f in Directory.GetFiles(_logDir, "app_*.log"))
        {
            try
            {
                if (new FileInfo(f).LastWriteTime < cutoff) { File.Delete(f); deleted++; }
            }
            catch { }
        }
        _ = ShowDialogAsync($"已删除 {deleted} 个过期日志文件", "清理完成");
        RefreshFiles();
    }

    private void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_logDir)) return;
        int deleted = 0;
        foreach (var f in Directory.GetFiles(_logDir, "app_*.log"))
        {
            try { File.Delete(f); deleted++; } catch { }
        }
        Entries.Clear();
        CountTxt.Text = "0 条";
        _currentFile = "";
        _ = ShowDialogAsync($"已删除 {deleted} 个日志文件", "清空完成");
        RefreshFiles();
    }

    private void OnAutoScrollToggled(object sender, RoutedEventArgs e) { }

    private void OnWrapToggled(object sender, RoutedEventArgs e)
    {
        s_wrapEnabled = WrapSwitch.IsOn;
        // 强制重新生成项 (x:Bind 静态属性 OneTime)
        var items = FileList.ItemsSource;
        if (FileList.SelectedItem is LogFileItem sel)
            OpenFile(sel);
    }

    private async System.Threading.Tasks.Task ShowDialogAsync(string content, string title)
    {
        try
        {
            var dlg = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "确定",
                XamlRoot = RootGrid.XamlRoot,
            };
            await dlg.ShowAsync();
        }
        catch { }
    }
}
