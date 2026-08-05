using System.Security.Cryptography;
using System.Text.Json;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private const int PreparedPublicationStateVersion = 1;

    private enum PreparedPublicationRecoveryOutcome
    {
        None,
        RolledBack,
        Completed
    }

    private readonly record struct PreparedPublicationRecoveryResult(
        PreparedPublicationRecoveryOutcome Outcome,
        string? PublishedDestinationObjectIdentity = null,
        string? SourceObjectIdentity = null);

    private sealed record PreparedPublicationState(
        int Version,
        string DestinationName,
        string SourceObjectIdentity,
        string PreparedObjectIdentity,
        string PreviousObjectIdentity,
        long PreparedLength,
        string PreparedSha256,
        bool Committed,
        string? PublishedDestinationObjectIdentity);

    private sealed record PreparedPublicationStateEnvelope(
        string PayloadBase64,
        string Sha256);

    private async Task<string> GetPreparedFilePublicationStateNameAsync(
        string destinationPath)
    {
        var normalizedDestination = Path.GetFullPath(destinationPath);
        var semantics = await _semanticsResolver.ResolveAsync(normalizedDestination);
        if (semantics.State != PathIdentityState.Valid
            || semantics.Semantics.CaseSensitivity
                == FileSystemCaseSensitivity.Unknown)
        {
            throw new IOException(
                "Filesystem identity is unavailable for recoverable file publication.");
        }

        var publicationIdentity = FileSystemPathIdentity.CreateKey(
            "file-publication",
            normalizedDestination,
            semantics.Semantics);
        return $".listenarr-file-publication-{HashPathIdentity(publicationIdentity)}.state";
    }

    private static void WritePreparedPublicationState(
        PinnedDirectoryCreation.PinnedFileEntry stateFile,
        PreparedPublicationState state)
    {
        ValidatePreparedPublicationState(state);
        var payload = JsonSerializer.SerializeToUtf8Bytes(state);
        var envelope = new PreparedPublicationStateEnvelope(
            Convert.ToBase64String(payload),
            Convert.ToHexString(SHA256.HashData(payload)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
        using var stream = stateFile.OpenWriteStream(
            bufferSize: 4096,
            asynchronous: false);
        stream.SetLength(0);
        stream.Position = 0;
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static PreparedPublicationState? ReadPreparedPublicationState(
        PinnedDirectoryCreation.PinnedFileEntry stateFile)
    {
        using var stream = stateFile.OpenReadStream(
            bufferSize: 4096,
            asynchronous: false);
        if (stream.Length is <= 0 or > 16 * 1024)
        {
            return null;
        }

        var bytes = new byte[stream.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                return null;
            }
            offset += read;
        }

        PreparedPublicationStateEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<PreparedPublicationStateEnvelope>(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
        if (envelope == null
            || string.IsNullOrWhiteSpace(envelope.PayloadBase64)
            || envelope.Sha256.Length != 64
            || envelope.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            return null;
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(envelope.PayloadBase64);
        }
        catch (FormatException)
        {
            return null;
        }
        if (!string.Equals(
                Convert.ToHexString(SHA256.HashData(payload)),
                envelope.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        PreparedPublicationState? state;
        try
        {
            state = JsonSerializer.Deserialize<PreparedPublicationState>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
        if (state == null)
        {
            return null;
        }

        try
        {
            ValidatePreparedPublicationState(state);
            return state;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void ValidatePreparedPublicationState(
        PreparedPublicationState state)
    {
        if (state.Version != PreparedPublicationStateVersion)
        {
            throw new InvalidOperationException(
                "The prepared publication state version is unsupported.");
        }
        if (string.IsNullOrWhiteSpace(state.DestinationName)
            || !string.Equals(
                Path.GetFileName(state.DestinationName),
                state.DestinationName,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(state.SourceObjectIdentity)
            || string.IsNullOrWhiteSpace(state.PreparedObjectIdentity)
            || string.IsNullOrWhiteSpace(state.PreviousObjectIdentity)
            || state.PreparedLength < 0
            || state.PreparedSha256.Length != 64
            || state.PreparedSha256.Any(character => !Uri.IsHexDigit(character))
            || state.SourceObjectIdentity.Contains('\r')
            || state.SourceObjectIdentity.Contains('\n')
            || state.PreparedObjectIdentity.Contains('\r')
            || state.PreparedObjectIdentity.Contains('\n')
            || state.PreviousObjectIdentity.Contains('\r')
            || state.PreviousObjectIdentity.Contains('\n')
            || state.PublishedDestinationObjectIdentity?.Contains('\r') == true
            || state.PublishedDestinationObjectIdentity?.Contains('\n') == true)
        {
            throw new ArgumentException(
                "The prepared publication state is invalid.",
                nameof(state));
        }
    }
}
