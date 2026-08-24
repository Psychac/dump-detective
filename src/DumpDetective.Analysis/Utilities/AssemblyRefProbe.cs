using System.Collections.Immutable;
using System.Reflection.Metadata;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Utilities
{
    // Reads a module's raw ECMA-335 metadata root directly out of process memory and parses its
    // AssemblyRef table. ClrModule.MetadataImport (the DAC's own metadata reader) is internal with
    // no public AssemblyRef surface in ClrMD 4.0.732401, but ClrModule.MetadataAddress/MetadataLength
    // are public and point at the same in-memory metadata blob the DAC reads from — System.Reflection.
    // Metadata.MetadataReader can parse it directly without any COM interop or PE-header parsing.
    internal static class AssemblyRefProbe
    {
        // Guards against a corrupt/implausible MetadataLength causing an oversized allocation.
        private const int MaxMetadataBytesToRead = 8 * 1024 * 1024;

        public readonly record struct AssemblyRefEntry(string Name, string Version);

        public static IReadOnlyList<AssemblyRefEntry> TryEnumerateAssemblyRefs(IMemoryReader reader, ulong metadataAddress, ulong metadataLength)
        {
            if (metadataAddress == 0 || metadataLength == 0 || metadataLength > MaxMetadataBytesToRead)
                return Array.Empty<AssemblyRefEntry>();

            byte[] buffer = new byte[(int)metadataLength];
            int bytesRead = reader.Read(metadataAddress, buffer);
            if (bytesRead <= 0)
                return Array.Empty<AssemblyRefEntry>();

            try
            {
                ImmutableArray<byte> image = ImmutableArray.Create(buffer, 0, bytesRead);
                using MetadataReaderProvider provider = MetadataReaderProvider.FromMetadataImage(image);
                MetadataReader mdReader = provider.GetMetadataReader();

                AssemblyReferenceHandleCollection assemblyRefs = mdReader.AssemblyReferences;
                var results = new List<AssemblyRefEntry>(assemblyRefs.Count);
                foreach (AssemblyReferenceHandle handle in assemblyRefs)
                {
                    AssemblyReference assemblyRef = mdReader.GetAssemblyReference(handle);
                    string name = mdReader.GetString(assemblyRef.Name);
                    results.Add(new AssemblyRefEntry(name, assemblyRef.Version.ToString()));
                }
                return results;
            }
            catch (BadImageFormatException)
            {
                return Array.Empty<AssemblyRefEntry>();
            }
        }
    }
}
