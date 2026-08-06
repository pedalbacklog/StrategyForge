using System.Collections.ObjectModel;
using System.Diagnostics;
using Coral.Core.Models;
using Coral.Core.Services;

namespace Coral.Core.ViewModels;

/// <summary>
/// Drives one provider's "Connect" flow (install the CLI if needed, then sign
/// in) for the UI: streams <see cref="ProviderInstaller.Connect"/>'s events
/// into observable state a XAML page can bind to, and forwards a pasted
/// browser auth code back into the running login. The UI/ViewModel half of
/// Fase 6 that <c>ProviderInstaller.cs</c> itself deliberately left out (see
/// its class doc comment) — same split as <see cref="ChatViewModel"/> not
/// touching process I/O directly.
/// </summary>
public sealed class ConnectViewModel : ObservableObject
{
    private readonly IProcessLauncher _processLauncher;
    private readonly IPseudoConsoleLauncher _ptyLauncher;
    private readonly Func<string, string?>? _resolveBinary;
    private readonly Action<string> _openUrl;
    private readonly LoginInput _loginInput = new();
    private CancellationTokenSource? _cts;

    public AIProvider Provider { get; }

    public ObservableCollection<string> LogLines { get; } = new();

    private ConnectPhase? _phase;
    public ConnectPhase? Phase
    {
        get => _phase;
        private set
        {
            if (SetProperty(ref _phase, value)) OnPropertyChanged(nameof(PhaseLabel));
        }
    }

    /// <summary>Binding-friendly label for <see cref="Phase"/> — avoids the UI
    /// needing its own enum converter, same reasoning as <c>ChatMessage.RoleLabel</c>.</summary>
    public string PhaseLabel => Phase switch
    {
        ConnectPhase.Installing => "Installing…",
        ConnectPhase.SigningIn => "Signing in…",
        _ => "",
    };

    private bool _isConnecting;
    public bool IsConnecting { get => _isConnecting; private set => SetProperty(ref _isConnecting, value); }

    /// <summary>True once the login is waiting for a browser auth code to be
    /// pasted back in (Claude's flow) — the UI should show the code entry box.</summary>
    private bool _needsCode;
    public bool NeedsCode { get => _needsCode; private set => SetProperty(ref _needsCode, value); }

    private string _codeInput = "";
    public string CodeInput { get => _codeInput; set => SetProperty(ref _codeInput, value); }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    /// <paramref name="resolveBinary"/> and <paramref name="openUrl"/> are
    /// injectable so this is unit-testable with fakes, same pattern as
    /// <see cref="ChatViewModel"/> — production callers leave both at their
    /// defaults (<see cref="BinaryResolver.Resolve"/> and the real
    /// shell-execute browser launch).</summary>
    public ConnectViewModel(IProcessLauncher processLauncher, IPseudoConsoleLauncher ptyLauncher,
        AIProvider provider, Func<string, string?>? resolveBinary = null, Action<string>? openUrl = null)
    {
        _processLauncher = processLauncher;
        _ptyLauncher = ptyLauncher;
        Provider = provider;
        _resolveBinary = resolveBinary;
        _openUrl = openUrl ?? RealOpenUrl;
    }

    private static void RealOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort — the URL is also visible in LogLines if the
            // browser doesn't open on its own.
        }
    }

    /// <summary>Best-effort — cancels the in-flight connect attempt, if any.</summary>
    public void CancelConnect() => _cts?.Cancel();

    /// <summary>Run the connect flow to completion. No-op while one is already
    /// in flight.</summary>
    public async Task ConnectAsync()
    {
        if (IsConnecting) return;

        LogLines.Clear();
        NeedsCode = false;
        CodeInput = "";
        StatusMessage = null;
        Phase = null;
        IsConnecting = true;
        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            await foreach (var ev in ProviderInstaller.Connect(_processLauncher, _ptyLauncher, Provider,
                _loginInput, _resolveBinary, ct: cts.Token))
            {
                switch (ev)
                {
                    case ConnectEvent.Phase p:
                        Phase = p.Step;
                        break;
                    case ConnectEvent.Log log:
                        LogLines.Add(log.Line);
                        break;
                    case ConnectEvent.Url url:
                        _openUrl(url.Value);
                        break;
                    case ConnectEvent.NeedsCode:
                        NeedsCode = true;
                        break;
                    case ConnectEvent.NeedsNode:
                        StatusMessage = $"Node.js isn't installed. Get it from {ProviderInstaller.NodeDownloadUrl}, then try again.";
                        break;
                    case ConnectEvent.NeedsTerminal:
                        StatusMessage = "This sign-in needs a visible terminal.";
                        break;
                    case ConnectEvent.Failed failed:
                        StatusMessage = $"Error: {failed.Message}";
                        break;
                    case ConnectEvent.Done:
                        StatusMessage = "Connected.";
                        break;
                }
            }
            if (cts.IsCancellationRequested) StatusMessage = "Cancelled.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        finally
        {
            IsConnecting = false;
            NeedsCode = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Send <see cref="CodeInput"/> back into the running login as
    /// the pasted browser auth code. No-op while blank.</summary>
    public async Task SubmitCodeAsync()
    {
        var code = CodeInput.Trim();
        if (code.Length == 0) return;
        await _loginInput.SubmitAsync(code);
        CodeInput = "";
        NeedsCode = false;
    }
}
