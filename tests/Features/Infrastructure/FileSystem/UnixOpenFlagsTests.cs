using Listenarr.Tests.Common;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "UnixOpenFlagsTests")]
[Trait("Category", "Infrastructure")]
public sealed class UnixOpenFlagsTests : BaseTests
{
    [Fact]
    public void GetLinuxDirectorySafetyFlags_UsesArm64ReleaseValues()
    {
        // Given / When
        var flags = UnixOpenFlags.GetLinuxDirectorySafetyFlags(
            RuntimeArchitecture.Arm64);

        // Then
        Assert.Equal(0x4000, flags.Directory);
        Assert.Equal(0x8000, flags.NoFollow);
    }

    [Fact]
    public void GetLinuxDirectorySafetyFlags_UsesX64ReleaseValues()
    {
        // Given / When
        var flags = UnixOpenFlags.GetLinuxDirectorySafetyFlags(
            RuntimeArchitecture.X64);

        // Then
        Assert.Equal(0x10000, flags.Directory);
        Assert.Equal(0x20000, flags.NoFollow);
    }

    [Theory]
    [InlineData(RuntimeArchitecture.X86)]
    [InlineData(RuntimeArchitecture.Arm)]
    [InlineData(RuntimeArchitecture.Armv6)]
    [InlineData(RuntimeArchitecture.Ppc64le)]
    [InlineData(RuntimeArchitecture.S390x)]
    [InlineData(RuntimeArchitecture.LoongArch64)]
    [InlineData(RuntimeArchitecture.RiscV64)]
    [InlineData(RuntimeArchitecture.Wasm)]
    public void GetLinuxDirectorySafetyFlags_RejectsArchitecturesListenarrDoesNotRelease(
        RuntimeArchitecture architecture)
    {
        // Given / When / Then
        Assert.Throws<PlatformNotSupportedException>(() =>
            UnixOpenFlags.GetLinuxDirectorySafetyFlags(architecture));
    }

    [Fact]
    public void OpenReadWriteNoFollow_AsksForReadAccessWhereOpenWriteDoesNot()
    {
        // Given / When
        var writeOnly = UnixOpenFlags.OpenWriteNoFollow();
        var readWrite = UnixOpenFlags.OpenReadWriteNoFollow();

        // Then: the access mode is the low two bits, and only that differs. Asserting the
        // difference rather than a literal keeps this honest on both released architectures,
        // since the noFollow bit is not the same value on arm64 and x64.
        Assert.Equal(1, writeOnly & 0x3);
        Assert.Equal(2, readWrite & 0x3);
        Assert.Equal(writeOnly & ~0x3, readWrite & ~0x3);
    }

    [Fact]
    public void EnsureMacOSArchitectureSupported_AcceptsReleasedX64Target()
    {
        // Given / When / Then
        UnixOpenFlags.EnsureMacOSArchitectureSupported(RuntimeArchitecture.X64);
    }

    [Theory]
    [InlineData(RuntimeArchitecture.X86)]
    [InlineData(RuntimeArchitecture.Arm)]
    [InlineData(RuntimeArchitecture.Arm64)]
    [InlineData(RuntimeArchitecture.Armv6)]
    [InlineData(RuntimeArchitecture.Ppc64le)]
    [InlineData(RuntimeArchitecture.S390x)]
    [InlineData(RuntimeArchitecture.LoongArch64)]
    [InlineData(RuntimeArchitecture.RiscV64)]
    [InlineData(RuntimeArchitecture.Wasm)]
    public void EnsureMacOSArchitectureSupported_RejectsArchitecturesListenarrDoesNotRelease(
        RuntimeArchitecture architecture)
    {
        // Given / When / Then
        Assert.Throws<PlatformNotSupportedException>(() =>
            UnixOpenFlags.EnsureMacOSArchitectureSupported(architecture));
    }
}
