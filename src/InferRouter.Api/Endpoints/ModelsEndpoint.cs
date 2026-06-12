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

using InferRouter.Api.Services;

namespace InferRouter.Api.Endpoints;

public class ModelsEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/v1/models", HandleAsync);
    }

    public static async Task<IResult> HandleAsync(
        ModelsService modelsService,
        CancellationToken ct)
    {
        var result = await modelsService.GetModelsAsync(ct);
        return Results.Ok(result);
    }
}
