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
using InferRouter.Core.Domain;
using InferRouter.Core.Services;

namespace InferRouter.Api.Endpoints;

public class HealthProvidersEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health/providers", HandleAsync);
    }

    public static async Task<IResult> HandleAsync(
        ProviderHealthChecker checker,
        CancellationToken ct)
    {
        var results = await checker.CheckAllAsync(ct);
        var response = new ProvidersHealthResponse(
            results.Select(r => new ProviderHealthEntry(
                Name: r.ProviderName,
                Status: r.Status,
                HttpStatus: r.HttpStatus,
                LatencyMs: r.HttpStatus is null ? r.LatencyMs : null
            )).ToList()
        );
        return Results.Ok(response);
    }

    private record ProvidersHealthResponse(
        [property: JsonPropertyName("providers")] List<ProviderHealthEntry> Providers
    );

    private record ProviderHealthEntry(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("http_status")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? HttpStatus,
        [property: JsonPropertyName("latency_ms")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long? LatencyMs
    );
}
