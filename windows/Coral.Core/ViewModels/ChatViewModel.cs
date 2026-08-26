using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
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

/// <summary>One entry in the live agent-activity panel. Port of
/// <c>ActivityStep.swift</c>'s shape (minus persistence/Codable, not needed
/// here yet): <see cref="IsDelegation"/>/<see cref="Agent"/> attribute each
/// step to whichever subagent was active when it happened (null = the
/// orchestrator) — <see cref="Coral.Core.Generators.MissionReport.AgentLines"/>
/// uses this to derive per-agent stats.</summary>
public sealed record ActivityStep(string Title, string? Detail, DateTimeOffset At,
    bool IsDelegation = false, string? Agent = null)
{
    /// <summary>Binding-friendly non-null <see cref="Detail"/> — avoids the UI
    /// layer needing a null check/converter just to show an optional line.</summary>
    public string DetailOrEmpty => Detail ?? "";
}

/// <summary>One shell command the agent ran, with its output (for the
/// code-mode terminal). Port of <c>ChatViewModel.swift</c>'s <c>CommandRun</c>.</summary>
public sealed record CommandRun(string Command, string Output, DateTimeOffset At);

/// <summary>
/// Drives one repo's headless-Claude chat: sends a turn, streams the reply,
/// and keeps a message list + activity timeline. Minimal P0 port of
/// <c>StrategyForge/ViewModels/ChatViewModel.swift</c> — only the plain
/// single-provider <c>-p</c> path (no "Ask" live-permission mode, no
/// cross-provider MetaOrchestrator, no persisted turn history); those are
/// each their own, much larger feature and stay out of scope here. Runs
/// with <c>permission-mode bypassPermissions</c> as a direct consequence of
/// that gap: Swift's own default (<c>acceptEdits</c>) still denies plenty of
/// tool calls, and its escape hatch when that happens (the live "Ask" UI's
/// "retry allowing all") needs a mid-turn channel this one-shot headless run
/// doesn't have — see <see cref="RunTurnAsync"/>. Takes an
/// <see cref="IProcessLauncher"/> so it's unit-testable with a fake, same
/// pattern as <see cref="ClaudeRunner"/> itself.
/// </summary>
public sealed class ChatViewModel : ObservableObject
{
    private readonly IProcessLauncher _launcher;
    private readonly string _repoPath;
    private readonly string _binary;
    private readonly Func<string, string?>? _resolveBinary;
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private bool _hasSentFirstTurn;
    private CancellationTokenSource? _cts;

    /// <summary>Which subagent is currently delegated to, if any — null means
    /// the orchestrator. Reset at the start of each turn (matches
    /// ChatViewModel.swift's `activeSubagent = nil` on send); every step
    /// added while a subagent is active gets attributed to it.</summary>
    private string? _activeSubagent;

    /// <summary>Bash commands started this turn, keyed by their tool_use id,
    /// awaiting the matching tool_result. Cleared at the start of each turn,
    /// same as <see cref="_activeSubagent"/> — a stray unmatched entry from a
    /// cancelled/failed turn shouldn't bleed into the next one.</summary>
    private readonly Dictionary<string, string> _pendingCommands = new();

    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public ObservableCollection<ActivityStep> Activity { get; } = new();

    /// <summary>Commands run this session, for Code Mode's terminal panel.
    /// Port of <c>ChatViewModel.swift</c>'s <c>commandLog</c>. Unlike Swift
    /// (which clears this per turn), kept accumulating for the whole session —
    /// matching the convention <see cref="Activity"/> already established in
    /// this port, for internal consistency.</summary>
    public ObservableCollection<CommandRun> CommandLog { get; } = new();

    private string _promptText = "";
    public string PromptText { get => _promptText; set => SetProperty(ref _promptText, value); }

    private bool _isSending;
    public bool IsSending { get => _isSending; private set => SetProperty(ref _isSending, value); }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    /// <summary>The orchestrator model the next turn runs on. A plain mutable
    /// property (not <c>SetProperty</c>-backed) rather than a constructor-only
    /// value: nothing in the UI displays it yet, so it doesn't need change
    /// notification — <c>StrategyPickerViewModel</c>'s caller (<c>MainPage.xaml.cs</c>)
    /// sets this after writing a selected template, so the picked strategy's
    /// suggested model takes effect on the very next turn without restarting
    /// the chat or losing the transcript/session id.</summary>
    public string Model { get; set; }

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
        Model = model;
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
        _activeSubagent = null;
        _pendingCommands.Clear();

