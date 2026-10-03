namespace AiDesktopSetup.Core.Workflow;

public static class SetupFailurePresentation
{
    public const string Generic = "Setup could not continue. Recovery was retained. Retry, or cancel to clear recovery; existing files and private backups are preserved.";
    // SetupException carries local authored guidance. Never include inner exceptions,
    // remote responses, process output or arbitrary helper-pipe error text here.
    public static string Message(Exception error)
    {
        if (error is not SetupException || string.IsNullOrWhiteSpace(error.Message) || error.Message.Length > 4096
            || error.Message.Any(char.IsControl)) return Generic;
        return error.Message;
    }
}
