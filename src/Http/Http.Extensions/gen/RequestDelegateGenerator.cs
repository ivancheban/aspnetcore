// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.AspNetCore.Http.SourceGeneration.StaticRouteHandlerModel;

namespace Microsoft.AspNetCore.Http.SourceGeneration;

[Generator]
public class RequestDelegateGenerator : IIncrementalGenerator
{
    private static readonly string[] _knownMethods =
    {
        "MapGet",
        "MapPost",
        "MapPut",
        "MapDelete",
        "MapPatch",
        "Map",
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var isGeneratorEnabled = context.AnalyzerConfigOptionsProvider.Select((provider, _) =>
            provider.GlobalOptions.TryGetValue("build_property.EnableRequestDelegateGenerator", out var enableRequestDelegateGenerator)
            && enableRequestDelegateGenerator == "true");

        var mapActionOperations = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: (node, _) => node is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Name: IdentifierNameSyntax
                    {
                        Identifier: { ValueText: var method }
                    }
                },
                ArgumentList: { Arguments: { Count: 2 } args }
            } && _knownMethods.Contains(method),
            transform: (context, token) => context.SemanticModel.GetOperation(context.Node, token) as IInvocationOperation);

        // Filter out any map actions if the generator is not enabled
        // via config
        var conditionalMapActionOperations  = mapActionOperations.Combine(isGeneratorEnabled)
            .Where(pair => pair.Right)
            .Select((pair, _) => pair.Left);

        var endpoints = conditionalMapActionOperations
            .Select((operation, _) => StaticRouteHandlerModelParser.GetEndpointFromOperation(operation))
            .WithTrackingName("EndpointModel");

        var genericThunks = endpoints.Select((endpoint, token) =>
        {
            var code = RequestDelegateGeneratorSources.GetGenericThunks(string.Empty);
            return code;
        });

        var thunks = endpoints.Select((endpoint, _) => $@"            [{StaticRouteHandlerModelEmitter.EmitSourceKey(endpoint)}] = (
           (del, builder) =>
            {{
builder.Metadata.Add(new SourceKey{StaticRouteHandlerModelEmitter.EmitSourceKey(endpoint)});
            }},
           (del, builder) =>
            {{
                var handler = ({StaticRouteHandlerModelEmitter.EmitHandlerDelegateType(endpoint)})del;
                EndpointFilterDelegate? filteredInvocation = null;

                if (builder.FilterFactories.Count > 0)
                {{
                    filteredInvocation = BuildFilterDelegate(ic =>
                    {{
                        if (ic.HttpContext.Response.StatusCode == 400)
                        {{
                            return System.Threading.Tasks.ValueTask.FromResult<object?>(Results.Empty);
                        }}
                        return System.Threading.Tasks.ValueTask.FromResult<object?>(handler());
                    }},
                    builder,
                    handler.Method);
                }}

                {StaticRouteHandlerModelEmitter.EmitRequestHandler()}
                {StaticRouteHandlerModelEmitter.EmitFilteredRequestHandler()}

                return filteredInvocation is null ? RequestHandler : RequestHandlerFiltered;
            }}),
");

        var stronglyTypedEndpointDefinitions = endpoints.Select((endpoint, _) =>
        {
            var code = new StringBuilder();
            code.AppendLine($"internal static Microsoft.AspNetCore.Builder.RouteHandlerBuilder {endpoint.HttpMethod}");
            code.Append("(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints, string pattern, ");
            code.Append(StaticRouteHandlerModelEmitter.EmitHandlerDelegateType(endpoint));
            code.Append(@" handler, [System.Runtime.CompilerServices.CallerFilePath] string filePath = """", [System.Runtime.CompilerServices.CallerLineNumber]int lineNumber = 0)");
            code.AppendLine("{");
            code.AppendLine("return MapCore(endpoints, pattern, handler, GetVerb, filePath, lineNumber);");
            code.AppendLine("}");
            return code.ToString();
        });

        context.RegisterSourceOutput(genericThunks, (context, source) =>
        {
            context.AddSource("GeneratedRouteBuilderExtensions.GenericThunks.g.cs", source);
        });

        context.RegisterSourceOutput(thunks, (context, source) =>
        {
            var code = RequestDelegateGeneratorSources.GetThunks(source);
            context.AddSource("GeneratedRouteBuilderExtensions.Thunks.g.cs", code);
        });

        context.RegisterSourceOutput(stronglyTypedEndpointDefinitions, (context, source) =>
        {
            var code = RequestDelegateGeneratorSources.GetEndpoints(source);
            context.AddSource("GeneratedRouteBuilderExtensions.Endpoints.g.cs", code);
        });

        context.RegisterSourceOutput(endpoints, (context, source) =>
        {
            context.AddSource("GeneratedRouteBuilderExtensions.Helpers.g.cs", RequestDelegateGeneratorSources.GeneratedRouteBuilderExtensionsSource);
        });
    }
}
