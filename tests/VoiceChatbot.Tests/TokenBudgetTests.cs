using VoiceChatbot;
using Xunit;

public class TokenBudgetTests
{
    [Fact]
    public void NormalRepliesUseTheMaxTokensSetting()
    {
        Assert.Equal(2048, TokenBudget.ResolveMaxTokens(2048, isArtifactRequest: false, contextWindow: 16384));
        Assert.Equal(TokenBudget.DefaultMaxTokens, TokenBudget.ResolveMaxTokens(0, isArtifactRequest: false, contextWindow: 16384));
    }

    [Fact]
    public void ArtifactsGetTheLargerBudgetButNeverMoreThanHalfTheWindow()
    {
        Assert.Equal(TokenBudget.ArtifactMaxTokens, TokenBudget.ResolveMaxTokens(2048, isArtifactRequest: true, contextWindow: 131072));
        Assert.Equal(8192, TokenBudget.ResolveMaxTokens(2048, isArtifactRequest: true, contextWindow: 16384));
        Assert.Equal(4096, TokenBudget.ResolveMaxTokens(8192, isArtifactRequest: false, contextWindow: 8192));
    }

    [Fact]
    public void OllamaWindowIsCappedBySetting()
    {
        Assert.Equal(16384, TokenBudget.ResolveContextWindow(262144, 16384, serverWindowIsFixed: false, fallback: 131072));
        Assert.Equal(8192, TokenBudget.ResolveContextWindow(8192, 16384, serverWindowIsFixed: false, fallback: 131072));
        Assert.Equal(16384, TokenBudget.ResolveContextWindow(null, 16384, serverWindowIsFixed: false, fallback: 131072));
    }

    [Fact]
    public void ZeroSettingUsesTheModelWindow()
    {
        Assert.Equal(262144, TokenBudget.ResolveContextWindow(262144, 0, serverWindowIsFixed: false, fallback: 131072));
        Assert.Equal(131072, TokenBudget.ResolveContextWindow(null, 0, serverWindowIsFixed: false, fallback: 131072));
    }

    [Fact]
    public void FixedServerWindowWins()
    {
        Assert.Equal(32768, TokenBudget.ResolveContextWindow(32768, 16384, serverWindowIsFixed: true, fallback: 131072));
        Assert.Equal(16384, TokenBudget.ResolveContextWindow(null, 16384, serverWindowIsFixed: true, fallback: 131072));
    }

    [Fact]
    public void FixedServerWindowWinsEvenWhenSmallOrBelowTheSetting()
    {
        // llama-server -c 2048 and a Context window box of 16384: the server's window is what fits.
        Assert.Equal(2048, TokenBudget.ResolveContextWindow(2048, 16384, serverWindowIsFixed: true, fallback: 131072));
        Assert.Equal(512, TokenBudget.ResolveContextWindow(512, 0, serverWindowIsFixed: true, fallback: 131072));
    }

    [Fact]
    public void NothingDetectedUsesTheSettingOrTheFallback()
    {
        Assert.Equal(16384, TokenBudget.ResolveContextWindow(null, 16384, serverWindowIsFixed: false, fallback: 131072));
        Assert.Equal(131072, TokenBudget.ResolveContextWindow(null, 0, serverWindowIsFixed: true, fallback: 131072));
        Assert.Equal(TokenBudget.MinContextWindow, TokenBudget.ResolveContextWindow(null, 100, serverWindowIsFixed: false, fallback: 131072));
    }

    [Fact]
    public void RepliesNeverTakeMoreThanHalfOfASmallServerWindow()
    {
        Assert.Equal(1024, TokenBudget.ResolveMaxTokens(2048, isArtifactRequest: false, contextWindow: 2048));
        Assert.Equal(1024, TokenBudget.ResolveMaxTokens(2048, isArtifactRequest: true, contextWindow: 2048));
        Assert.Equal(256, TokenBudget.ResolveMaxTokens(2048, isArtifactRequest: false, contextWindow: 512));
    }

    [Fact]
    public void PromptBudgetLeavesRoomForTheReplyAndAMargin()
    {
        // Unchanged for normal windows: window - reply - 4,096.
        Assert.Equal(10240, TokenBudget.PromptBudget(16384, 2048));
        Assert.Equal(124928, TokenBudget.PromptBudget(131072, 2048));
        Assert.Equal(4096, TokenBudget.PromptBudget(8192, 2048));
        // Small windows keep a quarter as margin instead of a fixed 4,096 that would not fit.
        Assert.Equal(1024, TokenBudget.PromptBudget(4096, 2048));
        Assert.Equal(512, TokenBudget.PromptBudget(2048, 1024));
        Assert.True(TokenBudget.PromptBudget(2048, 1024) + 1024 <= 2048);
        // No window known: no limit.
        Assert.Equal(int.MaxValue, TokenBudget.PromptBudget(0, 2048));
    }

    [Fact]
    public void PromptBudgetShrinksWhenTheEstimateUndercounted()
    {
        Assert.Equal(5120, TokenBudget.PromptBudget(16384, 2048, estimateScale: 2.0));
        Assert.Equal(10240, TokenBudget.PromptBudget(16384, 2048, estimateScale: 0.5));
    }

    [Fact]
    public void RetryScaleFollowsTheServersPromptCount()
    {
        // The server counted 20,000 tokens where the estimate said 10,000: twice as many, plus 10%.
        Assert.Equal(2.2, TokenBudget.RetryEstimateScale(20000, 10000, windowShrank: false), 3);
        // The estimate was high: never grow the budget.
        Assert.Equal(1.0, TokenBudget.RetryEstimateScale(5000, 10000, windowShrank: true));
        // No count from the server: a smaller window is enough, otherwise assume the estimate was low.
        Assert.Equal(1.0, TokenBudget.RetryEstimateScale(null, 10000, windowShrank: true));
        Assert.Equal(1.5, TokenBudget.RetryEstimateScale(null, 10000, windowShrank: false));
        Assert.Equal(8.0, TokenBudget.RetryEstimateScale(1_000_000, 10, windowShrank: false));
    }
}
