namespace DumpDetective.Analysis.Utilities
{
    internal readonly record struct AssemblyIdentity(string Name, string Version, string Culture, string PublicKeyToken, string? FileHash)
    {
        public override string ToString()
            => string.IsNullOrEmpty(Version)
                ? Name
                : $"{Name}, Version={Version}, Culture={Culture}, PublicKeyToken={PublicKeyToken}" + (FileHash is null ? string.Empty : $", Hash={FileHash[..8]}...");
    }

    internal sealed class AssemblyIdentityComparer : IEqualityComparer<AssemblyIdentity>
    {
        public bool Equals(AssemblyIdentity x, AssemblyIdentity y)
        {
            if (!string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(x.Version, y.Version, StringComparison.Ordinal))
                return false;
            if (!string.Equals(x.Culture, y.Culture, StringComparison.Ordinal))
                return false;
            if (!string.Equals(x.PublicKeyToken, y.PublicKeyToken, StringComparison.Ordinal))
                return false;
            if (!string.IsNullOrEmpty(x.FileHash) && !string.IsNullOrEmpty(y.FileHash))
                return string.Equals(x.FileHash, y.FileHash, StringComparison.Ordinal);
            return true;
        }

        public int GetHashCode(AssemblyIdentity obj)
        {
            var hc = new HashCode();
            hc.Add(obj.Name, StringComparer.OrdinalIgnoreCase);
            hc.Add(obj.Version);
            hc.Add(obj.Culture);
            hc.Add(obj.PublicKeyToken);
            if (!string.IsNullOrEmpty(obj.FileHash))
                hc.Add(obj.FileHash);
            return hc.ToHashCode();
        }
    }
}
