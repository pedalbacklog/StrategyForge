using System.Text.Json;

namespace Coral.Core.Services;

/// <summary>
/// Minimal persisted app settings — for now just Auto-PR's opt-in toggle
/// (Windows counterpart of <c>CodeModeView.swift</c>'s
/// <c>@AppStorage("code.autoPR")</c>). A plain JSON file rather than
/// <c>Windows.Storage.ApplicationData</c>: Coral is unpackaged for now (see
/// <c>PORT-PLAN.md</c> Fase 9), and <c>ApplicationData.LocalSettings</c>
/// needs package identity most unpackaged apps don't have. Same
/// pure-core-plus-real-wrapper shape as <see cref="ProviderAuth"/>:
/// <c>*Uncached</c> methods are fully unit-testable against a temp path,
/// <see cref="AutoPr"/> wires in the real <c>%LOCALAPPDATA%</c>.
/// </summary>
public static class AppSettings
{
    private sealed record Data(bool AutoPr = false);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Read the Auto-PR toggle from the settings file at
    /// <paramref name="path"/>. Missing file, unreadable file, or corrupt
    /// JSON all mean "off" — never throws, since a broken settings file
    /// shouldn't break Code Mode.</summary>
    public static bool ReadAutoPrUncached(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            return JsonSerializer.Deserialize<Data>(File.ReadAllText(path))?.AutoPr ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Write the Auto-PR toggle to the settings file at
    /// <paramref name="path"/>, creating its parent directory if needed.</summary>
    public static void WriteAutoPrUncached(string path, bool value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(new Data(value), JsonOptions));
    }

    /// <summary>The real settings file: <c>%LOCALAPPDATA%\Coral\settings.json</c>.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Coral", "settings.json");

    /// <summary>The Auto-PR toggle, backed by the real settings file.</summary>
    public static bool AutoPr
    {
        get => ReadAutoPrUncached(DefaultPath());
        set => WriteAutoPrUncached(DefaultPath(), value);
    }
}
