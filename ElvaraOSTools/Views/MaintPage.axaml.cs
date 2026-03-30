using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ElvaraOSTools.Views;

public partial class MaintPage : UserControl
{
    private List<string> _pacnewFiles = [];
    private string _selectedPacnew = "";

    public MaintPage()
    {
        InitializeComponent();
        Loaded += (_, _) => WireAll();
    }

    private void WireAll()
    {
        var tabs = Get<TabControl>("MainTabs");
        tabs.SelectionChanged += (_, _) => OnTabChanged(tabs.SelectedIndex);

        Get<Button>("BtnRunPaccache").Click     += (_, _) => _ = ExecPaccacheAsync();
        Get<Button>("BtnRunJournal").Click      += (_, _) => _ = ExecJournalAsync();
        Get<Button>("BtnPacnewDiff").Click      += (_, _) => _ = ShowDiffAsync();
        Get<Button>("BtnPacnewMerge").Click     += (_, _) => _ = MergeAsync();
        Get<Button>("BtnRunInitramfs").Click    += (_, _) => _ = ExecInitramfsAsync();
        Get<Button>("BtnRunGrub").Click         += (_, _) => _ = ExecGrubAsync();
        Get<Button>("BtnSaveGrubCmdline").Click += (_, _) => _ = SaveGrubCmdlineAsync();

        Get<ListBox>("PacnewList").SelectionChanged += (_, _) =>
        {
            if (Get<ListBox>("PacnewList").SelectedItem is string f)
            {
                _selectedPacnew = f;
                Get<Button>("BtnPacnewDiff").IsEnabled  = true;
                Get<Button>("BtnPacnewMerge").IsEnabled = true;
            }
        };

        OnTabChanged(0);
    }

    private void OnTabChanged(int idx)
    {
        switch (idx)
        {
            case 0: _ = LoadPaccacheInfoAsync(); break;
            case 1: _ = LoadJournalInfoAsync(); break;
            case 2: _ = LoadPacnewAsync(); break;
            case 5: _ = LoadGrubCmdlineAsync(); break;
        }
    }

    // ── Paccache ──────────────────────────────────────────────────────────────

