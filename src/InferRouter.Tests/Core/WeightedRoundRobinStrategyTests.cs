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

public class WeightedRoundRobinStrategyTests
{
    private static Mock<IInferenceClient> MakeProvider(string name)
    {
        var mock = new Mock<IInferenceClient>();
        mock.Setup(p => p.Name).Returns(name);
        mock.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);
        return mock;
    }

    private static Mock<IRateLimitTracker> MakeTracker(params string[] exhausted)
    {
        var mock = new Mock<IRateLimitTracker>();
        mock.Setup(t => t.IsExhausted(It.IsAny<string>()))
            .Returns<string>(name => exhausted.Contains(name, StringComparer.OrdinalIgnoreCase));
        return mock;
    }

    private static WeightedRoundRobinStrategy Build(
        IReadOnlyList<IInferenceClient> providers,
        IReadOnlyList<ProviderConfig> configs,
        IRateLimitTracker tracker) =>
        new(providers, configs, tracker);

    // --- Edge cases ---

    [Fact]
    public void AllProvidersHaveZeroDailyLimit_ReturnsEmpty()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var configs = new List<ProviderConfig>
        {
            new() { Name = "p1", DailyRequestLimit = 0 },
            new() { Name = "p2", DailyRequestLimit = 0 },
        };
        var tracker = MakeTracker();

        var strategy = Build(
            [p1.Object, p2.Object],
            configs.AsReadOnly(),
            tracker.Object);

        Assert.Empty(strategy.GetOrderedProviders());
    }

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
        var tracker = MakeTracker("p1", "p2");

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
        var tracker = MakeTracker();

        var strategy = Build([p1.Object], configs.AsReadOnly(), tracker.Object);

        var result = strategy.GetOrderedProviders();
        Assert.Single(result);
        Assert.Equal("p1", result[0].Name);
    }

    [Fact]
    public void ExhaustedProviderExcluded_OnlyEligibleReturned()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var configs = new List<ProviderConfig>
        {
            new() { Name = "p1", DailyRequestLimit = 1000 },
            new() { Name = "p2", DailyRequestLimit = 500 },
        };
        var tracker = MakeTracker("p1");

        var strategy = Build(
            [p1.Object, p2.Object],
            configs.AsReadOnly(),
            tracker.Object);

        var result = strategy.GetOrderedProviders();
        Assert.Single(result);
        Assert.Equal("p2", result[0].Name);
    }

    [Fact]
    public void ZeroDailyLimitProviderExcluded_OnlyPositiveLimitProviderReturned()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var configs = new List<ProviderConfig>
        {
            new() { Name = "p1", DailyRequestLimit = 0 },
            new() { Name = "p2", DailyRequestLimit = 500 },
        };
        var tracker = MakeTracker();

        var strategy = Build(
            [p1.Object, p2.Object],
            configs.AsReadOnly(),
            tracker.Object);

        var result = strategy.GetOrderedProviders();
        Assert.Single(result);
        Assert.Equal("p2", result[0].Name);
    }

    [Fact]
    public void TwoEligibleProviders_BothAppearInResult()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var configs = new List<ProviderConfig>
        {
            new() { Name = "p1", DailyRequestLimit = 14400 },
            new() { Name = "p2", DailyRequestLimit = 1500 },
        };
        var tracker = MakeTracker();

        var strategy = Build(
            [p1.Object, p2.Object],
            configs.AsReadOnly(),
            tracker.Object);

        var result = strategy.GetOrderedProviders();
        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.Name == "p1");
        Assert.Contains(result, p => p.Name == "p2");
    }

    [Fact]
    public void WeightedSelection_HigherWeightAppearFirstMoreOften()
    {
        // p1 has 90% weight, p2 has 10%. Over 1000 runs, p1 should lead ≥80% of the time.
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var configs = new List<ProviderConfig>
        {
            new() { Name = "p1", DailyRequestLimit = 9000 },
            new() { Name = "p2", DailyRequestLimit = 1000 },
        };
        var tracker = MakeTracker();
        var strategy = Build(
            [p1.Object, p2.Object],
            configs.AsReadOnly(),
            tracker.Object);

        int p1First = 0;
        for (int i = 0; i < 1000; i++)
        {
            var result = strategy.GetOrderedProviders();
            if (result[0].Name == "p1")
                p1First++;
        }

        // With 90% weight, p1 should appear first in at least 800 of 1000 runs
        Assert.True(p1First >= 800, $"Expected p1 first ≥800/1000 times, got {p1First}");
    }
}
