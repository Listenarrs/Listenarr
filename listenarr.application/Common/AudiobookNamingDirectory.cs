using System.Text.RegularExpressions;
namespace Listenarr.Application.Common;

public static class AudiobookNamingDirectory
{
    public static string Render(Audiobook audiobook, ApplicationSettings settings, IFileNamingService naming)
    {
        var metadata = audiobook.CreateBasicAudioMetadata();
        metadata.Artist = metadata.AlbumArtist = audiobook.Authors?.FirstOrDefault() ?? "Unknown Author";
        if (!string.IsNullOrWhiteSpace(settings.FolderNamingPattern))
        {
            var pattern = Regex.Replace(settings.FolderNamingPattern, @"\{(?:DiskNumber|ChapterNumber)[^}]*\}", string.Empty, RegexOptions.IgnoreCase);
            return naming.ApplyNamingPattern(pattern, metadata, false);
        }
        var filePattern = string.IsNullOrWhiteSpace(settings.FileNamingPattern)
            ? "{Author}/{Title}/{Title}" : settings.FileNamingPattern;
        var relative = naming.ApplyNamingPattern(filePattern, metadata, false);
        return Path.GetDirectoryName(relative) ?? string.Empty;
    }
}
