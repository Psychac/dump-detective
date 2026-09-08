using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace DumpDetective.Analysis.Utilities
{
    using Microsoft.Diagnostics.Runtime;

    internal static class ModuleProbe
    {
        public static AssemblyIdentity ProbeAssemblyIdentity(string? modulePath, string assemblyName)
        {
            try
            {
                if (!string.IsNullOrEmpty(assemblyName))
                {
                    var an = new AssemblyName(assemblyName);
                    string name = an.Name ?? assemblyName;
                    string version = an.Version?.ToString() ?? string.Empty;
                    string culture = an.CultureName ?? string.Empty;
                    string pkt = BitConverter.ToString(an.GetPublicKeyToken() ?? Array.Empty<byte>()).Replace("-", "").ToLowerInvariant();
                    return new AssemblyIdentity(name, version, culture, pkt, FileHashOrNull(modulePath));
                }
            }
            catch
            {
                // ignore
            }

            return new AssemblyIdentity(assemblyName ?? string.Empty, string.Empty, string.Empty, string.Empty, FileHashOrNull(modulePath));
        }

        // ClrMD 4.0.732401's ClrModule exposes no public in-memory metadata accessor: MetadataReader
        // is internal and wraps an internal IAbstractMetadataReader with no AssemblyRef/version surface.
        // Identity here is derived from the assembly name plus a disk file hash when the module path
        // is resolvable. (ClrModule.MetadataAddress/MetadataLength do expose the raw ECMA-335 metadata
        // root — AssemblyRefProbe reads and parses it directly via System.Reflection.Metadata for the
        // "required vs loaded version" cross-check, but that's a different, coarser-grained identity
        // question than per-instance conflict resolution here.)
        public static AssemblyIdentity ProbeAssemblyIdentity(Microsoft.Diagnostics.Runtime.ClrModule? module, string assemblyName)
            => ProbeAssemblyIdentity(module?.Name, assemblyName);

        public static string? FileHashOrNull(string? path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return null;
                if (!File.Exists(path)) return null;
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                byte[] hash = sha.ComputeHash(stream);
                return Convert.ToHexString(hash);
            }
            catch
            {
                return null;
            }
        }
    }
}
