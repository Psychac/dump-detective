using System.Reflection.PortableExecutable;

using DumpDetective.Analysis.Utilities;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class AssemblyRefProbeTests
{
    // Feeds AssemblyRefProbe the raw ECMA-335 metadata blob extracted from this test assembly's own
    // PE file — the exact same byte layout ClrModule.MetadataAddress/MetadataLength point at in a
    // live process — via a fake IMemoryReader, proving the read+parse pipeline resolves real
    // AssemblyRef entries (this assembly references xunit.core).
    [Fact]
    public void TryEnumerateAssemblyRefs_RealAssemblyMetadata_ResolvesKnownAssemblyRef()
    {
        string assemblyPath = typeof(AssemblyRefProbeTests).Assembly.Location;
        using FileStream stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        byte[] metadataBytes = peReader.GetMetadata().GetContent().ToArray();

        const ulong baseAddress = 0x1000_0000;
        var reader = new FakeMemoryReader(baseAddress, metadataBytes);

        var refs = AssemblyRefProbe.TryEnumerateAssemblyRefs(reader, baseAddress, (ulong)metadataBytes.Length);

        refs.Should().NotBeEmpty();
        refs.Should().Contain(r => r.Name.Contains("xunit", StringComparison.OrdinalIgnoreCase));
        refs.Should().OnlyContain(r => !string.IsNullOrEmpty(r.Name));
    }

    [Fact]
    public void TryEnumerateAssemblyRefs_ZeroAddress_ReturnsEmpty()
    {
        var reader = new FakeMemoryReader(0, Array.Empty<byte>());

        var refs = AssemblyRefProbe.TryEnumerateAssemblyRefs(reader, metadataAddress: 0, metadataLength: 100);

        refs.Should().BeEmpty();
    }

    [Fact]
    public void TryEnumerateAssemblyRefs_CorruptMetadata_ReturnsEmptyWithoutThrowing()
    {
        byte[] garbage = new byte[64];
        new Random(42).NextBytes(garbage);
        var reader = new FakeMemoryReader(0x2000, garbage);

        var refs = AssemblyRefProbe.TryEnumerateAssemblyRefs(reader, 0x2000, (ulong)garbage.Length);

        refs.Should().BeEmpty();
    }

    private sealed class FakeMemoryReader : IMemoryReader
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _data;

        public FakeMemoryReader(ulong baseAddress, byte[] data)
        {
            _baseAddress = baseAddress;
            _data = data;
        }

        public int PointerSize => 8;

        public int Read(ulong address, Span<byte> buffer)
        {
            if (address < _baseAddress) return 0;
            long offset = (long)(address - _baseAddress);
            if (offset >= _data.Length) return 0;

            int count = (int)Math.Min(buffer.Length, _data.Length - offset);
            _data.AsSpan((int)offset, count).CopyTo(buffer);
            return count;
        }

        public bool Read<T>(ulong address, out T value) where T : unmanaged
        {
            value = default;
            return false;
        }

        public T Read<T>(ulong address) where T : unmanaged => default;

        public bool ReadPointer(ulong address, out ulong value)
        {
            value = 0;
            return false;
        }

        public ulong ReadPointer(ulong address) => 0;
    }
}
