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
using InferRouter.Core.Services;
using InferRouter.Core.Strategies;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace InferRouter.Tests.Core;

public class ProviderOrchestratorTests
{
    private sealed record OrchestratorBundle(
        ProviderOrchestrator Orchestrator,
        IRateLimitTracker Tracker,
        string LogDir)
    {
        public string LogFilePath =>
            Path.Combine(LogDir, $"operations-{DateOnly.FromDateTime(DateTime.UtcNow):yyyy-MM-dd}.jsonl");
    }

    private static OrchestratorBundle BuildOrchestrator(
        IReadOnlyList<Mock<IInferenceClient>> mocks,
        Mock<IInferenceClient>? finalFallbackMock = null,
        Func<string, ProviderConfig>? configFactory = null)
    {
        var providerConfigs = mocks
            .Select(m => configFactory?.Invoke(m.Object.Name) ?? new ProviderConfig { Name = m.Object.Name })
            .ToList();
        var tracker = new RateLimitTracker(providerConfigs, NullLogger<RateLimitTracker>.Instance);
        var cloudProviders = mocks.Select(m => m.Object).ToList<IInferenceClient>().AsReadOnly();
        var strategy = new ChainOfResponsibilityStrategy(cloudProviders, tracker);
        var logDir = Path.Combine(Path.GetTempPath(), $"inferrouter-test-{Guid.NewGuid()}");
        var orchestrator = new ProviderOrchestrator(
            cloudProviders,
            (finalFallbackMock ?? MakeDefaultFinalFallback()).Object,
            strategy,
            tracker,
            new ErrorNormalizer(),
            new OperationLogger(logDir),
            NullLogger<ProviderOrchestrator>.Instance);
        return new OrchestratorBundle(orchestrator, tracker, logDir);
    }

    private static Mock<IInferenceClient> MakeDefaultFinalFallback()
    {
        var mock = new Mock<IInferenceClient>();
        mock.Setup(p => p.Name).Returns("_default_fallback");
        mock.Setup(p => p.Model).Returns("");
        mock.Setup(p => p.Type).Returns(ProviderType.LocalGguf);
        mock.Setup(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProviderException(401, null, [new ErrorMapping { HttpStatus = 401, InternalCategory = InternalErrorCategory.AuthError }]));
        mock.Setup(p => p.CompleteStreamingAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
            .Returns(ThrowingAsyncEnumerable(new ProviderException(401, null, [new ErrorMapping { HttpStatus = 401, InternalCategory = InternalErrorCategory.AuthError }])));
        return mock;
    }

    private static Mock<IInferenceClient> MakeProvider(string name, string? model = null)
    {
        var mock = new Mock<IInferenceClient>();
        mock.Setup(p => p.Name).Returns(name);
        mock.Setup(p => p.Model).Returns(model ?? "");
        mock.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);
        return mock;
    }

    private static InferRequest MakeRequest(string id = "req-001") =>
        new(id, [new ChatMessage("user", "hello")], "test-model", null, null);

    private static InferResult MakeResult(string providerName, string requestId = "req-001") =>
        new(requestId, providerName, "test-model", "Hello!", 10, 20, 100, false);

    private static ProviderException MakeRateLimitException() =>
        new(429, null, [new ErrorMapping { HttpStatus = 429, InternalCategory = InternalErrorCategory.RateLimit }]);

    private static ProviderException MakeAuthException() =>
        new(401, null, [new ErrorMapping { HttpStatus = 401, InternalCategory = InternalErrorCategory.AuthError }]);

    private static ProviderException MakeServerException() =>
        new(500, null, [new ErrorMapping { HttpStatus = 500, InternalCategory = InternalErrorCategory.ServerError }]);

    // Happy path

