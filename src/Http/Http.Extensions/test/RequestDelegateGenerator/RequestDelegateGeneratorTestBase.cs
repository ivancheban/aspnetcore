// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.AspNetCore.Http.SourceGeneration;
using Microsoft.AspNetCore.Http.SourceGeneration.StaticRouteHandlerModel;
using Microsoft.AspNetCore.Routing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.DependencyModel.Resolution;

namespace Microsoft.AspNetCore.Http.SourceGeneration.Tests;

public class RequestDelegateGeneratorTestBase
{
    internal static (ImmutableArray<GeneratorRunResult>, Compilation) RunGenerator(string sources)
    {
        var compilation = CreateCompilation(sources);
        var generator = new RequestDelegateGenerator().AsSourceGenerator();

        // Enable the source generator in tests
        var optionsProvider = new TestAnalyzerConfigOptionsProvider();
        optionsProvider.TestGlobalOptions["build_property.EnableRequestDelegateGenerator"] = "true";
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generators: new[]
            {
                generator
            },
            optionsProvider: optionsProvider,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        // Run the source generator
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updatedCompilation,
            out var _);
        var diagnostics = updatedCompilation.GetDiagnostics();
        Assert.Empty(diagnostics.Where(d => d.Severity > DiagnosticSeverity.Warning));
        var runResult = driver.GetRunResult();

