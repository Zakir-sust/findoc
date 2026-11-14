using FinDoc.Api.Chat;

namespace FinDoc.Tests.Chat;

public class ChatSettingsTests
{
    [Fact]
    public void Missing_provider_fails_and_names_Chat_Provider()
    {
        var chatSettings = new ChatSettings(); // Provider stays null

        var ex = Assert.Throws<InvalidOperationException>(() => chatSettings.Validate());

        Assert.Contains("Chat:Provider", ex.Message);
    }

    [Fact]
    public void Azure_without_an_api_key_fails_and_names_the_key()
    {
        // Endpoint and deployment are set, so the key is the only thing missing.
        // Without them, Validate would stop at the endpoint first and the message would name that instead.
        var chatSettings = new ChatSettings
        {
            Provider = ChatProvider.AzureOpenAI,
            AzureOpenAI = new AzureOpenAISettings
            {
                Endpoint = "https://example.openai.azure.com/",
                Deployment = "gpt-test",
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => chatSettings.Validate());

        Assert.Contains("Chat:AzureOpenAI:ApiKey", ex.Message);
    }

    [Fact]
    public void Complete_ollama_settings_pass()
    {
        // The other two tests would also pass if Validate always threw; this one rules that out.
        var chatSettings = new ChatSettings
        {
            Provider = ChatProvider.Ollama,
            Ollama = new OllamaSettings { Endpoint = "http://localhost:11434", Model = "qwen3.5:9b" },
        };

        chatSettings.Validate(); // throws = fails the test
    }
}
