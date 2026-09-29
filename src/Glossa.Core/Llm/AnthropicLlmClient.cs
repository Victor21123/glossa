using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Beta = Anthropic.Models.Beta.Messages;
using Api = Anthropic.Models.Messages;

namespace Glossa.Core.Llm;

/// <summary>
/// Claude through the official Anthropic SDK: streaming, structured JSON output, adaptive thinking at low effort
/// (a dictionary card is a short extraction task) and a server-side fallback when a request is declined.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    /// <summary>Models offered in settings; the first is the default.</summary>
    public static readonly string[] SuggestedModels =
        ["claude-opus-5", "claude-sonnet-5", "claude-haiku-4-5", "claude-fable-5-1"];

    private readonly AnthropicClient _client;

    public AnthropicLlmClient(LlmEndpoint endpoint)
    {
        Endpoint = endpoint;
        _client = new AnthropicClient { ApiKey = endpoint.ApiKey };
    }

    public LlmEndpoint Endpoint { get; }

    public async IAsyncEnumerable<string> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var model = Endpoint.Model;
        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == "system").Select(m => m.Content));
        var messages = request.Messages
            .Where(m => m.Role != "system")
            .Select(m => new Beta.BetaMessageParam
            {
                Role = m.Role == "assistant" ? Beta.Role.Assistant : Beta.Role.User,
                Content = m.Content,
            })
            .ToList();

        var p = new Beta.MessageCreateParams
        {
            Model = model,
            // Thinking shares this budget; a card needs well under 1k tokens of JSON.
            MaxTokens = 16000,
            Messages = messages,
        };
        if (system.Length > 0) p = p with { System = system };

        var caps = Capabilities(model);
        var output = new Beta.BetaOutputConfig();
        if (caps.Effort) output = output with { Effort = Beta.Effort.Low };
        if (request.JsonSchema is { } schema)
            output = output with { Format = new Beta.BetaJsonOutputFormat { Schema = ToSchema(schema) } };
        if (caps.Effort || request.JsonSchema is not null) p = p with { OutputConfig = output };
        if (caps.AdaptiveThinking) p = p with { Thinking = new Beta.BetaThinkingConfigAdaptive() };
        if (caps.Fallback)
            p = p with
            {
                Betas = [Anthropic.Models.Beta.AnthropicBeta.ServerSideFallback2026_06_01],
                Fallbacks = new List<Beta.BetaFallbackParam> { new(Api.Model.ClaudeOpus4_8) },
            };

        await foreach (var ev in _client.Beta.Messages.CreateStreaming(p, ct).ConfigureAwait(false))
        {
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                yield return text.Text;
        }
    }

    private sealed record Caps(bool AdaptiveThinking, bool Effort, bool Fallback);

    /// <summary>
    /// Haiku 4.5 takes neither adaptive thinking nor effort; the Opus 5 / Fable 5.1 family can decline requests
    /// through its safety classifiers, so those get a server-side fallback.
    /// </summary>
    private static Caps Capabilities(string model) => model switch
    {
        _ when model.StartsWith("claude-haiku", StringComparison.Ordinal) => new(false, false, false),
        "claude-opus-5" or "claude-fable-5-1" => new(true, true, true),
        _ when model.StartsWith("claude-opus-5", StringComparison.Ordinal) => new(true, true, false),
        _ when model.StartsWith("claude-sonnet-5", StringComparison.Ordinal)
               || model.StartsWith("claude-opus-4", StringComparison.Ordinal)
               || model.StartsWith("claude-fable", StringComparison.Ordinal) => new(true, true, false),
        _ => new(false, false, false),
    };

    private static Dictionary<string, JsonElement> ToSchema(System.Text.Json.Nodes.JsonObject schema) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(schema.ToJsonString())!;
}
