// Generates/verifies schema/DumpDetective.Schema/capability-registry.json from
// src/DumpDetective.Sdk/Artifacts/CapabilityVocabulary.cs — the mechanical half of adding a new
// capability (JSON shape, group/providedBy derivation, keeping the registry's key set in exact
// sync with CapabilityVocabulary.Known) no longer needs a hand-edit that only a test catches if
// forgotten. Run: dotnet run --project tools/CapabilityRegistryAudit
//
// What this does NOT do, on purpose: invent an addedIn version or a real note for a new
// capability. Those are curatorial judgment (same reasoning tools/CapabilityMappingAudit's own
// header gives for why its optional-trace-capability table is hand-curated, not derived) — a
// scaffold entry is written flagging exactly that, and the tool exits nonzero until it's filled
// in, so CI can catch an uncurated entry the same way SdkRegistryConformanceTests already catches
// a missing one.

using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using DumpDetective.Sdk.Artifacts;

const string NeedsCurationMarker = "NEEDS CURATION";

string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string registryPath = Path.Combine(repoRoot, "schema", "DumpDetective.Schema", "capability-registry.json");

// Same reflection CapabilityVocabulary.Known itself uses (see that property's own remarks on why
// it's reflection over const fields, not a hand-listed set) — this generator can never see a
// capability [RequiresCapability]/[OptionalCapability] couldn't also reference.
string[] orderedKeys = typeof(CapabilityVocabulary)
    .GetFields(BindingFlags.Public | BindingFlags.Static)
    .Where(f => f.IsLiteral && f.FieldType == typeof(string))
    .Select(f => (string)f.GetRawConstantValue()!)
    .ToArray();
HashSet<string> knownKeys = orderedKeys.ToHashSet(StringComparer.Ordinal);

JsonObject registry = JsonNode.Parse(File.ReadAllText(registryPath))!.AsObject();
JsonObject existingCapabilities = registry["capabilities"]!.AsObject();

List<string> added = [];
List<string> removed = [];
var mergedCapabilities = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

foreach (string key in orderedKeys)
{
    if (existingCapabilities.TryGetPropertyValue(key, out JsonNode? existingNode) && existingNode is not null)
    {
        mergedCapabilities[key] = existingNode.DeepClone().AsObject();
    }
    else
    {
        added.Add(key);
        mergedCapabilities[key] = ScaffoldEntry(key);
    }
}

foreach (KeyValuePair<string, JsonNode?> entry in existingCapabilities)
{
    if (!knownKeys.Contains(entry.Key))
        removed.Add(entry.Key);
}

// Checked on every run, not just one that just added an entry — otherwise a scaffold entry
// written by a previous run silently stops being flagged the moment its key exists, which would
// defeat the whole point of this being a CI-catchable exit code.
List<string> uncuratedKeys = mergedCapabilities
    .Where(kvp => kvp.Value.TryGetPropertyValue("note", out JsonNode? note)
        && note?.GetValue<string>().Contains(NeedsCurationMarker) == true)
    .Select(kvp => kvp.Key)
    .ToList();

if (added.Count == 0 && removed.Count == 0)
{
    if (uncuratedKeys.Count == 0)
    {
        Console.WriteLine($"capability-registry.json already matches CapabilityVocabulary.Known exactly ({orderedKeys.Length} capabilities) — nothing to do.");
        return 0;
    }

    Console.WriteLine($"capability-registry.json's key set matches CapabilityVocabulary.Known, but {uncuratedKeys.Count} entr{(uncuratedKeys.Count == 1 ? "y" : "ies")} still {NeedsCurationMarker}:");
    foreach (string key in uncuratedKeys)
        Console.WriteLine($"  ! {key}");
    return 1;
}

registry["registryVersion"] = BumpMinor((string)registry["registryVersion"]!);

WriteRegistry(registryPath, registry, orderedKeys, mergedCapabilities);

if (added.Count > 0)
{
    Console.WriteLine($"Added {added.Count} scaffold entr{(added.Count == 1 ? "y" : "ies")} to capability-registry.json — fill in a real 'addedIn' and 'note' before committing:");
    foreach (string key in added)
        Console.WriteLine($"  + {key}");
}

