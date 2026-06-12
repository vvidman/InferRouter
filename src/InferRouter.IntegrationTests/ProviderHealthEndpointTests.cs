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

public class ProviderHealthAllOkFactory : InferRouterWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var okResult = new InferResult("hc", "test-provider", "test-model", "ok", "stop", 0, 0, 0, false);

            var cloud = new Mock<IInferenceClient>();
            cloud.Setup(p => p.Name).Returns("test-provider");
            cloud.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);
            cloud.Setup(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(okResult);

            var local = new Mock<IInferenceClient>();
            local.Setup(p => p.Name).Returns("test-local");
            local.Setup(p => p.Type).Returns(ProviderType.LocalGguf);
            local.Setup(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(okResult with { ProviderName = "test-local" });

            services.AddSingleton<IReadOnlyList<IInferenceClient>>(_ =>
                new List<IInferenceClient> { cloud.Object }.AsReadOnly());
            services.AddSingleton<IInferenceClient>(_ => local.Object);
        });
    }
}

public class ProviderHealthCloudAuthErrorFactory : InferRouterWebAppFactory
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

            var cloud = new Mock<IInferenceClient>();
            cloud.Setup(p => p.Name).Returns("test-provider");
            cloud.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);
            cloud.Setup(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ProviderException(401, null, authMappings));

            var local = new Mock<IInferenceClient>();
            local.Setup(p => p.Name).Returns("test-local");
            local.Setup(p => p.Type).Returns(ProviderType.LocalGguf);
            local.Setup(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InferResult("hc", "test-local", "local-model", "ok", "stop", 0, 0, 0, false));

            services.AddSingleton<IReadOnlyList<IInferenceClient>>(_ =>
                new List<IInferenceClient> { cloud.Object }.AsReadOnly());
            services.AddSingleton<IInferenceClient>(_ => local.Object);
        });
    }
}

public class ProviderHealthAllFailFactory : InferRouterWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var cloud = new Mock<IInferenceClient>();
            cloud.Setup(p => p.Name).Returns("test-provider");
            cloud.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);
            cloud.Setup(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("provider unavailable"));

            services.AddSingleton<IReadOnlyList<IInferenceClient>>(_ =>
                new List<IInferenceClient> { cloud.Object }.AsReadOnly());
        });
    }
}

public class ProviderHealthEndpointTests(ProviderHealthAllOkFactory factory)
    : IClassFixture<ProviderHealthAllOkFactory>
{
    [Fact]
    public async Task AllProvidersHealthy_Returns200WithAllOkStatus()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/health/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var providers = doc.RootElement.GetProperty("providers").EnumerateArray().ToList();
        Assert.Single(providers);
        Assert.All(providers, p =>
            Assert.Equal("ok", p.GetProperty("status").GetString()));
    }
}

public class ProviderHealthEndpointTests_CloudAuthError(ProviderHealthCloudAuthErrorFactory factory)
    : IClassFixture<ProviderHealthCloudAuthErrorFactory>
{
    [Fact]
    public async Task CloudProviderAuthError_Returns200_CloudEntryHasAuthErrorStatus()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/health/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var providers = doc.RootElement.GetProperty("providers").EnumerateArray()
            .ToDictionary(p => p.GetProperty("name").GetString()!);

        Assert.Equal("auth_error", providers["test-provider"].GetProperty("status").GetString());
        Assert.Equal(401, providers["test-provider"].GetProperty("http_status").GetInt32());
    }
}

public class ProviderHealthEndpointTests_AllFail(ProviderHealthAllFailFactory factory)
    : IClassFixture<ProviderHealthAllFailFactory>
{
    [Fact]
    public async Task AllProvidersFail_Returns200_AllEntriesHaveNonOkStatus()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/health/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var providers = doc.RootElement.GetProperty("providers").EnumerateArray().ToList();
        Assert.Single(providers);
        Assert.All(providers, p =>
            Assert.NotEqual("ok", p.GetProperty("status").GetString()));
    }
}
