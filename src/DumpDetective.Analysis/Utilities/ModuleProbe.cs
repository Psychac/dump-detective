using System;
using System.IO;
using System.Security.Cryptography;
using System.Reflection;

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

        // Probe in-memory module metadata when available from ClrMD. Returns an identity with metadata-hash when
        // manifest/version data is not extractable but metadata bytes are available.
        public static AssemblyIdentity ProbeAssemblyIdentity(Microsoft.Diagnostics.Runtime.ClrModule? module, string assemblyName)
        {
            // Try to extract metadata bytes via common ClrMD methods/properties using reflection to stay compatible
            // across ClrMD versions. If we obtain metadata bytes we compute a hash and attach it to the identity.
            string? metaHash = null;
            try
            {
                if (module != null)
                {
                    var t = module.GetType();
                    // Common method name candidates
                    var m = t.GetMethod("GetMetadata", BindingFlags.Public | BindingFlags.Instance)
                           ?? t.GetMethod("GetMetaData",  BindingFlags.Public | BindingFlags.Instance);
                    if (m != null)
                    {
                        var res = m.Invoke(module, null);
                        if (res is byte[] b)
                            metaHash = ComputeHashHex(b);
                        else if (res is ReadOnlyMemory<byte> rom)
                            metaHash = ComputeHashHex(rom.ToArray());
                        else if (res is Array arr)
                        {
                            try { metaHash = ComputeHashHex((byte[])arr); } catch { }
                        }
                    }
                    else
                    {
                        // Try property candidates
                        var p = t.GetProperty("Metadata",      BindingFlags.Public | BindingFlags.Instance)
                               ?? t.GetProperty("MetadataBytes",  BindingFlags.Public | BindingFlags.Instance)
                               ?? t.GetProperty("ModuleMetadata", BindingFlags.Public | BindingFlags.Instance);
                        if (p != null)
                        {
                            var res = p.GetValue(module);
                            if (res is byte[] b2)
                                metaHash = ComputeHashHex(b2);
                            else if (res is ReadOnlyMemory<byte> rom2)
                                metaHash = ComputeHashHex(rom2.ToArray());
                        }
                    }
                }
            }
            catch
            {
                // best-effort — ignore failures and fall back
            }

            // If we found metadata bytes, compute a hash and attach; otherwise fall back to disk file probe.
            if (metaHash != null)
            {
                try
                {
                    var an = new AssemblyName(assemblyName ?? string.Empty);
                    string name = an.Name ?? assemblyName ?? string.Empty;
                    string version = an.Version?.ToString() ?? string.Empty;
                    string culture = an.CultureName ?? string.Empty;
                    string pkt = BitConverter.ToString(an.GetPublicKeyToken() ?? Array.Empty<byte>()).Replace("-", "").ToLowerInvariant();
                    return new AssemblyIdentity(name, version, culture, pkt, metaHash);
                }
                catch
                {
                    return new AssemblyIdentity(assemblyName ?? string.Empty, string.Empty, string.Empty, string.Empty, metaHash);
                }
            }

            // Fallback to file/provided path
            return ProbeAssemblyIdentity(module?.Name, assemblyName);
        }

        private static string ComputeHashHex(byte[] bytes)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }

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
