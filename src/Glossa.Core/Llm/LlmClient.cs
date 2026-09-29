using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glossa.Core.Llm;

public enum LlmProviderKind
{
    /// <summary>llama.cpp llama-server started by Glossa itself.</summary>
    LlamaServer,
    /// <summary>Any /v1/chat/completions API: OpenAI, OpenRouter, DeepSeek, Gemini-compat, LM Studio, Ollama.</summary>
    OpenAiCompatible,
    Anthropic,
}

/// <summary>Where a request goes. <see cref="BaseUrl"/> ends with the API root, e.g. "http://127.0.0.1:8091/v1".</summary>
public sealed record LlmEndpoint(
    string Name,
    LlmProviderKind Kind,
    string BaseUrl,
    string Model,
    string? ApiKey = null,
    StructuredOutputMode Structured = StructuredOutputMode.JsonSchema)
{
    public bool IsLocal => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var u) && u.IsLoopback;
}

public enum StructuredOutputMode
{
    /// <summary>response_format json_schema (llama.cpp, OpenAI, Gemini-compat).</summary>
    JsonSchema,
    /// <summary>response_format json_object plus the schema in the prompt (DeepSeek and similar).</summary>
    JsonObject,
    /// <summary>No server-side enforcement; the schema is described in the prompt only.</summary>
    PromptOnly,
}

public sealed record LlmMessage(string Role, string Content);

public sealed record LlmRequest(
    IReadOnlyList<LlmMessage> Messages,
    JsonObject? JsonSchema = null,
    double Temperature = 0.2,
    int MaxTokens = 700,
    double? TopP = null,
    int? TopK = null,
    double? RepeatPenalty = null,
    IReadOnlyList<string>? BannedTokens = null);

public interface ILlmClient
{
    LlmEndpoint Endpoint { get; }

    /// <summary>Streams text deltas of the assistant reply.</summary>
    IAsyncEnumerable<string> StreamAsync(LlmRequest request, CancellationToken ct);
}

public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Client for the OpenAI chat-completions wire format, including llama-server.</summary>
public sealed class OpenAiCompatibleClient(HttpClient http, LlmEndpoint endpoint) : ILlmClient
{
    public LlmEndpoint Endpoint { get; } = endpoint;

    public async IAsyncEnumerable<string> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var json = BuildBody(request).ToJsonString();
        HttpResponseMessage resp;
        try
        {
            resp = await SendAsync(json, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // A kept-alive connection the server closed at that moment fails before anything is sent back.
            // Nothing was streamed yet, so one retry on a fresh connection is safe.
            try
            {
                resp = await SendAsync(json, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new LlmException($"{Endpoint.Name}: нет соединения ({ex.Message})", ex);
            }
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LlmException($"{Endpoint.Name}: HTTP {(int)resp.StatusCode} {Trim(body, 300)}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var inThink = false;
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") yield break;

                var delta = ExtractDelta(data);
                if (string.IsNullOrEmpty(delta)) continue;

                // Reasoning models may still emit <think>…</think> despite enable_thinking=false; drop it.
                if (delta.Contains("<think>")) { inThink = true; delta = delta[..delta.IndexOf("<think>", StringComparison.Ordinal)]; }
                if (inThink)
                {
                    var end = delta.IndexOf("</think>", StringComparison.Ordinal);
                    if (end < 0) continue;
                    inThink = false;
                    delta = delta[(end + "</think>".Length)..];
                }
                if (delta.Length > 0) yield return delta;
            }
        }
    }

    private Task<HttpResponseMessage> SendAsync(string body, CancellationToken ct)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(Endpoint.ApiKey))
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Endpoint.ApiKey);
        // llama-server closes kept-alive connections on its own schedule, and a request racing that close fails
        // ("connection aborted", ~1 in 100 lookups). A new localhost connection costs well under a millisecond.
        if (Endpoint.Kind == LlmProviderKind.LlamaServer) msg.Headers.ConnectionClose = true;
        return http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    internal JsonObject BuildBody(LlmRequest r)
    {
        var messages = new JsonArray();
        foreach (var m in r.Messages)
            messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });

        var body = new JsonObject
        {
            ["model"] = Endpoint.Model,
            ["messages"] = messages,
            ["stream"] = true,
            ["temperature"] = r.Temperature,
            ["max_tokens"] = r.MaxTokens,
        };
        if (r.TopP is { } topP) body["top_p"] = topP;

        if (Endpoint.Kind == LlmProviderKind.LlamaServer)
        {
            // llama.cpp extensions: sampling knobs and disabling the reasoning phase of Qwen-style models.
            if (r.TopK is { } topK) body["top_k"] = topK;
            if (r.RepeatPenalty is { } rp) body["repeat_penalty"] = rp;
            if (r.BannedTokens is { Count: > 0 } banned)
            {
                // [["text", false]]: llama.cpp never samples the tokens of that text.
                var bias = new JsonArray();
                foreach (var t in banned) bias.Add(new JsonArray(t, false));
                body["logit_bias"] = bias;
            }
            body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
            body["cache_prompt"] = true;
        }

        if (r.JsonSchema is not null)
        {
            body["response_format"] = Endpoint.Structured switch
            {
                StructuredOutputMode.JsonSchema => new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = "result",
                        ["strict"] = true,
                        ["schema"] = r.JsonSchema.DeepClone(),
                    },
                },
                StructuredOutputMode.JsonObject => new JsonObject { ["type"] = "json_object" },
                _ => null,
            };
            if (body["response_format"] is null) body.Remove("response_format");
        }
        return body;
    }

    private static string? ExtractDelta(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return null;
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
                return content.GetString();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

public static class LlmClientExtensions
{
    /// <summary>Collects the whole reply.</summary>
    public static async Task<string> CompleteAsync(this ILlmClient client, LlmRequest request, CancellationToken ct)
    {
        var sb = new StringBuilder();
        await foreach (var d in client.StreamAsync(request, ct).ConfigureAwait(false)) sb.Append(d);
        return sb.ToString();
    }
}
