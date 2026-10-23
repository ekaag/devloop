using System.ClientModel;
using OpenAI.Responses;

namespace DevLoop;

#pragma warning disable OPENAI001 // Responses API surface is marked experimental in the SDK.
// OpenAI Responses API adapter producing schema-constrained JSON. Responses are not stored server-side.
public sealed class OpenAIStructuredModelClient : IStructuredModelClient
{
    private readonly ResponsesClient client;
    private readonly CriticOptions options;

    public OpenAIStructuredModelClient(CriticOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new InvalidOperationException("Critic:ApiKey is not configured. Store it with dotnet user-secrets.");
        if (string.IsNullOrWhiteSpace(options.Model)) throw new InvalidOperationException("Critic:Model is not configured.");
        this.options = options;
        var clientOptions = new ResponsesClientOptions { NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
        if (!string.IsNullOrWhiteSpace(options.Endpoint)) clientOptions.Endpoint = new Uri(options.Endpoint);
        client = new ResponsesClient(new ApiKeyCredential(options.ApiKey), clientOptions);
    }

    public async Task<StructuredModelResponse> CompleteJsonAsync(StructuredModelRequest request, CancellationToken token)
    {
        var create = new CreateResponseOptions(options.Model, [ResponseItem.CreateUserMessageItem(request.Input)])
        {
            Instructions = request.Instructions,
            MaxOutputTokenCount = request.MaxOutputTokens,
            StoredOutputEnabled = false,
            TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(request.SchemaName, BinaryData.FromString(request.JsonSchema), null, true)
            }
        };
        var response = (await client.CreateResponseAsync(create, token)).Value;
        var usage = response.Usage is { } u
            ? new ModelUsage("openai", response.Model ?? options.Model, u.InputTokenCount, u.OutputTokenCount, u.InputTokenDetails?.CachedTokenCount ?? 0)
            : null;
        var refusal = response.OutputItems.OfType<MessageResponseItem>().SelectMany(item => item.Content)
            .Select(part => part.Refusal).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        if (refusal is not null) return new(null, false, $"model refused: {refusal}", usage);
        if (response.Status != ResponseStatus.Completed)
            return new(null, false, $"status {response.Status}; {response.IncompleteStatusDetails?.Reason}; {response.Error?.Message}".Trim(' ', ';'), usage);
        return new(response.GetOutputText(), true, null, usage);
    }
}
