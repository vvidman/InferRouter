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

using InferRouter.Api;
using InferRouter.Api.Endpoints;
using InferRouter.Core.Domain;
using InferRouter.Core.Interfaces;
using InferRouter.Core.Services;
using InferRouter.Core.Strategies;
using InferRouter.Providers;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection("InferRouter").Get<InferRouterOptions>()
    ?? new InferRouterOptions();

// Startup validation 1: providers list must not be empty
if (options.Providers.Count == 0)
{
    Console.Error.WriteLine("FATAL: InferRouter.Providers is empty. At least one provider must be configured.");
    return 1;
}

// Startup validation 2: no provider may have an empty Name
var emptyNamesError = StartupValidator.ValidateNoEmptyNames(options.Providers);
if (emptyNamesError != null)
{
    Console.Error.WriteLine($"FATAL: {emptyNamesError}");
    return 1;
}

// Startup validation 3: provider Names must be unique
var duplicateNamesError = StartupValidator.ValidateNoDuplicateNames(options.Providers);
if (duplicateNamesError != null)
{
    Console.Error.WriteLine($"FATAL: {duplicateNamesError}");
    return 1;
}

// Startup validation A: FinalFallback must be present
var finalFallbackPresentError = StartupValidator.ValidateFinalFallbackPresent(options.FinalFallback);
if (finalFallbackPresentError != null)
{
    Console.Error.WriteLine($"FATAL: {finalFallbackPresentError}");
    return 1;
}

// Startup validation B: FinalFallback.Type must be LocalGguf or OpenAiCompatible
if (options.FinalFallback!.Type != ProviderType.LocalGguf &&
    options.FinalFallback.Type != ProviderType.OpenAiCompatible)
{
    Console.Error.WriteLine($"FATAL: FinalFallback.Type must be local_gguf or openai_compatible.");
    return 1;
}

// Startup validation B+: FinalFallback.BaseUrl required for OpenAiCompatible
var finalFallbackBaseUrlError = StartupValidator.ValidateFinalFallbackBaseUrl(options.FinalFallback);
if (finalFallbackBaseUrlError != null)
{
    Console.Error.WriteLine($"FATAL: {finalFallbackBaseUrlError}");
    return 1;
}

// Startup validation C: LocalGguf ModelPath must exist on disk (skipped in Test environment)
if (options.FinalFallback.Type == ProviderType.LocalGguf &&
    !builder.Environment.IsEnvironment("Test") &&
    !File.Exists(options.FinalFallback.ModelPath))
{
    Console.Error.WriteLine(
        $"FATAL: LocalGguf model not found at '{options.FinalFallback.ModelPath}'.");
    return 1;
}

// Startup validation D: OpenAiCompatible FinalFallback HTTP health check (skipped in Test environment)
if (options.FinalFallback.Type == ProviderType.OpenAiCompatible &&
    !builder.Environment.IsEnvironment("Test"))
{
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        await httpClient.GetAsync(options.FinalFallback.BaseUrl);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"FATAL: FinalFallback '{options.FinalFallback.Name}' is unreachable at " +
            $"'{options.FinalFallback.BaseUrl}': {ex.Message}");
        return 1;
    }
}

// Startup validation E: LocalGguf type is not allowed in the Providers array
var ggufInProvidersError = StartupValidator.ValidateNoLocalGgufInProviders(options.Providers);
if (ggufInProvidersError != null)
{
    Console.Error.WriteLine($"FATAL: {ggufInProvidersError}");
    return 1;
}

// Startup validation 6: all OpenAiCompatible providers must have a non-empty BaseUrl
var missingBaseUrl = options.Providers
    .Where(p => p.Type == ProviderType.OpenAiCompatible && string.IsNullOrEmpty(p.BaseUrl))
    .Select(p => p.Name)
    .ToList();

if (missingBaseUrl.Count > 0)
{
    Console.Error.WriteLine(
        $"FATAL: OpenAiCompatible providers missing BaseUrl: {string.Join(", ", missingBaseUrl)}.");
    return 1;
}

// Startup validation 8: DailyRequestLimit and RequestsPerMinute must be >= 0
var negativeLimitErrors = StartupValidator.ValidateNoNegativeLimits(options.Providers);
if (negativeLimitErrors.Count > 0)
{
    foreach (var error in negativeLimitErrors)
        Console.Error.WriteLine($"FATAL: {error}");
    return 1;
}

