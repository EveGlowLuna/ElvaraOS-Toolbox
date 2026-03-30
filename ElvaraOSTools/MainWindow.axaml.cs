using Avalonia.Styling;
using SukiUI;
using SukiUI.Controls;

namespace ElvaraOSTools;

public partial class MainWindow : SukiWindow
{
    public MainWindow()
    {
        InitializeComponent();
        SukiTheme theme = SukiTheme.GetInstance();
        // SukiTheme.GetInstance().ChangeBaseTheme(ThemeVariant.Dark);
    }
}