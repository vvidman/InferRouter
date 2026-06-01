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
using Xunit;

namespace InferRouter.IntegrationTests;

public class StatsLiveEndpointTests(InferRouterWebAppFactory factory)
    : IClassFixture<InferRouterWebAppFactory>
{
    [Fact]
    public async Task GetLiveStats_Returns200WithEntryForEveryConfiguredProvider()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/stats/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var providers = doc.RootElement.GetProperty("providers").EnumerateArray().ToList();
        Assert.Equal(2, providers.Count);
        var names = providers.Select(p => p.GetProperty("provider_name").GetString()).ToHashSet();
        Assert.Contains("test-provider", names);
        Assert.Contains("test-local", names);
    }

    [Fact]
    public async Task GetLiveStats_EachEntryContainsRequiredFields()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/stats/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var providers = doc.RootElement.GetProperty("providers").EnumerateArray().ToList();
        Assert.All(providers, p =>
        {
            Assert.True(p.TryGetProperty("provider_name", out _));
            Assert.True(p.TryGetProperty("daily_limit", out _));
            Assert.True(p.TryGetProperty("daily_count", out _));
            Assert.True(p.TryGetProperty("rpm_limit", out _));
            Assert.True(p.TryGetProperty("rpm_window_count", out _));
            Assert.True(p.TryGetProperty("is_exhausted", out _));
        });
    }
}
