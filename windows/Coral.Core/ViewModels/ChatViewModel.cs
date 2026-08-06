using System.Collections.ObjectModel;
using System.Globalization;
using Coral.Core.Services;

namespace Coral.Core.ViewModels;

public enum ChatRole { User, Assistant }

/// <summary>One entry in the transcript. <see cref="Text"/> is mutable so a
/// streaming assistant reply can grow in place (matches
/// <c>ChatView.swift</c>'s live-updating bubble) — the collection doesn't need
/// to be re-added, just this item's <c>PropertyChanged</c> fires so a
/// <c>x:Bind Mode=OneWay</c> in XAML picks up each delta.</summary>
public sealed class ChatMessage : ObservableObject
{
    public ChatRole Role { get; }

    /// <summary>Plain-string display label — kept here (not a WinUI enum/converter)
    /// so the UI-binding surface stays free of any WinUI dependency, same as the
    /// rest of Coral.Core. Role never changes after construction, so this needs
    /// no change notification of its own.</summary>
    public string RoleLabel => Role == ChatRole.User ? "You" : "Coral";

    private string _text;
    public string Text { get => _text; set => SetProperty(ref _text, value); }

    public ChatMessage(ChatRole role, string text)
    {
        Role = role;
        _text = text;
    }
}

/// <summary>One entry in the live agent-activity panel — a trimmed-down
/// <c>ActivityStep</c> (macOS also tracks delegation/per-agent attribution;
/// out of scope for this minimal P0, see PORT-PLAN.md Fase 5).</summary>
public sealed record ActivityStep(string Title, string? Detail, DateTimeOffset At)
{
    /// <summary>Binding-friendly non-null <see cref="Detail"/> — avoids the UI
    /// layer needing a null check/converter just to show an optional line.</summary>
    public string DetailOrEmpty => Detail ?? "";
}

/// <summary>
/// Drives one repo's headless-Claude chat: sends a turn, streams the reply,
/// and keeps a message list + activity timeline. Minimal P0 port of
/// <c>StrategyForge/ViewModels/ChatViewModel.swift</c> — only the plain
/// single-provider <c>-p</c> path (no "Ask" live-permission mode, no
/// cross-provider MetaOrchestrator, no persisted turn history); those are
/// each their own, much larger feature and stay out of scope here. Takes an
/// <see cref="IProcessLauncher"/> so it's unit-testable with a fake, same
/// pattern as <see cref="ClaudeRunner"/> itself.
/// </summary>
public sealed class ChatViewModel : ObservableObject
{
    private readonly IProcessLauncher _launcher;
    private readonly string _repoPath;
    private readonly string _binary;
    private readonly string _model;
    private readonly Func<string, string?>? _resolveBinary;
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private bool _hasSentFirstTurn;
    private CancellationTokenSource? _cts;

    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public ObservableCollection<ActivityStep> Activity { get; } = new();

    private string _promptText = "";
    public string PromptText { get => _promptText; set => SetProperty(ref _promptText, value); }

    private bool _isSending;
    public bool IsSending { get => _isSending; private set => SetProperty(ref _isSending, value); }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    /// <paramref name="resolveBinary"/> is injectable so this is unit-testable
    /// with a fake, same as <see cref="ClaudeRunner"/> itself — production
    /// callers leave it at its default (<see cref="BinaryResolver.Resolve"/>).
    public ChatViewModel(IProcessLauncher launcher, string repoPath,
        string binary = "claude", string model = "claude-sonnet-5",
        Func<string, string?>? resolveBinary = null)
    {
        _launcher = launcher;
        _repoPath = repoPath;
        _binary = binary;
        _model = model;
        _resolveBinary = resolveBinary;
    }

    /// <summary>Best-effort — cancels the in-flight turn, if any.</summary>
    public void CancelCurrentTurn() => _cts?.Cancel();

