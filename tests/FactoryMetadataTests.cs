using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace PepperDashPluginSamsungMdcDisplay.Tests;

public class FactoryMetadataTests
{
    private static readonly Lazy<string> FactorySourceContent = new(() =>
    {
        var factoryFile = Path.Combine(AssemblyFixture.SourceDirectory, "SamsungMdcControllerFactory.cs");
        return File.ReadAllText(factoryFile);
    });

    [Fact]
    public void Factory_Sets_MinimumEssentialsFrameworkVersion_To_3_0_0()
    {
        var content = FactorySourceContent.Value;

        // Must match the exact PepperDashEssentials version pinned in the csproj, not a bare
        // "3.0.0" (no stable/GA 3.0.0 has shipped; semver ranks a prerelease/RC lower than the
        // plain version, so a mismatched literal can fail the runtime compatibility gate).
        var pattern = @"MinimumEssentialsFrameworkVersion\s*=\s*""3\.0\.0-rc\.1""";
        Regex.IsMatch(content, pattern).Should().BeTrue(
            "Factory should set MinimumEssentialsFrameworkVersion to \"3.0.0-rc.1\", matching the pinned PackageReference");
    }

    [Fact]
    public void Factory_Sets_TypeNames()
    {
        var content = FactorySourceContent.Value;

        var pattern = @"TypeNames\s*=\s*new\s+List<string>";
        Regex.IsMatch(content, pattern).Should().BeTrue(
            "Factory should set TypeNames in the constructor");
    }

    [Theory]
    [InlineData("samsungMdcPlugin")]
    public void Factory_Source_Contains_TypeName(string typeName)
    {
        var content = FactorySourceContent.Value;

        content.Should().Contain($"\"{typeName}\"",
            $"Factory should register type name \"{typeName}\"");
    }

    [Fact]
    public void No_Duplicate_TypeNames_In_Factory_Source()
    {
        var content = FactorySourceContent.Value;

        var typeNamesMatch = Regex.Match(content, @"TypeNames\s*=\s*new\s+List<string>\s*\{([^}]+)\}");
        typeNamesMatch.Success.Should().BeTrue("Should find TypeNames assignment");

        var typeNamesBlock = typeNamesMatch.Groups[1].Value;
        var quotedStrings = Regex.Matches(typeNamesBlock, @"""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        quotedStrings.Should().OnlyHaveUniqueItems("TypeNames should not contain duplicates");
    }
}
