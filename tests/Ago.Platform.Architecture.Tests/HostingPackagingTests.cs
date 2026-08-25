using System.Xml.Linq;

namespace Ago.Platform.Architecture.Tests;

/// <summary>
/// `7-09`/adr/0046. Every product host, in every product repository, must reference
/// <c>Ago.Platform.Hosting</c> in order to exist at all - it holds <c>IProductModule</c>, the
/// platform/product seam. So its <c>PackageReference</c> list is not an ordinary dependency list: it
/// is a bill every host of every shape pays, including hosts that call nothing but
/// <c>AddPlatformKernel()</c>.
///
/// The defect this guards against is already measured, not hypothetical: while
/// <c>Ago.Platform.Hosting</c> also carried <c>AddPlatformObservability</c>, a plain
/// <c>Microsoft.NET.Sdk.Worker</c> generic host resolved eight OpenTelemetry packages - including
/// <c>OpenTelemetry.Exporter.Prometheus.AspNetCore</c>, which has never shipped a stable release -
/// for telemetry it could not use, because a Prometheus scrape endpoint needs an
/// <c>IEndpointRouteBuilder</c> a generic host does not have.
///
/// This is asserted against the **project files**, not the compiled assemblies, deliberately. The
/// harm is in the restore graph a consumer resolves, which comes from the packed <c>.nuspec</c>'s
/// dependency list - and that is written from <c>PackageReference</c>, whether or not any type from
/// the package is actually referenced in IL. An assembly-reference check would pass on a project
/// carrying a <c>PackageReference</c> it never calls into, which is precisely the case that hurts a
/// consumer most: paid for, unused, invisible.
/// </summary>
public class HostingPackagingTests
{
    /// <summary>
    /// The complete allowed dependency set for the one package every product host must reference.
    /// Adding to this list is a deliberate act with a cost paid by every host in every product
    /// repository - if a new entry is genuinely right, change this test in the same commit and say
    /// why in <c>CHANGELOG.md</c>.
    /// </summary>
    private static readonly string[] AllowedHostingPackageReferences =
    [
        "Microsoft.Extensions.Configuration.Abstractions",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
    ];

    [Fact]
    public void Hosting_ReferencesOnlyTheDependencyLightSetEveryProductHostMustPayFor()
    {
        var actual = PackageReferencesOf("Ago.Platform.Hosting");

        Assert.Equal(AllowedHostingPackageReferences.Order().ToArray(), actual);
    }

    [Fact]
    public void Hosting_ReferencesNoOpenTelemetryPackage()
    {
        var offenders = PackageReferencesOf("Ago.Platform.Hosting")
            .Where(IsOpenTelemetry)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Ago.Platform.Hosting must carry no OpenTelemetry dependency - telemetry wiring lives in "
            + "Ago.Platform.Observability, which only hosts that can actually serve a scrape endpoint "
            + $"reference (adr/0046). Offenders: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Observability_IsTheOnlyPackableProjectCarryingOpenTelemetry()
    {
        var offenders = SourceProjectDirectories()
            .Where(directory => directory.Name != "Ago.Platform.Observability")
            .Where(directory => PackageReferencesOf(directory.Name).Any(IsOpenTelemetry))
            .Select(directory => directory.Name)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Only Ago.Platform.Observability may take a dependency on the OpenTelemetry SDK. Every other "
            + "platform project instruments itself through System.Diagnostics' ActivitySource/Meter from "
            + "the BCL, which the wildcard subscription picks up with no SDK reference at all. "
            + $"Offenders: {string.Join(", ", offenders)}");
    }

    private static bool IsOpenTelemetry(string packageId) =>
        packageId.StartsWith("OpenTelemetry", StringComparison.Ordinal);

    private static string[] PackageReferencesOf(string projectName)
    {
        var path = Path.Combine(SourceRoot().FullName, projectName, $"{projectName}.csproj");
        var project = XDocument.Load(path);

        return project.Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<DirectoryInfo> SourceProjectDirectories() =>
        SourceRoot().EnumerateDirectories("Ago.Platform.*");

    // Walked from the test binary rather than baked in through [CallerFilePath]: the repository root
    // is wherever the checkout happens to be, and a compile-time path would be wrong the moment build
    // and test run from different directories.
    private static DirectoryInfo SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ago.Platform.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null,
            $"Could not find Ago.Platform.slnx walking up from {AppContext.BaseDirectory}.");

        return new DirectoryInfo(Path.Combine(directory!.FullName, "src"));
    }
}
