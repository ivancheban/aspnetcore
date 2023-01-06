// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Http.SourceGeneration;

namespace Microsoft.AspNetCore.Http.SourceGeneration.Tests;

public class RequestDelegateGeneratorTests : RequestDelegateGeneratorTestBase
{
    [Theory]
    [InlineData(@"app.MapGet(""/hello"", () => ""Hello world!"");", "Hello world!")]
    public async Task MapGet_NoParam_SimpleReturn(string source, string expectedBody)
    {
        var (results, compilation) = RunGenerator(source);

        var endpointModel = GetStaticEndpoint(results, "EndpointModel");
        var endpoint = GetEndpointFromCompilation(compilation);
        var requestDelegate = endpoint.RequestDelegate;

        Assert.Equal("/hello", endpointModel.Route.RoutePattern);

        var httpContext = new DefaultHttpContext();

        var outStream = new MemoryStream();
        httpContext.Response.Body = outStream;

        await requestDelegate(httpContext);

        var httpResponse = httpContext.Response;
        httpResponse.Body.Seek(0, SeekOrigin.Begin);
        var streamReader = new StreamReader(httpResponse.Body);
        var body = await streamReader.ReadToEndAsync();
        Assert.Equal(200, httpContext.Response.StatusCode);
        Assert.Equal(expectedBody, body);
    }
}
