using System.Buffers.Binary;

using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Satellite;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class DiskHandleSnapshotReaderTests
{
	[Fact]
	public void DiskHandleSnapshotReader_ShouldThrow_WhenContainerMissing()
	{
		string path = Path.Combine(Path.GetTempPath(), $"missing-handles-{Guid.NewGuid():N}.bin");

		var act = () => new DiskHandleSnapshotReader(path);

		act.Should().Throw<InvalidDataException>().WithMessage("*container*");
	}

	[Fact]
	public void DiskHandleSnapshotReader_ShouldThrow_WhenHandlesSectionMissing()
	{
		string path = CreateTempContainerWithoutHandlesSection();

		try
		{
			var act = () => new DiskHandleSnapshotReader(path);
			act.Should().Throw<InvalidDataException>().WithMessage("*section*");
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void DiskHandleSnapshotReader_ShouldParseRecords_WhenHeaderAndBodyValid()
	{
		string path = CreateTempContainerWithHandles(
			(0x111UL, 0xAAAUL, 2),
			(0x222UL, 0xBBBUL, 4));

		try
		{
			using var reader = new DiskHandleSnapshotReader(path);
			var records = reader.EnumerateRecords(CancellationToken.None).ToList();

			records.Should().HaveCount(2);
			records[0].Should().Be(new HandleRecord(0x111UL, 0xAAAUL, 2));
			records[1].Should().Be(new HandleRecord(0x222UL, 0xBBBUL, 4));
			reader.RecordCount.Should().Be(2);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void DiskHandleSnapshotReader_ShouldParseDependentTarget_WhenV2FormatValid()
	{
		string path = CreateTempContainerWithHandlesV2(
			(0x111UL, 0xAAAUL, 6, 0x999UL),
			(0x222UL, 0xBBBUL, 4, 0UL));

		try
		{
			using var reader = new DiskHandleSnapshotReader(path);
			var records = reader.EnumerateRecords(CancellationToken.None).ToList();

			records.Should().HaveCount(2);
			records[0].Should().Be(new HandleRecord(0x111UL, 0xAAAUL, 6, DependentTarget: 0x999UL));
			records[1].Should().Be(new HandleRecord(0x222UL, 0xBBBUL, 4));
			reader.RecordCount.Should().Be(2);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void DiskHandleSnapshotReader_ShouldThrow_WhenVersionUnsupported()
	{
		string path = Path.Combine(Path.GetTempPath(), $"handles-badversion-{Guid.NewGuid():N}.bin");

		using (var writer = new CacheContainerWriter(path))
		{
			writer.BeginSection(CacheSectionId.Handles);
			const int Magic = 0x53534448;
			const int UnsupportedVersion = 99;
			var header = new IndexHeader(Magic, UnsupportedVersion, recordCount: 0);
			header.WriteTo(writer.Stream);
			writer.EndSection(0);
			writer.Finish();
		}

		try
		{
			var act = () => new DiskHandleSnapshotReader(path);
			act.Should().Throw<InvalidDataException>().WithMessage("*version*");
		}
		finally
		{
			File.Delete(path);
		}
	}

	private static string CreateTempContainerWithHandlesV2(params (ulong Address, ulong MethodTable, byte Kind, ulong DependentTarget)[] records)
	{
		string path = Path.Combine(Path.GetTempPath(), $"handles-v2-{Guid.NewGuid():N}.bin");

		using var writer = new CacheContainerWriter(path);
		writer.BeginSection(CacheSectionId.Handles);

		const int Magic = 0x53534448; // "HDSS" Handle Snapshot
		const int Version = 2;
		const int RecordSize = 28;

		var header = new IndexHeader(Magic, Version, records.Length);
		header.WriteTo(writer.Stream);

		byte[] record = new byte[RecordSize];
		foreach (var (addr, mt, kind, dependentTarget) in records)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0, 8), addr);
			BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8, 8), mt);
			record[16] = kind;
			record[17] = 0;
			record[18] = 0;
			record[19] = 0;
			BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(20, 8), dependentTarget);

			writer.Stream.Write(record);
		}

		writer.EndSection(records.Length);
		writer.Finish();

		return path;
	}

	private static string CreateTempContainerWithoutHandlesSection()
	{
		string path = Path.Combine(Path.GetTempPath(), $"handles-no-section-{Guid.NewGuid():N}.bin");

		using var writer = new CacheContainerWriter(path);
		// Write some other section (e.g., Objects) to create a valid container
		writer.BeginSection(CacheSectionId.Objects);
		var header = new IndexHeader(0x4A42504F, 1, 0); // dummy magic
		header.WriteTo(writer.Stream);
		writer.EndSection(0);
		writer.Finish();

		return path;
	}

	private static string CreateTempContainerWithHandles(params (ulong Address, ulong MethodTable, byte Kind)[] records)
	{
		string path = Path.Combine(Path.GetTempPath(), $"handles-{Guid.NewGuid():N}.bin");

		using var writer = new CacheContainerWriter(path);
		writer.BeginSection(CacheSectionId.Handles);

		const int Magic = 0x53534448; // "HDSS" Handle Snapshot
		const int Version = 1;
		const int RecordSize = 20;

		var header = new IndexHeader(Magic, Version, records.Length);
		header.WriteTo(writer.Stream);

		byte[] record = new byte[RecordSize];
		foreach (var (addr, mt, kind) in records)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0, 8), addr);
			BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8, 8), mt);
			record[16] = kind;
			record[17] = 0;
			record[18] = 0;
			record[19] = 0;

			writer.Stream.Write(record);
		}

		writer.EndSection(records.Length);
		writer.Finish();

		return path;
	}
}
