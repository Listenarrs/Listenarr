using Listenarr.Domain.Models.Enumerations;

namespace Listenarr.Tests.Builders
{
    public class ApplicationSettingsBuilder
    {
        private readonly ApplicationSettings _applicationSettings = new();
        private List<string> _importBlacklistExtensions = [];

        public ApplicationSettingsBuilder WithMoveFileOnCompleted()
        {
            _applicationSettings.CompletedFileAction = FileAction.Move;
            return this;
        }

        public ApplicationSettingsBuilder WithCopyFileOnCompleted()
        {
            _applicationSettings.CompletedFileAction = FileAction.Copy;
            return this;
        }

        public ApplicationSettingsBuilder WithoutFileAction()
        {
            _applicationSettings.CompletedFileAction = FileAction.None;
            return this;
        }

        public ApplicationSettingsBuilder WithFileNamingPattern(string value)
        {
            _applicationSettings.FileNamingPattern = value;
            return this;
        }

        public ApplicationSettingsBuilder WithMetadataProcessing()
        {
            _applicationSettings.EnableMetadataProcessing = true;
            return this;
        }

        public ApplicationSettingsBuilder WithoutMetadataProcessing()
        {
            _applicationSettings.EnableMetadataProcessing = false;
            return this;
        }

        public ApplicationSettingsBuilder WithOutputPath(string value)
        {
            _applicationSettings.OutputPath = value;
            return this;
        }

        public ApplicationSettingsBuilder WithoutOutputPath()
        {
            _applicationSettings.OutputPath = string.Empty;
            return this;
        }

        public ApplicationSettingsBuilder WithExtractArchive()
        {
            _applicationSettings.ExtractArchives = true;
            return this;
        }

        public ApplicationSettingsBuilder WithoutExtractArchive()
        {
            _applicationSettings.ExtractArchives = false;
            return this;
        }

        public ApplicationSettingsBuilder WithImportBlacklistExtension(string value)
        {
            _importBlacklistExtensions.Add(value);
            return this;
        }

        public ApplicationSettingsBuilder WithoutImportBlacklistExtensions()
        {
            _importBlacklistExtensions = [];
            return this;
        }

        public ApplicationSettingsBuilder WithMultiFileNamingPattern(string value)
        {
            _applicationSettings.MultiFileNamingPattern = value;
            return this;
        }

        public ApplicationSettingsBuilder WithFolderNamingPattern(string value)
        {
            _applicationSettings.FolderNamingPattern = value;
            return this;
        }

        public ApplicationSettings Build()
        {
            _applicationSettings.ImportBlacklistExtensions = _importBlacklistExtensions;

            return _applicationSettings;
        }
    }
}
