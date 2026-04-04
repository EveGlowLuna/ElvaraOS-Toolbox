using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ElvaraOSTools.Views;

public partial class NvidiaPage : UserControl
{
    private string _recommendedPkg = "nvidia";

    public NvidiaPage()
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

        Get<Button>("BtnInstallDriver").Click  += (_, _) => _ = InstallDriverAsync();
        Get<Button>("BtnInstallCuda").Click    += (_, _) => _ = InstallCudaAsync();
        Get<Button>("BtnWriteXorg").Click      += (_, _) => _ = WriteXorgAsync();
        Get<Button>("BtnLaunchSettings").Click += (_, _) => _ = LaunchSettingsAsync();

        OnTabChanged(0);
    }

    private void OnTabChanged(int idx)
    {
        switch (idx)
        {
            case 0: _ = DetectAsync(); break;
            case 1: _ = RecommendAsync(); break;
            case 2: _ = CudaAsync(); break;
            case 3: _ = PrimeAsync(); break;
            case 4: ShowXorgPreview(); break;
        }
    }

    private async Task DetectAsync()
    {
        SetStatus("DetectStatus", "正在检测…");
        var sb = new StringBuilder();

        var lspci = await RunCaptureAsync("lspci", "-k");
        sb.AppendLine("=== 显卡信息 (lspci) ===");
        bool inGpu = false;
        foreach (var line in lspci.Split('\n'))
        {
            if (line.Contains("VGA") || line.Contains("3D") || line.Contains("Display")) inGpu = true;
            else if (line.Length > 0 && line[0] != '\t' && line[0] != ' ') inGpu = false;
            if (inGpu) sb.AppendLine(line);
        }

        sb.AppendLine();
        sb.AppendLine("=== nvidia-smi ===");
        var smi = await RunCaptureAsync("nvidia-smi", "");
        sb.AppendLine(string.IsNullOrWhiteSpace(smi) ? "nvidia-smi 未找到（驱动未安装）" : smi);

        if (File.Exists("/proc/driver/nvidia/version"))
        {
            sb.AppendLine("=== /proc/driver/nvidia/version ===");
            sb.AppendLine(await File.ReadAllTextAsync("/proc/driver/nvidia/version"));
        }

        sb.AppendLine("=== 已安装的 NVIDIA 相关包 ===");
        var pkgs = await RunCaptureAsync("pacman", "-Qq");
        foreach (var p in pkgs.Split('\n'))
            if (p.Contains("nvidia") || p.Contains("cuda") || p.Contains("cudnn"))
                sb.AppendLine("  " + p.Trim());

        Dispatcher.UIThread.Post(() => Get<TextBlock>("DetectText").Text = sb.ToString());
        SetStatus("DetectStatus", "检测完成");
    }

    private async Task RecommendAsync()
    {
        SetStatus("RecommendStatus", "正在分析…");
        var lspci = await RunCaptureAsync("lspci", "");
        var sb = new StringBuilder();

        if (!lspci.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("未检测到 NVIDIA 显卡。");
            Dispatcher.UIThread.Post(() => Get<TextBlock>("RecommendText").Text = sb.ToString());
            SetStatus("RecommendStatus", "无 NVIDIA 显卡");
            return;
        }

        bool isOld = lspci.Contains("GTX 6") || lspci.Contains("GTX 7") ||
                     lspci.Contains("GT 6")  || lspci.Contains("GT 7");
        _recommendedPkg = isOld ? "nvidia-470xx-dkms" : "nvidia-dkms";

        // 查询实际可用的 nvidia 驱动包
        var available = await RunCaptureAsync("pacman", "-Ssq nvidia");
        var driverPkgs = available.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"^nvidia(-\w+)?$"))
            .ToList();

        sb.AppendLine("检测到 NVIDIA 显卡。");
        sb.AppendLine();
        sb.AppendLine("仓库中可用的驱动包：");
        foreach (var p in driverPkgs) sb.AppendLine($"  {p}");
        sb.AppendLine();
        sb.AppendLine($"推荐安装：{_recommendedPkg}  +  nvidia-utils  nvidia-settings");
        sb.AppendLine("（如推荐包不存在，请从上方列表手动选择合适的版本）");

        Dispatcher.UIThread.Post(() =>
        {
            Get<TextBlock>("RecommendText").Text = sb.ToString();
            Get<Button>("BtnInstallDriver").IsVisible = true;
        });
        SetStatus("RecommendStatus", "分析完成");
    }

    private async Task InstallDriverAsync()
    {
        SetStatus("RecommendStatus", $"正在安装 {_recommendedPkg}…");
        var sb = new StringBuilder();
        await RunElevatedStreamAsync($"pacman -S --needed {_recommendedPkg}",
            line => { sb.AppendLine(line); Dispatcher.UIThread.Post(() => Get<TextBlock>("RecommendText").Text = sb.ToString()); });
        SetStatus("RecommendStatus", "安装完成，建议重启");
    }

    private async Task CudaAsync()
    {
        SetStatus("CudaStatus", "正在检测…");
        var installed = await RunCaptureAsync("pacman", "-Qq");
        var smi = await RunCaptureAsync("nvidia-smi", "");
        var sb = new StringBuilder();
        sb.AppendLine("=== CUDA 状态 ===");
        sb.AppendLine($"  nvidia-smi 可用：{(!string.IsNullOrWhiteSpace(smi) ? "是" : "否")}");
        sb.AppendLine($"  cuda 已安装：   {(installed.Contains("cuda") ? "是" : "否")}");
        sb.AppendLine($"  cudnn 已安装：  {(installed.Contains("cudnn") ? "是" : "否")}");
        sb.AppendLine();
        sb.AppendLine("安装命令：");
        sb.AppendLine("  sudo pacman -S cuda");
        sb.AppendLine("  sudo pacman -S cudnn");
        sb.AppendLine("  sudo pacman -S python-pytorch-cuda");
        Dispatcher.UIThread.Post(() =>
        {
            Get<TextBlock>("CudaText").Text = sb.ToString();
            Get<Button>("BtnInstallCuda").IsVisible = true;
        });
        SetStatus("CudaStatus", "就绪");
    }

    private async Task InstallCudaAsync()
    {
        SetStatus("CudaStatus", "正在安装 cuda cudnn…");
        var sb = new StringBuilder();
        await RunElevatedStreamAsync("pacman -S --needed cuda cudnn",
            line => { sb.AppendLine(line); Dispatcher.UIThread.Post(() => Get<TextBlock>("CudaText").Text = sb.ToString()); });
        SetStatus("CudaStatus", "安装完成");
    }

    private async Task PrimeAsync()
    {
        SetStatus("PrimeStatus", "正在检测…");
        var lspci = await RunCaptureAsync("lspci", "");
        bool hasIntel  = lspci.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
                         (lspci.Contains("VGA") || lspci.Contains("Display") || lspci.Contains("3D"));
        bool hasAmd    = lspci.Contains("AMD", StringComparison.OrdinalIgnoreCase) &&
                         (lspci.Contains("VGA") || lspci.Contains("Display") || lspci.Contains("3D"));
        bool hasNvidia = lspci.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        bool hasIgpu   = hasIntel || hasAmd;

        var sb = new StringBuilder();
        if (hasIntel)  sb.AppendLine("Intel 核显：检测到");
        if (hasAmd)    sb.AppendLine("AMD 显卡：检测到");
        if (hasNvidia) sb.AppendLine("NVIDIA 独显：检测到");
        sb.AppendLine();

        if (hasIgpu && hasNvidia)
        {
            sb.AppendLine("检测到双显卡配置，推荐安装 nvidia-prime：");
            sb.AppendLine("  sudo pacman -S nvidia-prime");
            sb.AppendLine();
            sb.AppendLine("使用方式：");
            sb.AppendLine("  prime-run <程序>        # 用 NVIDIA 独显运行");
            sb.AppendLine("  prime-run glxinfo | grep renderer");
            sb.AppendLine();
            sb.AppendLine("环境变量方式：");
            sb.AppendLine("  __NV_PRIME_RENDER_OFFLOAD=1 __GLX_VENDOR_LIBRARY_NAME=nvidia <程序>");
            if (hasAmd)
            {
                sb.AppendLine();
                sb.AppendLine("AMD + NVIDIA 用户注意：");
                sb.AppendLine("  确保已安装 xf86-video-amdgpu 或 amdgpu 内核模块作为主显卡驱动");
            }
        }
        else if (hasAmd && !hasNvidia)
        {
            sb.AppendLine("纯 AMD 配置，无需 NVIDIA Prime。");
            sb.AppendLine("AMD 多显卡切换可使用 AMDGPU 的 DRI_PRIME 环境变量：");
            sb.AppendLine("  DRI_PRIME=1 <程序>");
        }
        else
        {
            sb.AppendLine("未检测到双显卡配置，Prime 功能不适用。");
        }

        Dispatcher.UIThread.Post(() => Get<TextBlock>("PrimeText").Text = sb.ToString());
        SetStatus("PrimeStatus", "就绪");
    }

    private const string XorgConfContent = """
# /etc/X11/xorg.conf.d/20-nvidia.conf
# 由 ElvaraOS Tools 生成

Section "Device"
    Identifier  "NVIDIA Card"
    Driver      "nvidia"
    VendorName  "NVIDIA Corporation"
    Option      "NoLogo" "true"
    Option      "RegistryDwords" "EnableBrightnessControl=1"
EndSection

Section "Screen"
    Identifier "Default Screen"
    Device     "NVIDIA Card"
    DefaultDepth 24
    SubSection "Display"
        Depth 24
    EndSubSection
EndSection
""";

    private void ShowXorgPreview()
    {
        Get<TextBlock>("XorgText").Text =
            "将写入 /etc/X11/xorg.conf.d/20-nvidia.conf：\n\n" + XorgConfContent;
        SetStatus("XorgStatus", "就绪");
    }

    private async Task WriteXorgAsync()
    {
        SetStatus("XorgStatus", "正在写入…");
        var tmp = Path.GetTempFileName();
        await File.WriteAllTextAsync(tmp, XorgConfContent);
        var sb = new StringBuilder();
        await RunElevatedStreamAsync(
            $"sh -c \"mkdir -p /etc/X11/xorg.conf.d && cp {tmp} /etc/X11/xorg.conf.d/20-nvidia.conf\"",
            line => { sb.AppendLine(line); Dispatcher.UIThread.Post(() => Get<TextBlock>("XorgText").Text = sb.ToString()); });
        SetStatus("XorgStatus", "已写入 /etc/X11/xorg.conf.d/20-nvidia.conf");
    }

    private async Task LaunchSettingsAsync()
    {
        try
        {
            Process.Start(new ProcessStartInfo("nvidia-settings") { UseShellExecute = true });
            Dispatcher.UIThread.Post(() => Get<TextBlock>("SettingsText").Text = "nvidia-settings 已启动。");
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => Get<TextBlock>("SettingsText").Text =
                $"启动失败：{ex.Message}\n\n请先安装：sudo pacman -S nvidia-settings");
        }
        await Task.CompletedTask;
    }

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
