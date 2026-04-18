public class DownloadClientException : Exception
{
    private Uri? Uri {get; set;} = null;

    public DownloadClientException ()
    {}

    public DownloadClientException (string message, Exception? innerException = null) 
        : base(message, innerException)
    {}

    public DownloadClientException (string message, Uri? uri, Exception? innerException = null) 
        : base(message, innerException)
    {
        Uri = uri;
    }

    override public string ToString()
    {
        if (Uri != null)
        {
            return $"Error on {Uri}: {Message}";
        }

        return Message;
    }
}