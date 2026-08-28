namespace Listenarr.Domain.Common;

/// <summary>
/// Controls whether opaque platform evidence may identify any pinned filesystem object.
/// </summary>
public enum FileSystemObjectIdentityTrustMode
{
    Auto,
    Trusted,
    Untrusted
}
