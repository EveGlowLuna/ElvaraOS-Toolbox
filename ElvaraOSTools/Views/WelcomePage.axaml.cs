using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Input.Platform;

namespace ElvaraOSTools.Views;

public partial class WelcomePage : UserControl
{
    private static readonly string[] CategoryOrder =
        ["系统", "硬件", "桌面环境", "终端", "运行状态", "网络", "电源", "其他"];

    // Cached plain-text copy of all info rows
    private readonly StringBuilder _copyBuffer = new();

    public WelcomePage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            WireButtons();
            _ = LoadFastfetchAsync();
            _ = LoadUptimeAsync();
            _ = LoadCacheAsync();
            _ = LoadTempsAsync();
        };
    }

    private void WireButtons()
    {
        if (this.FindControl<Button>("BtnRefresh") is { } btnR)
            btnR.Click += async (_, _) => await LoadFastfetchAsync();

        if (this.FindControl<Button>("BtnCopy") is { } btnC)
            btnC.Click += async (_, _) =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                    await clipboard.SetTextAsync(_copyBuffer.ToString());
            };

        if (this.FindControl<Button>("BtnDropCache") is { } btnD)
            btnD.Click += async (_, _) => await DropCacheAsync();

        if (this.FindControl<Button>("BtnRefreshTemp") is { } btnT)
            btnT.Click += async (_, _) => await LoadTempsAsync();
    }

    // ── fastfetch ─────────────────────────────────────────────────────────────

    private async Task LoadFastfetchAsync()
    {
        var grid = this.FindControl<Grid>("InfoGrid");
        if (grid == null) return;

        try
        {
            var output = await RunAsync("fastfetch", "--format json");
            var entries = JsonNode.Parse(output)?.AsArray() ?? throw new Exception("JSON 解析失败");

            var groups = new Dictionary<string, List<InfoRow>>();
            foreach (var cat in CategoryOrder) groups[cat] = [];

            foreach (var entry in entries)
            {
                if (entry == null) continue;
                var type = entry["type"]?.GetValue<string>() ?? "";
                if (entry["error"] != null || entry["result"] == null) continue;
                groups[GetCategory(type)].AddRange(ExtractRows(type, entry["result"]!));
            }

            Dispatcher.UIThread.Post(() =>
            {
                grid.Children.Clear();
                grid.RowDefinitions.Clear();
                _copyBuffer.Clear();

                int row = 0;
                bool firstCat = true;

                foreach (var cat in CategoryOrder)
                {
                    if (groups[cat].Count == 0) continue;

                    if (!firstCat)
                    {
                        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                        var sep = new Border
                        {
                            Height = 1,
                            Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
                            Margin = new Thickness(0, 8, 0, 8)
                        };
                        Grid.SetRow(sep, row); Grid.SetColumnSpan(sep, 3);
                        grid.Children.Add(sep);
                        row++;
                    }
                    firstCat = false;

                    grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    var catLabel = new TextBlock
                    {
                        Text = cat, FontSize = 11, FontWeight = FontWeight.Bold,
                        Opacity = 0.45, Margin = new Thickness(0, 0, 0, 5)
                    };
                    Grid.SetRow(catLabel, row); Grid.SetColumnSpan(catLabel, 3);
                    grid.Children.Add(catLabel);
                    _copyBuffer.AppendLine($"[{cat}]");
                    row++;

                    foreach (var infoRow in groups[cat])
                    {
                        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                        var label = new TextBlock
                        {
                            Text = infoRow.Label, FontSize = 12, FontWeight = FontWeight.SemiBold,
                            Foreground = new SolidColorBrush(Color.Parse("#5B9BD5")),
                            VerticalAlignment = VerticalAlignment.Top,
                            Margin = new Thickness(0, 0, 0, 5)
                        };
                        Grid.SetRow(label, row); Grid.SetColumn(label, 0);
                        grid.Children.Add(label);

                        var valueStack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 3, Margin = new Thickness(0, 0, 0, 5) };
                        valueStack.Children.Add(new TextBlock
                        {
                            Text = infoRow.Value, FontSize = 12, Opacity = 0.85,
                            TextWrapping = TextWrapping.Wrap, MaxWidth = 220,
                            VerticalAlignment = VerticalAlignment.Top
                        });

                        if (infoRow.ProgressValue.HasValue)
                        {
                            var pct = Math.Clamp(infoRow.ProgressValue.Value, 0, 1);
                            valueStack.Children.Add(new ProgressBar
                            {
                                Value = pct * 100, Minimum = 0, Maximum = 100,
                                Height = 4, Width = 180,
                                HorizontalAlignment = HorizontalAlignment.Left,
                                Foreground = ProgressBrush(pct)
                            });
                        }

                        Grid.SetRow(valueStack, row); Grid.SetColumn(valueStack, 2);
                        grid.Children.Add(valueStack);
                        _copyBuffer.AppendLine($"  {infoRow.Label}: {infoRow.Value}");
                        row++;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => SetGridError(grid, ex.Message));
        }
    }

    // ── Uptime ────────────────────────────────────────────────────────────────

    private async Task LoadUptimeAsync()
    {
        var panel = this.FindControl<StackPanel>("UptimePanel");
        if (panel == null) return;

        try
        {
            var raw = await File.ReadAllTextAsync("/proc/uptime");
            var uptimeSecs = double.Parse(raw.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            var bootTime = DateTime.Now - TimeSpan.FromSeconds(uptimeSecs);
            var ts = TimeSpan.FromSeconds(uptimeSecs);
            var upStr = ts.TotalDays >= 1
                ? $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m {ts.Seconds}s"
                : ts.TotalHours >= 1
                    ? $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s"
                    : $"{ts.Minutes}m {ts.Seconds}s";

            Dispatcher.UIThread.Post(() =>
            {
                panel.Children.Clear();
                panel.Children.Add(MakeRow("启动于", bootTime.ToString("yyyy-MM-dd HH:mm:ss")));
                panel.Children.Add(MakeRow("已运行", upStr));
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => { panel.Children.Clear(); panel.Children.Add(ErrText(ex.Message)); });
        }
    }

    // ── Cache ─────────────────────────────────────────────────────────────────

    private async Task LoadCacheAsync()
    {
        var panel = this.FindControl<StackPanel>("CachePanel");
        if (panel == null) return;

        try
        {
            var output = await RunAsync("free", "-b");
            // Mem: total used free shared buff/cache available
            var line = output.Split('\n').FirstOrDefault(l => l.StartsWith("Mem:")) ?? "";
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var total = long.Parse(parts[1]);
            var cache = long.Parse(parts[5]);
            var pct = (double)cache / total;

            Dispatcher.UIThread.Post(() =>
            {
                panel.Children.Clear();
                panel.Children.Add(MakeRow("Buff/Cache", $"{FormatBytes(cache)} / {FormatBytes(total)}  ({pct:P0})"));
                panel.Children.Add(new ProgressBar
                {
                    Value = pct * 100, Minimum = 0, Maximum = 100,
                    Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch,
                    Foreground = ProgressBrush(pct)
                });
                panel.Children.Add(new TextBlock
                {
                    Text = "清理会执行 sync && echo 3 > /proc/sys/vm/drop_caches（需要 sudo）",
                    FontSize = 10, Opacity = 0.4, TextWrapping = TextWrapping.Wrap
                });
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => { panel.Children.Clear(); panel.Children.Add(ErrText(ex.Message)); });
        }
    }

    private async Task DropCacheAsync()
    {
        var panel = this.FindControl<StackPanel>("CachePanel");
        try
        {
            // sync first
            await RunAsync("sync", "");
            // drop_caches requires root; try pkexec
            await RunAsync("pkexec", "sh -c \"echo 3 > /proc/sys/vm/drop_caches\"");
            await LoadCacheAsync();
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (panel == null) return;
                // Remove old error if any, append new
                panel.Children.Add(ErrText($"清理失败: {ex.Message}"));
            });
        }
    }

    // ── Temperatures ──────────────────────────────────────────────────────────

    private async Task LoadTempsAsync()
    {
        var panel = this.FindControl<StackPanel>("TempPanel");
        if (panel == null) return;

        try
        {
            List<(string name, double temp)> readings = [];

            // Try sensors -j first
            try
            {
                var json = await RunAsync("sensors", "-j");
                var root = JsonNode.Parse(json)?.AsObject();
                if (root != null)
                    foreach (var chip in root)
                    {
                        if (chip.Value is not JsonObject chipObj) continue;
                        foreach (var feature in chipObj)
                        {
                            if (feature.Value is not JsonObject featureObj) continue;
                            foreach (var sub in featureObj)
                            {
                                if (sub.Key.EndsWith("_input") && sub.Value != null)
                                {
                                    var val = sub.Value.GetValue<double>();
                                    if (val > 0 && val < 150)
                                        readings.Add(($"{chip.Key} / {feature.Key}", val));
                                }
                            }
                        }
                    }
            }
            catch
            {
                // Fallback: /sys/class/thermal
                var zones = Directory.GetDirectories("/sys/class/thermal", "thermal_zone*");
                foreach (var zone in zones)
                {
                    try
                    {
                        var typeFile = Path.Combine(zone, "type");
                        var tempFile = Path.Combine(zone, "temp");
                        if (!File.Exists(tempFile)) continue;
                        var name = File.Exists(typeFile) ? (await File.ReadAllTextAsync(typeFile)).Trim() : Path.GetFileName(zone);
                        var millideg = int.Parse((await File.ReadAllTextAsync(tempFile)).Trim());
                        readings.Add((name, millideg / 1000.0));
                    }
                    catch { }
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                panel.Children.Clear();
                if (readings.Count == 0)
                {
                    panel.Children.Add(ErrText("未找到温度数据。请安装 lm-sensors：\nsudo apt install lm-sensors && sudo sensors-detect"));
                    return;
                }
                foreach (var (name, temp) in readings)
                {
                    var color = temp >= 85 ? "#E05252" : temp >= 65 ? "#E0A052" : "#5B9BD5";
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 4) };
                    row.Children.Add(new TextBlock { Text = name, FontSize = 12, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center });
                    var tempLabel = new TextBlock
                    {
                        Text = $"{temp:F1} °C",
                        FontSize = 12, FontWeight = FontWeight.SemiBold,
                        Foreground = new SolidColorBrush(Color.Parse(color)),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(tempLabel, 1);
                    row.Children.Add(tempLabel);
                    panel.Children.Add(row);
                }
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => { panel.Children.Clear(); panel.Children.Add(ErrText(ex.Message)); });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static StackPanel MakeRow(string label, string value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12, FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#5B9BD5")), MinWidth = 60
        });
        row.Children.Add(new TextBlock { Text = value, FontSize = 12, Opacity = 0.85 });
        return row;
    }

    private static TextBlock ErrText(string msg) => new()
    {
        Text = msg, FontSize = 11, Opacity = 0.6,
        TextWrapping = TextWrapping.Wrap, Foreground = Brushes.OrangeRed
    };

    private static void SetGridError(Grid grid, string msg)
    {
        grid.Children.Clear(); grid.RowDefinitions.Clear();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var t = ErrText($"⚠ {msg}"); Grid.SetColumnSpan(t, 3);
        grid.Children.Add(t);
    }

    private static async Task<string> RunAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = Process.Start(psi) ?? throw new Exception($"无法启动 {exe}");
        var output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return output;
    }

    private static IBrush ProgressBrush(double pct) => pct switch
    {
        > 0.85 => new SolidColorBrush(Color.Parse("#E05252")),
        > 0.65 => new SolidColorBrush(Color.Parse("#E0A052")),
        _ => new SolidColorBrush(Color.Parse("#5B9BD5"))
    };

    // ── Row model ─────────────────────────────────────────────────────────────

    private record InfoRow(string Label, string Value, double? ProgressValue = null);

    // ── Category mapping ──────────────────────────────────────────────────────

    private static string GetCategory(string type) => type switch
    {
        "OS" or "Kernel" or "Host" or "Bios" or "Board" or "Title" => "系统",
        "CPU" or "GPU" or "Memory" or "Swap" or "Disk" => "硬件",
        "Display" or "DE" or "WM" or "WMTheme" or "Theme" or "Icons" or "Font" or "Cursor" => "桌面环境",
        "Shell" or "Terminal" => "终端",
        "Uptime" or "Packages" or "Processes" => "运行状态",
        "LocalIp" or "PublicIp" or "Wifi" or "Bluetooth" => "网络",
        "Battery" or "PowerAdapter" => "电源",
        _ => "其他"
    };

    // ── Data extraction ───────────────────────────────────────────────────────

    private static List<InfoRow> ExtractRows(string type, JsonNode r)
    {
        var list = new List<InfoRow>();
        try
        {
            switch (type)
            {
                case "Title":
                    list.Add(new InfoRow("用户", $"{r["userName"]?.GetValue<string>()}@{r["hostName"]?.GetValue<string>()}"));
                    break;
                case "OS":
                    list.Add(new InfoRow("OS", r["prettyName"]?.GetValue<string>() ?? ""));
                    break;
                case "Host":
                    list.Add(new InfoRow("Host", $"{r["vendor"]?.GetValue<string>()} {r["name"]?.GetValue<string>()}".Trim()));
                    break;
                case "Kernel":
                    list.Add(new InfoRow("Kernel", $"{r["name"]?.GetValue<string>()} {r["release"]?.GetValue<string>()}".Trim()));
                    break;
                case "Bios":
                    list.Add(new InfoRow("BIOS", $"{r["vendor"]?.GetValue<string>()} {r["version"]?.GetValue<string>()}".Trim()));
                    break;
                case "Uptime":
                    list.Add(new InfoRow("Uptime", FormatUptime(r["uptime"]?.GetValue<long>() ?? 0)));
                    break;
                case "Packages":
                    var parts = new List<string>();
                    void AddPkg(string key, string name) { var v = r[key]?.GetValue<int>() ?? 0; if (v > 0) parts.Add($"{v} ({name})"); }
                    AddPkg("dpkg", "dpkg"); AddPkg("flatpakSystem", "flatpak"); AddPkg("snap", "snap");
                    AddPkg("pacman", "pacman"); AddPkg("rpm", "rpm"); AddPkg("brew", "brew");
                    list.Add(new InfoRow("Packages", parts.Count > 0 ? string.Join(", ", parts) : $"{r["all"]?.GetValue<int>()}"));
                    break;
                case "Shell":
                    list.Add(new InfoRow("Shell", $"{r["prettyName"]?.GetValue<string>()} {r["version"]?.GetValue<string>()}".Trim()));
                    break;
                case "Display":
                    if (r is JsonArray displays)
                        foreach (var d in displays)
                        {
                            if (d == null) continue;
                            var w = d["output"]?["width"]?.GetValue<int>() ?? 0;
                            var h = d["output"]?["height"]?.GetValue<int>() ?? 0;
                            var hz = d["output"]?["refreshRate"]?.GetValue<double>() ?? 0;
                            list.Add(new InfoRow("Display", $"{d["name"]?.GetValue<string>()}  {w}×{h} @ {hz:F0} Hz"));
                        }
                    break;
                case "DE":
                    list.Add(new InfoRow("DE", $"{r["prettyName"]?.GetValue<string>()} {r["version"]?.GetValue<string>()}".Trim()));
                    break;
                case "WM":
                    list.Add(new InfoRow("WM", $"{r["prettyName"]?.GetValue<string>()} ({r["protocolName"]?.GetValue<string>()})"));
                    break;
                case "WMTheme":
                    list.Add(new InfoRow("WM Theme", r.GetValue<string>()));
                    break;
                case "Theme":
                    var t1 = r["theme1"]?.GetValue<string>() ?? ""; var t2 = r["theme2"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(t1)) list.Add(new InfoRow("Theme", t1));
                    if (!string.IsNullOrEmpty(t2) && t2 != t1) list.Add(new InfoRow("Theme", t2));
                    break;
                case "Icons":
                    var i1 = r["icons1"]?.GetValue<string>() ?? ""; var i2 = r["icons2"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(i1)) list.Add(new InfoRow("Icons", i1));
                    if (!string.IsNullOrEmpty(i2) && i2 != i1) list.Add(new InfoRow("Icons", i2));
                    break;
                case "Font":
                    var disp = r["display"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(disp)) list.Add(new InfoRow("Font", disp));
                    break;
                case "Cursor":
                    list.Add(new InfoRow("Cursor", $"{r["theme"]?.GetValue<string>()} ({r["size"]?.GetValue<string>()}px)"));
                    break;
                case "Terminal":
                    list.Add(new InfoRow("Terminal", $"{r["prettyName"]?.GetValue<string>()} {r["version"]?.GetValue<string>()}".Trim()));
                    break;
                case "CPU":
                    var p = r["cores"]?["physical"]?.GetValue<int>() ?? 0;
                    var l = r["cores"]?["logical"]?.GetValue<int>() ?? 0;
                    var mhz = r["frequency"]?["max"]?.GetValue<double>() ?? 0;
                    list.Add(new InfoRow("CPU", $"{r["cpu"]?.GetValue<string>()}  ({p}P/{l}L) @ {mhz / 1000.0:F2} GHz"));
                    break;
                case "GPU":
                    if (r is JsonArray gpus)
                        foreach (var g in gpus)
                        {
                            if (g == null) continue;
                            var gf = g["frequency"]?.GetValue<double>() ?? 0;
                            list.Add(new InfoRow("GPU", $"{g["vendor"]?.GetValue<string>()} {g["name"]?.GetValue<string>()}{(gf > 0 ? $" @ {gf / 1000.0:F2} GHz" : "")} [{g["type"]?.GetValue<string>()}]"));
                        }
                    break;
                case "Memory":
                    var mu = r["used"]?.GetValue<long>() ?? 0; var mt = r["total"]?.GetValue<long>() ?? 0;
                    var mp = mt > 0 ? (double)mu / mt : 0;
                    list.Add(new InfoRow("Memory", $"{FormatBytes(mu)} / {FormatBytes(mt)}  ({mp:P0})", mp));
                    break;
                case "Swap":
                    if (r is JsonArray swaps)
                        foreach (var s in swaps)
                        {
                            if (s == null) continue;
                            var su = s["used"]?.GetValue<long>() ?? 0; var st = s["total"]?.GetValue<long>() ?? 0;
                            var sp = st > 0 ? (double)su / st : 0;
                            list.Add(new InfoRow("Swap", $"{s["name"]?.GetValue<string>()}  {FormatBytes(su)} / {FormatBytes(st)}  ({sp:P0})", sp));
                        }
                    break;
                case "Disk":
                    if (r is JsonArray disks)
                        foreach (var d in disks)
                        {
                            if (d == null) continue;
                            var du = d["bytes"]?["used"]?.GetValue<long>() ?? 0; var dt = d["bytes"]?["total"]?.GetValue<long>() ?? 0;
                            var dp = dt > 0 ? (double)du / dt : 0;
                            list.Add(new InfoRow($"Disk ({d["mountpoint"]?.GetValue<string>()})", $"{FormatBytes(du)} / {FormatBytes(dt)}  ({dp:P0})  {d["filesystem"]?.GetValue<string>()}", dp));
                        }
                    break;
                case "LocalIp":
                    if (r is JsonArray ips)
                        foreach (var ip in ips)
                        {
                            if (ip == null) continue;
                            var ipv4 = ip["ipv4"]?.GetValue<string>() ?? "";
                            if (!string.IsNullOrEmpty(ipv4))
                                list.Add(new InfoRow($"IP ({ip["name"]?.GetValue<string>()})", ipv4));
                        }
                    break;
                case "Battery":
                    if (r is JsonArray bats)
                        foreach (var b in bats)
                        {
                            if (b == null) continue;
                            var bc = b["capacity"]?.GetValue<double>() ?? 0;
                            list.Add(new InfoRow("Battery", $"{b["modelName"]?.GetValue<string>()}  {bc:F0}%  [{b["status"]?.GetValue<string>()}]", bc / 100.0));
                        }
                    break;
                case "Locale":
                    list.Add(new InfoRow("Locale", r.GetValue<string>()));
                    break;
            }
        }
        catch { }
        return list;
    }

    private static string FormatUptime(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GiB";
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MiB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F2} KiB";
        return $"{bytes} B";
    }
}
