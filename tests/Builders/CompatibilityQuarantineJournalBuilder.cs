using System.Security.Cryptography;
using System.Text;

namespace Listenarr.Tests.Builders;

internal sealed class CompatibilityQuarantineJournalBuilder(string root)
{
    public CompatibilityFilePublicationJournal Build()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("audio")));
        return new CompatibilityFilePublicationJournal
        {
            ProtocolVersion = CompatibilityFilePublicationProtocol.Current,
            RequestedAction = FileAction.Move,
            EffectiveAction = FileAction.Copy,
            SourcePath = Path.Join(root, "source.m4b"),
            DestinationPath = Path.Join(root, "destination.m4b"),
            QuarantinePath = Path.Join(root, "quarantine", "source.m4b"),
            SourceLength = 5,
            SourceSha256 = hash,
            TargetLength = 5,
            TargetSha256 = hash,
            IsCompanionFile = true,
            CleanupOwner = CompatibilityCleanupOwner.Listenarr,
            State = CompatibilityFilePublicationState.SourceQuarantined
        };
    }
}
