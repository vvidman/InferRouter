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
using InferRouter.Core.Interfaces;
using InferRouter.Core.Strategies;
using Moq;
using Xunit;

namespace InferRouter.Tests.Core;

public class LeastUsedStrategyTests
{
    private static Mock<IInferenceClient> MakeProvider(string name)
    {
        var mock = new Mock<IInferenceClient>();
        mock.Setup(p => p.Name).Returns(name);
        mock.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);
        return mock;
    }

    private static Mock<IRateLimitTracker> MakeTracker(
        string[] exhausted,
        Dictionary<string, int> dailyCounts)
    {
        var mock = new Mock<IRateLimitTracker>();
        mock.Setup(t => t.IsExhausted(It.IsAny<string>()))
            .Returns<string>(name => exhausted.Contains(name, StringComparer.OrdinalIgnoreCase));
        mock.Setup(t => t.GetDailyCount(It.IsAny<string>()))
            .Returns<string>(name => dailyCounts.TryGetValue(name, out var v) ? v : 0);
        return mock;
    }

    private static LeastUsedStrategy Build(
        IReadOnlyList<IInferenceClient> providers,
        IReadOnlyList<ProviderConfig> configs,
        IRateLimitTracker tracker) =>
        new(providers, configs, tracker);

    // --- Edge cases ---

    [Fact]
    public void AllProvidersExhausted_ReturnsEmpty()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var configs = new List<ProviderConfig>
        {
            new() { Name = "p1", DailyRequestLimit = 1000 },
            new() { Name = "p2", DailyRequestLimit = 500 },
        };
        var tracker = MakeTracker(["p1", "p2"], []);

        var strategy = Build(
            [p1.Object, p2.Object],
            configs.AsReadOnly(),
            tracker.Object);

        Assert.Empty(strategy.GetOrderedProviders());
    }

    [Fact]
    public void SingleEligibleProvider_ReturnsThatProvider()
    {
        var p1 = MakeProvider("p1");
        var configs = new List<ProviderConfig>
        {
            new() { Name = "p1", DailyRequestLimit = 1000 },
        };
        var tracker = MakeTracker([], new Dictionary<string, int> { ["p1"] = 100 });

        var strategy = Build([p1.Object], configs.AsReadOnly(), tracker.Object);

        var result = strategy.GetOrderedProviders();
        Assert.Single(result);
        Assert.Equal("p1", result[0].Name);
    }

    [Fact]
    public void ProvidersOrderedByRatioAscending_LeastUsedFirst()
    {
        // p1: 100/1000 = 0.1, p2: 400/1000 = 0.4 — p1 should be first
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var configs = new List<ProviderConfig>
        {
            new() { Name = "p1", DailyRequestLimit = 1000 },
            new() { Name = "p2", DailyRequestLimit = 1000 },
        };
        var tracker = MakeTracker([], new Dictionary<string, int> { ["p1"] = 100, ["p2"] = 400 });

        var strategy = Build(
            [p1.Object, p2.Object],
            configs.AsReadOnly(),
            tracker.Object);

        var result = strategy.GetOrderedProviders();
        Assert.Equal(2, result.Count);
        Assert.Equal("p1", result[0].Name);
        Assert.Equal("p2", result[1].Name);
    }

    [Fact]
    public void ZeroDailyLimitProvider_TreatedAsRatioZero_AppearsFirst()
    {
        // p1: limit 0 → ratio 0, p2: 500/1000 = 0.5 — p1 should be first
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var configs = new List<ProviderConfig>
        {
            new() { Name = "p1", DailyRequestLimit = 0 },
            new() { Name = "p2", DailyRequestLimit = 1000 },
        };
        var tracker = MakeTracker([], new Dictionary<string, int> { ["p1"] = 0, ["p2"] = 500 });

        var strategy = Build(
            [p1.Object, p2.Object],
            configs.AsReadOnly(),
            tracker.Object);

        var result = strategy.GetOrderedProviders();
        Assert.Equal(2, result.Count);
        Assert.Equal("p1", result[0].Name);
        Assert.Equal("p2", result[1].Name);
    }

    [Fact]
    public void ExhaustedProviderExcluded()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var configs = new List<ProviderConfig>
        {
            new() { Name = "p1", DailyRequestLimit = 1000 },
            new() { Name = "p2", DailyRequestLimit = 1000 },
        };
        var tracker = MakeTracker(["p1"], new Dictionary<string, int> { ["p1"] = 10, ["p2"] = 900 });

        var strategy = Build(
            [p1.Object, p2.Object],
            configs.AsReadOnly(),
            tracker.Object);

        var result = strategy.GetOrderedProviders();
        Assert.Single(result);
        Assert.Equal("p2", result[0].Name);
    }

    [Fact]
    public void EqualRatioProviders_RandomTiebreaker_BothOrderingsOccur()
    {
        // p1 and p2 both have ratio 0.5 — over many runs, both orderings should appear
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var configs = new List<ProviderConfig>
        {
            new() { Name = "p1", DailyRequestLimit = 1000 },
            new() { Name = "p2", DailyRequestLimit = 1000 },
        };
        var tracker = MakeTracker([], new Dictionary<string, int> { ["p1"] = 500, ["p2"] = 500 });

        var strategy = Build(
            [p1.Object, p2.Object],
            configs.AsReadOnly(),
            tracker.Object);

        bool sawP1First = false;
        bool sawP2First = false;

        for (int i = 0; i < 200; i++)
        {
            var result = strategy.GetOrderedProviders();
            if (result[0].Name == "p1") sawP1First = true;
            if (result[0].Name == "p2") sawP2First = true;
            if (sawP1First && sawP2First) break;
        }

        Assert.True(sawP1First, "p1 should appear first in at least one run");
        Assert.True(sawP2First, "p2 should appear first in at least one run");
    }

    [Fact]
    public void ThreeProviders_OrderedByRatioThenTiebreaker()
    {
        // p1: 100/1000 = 0.1, p2: 500/1000 = 0.5, p3: 100/1000 = 0.1
        // p1 and p3 tie at 0.1, p2 is last
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var p3 = MakeProvider("p3");
        var configs = new List<ProviderConfig>
        {
            new() { Name = "p1", DailyRequestLimit = 1000 },
            new() { Name = "p2", DailyRequestLimit = 1000 },
            new() { Name = "p3", DailyRequestLimit = 1000 },
        };
        var tracker = MakeTracker(
            [],
            new Dictionary<string, int> { ["p1"] = 100, ["p2"] = 500, ["p3"] = 100 });

        var strategy = Build(
            [p1.Object, p2.Object, p3.Object],
            configs.AsReadOnly(),
            tracker.Object);

        var result = strategy.GetOrderedProviders();
        Assert.Equal(3, result.Count);
        // p2 must always be last (highest ratio)
        Assert.Equal("p2", result[2].Name);
        // First two must be p1 and p3 in some order
        Assert.Contains(result.Take(2), p => p.Name == "p1");
        Assert.Contains(result.Take(2), p => p.Name == "p3");
    }
}
