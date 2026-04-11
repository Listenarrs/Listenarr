namespace Listenarr.Domain.Models.Configurations
{
    public class CategoryConfiguration
    {
        public string Value 
        {
            get
            {
                return field.Trim() ?? string.Empty;
            }
            set
            {
                field = value?.Trim() ?? string.Empty;
            }
        }

        public CategoryConfiguration(string value)
        {
            Value = value;
        }

        public bool Matches(string? itemCategory)
        {
            if (string.IsNullOrEmpty(Value))
            {
                return true;
            }

            return string.Equals(
                Value,
                (itemCategory ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        public bool MatchesAny(IEnumerable<string?> itemCategories)
        {
            return itemCategories.Any(Matches);
        }

        override public string ToString()
        {
            return Value;
        }
    }
}