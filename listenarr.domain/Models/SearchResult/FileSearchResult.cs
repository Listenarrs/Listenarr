namespace Listenarr.Domain.Models
{
    /// <summary>
    /// Allows search result to contain a list of files and attributes
    /// </summary>
    public abstract class FileSearchResult : BaseSearchResult
    {
        public int FileCount { get; set; }
        public long Size { get; set; }
        public FileResult[] Files 
        {
            get;
            set
            {
                field = value;

                // If we provide file informations: Override previously known file count
                if (value.Length > 0)
                {
                    FileCount = value.Length;
                }
                
                // If we provide size informations: Override previously known size
                if (value.Select(file => file.Size).Any(size => size != 0))
                {
                    Size = value.Sum(file => file.Size);
                }
            }
        } = [];
    }

    /// <summary>
    /// Base class for all search results with common properties
    /// </summary>
    public class FileResult
    {
        public string Filename { get; set; } = string.Empty;
        public long Size { get; set; }
    }

}