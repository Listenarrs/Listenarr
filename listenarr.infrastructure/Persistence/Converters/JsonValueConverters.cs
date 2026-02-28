using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Listenarr.Infrastructure.Persistence.Converters
{
    internal static class JsonConverterHelpers
    {
        // Safe serializer: returns empty string for null values so expression lambdas remain simple.
        public static string SerializeObject<T>(T? value) =>
            value == null
                ? string.Empty
                : JsonSerializer.Serialize(value);

        // Deserialize into a non-null instance when possible. Internal implementation can be statement-bodied
        // because lambdas will call this single helper method (keeps the expression tree lambdas simple).
        public static T DeserializeObjectOrNew<T>(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                try { return Activator.CreateInstance<T>()!; }
                catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) { return default!; }
            }

            // Quick heuristic: check first non-whitespace character to avoid attempting
            // JSON deserialization on clearly non-JSON values (e.g., legacy single-letter
            // flags or database placeholders). Valid JSON values start with '{', '[' or
            // '"' (string), digits, '-', or the literals 't','f','n'. If the value does
            // not look like JSON, return a new instance without attempting to deserialize.
            var trimmed = json.TrimStart();
            if (trimmed.Length == 0)
            {
                try { return Activator.CreateInstance<T>()!; }
                catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException) { return default!; }
            }

            var first = trimmed[0];
            if (first != '{' && first != '[' && first != '"' && first != 't' && first != 'f' && first != 'n' && first != '-' && !char.IsDigit(first))
            {
                try { return Activator.CreateInstance<T>()!; }
                catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException) { return default!; }
            }

            try
            {
                // If T is a collection of strings, be permissive: allow a primitive
                // JSON value (number/string) or a JSON string to be treated as a
                // single-item array. This helps tolerate legacy DB rows while we
                // normalize storage.
                if (typeof(T).IsGenericType && typeof(T).GetGenericArguments().Length == 1 &&
                    typeof(T).GetGenericArguments()[0] == typeof(string))
                {
                    // If the JSON is not an array or object, try wrapping it.
                    if (first != '[' && first != '{')
                    {
                        try
                        {
                            string wrappedJson;
                            if (first == '"')
                            {
                                // Deserialize the single JSON string and re-serialize as array
                                var single = JsonSerializer.Deserialize<string>(json);
                                wrappedJson = JsonSerializer.Serialize(new[] { single ?? string.Empty });
                            }
                            else
                            {
                                // Treat numeric or bare token as string and wrap
                                var raw = trimmed;
                                wrappedJson = JsonSerializer.Serialize(new[] { raw });
                            }

                            var desWrapped = JsonSerializer.Deserialize<T>(wrappedJson);
                            if (desWrapped != null) return desWrapped;
                        }
                        catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException) {
                            // Fall through to the default attempt below
                                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                        }
                    }
                }

                var des = JsonSerializer.Deserialize<T>(json);
                if (des != null) return des;
            }
            catch (Exception caughtEx_5) when (caughtEx_5 is not OperationCanceledException && caughtEx_5 is not OutOfMemoryException && caughtEx_5 is not StackOverflowException) {
                // Ignore deserialization errors and fall back to creating new instance
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }

            try { return Activator.CreateInstance<T>()!; }
            catch (Exception caughtEx_6) when (caughtEx_6 is not OperationCanceledException && caughtEx_6 is not OutOfMemoryException && caughtEx_6 is not StackOverflowException) { return default!; }
        }
    }

    public class JsonValueConverter<T> : ValueConverter<T, string>
    {
        public JsonValueConverter()
            : base(
                v => JsonConverterHelpers.SerializeObject(v),
                s => JsonConverterHelpers.DeserializeObjectOrNew<T>(s))
        {
        }
    }

    public static class JsonValueComparer
    {
        public static ValueComparer<T> Create<T>() =>
            new ValueComparer<T>(
                (a, b) => JsonConverterHelpers.SerializeObject(a) == JsonConverterHelpers.SerializeObject(b),
                v => JsonConverterHelpers.SerializeObject(v).GetHashCode(),
                v => JsonConverterHelpers.DeserializeObjectOrNew<T>(JsonConverterHelpers.SerializeObject(v)));
    }
}
