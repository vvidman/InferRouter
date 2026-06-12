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
using InferRouter.Api.Services;
using InferRouter.Core.Config;
using InferRouter.Core.Domain;
using InferRouter.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace InferRouter.Tests.Core;

public class ModelsServiceTests
{
    private static ModelsService CreateService(
        IReadOnlyList<ProviderConfig> configs,
        bool hideModels,
        IHttpClientFactory? httpClientFactory = null,
        SecretReader? secretReader = null)
    {
        httpClientFactory ??= Mock.Of<IHttpClientFactory>();
        secretReader ??= new Mock<SecretReader>(Mock.Of<ILogger<SecretReader>>()) { CallBase = false }.Object;
        return new ModelsService(configs, hideModels, httpClientFactory, secretReader);
    }

    private static IReadOnlyList<ProviderConfig> Configs(params (string name, string? model, string? baseUrl)[] entries) =>
        entries.Select(e => new ProviderConfig
        {
            Name = e.name,
            Type = ProviderType.OpenAiCompatible,
            Model = e.model,
            BaseUrl = e.baseUrl ?? "https://example.com"
        }).ToList().AsReadOnly();

    private static SecretReader NullSecretReader()
    {
        var mock = new Mock<SecretReader>(Mock.Of<ILogger<SecretReader>>());
        mock.Setup(s => s.ReadApiKey(It.IsAny<string>())).Returns((string?)null);
        return mock.Object;
    }

    private static (IHttpClientFactory factory, int[] callCount) FakeFactory(
        params Func<HttpResponseMessage>[] responses)
    {
        var idx = new int[1];
        var handler = new FakeHttpMessageHandler(_ =>
        {
            var i = idx[0]++;
            return i < responses.Length ? responses[i]() : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var httpClient = new HttpClient(handler);
        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient(It.IsAny<string>()) == httpClient);
        return (factory, idx);
    }

    private static HttpResponseMessage ModelListOk(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string ValidModelListJson =
        """{"object":"list","data":[{"id":"upstream-model","object":"model","created":0,"owned_by":"upstream"}]}""";

    // Static mode tests

    [Fact]
    public async Task StaticMode_TwoProviders_ReturnsTwoEntries()
    {
        var configs = Configs(("p1", "model-a", null), ("p2", "model-b", null));
        var svc = CreateService(configs, hideModels: true);

        var result = await svc.GetModelsAsync(CancellationToken.None);

        Assert.Equal("list", result.Object);
        Assert.Equal(2, result.Data.Count);
        Assert.Contains(result.Data, m => m.Id == "model-a" && m.OwnedBy == "p1");
        Assert.Contains(result.Data, m => m.Id == "model-b" && m.OwnedBy == "p2");
    }

    [Fact]
    public async Task StaticMode_SharedModel_DeduplicatesFirstWins()
    {
        var configs = Configs(("p1", "shared-model", null), ("p2", "shared-model", null));
        var svc = CreateService(configs, hideModels: true);

        var result = await svc.GetModelsAsync(CancellationToken.None);

        Assert.Single(result.Data);
        Assert.Equal("shared-model", result.Data[0].Id);
        Assert.Equal("p1", result.Data[0].OwnedBy);
    }

    [Fact]
    public async Task StaticMode_NullModel_ExcludesProvider()
    {
        var configs = Configs(("p1", null, null), ("p2", "model-b", null));
        var svc = CreateService(configs, hideModels: true);

        var result = await svc.GetModelsAsync(CancellationToken.None);

        Assert.Single(result.Data);
        Assert.Equal("model-b", result.Data[0].Id);
    }

    // Dynamic mode tests

    [Fact]
    public async Task DynamicMode_FirstProviderReturnsValidList_ReturnedAsIs()
    {
        var configs = Configs(("p1", "static-model", "https://p1.example.com"));
        var (factory, _) = FakeFactory(() => ModelListOk(ValidModelListJson));
        var svc = CreateService(configs, hideModels: false, factory, NullSecretReader());

        var result = await svc.GetModelsAsync(CancellationToken.None);

        Assert.Single(result.Data);
        Assert.Equal("upstream-model", result.Data[0].Id);
    }

    [Fact]
    public async Task DynamicMode_FirstFails_SecondReturnsValidList()
    {
        var configs = Configs(
            ("p1", "model-a", "https://p1.example.com"),
            ("p2", "model-b", "https://p2.example.com"));
        var (factory, _) = FakeFactory(
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            () => ModelListOk(ValidModelListJson));
        var svc = CreateService(configs, hideModels: false, factory, NullSecretReader());

        var result = await svc.GetModelsAsync(CancellationToken.None);

        Assert.Single(result.Data);
        Assert.Equal("upstream-model", result.Data[0].Id);
    }

    [Fact]
    public async Task DynamicMode_AllProvidersFail_FallsBackToStaticList()
    {
        var configs = Configs(
            ("p1", "static-model", "https://p1.example.com"),
            ("p2", "static-model-2", "https://p2.example.com"));
        var (factory, _) = FakeFactory(
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var svc = CreateService(configs, hideModels: false, factory, NullSecretReader());

        var result = await svc.GetModelsAsync(CancellationToken.None);

        Assert.Equal(2, result.Data.Count);
        Assert.Contains(result.Data, m => m.Id == "static-model");
        Assert.Contains(result.Data, m => m.Id == "static-model-2");
    }

    [Fact]
    public async Task DynamicMode_RequestsSentToCorrectUrl_NoDoubledV1()
    {
        var capturedUrls = new List<string>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            capturedUrls.Add(req.RequestUri!.ToString());
            return ModelListOk(ValidModelListJson);
        });
        var httpClient = new HttpClient(handler);
        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient(It.IsAny<string>()) == httpClient);

        var configs = Configs(("groq", "llama3", "https://api.groq.com/openai/v1"));
        var svc = new ModelsService(configs, hideModels: false, factory, NullSecretReader());

        await svc.GetModelsAsync(CancellationToken.None);

        Assert.Single(capturedUrls);
        Assert.Equal("https://api.groq.com/openai/v1/models", capturedUrls[0]);
        Assert.DoesNotContain("/v1/v1/", capturedUrls[0]);
    }
}

internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(handler(request));
}
