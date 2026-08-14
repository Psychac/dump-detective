using DumpDetective.Analysis.Utilities;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class TaskTypeNamePatternTests
{
    [Theory]
    [InlineData("System.Threading.Tasks.Task")]
    [InlineData("System.Threading.Tasks.Task`1[[System.String, mscorlib]]")]
    [InlineData("System.Threading.Tasks.Task+DelayPromise")]
    [InlineData("System.Threading.Tasks.Task+UnwrapPromise`1[[System.Int32, mscorlib]]")]
    public void IsTaskType_ReturnsTrue_ForGenuineTaskTypes(string typeName)
    {
        TaskTypeNamePattern.IsTaskType(typeName).Should().BeTrue();
    }

    // Regression guard for the conflation bug: TaskCompletionSource shares the
    // "System.Threading.Tasks.Task" name prefix but wraps a Task via m_task rather than
    // being one, and has no m_stateFlags field of its own. A prior StartsWith-only check
    // silently misclassified every heap-resident TaskCompletionSource<T> as a Pending task.
    [Theory]
    [InlineData("System.Threading.Tasks.TaskCompletionSource")]
    [InlineData("System.Threading.Tasks.TaskCompletionSource`1[[System.Int32, mscorlib]]")]
    public void IsTaskType_ReturnsFalse_ForTaskCompletionSource(string typeName)
    {
        TaskTypeNamePattern.IsTaskType(typeName).Should().BeFalse();
    }

    [Theory]
    [InlineData("System.String")]
    [InlineData("MyNamespace.TaskLikeThing")]
    [InlineData("")]
    public void IsTaskType_ReturnsFalse_ForUnrelatedTypes(string typeName)
    {
        TaskTypeNamePattern.IsTaskType(typeName).Should().BeFalse();
    }

    [Theory]
    [InlineData("System.Threading.Tasks.TaskCompletionSource")]
    [InlineData("System.Threading.Tasks.TaskCompletionSource`1[[System.Int32, mscorlib]]")]
    public void IsTaskCompletionSource_ReturnsTrue_ForTaskCompletionSourceTypes(string typeName)
    {
        TaskTypeNamePattern.IsTaskCompletionSource(typeName).Should().BeTrue();
    }

    [Theory]
    [InlineData("System.Threading.Tasks.Task")]
    [InlineData("System.Threading.Tasks.Task`1[[System.String, mscorlib]]")]
    [InlineData("System.String")]
    [InlineData("")]
    public void IsTaskCompletionSource_ReturnsFalse_ForNonTaskCompletionSourceTypes(string typeName)
    {
        TaskTypeNamePattern.IsTaskCompletionSource(typeName).Should().BeFalse();
    }
}