        return (runResult.Results, updatedCompilation);
    }

    internal static StaticRouteHandlerModel.Endpoint GetStaticEndpoint(ImmutableArray<GeneratorRunResult> results, string stepName)
    {
        var StaticEndpointStep = results[0].TrackedSteps[stepName].Single();
        var StaticEndpointOutput = StaticEndpointStep.Outputs.Single();
        var (StaticEndpoint, _) = StaticEndpointOutput;
        var endpoint = Assert.IsType<StaticRouteHandlerModel.Endpoint>(StaticEndpoint);
        return endpoint;
    }

    internal static Endpoint GetEndpointFromCompilation(Compilation compilation)
    {
        var assemblyName = compilation.AssemblyName!;
        var symbolsName = Path.ChangeExtension(assemblyName, "pdb");

        var output = new MemoryStream();
        var pdb = new MemoryStream();

        var emitOptions = new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb,
            pdbFilePath: symbolsName);

        var embeddedTexts = new List<EmbeddedText>();

        // Make sure we embed the sources in pdb for easy debugging
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var text = syntaxTree.GetText();
            var encoding = text.Encoding ?? Encoding.UTF8;
            var buffer = encoding.GetBytes(text.ToString());
            var sourceText = SourceText.From(buffer, buffer.Length, encoding, canBeEmbedded: true);

            var syntaxRootNode = (CSharpSyntaxNode)syntaxTree.GetRoot();
            var newSyntaxTree = CSharpSyntaxTree.Create(syntaxRootNode, options: null, encoding: encoding, path: syntaxTree.FilePath);

            compilation = compilation.ReplaceSyntaxTree(syntaxTree, newSyntaxTree);

            embeddedTexts.Add(EmbeddedText.FromSource(syntaxTree.FilePath, sourceText));
        }

        var result = compilation.Emit(output, pdb, options: emitOptions, embeddedTexts: embeddedTexts);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity > DiagnosticSeverity.Warning));
        Assert.True(result.Success);

        output.Position = 0;
        pdb.Position = 0;

        var assembly = AssemblyLoadContext.Default.LoadFromStream(output, pdb);
        var handler = assembly?.GetType("TestMapActions")
            ?.GetMethod("MapTestEndpoints", BindingFlags.Public | BindingFlags.Static)
            ?.CreateDelegate<Func<IEndpointRouteBuilder, IEndpointRouteBuilder>>();
        var sourceKeyType = assembly.GetType("Microsoft.AspNetCore.Builder.SourceKey");

        Assert.NotNull(handler);

        var builder = new DefaultEndpointRouteBuilder(new ApplicationBuilder(new EmptyServiceProvider()));
        _ = handler(builder);

        var dataSource = Assert.Single(builder.DataSources);
        // Trigger Endpoint build by calling getter.
        var endpoint = Assert.Single(dataSource.Endpoints);

        var sourceKeyMetadata = endpoint.Metadata.Single(metadata => metadata.GetType() == sourceKeyType);
        Assert.NotNull(sourceKeyMetadata);

        return endpoint;
    }

    private static Compilation CreateCompilation(string sources)
    {
        var source = $$"""
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

public static class TestMapActions
{
    public static IEndpointRouteBuilder MapTestEndpoints(this IEndpointRouteBuilder app)
    {
        {{sources}}
        return app;
    }
}
""";

        var syntaxTrees = new[]
        {
            CSharpSyntaxTree.ParseText(source, path: $"TestMapActions.cs")
        };

        // Add in required metadata references
        var resolver = new AppLocalResolver();
        var references = new List<PortableExecutableReference>();
        var dependencyContext = DependencyContext.Load(typeof(RequestDelegateGeneratorTestBase).Assembly);

        Assert.NotNull(dependencyContext);

        foreach (var defaultCompileLibrary in dependencyContext.CompileLibraries)
        {
            foreach (var resolveReferencePath in defaultCompileLibrary.ResolveReferencePaths(resolver))
            {
                // Skip the source generator itself
                if (resolveReferencePath.Equals(typeof(RequestDelegateGenerator).Assembly.Location, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                references.Add(MetadataReference.CreateFromFile(resolveReferencePath));
            }
        }

        // Create a Roslyn compilation for the syntax tree.
        var compilation = CSharpCompilation.Create(assemblyName: Guid.NewGuid().ToString(),
            syntaxTrees: syntaxTrees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation;
    }

    private sealed class AppLocalResolver : ICompilationAssemblyResolver
    {
        public bool TryResolveAssemblyPaths(CompilationLibrary library, List<string> assemblies)
        {
            foreach (var assembly in library.Assemblies)
            {
                var dll = Path.Combine(Directory.GetCurrentDirectory(), "refs", Path.GetFileName(assembly));
                if (File.Exists(dll))
                {
                    assemblies ??= new();
                    assemblies.Add(dll);
                    return true;
                }

                dll = Path.Combine(Directory.GetCurrentDirectory(), Path.GetFileName(assembly));
                if (File.Exists(dll))
                {
                    assemblies ??= new();
                    assemblies.Add(dll);
                    return true;
                }
            }

            return false;
        }
    }

    private class EmptyServiceProvider : IServiceScope, IServiceProvider, IServiceScopeFactory
    {
        public IServiceProvider ServiceProvider => this;

        public RouteHandlerOptions RouteHandlerOptions { get; set; } = new RouteHandlerOptions();

        public IServiceScope CreateScope()
        {
            return this;
        }

        public void Dispose() { }

        public object GetService(Type serviceType)
        {
            return null;
        }
    }

    private class DefaultEndpointRouteBuilder : IEndpointRouteBuilder
    {
        public DefaultEndpointRouteBuilder(IApplicationBuilder applicationBuilder)
        {
            ApplicationBuilder = applicationBuilder ?? throw new ArgumentNullException(nameof(applicationBuilder));
            DataSources = new List<EndpointDataSource>();
        }

        public IApplicationBuilder ApplicationBuilder { get; }

        public IApplicationBuilder CreateApplicationBuilder() => ApplicationBuilder.New();

        public ICollection<EndpointDataSource> DataSources { get; }

        public IServiceProvider ServiceProvider => ApplicationBuilder.ApplicationServices;
    }

    private class TestAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions => TestGlobalOptions;

        public TestAnalyzerConfigOptions TestGlobalOptions { get; } = new TestAnalyzerConfigOptions();

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => throw new NotImplementedException();

        public Dictionary<string, TestAnalyzerConfigOptions> AdditionalTextOptions { get; } = new();

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile)
        {
            return AdditionalTextOptions.TryGetValue(textFile.Path, out var options) ? options : new TestAnalyzerConfigOptions();
        }

        public TestAnalyzerConfigOptionsProvider Clone()
        {
            var provider = new TestAnalyzerConfigOptionsProvider();
            foreach (var option in this.TestGlobalOptions.Options)
            {
                provider.TestGlobalOptions[option.Key] = option.Value;
            }
            foreach (var option in this.AdditionalTextOptions)
            {
                var newOptions = new TestAnalyzerConfigOptions();
                foreach (var subOption in option.Value.Options)
                {
                    newOptions[subOption.Key] = subOption.Value;
                }
                provider.AdditionalTextOptions[option.Key] = newOptions;

            }
            return provider;
        }
    }

    private class TestAnalyzerConfigOptions : AnalyzerConfigOptions
    {
        public Dictionary<string, string> Options { get; } = new();

        public string this[string name]
        {
            get => Options[name];
            set => Options[name] = value;
        }

        public override bool TryGetValue(string key, out string value)
            => Options.TryGetValue(key, out value);
    }
}
