
namespace Listenarr.Tests.Builders
{
    public class AudibleAuthorBuilder
    {
        private readonly AudibleAuthor _author = new();

        public AudibleAuthorBuilder WithAsin(string value)
        {
            _author.Asin = value;
            return this;
        }

        public AudibleAuthorBuilder WithName(string value)
        {
            _author.Name = value;
            return this;
        }

        public AudibleAuthorBuilder WithRegion(string value)
        {
            _author.Region = value;
            return this;
        }

        public AudibleAuthor Build()
        {
            return _author;
        }
    }
}
