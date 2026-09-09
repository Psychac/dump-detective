using DumpDetective.Sdk.Identity;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Sdk;

public sealed class EntityCanonicalizerTests
{
    [Fact]
    public void CanonicalizeTypeName_SimpleTypeNoAssemblyQualification_IsUnchangedAndExact()
    {
        var (canonical, fidelity) = EntityCanonicalizer.CanonicalizeTypeName("System.String");

        canonical.Should().Be("System.String");
        fidelity.Should().Be(MatchFidelity.Exact);
    }

    [Fact]
    public void CanonicalizeTypeName_AssemblyQualifiedSimpleType_StripsQualificationAndIsExact()
    {
        var (canonical, fidelity) = EntityCanonicalizer.CanonicalizeTypeName(
            "System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");

        canonical.Should().Be("System.String");
        fidelity.Should().Be(MatchFidelity.Exact);
    }

    [Fact]
    public void CanonicalizeTypeName_GenericInstantiationWithNestedAssemblyQualification_StripsBothLevels()
    {
        // Both the outer type and the generic argument carry independent assembly qualification —
        // this is the case the earlier bracket-depth-walking implementation got wrong.
        var (canonical, fidelity) = EntityCanonicalizer.CanonicalizeTypeName(
            "System.Collections.Generic.List`1[[System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]], mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");

        canonical.Should().Be("System.Collections.Generic.List`1[[System.String]]");
        fidelity.Should().Be(MatchFidelity.Exact);
    }

    [Fact]
    public void CanonicalizeTypeName_ArrayType_StripsQualificationKeepsSuffix()
    {
        var (canonical, fidelity) = EntityCanonicalizer.CanonicalizeTypeName(
            "System.String[], mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");

        canonical.Should().Be("System.String[]");
        fidelity.Should().Be(MatchFidelity.Exact);
    }

    [Fact]
    public void CanonicalizeTypeName_AsyncStateMachine_UnwrapsToDeclaringMethodAtHighFidelity()
    {
        var (canonical, fidelity) = EntityCanonicalizer.CanonicalizeTypeName(
            "MyNamespace.MyClass+<DoWorkAsync>d__12");

        canonical.Should().Be("DoWorkAsync");
        fidelity.Should().Be(MatchFidelity.High);
    }

    [Fact]
    public void CanonicalizeTypeName_AsyncStateMachine_DifferentOrdinalsCanonicalizeToSameKey()
    {
        // The whole point: two builds where the compiler renumbered the ordinal must still join.
        var (canonicalA, _) = EntityCanonicalizer.CanonicalizeTypeName("MyNamespace.MyClass+<DoWorkAsync>d__12");
        var (canonicalB, _) = EntityCanonicalizer.CanonicalizeTypeName("MyNamespace.MyClass+<DoWorkAsync>d__47");

        canonicalA.Should().Be(canonicalB);
    }

    [Fact]
    public void CanonicalizeTypeName_DisplayClass_StripsOrdinalAtLowFidelity()
    {
        var (canonical, fidelity) = EntityCanonicalizer.CanonicalizeTypeName(
            "MyNamespace.MyClass+<>c__DisplayClass5_0");

        canonical.Should().Be("MyNamespace.MyClass+<>c__DisplayClass");
        fidelity.Should().Be(MatchFidelity.Low);
    }

    [Fact]
    public void CanonicalizeTypeName_LambdaCacheMethod_StripsLeadingOrdinalAtLowFidelity()
    {
        // Faithful to the exact regex measured in tools/EntityJoinSpike/Program.cs: it strips the
        // ordinal digits immediately after ">b__" but not a trailing "_N" disambiguator, since that
        // asymmetry (vs. DisplayClass's "\d+(_\d+)?") is what was actually validated on real data.
        var (canonical, fidelity) = EntityCanonicalizer.CanonicalizeTypeName(
            "MyNamespace.MyClass+<>c.<DoWork>b__3_0");

        canonical.Should().Be("MyNamespace.MyClass+<>c.<DoWork>b___0");
        fidelity.Should().Be(MatchFidelity.Low);
    }

    [Fact]
    public void CanonicalizeTypeName_LocalFunction_StripsOrdinalAtMediumFidelity()
    {
        var (canonical, fidelity) = EntityCanonicalizer.CanonicalizeTypeName("<Foo>g__Bar|3_1");

        canonical.Should().Be("<Foo>g__Bar");
        fidelity.Should().Be(MatchFidelity.Medium);
    }

    [Fact]
    public void CanonicalizeTypeName_DynamicOrUnrecognizedShape_FallsThroughToExact_KnownGap()
    {
        // Documented gap: there's no reliable way to detect a dynamically-emitted type from its
        // name string alone, so this currently (incorrectly) reports Exact rather than None. See
        // the class-level remarks on EntityCanonicalizer.
        var (canonical, fidelity) = EntityCanonicalizer.CanonicalizeTypeName("SomeUnrecognizedShapeName");

        canonical.Should().Be("SomeUnrecognizedShapeName");
        fidelity.Should().Be(MatchFidelity.Exact);
    }

    [Fact]
    public void NormalizeSignature_NoParameters_ReturnsEmptyParensAtExactFidelity()
    {
        var (signature, fidelity) = EntityCanonicalizer.NormalizeSignature([]);

        signature.Should().Be("()");
        fidelity.Should().Be(MatchFidelity.Exact);
    }

    [Fact]
    public void NormalizeSignature_MultipleParameters_JoinsCanonicalizedNames()
    {
        var (signature, fidelity) = EntityCanonicalizer.NormalizeSignature(
        [
            "System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
            "System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
        ]);

        signature.Should().Be("(System.String,System.Int32)");
        fidelity.Should().Be(MatchFidelity.Exact);
    }

    [Fact]
    public void NormalizeSignature_FidelityIsMinimumAcrossParameters()
    {
        var (_, fidelity) = EntityCanonicalizer.NormalizeSignature(
        [
            "System.String",
            "MyNamespace.MyClass+<>c__DisplayClass5_0", // Low
        ]);

        fidelity.Should().Be(MatchFidelity.Low);
    }
}
