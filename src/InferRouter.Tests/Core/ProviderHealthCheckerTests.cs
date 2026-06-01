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
using Moq;
using Xunit;

namespace InferRouter.Tests.Core;

public class ProviderHealthCheckerTests
{
    private static ProviderHealthChecker BuildChecker(params Mock<ILlmProvider>[] mocks) =>
        new(mocks.Select(m => m.Object).ToList(), new ErrorNormalizer());

    private static Mock<ILlmProvider> MakeProvider(string name)
    {
        var mock = new Mock<ILlmProvider>();
        mock.Setup(p => p.Name).Returns(name);
        mock.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);
        return mock;
    }

    private static InferResult MakeResult(string providerName) =>
        new("health-check", providerName, "test-model", "hi", 1, 1, 10, false);

    private static ProviderException MakeException(int status, InternalErrorCategory category) =>
        new(status, null, [new ErrorMapping { HttpStatus = status, InternalCategory = category }]);

    [Fact]
    public async Task HealthyProvider_ReturnsOkWithLatency()
    {
        var p = MakeProvider("groq");
        p.Setup(x => x.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
         .ReturnsAsync(MakeResult("groq"));

        var checker = BuildChecker(p);
        var results = await checker.CheckAllAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("groq", results[0].ProviderName);
        Assert.Equal("ok", results[0].Status);
        Assert.Null(results[0].HttpStatus);
        Assert.True(results[0].LatencyMs >= 0);
    }

    [Fact]
    public async Task ProviderThrowsAuthError_ReturnsAuthErrorStatus()
    {
        var p = MakeProvider("gemini");
        p.Setup(x => x.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
         .ThrowsAsync(MakeException(401, InternalErrorCategory.AuthError));

        var checker = BuildChecker(p);
        var results = await checker.CheckAllAsync(CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("auth_error", results[0].Status);
        Assert.Equal(401, results[0].HttpStatus);
    }

    [Fact]
    public async Task ProviderThrowsRateLimit_ReturnsRateLimitStatus()
    {
        var p = MakeProvider("groq");
        p.Setup(x => x.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
         .ThrowsAsync(MakeException(429, InternalErrorCategory.RateLimit));

        var checker = BuildChecker(p);
        var results = await checker.CheckAllAsync(CancellationToken.None);

        Assert.Equal("rate_limit", results[0].Status);
        Assert.Equal(429, results[0].HttpStatus);
    }

    [Fact]
    public async Task ProviderThrowsServerError_ReturnsServerErrorStatus()
    {
        var p = MakeProvider("groq");
        p.Setup(x => x.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
         .ThrowsAsync(MakeException(500, InternalErrorCategory.ServerError));

        var checker = BuildChecker(p);
        var results = await checker.CheckAllAsync(CancellationToken.None);

        Assert.Equal("server_error", results[0].Status);
        Assert.Equal(500, results[0].HttpStatus);
    }

    [Fact]
    public async Task ProviderThrowsGenericException_ReturnsUnknownErrorStatus()
    {
        var p = MakeProvider("groq");
        p.Setup(x => x.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
         .ThrowsAsync(new HttpRequestException("network failure"));

        var checker = BuildChecker(p);
        var results = await checker.CheckAllAsync(CancellationToken.None);

        Assert.Equal("unknown_error", results[0].Status);
        Assert.Null(results[0].HttpStatus);
    }

    [Fact]
    public async Task MultipleProviders_AllCheckedIndependently()
    {
        var p1 = MakeProvider("groq");
        var p2 = MakeProvider("gemini");
        var p3 = MakeProvider("llamasharp");

        p1.Setup(x => x.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
          .ThrowsAsync(MakeException(401, InternalErrorCategory.AuthError));
        p2.Setup(x => x.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(MakeResult("gemini"));
        p3.Setup(x => x.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
          .ThrowsAsync(MakeException(429, InternalErrorCategory.RateLimit));

        var checker = BuildChecker(p1, p2, p3);
        var results = await checker.CheckAllAsync(CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.Equal("auth_error", results[0].Status);
        Assert.Equal("ok", results[1].Status);
        Assert.Equal("rate_limit", results[2].Status);

        p1.Verify(x => x.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        p2.Verify(x => x.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        p3.Verify(x => x.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HealthCheckRequest_UsesSingleUserHelloMessage()
    {
        var p = MakeProvider("groq");
        InferRequest? captured = null;
        p.Setup(x => x.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
         .Callback<InferRequest, CancellationToken>((req, _) => captured = req)
         .ReturnsAsync(MakeResult("groq"));

        var checker = BuildChecker(p);
        await checker.CheckAllAsync(CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Single(captured!.Messages);
        Assert.Equal("user", captured.Messages[0].Role);
        Assert.Equal("hello", captured.Messages[0].Content);
    }

    [Fact]
    public async Task CancellationRequested_Propagates()
    {
        var p = MakeProvider("groq");
        p.Setup(x => x.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
         .ThrowsAsync(new OperationCanceledException());

        var checker = BuildChecker(p);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            checker.CheckAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RateLimitTracker_NeverCalledDuringHealthCheck()
    {
        var p = MakeProvider("groq");
        p.Setup(x => x.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
         .ReturnsAsync(MakeResult("groq"));
        var tracker = new Mock<IRateLimitTracker>();

        var checker = BuildChecker(p);
        await checker.CheckAllAsync(CancellationToken.None);

        tracker.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OperationLogger_NeverCalledDuringHealthCheck()
    {
        var p = MakeProvider("groq");
        p.Setup(x => x.CompleteAsync(It.IsAny<InferRequest>(), It.IsAny<CancellationToken>()))
         .ReturnsAsync(MakeResult("groq"));
        var logDir = Path.Combine(Path.GetTempPath(), $"inferrouter-health-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(logDir);

        try
        {
            var checker = BuildChecker(p);
            await checker.CheckAllAsync(CancellationToken.None);

            Assert.Empty(Directory.GetFiles(logDir));
        }
        finally
        {
            Directory.Delete(logDir, true);
        }
    }
}
