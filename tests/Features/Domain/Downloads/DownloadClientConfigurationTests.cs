using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Domain.Downloads;

[Trait("Name", "DownloadClientConfigurationTests")]
[Trait("Category", "Domain")]
public sealed class DownloadClientConfigurationTests
    : BaseTests
{
    [Fact]
    public void SourceCaseSensitivityMode_MissingSetting_DefaultsToAuto()
    {
        var client = new DownloadClientConfiguration();

        Assert.Equal(
            FileSystemCaseSensitivityMode.Auto,
            client.GetSourceCaseSensitivityMode());
    }

    [Theory]
    [InlineData("Auto", FileSystemCaseSensitivityMode.Auto)]
    [InlineData("sensitive", FileSystemCaseSensitivityMode.Sensitive)]
    [InlineData("INSENSITIVE", FileSystemCaseSensitivityMode.Insensitive)]
    public void SourceCaseSensitivityMode_ValidName_IsParsed(
        string configuredValue,
        FileSystemCaseSensitivityMode expected)
    {
        var client = new DownloadClientConfiguration
        {
            Settings = new Dictionary<string, object>
            {
                [DownloadClientConfiguration.SourceCaseSensitivityModeSetting] =
                    configuredValue
            }
        };

        Assert.Equal(expected, client.GetSourceCaseSensitivityMode());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("detect")]
    public void SourceCaseSensitivityMode_InvalidValue_FailsClearly(string value)
    {
        var client = new DownloadClientConfiguration
        {
            Name = "Test client",
            Settings = new Dictionary<string, object>
            {
                [DownloadClientConfiguration.SourceCaseSensitivityModeSetting] =
                    value
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => client.GetSourceCaseSensitivityMode());

        Assert.Contains(
            DownloadClientConfiguration.SourceCaseSensitivityModeSetting,
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("Auto, Sensitive, or Insensitive", exception.Message);
    }
}
