namespace Listenarr.Domain.Configuration
{
    /// <summary>
    /// The single vocabulary of webhook trigger names.
    /// </summary>
    /// <remarks>
    /// Trigger names are stored as free text on <see cref="WebhookConfiguration.Triggers"/> and on
    /// <see cref="ApplicationSettings.EnabledNotificationTriggers"/>, and they are compared against the
    /// name a dispatch site passes. Before this type existed the two ends of that comparison were
    /// written independently, drifted apart, and an ordinal <c>Contains</c> made the mismatch silent.
    /// Every subscription check now goes through <see cref="IsEnabled"/> so there is one place where
    /// the vocabulary is defined and one rule for how names are compared.
    /// </remarks>
    public static class NotificationTriggers
    {
        /// <summary>An audiobook was added to the library.</summary>
        public const string BookAdded = "book-added";

        /// <summary>A release was handed to a download client.</summary>
        public const string BookDownloading = "book-downloading";

        /// <summary>A scan found new files for a monitored audiobook.</summary>
        public const string BookAvailable = "book-available";

        /// <summary>A download finished processing and its files were imported into the library.</summary>
        public const string BookCompleted = "book-completed";

        /// <summary>A download failed or was blocked from importing.</summary>
        public const string DownloadFailed = "Failed";

        /// <summary>A library move job finished relocating an audiobook.</summary>
        public const string LibraryMoved = "Moved";

        /// <summary>A general system message.</summary>
        public const string SystemMessage = "System";

        /// <summary>
        /// The triggers a user can subscribe to from the notification settings screen, in the order
        /// the screen presents them.
        /// </summary>
        public static IReadOnlyList<string> UserSelectable { get; } =
        [
            BookAdded,
            BookDownloading,
            BookAvailable,
            BookCompleted
        ];

        /// <summary>
        /// Maps every accepted spelling to the canonical name it stands for. Names that only ever
        /// existed inside the dispatch code are kept here so webhook rows saved against them keep
        /// working.
        /// </summary>
        private static readonly Dictionary<string, string> CanonicalNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [BookAdded] = BookAdded,
                [BookDownloading] = BookDownloading,
                [BookAvailable] = BookAvailable,
                [BookCompleted] = BookCompleted,
                ["Imported"] = BookCompleted,
                [DownloadFailed] = DownloadFailed,
                [LibraryMoved] = LibraryMoved,
                [SystemMessage] = SystemMessage
            };

        /// <summary>
        /// Resolves a stored or dispatched trigger name to its canonical spelling. Unknown names are
        /// returned trimmed and otherwise unchanged so a hand-edited configuration still matches
        /// itself.
        /// </summary>
        public static string Canonicalize(string? trigger)
        {
            if (string.IsNullOrWhiteSpace(trigger))
            {
                return string.Empty;
            }

            var trimmed = trigger.Trim();
            return CanonicalNames.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
        }

        /// <summary>
        /// Reports whether a subscription list selects a dispatched trigger. Comparison is
        /// case-insensitive and alias-aware, matching how notification payloads already compare
        /// trigger names.
        /// </summary>
        public static bool IsEnabled(IEnumerable<string>? enabledTriggers, string? trigger)
        {
            if (enabledTriggers is null)
            {
                return false;
            }

            var wanted = Canonicalize(trigger);
            if (wanted.Length == 0)
            {
                return false;
            }

            foreach (var enabled in enabledTriggers)
            {
                if (string.Equals(Canonicalize(enabled), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
