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
using InferRouter.Providers;
using Xunit;

namespace InferRouter.Tests.Providers;

file sealed class TestableLlamaSharpProvider(ProviderConfig config, InferResult result)
    : LlamaSharpProvider(config)
{
    public override Task<InferResult> CompleteAsync(InferRequest request, CancellationToken ct)
        => Task.FromResult(result);
}

public class LlamaSharpProviderTests
{
    private static LlamaSharpProvider MakeProvider(string modelPath = "/tmp/nonexistent-model-12345.gguf") =>
        new(new ProviderConfig { Name = "local", Type = ProviderType.LocalGguf, ModelPath = modelPath });

    private static InferRequest MakeRequest() =>
        new("req-001", [new ChatMessage("user", "hello")], "local-model", null, null, null, null, null);

    [Fact]
    public async Task CompleteAsync_ModelFileNotFound_ThrowsProviderException_WithModelLoadFailedCode()
    {
        using var provider = MakeProvider();
        var request = MakeRequest();

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.CompleteAsync(request, CancellationToken.None));

        Assert.Equal("model_load_failed", ex.RawErrorCode);
        Assert.Equal(500, ex.HttpStatus);
    }

    [Fact]
    public async Task CompleteAsync_AfterLoadFailure_ImmediatelyThrowsPermanentlyFailed()
    {
        using var provider = MakeProvider();
        var request = MakeRequest();

        // First call — load failure sets permanent flag
        await Assert.ThrowsAsync<ProviderException>(() =>
            provider.CompleteAsync(request, CancellationToken.None));

        // Second call — must immediately return permanently_failed without retrying
        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.CompleteAsync(request, CancellationToken.None));

        Assert.Equal("model_permanently_failed", ex.RawErrorCode);
        Assert.Equal(500, ex.HttpStatus);
    }

    [Fact]
    public async Task CompleteAsync_AfterLoadFailure_MessageContainsOriginalCause()
    {
        using var provider = MakeProvider();
        var request = MakeRequest();

        // Trigger load failure
        await Assert.ThrowsAsync<ProviderException>(() =>
            provider.CompleteAsync(request, CancellationToken.None));

        // Permanent failure message should describe the root cause
        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.CompleteAsync(request, CancellationToken.None));

        Assert.Contains("permanently unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteAsync_WhenCancelledBeforeLoad_PropagatesOperationCanceledException()
    {
        using var provider = MakeProvider();
        var request = MakeRequest();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            provider.CompleteAsync(request, cts.Token));
    }

    [Fact]
    public async Task CompleteAsync_WhenCancelled_DoesNotSetPermanentFailureFlag()
    {
        using var provider = MakeProvider();
        var request = MakeRequest();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Cancellation should not set the permanent failure flag
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            provider.CompleteAsync(request, cts.Token));

        // The permanent flag must not be set — a subsequent call with a valid
        // cancellation token should still attempt loading (and fail with load error,
        // not "permanently unavailable")
        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.CompleteAsync(request, CancellationToken.None));

        Assert.Equal("model_load_failed", ex.RawErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_InvalidGgufContent_ThrowsProviderException_WithModelLoadFailedCode()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Write garbage content — not a valid GGUF file
            await File.WriteAllBytesAsync(tempFile, [0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE]);

            using var provider = MakeProvider(tempFile);
            var request = MakeRequest();

            var ex = await Assert.ThrowsAsync<ProviderException>(() =>
                provider.CompleteAsync(request, CancellationToken.None));

            Assert.Equal("model_load_failed", ex.RawErrorCode);
            Assert.Equal(500, ex.HttpStatus);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task CompleteStreamingAsync_YieldsContentChunksAndFinalChunkWithTokenCounts()
    {
        const string content = "The quick brown fox jumps over the lazy dog";
        var request = MakeRequest();
        var inferResult = new InferResult("req-001", "local", "model.gguf", content, 10, 20, 0, false);
        using var provider = new TestableLlamaSharpProvider(
            new ProviderConfig { Name = "local", Type = ProviderType.LocalGguf, ModelPath = "/tmp/nonexistent.gguf" },
            inferResult);

        var chunks = new List<StreamChunk>();
        await foreach (var chunk in provider.CompleteStreamingAsync(request, CancellationToken.None))
            chunks.Add(chunk);

        Assert.True(chunks.Count > 1, "Expected at least one content chunk plus the final chunk");

        var lastChunk = chunks.Last();
        Assert.True(lastChunk.IsLast);
        Assert.Equal("", lastChunk.Delta);
        Assert.Equal(10, lastChunk.PromptTokens);
        Assert.Equal(20, lastChunk.CompletionTokens);

        var contentChunks = chunks.SkipLast(1).ToList();
        Assert.True(contentChunks.Count > 0);
        Assert.All(contentChunks, c => Assert.False(c.IsLast));

        var concatenated = string.Concat(contentChunks.Select(c => c.Delta));
        Assert.Equal(content, concatenated);
    }

    [Fact]
    public async Task CompleteStreamingAsync_EmptyContent_YieldsOnlyFinalChunk()
    {
        var request = MakeRequest();
        var inferResult = new InferResult("req-001", "local", "model.gguf", "", 5, 0, 0, false);
        using var provider = new TestableLlamaSharpProvider(
            new ProviderConfig { Name = "local", Type = ProviderType.LocalGguf, ModelPath = "/tmp/nonexistent.gguf" },
            inferResult);

        var chunks = new List<StreamChunk>();
        await foreach (var chunk in provider.CompleteStreamingAsync(request, CancellationToken.None))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.True(chunks[0].IsLast);
        Assert.Equal("", chunks[0].Delta);
        Assert.Equal(5, chunks[0].PromptTokens);
        Assert.Equal(0, chunks[0].CompletionTokens);
    }
}
