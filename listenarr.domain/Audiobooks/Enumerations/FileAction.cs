using System.Text.Json.Serialization;

namespace Listenarr.Domain.Audiobooks.Enumerations
{
    /// <summary>
    /// Represents action that can be taken on files
    /// This acts as a DTO too for the API layer
    /// </summary>
    public enum FileAction
    {
        [JsonStringEnumMemberName("none")]
        None = 0,
        [JsonStringEnumMemberName("move")]
        Move = 1,
        [JsonStringEnumMemberName("copy")]
        Copy = 2,
        [JsonStringEnumMemberName("hardlink/copy")]
        HardlinkCopy = 3
    }
}
