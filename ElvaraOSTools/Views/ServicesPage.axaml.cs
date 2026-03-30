using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace ElvaraOSTools.Views;

public class ServiceItem
{
    public string Name        { get; set; } = "";
    public string ActiveState { get; set; } = "";
    public string SubState    { get; set; } = "";
    public string LoadState   { get; set; } = "";
    public string Description { get; set; } = "";

    public string StatusDot => ActiveState switch
    {
        "active"   => "●",
        "failed"   => "●",
        "inactive" => "○",
        _          => "·"
    };

    public IBrush StatusColor => ActiveState switch
    {
        "active"   => new SolidColorBrush(Color.Parse("#5B9BD5")),
        "failed"   => new SolidColorBrush(Color.Parse("#E05252")),
        "inactive" => new SolidColorBrush(Color.Parse("#888888")),
        _          => new SolidColorBrush(Color.Parse("#666666"))
    };
}

public partial class ServicesPage : UserControl
{
    private readonly ObservableCollection<ServiceItem> _allServices = [];
    private readonly ObservableCollection<ServiceItem> _filtered   = [];
    private ServiceItem? _selected;

    // Cache timestamp
    private DateTime _lastLoad = DateTime.MinValue;

    public ServicesPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            WireControls();
            _ = LoadServicesAsync();
        };
    }

    private void WireControls()
    {
        Get<Button>("BtnRefresh").Click += (_, _) => _ = LoadServicesAsync(force: true);
        Get<Button>("BtnBlame").Click   += (_, _) => _ = ShowBlameAsync();

        Get<ComboBox>("FilterBox").SelectionChanged += (_, _) => ApplyFilter();
        Get<TextBox>("SearchBox").TextChanged       += (_, _) => ApplyFilter();

        var list = Get<ListBox>("ServiceList");
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ServiceItem svc)
                SelectService(svc);
        };

        Get<Button>("BtnStart").Click   += (_, _) => _ = ServiceActionAsync("start");
        Get<Button>("BtnStop").Click    += (_, _) => _ = ServiceActionAsync("stop");
        Get<Button>("BtnRestart").Click += (_, _) => _ = ServiceActionAsync("restart");
        Get<Button>("BtnEnable").Click  += (_, _) => _ = ServiceActionAsync("enable");
        Get<Button>("BtnDisable").Click += (_, _) => _ = ServiceActionAsync("disable");
        Get<Button>("BtnStatus").Click  += (_, _) => _ = ShowServiceLogAsync();
    }

    // ── Load services ─────────────────────────────────────────────────────────

    private async Task LoadServicesAsync(bool force = false)
    {
        // 30-second cache
        if (!force && (DateTime.Now - _lastLoad).TotalSeconds < 30 && _allServices.Count > 0)
        {
            ApplyFilter();
            return;
        }

        SetListStatus("正在加载服务列表…");

        var output = await RunCaptureAsync(
            "systemctl", "list-units --type=service --all --no-legend --no-pager");

        var items = new List<ServiceItem>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Format: [●] name.service  load  active  sub  description
            var trimmed = line.TrimStart('●', ' ', '\t');
            var parts   = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;

            items.Add(new ServiceItem
            {
                Name        = parts[0],
                LoadState   = parts.Length > 1 ? parts[1] : "",
                ActiveState = parts.Length > 2 ? parts[2] : "",
                SubState    = parts.Length > 3 ? parts[3] : "",
                Description = parts.Length > 4 ? string.Join(" ", parts[4..]) : ""
            });
        }

        _lastLoad = DateTime.Now;

        Dispatcher.UIThread.Post(() =>
        {
            _allServices.Clear();
            foreach (var i in items) _allServices.Add(i);
            ApplyFilter();
        });
    }

    private void ApplyFilter()
    {
        var filterBox  = Get<ComboBox>("FilterBox");
        var searchBox  = Get<TextBox>("SearchBox");
        var filterText = (filterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部";
        var search     = searchBox.Text?.Trim().ToLower() ?? "";

        var result = _allServices.AsEnumerable();

        if (filterText != "全部")
            result = result.Where(s =>
                s.ActiveState.Equals(filterText, StringComparison.OrdinalIgnoreCase) ||
                s.LoadState.Equals(filterText, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(search))
            result = result.Where(s => s.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

        var list = result.ToList();
        Get<ListBox>("ServiceList").ItemsSource = list;
        SetListStatus($"{list.Count} 个服务");
        Get<TextBlock>("CountLabel").Text = $"共 {_allServices.Count} 个";
    }

    // ── Select service ────────────────────────────────────────────────────────

    private void SelectService(ServiceItem svc)
    {
        _selected = svc;
        Get<TextBlock>("DetailName").Text = $"{svc.Name}\n{svc.ActiveState} ({svc.SubState})  {svc.Description}";
        Get<TextBlock>("LogText").Text    = "点击「状态」查看日志";

        // Enable all action buttons
        foreach (var name in new[] { "BtnStart", "BtnStop", "BtnRestart", "BtnEnable", "BtnDisable", "BtnStatus" })
            Get<Button>(name).IsEnabled = true;
    }

    // ── Service actions ───────────────────────────────────────────────────────

    private async Task ServiceActionAsync(string action)
    {
        if (_selected == null) return;
        var svcName = _selected.Name;
        SetLog($"正在执行 systemctl {action} {svcName}…");

        await RunElevatedStreamAsync($"systemctl {action} {svcName}");

        // Refresh list after action
        await LoadServicesAsync(force: true);
    }

    private async Task ShowServiceLogAsync()
    {
        if (_selected == null) return;
        SetLog("正在获取日志…");
        var log = await RunCaptureAsync("journalctl", $"-u {_selected.Name} -n 80 --no-pager");
        SetLog(string.IsNullOrWhiteSpace(log) ? "无日志" : log);
    }

    // ── Blame ─────────────────────────────────────────────────────────────────

    private async Task ShowBlameAsync()
    {
        SetLog("正在分析启动耗时…");
        var blame = await RunCaptureAsync("systemd-analyze", "blame");
        var total = await RunCaptureAsync("systemd-analyze", "");
        SetLog($"=== 总启动时间 ===\n{total}\n=== 各服务耗时 ===\n{blame}");
        Get<TextBlock>("DetailName").Text = "启动耗时分析";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task RunElevatedStreamAsync(string command)
    {
        var sb  = new StringBuilder();
        var psi = new ProcessStartInfo("pkexec", $"sh -c \"{command}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        await Task.Run(async () =>
        {
            var proc = Process.Start(psi); if (proc == null) return;
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) { sb.AppendLine(e.Data); Dispatcher.UIThread.Post(() => SetLog(sb.ToString())); } };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) { sb.AppendLine(e.Data); Dispatcher.UIThread.Post(() => SetLog(sb.ToString())); } };
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
            await proc.WaitForExitAsync();
            return o;
        }
        catch { return ""; }
    }

    private void SetLog(string t)        => Dispatcher.UIThread.Post(() => Get<TextBlock>("LogText").Text = t);
    private void SetListStatus(string t) => Dispatcher.UIThread.Post(() => Get<TextBlock>("ListStatus").Text = t);
    private T Get<T>(string name) where T : Control =>
        this.FindControl<T>(name) ?? throw new InvalidOperationException($"{name} not found");
}
