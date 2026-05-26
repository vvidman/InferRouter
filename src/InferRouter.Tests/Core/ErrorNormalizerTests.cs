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
using InferRouter.Core.Services;
using Xunit;

namespace InferRouter.Tests.Core;

public class ErrorNormalizerTests
{
    private readonly ErrorNormalizer _sut = new();

    [Fact]
    public void Categorize_HttpStatusAndErrorCodeMatch_ReturnsMostSpecificCategory()
    {
        var mappings = new List<ErrorMapping>
        {
            new() { HttpStatus = 429, ErrorCode = "rate_limit_exceeded", InternalCategory = InternalErrorCategory.RateLimit }
        };

        var result = _sut.Categorize(429, "rate_limit_exceeded", mappings);

        Assert.Equal(InternalErrorCategory.RateLimit, result);
    }

    [Fact]
    public void Categorize_HttpStatusAndErrorCodeMatchTakesPriorityOverHttpStatusOnly_ReturnsMostSpecificCategory()
    {
        var mappings = new List<ErrorMapping>
        {
            new() { HttpStatus = 429, ErrorCode = null, InternalCategory = InternalErrorCategory.UnknownError },
            new() { HttpStatus = 429, ErrorCode = "rate_limit_exceeded", InternalCategory = InternalErrorCategory.RateLimit }
        };

        var result = _sut.Categorize(429, "rate_limit_exceeded", mappings);

        Assert.Equal(InternalErrorCategory.RateLimit, result);
    }

    [Fact]
    public void Categorize_HttpStatusOnlyMatch_ReturnsAuthError()
    {
        var mappings = new List<ErrorMapping>
        {
            new() { HttpStatus = 401, ErrorCode = null, InternalCategory = InternalErrorCategory.AuthError }
        };

        var result = _sut.Categorize(401, "unrecognized_code", mappings);

        Assert.Equal(InternalErrorCategory.AuthError, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Categorize_NullOrEmptyRawErrorCode_HttpStatusOnlyMatchResolves(string? rawErrorCode)
    {
        var mappings = new List<ErrorMapping>
        {
            new() { HttpStatus = 401, ErrorCode = null, InternalCategory = InternalErrorCategory.AuthError }
        };

        var result = _sut.Categorize(401, rawErrorCode, mappings);

        Assert.Equal(InternalErrorCategory.AuthError, result);
    }

    [Fact]
    public void Categorize_NoMatchingHttpStatus_ReturnsUnknownError()
    {
        var mappings = new List<ErrorMapping>
        {
            new() { HttpStatus = 401, ErrorCode = null, InternalCategory = InternalErrorCategory.AuthError }
        };

        var result = _sut.Categorize(500, null, mappings);

        Assert.Equal(InternalErrorCategory.UnknownError, result);
    }

    [Fact]
    public void Categorize_EmptyMappings_ReturnsUnknownError()
    {
        var result = _sut.Categorize(429, "rate_limit_exceeded", new List<ErrorMapping>());

        Assert.Equal(InternalErrorCategory.UnknownError, result);
    }

    [Fact]
    public void Categorize_ErrorCodeMatchIsCaseInsensitive_StillMatches()
    {
        var mappings = new List<ErrorMapping>
        {
            new() { HttpStatus = 429, ErrorCode = "RESOURCE_EXHAUSTED", InternalCategory = InternalErrorCategory.RateLimit }
        };

        var result = _sut.Categorize(429, "resource_exhausted", mappings);

        Assert.Equal(InternalErrorCategory.RateLimit, result);
    }

    [Fact]
    public void Categorize_HttpStatusMatchesGenericAndSpecificErrorCode_SpecificWins()
    {
        var mappings = new List<ErrorMapping>
        {
            new() { HttpStatus = 429, ErrorCode = null, InternalCategory = InternalErrorCategory.ServerError },
            new() { HttpStatus = 429, ErrorCode = "rate_limit_exceeded", InternalCategory = InternalErrorCategory.RateLimit }
        };

        var result = _sut.Categorize(429, "rate_limit_exceeded", mappings);

        Assert.Equal(InternalErrorCategory.RateLimit, result);
    }
}