        try
        {
            // "bypassPermissions", not Swift's own default ("acceptEdits") — a
            // second real bug, found right after fixing the first: acceptEdits
            // alone still denies plenty of tool calls (confirmed live — a
            // requested `git add`/`git commit` sat asking for approval that
            // never arrived). Swift's app can recover from that because it has
            // a live "Ask" permission UI (ChatViewModel.swift's pendingPermission/
            // respondPermission/retryAllowingAll — deliberately out of scope
            // here, see this type's doc comment) that resumes the SAME run with
            // "bypassPermissions" once the user answers. A one-shot headless -p
            // run has no equivalent mid-turn channel to ask through, so a denial
            // here is a dead end, not a recoverable prompt — bypassing from the
            // start is the only sane option until "Ask" mode gets ported.
            await foreach (var evt in ClaudeRunner.Stream(_launcher, _binary, _repoPath, prompt, Model,
                _sessionId, resume, permissionMode: "bypassPermissions",
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
                        Activity.Add(new ActivityStep(tool.Name, tool.Detail, DateTimeOffset.Now,
                            Agent: _activeSubagent));
                        separatorPending = true;
                        break;

                    case ChatEvent.Delegated del:
                        // The delegation itself is an orchestrator action (Agent: null) —
                        // it's the STEPS the subagent takes afterward that get attributed
                        // to it, via _activeSubagent below.
                        _activeSubagent = del.SubagentName;
                        Activity.Add(new ActivityStep($"→ {del.SubagentName}", null, DateTimeOffset.Now,
                            IsDelegation: true));
                        separatorPending = true;
                        break;

                    case ChatEvent.CommandStarted cmd:
                        Activity.Add(new ActivityStep("Ran a command", cmd.Command, DateTimeOffset.Now,
                            Agent: _activeSubagent));
                        _pendingCommands[cmd.Id] = cmd.Command;
                        break;

                    case ChatEvent.CommandOutput output:
                        // Only surface output for commands we tracked (Bash),
                        // not every tool_result (e.g. Read/Grep also flow
                        // through here).
                        if (_pendingCommands.Remove(output.Id, out var command))
                        {
                            CommandLog.Add(new CommandRun(command, Trimmed(output.Output), DateTimeOffset.Now));
                        }
                        break;

                    case ChatEvent.FileEdited f:
                        Activity.Add(new ActivityStep("Edited " + Path.GetFileName(f.Path), f.Path, DateTimeOffset.Now,
                            Agent: _activeSubagent));
                        break;

                    case ChatEvent.SkillUsed s:
                        Activity.Add(new ActivityStep("Skill: " + s.Slug, null, DateTimeOffset.Now,
                            Agent: _activeSubagent));
                        separatorPending = true;
                        break;

                    case ChatEvent.Denied den:
                        Activity.Add(new ActivityStep("Blocked", string.Join(", ", den.Items), DateTimeOffset.Now,
                            Agent: _activeSubagent));
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

    /// <summary>Draft a commit message from the agent's last reply — its first
    /// line, capped at 64 chars. Empty if no assistant message has arrived
    /// yet. Port of <c>CodeModeView.swift</c>'s <c>draftMessage()</c>, moved
    /// here (rather than Code Mode's own ViewModel) since it only needs
    /// <see cref="Messages"/>, which this type already owns.</summary>
    public string DraftCommitMessage()
    {
        var last = Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text;
        if (string.IsNullOrEmpty(last)) return "";
        var firstLine = last.Split('\n')[0].Trim();
        return firstLine.Length > 64 ? firstLine[..64] + "…" : firstLine;
    }

    /// <summary>Draft a PR body from the agent's last reply, capped at 1200
    /// chars, with a footer. Port of <c>CodeModeView.swift</c>'s <c>prBody()</c>.</summary>
    public string DraftPrBody()
    {
        const string footer = "\n\n— Opened from Coral.";
        var last = (Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text ?? "").Trim();
        if (last.Length == 0) return "Opened from Coral.";
        var capped = last.Length > 1200 ? last[..1200] + "…" : last;
        return capped + footer;
    }

    /// <summary>Port of <c>ChatViewModel.swift</c>'s <c>trimmed(_:limit:)</c>: a
    /// build/test can emit hundreds of KB; kept verbatim ×50 turns ×every
    /// resident chat that adds up fast. Keeps head+tail (~<paramref name="limit"/>
    /// chars total), enough to read the gist, and elides the middle. Uses plain
    /// UTF-16 code-unit slicing rather than Swift's Character-boundary
    /// counting — a pragmatic difference that doesn't matter for a truncation
    /// heuristic.</summary>
    public static string Trimmed(string s, int limit = 12_000)
    {
        if (s.Length <= limit) return s;
        var head = s[..(limit * 2 / 3)];
        var tail = s[^(limit / 3)..];
        var elided = s.Length - head.Length - tail.Length;
        return $"{head}\n… [{elided} chars elided] …\n{tail}";
    }
}
