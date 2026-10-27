using System.Buffers.Binary;
using System.Net;
using Listenarr.Infrastructure.Images.Validation;
using Listenarr.Tests.Common;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Listenarr.Tests.Features.Infrastructure.Images.Validation;

[Trait("Name", "ImageSharpCoverImageProbeTests")]
[Trait("Category", "Images")]
public class ImageSharpCoverImageProbeTests : BaseTests
{
    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("gif")]
    [InlineData("webp")]
    public async Task ProbeAsync_OrdinaryCover_ReturnsDecodedDimensions(string format)
    {
        // Given an actual encoded cover, rather than a mocked decoder.
        using var image = new Image<Rgba32>(32, 48);
        IImageEncoder encoder = format switch
        {
            "png" => new PngEncoder(),
            "jpeg" => new JpegEncoder(),
            "gif" => new GifEncoder(),
            "webp" => new WebpEncoder(),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        using var api = new BaseApiMock();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(stream.ToArray())
        };
        using var httpClient = new HttpClient(api, disposeHandler: false);
        ConfigureProbe(api, httpClient, response);

        // When the application probe fetches and decodes the cover.
        var dimensions = await _provider.GetRequiredService<ICoverImageProbe>()
            .ProbeAsync("https://covers.example/cover");

        // Then every supported format preserves its dimensions.
        Assert.Equal(new ImageDimensions(32, 48), dimensions);
        Assert.Equal(1, api.GetCallCount());
    }

    [Theory(Timeout = 5000)]
    [InlineData(1UL)]
    [InlineData(5_000_000_000UL)]
    public async Task ProbeAsync_TruncatedBigTiff_ReturnsWithoutDecoderStall(ulong entryCount)
    {
        // Given the GHSA-wmxv-xphr-5c9g header, with ordinary and oversized IFD counts.
        var bytes = new byte[24];
        bytes[0] = 0x49;
        bytes[1] = 0x49;
        bytes[2] = 0x2B;
        bytes[4] = 0x08;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 16);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), entryCount);
        using var api = new BaseApiMock();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        using var httpClient = new HttpClient(api, disposeHandler: false);
        ConfigureProbe(api, httpClient, response);

        // When malformed remote bytes reach the real ImageSharp decoder.
        var dimensions = await _provider.GetRequiredService<ICoverImageProbe>()
            .ProbeAsync("https://covers.example/cover");

        // Then the probe retains its failure contract without scanning billions of entries.
        Assert.Null(dimensions);
        Assert.Equal(1, api.GetCallCount());
    }

    private void ConfigureProbe(BaseApiMock api, HttpClient httpClient, HttpResponseMessage response)
    {
        api.AddRoute("^https://covers\\.example/cover$", (_, _) => Task.FromResult(response));
        Init(services => services
            .WithSingleton(httpClient)
            .WithSingleton<ICoverImageProbe, ImageSharpCoverImageProbe>());
    }
}
