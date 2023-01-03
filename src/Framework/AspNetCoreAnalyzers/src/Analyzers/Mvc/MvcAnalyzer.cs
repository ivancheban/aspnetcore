// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using Microsoft.AspNetCore.Analyzers.Infrastructure.RoutePattern;
using Microsoft.AspNetCore.Analyzers.Infrastructure;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Infrastructure;
using Microsoft.AspNetCore.App.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Linq;

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
                    actionRoutes.TryAdd(new ActionRoute(methodSymbol, routeUsage, httpMethodsBuilder.ToImmutable()), 0);
                }
            }
        }

        if (!actionRoutes.IsEmpty)
        {
            var groupedByParent = actionRoutes
                .Select(kvp => kvp.Key)
                .GroupBy(ar => new ActionRouteGroupKey(ar.ActionSymbol, ar.RouteUsageModel.RoutePattern, ar.HttpMethods));

            foreach (var ambigiousGroup in groupedByParent.Where(g => g.Count() >= 2))
            {
                foreach (var ambigiousActionRoute in ambigiousGroup)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.AmbiguousActionRoute,
                        ambigiousActionRoute.RouteUsageModel.UsageContext.RouteToken.GetLocation(),
                        ambigiousActionRoute.RouteUsageModel.RoutePattern.Root.ToString()));
                }
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

    private readonly struct ActionRouteGroupKey : IEquatable<ActionRouteGroupKey>
    {
        public IMethodSymbol ActionSymbol { get; }
        public RoutePatternTree RoutePattern { get; }
        public ImmutableArray<string> HttpMethods { get; }

        public ActionRouteGroupKey(IMethodSymbol actionSymbol, RoutePatternTree routePattern, ImmutableArray<string> httpMethods)
        {
            Debug.Assert(!httpMethods.IsDefault);

            ActionSymbol = actionSymbol;
            RoutePattern = routePattern;
            HttpMethods = httpMethods;
        }

        public override bool Equals(object obj)
        {
            if (obj is ActionRouteGroupKey key)
            {
                return Equals(key);
            }
            return false;
        }

        public bool Equals(ActionRouteGroupKey other)
        {
            return
                AmbiguousRoutePatternComparer.Instance.Equals(RoutePattern, other.RoutePattern) &&
                HasMatchingHttpMethods(HttpMethods, other.HttpMethods);
        }

        private static bool HasMatchingHttpMethods(ImmutableArray<string> httpMethods1, ImmutableArray<string> httpMethods2)
        {
            if (httpMethods1.IsEmpty || httpMethods2.IsEmpty)
            {
                return true;
            }

            foreach (var item1 in httpMethods1)
            {
                foreach (var item2 in httpMethods2)
                {
                    if (item2 == item1)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public override int GetHashCode()
        {
            return HttpMethods.GetHashCode() ^ AmbiguousRoutePatternComparer.Instance.GetHashCode(RoutePattern);
        }
    }
}
