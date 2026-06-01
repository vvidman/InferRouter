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

using InferRouter.Core.Config;
using InferRouter.Core.Interfaces;

namespace InferRouter.Core.Strategies;

public class LeastUsedStrategy(
    IReadOnlyList<ILlmProvider> cloudProviders,
    IReadOnlyList<ProviderConfig> providerConfigs,
    IRateLimitTracker rateLimitTracker) : IRoutingStrategy
{
    public IReadOnlyList<ILlmProvider> GetOrderedProviders()
    {
        var limitMap = providerConfigs.ToDictionary(
            c => c.Name,
            c => c.DailyRequestLimit,
            StringComparer.OrdinalIgnoreCase);

        var eligible = cloudProviders
            .Where(p => !rateLimitTracker.IsExhausted(p.Name))
            .Select(p =>
            {
                limitMap.TryGetValue(p.Name, out var limit);
                double ratio = limit > 0
                    ? (double)rateLimitTracker.GetDailyCount(p.Name) / limit
                    : 0.0;
                return (Provider: p, Ratio: ratio);
            })
            .ToList();

        if (eligible.Count == 0)
            return [];

        var rng = Random.Shared;

        return eligible
            .GroupBy(e => e.Ratio)
            .OrderBy(g => g.Key)
            .SelectMany(g => g.OrderBy(_ => rng.NextDouble()))
            .Select(e => e.Provider)
            .ToList()
            .AsReadOnly();
    }
}
