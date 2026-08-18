using FluentAssertions;
using Xunit;

namespace PepperDashPluginSamsungMdcDisplay.Tests;

public class FactoryDiscoveryTests
{
    [Fact]
    public void Assembly_Loads_Successfully()
    {
        var assembly = AssemblyFixture.PluginAssembly;
        assembly.Should().NotBeNull();
    }

    [Fact]
    public void Assembly_Name_Is_EpiDisplaySamsungMdc()
    {
        var assembly = AssemblyFixture.PluginAssembly;
        assembly.GetName().Name.Should().Be("Pepperdash.Essentials.Plugins.Display.Samsung.MDC");
    }

    [Fact]
    public void Factory_Count_Is_One()
    {
        // SamsungMdcControllerFactory is the only user-instantiable factory.
        var factories = AssemblyFixture.FindFactoryTypes();
        factories.Should().HaveCount(1);
    }

    [Fact]
    public void SamsungMdcControllerFactory_Exists()
    {
        var factories = AssemblyFixture.FindFactoryTypes();
        factories.Should().ContainSingle(t => t.Name == "SamsungMdcControllerFactory");
    }

    [Fact]
    public void Factory_Has_Parameterless_Constructor()
    {
        var factories = AssemblyFixture.FindFactoryTypes();
        foreach (var factory in factories)
        {
            var ctor = factory.GetConstructor(Type.EmptyTypes);
            ctor.Should().NotBeNull($"Factory '{factory.Name}' must have a parameterless constructor");
        }
    }
}
