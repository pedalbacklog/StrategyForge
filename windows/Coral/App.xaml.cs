using Microsoft.UI.Xaml;

namespace Coral;

/// <summary>
/// App entry point. Mirrors the role of <c>StrategyForgeApp.swift</c> on macOS —
/// this file should stay this thin; real startup wiring (DI, ModelCatalog.RefreshAsync,
/// etc.) lands in later phases alongside the actual chat UI (windows/PORT-PLAN.md, Fase 5).
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
