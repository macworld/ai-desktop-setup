namespace AiDesktopSetup.Core;

/// <summary>Explicit local configuration input. Authentication and protocol validation are supplied by the workflow.</summary>
public sealed class CodexConfiguration
{
    public string ApiBaseUrl { get; }
    public string Model { get; }
    public string? ReasoningEffort { get; }

    public CodexConfiguration(string apiBaseUrl, string model, string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl) || string.IsNullOrWhiteSpace(model) || (reasoningEffort != null && string.IsNullOrWhiteSpace(reasoningEffort)))
            throw new SetupException("Configuration requires an API base URL and model.");
        ApiBaseUrl = apiBaseUrl; Model = model; ReasoningEffort = reasoningEffort;
    }
}
