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
        CancellationToken ct)
    {
        try
        {
            var request = new InferRequest(
                RequestId: Guid.NewGuid().ToString(),
                Messages: openAiRequest.Messages
                    .Select(m => new ChatMessage(m.Role, m.Content))
                    .ToList(),
                Model: openAiRequest.Model,
                MaxTokens: openAiRequest.MaxTokens,
                Temperature: openAiRequest.Temperature);

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
}
