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

using System.Diagnostics;
using System.Text;
using InferRouter.Core.Config;
using InferRouter.Core.Domain;
using InferRouter.Core.Interfaces;
using LLama;
using LLama.Common;

namespace InferRouter.Providers;

public class LlamaSharpProvider : ILlmProvider, IDisposable
{
    private readonly ProviderConfig _config;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private bool _permanentlyFailed;
    private Exception? _permanentFailureCause;
    private bool _disposed;

    public string Name => _config.Name;
    public ProviderType Type => ProviderType.LocalGguf;

    public LlamaSharpProvider(ProviderConfig config)
    {
        _config = config;
    }

    public async Task<InferResult> CompleteAsync(InferRequest request, CancellationToken ct)
    {
        if (_permanentlyFailed)
            throw new ProviderException(500, "model_permanently_failed", [],
                $"LlamaSharp provider is permanently unavailable: {_permanentFailureCause?.Message}");

        await _semaphore.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync();

            var promptBuilder = new StringBuilder();
            foreach (var msg in request.Messages)
                promptBuilder.AppendLine($"{msg.Role}: {msg.Content}");
            promptBuilder.Append("assistant:");

            var sw = Stopwatch.StartNew();

            try
            {
                var executor = new InteractiveExecutor(_context!);
                var inferenceParams = new InferenceParams
                {
                    MaxTokens = request.MaxTokens ?? 256,
                    AntiPrompts = ["\nuser:", "\nUser:"]
                };

                var sb = new StringBuilder();
                await foreach (var token in executor.InferAsync(promptBuilder.ToString(), inferenceParams, ct))
                    sb.Append(token);

                sw.Stop();

                return new InferResult(
                    RequestId: request.RequestId,
                    ProviderName: _config.Name,
                    Model: Path.GetFileName(_config.ModelPath) ?? "",
                    Content: sb.ToString().Trim(),
                    PromptTokens: 0,
                    CompletionTokens: 0,
                    LatencyMs: sw.ElapsedMilliseconds,
                    WasFallback: false
                );
            }
            catch (ProviderException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new ProviderException(500, "inference_failed", [],
                    $"LlamaSharp inference failed: {ex.Message}", ex);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task EnsureLoadedAsync()
    {
        if (_context is not null) return;
        if (_permanentlyFailed)
            throw new ProviderException(500, "model_permanently_failed", []);

        try
        {
            var modelParams = new ModelParams(_config.ModelPath!);
            _weights = await Task.Run(() => LLamaWeights.LoadFromFile(modelParams));
            _context = _weights.CreateContext(modelParams);
        }
        catch (ProviderException) { throw; }
        catch (Exception ex)
        {
            _permanentlyFailed = true;
            _permanentFailureCause = ex;
            _weights?.Dispose();
            _weights = null;
            _context = null;
            throw new ProviderException(500, "model_load_failed", [],
                $"Failed to load GGUF model from '{_config.ModelPath}': {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _context?.Dispose();
        _weights?.Dispose();
        _semaphore.Dispose();
    }
}
