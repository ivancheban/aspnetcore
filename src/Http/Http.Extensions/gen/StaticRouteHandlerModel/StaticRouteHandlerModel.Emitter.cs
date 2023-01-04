// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.AspNetCore.Http.SourceGeneration.StaticRouteHandlerModel;

internal static class StaticRouteHandlerModelEmitter
{
    public static string EmitHandlerDelegateType(Endpoint endpoint)
    {
        return $"System.Func<{endpoint.Response.ResponseType}>";
    }

    public static string EmitSourceKey(Endpoint endpoint)
    {
        return $@"(""{endpoint.Location.Item1}"", {endpoint.Location.Item2})";
    }

    public static string EmitRequestHandler()
    {
        return $@"
System.Threading.Tasks.Task RequestHandler(Microsoft.AspNetCore.Http.HttpContext httpContext)
{{
        var result = handler();
        return httpContext.Response.WriteAsync(result);
}}
";
    }

    public static string EmitFilteredRequestHandler()
    {
        return $@"
async System.Threading.Tasks.Task RequestHandlerFiltered(Microsoft.AspNetCore.Http.HttpContext httpContext)
{{
    var result = await filteredInvocation(new DefaultEndpointFilterInvocationContext(httpContext));
    await ExecuteObjectResult(result, httpContext);
}}
";
    }
}
