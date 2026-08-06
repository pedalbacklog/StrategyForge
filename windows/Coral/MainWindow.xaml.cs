using Microsoft.UI.Xaml;

namespace Coral;

/// <summary>Thin shell — WinUI 3's Window isn't a FrameworkElement, so it can't
/// host x:Bind directly; the real UI/bindings live in <see cref="MainPage"/>.</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "Coral";
        Content = new MainPage();
    }
}
