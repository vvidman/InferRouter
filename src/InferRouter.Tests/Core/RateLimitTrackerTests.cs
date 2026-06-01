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

using System.Collections;
using System.Reflection;
using InferRouter.Core.Config;
using InferRouter.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InferRouter.Tests.Core;

public class RateLimitTrackerTests
{
    private static RateLimitTracker CreateTracker(string name, int daily, int rpm) =>
        new(
            [new ProviderConfig { Name = name, DailyRequestLimit = daily, RequestsPerMinute = rpm }],
            NullLogger<RateLimitTracker>.Instance);

    // --- IsExhausted — daily limit ---

    [Fact]
    public void IsExhausted_DailyLimitZero_NeverExhaustedByDailyCount()
    {
        using var tracker = CreateTracker("p", 0, 0);
        for (var i = 0; i < 100; i++)
            tracker.RecordRequest("p");
        Assert.False(tracker.IsExhausted("p"));
    }

    [Fact]
    public void IsExhausted_DailyCountBelowDailyLimit_ReturnsFalse()
    {
        using var tracker = CreateTracker("p", 5, 0);
        for (var i = 0; i < 4; i++)
            tracker.RecordRequest("p");
        Assert.False(tracker.IsExhausted("p"));
    }

    [Fact]
    public void IsExhausted_DailyCountEqualsDailyLimit_ReturnsTrue()
    {
        using var tracker = CreateTracker("p", 5, 0);
        for (var i = 0; i < 5; i++)
            tracker.RecordRequest("p");
        Assert.True(tracker.IsExhausted("p"));
    }

    [Fact]
    public void IsExhausted_DailyCountExceedsDailyLimit_ReturnsTrue()
    {
        using var tracker = CreateTracker("p", 5, 0);
        for (var i = 0; i < 6; i++)
            tracker.RecordRequest("p");
        Assert.True(tracker.IsExhausted("p"));
    }

    // --- IsExhausted — HardExhausted flag ---

    [Fact]
    public void IsExhausted_AfterMarkExhausted_ReturnsTrue()
    {
        using var tracker = CreateTracker("p", 100, 0);
        tracker.MarkExhausted("p");
        Assert.True(tracker.IsExhausted("p"));
    }

    [Fact]
    public void IsExhausted_AfterMarkExhausted_DailyCountIrrelevant()
    {
        using var tracker = CreateTracker("p", 100, 0);
        // DailyCount is 0, well below limit — MarkExhausted overrides regardless
        tracker.MarkExhausted("p");
        Assert.True(tracker.IsExhausted("p"));
    }

    // --- IsExhausted — RPM window ---

    [Fact]
    public void IsExhausted_RpmLimitZero_NeverExhaustedByRpm()
    {
        using var tracker = CreateTracker("p", 0, 0);
        for (var i = 0; i < 100; i++)
            tracker.RecordRequest("p");
        Assert.False(tracker.IsExhausted("p"));
    }

    [Fact]
    public void IsExhausted_RpmRequestsBelowRpmLimit_ReturnsFalse()
    {
        using var tracker = CreateTracker("p", 0, 5);
        for (var i = 0; i < 4; i++)
            tracker.RecordRequest("p");
        Assert.False(tracker.IsExhausted("p"));
    }

    [Fact]
    public void IsExhausted_RpmRequestsEqualsRpmLimit_ReturnsTrue()
    {
        using var tracker = CreateTracker("p", 0, 5);
        for (var i = 0; i < 5; i++)
            tracker.RecordRequest("p");
        Assert.True(tracker.IsExhausted("p"));
    }

    // --- RecordRequest ---

    [Fact]
    public void RecordRequest_IncrementsDailyCount()
    {
        using var tracker = CreateTracker("p", 3, 0);
        Assert.False(tracker.IsExhausted("p"));
        tracker.RecordRequest("p");
        tracker.RecordRequest("p");
        Assert.False(tracker.IsExhausted("p"));
        tracker.RecordRequest("p");
        Assert.True(tracker.IsExhausted("p"));
    }

    [Fact]
    public void RecordRequest_AddsEntryToRpmWindow()
    {
        using var tracker = CreateTracker("p", 0, 2);
        Assert.False(tracker.IsExhausted("p"));
        tracker.RecordRequest("p");
        Assert.False(tracker.IsExhausted("p"));
        tracker.RecordRequest("p");
        Assert.True(tracker.IsExhausted("p"));
    }

    [Fact]
    public void RecordRequest_PrunesOldRpmEntries()
    {
        using var tracker = CreateTracker("p", 0, 5);

        var statesField = typeof(RateLimitTracker)
            .GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var states = (IDictionary)statesField.GetValue(tracker)!;
        var state = states["p"]!;
        var rpmWindow = (Queue<DateTimeOffset>)state.GetType()
            .GetProperty("RpmWindow", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(state)!;

        rpmWindow.Enqueue(DateTimeOffset.UtcNow.AddSeconds(-90));
        rpmWindow.Enqueue(DateTimeOffset.UtcNow.AddSeconds(-90));
        rpmWindow.Enqueue(DateTimeOffset.UtcNow.AddSeconds(-90));

        tracker.RecordRequest("p");

        // 3 old entries pruned, 1 new entry added
        Assert.Single(rpmWindow);
    }

    // --- Unknown provider ---

    [Fact]
    public void IsExhausted_UnknownProvider_ReturnsFalse()
    {
        using var tracker = CreateTracker("p", 5, 5);
        Assert.False(tracker.IsExhausted("unknown"));
    }

    [Fact]
    public void RecordRequest_UnknownProvider_DoesNotThrow()
    {
        using var tracker = CreateTracker("p", 5, 5);
        tracker.RecordRequest("unknown");
    }

    [Fact]
    public void MarkExhausted_UnknownProvider_DoesNotThrow()
    {
        using var tracker = CreateTracker("p", 5, 5);
        tracker.MarkExhausted("unknown");
    }
}
