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

using System.Net.Http.Headers;
using System.Text.Json;
using InferRouter.Core.Config;
using InferRouter.Core.Domain;
using InferRouter.Core.Interfaces;
using InferRouter.Core.Services;

namespace InferRouter.Api.Services;

public class ModelsService(
    IReadOnlyList<IInferenceClient> providers,
    IReadOnlyList<ProviderConfig> providerConfigs,
    bool hideModels,
    IHttpClientFactory httpClientFactory,
    SecretReader secretReader)
{
    private readonly IReadOnlyList<IInferenceClient> _providers = providers;

    public async Task<ModelListResponse> GetModelsAsync(CancellationToken ct)
    {
        if (hideModels)
            return BuildStaticList();

        foreach (var config in providerConfigs)
        {
            if (string.IsNullOrEmpty(config.BaseUrl))
                continue;

            var apiKey = secretReader.ReadApiKey(config.Name);

            try
            {
                var httpClient = httpClientFactory.CreateClient();

                var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{config.BaseUrl.TrimEnd('/')}/v1/models");

                if (apiKey != null)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                var response = await httpClient.SendAsync(request, linkedCts.Token);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(linkedCts.Token);
                    var result = JsonSerializer.Deserialize<ModelListResponse>(json);
                    if (result != null)
                        return result;
                }
            }
            catch (Exception)
            {
                // treat failed provider as unavailable; try next
            }
        }

        return BuildStaticList();
    }

    private ModelListResponse BuildStaticList()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var models = new List<ModelInfo>();

        foreach (var config in providerConfigs)
        {
            if (string.IsNullOrEmpty(config.Model))
                continue;
            if (!seen.Add(config.Model))
                continue;

            models.Add(new ModelInfo(
                Id: config.Model,
                Object: "model",
                Created: 0,
                OwnedBy: config.Name));
        }

        return new ModelListResponse(Object: "list", Data: models);
    }
}
