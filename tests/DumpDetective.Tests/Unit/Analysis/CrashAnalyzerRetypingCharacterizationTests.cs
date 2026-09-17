using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Analyzers.Runtime;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for <see cref="CrashAnalyzer"/>'s Tier-1+Tier-2 retyping
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) — against fake,
/// dictionary-backed capability implementations, same shape as
/// <c>TimerLeakAnalyzerRetypingCharacterizationTests</c>. Exercises message/HResult/inner-exception
/// field reads, <c>AggregateException</c> unwrapping via the array-shape capability, active-thread
/// correlation via <see cref="RuntimeThreadRef.ExceptionAddress"/>, generation bucketing via
/// <see cref="IHeapSegmentQuery.GetGeneration"/>, and Gen2/LOH retention-path root-kind splitting.
/// </summary>
public sealed class CrashAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public async Task AnalyzeAsync_SingleException_CountsAndReadsMessageAndHResult()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetString(0x1000, "_message", "boom");
        fields.SetInt32(0x1000, "_HResult", unchecked((int)0x80131500));

        CrashDomainResult result = await Analyze(fields: fields, candidates: [Candidate(0x1000, "System.InvalidOperationException")]);

        result.TotalExceptions.Should().Be(1);
        result.ExceptionTypeCounts.Should().ContainKey("System.InvalidOperationException").WhoseValue.Should().Be(1);
        result.TopExceptionInstances.Should().ContainSingle().Which.Message.Should().Be("boom");
        result.TopExceptionInstances.Should().ContainSingle().Which.HResult.Should().Be(unchecked((int)0x80131500));
    }

    [Fact]
    public async Task AnalyzeAsync_InnerException_ResolvesInnerExceptionTypeViaObjectLookup()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_innerException", 0x2000);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "System.IO.IOException");

        CrashDomainResult result = await Analyze(fields: fields, objectLookup: objectLookup, candidates: [Candidate(0x1000, "System.AggregateException")]);

        result.TopExceptionInstances.Should().ContainSingle().Which.InnerExceptionType.Should().Be("System.IO.IOException");
    }

    [Fact]
    public async Task AnalyzeAsync_AggregateException_UnwrapsInnerExceptionsArray()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetObjectReference(0x1000, "_innerExceptions", 0x9000);

        var arrayShapes = new FakeDumpArrayShapeQuery();
        arrayShapes.SetArray(0x9000, [0x2000, 0x3000]);

        var objectLookup = new FakeHeapObjectLookup();
        objectLookup.SetObject(0x2000, "System.IO.IOException");
        objectLookup.SetObject(0x3000, "System.TimeoutException");

        CrashDomainResult result = await Analyze(
            fields: fields, arrayShapes: arrayShapes, objectLookup: objectLookup,
            aggregateAddresses: new HashSet<ulong> { 0x1000UL },
            candidates: [Candidate(0x1000, "System.AggregateException")]);

        result.AggregateExceptionCount.Should().Be(1);
        result.AggregateInnerExceptionTypeCounts.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["System.IO.IOException"] = 1,
            ["System.TimeoutException"] = 1,
        });
    }

    [Fact]
    public async Task AnalyzeAsync_ThreadWithMatchingCurrentException_TracksAsActiveCandidate()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddActiveExceptionThread(osThreadId: 7, managedThreadId: 3, exceptionAddress: 0x1000);

        CrashDomainResult result = await Analyze(threads: threads, candidates: [Candidate(0x1000, "System.NullReferenceException")]);

        result.ActiveExceptions.Should().Be(1);
        result.TopCrashThreadCandidates.Should().ContainSingle().Which.OSThreadId.Should().Be(7u);
    }

    [Fact]
    public async Task AnalyzeAsync_NoStackTraceStringText_FallsBackToBinaryUnwoundTrace()
    {
        // No "_stackTraceString" set -- exception was thrown/unwound but never had .ToString()/
        // .StackTrace called to materialize it as text; EnumerateExceptionStackTrace still resolves it.
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetUnwoundStackTrace(0x1000, "MyApp.Services.OrderService.Process()");

        CrashDomainResult result = await Analyze(fields: fields, candidates: [Candidate(0x1000, "System.Exception")]);

        result.TopExceptionInstances.Should().ContainSingle().Which.OriginalStackTrace
            .Should().ContainSingle(f => f.Contains("MyApp.Services.OrderService.Process()"));
    }

    [Fact]
    public async Task AnalyzeAsync_RemoteStackTraceStringPresent_MarksInstanceRethrown()
    {
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetString(0x1000, "_remoteStackTraceString", "   at Some.Original.Frame()");
        fields.SetString(0x1000, "_stackTraceString", "   at Some.Rethrow.Frame()");

        CrashDomainResult result = await Analyze(fields: fields, candidates: [Candidate(0x1000, "System.Exception")]);

        result.TopExceptionInstances.Should().ContainSingle().Which.IsRethrown.Should().BeTrue();
    }

    [Theory]
    [InlineData(HeapGenerationTag.Gen0, false)]
    [InlineData(HeapGenerationTag.Gen1, false)]
    [InlineData(HeapGenerationTag.Gen2, true)]
    [InlineData(HeapGenerationTag.Loh, true)]
    public async Task AnalyzeAsync_GenerationBucketing_GatesRetentionPathSearchToGen2AndAbove(HeapGenerationTag tag, bool expectRetentionPath)
    {
        var segments = new FakeHeapSegmentQuery();
        segments.SetGeneration(0x1000, tag);

        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "StaticVar: MyApp.Registry@0x2000 -> System.Exception@0x1000");

        CrashDomainResult result = await Analyze(segments: segments, rootPaths: rootPaths, candidates: [Candidate(0x1000, "System.Exception")]);

        if (expectRetentionPath)
            result.Gen2RetentionPaths.Should().ContainSingle();
        else
            result.Gen2RetentionPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_Gen2Exception_GetsRootPathWithSplitRootKind()
    {
        var segments = new FakeHeapSegmentQuery();
        segments.SetGeneration(0x1000, HeapGenerationTag.Gen2);

        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "StaticVar: MyApp.Registry@0x2000 -> System.Exception@0x1000");

        CrashDomainResult result = await Analyze(segments: segments, rootPaths: rootPaths, candidates: [Candidate(0x1000, "System.Exception")]);

        result.Gen2RetentionPaths.Should().ContainSingle();
        var path = result.Gen2RetentionPaths!.Single();
        path.RootKind.Should().Be("StaticVar");
        path.FormattedPath.Should().Be("MyApp.Registry@0x2000 -> System.Exception@0x1000");
    }

    [Fact]
    public async Task AnalyzeAsync_NoCandidates_ReturnsEmptyResult()
    {
        CrashDomainResult result = await Analyze(candidates: []);

        result.TotalExceptions.Should().Be(0);
        result.ActiveExceptions.Should().Be(0);
    }

    private static HeapObjectRef Candidate(ulong address, string typeName) =>
        new(address, SdkTypeRefFactory.FromName(typeName, null), Size: 24, TypeDisplayName: typeName);

    private static async Task<CrashDomainResult> Analyze(
        FakeDumpObjectFieldQuery? fields = null,
        FakeDumpArrayShapeQuery? arrayShapes = null,
        FakeHeapObjectLookup? objectLookup = null,
        FakeHeapSegmentQuery? segments = null,
        FakeRuntimeThreadQuery? threads = null,
        FakeHeapRootPathQuery? rootPaths = null,
        IReadOnlySet<ulong>? aggregateAddresses = null,
        params HeapObjectRef[] candidates)
    {
        CrashAnalyzer analyzer = new();
        DumpDetective.Sdk.Analysis.AnalysisContext context = new() {
            Observations = NoOpObservationSink.Instance,
            ArtifactId = new ArtifactId("test"),
            DumpObjectFields = fields ?? new FakeDumpObjectFieldQuery(),
            DumpArrayShapes = arrayShapes ?? new FakeDumpArrayShapeQuery(),
            HeapObjectLookup = objectLookup ?? new FakeHeapObjectLookup(),
            HeapSegments = segments ?? new FakeHeapSegmentQuery(),
            RuntimeThreads = threads ?? new FakeRuntimeThreadQuery(),
            HeapRootPaths = rootPaths ?? new FakeHeapRootPathQuery(),
            HeapObjects = new PrecomputedHeapObjectStream(candidates),
            AnalyzerOptions = new CrashAnalyzerOptions(new CrashAnalysisOptions(), aggregateAddresses ?? new HashSet<ulong>()),
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (CrashDomainResult)analyzer.LastResult!;
    }

    private sealed class FakeDumpObjectFieldQuery : IDumpObjectFieldQuery
    {
        private readonly Dictionary<(ulong Address, string FieldName), FieldValue> _values = new();
        private readonly Dictionary<ulong, List<ThreadStackFrameRef>> _unwoundTraces = new();

        public void SetString(ulong address, string fieldName, string value) => _values[(address, fieldName)] = FieldValue.ForString(value);
        public void SetInt32(ulong address, string fieldName, int value) => _values[(address, fieldName)] = FieldValue.ForInt32(value);
        public void SetObjectReference(ulong address, string fieldName, ulong target) => _values[(address, fieldName)] = FieldValue.ForObjectReference(target);

        public void SetUnwoundStackTrace(ulong exceptionAddress, params string[] methodSignatures)
        {
            var frames = new List<ThreadStackFrameRef>(methodSignatures.Length);
            foreach (string signature in methodSignatures)
                frames.Add(new ThreadStackFrameRef(IsManagedMethod: true, HasMethod: true, DeclaringTypeName: "", ModuleName: "",
                    IsDynamicModule: false, IsReadyToRun: false, MethodDesc: 0, NativeCodeAddress: 0, HotSize: 0, ColdSize: 0,
                    MethodDisplayName: signature, RawMethodSignature: signature));
            _unwoundTraces[exceptionAddress] = frames;
        }

        public bool TryReadField(ulong address, string fieldName, out FieldValue value) =>
            _values.TryGetValue((address, fieldName), out value);

        public bool TryGetStringValue(ulong address, out string? value) { value = null; return false; }

        public bool TryGetStringValue(ulong address, int maxLength, out string? value) => TryGetStringValue(address, out value);

        public bool TryGetFieldTypeName(ulong address, string fieldName, out string? typeName) { typeName = null; return false; }

        public IReadOnlyList<(string Name, ulong TargetAddress)> EnumerateObjectReferenceFields(ulong address) => [];

        public bool TryReadNestedStructField(ulong address, string outerFieldName, string innerFieldName, out FieldValue value)
        {
            value = FieldValue.Null;
            return false;
        }

        public IReadOnlyList<InstanceFieldDescriptor> EnumerateInstanceFields(ulong address) => [];

        public IReadOnlyList<ThreadStackFrameRef> EnumerateExceptionStackTrace(ulong exceptionAddress, int maxFrames) =>
            _unwoundTraces.TryGetValue(exceptionAddress, out List<ThreadStackFrameRef>? frames) ? frames.Take(maxFrames).ToList() : [];

        public bool HasDelegateField(ulong address) => false;
    }

    private sealed class FakeDumpArrayShapeQuery : IDumpArrayShapeQuery
    {
        private readonly Dictionary<ulong, ulong[]> _arrays = new();
        public void SetArray(ulong address, ulong[] elementAddresses) => _arrays[address] = elementAddresses;

        public bool TryGetArrayShape(ulong address, out int length, out int rank)
        {
            if (_arrays.TryGetValue(address, out ulong[]? elements))
            {
                length = elements.Length;
                rank = 1;
                return true;
            }
            length = 0;
            rank = 0;
            return false;
        }

        public int? CountNullOrZeroElements(ulong address) => null;

        public IReadOnlyList<ulong> EnumerateObjectReferenceElements(ulong address, int maxElements) =>
            _arrays.TryGetValue(address, out ulong[]? elements) ? elements.Take(maxElements).ToArray() : [];
    }

    private sealed class FakeHeapObjectLookup : IHeapObjectLookup
    {
        private readonly Dictionary<ulong, string> _typeNames = new();
        public void SetObject(ulong address, string typeName) => _typeNames[address] = typeName;

        public bool TryGetObject(ulong address, out HeapObjectRef heapObject)
        {
            if (_typeNames.TryGetValue(address, out string? typeName))
            {
                heapObject = new HeapObjectRef(address, SdkTypeRefFactory.FromName(typeName, null), Size: 24, TypeDisplayName: typeName);
                return true;
            }
            heapObject = default;
            return false;
        }
    }

    private sealed class FakeHeapSegmentQuery : IHeapSegmentQuery
    {
        private readonly Dictionary<ulong, HeapGenerationTag> _generations = new();
        public void SetGeneration(ulong address, HeapGenerationTag tag) => _generations[address] = tag;

        public HeapGenerationTag GetGeneration(ulong address) =>
            _generations.TryGetValue(address, out HeapGenerationTag tag) ? tag : HeapGenerationTag.Unknown;

        public IEnumerable<HeapSegmentRef> EnumerateSegments() => [];
        public bool TryGetSegment(ulong address, out HeapSegmentRef segment) { segment = default; return false; }
        public int DumpPointerSize => 8;
        public bool IsServerGc => false;
        public int LogicalHeapCount => 1;
        public bool CanWalkHeap => true;
        public IEnumerable<HeapObjectRef> EnumerateObjects(HeapSegmentRef segment, bool includeFree = false) => [];
        public IEnumerable<HeapFreeBlockRef> EnumerateLohFreeBlocks() => [];
        public IEnumerable<HeapObjectRef> EnumerateCapturedLargeObjects() => [];
        public bool HasLohSatelliteIndex => false;
    }

    private sealed class FakeRuntimeThreadQuery : IRuntimeThreadQuery
    {
        private readonly List<RuntimeThreadRef> _threads = new();

        public void AddActiveExceptionThread(uint osThreadId, int managedThreadId, ulong exceptionAddress) =>
            _threads.Add(new RuntimeThreadRef(
                Thread: new ThreadRef { OsThreadId = osThreadId, ManagedThreadId = managedThreadId, Fidelity = MatchFidelity.Exact },
                IsGCSuspendPending: false,
                StackRootCount: 0,
                IsAlive: true,
                HasCurrentException: true,
                ExceptionAddress: exceptionAddress));

        public IEnumerable<RuntimeThreadRef> EnumerateThreads() => _threads;

        public IEnumerable<ThreadStackFrameRef> EnumerateStackFrames(RuntimeThreadRef thread) => [];
    }

    private sealed class FakeHeapRootPathQuery : IHeapRootPathQuery
    {
        private readonly Dictionary<ulong, string> _paths = new();
        public void SetPath(ulong address, string formattedPath) => _paths[address] = formattedPath;

        public bool TryFindRootPath(ulong targetAddress, RootPathSearchOptions options, out string? formattedPath, out bool searchTruncated)
        {
            searchTruncated = false;
            return _paths.TryGetValue(targetAddress, out formattedPath);
        }

        public bool TryFindRootPathHops(ulong targetAddress, RootPathSearchOptions options, out string? rootKind, out IReadOnlyList<string>? hopTypeNames, out bool searchTruncated)
        {
            rootKind = null;
            hopTypeNames = null;
            searchTruncated = false;
            return false;
        }

        public bool TryFindRootPathAddresses(ulong targetAddress, RootPathSearchOptions options, out string? rootKind, out IReadOnlyList<ulong>? hopAddresses, out bool searchTruncated)
        {
            rootKind = null;
            hopAddresses = null;
            searchTruncated = false;
            return false;
        }
    }
}
