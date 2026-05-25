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
using Microsoft.Extensions.Logging;

namespace InferRouter.Core.Services;

public sealed class RateLimitTracker : IDisposable
{
    private sealed class ProviderState(int dailyLimit, int rpmLimit)
    {
        public int DailyLimit { get; } = dailyLimit;
        public int RpmLimit { get; } = rpmLimit;
        public int DailyCount { get; set; }
        public bool HardExhausted { get; set; }
        public Queue<DateTimeOffset> RpmWindow { get; } = new();
    }

    private readonly Dictionary<string, ProviderState> _states;
    private readonly ILogger<RateLimitTracker> _logger;
    private readonly object _lock = new();
    private readonly Timer _midnightTimer;

    public RateLimitTracker(IReadOnlyList<ProviderConfig> providers, ILogger<RateLimitTracker> logger)
    {
        _logger = logger;
        _states = providers.ToDictionary(
            p => p.Name,
            p => new ProviderState(p.DailyRequestLimit, p.RequestsPerMinute),
            StringComparer.OrdinalIgnoreCase);

        var delay = TimeUntilMidnightUtc();
        _midnightTimer = new Timer(_ => ResetDailyCounters(), null, delay, TimeSpan.FromHours(24));
    }

    public bool IsExhausted(string providerName)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(providerName, out var state))
                return false;

            if (state.HardExhausted)
                return true;

            if (state.DailyLimit > 0 && state.DailyCount >= state.DailyLimit)
                return true;

            if (state.RpmLimit > 0)
            {
                PruneRpmWindow(state);
                if (state.RpmWindow.Count >= state.RpmLimit)
                    return true;
            }

            return false;
        }
    }

    public void RecordRequest(string providerName)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(providerName, out var state))
                return;

            state.DailyCount++;
            state.RpmWindow.Enqueue(DateTimeOffset.UtcNow);
            PruneRpmWindow(state);
        }
    }

    public void MarkExhausted(string providerName)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(providerName, out var state))
                return;

            state.HardExhausted = true;
        }

        _logger.LogWarning("Provider {ProviderName} marked as rate-limit exhausted until UTC midnight reset.", providerName);
    }

    private void ResetDailyCounters()
    {
        lock (_lock)
        {
            foreach (var state in _states.Values)
            {
                state.DailyCount = 0;
                state.HardExhausted = false;
            }
        }

        _logger.LogInformation("Daily rate limit counters reset at UTC midnight.");
    }

    private static void PruneRpmWindow(ProviderState state)
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-60);
        while (state.RpmWindow.Count > 0 && state.RpmWindow.Peek() <= cutoff)
            state.RpmWindow.Dequeue();
    }

    private static TimeSpan TimeUntilMidnightUtc()
    {
        var now = DateTime.UtcNow;
        var nextMidnight = now.Date.AddDays(1);
        return nextMidnight - now;
    }

    public void Dispose() => _midnightTimer.Dispose();
}
