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

using System.Text.Json.Serialization;
using InferRouter.Core.Services;

namespace InferRouter.Api.Endpoints;

public class StatsEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/stats/live", HandleLive);
        app.MapGet("/stats/history", HandleHistory);
    }

    public static IResult HandleLive(StatsService statsService)
    {
        var stats = statsService.GetLiveStats();
        var response = new LiveStatsResponse(
            stats.Select(s => new ProviderStatsEntry(
                ProviderName: s.ProviderName,
                DailyLimit: s.DailyLimit,
                DailyCount: s.DailyCount,
                RpmLimit: s.RpmLimit,
                RpmWindowCount: s.RpmWindowCount,
                IsExhausted: s.IsExhausted
            )).ToList()
        );
        return Results.Ok(response);
    }

    public static IResult HandleHistory(StatsService statsService, string? date = null)
    {
        DateOnly targetDate;
        if (date is null)
        {
            targetDate = DateOnly.FromDateTime(DateTime.UtcNow);
        }
        else if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out targetDate))
        {
            return Results.BadRequest("Invalid date format. Use YYYY-MM-DD.");
        }

        var (found, content) = statsService.GetHistoryForDate(targetDate);
        if (!found)
            return Results.NotFound();

        return Results.Text(content!, "text/plain");
    }

    private record LiveStatsResponse(
        [property: JsonPropertyName("providers")] List<ProviderStatsEntry> Providers
    );

    private record ProviderStatsEntry(
        [property: JsonPropertyName("provider_name")] string ProviderName,
        [property: JsonPropertyName("daily_limit")] int DailyLimit,
        [property: JsonPropertyName("daily_count")] int DailyCount,
        [property: JsonPropertyName("rpm_limit")] int RpmLimit,
        [property: JsonPropertyName("rpm_window_count")] int RpmWindowCount,
        [property: JsonPropertyName("is_exhausted")] bool IsExhausted
    );
}
