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

namespace InferRouter.Core.Config;

public class ProviderConfig
{
    public string Name { get; init; } = "";
    public ProviderType Type { get; init; }
    public string? BaseUrl { get; init; }
    public string? Model { get; init; }
    public int DailyRequestLimit { get; init; }
    public int RequestsPerMinute { get; init; }
    public string ErrorCodePath { get; init; } = "error.code";
    public List<ErrorMapping> ErrorMappings { get; init; } = [];
    public string? ModelPath { get; init; }
}
