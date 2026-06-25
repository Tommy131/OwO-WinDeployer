using WinDeploy.Core.Config;
using Xunit;

namespace WinDeploy.Core.Tests;

public class SecretsTests
{
    private const string Mask = "***REDACTED***";

    [Fact]
    public void Redact_MasksGitHubTokenBlob()
    {
        var (text, count) = Secrets.Redact("token is ghp_ABCDEF1234567890abcdef done");
        Assert.True(count >= 1);
        Assert.DoesNotContain("ghp_ABCDEF1234567890abcdef", text);
        Assert.Contains(Mask, text);
    }

    [Fact]
    public void Redact_MasksJsonSecretValue()
    {
        var (text, count) = Secrets.Redact("{ \"apiKey\": \"super-secret-value\" }");
        Assert.True(count >= 1);
        Assert.DoesNotContain("super-secret-value", text);
        // The key name and JSON structure survive; only the value is masked.
        Assert.Contains("\"apiKey\"", text);
        Assert.Contains(Mask, text);
    }

    [Fact]
    public void Redact_MasksIniSecretValue()
    {
        var (text, count) = Secrets.Redact("password = hunter2");
        Assert.True(count >= 1);
        Assert.DoesNotContain("hunter2", text);
        Assert.Contains(Mask, text);
    }

    [Fact]
    public void Redact_LeavesPlainTextUntouched()
    {
        var input = "this is just ordinary config with no secrets";
        var (text, count) = Secrets.Redact(input);
        Assert.Equal(0, count);
        Assert.Equal(input, text);
    }

    [Fact]
    public void Redact_CountsMultipleSecrets()
    {
        var (_, count) = Secrets.Redact("password = a\ntoken = b");
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("config.yml")]
    [InlineData("app.ini")]
    [InlineData("notes.txt")]
    [InlineData(".gitconfig")]
    [InlineData(".npmrc")]
    [InlineData("config")]
    public void IsTextConfig_ClassifiesTextLikeFiles(string fileName)
    {
        Assert.True(Secrets.IsTextConfig(fileName));
    }

    [Theory]
    [InlineData("image.png")]
    [InlineData("archive.zip")]
    [InlineData("library.dll")]
    [InlineData("photo.jpg")]
    public void IsTextConfig_RejectsBinaryFiles(string fileName)
    {
        Assert.False(Secrets.IsTextConfig(fileName));
    }
}
