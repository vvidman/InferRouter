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
using InferRouter.Core.Services;
using InferRouter.Providers;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Text;
using Xunit;

namespace InferRouter.Tests.Providers;

public class OpenAiCompatibleProviderTests
{
    private static ProviderConfig MakeConfig() => new()
    {
        Name = "test-provider",
        Type = ProviderType.OpenAiCompatible,
        BaseUrl = "https://api.example.com",
        Model = "gpt-test",
        ErrorCodePath = "error.code"
    };

    private static InferRequest MakeRequest() =>
        new("req-001", [new ChatMessage("user", "hello")], null, null, null);

    private static OpenAiCompatibleProvider MakeProvider(HttpMessageHandler handler)
    {
        var secretReader = new Mock<SecretReader>(Mock.Of<ILogger<SecretReader>>());
        secretReader.Setup(s => s.ReadApiKey(It.IsAny<string>())).Returns("test-key");
        var httpClient = new HttpClient(handler);
        return new OpenAiCompatibleProvider(MakeConfig(), false, secretReader.Object, httpClient);
    }

    private sealed class FakeHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    [Fact]
    public async Task CompleteStreamingAsync_WellFormedSseResponse_YieldsCorrectChunks()
    {
        const string sseBody =
            "data: {\"id\":\"c1\",\"choices\":[{\"delta\":{\"content\":\"Hello\"}}],\"usage\":null}\n" +
            "\n" +
            "data: {\"id\":\"c2\",\"choices\":[{\"delta\":{\"content\":\" world\"}}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2}}\n" +
            "\n" +
            "data: [DONE]\n";

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream")
        };

        var provider = MakeProvider(new FakeHandler(response));
        var request = MakeRequest();

        var chunks = new List<StreamChunk>();
        await foreach (var chunk in provider.CompleteStreamingAsync(request, CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal(2, chunks.Count);

        Assert.Equal("req-001", chunks[0].RequestId);
        Assert.Equal("Hello", chunks[0].Delta);
        Assert.False(chunks[0].IsLast);
        Assert.Null(chunks[0].PromptTokens);
        Assert.Null(chunks[0].CompletionTokens);

        Assert.Equal("req-001", chunks[1].RequestId);
        Assert.Equal(" world", chunks[1].Delta);
        Assert.True(chunks[1].IsLast);
        Assert.Equal(5, chunks[1].PromptTokens);
        Assert.Equal(2, chunks[1].CompletionTokens);
    }

    [Fact]
    public async Task CompleteStreamingAsync_NonSuccessStatusBeforeStreaming_ThrowsProviderException()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":{\"code\":\"rate_limit_exceeded\"}}")
        };

        var provider = MakeProvider(new FakeHandler(response));
        var request = MakeRequest();

        var ex = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var _ in provider.CompleteStreamingAsync(request, CancellationToken.None))
            { }
        });

        Assert.Equal(429, ex.HttpStatus);
        Assert.Equal("rate_limit_exceeded", ex.RawErrorCode);
    }
}
