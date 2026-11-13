namespace FinDoc.Api.Chat;

public enum ChatProvider
{
    Ollama,
    AzureOpenAI,
}

/// <summary>
/// The "Chat" section of appsettings.json: which provider answers <c>POST /chat</c>, and its settings.
/// Called ChatSettings, not ChatOptions, so it doesn't clash with <c>Microsoft.Extensions.AI.ChatOptions</c>.
/// </summary>
public sealed class ChatSettings
{
    public const string SectionName = "Chat";

    /// <summary>"Ollama" (local) or "AzureOpenAI" (hosted). Nullable so a missing value fails instead of quietly meaning Ollama.</summary>
    public ChatProvider? Provider { get; set; }

    public OllamaSettings Ollama { get; set; } = new();

    public AzureOpenAISettings AzureOpenAI { get; set; } = new();

    /// <summary>Checks only the chosen provider's settings, and names the missing key in the error.</summary>
    public void Validate()
    {
        switch (Provider)
        {
            case null:
                throw new InvalidOperationException(
                    "Chat:Provider is missing. Set it to \"Ollama\" or \"AzureOpenAI\" in appsettings.json.");

            case ChatProvider.Ollama:
                RequireUrl(Ollama.Endpoint, "Chat:Ollama:Endpoint");
                Require(Ollama.Model, "Chat:Ollama:Model");
                break;

            case ChatProvider.AzureOpenAI:
                RequireUrl(AzureOpenAI.Endpoint, "Chat:AzureOpenAI:Endpoint");
                Require(AzureOpenAI.Deployment, "Chat:AzureOpenAI:Deployment");
                Require(AzureOpenAI.ApiKey, "Chat:AzureOpenAI:ApiKey",
                    "Set it with `dotnet user-secrets set \"Chat:AzureOpenAI:ApiKey\" \"<key>\" --project src/FinDoc.Api` " +
                    "or the env var Chat__AzureOpenAI__ApiKey. Never put it in appsettings.json.");
                break;
        }
    }

    private static void Require(string value, string key, string? hint = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{key} is missing. {hint}".TrimEnd());
    }

    private static void RequireUrl(string value, string key)
    {
        Require(value, key);
        if (!Uri.TryCreate(value, UriKind.Absolute, out _))
            throw new InvalidOperationException($"{key} must be an absolute URL, got \"{value}\".");
    }
}

public sealed class OllamaSettings
{
    /// <summary>e.g. http://localhost:11434</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>e.g. qwen3.5:9b (must be pulled already: <c>ollama pull qwen3.5:9b</c>).</summary>
    public string Model { get; set; } = "";
}

public sealed class AzureOpenAISettings
{
    /// <summary>e.g. https://&lt;resource&gt;.openai.azure.com/</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>The deployment name chosen in Foundry (not the model name). Azure routes by deployment.</summary>
    public string Deployment { get; set; } = "";

    /// <summary>Secret: user secrets or an env var only, never a committed file.</summary>
    public string ApiKey { get; set; } = "";
}
