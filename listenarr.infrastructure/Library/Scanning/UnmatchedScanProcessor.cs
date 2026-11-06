/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning
{
    public partial class UnmatchedScanProcessor : IUnmatchedScanProcessor
    {
        private static readonly string[] AudioExtensions = { ".m4b", ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".wav" };
        private sealed record StemGroup(string Stem, List<string> Files);
        private sealed record GroupCandidate(string FilePath, string Stem, bool IsAncillary, string TitleKey, string AuthorKey);
        private sealed record UnmatchedScanOutcome(
            List<UnmatchedFileResult> Results,
            List<string> Warnings);

        private readonly IUnmatchedScanQueueService _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<UnmatchedScanProcessor> _logger;
        private readonly IHubContext<SettingsHub> _hubContext;
        private readonly IFfmpegService _ffmpegService;
        private readonly IFileSystemSemanticsResolver _semanticsResolver;

        public UnmatchedScanProcessor(
            IUnmatchedScanQueueService queue,
            IServiceScopeFactory scopeFactory,
            ILogger<UnmatchedScanProcessor> logger,
            IHubContext<SettingsHub> hubContext,
            IFfmpegService ffmpegService,
            IFileSystemSemanticsResolver semanticsResolver)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _hubContext = hubContext;
            _ffmpegService = ffmpegService;
            _semanticsResolver = semanticsResolver;
        }
    }
}