// DI registrations
builder.Services.Configure<InferRouterOptions>(builder.Configuration.GetSection("InferRouter"));

builder.Services.AddSingleton<SecretReader>();
builder.Services.AddSingleton<ErrorNormalizer>();

builder.Services.AddSingleton<IRateLimitTracker>(sp =>
{
    var allConfigs = options.Providers
        .Append(options.FinalFallback!)
        .ToList()
        .AsReadOnly();

    return new RateLimitTracker(
        allConfigs,
        sp.GetRequiredService<ILogger<RateLimitTracker>>());
});

builder.Services.AddSingleton<OperationLogger>(_ => new OperationLogger(options.OperationLogPath));

builder.Services.AddHttpClient();

// Cloud providers built in config order, registered as IReadOnlyList<IInferenceClient>
builder.Services.AddSingleton<IReadOnlyList<IInferenceClient>>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var secretReader = sp.GetRequiredService<SecretReader>();
    var providers = new List<IInferenceClient>();

    foreach (var config in options.Providers)
    {
        IInferenceClient provider = config.Type switch
        {
            ProviderType.OpenAiCompatible => new OpenAiCompatibleProvider(
                config,
                options.HideModels,
                secretReader,
                httpClientFactory.CreateClient()),
            _ => throw new InvalidOperationException($"Unknown provider type: {config.Type}")
        };

        providers.Add(provider);
    }

    return providers.AsReadOnly();
});

// Final fallback provider — built from FinalFallback config, registered separately
builder.Services.AddSingleton<IInferenceClient>(sp =>
{
    var config = options.FinalFallback!;
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var secretReader = sp.GetRequiredService<SecretReader>();
    return config.Type switch
    {
        ProviderType.LocalGguf => new LlamaSharpProvider(config),
        ProviderType.OpenAiCompatible => new OpenAiCompatibleProvider(
            config,
            options.HideModels,
            secretReader,
            httpClientFactory.CreateClient()),
        _ => throw new InvalidOperationException($"Unsupported FinalFallback type: {config.Type}")
    };
});

builder.Services.AddSingleton<IRoutingStrategy>(sp =>
{
    var cloudProviders = sp.GetRequiredService<IReadOnlyList<IInferenceClient>>();
    var cloudConfigs = options.Providers.AsReadOnly();
    var tracker = sp.GetRequiredService<IRateLimitTracker>();
    var strategyName = options.RoutingStrategy;

    return strategyName switch
    {
        "WeightedRoundRobin" => new WeightedRoundRobinStrategy(cloudProviders, cloudConfigs, tracker),
        "LeastUsed" => new LeastUsedStrategy(cloudProviders, cloudConfigs, tracker),
        _ => new ChainOfResponsibilityStrategy(cloudProviders, tracker)
    };
});

builder.Services.AddSingleton<ProviderOrchestrator>();
builder.Services.AddSingleton<ProviderHealthChecker>(sp => new ProviderHealthChecker(
    sp.GetRequiredService<IReadOnlyList<IInferenceClient>>(),
    sp.GetRequiredService<IInferenceClient>(),
    sp.GetRequiredService<ErrorNormalizer>()));

builder.Services.AddSingleton(sp => new StatsService(
    sp.GetRequiredService<IRateLimitTracker>(),
    sp.GetRequiredService<IReadOnlyList<IInferenceClient>>(),
    sp.GetRequiredService<IInferenceClient>(),
    options.OperationLogPath));

var app = builder.Build();

// Startup validation 9: strategy-specific warnings (logged, do not block startup)
var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();

var weightedWarn = StartupValidator.WarnWeightedRoundRobinAllZeroDailyLimits(options.Providers, options.RoutingStrategy);
if (weightedWarn != null)
    startupLogger.LogWarning("{Warning}", weightedWarn);

var unknownStrategyWarn = StartupValidator.WarnUnknownRoutingStrategy(options.RoutingStrategy);
if (unknownStrategyWarn != null)
    startupLogger.LogWarning("{Warning}", unknownStrategyWarn);

var strategyLogger = app.Services.GetRequiredService<ILogger<ProviderOrchestrator>>();
strategyLogger.LogInformation(
    "ProviderOrchestrator — active routing strategy: {StrategyName}", options.RoutingStrategy);

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

ChatCompletionsEndpoint.Map(app);
HealthProvidersEndpoint.Map(app);
StatsEndpoint.Map(app);

app.Run();
return 0;
