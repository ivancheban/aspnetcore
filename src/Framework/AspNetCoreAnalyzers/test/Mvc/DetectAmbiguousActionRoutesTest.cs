// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis.Testing;
using VerifyCS = Microsoft.AspNetCore.Analyzers.Verifiers.CSharpAnalyzerVerifier<Microsoft.AspNetCore.Analyzers.Mvc.MvcAnalyzer>;

namespace Microsoft.AspNetCore.Analyzers.Mvc;

public partial class DetectAmbiguousActionRoutesTest
{
    [Fact]
    public async Task SameRoutes_DifferentAction_HasDiagnostics()
    {
        // Arrange
        var source = @"
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
public class WeatherForecastController : ControllerBase
{
    [Route({|#0:""/a""|})]
    public object Get() => new object();

    [Route({|#1:""/a""|})]
    public object Get1() => new object();
}
internal class Program
{
    static void Main(string[] args)
    {
    }
}
";

        var expectedDiagnostics = new[] {
            new DiagnosticResult(DiagnosticDescriptors.AmbiguousActionRoute).WithArguments("/a").WithLocation(0),
            new DiagnosticResult(DiagnosticDescriptors.AmbiguousActionRoute).WithArguments("/a").WithLocation(1)
        };

        // Act & Assert
        await VerifyCS.VerifyAnalyzerAsync(source, expectedDiagnostics);
    }

    [Fact]
    public async Task DifferentRoutes_DifferentAction_HasDiagnostics()
    {
        // Arrange
        var source = @"
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
public class WeatherForecastController : ControllerBase
{
    [Route(""/a"")]
    public object Get() => new object();

    [Route(""/b"")]
    public object Get1() => new object();
}
internal class Program
{
    static void Main(string[] args)
    {
    }
}
";

        // Act & Assert
        await VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task DuplicateRoutes_SameAction_HasDiagnostics()
    {
        // Arrange
        var source = @"
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
public class WeatherForecastController : ControllerBase
{
    [Route({|#0:""/""|})]
    [Route({|#1:""/""|})]
    public object Get() => new object();
}
internal class Program
{
    static void Main(string[] args)
    {
    }
}
";

        var expectedDiagnostics = new[] {
            new DiagnosticResult(DiagnosticDescriptors.AmbiguousActionRoute).WithArguments("/").WithLocation(0),
            new DiagnosticResult(DiagnosticDescriptors.AmbiguousActionRoute).WithArguments("/").WithLocation(1)
        };

        // Act & Assert
        await VerifyCS.VerifyAnalyzerAsync(source, expectedDiagnostics);
    }
}

