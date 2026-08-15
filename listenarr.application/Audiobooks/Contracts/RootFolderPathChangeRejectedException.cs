namespace Listenarr.Application.Audiobooks.Contracts;

public sealed class RootFolderPathChangeRejectedException : InvalidOperationException
{
    public RootFolderPathChangeRejectedException(
        string code,
        string publicMessage,
        string? internalMessage = null)
        : base(internalMessage ?? publicMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicMessage);
        Code = code;
        PublicMessage = publicMessage;
    }

    public string Code { get; }

    public string PublicMessage { get; }
}