    /// <summary>Send <see cref="PromptText"/> as the next turn. No-op while a
    /// turn is already in flight or the prompt is blank.</summary>
    public async Task SendAsync()
    {
        var prompt = PromptText.Trim();
        if (prompt.Length == 0 || IsSending) return;

        Messages.Add(new ChatMessage(ChatRole.User, prompt));
        PromptText = "";
        StatusMessage = null;
        IsSending = true;

        try
        {
            // If --resume points at a session that no longer exists (e.g. its
            // history file was cleared), retry once as a fresh session rather
            // than surfacing a confusing error — mirrors ChatViewModel.swift's
            // sessionMissing handling.
            var resume = _hasSentFirstTurn;
            var sessionMissing = await RunTurnAsync(prompt, resume);
            if (sessionMissing) await RunTurnAsync(prompt, resume: false);
        }
        finally
        {
            IsSending = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Runs one turn to completion; returns true if it should be
    /// retried fresh because the resumed session no longer exists.</summary>
    private async Task<bool> RunTurnAsync(string prompt, bool resume)
    {
        ChatMessage? assistantMessage = null;
        var gotDelta = false;          // did live streaming deliver text this turn?
        var separatorPending = false;  // insert a blank line before the next text block
        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            await foreach (var evt in ClaudeRunner.Stream(_launcher, _binary, _repoPath, prompt, _model,
                _sessionId, resume, permissionMode: "default",
                inactivityTimeout: TimeSpan.FromMinutes(5), resolveBinary: _resolveBinary, ct: cts.Token))
            {
                switch (evt)
                {
                    case ChatEvent.AssistantDelta d:
                        assistantMessage ??= AddAssistantMessage();
                        gotDelta = true;
                        if (separatorPending && assistantMessage.Text.Length > 0) assistantMessage.Text += "\n\n";
                        separatorPending = false;
                        assistantMessage.Text += d.Text;
                        break;

                    case ChatEvent.AssistantText t:
                        if (gotDelta) break; // already streamed live via deltas
                        assistantMessage ??= AddAssistantMessage();
                        if (assistantMessage.Text.Length > 0) assistantMessage.Text += "\n\n";
                        assistantMessage.Text += t.Text;
                        break;

                    case ChatEvent.Tool tool:
                        Activity.Add(new ActivityStep(tool.Name, tool.Detail, DateTimeOffset.Now));
                        separatorPending = true;
                        break;

                    case ChatEvent.Delegated del:
                        Activity.Add(new ActivityStep($"→ {del.SubagentName}", null, DateTimeOffset.Now));
                        separatorPending = true;
                        break;

                    case ChatEvent.CommandStarted cmd:
                        Activity.Add(new ActivityStep("Ran a command", cmd.Command, DateTimeOffset.Now));
                        break;

                    case ChatEvent.FileEdited f:
                        Activity.Add(new ActivityStep("Edited " + Path.GetFileName(f.Path), f.Path, DateTimeOffset.Now));
                        break;

                    case ChatEvent.SkillUsed s:
                        Activity.Add(new ActivityStep("Skill: " + s.Slug, null, DateTimeOffset.Now));
                        separatorPending = true;
                        break;

                    case ChatEvent.Denied den:
                        Activity.Add(new ActivityStep("Blocked", string.Join(", ", den.Items), DateTimeOffset.Now));
                        break;

                    case ChatEvent.Usage u:
                        StatusMessage = $"{u.Tokens} tokens · ${u.CostUsd.ToString("F4", CultureInfo.InvariantCulture)}";
                        break;

                    case ChatEvent.Finished:
                        _hasSentFirstTurn = true;
                        break;

                    case ChatEvent.Failed failed:
                        if (resume && failed.Message.Contains("No conversation found",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return true; // caller retries fresh
                        }
                        StatusMessage = $"Error: {failed.Message}";
                        break;
                }
            }
            // A plain user cancellation makes ClaudeRunner.Stream stop yielding
            // quietly (it kills the process itself and returns, no exception) —
            // check the token directly rather than relying on a catch that may
            // never fire.
            if (cts.IsCancellationRequested) StatusMessage = "Cancelled.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        return false;
    }

    private ChatMessage AddAssistantMessage()
    {
        var m = new ChatMessage(ChatRole.Assistant, "");
        Messages.Add(m);
        return m;
    }
}
