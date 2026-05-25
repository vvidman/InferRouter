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

public class FallbackChainExecutor(
    IReadOnlyList<ILlmProvider> providers,
    RateLimitTracker rateLimitTracker,
    ErrorNormalizer errorNormalizer,
    OperationLogger operationLogger,
    ILogger<FallbackChainExecutor> logger)
{
    public async Task<InferResult> ExecuteAsync(InferRequest request, CancellationToken ct)
    {
        operationLogger.LogStarted(request);

        for (int i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];
            var nextProviderName = i + 1 < providers.Count ? providers[i + 1].Name : string.Empty;

            if (rateLimitTracker.IsExhausted(provider.Name))
            {
                operationLogger.LogRateLimitHit(provider.Name, request.RequestId);
                continue;
            }

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
        }

        operationLogger.LogFailed(request.RequestId, "All providers exhausted");
        throw new InferRouterException("All providers exhausted");
    }
}
