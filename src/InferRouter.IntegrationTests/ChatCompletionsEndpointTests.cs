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

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using InferRouter.Core.Config;
using InferRouter.Core.Domain;
using InferRouter.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace InferRouter.IntegrationTests;

public class ChatCompletionsSuccessFactory : InferRouterWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var cloud = new Mock<IInferenceClient>();
            cloud.Setup(p => p.Name).Returns("test-provider");
            cloud.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);
            cloud.Setup(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InferResult("req-1", "test-provider", "test-model",
                    "Hello!", 10, 5, 50, false));

            var local = new Mock<IInferenceClient>();
            local.Setup(p => p.Name).Returns("test-local");
            local.Setup(p => p.Type).Returns(ProviderType.LocalGguf);

            services.AddSingleton<IReadOnlyList<IInferenceClient>>(_ =>
                new List<IInferenceClient> { cloud.Object, local.Object }.AsReadOnly());
        });
    }
}

public class ChatCompletionsAllFailFactory : InferRouterWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var authMappings = new List<ErrorMapping>
            {
                new() { HttpStatus = 401, InternalCategory = InternalErrorCategory.AuthError }
            }.AsReadOnly();

            var cloud = new Mock<IInferenceClient>();
            cloud.Setup(p => p.Name).Returns("test-provider");
            cloud.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);
            cloud.Setup(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ProviderException(401, null, authMappings));

            var local = new Mock<IInferenceClient>();
            local.Setup(p => p.Name).Returns("test-local");
            local.Setup(p => p.Type).Returns(ProviderType.LocalGguf);
            local.Setup(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ProviderException(401, null, authMappings));

            services.AddSingleton<IReadOnlyList<IInferenceClient>>(_ =>
                new List<IInferenceClient> { cloud.Object, local.Object }.AsReadOnly());
        });
    }
}

public class ChatCompletionsFallbackFactory : InferRouterWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var rateMappings = new List<ErrorMapping>
            {
                new() { HttpStatus = 429, InternalCategory = InternalErrorCategory.RateLimit }
            }.AsReadOnly();

            var cloud = new Mock<IInferenceClient>();
            cloud.Setup(p => p.Name).Returns("test-provider");
            cloud.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);
            cloud.Setup(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ProviderException(429, null, rateMappings));

            var local = new Mock<IInferenceClient>();
            local.Setup(p => p.Name).Returns("test-local");
            local.Setup(p => p.Type).Returns(ProviderType.LocalGguf);
            local.Setup(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InferResult("req-fb", "test-local", "local-model",
                    "Fallback response", 8, 4, 200, true));

            services.AddSingleton<IReadOnlyList<IInferenceClient>>(_ =>
                new List<IInferenceClient> { cloud.Object, local.Object }.AsReadOnly());
        });
    }
}

