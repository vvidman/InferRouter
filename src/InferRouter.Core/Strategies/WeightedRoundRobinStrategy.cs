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

public class WeightedRoundRobinStrategy(
    IReadOnlyList<ILlmProvider> cloudProviders,
    IReadOnlyList<ProviderConfig> providerConfigs,
    IRateLimitTracker rateLimitTracker) : IRoutingStrategy
{
    public IReadOnlyList<ILlmProvider> GetOrderedProviders()
    {
        var weightMap = providerConfigs.ToDictionary(
            c => c.Name,
            c => c.DailyRequestLimit,
            StringComparer.OrdinalIgnoreCase);

        var eligible = cloudProviders
            .Where(p =>
                !rateLimitTracker.IsExhausted(p.Name) &&
                weightMap.TryGetValue(p.Name, out var w) && w > 0)
            .Select(p => (Provider: p, Weight: weightMap[p.Name]))
            .ToList();

        if (eligible.Count == 0)
            return [];

        var result = new List<ILlmProvider>(eligible.Count);
        var rng = Random.Shared;

        while (eligible.Count > 0)
        {
            long total = eligible.Sum(e => (long)e.Weight);
            long pick = (long)(rng.NextDouble() * total);

            long cumulative = 0;
            int selectedIndex = 0;
            for (int i = 0; i < eligible.Count; i++)
            {
                cumulative += eligible[i].Weight;
                if (pick < cumulative)
                {
                    selectedIndex = i;
                    break;
                }
                selectedIndex = i;
            }

            result.Add(eligible[selectedIndex].Provider);
            eligible.RemoveAt(selectedIndex);
        }

        return result.AsReadOnly();
    }
}
