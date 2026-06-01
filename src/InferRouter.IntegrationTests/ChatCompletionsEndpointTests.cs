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

using System.Net;
using System.Text;
using System.Text.Json;
using InferRouter.Core.Config;
using InferRouter.Core.Domain;
using InferRouter.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace InferRouter.IntegrationTests;

public class ChatCompletionsSuccessFactory : InferRouterWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var cloud = new Mock<ILlmProvider>();
            cloud.Setup(p => p.Name).Returns("test-provider");
            cloud.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);
            cloud.Setup(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InferResult("req-1", "test-provider", "test-model",
                    "Hello!", 10, 5, 50, false));

            var local = new Mock<ILlmProvider>();
            local.Setup(p => p.Name).Returns("test-local");
            local.Setup(p => p.Type).Returns(ProviderType.LocalGguf);

            services.AddSingleton<IReadOnlyList<ILlmProvider>>(_ =>
                new List<ILlmProvider> { cloud.Object, local.Object }.AsReadOnly());
        });
    }
}

public class ChatCompletionsAllFailFactory : InferRouterWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var authMappings = new List<ErrorMapping>
            {
                new() { HttpStatus = 401, InternalCategory = InternalErrorCategory.AuthError }
            }.AsReadOnly();

            var cloud = new Mock<ILlmProvider>();
            cloud.Setup(p => p.Name).Returns("test-provider");
            cloud.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);
            cloud.Setup(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ProviderException(401, null, authMappings));

            var local = new Mock<ILlmProvider>();
            local.Setup(p => p.Name).Returns("test-local");
            local.Setup(p => p.Type).Returns(ProviderType.LocalGguf);
            local.Setup(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ProviderException(401, null, authMappings));

            services.AddSingleton<IReadOnlyList<ILlmProvider>>(_ =>
                new List<ILlmProvider> { cloud.Object, local.Object }.AsReadOnly());
        });
    }
}

public class ChatCompletionsFallbackFactory : InferRouterWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var rateMappings = new List<ErrorMapping>
            {
                new() { HttpStatus = 429, InternalCategory = InternalErrorCategory.RateLimit }
            }.AsReadOnly();

            var cloud = new Mock<ILlmProvider>();
            cloud.Setup(p => p.Name).Returns("test-provider");
            cloud.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);
            cloud.Setup(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ProviderException(429, null, rateMappings));

            var local = new Mock<ILlmProvider>();
            local.Setup(p => p.Name).Returns("test-local");
            local.Setup(p => p.Type).Returns(ProviderType.LocalGguf);
            local.Setup(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InferResult("req-fb", "test-local", "local-model",
                    "Fallback response", 8, 4, 200, true));

            services.AddSingleton<IReadOnlyList<ILlmProvider>>(_ =>
                new List<ILlmProvider> { cloud.Object, local.Object }.AsReadOnly());
        });
    }
}

public class ChatCompletionsEndpointTests(ChatCompletionsSuccessFactory factory)
    : IClassFixture<ChatCompletionsSuccessFactory>
{
    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task ValidRequest_ProviderSucceeds_Returns200WithOpenAiShape()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsync("/v1/chat/completions",
            JsonBody("""{"model":"test-model","messages":[{"role":"user","content":"hi"}]}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("id", out _));
        Assert.True(root.TryGetProperty("object", out _));
        Assert.True(root.TryGetProperty("model", out _));
        Assert.True(root.TryGetProperty("choices", out _));
        Assert.True(root.TryGetProperty("usage", out _));
    }
}

public class ChatCompletionsEndpointTests_AuthError(ChatCompletionsAllFailFactory factory)
    : IClassFixture<ChatCompletionsAllFailFactory>
{
    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task AuthError_NoFurtherProviders_Returns503()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsync("/v1/chat/completions",
            JsonBody("""{"model":"test-model","messages":[{"role":"user","content":"hi"}]}"""));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}

public class ChatCompletionsEndpointTests_Fallback(ChatCompletionsFallbackFactory factory)
    : IClassFixture<ChatCompletionsFallbackFactory>
{
    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task CloudRateLimited_LocalFallbackSucceeds_Returns200()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsync("/v1/chat/completions",
            JsonBody("""{"model":"test-model","messages":[{"role":"user","content":"hi"}]}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
