using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Sdk;

public sealed class IdentityTests
{
    [Fact]
    public void TypeRef_JoinKey_IsCanonicalName()
    {
        var typeRef = new TypeRef { CanonicalName = "System.String", Fidelity = MatchFidelity.Exact };

        typeRef.JoinKey.Should().Be("System.String");
        typeRef.Kind.Should().Be(EntityKind.Type);
    }

    [Fact]
    public void MethodRef_JoinKey_CombinesDeclaringTypeNameAndSignature()
    {
        var declaringType = new TypeRef { CanonicalName = "MyNamespace.MyClass", Fidelity = MatchFidelity.Exact };
        var methodRef = new MethodRef
        {
            DeclaringType = declaringType,
            Name = "DoWork",
            NormalizedSignature = "(System.String)",
            Fidelity = MatchFidelity.Exact,
        };

        methodRef.JoinKey.Should().Be("MyNamespace.MyClass::DoWork(System.String)");
        methodRef.Kind.Should().Be(EntityKind.Method);
    }

    [Fact]
    public void ThreadRef_JoinKey_IsOsThreadId()
    {
        var threadRef = new ThreadRef { OsThreadId = 4242, Fidelity = MatchFidelity.Exact };

        threadRef.JoinKey.Should().Be("4242");
        threadRef.Kind.Should().Be(EntityKind.Thread);
    }

    [Fact]
    public void ObjectRef_JoinKey_ScopesAddressToItsArtifact()
    {
        var artifactA = new ArtifactId("dump-a");
        var artifactB = new ArtifactId("dump-b");

        var refA = new ObjectRef { Address = 0x1000, Artifact = artifactA, Fidelity = MatchFidelity.Exact };
        var refB = new ObjectRef { Address = 0x1000, Artifact = artifactB, Fidelity = MatchFidelity.Exact };

        // Same address, different artifact — must not collide. ObjectRef is never a cross-source
        // join key by design; this only guards that the same raw address in two artifacts doesn't
        // accidentally produce the same JoinKey.
        refA.JoinKey.Should().NotBe(refB.JoinKey);
    }

    [Fact]
    public void MatchFidelity_IsOrderedWorstToBestForMinComparisons()
    {
        // The confidence-cap formula (observation-and-correlation-model.md § 4) takes
        // min(MatchFidelity) over every EntityRef join in a finding's lineage — this only works if
        // the enum's declaration order is the trust ordering.
        (MatchFidelity.None < MatchFidelity.Low).Should().BeTrue();
        (MatchFidelity.Low < MatchFidelity.Medium).Should().BeTrue();
        (MatchFidelity.Medium < MatchFidelity.High).Should().BeTrue();
        (MatchFidelity.High < MatchFidelity.Exact).Should().BeTrue();
    }

    [Fact]
    public void Capability_ImplicitlyConvertsFromString()
    {
        Capability capability = CapabilityVocabulary.HeapObjects;

        capability.Key.Should().Be("heap.objects");
        capability.ToString().Should().Be("heap.objects");
    }

    [Fact]
    public void CapabilityVocabulary_Known_ContainsEveryDeclaredConstant()
    {
        CapabilityVocabulary.Known.Should().Contain(CapabilityVocabulary.HeapObjects);
        CapabilityVocabulary.Known.Should().Contain(CapabilityVocabulary.TraceGcEvents);
        CapabilityVocabulary.Known.Should().Contain(CapabilityVocabulary.TemporalSeries);
    }
}
