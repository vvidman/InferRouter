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

using System.Diagnostics;
using InferRouter.Core.Domain;
using InferRouter.Core.Interfaces;

namespace InferRouter.Core.Services;

public class ProviderHealthChecker(
    IReadOnlyList<IInferenceClient> providers,
    IInferenceClient finalFallback,
    ErrorNormalizer errorNormalizer)
{
    private static readonly InferRequest HealthCheckRequest = new(
        RequestId: "health-check",
        Messages: [new ChatMessage("user", "hello")],
        Model: null,
        MaxTokens: 1,
        Temperature: null,
        TopP: null,
        FrequencyPenalty: null,
        PresencePenalty: null
    );

    public async Task<IReadOnlyList<ProviderHealthResult>> CheckAllAsync(CancellationToken ct)
    {
        var results = new List<ProviderHealthResult>(providers.Count + 1);
        foreach (var provider in providers)
            results.Add(await CheckProviderAsync(provider, ct));
        results.Add(await CheckProviderAsync(finalFallback, ct));
        return results;
    }

    private async Task<ProviderHealthResult> CheckProviderAsync(IInferenceClient provider, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await provider.CompleteAsync(HealthCheckRequest, ct);
            sw.Stop();
            return new ProviderHealthResult(provider.Name, "ok", null, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderException ex)
        {
            sw.Stop();
            var category = errorNormalizer.Categorize(ex.HttpStatus, ex.RawErrorCode, ex.Mappings);
            return new ProviderHealthResult(provider.Name, CategoryToStatus(category), ex.HttpStatus, sw.ElapsedMilliseconds);
        }
        catch (Exception)
        {
            sw.Stop();
            return new ProviderHealthResult(provider.Name, "unknown_error", null, sw.ElapsedMilliseconds);
        }
    }

    private static string CategoryToStatus(InternalErrorCategory category) => category switch
    {
        InternalErrorCategory.RateLimit => "rate_limit",
        InternalErrorCategory.ModelUnavailable => "model_unavailable",
        InternalErrorCategory.ServerError => "server_error",
        InternalErrorCategory.AuthError => "auth_error",
        _ => "unknown_error"
    };
}
