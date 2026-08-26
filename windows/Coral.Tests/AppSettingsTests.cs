using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for AppSettings' pure *Uncached core against a real temp
/// file — mirrors ProviderAuthTests's style (FreshnessUncached against a
/// temp directory). Never touches the real %LOCALAPPDATA%.</summary>
public class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coral-settings-tests-" + Guid.NewGuid());
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ReadAutoPrUncachedDefaultsToFalseWhenTheFileDoesntExist()
    {
        Assert.False(AppSettings.ReadAutoPrUncached(SettingsPath));
    }

    [Fact]
    public void WriteThenReadRoundTripsTrue()
    {
        AppSettings.WriteAutoPrUncached(SettingsPath, true);

        Assert.True(AppSettings.ReadAutoPrUncached(SettingsPath));
    }

    [Fact]
    public void WriteThenReadRoundTripsFalse()
    {
        AppSettings.WriteAutoPrUncached(SettingsPath, true);
        AppSettings.WriteAutoPrUncached(SettingsPath, false);

        Assert.False(AppSettings.ReadAutoPrUncached(SettingsPath));
    }

    [Fact]
    public void WriteUncachedCreatesTheParentDirectoryIfMissing()
    {
        Assert.False(Directory.Exists(_dir));

        AppSettings.WriteAutoPrUncached(SettingsPath, true);

        Assert.True(File.Exists(SettingsPath));
    }

    [Fact]
    public void ReadAutoPrUncachedToleratesCorruptJson()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ not valid json");

        Assert.False(AppSettings.ReadAutoPrUncached(SettingsPath));
    }

    [Fact]
    public void DefaultPathPointsUnderLocalAppDataCoral()
    {
        var path = AppSettings.DefaultPath();

        Assert.Contains("Coral", path);
        Assert.EndsWith("settings.json", path);
    }
}
