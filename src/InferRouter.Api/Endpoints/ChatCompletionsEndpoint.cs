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

using System.Text.Json;
using System.Text.Json.Serialization;
using InferRouter.Api.Models;
using InferRouter.Core.Domain;
using InferRouter.Core.Services;

namespace InferRouter.Api.Endpoints;

public class ChatCompletionsEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/v1/chat/completions", HandleAsync);
    }

    public static async Task<IResult> HandleAsync(
        OpenAiChatRequest openAiRequest,
        ProviderOrchestrator executor,
        ILogger<ChatCompletionsEndpoint> logger,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var request = new InferRequest(
            RequestId: Guid.NewGuid().ToString(),
            Messages: openAiRequest.Messages
                .Select(m => new ChatMessage(m.Role, m.Content))
                .ToList(),
            Model: openAiRequest.Model,
            MaxTokens: openAiRequest.MaxTokens,
            Temperature: openAiRequest.Temperature);

        if (openAiRequest.Stream == true)
            return await HandleStreamingAsync(request, executor, httpContext, ct);

        try
        {
            var result = await executor.ExecuteAsync(request, ct);

            var response = new OpenAiChatResponse(
                Id: "inferrouter-" + result.RequestId,
                Object: "chat.completion",
                Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model: result.Model,
                Choices:
                [
                    new OpenAiChoice(
                        Index: 0,
                        Message: new OpenAiMessage("assistant", result.Content),
                        FinishReason: "stop")
                ],
                Usage: new OpenAiUsage(
                    result.PromptTokens,
                    result.CompletionTokens,
                    result.PromptTokens + result.CompletionTokens));

            return Results.Ok(response);
        }
        catch (InferRouterException)
        {
            return Results.Problem(
                detail: "All providers exhausted",
                statusCode: 503);
        }
        catch (OperationCanceledException)
        {
            return Results.Problem(
                detail: "Request cancelled",
                statusCode: 499);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception processing chat completion request");
            return Results.Problem(
                detail: "Internal error",
                statusCode: 500);
        }
    }

    private static async Task<IResult> HandleStreamingAsync(
        InferRequest request,
        ProviderOrchestrator executor,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var response = httpContext.Response;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            await foreach (var chunk in executor.ExecuteStreamingAsync(request, ct))
            {
                var chunkResponse = new SseChunkResponse(
                    Id: "inferrouter-" + chunk.RequestId,
                    Object: "chat.completion.chunk",
                    Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Model: "",
                    Choices:
                    [
                        new SseChunkChoice(
                            Index: 0,
                            Delta: new SseChunkDelta(Content: chunk.Delta),
                            FinishReason: chunk.IsLast ? "stop" : null)
                    ]);

                await response.WriteAsync($"data: {JsonSerializer.Serialize(chunkResponse)}\n\n", ct);

                if (chunk.IsLast)
                    await response.WriteAsync("data: [DONE]\n\n", ct);
            }
        }
        catch (InferRouterException)
        {
            await response.WriteAsync("data: [DONE]\n\n", CancellationToken.None);
            await response.Body.FlushAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }

        return Results.Empty;
    }

    private record SseChunkDelta(
        [property: JsonPropertyName("content")] string Content);

    private record SseChunkChoice(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("delta")] SseChunkDelta Delta,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private record SseChunkResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("object")] string Object,
        [property: JsonPropertyName("created")] long Created,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("choices")] SseChunkChoice[] Choices);
}