    [Fact]
    public async Task SingleProvider_ReturnsResult()
    {
        var p1 = MakeProvider("p1");
        var request = MakeRequest();
        var expected = MakeResult("p1");
        p1.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var bundle = BuildOrchestrator([p1]);
        var result = await bundle.Orchestrator.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task SingleProvider_LogsStartedAndCompleted()
    {
        var p1 = MakeProvider("p1");
        var request = MakeRequest();
        p1.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(MakeResult("p1"));

        var bundle = BuildOrchestrator([p1]);
        await bundle.Orchestrator.ExecuteAsync(request, CancellationToken.None);

        var log = await File.ReadAllTextAsync(bundle.LogFilePath);
        Assert.Contains("infer_started", log);
        Assert.Contains("infer_completed", log);
    }

    [Fact]
    public async Task SingleProvider_RecordsRequestOnSuccess()
    {
        var p1 = MakeProvider("p1");
        var request = MakeRequest();
        p1.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(MakeResult("p1"));

        var bundle = BuildOrchestrator([p1], configFactory: name => new ProviderConfig { Name = name, RequestsPerMinute = 1 });
        await bundle.Orchestrator.ExecuteAsync(request, CancellationToken.None);

        Assert.True(bundle.Tracker.IsExhausted("p1"));
    }

    // Fallback — pre-exhausted provider

    [Fact]
    public async Task FirstProviderPreExhausted_SecondProviderCalled()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var request = MakeRequest();
        p2.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(MakeResult("p2"));

        var bundle = BuildOrchestrator([p1, p2]);
        bundle.Tracker.MarkExhausted("p1");
        await bundle.Orchestrator.ExecuteAsync(request, CancellationToken.None);

        p1.Verify(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        p2.Verify(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FirstProviderPreExhausted_LogsRateLimitHit()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var request = MakeRequest();
        p2.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(MakeResult("p2"));

        var bundle = BuildOrchestrator([p1, p2]);
        bundle.Tracker.MarkExhausted("p1");
        await bundle.Orchestrator.ExecuteAsync(request, CancellationToken.None);

        var log = await File.ReadAllTextAsync(bundle.LogFilePath);
        Assert.Contains("rate_limit_hit", log);
        Assert.Contains("\"p1\"", log);
    }

    // Fallback — ProviderException with RateLimit category

    [Fact]
    public async Task FirstProviderThrowsRateLimit_MarkExhaustedAndFallsToNext()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var request = MakeRequest();
        p1.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>())).ThrowsAsync(MakeRateLimitException());
        p2.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(MakeResult("p2"));

        var bundle = BuildOrchestrator([p1, p2]);
        await bundle.Orchestrator.ExecuteAsync(request, CancellationToken.None);

