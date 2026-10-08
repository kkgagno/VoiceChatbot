using VoiceChatbot;
using Xunit;

public sealed class FileLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vc-log-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Write_CreatesDailyFileWithTimestampAndLevel()
    {
        var now = new DateTime(2026, 10, 8, 14, 3, 12, 345);
        var logger = new FileLogger(_dir, clock: () => now);

        logger.Info("Started");
        logger.Error("Backend failed", new InvalidOperationException("no server"));

        var path = Path.Combine(_dir, "app-20261008.log");
        Assert.Equal(path, logger.GetFilePath(now));
        var lines = File.ReadAllLines(path);
        Assert.Equal("2026-10-08 14:03:12.345 [INFO ] Started", lines[0]);
        Assert.StartsWith("2026-10-08 14:03:12.345 [ERROR] Backend failed", lines[1]);
        Assert.Contains(lines, l => l.StartsWith("    System.InvalidOperationException: no server"));
    }

    [Fact]
    public void Write_IndentsContinuationLines()
    {
        var logger = new FileLogger(_dir, clock: () => new DateTime(2026, 1, 2));
        logger.Info("first\r\nsecond\nthird\n");

        var lines = File.ReadAllLines(logger.GetFilePath(new DateTime(2026, 1, 2)));
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("[INFO ] first", lines[0]);
        Assert.Equal("    second", lines[1]);
        Assert.Equal("    third", lines[2]);
    }

    [Fact]
    public void Write_RollsOverAndKeepsSevenDays()
    {
        var now = new DateTime(2026, 10, 1, 9, 0, 0);
        var logger = new FileLogger(_dir, clock: () => now);
        Directory.CreateDirectory(_dir);
        var unrelated = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(unrelated, "keep me");
        var odd = Path.Combine(_dir, "app-latest.log");
        File.WriteAllText(odd, "keep me too");

        for (var day = 0; day < 10; day++)
        {
            now = new DateTime(2026, 10, 1 + day, 9, 0, 0);
            logger.Info($"day {day}");
        }

        var kept = Directory.GetFiles(_dir, "app-2026*.log").Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(7, kept.Length);
        Assert.Equal("app-20261004.log", kept[0]);
        Assert.Equal("app-20261010.log", kept[^1]);
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(odd));
    }

    [Fact]
    public void Write_MasksSecrets()
    {
        var logger = new FileLogger(_dir, clock: () => new DateTime(2026, 3, 3))
        {
            SecretsProvider = () => new[] { "sk-live-abc", null, "", "abc" }
        };

        logger.Warn("Request with sk-live-abc failed for abc", new Exception("token sk-live-abc rejected"));

        var text = File.ReadAllText(logger.GetFilePath(new DateTime(2026, 3, 3)));
        Assert.DoesNotContain("sk-live-abc", text);
        Assert.Contains("[WARN ] Request with **** failed", text);
        Assert.Contains("token **** rejected", text);
        // Values shorter than four characters are not masked; they would hide too much.
        Assert.Contains("failed for abc", text);
    }

    [Fact]
    public void Write_NeverThrows()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "vc-log-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "a file where the log directory should be");
        try
        {
            var logger = new FileLogger(blocker)
            {
                SecretsProvider = () => throw new InvalidOperationException("provider failed")
            };
            logger.Info("goes nowhere");
            logger.Error(null, null);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task Write_IsThreadSafe()
    {
        var logger = new FileLogger(_dir, clock: () => new DateTime(2026, 5, 5));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
                logger.Info($"thread {t} line {i}");
        })));

        var lines = File.ReadAllLines(logger.GetFilePath(new DateTime(2026, 5, 5)));
        Assert.Equal(400, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"^2026-05-05 00:00:00\.000 \[INFO \] thread \d line \d+$", l));
    }
}
