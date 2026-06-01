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

public class LlamaSharpProviderTests
{
    private static LlamaSharpProvider MakeProvider(string modelPath = "/tmp/nonexistent-model-12345.gguf") =>
        new(new ProviderConfig { Name = "local", Type = ProviderType.LocalGguf, ModelPath = modelPath });

    private static InferRequest MakeRequest() =>
        new("req-001", [new ChatMessage("user", "hello")], "local-model", null, null);

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

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
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
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
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
}
