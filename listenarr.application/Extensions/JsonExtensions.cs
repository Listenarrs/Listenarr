namespace Listenarr.Application.Extensions
{
    /// <summary>
    /// Extension methods for System.Text.Json types
    /// </summary>
    public static class JsonExtensions
    {
        /// <summary>
        /// Gets a property value from a JsonElement, returning defaultValue if the property doesn't exist or is null.
        /// </summary>
        public static T GetPropertyOrDefault<T>(this System.Text.Json.JsonElement element, string propertyName, T defaultValue = default!)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<T>(prop.GetRawText()) ?? defaultValue;
                }
                catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
                {
                    return defaultValue;
                }
            }
            return defaultValue;
        }
    }
}
