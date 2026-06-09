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
    public ProviderType Type => ProviderType.OpenAiCompatible;
    public bool SupportsStreaming => false;

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
                .Select(m => new ChatRequestMessage { Role = m.Role, Content = m.Content })
                .ToList(),
            MaxTokens = request.MaxTokens,
            Temperature = request.Temperature
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

        return new InferResult(
            RequestId: request.RequestId,
            ProviderName: _config.Name,
            Model: chatResponse.Model,
            Content: chatResponse.Choices[0].Message.Content,
            PromptTokens: chatResponse.Usage.PromptTokens,
            CompletionTokens: chatResponse.Usage.CompletionTokens,
            LatencyMs: sw.ElapsedMilliseconds,
            WasFallback: false
        );
    }

    public IAsyncEnumerable<StreamChunk> CompleteStreamingAsync(InferRequest request, CancellationToken ct)
        => throw new NotImplementedException();

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
    }

    private class ChatRequestMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
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
    }

    private class ChatResponseMessage
    {
        public string Content { get; set; } = "";
    }

    private class ChatUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
    }
}