        Assert.True(bundle.Tracker.IsExhausted("p1"));
        p2.Verify(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FirstProviderThrowsRateLimit_LogsFallback()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var request = MakeRequest();
        p1.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>())).ThrowsAsync(MakeRateLimitException());
        p2.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(MakeResult("p2"));

        var bundle = BuildOrchestrator([p1, p2]);
        await bundle.Orchestrator.ExecuteAsync(request, CancellationToken.None);

        var log = await File.ReadAllTextAsync(bundle.LogFilePath);
        Assert.Contains("infer_fallback", log);
        Assert.Contains("\"p1\"", log);
        Assert.Contains("\"p2\"", log);
    }

    // Fallback — ProviderException with AuthError category

    [Fact]
    public async Task FirstProviderThrowsAuthError_NoMarkExhausted_FallsToNext()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var request = MakeRequest();
        p1.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>())).ThrowsAsync(MakeAuthException());
        p2.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(MakeResult("p2"));

        var bundle = BuildOrchestrator([p1, p2]);
        await bundle.Orchestrator.ExecuteAsync(request, CancellationToken.None);

        Assert.False(bundle.Tracker.IsExhausted("p1"));
        p2.Verify(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Fallback — ProviderException with ServerError category

    [Fact]
    public async Task FirstProviderThrowsServerError_RetrySucceeds_ReturnsResult()
    {
        var p1 = MakeProvider("p1");
        var request = MakeRequest();
        var expected = MakeResult("p1");
        p1.SetupSequence(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(MakeServerException())
            .ReturnsAsync(expected);

        var bundle = BuildOrchestrator([p1]);
        var result = await bundle.Orchestrator.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(expected, result);
        var log = await File.ReadAllTextAsync(bundle.LogFilePath);
        Assert.Contains("infer_completed", log);
    }

    [Fact]
    public async Task FirstProviderThrowsServerError_RetryAlsoFails_FallsToNextAndLogsFallback()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var request = MakeRequest();
        var expected = MakeResult("p2");
        p1.SetupSequence(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(MakeServerException())
            .ThrowsAsync(MakeServerException());
        p2.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var bundle = BuildOrchestrator([p1, p2]);
        var result = await bundle.Orchestrator.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(expected, result);
        var log = await File.ReadAllTextAsync(bundle.LogFilePath);
        Assert.Contains("infer_fallback", log);
    }

    // All providers exhausted

    [Fact]
    public async Task AllProvidersExhausted_ThrowsInferRouterExceptionAndLogsFailed()
    {
        var p1 = MakeProvider("p1");
        var request = MakeRequest();
        p1.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>())).ThrowsAsync(MakeAuthException());

        var bundle = BuildOrchestrator([p1]);
        await Assert.ThrowsAsync<InferRouterException>(() =>
            bundle.Orchestrator.ExecuteAsync(request, CancellationToken.None));

        var log = await File.ReadAllTextAsync(bundle.LogFilePath);
        Assert.Contains("infer_failed", log);
    }

    // Unexpected exception fallback (safety net)

    [Fact]
    public async Task Provider_ThrowsUnexpectedException_FallsBackToNextProvider()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var request = MakeRequest();
        p1.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unexpected error"));
        p2.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult("p2"));

        var bundle = BuildOrchestrator([p1, p2]);
        var result = await bundle.Orchestrator.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal("p2", result.ProviderName);
        p2.Verify(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Provider_ThrowsUnexpectedException_LogsFallback()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var request = MakeRequest();
        p1.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unexpected error"));
        p2.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult("p2"));

        var bundle = BuildOrchestrator([p1, p2]);
        await bundle.Orchestrator.ExecuteAsync(request, CancellationToken.None);

        var log = await File.ReadAllTextAsync(bundle.LogFilePath);
        Assert.Contains("infer_fallback", log);
        Assert.Contains("\"p1\"", log);
    }

    [Fact]
    public async Task Provider_ThrowsOperationCanceledException_Propagates_NotTreatedAsFallback()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var request = MakeRequest();
        p1.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        p2.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult("p2"));

        var bundle = BuildOrchestrator([p1, p2]);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            bundle.Orchestrator.ExecuteAsync(request, CancellationToken.None));

        p2.Verify(p => p.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Streaming helpers

    private static async IAsyncEnumerable<StreamChunk> ToAsyncEnumerable(IEnumerable<StreamChunk> chunks)
    {
        foreach (var chunk in chunks)
            yield return chunk;
    }

    private static async IAsyncEnumerable<StreamChunk> ThrowingAsyncEnumerable(Exception ex)
    {
        bool willThrow = true;
        if (willThrow) throw ex;
        yield break;
    }

    private static List<StreamChunk> MakeChunks(string requestId) =>
    [
        new StreamChunk(requestId, "Hello", false, null, null),
        new StreamChunk(requestId, " World", true, 10, 20)
    ];

    // Streaming — fallback before first chunk

    [Fact]
    public async Task Streaming_FirstProviderThrowsBeforeFirstChunk_FallsBackToNext()
    {
        var p1 = MakeProvider("p1");
        var p2 = MakeProvider("p2");
        var request = MakeRequest();
        var chunks = MakeChunks(request.RequestId);

        p1.Setup(p => p.CompleteStreamingAsync(request, It.IsAny<CancellationToken>()))
            .Returns(ThrowingAsyncEnumerable(MakeRateLimitException()));
        p2.Setup(p => p.CompleteStreamingAsync(request, It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(chunks));

        var bundle = BuildOrchestrator([p1, p2]);
        var result = new List<StreamChunk>();
        await foreach (var chunk in bundle.Orchestrator.ExecuteStreamingAsync(request, CancellationToken.None))
            result.Add(chunk);

        Assert.Equal(chunks.Count, result.Count);
        Assert.Equal("Hello", result[0].Delta);
        Assert.True(result[^1].IsLast);
        p1.Verify(p => p.CompleteStreamingAsync(request, It.IsAny<CancellationToken>()), Times.Once);
        p2.Verify(p => p.CompleteStreamingAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Streaming — LlamaSharp as final fallback

    [Fact]
    public async Task Streaming_LlamaSharpSelected_CompleteStreamingAsyncCalled_ChunksYielded()
    {
        var llamaMock = new Mock<IInferenceClient>();
        llamaMock.Setup(p => p.Name).Returns("llama");
        llamaMock.Setup(p => p.Type).Returns(ProviderType.LocalGguf);
        var request = MakeRequest();
        var chunks = MakeChunks(request.RequestId);
        llamaMock.Setup(p => p.CompleteStreamingAsync(request, It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(chunks));

        var bundle = BuildOrchestrator([], finalFallbackMock: llamaMock);
        var result = new List<StreamChunk>();
        await foreach (var chunk in bundle.Orchestrator.ExecuteStreamingAsync(request, CancellationToken.None))
            result.Add(chunk);

        Assert.Equal(chunks.Count, result.Count);
        Assert.True(result[^1].IsLast);
        llamaMock.Verify(p => p.CompleteStreamingAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Streaming — no IsLast chunk

    [Fact]
    public async Task Streaming_StreamEndsWithoutIsLast_LogsInferFailed()
    {
        var p1 = MakeProvider("p1");
        var request = MakeRequest();
        var chunksWithoutLast = new List<StreamChunk>
        {
            new StreamChunk(request.RequestId, "Hello", false, null, null),
            new StreamChunk(request.RequestId, " world", false, null, null)
        };
        p1.Setup(p => p.CompleteStreamingAsync(request, It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(chunksWithoutLast));

        var bundle = BuildOrchestrator([p1]);
        await foreach (var _ in bundle.Orchestrator.ExecuteStreamingAsync(request, CancellationToken.None)) { }

        var log = await File.ReadAllTextAsync(bundle.LogFilePath);
        Assert.Contains("infer_failed", log);
    }

    // Streaming — logging

    [Fact]
    public async Task Streaming_LogsStreamStartedAndStreamCompleted_WithCorrectArguments()
    {
        var p1 = MakeProvider("p1");
        var request = MakeRequest();
        var chunks = MakeChunks(request.RequestId);
        p1.Setup(p => p.CompleteStreamingAsync(request, It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(chunks));

        var bundle = BuildOrchestrator([p1]);
        await foreach (var _ in bundle.Orchestrator.ExecuteStreamingAsync(request, CancellationToken.None)) { }

        var log = await File.ReadAllTextAsync(bundle.LogFilePath);
        Assert.Contains("stream_started", log);
        Assert.Contains("stream_completed", log);
        Assert.Contains("\"p1\"", log);
        Assert.Contains("\"prompt_tokens\":10", log);
        Assert.Contains("\"completion_tokens\":20", log);
    }

    // Streaming — model name on chunks

    [Fact]
    public async Task Streaming_ChunksCarryConfiguredModelName()
    {
        var p1 = MakeProvider("p1", model: "llama-3-70b");
        var request = MakeRequest();
        var chunks = MakeChunks(request.RequestId);
        p1.Setup(p => p.CompleteStreamingAsync(request, It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(chunks));

        var bundle = BuildOrchestrator([p1]);
        var result = new List<StreamChunk>();
        await foreach (var chunk in bundle.Orchestrator.ExecuteStreamingAsync(request, CancellationToken.None))
            result.Add(chunk);

        Assert.All(result, c => Assert.Equal("llama-3-70b", c.Model));
    }

    [Fact]
    public async Task Streaming_ChunksFallBackToProviderName_WhenModelNotConfigured()
    {
        var p1 = MakeProvider("p1"); // model defaults to ""
        var request = MakeRequest();
        var chunks = MakeChunks(request.RequestId);
        p1.Setup(p => p.CompleteStreamingAsync(request, It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(chunks));

        var bundle = BuildOrchestrator([p1]);
        var result = new List<StreamChunk>();
        await foreach (var chunk in bundle.Orchestrator.ExecuteStreamingAsync(request, CancellationToken.None))
            result.Add(chunk);

        Assert.All(result, c => Assert.Equal("p1", c.Model));
    }

    // FinalFallback injection

    [Fact]
    public async Task AllCloudProvidersFail_FinalFallbackIsCalled()
    {
        var p1 = MakeProvider("p1");
        var fallback = new Mock<IInferenceClient>();
        fallback.Setup(p => p.Name).Returns("fallback");
        fallback.Setup(p => p.Type).Returns(ProviderType.LocalGguf);
        var request = MakeRequest();
        var expected = MakeResult("fallback");
        p1.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(MakeRateLimitException());
        fallback.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var bundle = BuildOrchestrator([p1], finalFallbackMock: fallback);
        var result = await bundle.Orchestrator.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(expected, result);
        fallback.Verify(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FinalFallback_IsNeverPassedToRoutingStrategy()
    {
        var p1 = MakeProvider("p1");
        var fallback = new Mock<IInferenceClient>();
        fallback.Setup(p => p.Name).Returns("fallback");
        fallback.Setup(p => p.Type).Returns(ProviderType.LocalGguf);
        var request = MakeRequest();
        p1.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(MakeRateLimitException());
        fallback.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult("fallback"));

        var cloudProviders = new List<IInferenceClient> { p1.Object }.AsReadOnly();
        var mockStrategy = new Mock<IRoutingStrategy>();
        mockStrategy.Setup(s => s.GetOrderedProviders()).Returns(cloudProviders);

        var tracker = new RateLimitTracker(
            [new ProviderConfig { Name = "p1" }],
            NullLogger<RateLimitTracker>.Instance);
        var logDir = Path.Combine(Path.GetTempPath(), $"inferrouter-test-{Guid.NewGuid()}");
        var orchestrator = new ProviderOrchestrator(
            cloudProviders,
            fallback.Object,
            mockStrategy.Object,
            tracker,
            new ErrorNormalizer(),
            new OperationLogger(logDir),
            NullLogger<ProviderOrchestrator>.Instance);

        await orchestrator.ExecuteAsync(request, CancellationToken.None);

        // Strategy was called and returned cloud-only providers
        mockStrategy.Verify(s => s.GetOrderedProviders(), Times.AtLeastOnce);
        // FinalFallback was used as the last resort after cloud failed
        fallback.Verify(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FinalFallback_HandlesRequest_DailyCountIncremented()
    {
        const string fallbackName = "finalfallback";
        var p1 = MakeProvider("p1");
        var fallback = new Mock<IInferenceClient>();
        fallback.Setup(p => p.Name).Returns(fallbackName);
        fallback.Setup(p => p.Type).Returns(ProviderType.LocalGguf);
        var request = MakeRequest();
        p1.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(MakeRateLimitException());
        fallback.Setup(p => p.CompleteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult(fallbackName));

        var allConfigs = new List<ProviderConfig>
        {
            new() { Name = "p1" },
            new() { Name = fallbackName, DailyRequestLimit = 0, RequestsPerMinute = 0 }
        };
        var tracker = new RateLimitTracker(allConfigs, NullLogger<RateLimitTracker>.Instance);
        var cloudProviders = new List<IInferenceClient> { p1.Object }.AsReadOnly();
        var strategy = new ChainOfResponsibilityStrategy(cloudProviders, tracker);
        var logDir = Path.Combine(Path.GetTempPath(), $"inferrouter-test-{Guid.NewGuid()}");
        var orchestrator = new ProviderOrchestrator(
            cloudProviders,
            fallback.Object,
            strategy,
            tracker,
            new ErrorNormalizer(),
            new OperationLogger(logDir),
            NullLogger<ProviderOrchestrator>.Instance);

        await orchestrator.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(1, tracker.GetStats(fallbackName).DailyCount);
    }
}
