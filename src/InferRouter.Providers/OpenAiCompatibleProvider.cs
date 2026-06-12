/*
   Copyright 2026 Viktor Vidman (vvidman)

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/

using InferRouter.Core.Config;
using InferRouter.Core.Domain;
using InferRouter.Core.Interfaces;
using InferRouter.Core.Services;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferRouter.Providers;

public class OpenAiCompatibleProvider : IInferenceClient
{
    private readonly ProviderConfig _config;
    private readonly SecretReader _secretReader;
    private readonly HttpClient _httpClient;
    private readonly bool _hideProviderModel;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public string Name => _config.Name;
    public string Model => string.IsNullOrEmpty(_config.Model) ? _config.Name : _config.Model;
    public ProviderType Type => ProviderType.OpenAiCompatible;
    public bool SupportsStreaming => true;

    public OpenAiCompatibleProvider(ProviderConfig config, bool hideModel, SecretReader secretReader, HttpClient httpClient)
    {
        _config = config;
        _secretReader = secretReader;
        _httpClient = httpClient;
        _hideProviderModel = hideModel;
    }

    public async Task<InferResult> CompleteAsync(InferRequest request, CancellationToken ct)
    {
        var apiKey = _secretReader.ReadApiKey(_config.Name)
            ?? throw new ProviderException(401, "missing_api_key", _config.ErrorMappings);

        var modelName = (_hideProviderModel || string.IsNullOrEmpty(request.Model))
                            ? _config.Model
                            : request.Model;

        var body = new ChatCompletionRequest
        {
            Model = modelName ?? "",
            Messages = request.Messages
                .Select(m => new ChatRequestMessage
                {
                    Role = m.Role,
                    Content = m.Content,
                    ToolCallId = m.ToolCallId,
                    ToolCalls = m.ToolCalls?.Select(tc => new ToolCallDto
                    {
                        Id = tc.Id,
                        Type = tc.Type,
                        Function = new ToolCallFunctionDto { Name = tc.Function.Name, Arguments = tc.Function.Arguments }
                    }).ToList()
                })
                .ToList(),
            MaxTokens = request.MaxTokens,
            Temperature = request.Temperature,
            TopP = request.TopP,
            FrequencyPenalty = request.FrequencyPenalty,
            PresencePenalty = request.PresencePenalty,
            Tools = request.Tools?.Select(t => new ToolDefinitionDto
            {
                Type = t.Type,
                Function = new ToolFunctionDto { Name = t.Function.Name, Description = t.Function.Description, Parameters = t.Function.Parameters }
            }).ToList(),
            ToolChoice = request.ToolChoice
        };

        var json = JsonSerializer.Serialize(body, SerializerOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl}/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var sw = Stopwatch.StartNew();
        var response = await _httpClient.SendAsync(httpRequest, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            var errorCode = ExtractErrorCode(responseBody, _config.ErrorCodePath);
            throw new ProviderException((int)response.StatusCode, errorCode, _config.ErrorMappings);
        }

        var chatResponse = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody, SerializerOptions)
            ?? throw new ProviderException((int)response.StatusCode, null, _config.ErrorMappings, "Empty response from provider");

        var choice = chatResponse.Choices[0];
        return new InferResult(
            RequestId: request.RequestId,
            ProviderName: _config.Name,
            Model: chatResponse.Model,
            Content: choice.Message.Content,
            FinishReason: chatResponse.Choices.Count > 0 ? choice.FinishReason : null,
            PromptTokens: chatResponse.Usage.PromptTokens,
            CompletionTokens: chatResponse.Usage.CompletionTokens,
            LatencyMs: sw.ElapsedMilliseconds,
            WasFallback: false,
            ToolCalls: choice.Message.ToolCalls?
                .Select(tc => new ToolCall(tc.Id, tc.Type, new ToolCallFunction(tc.Function.Name, tc.Function.Arguments)))
                .ToList()
        );
    }

    public async IAsyncEnumerable<StreamChunk> CompleteStreamingAsync(
        InferRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var apiKey = _secretReader.ReadApiKey(_config.Name)
            ?? throw new ProviderException(401, "missing_api_key", _config.ErrorMappings);

        var modelName = (_hideProviderModel || string.IsNullOrEmpty(request.Model))
                            ? _config.Model
                            : request.Model;

        var body = new ChatCompletionRequest
        {
            Model = modelName ?? "",
            Messages = request.Messages
                .Select(m => new ChatRequestMessage
                {
                    Role = m.Role,
                    Content = m.Content,
                    ToolCallId = m.ToolCallId,
                    ToolCalls = m.ToolCalls?.Select(tc => new ToolCallDto
                    {
                        Id = tc.Id,
                        Type = tc.Type,
                        Function = new ToolCallFunctionDto { Name = tc.Function.Name, Arguments = tc.Function.Arguments }
                    }).ToList()
                })
                .ToList(),
            MaxTokens = request.MaxTokens,
            Temperature = request.Temperature,
            TopP = request.TopP,
            FrequencyPenalty = request.FrequencyPenalty,
            PresencePenalty = request.PresencePenalty,
            Stream = true,
            Tools = request.Tools?.Select(t => new ToolDefinitionDto
            {
                Type = t.Type,
                Function = new ToolFunctionDto { Name = t.Function.Name, Description = t.Function.Description, Parameters = t.Function.Parameters }
            }).ToList(),
            ToolChoice = request.ToolChoice
        };

        var json = JsonSerializer.Serialize(body, SerializerOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl}/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var errorCode = ExtractErrorCode(responseBody, _config.ErrorCodePath);
            throw new ProviderException((int)response.StatusCode, errorCode, _config.ErrorMappings);
        }

        var stream = await response.Content.ReadAsStreamAsync(ct);
        await using (stream)
        {
            using var reader = new StreamReader(stream);

            StreamChunk? pending = null;
            string? lastFinishReason = null;

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrEmpty(line)) continue;
                if (!line.StartsWith("data:")) continue;

                var data = line["data:".Length..].TrimStart();

                if (data == "[DONE]")
                {
                    if (pending is not null)
                        yield return pending with { IsLast = true, FinishReason = pending.FinishReason ?? lastFinishReason };
                    yield break;
                }

                var sseChunk = JsonSerializer.Deserialize<SseChatChunk>(data, SerializerOptions);
                if (sseChunk is null) continue;

                var finishReason = sseChunk.Choices.Count > 0 ? sseChunk.Choices[0].FinishReason : null;
                if (finishReason is not null)
                    lastFinishReason = finishReason;

                var delta = sseChunk.Choices.Count > 0
                    ? sseChunk.Choices[0].Delta.Content ?? ""
                    : "";

                List<ToolCallDelta>? toolCallsDelta = null;
                if (sseChunk.Choices.Count > 0 && sseChunk.Choices[0].Delta.ToolCalls is { Count: > 0 } rawDeltas)
                {
                    toolCallsDelta = rawDeltas.Select(tc => new ToolCallDelta(
                        tc.Index, tc.Id, tc.Type,
                        tc.Function is null ? null : new ToolCallFunctionDelta(tc.Function.Name, tc.Function.Arguments)))
                        .ToList();
                }

                if (pending is not null)
                    yield return pending;

                pending = new StreamChunk(
                    RequestId: request.RequestId,
                    Delta: delta,
                    IsLast: false,
                    PromptTokens: sseChunk.Usage?.PromptTokens,
                    CompletionTokens: sseChunk.Usage?.CompletionTokens,
                    FinishReason: finishReason,
                    ToolCallsDelta: toolCallsDelta);
            }

            if (pending is not null)
                yield return pending with { IsLast = true, FinishReason = pending.FinishReason ?? lastFinishReason };
        }
    }

    private static string? ExtractErrorCode(string body, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var parts = path.Split('.');
            var current = doc.RootElement;
            foreach (var part in parts)
            {
                if (!current.TryGetProperty(part, out current))
                    return null;
            }
            return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
        }
        catch
        {
            return null;
        }
    }

    private class ChatCompletionRequest
    {
        public string Model { get; set; } = "";
        public List<ChatRequestMessage> Messages { get; set; } = [];
        public int? MaxTokens { get; set; }
        public float? Temperature { get; set; }
        public bool? Stream { get; set; }
        public float? TopP { get; set; }
        public float? FrequencyPenalty { get; set; }
        public float? PresencePenalty { get; set; }
        public List<ToolDefinitionDto>? Tools { get; set; }
        public string? ToolChoice { get; set; }
    }

    private class ChatRequestMessage
    {
        public string Role { get; set; } = "";
        public string? Content { get; set; }
        public string? ToolCallId { get; set; }
        public List<ToolCallDto>? ToolCalls { get; set; }
    }

    private class ToolDefinitionDto
    {
        public string Type { get; set; } = "";
        public ToolFunctionDto Function { get; set; } = new();
    }

    private class ToolFunctionDto
    {
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public JsonElement? Parameters { get; set; }
    }

    private class ToolCallDto
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public ToolCallFunctionDto Function { get; set; } = new();
    }

    private class ToolCallFunctionDto
    {
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "";
    }

    private class ChatCompletionResponse
    {
        public string Model { get; set; } = "";
        public List<ChatResponseChoice> Choices { get; set; } = [];
        public ChatUsage Usage { get; set; } = new();
    }

    private class ChatResponseChoice
    {
        public ChatResponseMessage Message { get; set; } = new();
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
    }

    private class ChatResponseMessage
    {
        public string? Content { get; set; }
        public List<ToolCallDto>? ToolCalls { get; set; }
    }

    private class ChatUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
    }

    private class SseChatChunk
    {
        public List<SseChoice> Choices { get; set; } = [];
        public SseUsage? Usage { get; set; }
    }

    private class SseChoice
    {
        public SseDelta Delta { get; set; } = new();
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
    }

    private class SseDelta
    {
        public string? Content { get; set; }
        public List<ToolCallDeltaDto>? ToolCalls { get; set; }
    }

    private class ToolCallDeltaDto
    {
        public int Index { get; set; }
        public string? Id { get; set; }
        public string? Type { get; set; }
        public ToolCallFunctionDeltaDto? Function { get; set; }
    }

    private class ToolCallFunctionDeltaDto
    {
        public string? Name { get; set; }
        public string? Arguments { get; set; }
    }

    private class SseUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
    }
}
