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
    private bool _disposed;

    public string Name => _config.Name;
    public ProviderType Type => ProviderType.LocalGguf;

    public LlamaSharpProvider(ProviderConfig config)
    {
        _config = config;
    }

    public async Task<InferResult> CompleteAsync(InferRequest request, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync();

            var promptBuilder = new StringBuilder();
            foreach (var msg in request.Messages)
                promptBuilder.AppendLine($"{msg.Role}: {msg.Content}");
            promptBuilder.Append("assistant:");

            var sw = Stopwatch.StartNew();

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
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task EnsureLoadedAsync()
    {
        if (_context is not null)
            return;

        if (!File.Exists(_config.ModelPath))
            throw new InvalidOperationException(
                $"GGUF model file not found at '{_config.ModelPath}'. " +
                "Ensure the model file exists and ModelPath in configuration is correct.");

        var modelParams = new ModelParams(_config.ModelPath!);
        _weights = await Task.Run(() => LLamaWeights.LoadFromFile(modelParams));
        _context = _weights.CreateContext(modelParams);
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
