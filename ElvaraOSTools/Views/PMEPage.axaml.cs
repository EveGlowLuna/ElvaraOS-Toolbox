using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace ElvaraOSTools.Views;

public partial class PMEPage : UserControl
{
    private List<string> _orphans   = [];
    private readonly ObservableCollection<string> _orphanItems = [];
    private List<string> _explicit  = [];
    private readonly ObservableCollection<string> _explicitItems = [];
    private List<(string display, string path)> _downgradeEntries = [];
    private readonly ObservableCollection<string> _downgradeItems = [];

    public PMEPage()
    {
        InitializeComponent();
        Loaded += (_, _) => WireAll();
    }

    private void WireAll()
    {
        var tabs = Get<TabControl>("MainTabs");
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source is TabControl) OnTabChanged(tabs.SelectedIndex);
        };

        // Orphans
        Get<Button>("BtnDeleteOrphans").Click += (_, _) => _ = DeleteOrphansAsync();

        // Owner
        Get<Button>("BtnOwnerQuery").Click += (_, _) => _ = QueryOwnerAsync();
        Get<TextBox>("OwnerInput").KeyDown += (_, e) =>
        { if (e.Key == Avalonia.Input.Key.Return) _ = QueryOwnerAsync(); };

        // Downgrade
        Get<Button>("BtnDowngradeFind").Click += (_, _) => _ = FindDowngradeAsync();
        Get<Button>("BtnDowngradeExec").Click += (_, _) => _ = ExecDowngradeAsync();
        Get<TextBox>("DowngradeInput").KeyDown += (_, e) =>
        { if (e.Key == Avalonia.Input.Key.Return) _ = FindDowngradeAsync(); };

        // Explicit
        Get<Button>("BtnExportList").Click += (_, _) => _ = ExportExplicitAsync();
        Get<Button>("BtnImportList").Click += (_, _) => _ = ImportExplicitAsync();

        // PkgInfo
        Get<Button>("BtnPkgInfoQuery").Click += (_, _) => _ = QueryPkgInfoAsync();
        Get<TextBox>("PkgInfoInput").KeyDown += (_, e) =>
        { if (e.Key == Avalonia.Input.Key.Return) _ = QueryPkgInfoAsync(); };

        // Mirror
        Get<Button>("BtnMirrorReload").Click     += (_, _) => _ = LoadMirrorlistAsync();
        Get<Button>("BtnMirrorReflector").Click  += (_, _) => _ = RunReflectorAsync();
        Get<Button>("BtnMirrorSave").Click       += (_, _) => _ = SaveMirrorlistAsync();
        Get<Button>("BtnMirrorBackup").Click     += (_, _) => _ = BackupMirrorlistAsync();

        // Load first tab
        Get<ListBox>("ResultList").ItemsSource   = _orphanItems;
        Get<ListBox>("ExplicitList").ItemsSource = _explicitItems;
        Get<ListBox>("DowngradeList").ItemsSource = _downgradeItems;
        OnTabChanged(0);
    }

    private void OnTabChanged(int idx)
    {
        switch (idx)
        {
            case 0: _ = LoadOrphansAsync(); break;
            case 1: _ = RunIntegrityAsync(); break;
            case 4: _ = LoadExplicitAsync(); break;
            case 6: _ = LoadMirrorlistAsync(); break;
        }
    }

    // Orphans

    private async Task LoadOrphansAsync()
    {
        SetText("OrphanStatus", "正在扫描孤儿包…");
        var output = await RunCaptureAsync("pacman", "-Qtdq");
        _orphans = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        Dispatcher.UIThread.Post(() =>
        {
            _orphanItems.Clear();
            foreach (var p in _orphans) _orphanItems.Add(p);
            Get<Button>("BtnDeleteOrphans").IsVisible = _orphans.Count > 0;
            SetText("OrphanStatus", _orphans.Count == 0 ? "没有孤儿包 ✓" : $"找到 {_orphans.Count} 个孤儿包，选中后点击删除");
        });
    }

    private async Task DeleteOrphansAsync()
    {
        var list = Get<ListBox>("ResultList");
        var selected = list.SelectedItems?.Cast<string>().ToList() ?? [];
        if (selected.Count == 0) { SetText("OrphanStatus", "请先选中要删除的包"); return; }
        SetText("OrphanStatus", $"正在删除 {selected.Count} 个包…");
        await RunElevatedStreamAsync(string.Join("\n", Get<TextBlock>("OrphanStatus").Text ?? ""),
            $"pacman -Rns {string.Join(" ", selected)}",
            t => SetText("OrphanStatus", t));
        await LoadOrphansAsync();
    }

    // Integrity

    private async Task RunIntegrityAsync()
    {
        SetText("IntegrityStatus", "正在运行 pacman -Qkk，请稍候…");
        var sb = new StringBuilder();
        var lck = new object();
        await StreamAsync("pacman", "-Qkk", line =>
        {
            string snapshot;
            lock (lck) { sb.AppendLine(line); snapshot = sb.ToString(); }
            Dispatcher.UIThread.Post(
                () => Get<TextBlock>("IntegrityText").Text = snapshot,
                DispatcherPriority.Background);
        });
        SetText("IntegrityStatus", "检查完成");
    }

    // Owner

    private async Task QueryOwnerAsync()
    {
        var path = Get<TextBox>("OwnerInput").Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(path)) return;
        SetText("OwnerStatus", "查询中…");
        var result = await RunCaptureAsync("pacman", $"-Qo {path}");
        Dispatcher.UIThread.Post(() => Get<TextBlock>("OwnerText").Text =
            string.IsNullOrWhiteSpace(result) ? $"未找到归属：{path}" : result);
        SetText("OwnerStatus", "完成");
    }

    // Downgrade

    private async Task FindDowngradeAsync()
    {
        var pkg = Get<TextBox>("DowngradeInput").Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(pkg)) return;
        SetText("DowngradeStatus", "正在扫描 /var/cache/pacman/pkg/…");
        _downgradeEntries = [];
        await Task.Run(() =>
        {
            const string dir = "/var/cache/pacman/pkg/";
            if (!Directory.Exists(dir)) return;
            _downgradeEntries = Directory.GetFiles(dir, $"{pkg}-*.pkg.tar.*")
                .OrderByDescending(File.GetLastWriteTime)
                .Select(f => (Path.GetFileName(f), f))
                .ToList();
        });
        Dispatcher.UIThread.Post(() =>
        {
            _downgradeItems.Clear();
            foreach (var e in _downgradeEntries) _downgradeItems.Add(e.display);
            Get<Button>("BtnDowngradeExec").IsVisible = _downgradeEntries.Count > 0;
            SetText("DowngradeStatus", _downgradeEntries.Count == 0
                ? $"缓存中没有 {pkg} 的旧版本"
                : $"找到 {_downgradeEntries.Count} 个版本，选中后点击降级");
        });
    }

    private async Task ExecDowngradeAsync()
    {
        var idx = Get<ListBox>("DowngradeList").SelectedIndex;
        if (idx < 0 || idx >= _downgradeEntries.Count) { SetText("DowngradeStatus", "请先选中一个版本"); return; }
        var (display, path) = _downgradeEntries[idx];
        SetText("DowngradeStatus", $"正在降级到 {display}…");
        var sb = new StringBuilder();
        await RunElevatedStreamAsync("", $"pacman -U {path}",
            t => { sb.AppendLine(t); });
        SetText("DowngradeStatus", "降级完成");
    }

    // Explicit

    private async Task LoadExplicitAsync()
    {
        SetText("ExplicitStatus", "正在获取显式安装包列表…");
        var output = await RunCaptureAsync("pacman", "-Qqe");
        _explicit = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        Dispatcher.UIThread.Post(() =>
        {
            _explicitItems.Clear();
            foreach (var p in _explicit) _explicitItems.Add(p);
            SetText("ExplicitStatus", $"共 {_explicit.Count} 个显式安装的包");
        });
    }

    private async Task ExportExplicitAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出包列表", SuggestedFileName = "pkglist.txt",
            FileTypeChoices = [new FilePickerFileType("文本文件") { Patterns = ["*.txt"] }]
        });
        if (file == null) return;
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        foreach (var p in _explicit) await writer.WriteLineAsync(p);
        SetText("ExplicitStatus", $"已导出到 {file.Name}");
    }

    private async Task ImportExplicitAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入包列表", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("文本文件") { Patterns = ["*.txt"] }]
        });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        var pkgs = (await reader.ReadToEndAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (pkgs.Count == 0) return;
        SetText("ExplicitStatus", $"准备安装 {pkgs.Count} 个包…");
        var sb = new StringBuilder();
        await RunElevatedStreamAsync("", $"pacman -S --needed {string.Join(" ", pkgs)}",
            t => { sb.AppendLine(t); });
        SetText("ExplicitStatus", "安装完成");
    }

    // PkgInfo

    private async Task QueryPkgInfoAsync()
    {
        var pkg = Get<TextBox>("PkgInfoInput").Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(pkg)) return;
        SetText("PkgInfoStatus", "查询中…");
        var info = await RunCaptureAsync("pacman", $"-Qi {pkg}");
        if (string.IsNullOrWhiteSpace(info))
            info = await RunCaptureAsync("pacman", $"-Si {pkg}");
        Dispatcher.UIThread.Post(() => Get<TextBlock>("PkgInfoText").Text =
            string.IsNullOrWhiteSpace(info) ? $"未找到包：{pkg}" : info);
        SetText("PkgInfoStatus", "完成");
    }

    // Mirror

    private const string MirrorlistPath = "/etc/pacman.d/mirrorlist";

    private async Task LoadMirrorlistAsync()
    {
        SetText("MirrorStatus", "正在读取 mirrorlist…");
        try
        {
            var content = await File.ReadAllTextAsync(MirrorlistPath);
            Dispatcher.UIThread.Post(() => Get<TextBox>("MirrorlistEditor").Text = content);
            SetText("MirrorStatus", $"已加载 {MirrorlistPath}");
        }
        catch (Exception ex)
        {
            SetText("MirrorStatus", $"读取失败：{ex.Message}");
        }
    }

    private async Task RunReflectorAsync()
    {
        SetText("MirrorStatus", "正在运行 reflector，请稍候（可能需要 1-2 分钟）…");
        var sb = new StringBuilder();
        const string cmd = "reflector -a 12 -c cn -f 10 --sort rate --verbose --save /etc/pacman.d/mirrorlist";
        await RunElevatedStreamAsync("", cmd, line =>
            Dispatcher.UIThread.Post(() => { sb.AppendLine(line); Get<TextBox>("MirrorlistEditor").Text = sb.ToString(); }));
        // Reload the saved mirrorlist after reflector finishes
        await LoadMirrorlistAsync();
        SetText("MirrorStatus", "reflector 配置完成，mirrorlist 已更新");
    }

    private async Task SaveMirrorlistAsync()
    {
        var content = Get<TextBox>("MirrorlistEditor").Text ?? "";
        if (string.IsNullOrWhiteSpace(content)) return;
        SetText("MirrorStatus", "正在保存…");
        var tmp = Path.GetTempFileName();
        await File.WriteAllTextAsync(tmp, content);
        await RunElevatedStreamAsync("", $"cp {tmp} {MirrorlistPath} && rm {tmp}", _ => { });
        SetText("MirrorStatus", "已保存到 /etc/pacman.d/mirrorlist");
    }

    private async Task BackupMirrorlistAsync()
    {
        var backup = $"{MirrorlistPath}.bak.{DateTime.Now:yyyyMMdd_HHmmss}";
        SetText("MirrorStatus", $"正在备份到 {backup}…");
        await RunElevatedStreamAsync("", $"cp {MirrorlistPath} {backup}", _ => { });
        SetText("MirrorStatus", $"已备份到 {backup}");
    }

    // Helpers

    private static async Task StreamAsync(string exe, string args, Action<string> onLine)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        await Task.Run(async () =>
        {
            var proc = Process.Start(psi); if (proc == null) return;
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) onLine(e.Data); };
            proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
            await proc.WaitForExitAsync();
        });
    }

    private static async Task RunElevatedStreamAsync(string _, string command, Action<string> onLine)
    {
        var psi = new ProcessStartInfo("pkexec", $"sh -c \"{command}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        await Task.Run(async () =>
        {
            var proc = Process.Start(psi); if (proc == null) return;
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) onLine(e.Data); };
            proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
            await proc.WaitForExitAsync();
        });
    }

    private static async Task<string> RunCaptureAsync(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            var proc = Process.Start(psi); if (proc == null) return "";
            var o = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync(); return o;
        }
        catch { return ""; }
    }

    private void SetText(string name, string text) =>
        Dispatcher.UIThread.Post(() => Get<TextBlock>(name).Text = text);

    private T Get<T>(string name) where T : Control =>
        this.FindControl<T>(name) ?? throw new InvalidOperationException($"{name} not found");
}
