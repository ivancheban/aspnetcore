// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Infrastructure;
using Microsoft.AspNetCore.App.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.AspNetCore.Analyzers.Mvc;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public partial class MvcAnalyzer : DiagnosticAnalyzer
{
    private static readonly WellKnownType[] RouteAttributeTypes = new[]
    {
        WellKnownType.Microsoft_AspNetCore_Mvc_RouteAttribute,
        WellKnownType.Microsoft_AspNetCore_Mvc_HttpDeleteAttribute,
        WellKnownType.Microsoft_AspNetCore_Mvc_HttpGetAttribute,
        WellKnownType.Microsoft_AspNetCore_Mvc_HttpHeadAttribute,
        WellKnownType.Microsoft_AspNetCore_Mvc_HttpOptionsAttribute,
        WellKnownType.Microsoft_AspNetCore_Mvc_HttpPatchAttribute,
        WellKnownType.Microsoft_AspNetCore_Mvc_HttpPostAttribute,
        WellKnownType.Microsoft_AspNetCore_Mvc_HttpPutAttribute
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        DiagnosticDescriptors.AmbiguousActionRoute
    );

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static context =>
        {
            var compilation = context.Compilation;
            var wellKnownTypes = WellKnownTypes.GetOrCreate(compilation);
            var routeUsageCache = RouteUsageCache.GetOrCreate(compilation);

            // We want ConcurrentHashSet here in case RegisterOperationAction runs in parallel.
            // Since ConcurrentHashSet doesn't exist, use ConcurrentDictionary and ignore the value.
            var concurrentQueue = new ConcurrentQueue<List<ActionRoute>>();

            context.RegisterSymbolAction(context =>
            {
                if (context.Symbol is INamedTypeSymbol namedTypeSymbol &&
                    MvcDetector.IsController(namedTypeSymbol, wellKnownTypes))
                {
                    // Pool and reuse lists for each block.
                    if (!concurrentQueue.TryDequeue(out var actionRoutes))
                    {
                        actionRoutes = new List<ActionRoute>();
                    }

                    PopulateActionRoutes(context, wellKnownTypes, routeUsageCache, namedTypeSymbol, actionRoutes);

                    DetectAmbiguousActionRoutes(context, wellKnownTypes, actionRoutes);

                    // Return to the pool.
                    actionRoutes.Clear();
                    concurrentQueue.Enqueue(actionRoutes);
                }
            }, SymbolKind.NamedType);
        });
    }

    private static void PopulateActionRoutes(SymbolAnalysisContext context, WellKnownTypes wellKnownTypes, RouteUsageCache routeUsageCache, INamedTypeSymbol namedTypeSymbol, List<ActionRoute> actionRoutes)
    {
        foreach (var member in namedTypeSymbol.GetMembers())
        {
            if (member is IMethodSymbol methodSymbol &&
                MvcDetector.IsAction(methodSymbol, wellKnownTypes))
            {
                foreach (var attribute in methodSymbol.GetAttributes())
                {
                    if (attribute.AttributeClass is null || !wellKnownTypes.IsType(attribute.AttributeClass, RouteAttributeTypes, out var match))
                    {
                        continue;
                    }

                    var routeUsage = GetRouteUsageModel(attribute, routeUsageCache, context.CancellationToken);
                    if (routeUsage is null)
                    {
                        continue;
                    }

                    var httpMethodsBuilder = ImmutableArray.CreateBuilder<string>();

                    switch (match.Value)
                    {
                        case WellKnownType.Microsoft_AspNetCore_Mvc_RouteAttribute:
                            // No HTTP method.
                            break;
                        case WellKnownType.Microsoft_AspNetCore_Mvc_HttpDeleteAttribute:
                            httpMethodsBuilder.Add("DELETE");
                            break;
                        case WellKnownType.Microsoft_AspNetCore_Mvc_HttpGetAttribute:
                            httpMethodsBuilder.Add("GET");
                            break;
                        case WellKnownType.Microsoft_AspNetCore_Mvc_HttpHeadAttribute:
                            httpMethodsBuilder.Add("HEAD");
                            break;
                        case WellKnownType.Microsoft_AspNetCore_Mvc_HttpOptionsAttribute:
                            httpMethodsBuilder.Add("OPTIONS");
                            break;
                        case WellKnownType.Microsoft_AspNetCore_Mvc_HttpPatchAttribute:
                            httpMethodsBuilder.Add("PATCH");
                            break;
                        case WellKnownType.Microsoft_AspNetCore_Mvc_HttpPostAttribute:
                            httpMethodsBuilder.Add("POST");
                            break;
                        case WellKnownType.Microsoft_AspNetCore_Mvc_HttpPutAttribute:
                            httpMethodsBuilder.Add("PUT");
                            break;
                        default:
                            throw new InvalidOperationException("Unexpected well known type:" + match);
                    }
                    actionRoutes.Add(new ActionRoute(methodSymbol, routeUsage, httpMethodsBuilder.ToImmutable()));
                }
            }
        }
    }

    private record struct ActionRoute(IMethodSymbol ActionSymbol, RouteUsageModel RouteUsageModel, ImmutableArray<string> HttpMethods);
}
