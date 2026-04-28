namespace DumpDetective.Cli.Services;

internal sealed class ConfigurationException(string message, Exception? innerException = null) : Exception(message, innerException);

internal sealed class AnalysisPipelineException(string message, Exception? innerException = null) : Exception(message, innerException);

internal sealed class OutputWriteException(string message, Exception? innerException = null) : Exception(message, innerException);
