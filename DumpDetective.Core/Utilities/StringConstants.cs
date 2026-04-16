namespace DumpDetective.Core.Utilities;
internal static class StringConstants
{
// Separator strings - pre-allocated to avoid repeated allocations
public static readonly string Separator80 = new string('-', 80);
public static readonly string Equals80 = new string('=', 80);

// Common field names in delegates
public const string DelegateTargetField = "_target";
public const string DelegateInvocationListField = "_invocationList";
public const string MulticastDelegateName = "MulticastDelegate";

// Common patterns
public const string StaticPattern = "Static";
public const string UnknownType = "Unknown";
}
