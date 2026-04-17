namespace DumpDetective.Cli.Services;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int ConfigurationFailure = 1;
    public const int DumpLoadFailure = 2;
    public const int AnalysisFailure = 3;
    public const int OutputFailure = 4;
    public const int Canceled = 130;
}