if (removed.Count > 0)
{
    Console.WriteLine($"Removed {removed.Count} stale entr{(removed.Count == 1 ? "y" : "ies")} no longer in CapabilityVocabulary (deleted, not just renamed? verify before committing):");
    foreach (string key in removed)
        Console.WriteLine($"  - {key}");
}

return uncuratedKeys.Count > 0 ? 1 : 0;

static JsonObject ScaffoldEntry(string key)
{
    string prefix = key[..key.IndexOf('.')];
    (string group, string providedBy) = prefix switch
    {
        "heap" => ("heap", "dump"),
        "runtime" => ("runtime", "dump"),
        "dump" => ("dump", "dump"),
        "trace" => ("trace", "trace"),
        "temporal" => ("temporal", "any"),
        _ => throw new InvalidOperationException(
            $"Unknown capability prefix '{prefix}' for '{key}' — add a group/providedBy mapping to this tool before regenerating."),
    };

    return new JsonObject
    {
        ["group"] = group,
        ["providedBy"] = providedBy,
        ["addedIn"] = NeedsCurationMarker,
        ["note"] = $"{NeedsCurationMarker} — scaffolded by tools/CapabilityRegistryAudit from a new CapabilityVocabulary entry; " +
                   "replace this with the real addedIn version (current registryVersion, unless this ships alongside a release) " +
                   "and a real rationale before committing.",
    };
}

static string BumpMinor(string version)
{
    string[] parts = version.Split('.');
    if (parts.Length != 3 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
        throw new InvalidOperationException($"registryVersion '{version}' isn't in major.minor.patch form.");

    return $"{major}.{minor + 1}.0";
}

static void WriteRegistry(
    string path,
    JsonObject registry,
    IReadOnlyList<string> orderedKeys,
    IReadOnlyDictionary<string, JsonObject> capabilities)
{
    // The rest of the registry (and CapabilityVocabulary.cs's own doc comments) use literal UTF-8
    // em-dashes/etc. in prose, not \uXXXX escapes — match that instead of System.Text.Json's
    // default escaping of anything non-ASCII.
    var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    string S(string value) => JsonSerializer.Serialize(value, options);

    var sb = new StringBuilder();
    sb.Append('{').Append('\n');
    sb.Append("  \"$schema\": ").Append(S((string)registry["$schema"]!)).Append(",\n");
    sb.Append("  \"$id\": ").Append(S((string)registry["$id"]!)).Append(",\n");
    sb.Append("  \"registryVersion\": ").Append(S((string)registry["registryVersion"]!)).Append(",\n");
    sb.Append("  \"description\": ").Append(S((string)registry["description"]!)).Append(",\n");
    sb.Append("  \"capabilities\": {\n");

    string? previousGroup = null;
    for (int i = 0; i < orderedKeys.Count; i++)
    {
        string key = orderedKeys[i];
        JsonObject entry = capabilities[key];
        string group = (string)entry["group"]!;

        if (previousGroup is not null && group != previousGroup)
            sb.Append('\n');
        previousGroup = group;

        string providedBy = (string)entry["providedBy"]!;
        string addedIn = (string)entry["addedIn"]!;
        string? note = entry.TryGetPropertyValue("note", out JsonNode? noteNode) ? (string?)noteNode : null;
        string comma = i == orderedKeys.Count - 1 ? "" : ",";

        sb.Append("    ").Append(S(key)).Append(": ");
        if (note is null)
        {
            sb.Append("{ \"group\": ").Append(S(group))
              .Append(", \"providedBy\": ").Append(S(providedBy))
              .Append(", \"addedIn\": ").Append(S(addedIn))
              .Append(" }").Append(comma).Append('\n');
        }
        else
        {
            sb.Append("{\n      \"group\": ").Append(S(group))
              .Append(", \"providedBy\": ").Append(S(providedBy))
              .Append(", \"addedIn\": ").Append(S(addedIn)).Append(",\n")
              .Append("      \"note\": ").Append(S(note)).Append('\n')
              .Append("    }").Append(comma).Append('\n');
        }
    }

    sb.Append("  }\n}\n");
    File.WriteAllText(path, sb.ToString());
}

static string FindRepoRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "src", "DumpDetective.Core")))
            return dir.FullName;
        dir = dir.Parent;
    }

    throw new InvalidOperationException($"Could not locate repo root (src/DumpDetective.Core) above '{startDir}'.");
}
