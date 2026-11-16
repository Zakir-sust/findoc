using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;

namespace FinDoc.Api.Chat;

public static class ChatClientSetup
{
    /// <summary>
    /// Registers one <see cref="IChatClient"/> (a singleton) for the provider named in Chat:Provider.
    /// Endpoints ask for IChatClient and never know which provider is behind it.
    /// </summary>
    public static IServiceCollection AddFinDocChatClient(this IServiceCollection services, IConfiguration configuration)
    {
        // Read and check the settings now, not on the first request: a missing key stops start-up with a clear message.
        var settings = configuration.GetSection(ChatSettings.SectionName).Get<ChatSettings>()
            ?? throw new InvalidOperationException("The \"Chat\" section is missing from appsettings.json.");
        settings.Validate();

        // AddChatClient registers the provider's client and returns a ChatClientBuilder. Each layer added to the
        // builder wraps the client; the first one added is the outermost. A request goes:
        //   ConfigureOptions (Ollama only) -> UseLogging -> provider client
        var pipeline = settings.Provider switch
        {
            ChatProvider.Ollama => services
                .AddChatClient(CreateOllamaClient(settings.Ollama))
                .ConfigureOptions(ApplyOllamaDefaults),
            ChatProvider.AzureOpenAI => services
                .AddChatClient(CreateAzureOpenAIClient(settings.AzureOpenAI)),
            _ => throw new InvalidOperationException($"Unknown Chat:Provider '{settings.Provider}'."),
        };

        // UseLogging: logs "GetStreamingResponseAsync invoked/completed" at Debug, and at Trace also the full
        // messages and options sent (so you can see the real prompt). Trace contains user data: dev only.
        // It sits inside ConfigureOptions, so Trace shows the options the provider actually receives.
        pipeline.UseLogging();

        return services;
    }

    /// <summary>
    /// Ollama-only defaults, applied to every request (ConfigureOptions gets a copy of the caller's ChatOptions, or a new one):
    /// <list type="bullet">
    /// <item>num_ctx 16384: Ollama runs qwen3.5:9b with a 4096-token context by default and silently drops the
    /// start of a longer prompt (the system message first). 16k leaves room for the retrieved chunks from C13.</item>
    /// <item>think false: qwen3.5 otherwise "thinks" before answering, which delays the first streamed token
    /// and isn't shown to the user anyway.</item>
    /// </list>
    /// Azure OpenAI needs neither: the deployed model sets its own context window.
    /// </summary>
    private static void ApplyOllamaDefaults(ChatOptions options)
    {
        options.AddOllamaOption(OllamaOption.NumCtx, 16384);
        options.AddOllamaOption(OllamaOption.Think, false);
    }

    private static IChatClient CreateOllamaClient(OllamaSettings ollama)
    {
        // The default HttpClient.Timeout (100 s) cut off answers in earlier blocks: loading the model plus a long
        // prompt can take minutes on a laptop. One HttpClient for the app's lifetime, because the chat client is a singleton.
        var http = new HttpClient
        {
            BaseAddress = new Uri(ollama.Endpoint),
            Timeout = TimeSpan.FromMinutes(5),
        };
        return new OllamaApiClient(http, ollama.Model);
    }

    // Tested 15 Nov 2026 (C14) against a gpt-4.1-mini Global Standard deployment.
    // Endpoint, deployment and key come from user secrets or env vars, never from appsettings.json.
    // AsIChatClient (Microsoft.Extensions.AI.OpenAI) adapts the OpenAI SDK's ChatClient to IChatClient;
    // on Azure the "model" is the deployment name.
    private static IChatClient CreateAzureOpenAIClient(AzureOpenAISettings azure) =>
        new AzureOpenAIClient(new Uri(azure.Endpoint), new ApiKeyCredential(azure.ApiKey))
            .GetChatClient(azure.Deployment)
            .AsIChatClient();
}
