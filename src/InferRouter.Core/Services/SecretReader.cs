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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferRouter.Core.Services;

public static class SecretReader
{
    private static ILogger _logger = NullLogger.Instance;

    public static void Configure(ILoggerFactory loggerFactory) =>
        _logger = loggerFactory.CreateLogger(nameof(SecretReader));

    public static string? ReadApiKey(string providerName)
    {
        var path = $"/run/secrets/{providerName}_api_key";

        if (!File.Exists(path))
        {
            _logger.LogWarning(
                "Secret file not found for provider '{ProviderName}'. Expected path: {Path}",
                providerName, path);
            return null;
        }

        var content = File.ReadAllText(path).Trim();

        if (string.IsNullOrEmpty(content))
        {
            _logger.LogWarning(
                "Secret file for provider '{ProviderName}' is empty. Expected path: {Path}",
                providerName, path);
            return null;
        }

        return content;
    }
}
