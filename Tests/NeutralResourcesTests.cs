using AiDesktopSetup.Core;
namespace AiDesktopSetup.Tests;
public class NeutralResourcesTests
{
    [Fact]
    public void HasNoProviderDefaults()
    {
        var constructors = typeof(CodexConfiguration).GetConstructors();
        var constructor = Assert.Single(constructors);
        var resources = constructor.GetParameters();
        var providerDefaults = resources.Where(parameter => parameter.HasDefaultValue);
        Assert.Empty(providerDefaults);
        Assert.Equal(3, resources.Length);
        var merged = ConservativeToml.Merge("", new CodexConfiguration("https://gateway.example/tenant/api/", "example-model", "high"));
        Assert.Contains("base_url = \"https://gateway.example/tenant/api/\"", merged);
        Assert.Contains("model = \"example-model\"", merged);
        Assert.Contains("model_provider = \"ai_gateway\"", merged);
    }

    [Theory]
    [InlineData("", "model", "high")]
    [InlineData("https://gateway.example/v1", "", "high")]
    [InlineData("https://gateway.example/v1", "model", "")]
    public void MissingConfigurationIsRejected(string url, string model, string effort)
        => Assert.Throws<SetupException>(() => new CodexConfiguration(url, model, effort));
}
