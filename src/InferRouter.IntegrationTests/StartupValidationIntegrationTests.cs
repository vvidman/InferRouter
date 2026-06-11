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

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace InferRouter.IntegrationTests;

public class StartupValidationIntegrationTests
{
    [Fact]
    public void FinalFallback_OpenAiCompatible_UnreachableBaseUrl_StartupFails()
    {
        // Arrange: use Development environment (not Test) so validation D runs
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.Sources.Clear();
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Logging:LogLevel:Default"] = "Warning",
                        ["InferRouter:Providers:0:Name"] = "test-cloud",
                        ["InferRouter:Providers:0:Type"] = "OpenAiCompatible",
                        ["InferRouter:Providers:0:BaseUrl"] = "https://example.com/v1",
                        ["InferRouter:FinalFallback:Name"] = "local-compat",
                        ["InferRouter:FinalFallback:Type"] = "OpenAiCompatible",
                        // Port 1 is reserved and always unreachable
                        ["InferRouter:FinalFallback:BaseUrl"] = "http://localhost:1"
                    });
                });
            });

        // Act & Assert: startup must fail because the FinalFallback is unreachable
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }
}
