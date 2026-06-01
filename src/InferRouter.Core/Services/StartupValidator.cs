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
using InferRouter.Core.Domain;

namespace InferRouter.Core.Services;

public static class StartupValidator
{
    private static readonly HashSet<string> KnownStrategies =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ChainOfResponsibility",
            "WeightedRoundRobin",
            "LeastUsed"
        };

    public static string? ValidateNoEmptyNames(IReadOnlyList<ProviderConfig> providers)
    {
        if (providers.Any(p => string.IsNullOrWhiteSpace(p.Name)))
            return "One or more providers have an empty Name. All providers must have a non-empty name.";
        return null;
    }

    public static string? ValidateNoDuplicateNames(IReadOnlyList<ProviderConfig> providers)
    {
        var duplicates = providers
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            return $"Duplicate provider names detected: {string.Join(", ", duplicates)}. Provider names must be unique.";
        return null;
    }

    public static string? ValidateNoMultipleLocalGguf(IReadOnlyList<ProviderConfig> providers)
    {
        if (providers.Count(p => p.Type == ProviderType.LocalGguf) > 1)
            return "Multiple LocalGguf providers configured. Exactly one LocalGguf entry is required and must be last.";
        return null;
    }

    public static IReadOnlyList<string> ValidateNoNegativeLimits(IReadOnlyList<ProviderConfig> providers)
    {
        var errors = new List<string>();
        foreach (var p in providers)
        {
            if (p.DailyRequestLimit < 0)
                errors.Add(
                    $"Provider '{p.Name}' has a negative DailyRequestLimit ({p.DailyRequestLimit}). Value must be 0 or greater.");
            if (p.RequestsPerMinute < 0)
                errors.Add(
                    $"Provider '{p.Name}' has a negative RequestsPerMinute ({p.RequestsPerMinute}). Value must be 0 or greater.");
        }
        return errors.AsReadOnly();
    }

    public static string? WarnWeightedRoundRobinAllZeroDailyLimits(
        IReadOnlyList<ProviderConfig> providers,
        string? routingStrategy)
    {
        if (routingStrategy != "WeightedRoundRobin")
            return null;
        var cloudProviders = providers.Where(p => p.Type != ProviderType.LocalGguf).ToList();
        if (cloudProviders.Count > 0 && cloudProviders.All(p => p.DailyRequestLimit == 0))
            return "RoutingStrategy is WeightedRoundRobin but all cloud providers have DailyRequestLimit: 0. " +
                   "WeightedRoundRobin will return an empty provider list on every request, causing immediate " +
                   "LlamaSharp fallback. Consider setting DailyRequestLimit or switching to a different strategy.";
        return null;
    }

    public static string? WarnUnknownRoutingStrategy(string? routingStrategy)
    {
        if (string.IsNullOrEmpty(routingStrategy) || KnownStrategies.Contains(routingStrategy))
            return null;
        return $"RoutingStrategy value '{routingStrategy}' is not recognised. " +
               "Falling back to ChainOfResponsibility. Valid values: ChainOfResponsibility, WeightedRoundRobin, LeastUsed.";
    }
}
