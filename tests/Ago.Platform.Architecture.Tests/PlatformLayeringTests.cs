using System.Reflection;
using Mono.Cecil;
using NetArchTest.Rules;

namespace Ago.Platform.Architecture.Tests;

/// <summary>
/// The platform's own half of clean-architecture.md's arch-test list. Ago.Chat.Architecture.Tests
/// (in the ago-chat repository) checks the same "platform never references a product" guarantee
/// from the consuming side, against the published package; this checks it here too, unconditionally,
/// against every build of this repository regardless of who consumes it.
/// </summary>
public class PlatformLayeringTests
{
    [Fact]
    public void Kernel_HasNoDependenciesOutsideTheBcl()
    {
        var path = typeof(Ago.Platform.Kernel.Error).Assembly.Location;
        using var kernel = AssemblyDefinition.ReadAssembly(path);

        var offenders = kernel.MainModule.AssemblyReferences
            .Select(reference => reference.Name)
            .Where(name => !IsBcl(name))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Ago.Platform.Kernel must depend on nothing but the BCL (naming-and-structure.md). Offenders: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void NoPlatformAssembly_EverReferencesAnythingNamedAgoChat()
    {
        AssertNoAgoChatDependency(typeof(Ago.Platform.Kernel.Error).Assembly, "Ago.Platform.Kernel");
        AssertNoAgoChatDependency(typeof(Ago.Platform.Hosting.IProductModule).Assembly, "Ago.Platform.Hosting");
    }

    private static void AssertNoAgoChatDependency(Assembly assembly, string assemblyName)
    {
        var result = Types.InAssembly(assembly)
            .Should()
            .NotHaveDependencyOn("Ago.Chat")
            .GetResult();

        var offenders = result.FailingTypeNames is null ? "n/a" : string.Join(", ", result.FailingTypeNames);
        Assert.True(result.IsSuccessful, $"{assemblyName} must never reference an Ago.Chat.* assembly. Offenders: {offenders}");
    }

    private static bool IsBcl(string assemblyName) =>
        assemblyName is "System.Private.CoreLib" or "System.Runtime" or "netstandard" or "mscorlib"
        || assemblyName.StartsWith("System.", StringComparison.Ordinal)
        || assemblyName.StartsWith("Microsoft.CSharp", StringComparison.Ordinal);
}
