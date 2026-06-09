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
using Xunit;

namespace InferRouter.IntegrationTests;

public class StatsHistoryEndpointTests(InferRouterWebAppFactory factory)
    : IClassFixture<InferRouterWebAppFactory>
{
    private const string LogDir = "/tmp/inferrouter-test-logs";

    [Fact]
    public async Task NoDateParam_TodayLogExists_Returns200WithJsonlContent()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var logPath = Path.Combine(LogDir, $"operations-{today:yyyy-MM-dd}.jsonl");
        Directory.CreateDirectory(LogDir);
        await File.WriteAllTextAsync(logPath, "{\"event\":\"infer_started\"}\n");
        try
        {
            var client = factory.CreateClient();
            var response = await client.GetAsync("/stats/history");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task DateWithNoLogFile_Returns404()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/stats/history?date=1999-01-01");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task InvalidDateFormat_Returns400()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/stats/history?date=notadate");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
