using System.Text;

namespace Listenarr.Infrastructure.FileSystem;

/// <summary>
/// Stands in for an exception on its way into the logger. Messages are run through
/// <see cref="LogRedaction.SanitizeText"/> so a line break in a path or an inner cause cannot
/// start a forged log line, while the original type name, the inner cause chain and the stack
/// trace are kept so the failure stays diagnosable.
/// </summary>
/// <remarks>
/// Loggers render <c>ToString()</c>, so that is what is rebuilt. Passing the raw exception
/// instead renders every message in the chain verbatim, bypassing the sanitized template
/// arguments next to it.
/// </remarks>
internal sealed class SanitizedLogException : Exception
{
    private const int MaxChainDepth = 8;
    private const int MaxMessageLength = 4000;

    private readonly string _originalTypeName;
    private readonly string? _originalStackTrace;

    public SanitizedLogException(Exception original)
        : this(original, 1)
    {
    }

    private SanitizedLogException(Exception original, int depth)
        : base(
            LogRedaction.SanitizeText(original.Message, MaxMessageLength),
            original.InnerException is not null && depth < MaxChainDepth
                ? new SanitizedLogException(original.InnerException, depth + 1)
                : null)
    {
        _originalTypeName = original.GetType().FullName ?? original.GetType().Name;
        _originalStackTrace = original.StackTrace;
    }

    public override string? StackTrace => _originalStackTrace;

    public override string ToString()
    {
        var text = new StringBuilder($"{_originalTypeName}: {Message}");
        if (InnerException is not null)
        {
            text.AppendLine().Append(" ---> ").Append(InnerException).AppendLine()
                .Append("   --- End of inner exception stack trace ---");
        }

        if (_originalStackTrace is not null)
        {
            text.AppendLine().Append(_originalStackTrace);
        }

        return text.ToString();
    }
}
