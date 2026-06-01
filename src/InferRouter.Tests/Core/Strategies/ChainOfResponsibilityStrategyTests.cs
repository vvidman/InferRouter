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
using InferRouter.Core.Strategies;
using Moq;
using Xunit;

namespace InferRouter.Tests.Core.Strategies;

public class ChainOfResponsibilityStrategyTests
{
    private static Mock<ILlmProvider> MakeProvider(string name)
    {
        var mock = new Mock<ILlmProvider>();
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

    [Fact]
    public void TwoEligibleProviders_ReturnedInConfigDefinedOrder()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var tracker = MakeTracker();

        var strategy = new ChainOfResponsibilityStrategy([p1.Object, p2.Object], tracker.Object);

        var result = strategy.GetOrderedProviders();
        Assert.Equal(2, result.Count);
        Assert.Equal("p1", result[0].Name);
        Assert.Equal("p2", result[1].Name);
    }

    [Fact]
    public void FirstProviderExhausted_OnlySecondReturned()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var tracker = MakeTracker("p1");

        var strategy = new ChainOfResponsibilityStrategy([p1.Object, p2.Object], tracker.Object);

        var result = strategy.GetOrderedProviders();
        Assert.Single(result);
        Assert.Equal("p2", result[0].Name);
    }

    [Fact]
    public void AllProvidersExhausted_ReturnsEmpty()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var tracker = MakeTracker("p1", "p2");

        var strategy = new ChainOfResponsibilityStrategy([p1.Object, p2.Object], tracker.Object);

        Assert.Empty(strategy.GetOrderedProviders());
    }

    [Fact]
    public void SingleEligibleProvider_ReturnsThatProvider()
    {
        var p1 = MakeProvider("p1");
        var tracker = MakeTracker();

        var strategy = new ChainOfResponsibilityStrategy([p1.Object], tracker.Object);

        var result = strategy.GetOrderedProviders();
        Assert.Single(result);
        Assert.Equal("p1", result[0].Name);
    }
}
