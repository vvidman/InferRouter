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
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace InferRouter.IntegrationTests;

public class ModelsEndpointTests(InferRouterWebAppFactory factory)
    : IClassFixture<InferRouterWebAppFactory>
{
    [Fact]
    public async Task GetModels_Returns200()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetModels_ResponseHasListObjectAndDataArray()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/v1/models");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("list", root.GetProperty("object").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task GetModels_EachEntryHasRequiredFields()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/v1/models");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");

        foreach (var entry in data.EnumerateArray())
        {
            Assert.True(entry.TryGetProperty("id", out _));
            Assert.True(entry.TryGetProperty("object", out _));
            Assert.True(entry.TryGetProperty("created", out _));
            Assert.True(entry.TryGetProperty("owned_by", out _));
        }
    }
}

public class ModelsEndpointHideModelsFactory : InferRouterWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InferRouter:HideModels"] = "true"
            });
        });
    }
}

public class ModelsEndpointTests_HideModels(ModelsEndpointHideModelsFactory factory)
    : IClassFixture<ModelsEndpointHideModelsFactory>
{
    [Fact]
    public async Task HideModels_True_Returns200WithListShape()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("list", root.GetProperty("object").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("data").ValueKind);
    }
}
