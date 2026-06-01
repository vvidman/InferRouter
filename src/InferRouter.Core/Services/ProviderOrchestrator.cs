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

using InferRouter.Core.Domain;
using InferRouter.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace InferRouter.Core.Services;

public class ProviderOrchestrator(
    IReadOnlyList<ILlmProvider> allProviders,
    IRoutingStrategy routingStrategy,
    IRateLimitTracker rateLimitTracker,
    ErrorNormalizer errorNormalizer,
    OperationLogger operationLogger,
    ILogger<ProviderOrchestrator> logger)
{
    private readonly ILlmProvider? _localFallback =
        allProviders.FirstOrDefault(p => p.Type == ProviderType.LocalGguf);

    public async Task<InferResult> ExecuteAsync(InferRequest request, CancellationToken ct)
    {
        operationLogger.LogStarted(request);

        var orderedCloud = routingStrategy.GetOrderedProviders();

        // Log rate_limit_hit for cloud providers excluded by the strategy (they are exhausted)
        var allCloud = allProviders.Where(p => p.Type != ProviderType.LocalGguf).ToList();
        foreach (var skipped in allCloud.Where(p => orderedCloud.All(op => op.Name != p.Name)))
            operationLogger.LogRateLimitHit(skipped.Name, request.RequestId);

        operationLogger.LogProviderOrdering(request.RequestId,
            orderedCloud.Select(p => p.Name).ToList().AsReadOnly());

        var toAttempt = new List<ILlmProvider>(orderedCloud);
        if (_localFallback != null)
            toAttempt.Add(_localFallback);

        for (int i = 0; i < toAttempt.Count; i++)
        {
            var provider = toAttempt[i];
            var nextProviderName = i + 1 < toAttempt.Count ? toAttempt[i + 1].Name : string.Empty;

            try
            {
                var result = await provider.CompleteAsync(request, ct);
                rateLimitTracker.RecordRequest(provider.Name);
                operationLogger.LogCompleted(result);
                return result;
            }
            catch (ProviderException ex)
            {
                var category = errorNormalizer.Categorize(ex.HttpStatus, ex.RawErrorCode, ex.Mappings);

                if (category == InternalErrorCategory.AuthError)
                {
                    logger.LogWarning(
                        "Provider {ProviderName} returned an auth error; skipping permanently.",
                        provider.Name);
                    continue;
                }

                if (category == InternalErrorCategory.RateLimit)
                    rateLimitTracker.MarkExhausted(provider.Name);

                if (category == InternalErrorCategory.ServerError)
                {
                    try
                    {
                        var retryResult = await provider.CompleteAsync(request, ct);
                        rateLimitTracker.RecordRequest(provider.Name);
                        operationLogger.LogCompleted(retryResult);
                        return retryResult;
                    }
                    catch (ProviderException)
                    {
                        // retry failed — fall through to LogFallback
                    }
                }

                operationLogger.LogFallback(provider.Name, nextProviderName, category, request.RequestId);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Provider {ProviderName} network error; falling back.", provider.Name);
                operationLogger.LogFallback(provider.Name, nextProviderName, InternalErrorCategory.ServerError, request.RequestId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Provider {ProviderName} threw an unexpected exception; falling back.",
                    provider.Name);
                operationLogger.LogFallback(provider.Name, nextProviderName,
                    InternalErrorCategory.UnknownError, request.RequestId);
            }
        }

        operationLogger.LogFailed(request.RequestId, "All providers exhausted");
        throw new InferRouterException("All providers exhausted");
    }
}
