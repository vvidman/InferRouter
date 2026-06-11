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
using InferRouter.Core.Services;
using Xunit;

namespace InferRouter.Tests.Core;

public class StartupValidatorTests
{
    private static ProviderConfig OpenAi(string name, int daily = 100, int rpm = 10) =>
        new() { Name = name, Type = ProviderType.OpenAiCompatible, DailyRequestLimit = daily, RequestsPerMinute = rpm };

    private static ProviderConfig LocalGguf(string name = "local") =>
        new() { Name = name, Type = ProviderType.LocalGguf };

    // --- ValidateNoEmptyNames ---

    [Fact]
    public void ValidateNoEmptyNames_AllHaveNames_ReturnsNull()
    {
        var providers = new List<ProviderConfig> { OpenAi("groq"), LocalGguf() };
        Assert.Null(StartupValidator.ValidateNoEmptyNames(providers));
    }

    [Fact]
    public void ValidateNoEmptyNames_EmptyName_ReturnsError()
    {
        var providers = new List<ProviderConfig> { OpenAi(""), LocalGguf() };
        var error = StartupValidator.ValidateNoEmptyNames(providers);
        Assert.NotNull(error);
        Assert.Contains("empty Name", error);
    }

    [Fact]
    public void ValidateNoEmptyNames_WhitespaceName_ReturnsError()
    {
        var providers = new List<ProviderConfig> { OpenAi("   "), LocalGguf() };
        var error = StartupValidator.ValidateNoEmptyNames(providers);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateNoEmptyNames_NullName_ReturnsError()
    {
        var providers = new List<ProviderConfig>
        {
            new() { Name = null!, Type = ProviderType.OpenAiCompatible },
            LocalGguf()
        };
        var error = StartupValidator.ValidateNoEmptyNames(providers);
        Assert.NotNull(error);
    }

    // --- ValidateNoDuplicateNames ---

    [Fact]
    public void ValidateNoDuplicateNames_AllUnique_ReturnsNull()
    {
        var providers = new List<ProviderConfig> { OpenAi("groq"), OpenAi("gemini"), LocalGguf() };
        Assert.Null(StartupValidator.ValidateNoDuplicateNames(providers));
    }

    [Fact]
    public void ValidateNoDuplicateNames_DuplicateName_ReturnsError()
    {
        var providers = new List<ProviderConfig> { OpenAi("groq"), OpenAi("groq"), LocalGguf() };
        var error = StartupValidator.ValidateNoDuplicateNames(providers);
        Assert.NotNull(error);
        Assert.Contains("groq", error);
        Assert.Contains("unique", error);
    }

    [Fact]
    public void ValidateNoDuplicateNames_CaseInsensitiveDuplicate_ReturnsError()
    {
        var providers = new List<ProviderConfig> { OpenAi("Groq"), OpenAi("groq"), LocalGguf() };
        var error = StartupValidator.ValidateNoDuplicateNames(providers);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateNoDuplicateNames_MultipleDuplicates_ErrorListsAll()
    {
        var providers = new List<ProviderConfig>
        {
            OpenAi("groq"), OpenAi("groq"),
            OpenAi("gemini"), OpenAi("gemini"),
            LocalGguf()
        };
        var error = StartupValidator.ValidateNoDuplicateNames(providers);
        Assert.NotNull(error);
        Assert.Contains("groq", error);
        Assert.Contains("gemini", error);
    }

    // --- ValidateFinalFallbackPresent ---

    [Fact]
    public void ValidateFinalFallbackPresent_NullFallback_ReturnsError()
    {
        var error = StartupValidator.ValidateFinalFallbackPresent(null);
        Assert.NotNull(error);
        Assert.Contains("FinalFallback", error);
        Assert.Contains("required", error);
    }

    [Fact]
    public void ValidateFinalFallbackPresent_LocalGgufFallback_ReturnsNull()
    {
        var fallback = new ProviderConfig { Name = "local", Type = ProviderType.LocalGguf };
        Assert.Null(StartupValidator.ValidateFinalFallbackPresent(fallback));
    }

    [Fact]
    public void ValidateFinalFallbackPresent_OpenAiCompatibleFallback_ReturnsNull()
    {
        var fallback = new ProviderConfig { Name = "ollama", Type = ProviderType.OpenAiCompatible, BaseUrl = "http://localhost:11434" };
        Assert.Null(StartupValidator.ValidateFinalFallbackPresent(fallback));
    }

    // --- ValidateNoLocalGgufInProviders ---

    [Fact]
    public void ValidateNoLocalGgufInProviders_NoLocalGguf_ReturnsNull()
    {
        var providers = new List<ProviderConfig> { OpenAi("groq"), OpenAi("gemini") };
        Assert.Null(StartupValidator.ValidateNoLocalGgufInProviders(providers));
    }

    [Fact]
    public void ValidateNoLocalGgufInProviders_HasLocalGguf_ReturnsError()
    {
        var providers = new List<ProviderConfig> { OpenAi("groq"), LocalGguf("local") };
        var error = StartupValidator.ValidateNoLocalGgufInProviders(providers);
        Assert.NotNull(error);
        Assert.Contains("LocalGguf", error);
        Assert.Contains("FinalFallback", error);
    }

    [Fact]
    public void ValidateNoLocalGgufInProviders_EmptyList_ReturnsNull()
    {
        Assert.Null(StartupValidator.ValidateNoLocalGgufInProviders(new List<ProviderConfig>()));
    }

    // --- ValidateFinalFallbackBaseUrl ---

    [Fact]
    public void ValidateFinalFallbackBaseUrl_OpenAiCompatibleMissingBaseUrl_ReturnsError()
    {
        var fallback = new ProviderConfig { Name = "ollama", Type = ProviderType.OpenAiCompatible };
        var error = StartupValidator.ValidateFinalFallbackBaseUrl(fallback);
        Assert.NotNull(error);
        Assert.Contains("BaseUrl", error);
        Assert.Contains("ollama", error);
    }

    [Fact]
    public void ValidateFinalFallbackBaseUrl_OpenAiCompatibleWithBaseUrl_ReturnsNull()
    {
        var fallback = new ProviderConfig { Name = "ollama", Type = ProviderType.OpenAiCompatible, BaseUrl = "http://localhost:11434" };
        Assert.Null(StartupValidator.ValidateFinalFallbackBaseUrl(fallback));
    }

    [Fact]
    public void ValidateFinalFallbackBaseUrl_LocalGgufType_ReturnsNull()
    {
        var fallback = new ProviderConfig { Name = "local", Type = ProviderType.LocalGguf };
        Assert.Null(StartupValidator.ValidateFinalFallbackBaseUrl(fallback));
    }

    // --- ValidateNoNegativeLimits ---

    [Fact]
    public void ValidateNoNegativeLimits_AllNonNegative_ReturnsEmpty()
    {
        var providers = new List<ProviderConfig> { OpenAi("groq", 100, 10), LocalGguf() };
        Assert.Empty(StartupValidator.ValidateNoNegativeLimits(providers));
    }

    [Fact]
    public void ValidateNoNegativeLimits_ZeroLimits_ReturnsEmpty()
    {
        var providers = new List<ProviderConfig> { OpenAi("groq", 0, 0), LocalGguf() };
        Assert.Empty(StartupValidator.ValidateNoNegativeLimits(providers));
    }

    [Fact]
    public void ValidateNoNegativeLimits_NegativeDailyLimit_ReturnsError()
    {
        var providers = new List<ProviderConfig> { OpenAi("groq", -1, 10), LocalGguf() };
        var errors = StartupValidator.ValidateNoNegativeLimits(providers);
        Assert.Single(errors);
        Assert.Contains("groq", errors[0]);
        Assert.Contains("DailyRequestLimit", errors[0]);
        Assert.Contains("-1", errors[0]);
    }

    [Fact]
    public void ValidateNoNegativeLimits_NegativeRpm_ReturnsError()
    {
        var providers = new List<ProviderConfig> { OpenAi("groq", 100, -5), LocalGguf() };
        var errors = StartupValidator.ValidateNoNegativeLimits(providers);
        Assert.Single(errors);
        Assert.Contains("groq", errors[0]);
        Assert.Contains("RequestsPerMinute", errors[0]);
        Assert.Contains("-5", errors[0]);
    }

    [Fact]
    public void ValidateNoNegativeLimits_BothNegative_ReturnsTwoErrors()
    {
        var providers = new List<ProviderConfig> { OpenAi("groq", -1, -5), LocalGguf() };
        var errors = StartupValidator.ValidateNoNegativeLimits(providers);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void ValidateNoNegativeLimits_MultipleProvidersWithNegative_ReturnsErrorPerViolation()
    {
        var providers = new List<ProviderConfig>
        {
            OpenAi("groq", -1, 10),
            OpenAi("gemini", 100, -5),
            LocalGguf()
        };
        var errors = StartupValidator.ValidateNoNegativeLimits(providers);
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("groq") && e.Contains("DailyRequestLimit"));
        Assert.Contains(errors, e => e.Contains("gemini") && e.Contains("RequestsPerMinute"));
    }

    // --- WarnWeightedRoundRobinAllZeroDailyLimits ---

    [Fact]
    public void WarnWeightedRoundRobin_NotWeightedRoundRobin_ReturnsNull()
    {
        var providers = new List<ProviderConfig> { OpenAi("groq", 0, 0), LocalGguf() };
        Assert.Null(StartupValidator.WarnWeightedRoundRobinAllZeroDailyLimits(providers, "ChainOfResponsibility"));
    }

    [Fact]
    public void WarnWeightedRoundRobin_AllCloudProvidersZeroDailyLimit_ReturnsWarning()
    {
        var providers = new List<ProviderConfig>
        {
            OpenAi("groq", 0, 10),
            OpenAi("gemini", 0, 10),
            LocalGguf()
        };
        var warn = StartupValidator.WarnWeightedRoundRobinAllZeroDailyLimits(providers, "WeightedRoundRobin");
        Assert.NotNull(warn);
        Assert.Contains("WeightedRoundRobin", warn);
        Assert.Contains("DailyRequestLimit: 0", warn);
    }

    [Fact]
    public void WarnWeightedRoundRobin_SomeCloudProvidersHavePositiveDailyLimit_ReturnsNull()
    {
        var providers = new List<ProviderConfig>
        {
            OpenAi("groq", 0, 10),
            OpenAi("gemini", 100, 10),
            LocalGguf()
        };
        Assert.Null(StartupValidator.WarnWeightedRoundRobinAllZeroDailyLimits(providers, "WeightedRoundRobin"));
    }

    [Fact]
    public void WarnWeightedRoundRobin_NoCloudProviders_ReturnsNull()
    {
        // Only LocalGguf — nothing to warn about (structural error handled elsewhere)
        var providers = new List<ProviderConfig> { LocalGguf() };
        Assert.Null(StartupValidator.WarnWeightedRoundRobinAllZeroDailyLimits(providers, "WeightedRoundRobin"));
    }

    // --- WarnUnknownRoutingStrategy ---

    [Fact]
    public void WarnUnknownRoutingStrategy_KnownStrategy_ReturnsNull()
    {
        Assert.Null(StartupValidator.WarnUnknownRoutingStrategy("ChainOfResponsibility"));
        Assert.Null(StartupValidator.WarnUnknownRoutingStrategy("WeightedRoundRobin"));
        Assert.Null(StartupValidator.WarnUnknownRoutingStrategy("LeastUsed"));
    }

    [Fact]
    public void WarnUnknownRoutingStrategy_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(StartupValidator.WarnUnknownRoutingStrategy(null));
        Assert.Null(StartupValidator.WarnUnknownRoutingStrategy(""));
    }

    [Fact]
    public void WarnUnknownRoutingStrategy_UnknownValue_ReturnsWarning()
    {
        var warn = StartupValidator.WarnUnknownRoutingStrategy("InvalidValue");
        Assert.NotNull(warn);
        Assert.Contains("InvalidValue", warn);
        Assert.Contains("ChainOfResponsibility", warn);
        Assert.Contains("WeightedRoundRobin", warn);
        Assert.Contains("LeastUsed", warn);
    }

    [Fact]
    public void WarnUnknownRoutingStrategy_CaseInsensitiveMatch_ReturnsNull()
    {
        Assert.Null(StartupValidator.WarnUnknownRoutingStrategy("chainofresponsibility"));
        Assert.Null(StartupValidator.WarnUnknownRoutingStrategy("WEIGHTEDROUNDROBIN"));
        Assert.Null(StartupValidator.WarnUnknownRoutingStrategy("leastused"));
    }
}
