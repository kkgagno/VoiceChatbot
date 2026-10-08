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
}
