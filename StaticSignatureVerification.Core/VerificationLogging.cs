namespace StaticSignatureVerification.Core;

public interface IVerificationLogger
{
    void StageStarted(string correlationId, string stage);
    void StageCompleted(string correlationId, string stage);
    void Warning(string correlationId, string stage, string code, string message);
    void Error(string correlationId, string stage, Exception exception, string publicCode);
}

public sealed class NoopVerificationLogger : IVerificationLogger
{
    public static NoopVerificationLogger Instance { get; } = new();

    private NoopVerificationLogger()
    {
    }

    public void StageStarted(string correlationId, string stage)
    {
    }

    public void StageCompleted(string correlationId, string stage)
    {
    }

    public void Warning(string correlationId, string stage, string code, string message)
    {
    }

    public void Error(string correlationId, string stage, Exception exception, string publicCode)
    {
    }
}

public sealed class InMemoryVerificationLogger : IVerificationLogger
{
    private readonly List<VerificationLogEntry> _entries = new();

    public IReadOnlyList<VerificationLogEntry> Entries => _entries;

    public void StageStarted(string correlationId, string stage) =>
        _entries.Add(new VerificationLogEntry(correlationId, stage, "Started", null, null, null));

    public void StageCompleted(string correlationId, string stage) =>
        _entries.Add(new VerificationLogEntry(correlationId, stage, "Completed", null, null, null));

    public void Warning(string correlationId, string stage, string code, string message) =>
        _entries.Add(new VerificationLogEntry(correlationId, stage, "Warning", code, message, null));

    public void Error(string correlationId, string stage, Exception exception, string publicCode) =>
        _entries.Add(new VerificationLogEntry(correlationId, stage, "Error", publicCode, exception.GetType().Name, exception.Message));
}

public sealed record VerificationLogEntry(
    string CorrelationId,
    string Stage,
    string Level,
    string? Code,
    string? Message,
    string? Detail);

