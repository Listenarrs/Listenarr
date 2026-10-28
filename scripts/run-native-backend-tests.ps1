[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$requiredCapabilities = $env:LISTENARR_REQUIRED_NATIVE_TEST_CAPABILITIES
if ([string]::IsNullOrWhiteSpace($requiredCapabilities)) {
    throw 'LISTENARR_REQUIRED_NATIVE_TEST_CAPABILITIES must declare the native capabilities required by this CI job.'
}

Write-Host "Required native test capabilities: $requiredCapabilities"
Write-Host "Runner OS: $env:RUNNER_OS"
Write-Host "Runner architecture: $env:RUNNER_ARCH"
Write-Host "Runner image: $env:ImageOS $env:ImageVersion"

$readOnlySourceRoot = $null
$readOnlyMountRoot = $null
$readOnlyMountActive = $false
$exitCode = 0

try {
    if ($IsLinux) {
        $mountId = [Guid]::NewGuid().ToString('N')
        $readOnlySourceRoot = Join-Path ([IO.Path]::GetTempPath()) "listenarr-readonly-source-$mountId"
        $readOnlyMountRoot = Join-Path ([IO.Path]::GetTempPath()) "listenarr-readonly-mount-$mountId"
        $bookDirectory = Join-Path $readOnlySourceRoot 'Author/Book B012345678'
        New-Item -ItemType Directory -Path $bookDirectory -Force | Out-Null
        New-Item -ItemType Directory -Path $readOnlyMountRoot -Force | Out-Null
        [IO.File]::WriteAllText(
            (Join-Path $bookDirectory '01.m4b'),
            'audio')

        & sudo mount --bind $readOnlySourceRoot $readOnlyMountRoot
        if ($LASTEXITCODE -ne 0) {
            throw "Could not create the native read-only validation bind mount."
        }
        $readOnlyMountActive = $true

        & sudo mount -o remount,bind,ro $readOnlySourceRoot $readOnlyMountRoot
        if ($LASTEXITCODE -ne 0) {
            throw "Could not remount the native validation bind mount read-only."
        }

        $mountOptions = (& findmnt -no OPTIONS --target $readOnlyMountRoot | Out-String).Trim()
        if (($mountOptions -split ',') -notcontains 'ro') {
            throw "Expected a read-only bind mount, got: $mountOptions"
        }

        $env:LISTENARR_READONLY_LIBRARY_PATH = $readOnlyMountRoot
        Write-Host "Read-only scan validation mount: $readOnlyMountRoot"
    }

    $preflightFilter = 'FullyQualifiedName=Listenarr.Tests.Features.Architecture.NativeTestCapabilityContractTests.RequiredNativeTestCapabilities_AreAvailable'
    & dotnet test tests/Listenarr.Tests.csproj `
        -c Release `
        --no-build `
        --filter $preflightFilter `
        --logger 'console;verbosity=normal'
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        & dotnet test listenarr.slnx `
            -c Release `
            --no-build `
            --logger 'console;verbosity=normal'
        $exitCode = $LASTEXITCODE
    }

    if ($exitCode -eq 0 -and $readOnlySourceRoot) {
        $artifacts = @(
            Get-ChildItem -LiteralPath $readOnlySourceRoot -Force -Recurse |
                Where-Object { $_.Name.StartsWith('.listenarr', [StringComparison]::OrdinalIgnoreCase) })
        if ($artifacts.Count -gt 0) {
            Write-Error "Read-only scan validation found Listenarr filesystem artifacts: $($artifacts.FullName -join ', ')"
            $exitCode = 1
        }
    }
}
finally {
    Remove-Item Env:LISTENARR_READONLY_LIBRARY_PATH -ErrorAction SilentlyContinue
    if ($readOnlyMountActive -and $readOnlyMountRoot) {
        & sudo umount $readOnlyMountRoot
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Could not unmount read-only validation path $readOnlyMountRoot."
        }
    }
    if ($readOnlyMountRoot) {
        Remove-Item -LiteralPath $readOnlyMountRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($readOnlySourceRoot) {
        Remove-Item -LiteralPath $readOnlySourceRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

exit $exitCode
