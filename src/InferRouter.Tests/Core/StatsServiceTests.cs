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
using InferRouter.Core.Services;
using Moq;
using Xunit;

namespace InferRouter.Tests.Core;

public class StatsServiceTests
{
    private static Mock<IInferenceClient> MakeProvider(string name)
    {
        var mock = new Mock<IInferenceClient>();
        mock.Setup(p => p.Name).Returns(name);
        return mock;
    }

    private static StatsService CreateService(
        IRateLimitTracker tracker,
        IReadOnlyList<IInferenceClient> providers,
        IInferenceClient finalFallback) =>
        new StatsService(tracker, providers, finalFallback, Path.GetTempPath());

    [Fact]
    public void GetLiveStats_WithTwoCloudProvidersAndFinalFallback_ReturnsThreeEntries()
    {
        var trackerMock = new Mock<IRateLimitTracker>();
        trackerMock
            .Setup(t => t.GetStats(It.IsAny<string>()))
            .Returns<string>(name => new ProviderRateLimitStats(name, 0, 0, 0, 0, false));

        var cloud1 = MakeProvider("cloud-1");
        var cloud2 = MakeProvider("cloud-2");
        var fallback = MakeProvider("local-fallback");

        var service = CreateService(
            trackerMock.Object,
            new List<IInferenceClient> { cloud1.Object, cloud2.Object }.AsReadOnly(),
            fallback.Object);

        var stats = service.GetLiveStats();

        Assert.Equal(3, stats.Count);
        var names = stats.Select(s => s.ProviderName).ToHashSet();
        Assert.Contains("cloud-1", names);
        Assert.Contains("cloud-2", names);
        Assert.Contains("local-fallback", names);
    }

    [Fact]
    public void GetLiveStats_FinalFallbackEntry_HasCorrectProviderName()
    {
        var trackerMock = new Mock<IRateLimitTracker>();
        trackerMock
            .Setup(t => t.GetStats(It.IsAny<string>()))
            .Returns<string>(name => new ProviderRateLimitStats(name, 0, 0, 0, 0, false));

        var fallback = MakeProvider("my-local-fallback");

        var service = CreateService(
            trackerMock.Object,
            new List<IInferenceClient>().AsReadOnly(),
            fallback.Object);

        var stats = service.GetLiveStats();

        Assert.Single(stats);
        Assert.Equal("my-local-fallback", stats[0].ProviderName);
    }
}
