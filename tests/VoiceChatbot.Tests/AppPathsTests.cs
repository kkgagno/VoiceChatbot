using VoiceChatbot;
using Xunit;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vc-apppaths-" + Guid.NewGuid().ToString("N"));

    private string MiniDir => Path.Combine(_root, "VoiceChatbotMini");
    private string FullDir => Path.Combine(_root, "VoiceChatbot");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void MiniHasItsOwnDataAndTempFolders()
    {
        Assert.Equal("VoiceChatbotMini", Path.GetFileName(AppPaths.DataDirectory));
        Assert.Equal("VoiceChatbot", Path.GetFileName(AppPaths.FullAppDataDirectory));
        Assert.NotEqual(AppPaths.DataDirectory, AppPaths.FullAppDataDirectory, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("VoiceChatbotMini", Path.GetFileName(AppPaths.TempDirectory));
        Assert.Equal(Path.Combine(AppPaths.DataDirectory, "settings.json"), AppPaths.SettingsFile);
    }

    [Fact]
    public void PathsAreBuiltInsideTheMiniFolders()
    {
        Assert.Equal(Path.Combine(AppPaths.DataDirectory, "logs"), AppPaths.DataPath("logs"));
        Assert.Equal(Path.Combine(AppPaths.DataDirectory, "phone-remote-cert"), AppPaths.DataPath("phone-remote-cert"));
        Assert.Equal(Path.Combine(AppPaths.TempDirectory, "document-ocr", "abc"), AppPaths.TempPath("document-ocr", "abc"));
        Assert.Equal(AppPaths.DataPath("logs"), AppLog.LogDirectory);
        Assert.Equal(AppPaths.DataPath("conversations"), ConversationStore.DefaultFolder);
    }

    [Fact]
    public void FirstRunCopiesOnlyTheFullAppsSettings()
    {
        Directory.CreateDirectory(FullDir);
        File.WriteAllText(Path.Combine(FullDir, "settings.json"), "{\"Model\":\"llama3\",\"HermesSshHost\":\"10.0.0.2\"}");
        File.WriteAllText(Path.Combine(FullDir, "scheduler.json"), "{}");
        Directory.CreateDirectory(Path.Combine(FullDir, "conversations"));

        var result = AppPaths.ImportFullAppSettingsIfMissing(MiniDir, FullDir);

        Assert.Equal(SettingsImportStatus.Imported, result.Status);
        Assert.Equal(File.ReadAllText(Path.Combine(FullDir, "settings.json")), File.ReadAllText(Path.Combine(MiniDir, "settings.json")));
        Assert.False(File.Exists(Path.Combine(MiniDir, "scheduler.json")));
        Assert.False(Directory.Exists(Path.Combine(MiniDir, "conversations")));
        Assert.Contains(Path.Combine(FullDir, "settings.json"), result.Describe());
    }

    [Fact]
    public void ExistingMiniSettingsAreNeverReplaced()
    {
        Directory.CreateDirectory(FullDir);
        Directory.CreateDirectory(MiniDir);
        File.WriteAllText(Path.Combine(FullDir, "settings.json"), "{\"Model\":\"full\"}");
        File.WriteAllText(Path.Combine(MiniDir, "settings.json"), "{\"Model\":\"mini\"}");

        var result = AppPaths.ImportFullAppSettingsIfMissing(MiniDir, FullDir);

        Assert.Equal(SettingsImportStatus.AlreadyHasSettings, result.Status);
        Assert.Equal("{\"Model\":\"mini\"}", File.ReadAllText(Path.Combine(MiniDir, "settings.json")));
        Assert.Equal("", result.Describe());
    }

    [Fact]
    public void NothingHappensWithoutTheFullApp()
    {
        var result = AppPaths.ImportFullAppSettingsIfMissing(MiniDir, FullDir);

        Assert.Equal(SettingsImportStatus.NothingToImport, result.Status);
        Assert.False(Directory.Exists(MiniDir));
        Assert.Equal("", result.Describe());
    }

    [Fact]
    public void AFailedCopyIsReportedNotThrown()
    {
        Directory.CreateDirectory(FullDir);
        File.WriteAllText(Path.Combine(FullDir, "settings.json"), "{}");
        // A file where the Mini data folder should be: the folder cannot be created.
        Directory.CreateDirectory(_root);
        File.WriteAllText(MiniDir, "not a folder");

        var result = AppPaths.ImportFullAppSettingsIfMissing(MiniDir, FullDir);

        Assert.Equal(SettingsImportStatus.Failed, result.Status);
        Assert.NotEqual("", result.Error);
        Assert.Contains("Using default settings", result.Describe());
    }

    [Fact]
    public void TrayTooltipNamesTheMiniEdition() =>
        Assert.Equal("Voice Chatbot Mini", TrayTooltip.Format(null, null));
}
