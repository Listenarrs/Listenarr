namespace Listenarr.Tests.Builders
{
    public class QualityProfileBuilder
    {
        private static int IdCounter = 0;
        private QualityProfile _qualityProfile = new();

        public QualityProfileBuilder()
        {
            _qualityProfile.Id = ++IdCounter;
        }

        public QualityProfileBuilder WithName(string value)
        {
            _qualityProfile.Name = value;
            return this;
        }

        public QualityProfile Build()
        {
            return _qualityProfile;
        }
    }
}