public class ChatCompletionsEndpointTests(ChatCompletionsSuccessFactory factory)
    : IClassFixture<ChatCompletionsSuccessFactory>
{
    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task ValidRequest_ProviderSucceeds_Returns200WithOpenAiShape()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsync("/v1/chat/completions",
            JsonBody("""{"model":"test-model","messages":[{"role":"user","content":"hi"}]}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("id", out _));
        Assert.True(root.TryGetProperty("object", out _));
        Assert.True(root.TryGetProperty("model", out _));
        Assert.True(root.TryGetProperty("choices", out _));
        Assert.True(root.TryGetProperty("usage", out _));
    }

    [Fact]
    public async Task StreamFalse_ProviderSucceeds_Returns200WithJsonShape()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsync("/v1/chat/completions",
            JsonBody("""{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":false}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? "");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("choices", out _));
        Assert.True(root.TryGetProperty("usage", out _));
    }
}

public class ChatCompletionsEndpointTests_AuthError(ChatCompletionsAllFailFactory factory)
    : IClassFixture<ChatCompletionsAllFailFactory>
{
    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task AuthError_NoFurtherProviders_Returns503()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsync("/v1/chat/completions",
            JsonBody("""{"model":"test-model","messages":[{"role":"user","content":"hi"}]}"""));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}

public class ChatCompletionsEndpointTests_Fallback(ChatCompletionsFallbackFactory factory)
    : IClassFixture<ChatCompletionsFallbackFactory>
{
    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task CloudRateLimited_LocalFallbackSucceeds_Returns200()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsync("/v1/chat/completions",
            JsonBody("""{"model":"test-model","messages":[{"role":"user","content":"hi"}]}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

public class ChatCompletionsStreamFactory : InferRouterWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var cloud = new Mock<IInferenceClient>();
            cloud.Setup(p => p.Name).Returns("test-provider");
            cloud.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);
            cloud.Setup(p => p.CompleteStreamingAsync(
                    It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .Returns<InferRequest, CancellationToken>((_, _) => TwoChunks());

            var local = new Mock<IInferenceClient>();
            local.Setup(p => p.Name).Returns("test-local");
            local.Setup(p => p.Type).Returns(ProviderType.LocalGguf);

            services.AddSingleton<IReadOnlyList<IInferenceClient>>(_ =>
                new List<IInferenceClient> { cloud.Object, local.Object }.AsReadOnly());
        });
    }

    private static async IAsyncEnumerable<StreamChunk> TwoChunks()
    {
        yield return new StreamChunk("stream-req-id", "Hello", false, null, null);
        await Task.Yield();
        yield return new StreamChunk("stream-req-id", " world", true, 5, 10);
    }
}

public class ChatCompletionsEndpointTests_Streaming(ChatCompletionsStreamFactory factory)
    : IClassFixture<ChatCompletionsStreamFactory>
{
    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task StreamTrue_ProviderSucceeds_ReturnsSseWithCorrectHeadersAndBody()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsync("/v1/chat/completions",
            JsonBody("""{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/event-stream", response.Content.Headers.ContentType?.ToString() ?? "");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("data: ", body);
        Assert.Contains("data: [DONE]", body);

        var dataLine = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .First(l => l.StartsWith("data: ") && !l.Contains("[DONE]"));
        using var doc = JsonDocument.Parse(dataLine["data: ".Length..]);
        var root = doc.RootElement;
        Assert.Equal("chat.completion.chunk", root.GetProperty("object").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("choices").ValueKind);
    }
}

public class ChatCompletionsStreamFallbackFactory : InferRouterWebAppFactory
{
    private static readonly IReadOnlyList<ErrorMapping> RateMappings = new List<ErrorMapping>
    {
        new() { HttpStatus = 429, InternalCategory = InternalErrorCategory.RateLimit }
    }.AsReadOnly();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var cloud = new Mock<IInferenceClient>();
            cloud.Setup(p => p.Name).Returns("test-provider");
            cloud.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);
            cloud.Setup(p => p.CompleteStreamingAsync(
                    It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .Returns<InferRequest, CancellationToken>((_, _) => CloudThrows());

            var local = new Mock<IInferenceClient>();
            local.Setup(p => p.Name).Returns("test-local");
            local.Setup(p => p.Type).Returns(ProviderType.LocalGguf);
            local.Setup(p => p.CompleteStreamingAsync(
                    It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .Returns<InferRequest, CancellationToken>((_, _) => FallbackChunks());

            services.AddSingleton<IReadOnlyList<IInferenceClient>>(_ =>
                new List<IInferenceClient> { cloud.Object, local.Object }.AsReadOnly());
        });
    }

    private static async IAsyncEnumerable<StreamChunk> CloudThrows()
    {
        await Task.Yield();
        throw new ProviderException(429, null, RateMappings);
#pragma warning disable CS0162 // Unreachable code detected
        yield return default!;
#pragma warning restore CS0162 // Unreachable code detected
    }

    private static async IAsyncEnumerable<StreamChunk> FallbackChunks()
    {
        yield return new StreamChunk("fallback-req-id", "Fallback", false, null, null);
        await Task.Yield();
        yield return new StreamChunk("fallback-req-id", " response", true, 5, 8);
    }
}

public class ChatCompletionsEndpointTests_StreamFallback(ChatCompletionsStreamFallbackFactory factory)
    : IClassFixture<ChatCompletionsStreamFallbackFactory>
{
    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task StreamTrue_CloudThrowsBeforeFirstChunk_FallsBackToLocalAndReturnsStream()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsync("/v1/chat/completions",
            JsonBody("""{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/event-stream", response.Content.Headers.ContentType?.ToString() ?? "");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("data: ", body);
        Assert.Contains("data: [DONE]", body);
    }
}

public class ChatCompletionsStreamNoNativeFactory : InferRouterWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var cloud = new Mock<IInferenceClient>();
            cloud.Setup(p => p.Name).Returns("test-provider");
            cloud.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);
            cloud.Setup(p => p.SupportsStreaming).Returns(false);
            cloud.Setup(p => p.CompleteStreamingAsync(
                    It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .Returns<InferRequest, CancellationToken>((_, _) => SimulatedChunks());

            var local = new Mock<IInferenceClient>();
            local.Setup(p => p.Name).Returns("test-local");
            local.Setup(p => p.Type).Returns(ProviderType.LocalGguf);

            services.AddSingleton<IReadOnlyList<IInferenceClient>>(_ =>
                new List<IInferenceClient> { cloud.Object, local.Object }.AsReadOnly());
        });
    }

    private static async IAsyncEnumerable<StreamChunk> SimulatedChunks()
    {
        yield return new StreamChunk("simulated-req-id", "Simulated", false, null, null);
        await Task.Yield();
        yield return new StreamChunk("simulated-req-id", " response", true, 4, 6);
    }
}

public class ChatCompletionsEndpointTests_StreamNoNative(ChatCompletionsStreamNoNativeFactory factory)
    : IClassFixture<ChatCompletionsStreamNoNativeFactory>
{
    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task StreamTrue_SupportsStreamingFalse_SimulatedSseWithDoneTermination()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsync("/v1/chat/completions",
            JsonBody("""{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/event-stream", response.Content.Headers.ContentType?.ToString() ?? "");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("data: ", body);

        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("data: [DONE]", lines.Last());
    }
}

// Log capture helpers

public sealed class LogEntry(LogLevel level, string category, string message)
{
    public LogLevel Level { get; } = level;
    public string Category { get; } = category;
    public string Message { get; } = message;
}

public sealed class LogCapture
{
    private readonly ConcurrentBag<LogEntry> _entries = new();
    public IReadOnlyCollection<LogEntry> Entries => _entries;

    public void Add(LogLevel level, string category, string message) =>
        _entries.Add(new LogEntry(level, category, message));

    public bool HasWarning(string categoryContains, string messageContains) =>
        _entries.Any(e =>
            e.Level == LogLevel.Warning &&
            e.Category.Contains(categoryContains, StringComparison.OrdinalIgnoreCase) &&
            e.Message.Contains(messageContains, StringComparison.OrdinalIgnoreCase));
}

public sealed class CapturingLogger(LogCapture capture, string categoryName) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        capture.Add(logLevel, categoryName, formatter(state, exception));
}

public sealed class CapturingLoggerProvider(LogCapture capture) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(capture, categoryName);
    public void Dispose() { }
}

// Streaming exhaustion factory and test

public class ChatCompletionsStreamExhaustedFactory : InferRouterWebAppFactory
{
    private static readonly IReadOnlyList<ErrorMapping> AuthMappings = new List<ErrorMapping>
    {
        new() { HttpStatus = 401, InternalCategory = InternalErrorCategory.AuthError }
    }.AsReadOnly();

    public LogCapture Capture { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(Capture)));

        builder.ConfigureTestServices(services =>
        {
            var cloud = new Mock<IInferenceClient>();
            cloud.Setup(p => p.Name).Returns("test-provider");
            cloud.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);
            cloud.Setup(p => p.CompleteStreamingAsync(
                    It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .Returns<InferRequest, CancellationToken>((_, _) => ThrowAuth());

            var local = new Mock<IInferenceClient>();
            local.Setup(p => p.Name).Returns("test-local");
            local.Setup(p => p.Type).Returns(ProviderType.LocalGguf);
            local.Setup(p => p.CompleteStreamingAsync(
                    It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
                .Returns<InferRequest, CancellationToken>((_, _) => ThrowAuth());

            services.AddSingleton<IReadOnlyList<IInferenceClient>>(_ =>
                new List<IInferenceClient> { cloud.Object, local.Object }.AsReadOnly());
        });
    }

    private static async IAsyncEnumerable<StreamChunk> ThrowAuth()
    {
        await Task.Yield();
        throw new ProviderException(401, null, AuthMappings);
#pragma warning disable CS0162 // Unreachable code detected
        yield return default!;
#pragma warning restore CS0162 // Unreachable code detected
    }
}

public class ChatCompletionsEndpointTests_StreamExhausted(ChatCompletionsStreamExhaustedFactory factory)
    : IClassFixture<ChatCompletionsStreamExhaustedFactory>
{
    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task StreamTrue_AllProvidersExhausted_LogsWarning()
    {
        var client = factory.CreateClient();
        await client.PostAsync("/v1/chat/completions",
            JsonBody("""{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true}"""));

        Assert.True(
            factory.Capture.HasWarning("ChatCompletionsEndpoint", "All providers exhausted"),
            "Expected a Warning log from ChatCompletionsEndpoint about providers being exhausted");
    }
}
