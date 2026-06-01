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

using InferRouter.Core.Domain;
using InferRouter.Core.Interfaces;
using InferRouter.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace InferRouter.IntegrationTests;

public class InferRouterWebAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((context, config) =>
        {
            config.SetBasePath(AppContext.BaseDirectory)
                  .AddJsonFile("appsettings.Test.json", optional: false)
                  .AddInMemoryCollection(new Dictionary<string, string?>
                  {
                      ["InferRouter:OperationLogPath"] = Path.Combine(
                          Path.GetTempPath(), $"inferrouter-test-{Guid.NewGuid():N}")
                  });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<OperationLogger>(_ =>
                new OperationLogger(Path.Combine(Path.GetTempPath(), $"inferrouter-test-{Guid.NewGuid():N}")));

            services.AddSingleton<IReadOnlyList<ILlmProvider>>(_ =>
            {
                var cloudProvider = new Mock<ILlmProvider>();
                cloudProvider.Setup(p => p.Name).Returns("test-provider");
                cloudProvider.Setup(p => p.Type).Returns(ProviderType.OpenAiCompatible);

                var localProvider = new Mock<ILlmProvider>();
                localProvider.Setup(p => p.Name).Returns("test-local");
                localProvider.Setup(p => p.Type).Returns(ProviderType.LocalGguf);

                return new List<ILlmProvider>
                {
                    cloudProvider.Object,
                    localProvider.Object
                }.AsReadOnly();
            });
        });
    }
}
