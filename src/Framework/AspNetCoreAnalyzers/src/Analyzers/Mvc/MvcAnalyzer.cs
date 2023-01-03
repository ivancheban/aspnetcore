// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Threading;
using System.Xml.Schema;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Infrastructure;
using Microsoft.AspNetCore.App.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.AspNetCore.Analyzers.Mvc;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public partial class MvcAnalyzer : DiagnosticAnalyzer
{
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
            var concurrentQueue = new ConcurrentQueue<ConcurrentDictionary<ActionRoute, byte>>();

            context.RegisterSymbolAction(context =>
            {
                if (context.Symbol is INamedTypeSymbol namedTypeSymbol &&
                    MvcDetector.IsController(namedTypeSymbol, wellKnownTypes))
                {
                    // Pool and reuse lists for each block.
                    if (!concurrentQueue.TryDequeue(out var actionRoutes))
                    {
                        actionRoutes = new ConcurrentDictionary<ActionRoute, byte>();
                    }

                    DetectAmbiguousRoutes(context, namedTypeSymbol, wellKnownTypes, routeUsageCache, actionRoutes);

                    // Return to the pool.
                    actionRoutes.Clear();
                    concurrentQueue.Enqueue(actionRoutes);
                }
            }, SymbolKind.NamedType);
        });
    }

    private static void DetectAmbiguousRoutes(SymbolAnalysisContext context, INamedTypeSymbol mvcTypeSymbol, WellKnownTypes wellKnownTypes, RouteUsageCache routeUsageCache, ConcurrentDictionary<ActionRoute, byte> actionRoutes)
    {
        foreach (var member in mvcTypeSymbol.GetMembers())
        {
            if (member is IMethodSymbol methodSymbol &&
                MvcDetector.IsAction(methodSymbol, wellKnownTypes))
            {
                var attributes = methodSymbol.GetAttributes();
                foreach (var attribute in attributes)
                {
                    if (attribute.AttributeClass is not null)
                    {
                        var isRouteAttribute = wellKnownTypes.IsType(attribute.AttributeClass, new[]
                        {
                            WellKnownType.Microsoft_AspNetCore_Mvc_RouteAttribute,
                            WellKnownType.Microsoft_AspNetCore_Mvc_HttpDeleteAttribute,
                            WellKnownType.Microsoft_AspNetCore_Mvc_HttpGetAttribute,
                            WellKnownType.Microsoft_AspNetCore_Mvc_HttpHeadAttribute,
                            WellKnownType.Microsoft_AspNetCore_Mvc_HttpOptionsAttribute,
                            WellKnownType.Microsoft_AspNetCore_Mvc_HttpPatchAttribute,
                            WellKnownType.Microsoft_AspNetCore_Mvc_HttpPostAttribute,
                            WellKnownType.Microsoft_AspNetCore_Mvc_HttpPutAttribute
                        }, out var match);

                        if (isRouteAttribute)
                        {
                            var routeUsage = GetRouteUsageModel(attribute, routeUsageCache, context.CancellationToken);

                            if (routeUsage is not null)
                            {
                                var httpMethodsBuilder = ImmutableArray.CreateBuilder<string>();

                                switch (match!.Value)
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
                                actionRoutes.TryAdd(new ActionRoute(methodSymbol, routeUsage, httpMethodsBuilder.ToImmutable()), 0);
                            }
                        }
                    }
                }
            }
        }

        if (!actionRoutes.IsEmpty)
        {
            foreach (var kvp in actionRoutes)
            {
                var actionRoute = kvp.Key;

                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.AmbiguousActionRoute,
                    actionRoute.RouteUsageModel.UsageContext.RouteToken.GetLocation(),
                    actionRoute.RouteUsageModel.RoutePattern.Root.ToString()));
            }
        }
    }

    private static RouteUsageModel? GetRouteUsageModel(AttributeData attribute, RouteUsageCache routeUsageCache, CancellationToken cancellationToken)
    {
        if (attribute.ConstructorArguments.IsEmpty || attribute.ApplicationSyntaxReference is null)
        {
            return null;
        }

        if (attribute.ApplicationSyntaxReference.GetSyntax(cancellationToken) is AttributeSyntax attributeSyntax &&
            attributeSyntax.ArgumentList is { } argumentList)
        {
            var attributeArgument = argumentList.Arguments[0];
            if (attributeArgument.Expression is LiteralExpressionSyntax literalExpression)
            {
                return routeUsageCache.Get(literalExpression.Token, cancellationToken);
            }
        }

        return null;
    }

    private record struct ActionRoute(IMethodSymbol ActionSymbol, RouteUsageModel RouteUsageModel, ImmutableArray<string> HttpMethods);
}