    private async Task LoadPaccacheInfoAsync()
    {
        SetStatus("PaccacheStatus", "正在分析缓存…");
        var du    = await RunCaptureAsync("du", "-sh /var/cache/pacman/pkg/");
        var which = await RunCaptureAsync("which", "paccache");
        var sb = new StringBuilder();
        sb.AppendLine($"当前缓存大小：{du.Trim()}");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(which)
            ? "paccache 未找到，将使用 pacman -Sc（仅保留最新版本）。"
            : "将执行 paccache -r（保留每个包最近 3 个版本）。");
        Dispatcher.UIThread.Post(() => Get<TextBlock>("PaccacheText").Text = sb.ToString());
        SetStatus("PaccacheStatus", "就绪");
    }

    private async Task ExecPaccacheAsync()
    {
        SetStatus("PaccacheStatus", "正在清理…");
        var which = await RunCaptureAsync("which", "paccache");
        var cmd   = string.IsNullOrWhiteSpace(which) ? "pacman -Sc --noconfirm" : "paccache -r";
        var sb    = new StringBuilder();
        await RunElevatedStreamAsync(cmd,
            line => { sb.AppendLine(line); Dispatcher.UIThread.Post(() => Get<TextBlock>("PaccacheText").Text = sb.ToString()); });
        SetStatus("PaccacheStatus", "清理完成");
    }

    // ── Journal ───────────────────────────────────────────────────────────────

    private async Task LoadJournalInfoAsync()
    {
        SetStatus("JournalStatus", "正在获取日志占用…");
        var du = await RunCaptureAsync("journalctl", "--disk-usage");
        Dispatcher.UIThread.Post(() => Get<TextBlock>("JournalText").Text = $"当前日志占用：\n{du}");
        SetStatus("JournalStatus", "就绪");
    }

    private async Task ExecJournalAsync()
    {
        var size = Get<TextBox>("JournalSizeInput").Text?.Trim() ?? "200M";
        SetStatus("JournalStatus", $"正在清理，保留 {size}…");
        var sb = new StringBuilder();
        await RunElevatedStreamAsync($"journalctl --vacuum-size={size}",
            line => { sb.AppendLine(line); Dispatcher.UIThread.Post(() => Get<TextBlock>("JournalText").Text = sb.ToString()); });
        SetStatus("JournalStatus", "清理完成");
    }

    // ── Pacnew ────────────────────────────────────────────────────────────────

    private async Task LoadPacnewAsync()
    {
        SetStatus("PacnewStatus", "正在扫描…");
        _pacnewFiles = [];
        await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("find", "/etc -name \"*.pacnew\" 2>/dev/null")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                };
                var proc = Process.Start(psi); if (proc == null) return;
                var output = proc.StandardOutput.ReadToEnd(); proc.WaitForExit();
                _pacnewFiles = [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
            }
            catch { }
        });
        Dispatcher.UIThread.Post(() =>
        {
            Get<ListBox>("PacnewList").ItemsSource = _pacnewFiles;
            SetStatus("PacnewStatus", _pacnewFiles.Count == 0
                ? "没有 .pacnew 文件 ✓"
                : $"找到 {_pacnewFiles.Count} 个 .pacnew 文件，选中后操作");
        });
    }

    private async Task ShowDiffAsync()
    {
        if (string.IsNullOrEmpty(_selectedPacnew)) return;
        var original = _selectedPacnew.Replace(".pacnew", "");
        if (!File.Exists(original))
        {
            Dispatcher.UIThread.Post(() => Get<TextBlock>("PacnewDiffText").Text = $"原文件不存在：{original}");
            return;
        }
        var diff = await RunCaptureAsync("diff", $"-u {original} {_selectedPacnew}");
        Dispatcher.UIThread.Post(() => Get<TextBlock>("PacnewDiffText").Text =
            string.IsNullOrWhiteSpace(diff) ? "文件无差异" : diff);
    }

    private async Task MergeAsync()
    {
        if (string.IsNullOrEmpty(_selectedPacnew)) return;
        var original = _selectedPacnew.Replace(".pacnew", "");
        SetStatus("PacnewStatus", $"正在合并…");
        var sb = new StringBuilder();
        await RunElevatedStreamAsync($"cp {_selectedPacnew} {original} && rm {_selectedPacnew}",
            line => { sb.AppendLine(line); });
        SetStatus("PacnewStatus", "合并完成");
        await LoadPacnewAsync();
    }

    // ── Initramfs ─────────────────────────────────────────────────────────────

    private async Task ExecInitramfsAsync()
    {
        SetStatus("InitramfsStatus", "正在重建…");
        var sb = new StringBuilder();
        await RunElevatedStreamAsync("mkinitcpio -P",
            line => { sb.AppendLine(line); Dispatcher.UIThread.Post(() => Get<TextBlock>("InitramfsText").Text = sb.ToString()); });
        SetStatus("InitramfsStatus", "重建完成");
    }

    // ── GRUB update ───────────────────────────────────────────────────────────

    private async Task ExecGrubAsync()
    {
        SetStatus("GrubStatus", "正在更新 GRUB…");
        var sb = new StringBuilder();
        await RunElevatedStreamAsync("grub-mkconfig -o /boot/grub/grub.cfg",
            line => { sb.AppendLine(line); Dispatcher.UIThread.Post(() => Get<TextBlock>("GrubText").Text = sb.ToString()); });
        SetStatus("GrubStatus", "GRUB 更新完成");
    }

    // ── GRUB cmdline editor ───────────────────────────────────────────────────

    private async Task LoadGrubCmdlineAsync()
    {
        SetStatus("GrubEditStatus", "正在读取…");
        try
        {
            var content = await File.ReadAllTextAsync("/etc/default/grub");
            var match   = Regex.Match(content, @"GRUB_CMDLINE_LINUX_DEFAULT=""([^""]*)""");
            var current = match.Success ? match.Groups[1].Value : "quiet splash";
            Dispatcher.UIThread.Post(() =>
            {
                Get<TextBox>("GrubCmdlineInput").Text = current;
                Get<TextBlock>("GrubEditText").Text =
                    "常用参数：\n" +
                    "  quiet                    — 减少启动输出\n" +
                    "  splash                   — 显示启动画面\n" +
                    "  nomodeset                — 禁用内核模式设置\n" +
                    "  nvidia-drm.modeset=1     — NVIDIA DRM 模式设置\n" +
                    "  iommu=pt                 — IOMMU 直通\n" +
                    "  mitigations=off          — 关闭 CPU 漏洞缓解\n\n" +
                    "修改上方输入框后点击「保存参数」，再切换到「更新 GRUB 配置」使其生效。";
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => Get<TextBlock>("GrubEditText").Text = $"读取失败：{ex.Message}");
        }
        SetStatus("GrubEditStatus", "就绪");
    }

    private async Task SaveGrubCmdlineAsync()
    {
        var newParams = Get<TextBox>("GrubCmdlineInput").Text?.Trim() ?? "";
        SetStatus("GrubEditStatus", "正在写入…");
        var escaped = newParams.Replace("\"", "\\\"");
        var sb = new StringBuilder();
        await RunElevatedStreamAsync(
            $"sed -i 's|^GRUB_CMDLINE_LINUX_DEFAULT=.*|GRUB_CMDLINE_LINUX_DEFAULT=\"{escaped}\"|' /etc/default/grub",
            line => { sb.AppendLine(line); });
        SetStatus("GrubEditStatus", "已保存，请切换到「更新 GRUB 配置」使其生效");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task RunElevatedStreamAsync(string command, Action<string> onLine)
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

    private void SetStatus(string name, string text) =>
        Dispatcher.UIThread.Post(() => Get<TextBlock>(name).Text = text);

    private T Get<T>(string name) where T : Control =>
        this.FindControl<T>(name) ?? throw new InvalidOperationException($"{name} not found");
}
