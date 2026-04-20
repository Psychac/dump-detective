namespace DumpDetective.Core.Models;
internal class RootedTypeInfo
{
public string TypeName { get; set; } = string.Empty;
public int Count { get; set; }
public ulong TotalSize { get; set; }
public Dictionary<string, int> RootKinds { get; set; } = new();
}
