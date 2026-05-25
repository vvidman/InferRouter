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

using System.Text.Json;
using InferRouter.Core.Domain;

namespace InferRouter.Core.Services;

public class OperationLogger(string logFilePath)
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public void LogStarted(InferRequest request) =>
        AppendLine(new
        {
            Ts = DateTimeOffset.UtcNow,
            RequestId = request.RequestId,
            Event = "infer_started"
        });

    public void LogCompleted(InferResult result) =>
        AppendLine(new
        {
            Ts = DateTimeOffset.UtcNow,
            RequestId = result.RequestId,
            Event = "infer_completed",
            Provider = result.ProviderName,
            Model = result.Model,
            PromptTokens = result.PromptTokens,
            CompletionTokens = result.CompletionTokens,
            LatencyMs = result.LatencyMs,
            Fallback = result.WasFallback,
            Status = "ok"
        });

    public void LogFallback(string fromProvider, string toProvider,
                            InternalErrorCategory reason, string requestId) =>
        AppendLine(new
        {
            Ts = DateTimeOffset.UtcNow,
            RequestId = requestId,
            Event = "infer_fallback",
            FromProvider = fromProvider,
            ToProvider = toProvider,
            Reason = ToSnakeCaseString(reason)
        });

    public void LogFailed(string requestId, string reason) =>
        AppendLine(new
        {
            Ts = DateTimeOffset.UtcNow,
            RequestId = requestId,
            Event = "infer_failed",
            Reason = reason
        });

    public void LogRateLimitHit(string providerName, string requestId) =>
        AppendLine(new
        {
            Ts = DateTimeOffset.UtcNow,
            RequestId = requestId,
            Event = "rate_limit_hit",
            Provider = providerName
        });

    private void AppendLine<T>(T entry)
    {
        var dir = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var line = JsonSerializer.Serialize(entry, _jsonOptions);
        File.AppendAllText(logFilePath, line + "\n");
    }

    private static string ToSnakeCaseString(InternalErrorCategory category) => category switch
    {
        InternalErrorCategory.RateLimit => "rate_limit",
        InternalErrorCategory.ModelUnavailable => "model_unavailable",
        InternalErrorCategory.ServerError => "server_error",
        InternalErrorCategory.AuthError => "auth_error",
        InternalErrorCategory.UnknownError => "unknown_error",
        _ => category.ToString().ToLowerInvariant()
    };
}
