namespace DumpDetective
{
    class DeadlockInfo
    {
        public List<uint> ThreadIds { get; set; } = new();
        public List<LockInfo> LockChain { get; set; } = new();
    }
}
