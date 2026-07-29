1. Analyzers have category strings but IAnalyzer's category uses inference still from analyzer name. Having analyzers define category will be much better I believe.

2. FormatBytes(ulong bytes), BuildFohSegments(ClrHeap heap) and other such helpers are implemented separately in many analyzers, can centralize. 

3. AsyncTaskAnalyzer seems to have some logic about extracting exceptions and their data. Could be a possible duplicate of logic in CrashAnalyzer? Maybe we can extract a helper or something.