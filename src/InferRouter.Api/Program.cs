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

// Startup validation 2: last provider must be LocalGguf (local fallback)
if (options.Providers[^1].Type != ProviderType.LocalGguf)
{
    Console.Error.WriteLine("FATAL: The last entry in InferRouter.Providers must have Type == LocalGguf.");
    return 1;
}

// Startup validation 3: all OpenAiCompatible providers must have a non-empty BaseUrl
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

// Startup validation 4: LocalGguf ModelPath must exist on disk
var ggufConfig = options.Providers.First(p => p.Type == ProviderType.LocalGguf);
if (!File.Exists(ggufConfig.ModelPath))
{
    Console.Error.WriteLine(
        $"FATAL: LocalGguf model not found at '{ggufConfig.ModelPath}'. Ensure the model file exists and ModelPath is correct.");
    return 1;
}

// Read API keys early (step 5 of startup validation — null keys are warned later, not fatal)
var apiKeys = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
foreach (var config in options.Providers.Where(p => p.Type == ProviderType.OpenAiCompatible))
    apiKeys[config.Name] = SecretReader.ReadApiKey(config.Name);

// DI registrations
builder.Services.Configure<InferRouterOptions>(builder.Configuration.GetSection("InferRouter"));

builder.Services.AddSingleton<ErrorNormalizer>();

builder.Services.AddSingleton<RateLimitTracker>(sp =>
    new RateLimitTracker(
        options.Providers.AsReadOnly(),
        sp.GetRequiredService<ILogger<RateLimitTracker>>()));

builder.Services.AddSingleton<OperationLogger>(_ => new OperationLogger(options.OperationLogPath));

builder.Services.AddHttpClient();

// Providers built in config order, registered as IReadOnlyList<ILlmProvider>
builder.Services.AddSingleton<IReadOnlyList<ILlmProvider>>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var providers = new List<ILlmProvider>();

    foreach (var config in options.Providers)
    {
        ILlmProvider provider = config.Type switch
        {
            ProviderType.OpenAiCompatible => new OpenAiCompatibleProvider(
                config,
                apiKeys.GetValueOrDefault(config.Name),
                httpClientFactory.CreateClient()),
            ProviderType.LocalGguf => new LlamaSharpProvider(config),
            _ => throw new InvalidOperationException($"Unknown provider type: {config.Type}")
        };

        providers.Add(provider);
    }

    return providers.AsReadOnly();
});

builder.Services.AddSingleton<FallbackChainExecutor>();

var app = builder.Build();

// Configure SecretReader with the DI logger factory for runtime use
SecretReader.Configure(app.Services.GetRequiredService<ILoggerFactory>());

// Startup validation 5: warn for each OpenAiCompatible provider with a missing API key
var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
foreach (var (name, key) in apiKeys)
{
    if (key is null)
        startupLogger.LogWarning(
            "API key missing for provider '{ProviderName}'. Requests to this provider will result in AuthError until the secret is available.",
            name);
}

ChatCompletionsEndpoint.Map(app);

app.Run();
return 0;
