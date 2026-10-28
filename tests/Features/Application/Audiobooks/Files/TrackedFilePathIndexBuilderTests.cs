using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Files;

[Trait("Area", "AudiobookFiles")]
[Trait("Name", "TrackedFilePathIndexBuilderTests")]
[Trait("Category", "Application")]
public sealed class TrackedFilePathIndexBuilderTests : BaseTests
{
    [Fact]
    public void Build_RelativeAudiobookFilePath_ResolvesAgainstOwningBasePath()
    {
        var root = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"tracked-index-{Guid.NewGuid():N}"));
        var basePath = Path.Join(root, "Author", "Book");
        var relativePath = Path.Join("disc-1", "chapter.m4b");
        var audiobook = new Audiobook
        {
            Id = 42,
            BasePath = basePath
        };
        var file = AudiobookFile.CreateUnresolved(relativePath);
        file.AudiobookId = audiobook.Id;
        var semantics = FileSystemPathSemantics.CurrentHostDefault;

        var index = TrackedFilePathIndexBuilder.Build(
            [file],
            [audiobook],
            semantics);

        Assert.Contains(
            FileSystemPathIdentity.Canonicalize(
                Path.Join(basePath, relativePath),
                semantics.Syntax),
            index);
        Assert.DoesNotContain(
            FileSystemPathIdentity.Canonicalize(
                Path.GetFullPath(relativePath),
                semantics.Syntax),
            index);
    }

    [Fact]
    public void Build_CaseDistinctPaths_RespectBoundarySemantics()
    {
        var root = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"tracked-index-case-{Guid.NewGuid():N}"));
        var audiobook = new Audiobook { Id = 7, BasePath = root };
        var file = AudiobookFile.CreateUnresolved("Book.m4b");
        file.AudiobookId = audiobook.Id;
        var sensitive = new FileSystemPathSemantics(
            FileSystemPathSemantics.CurrentHostDefault.Syntax,
            FileSystemCaseSensitivity.Sensitive);
        var insensitive = new FileSystemPathSemantics(
            FileSystemPathSemantics.CurrentHostDefault.Syntax,
            FileSystemCaseSensitivity.Insensitive);

        var sensitiveIndex = TrackedFilePathIndexBuilder.Build([file], [audiobook], sensitive);
        var insensitiveIndex = TrackedFilePathIndexBuilder.Build([file], [audiobook], insensitive);
        var caseVariant = FileSystemPathIdentity.Canonicalize(
            Path.Join(root, "book.m4b"),
            sensitive.Syntax);

        Assert.DoesNotContain(caseVariant, sensitiveIndex);
        Assert.Contains(caseVariant, insensitiveIndex);
    }
}
