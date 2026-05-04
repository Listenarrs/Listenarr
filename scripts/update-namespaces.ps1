# Replace namespaces after moving Models and Migrations
# Run from repo root: .\scripts\update-namespaces.ps1
# This script performs targeted, reversible text replacements. Review before running.
# It will:
#  - Change model namespaces from Listenarr.Api.Models -> Listenarr.Domain.Models (in listenarr.domain/Models)
#  - Replace using Listenarr.Api.Models -> using Listenarr.Domain.Models across the repo
#  - Update the Infrastructure DbContext namespace to Listenarr.Infrastructure.Models
#  - Update migration namespaces from Listenarr.Api.Migrations -> Listenarr.Infrastructure.Migrations
#
# Note: This script edits files in-place. It's recommended to run in a clean git working tree so you can inspect & commit changes.

Set-StrictMode -Version Latest

Write-Host "Updating model file namespaces in listenarr.domain/Models..."
Get-ChildItem -Path .\listenarr.domain\Models -Filter *.cs -File -Recurse | ForEach-Object {
    (Get-Content -Raw -LiteralPath $_.FullName) -replace 'namespace\s+Listenarr\.Api\.Models', 'namespace Listenarr.Domain.Models' |
        Set-Content -LiteralPath $_.FullName -Encoding UTF8
    Write-Host "Updated namespace in $($_.FullName)"
}

Write-Host "Updating using directives across repository: Listenarr.Api.Models -> Listenarr.Domain.Models"
Get-ChildItem -Path . -Filter *.cs -File -Recurse | ForEach-Object {
    $path = $_.FullName
    $content = Get-Content -Raw -LiteralPath $path
    if ($content -match 'using\s+Listenarr\.Api\.Models') {
        $new = $content -replace 'using\s+Listenarr\.Api\.Models', 'using Listenarr.Domain.Models'
        Set-Content -LiteralPath $path -Value $new -Encoding UTF8
        Write-Host "Replaced using in $path"
    }
}

Write-Host "Updating DbContext namespace in listenarr.infrastructure/Models/ListenArrDbContext.cs..."
$dbcPath = ".\listenarr.infrastructure\Models\ListenArrDbContext.cs"
if (Test-Path $dbcPath) {
    $dbcContent = Get-Content -Raw -LiteralPath $dbcPath
    # Ensure model types are referenced from Listenarr.Domain.Models via using
    if ($dbcContent -notmatch 'using Listenarr.Domain.Models') {
        $dbcContent = "using Listenarr.Domain.Models`r`n" + $dbcContent
    }
    # Replace whatever namespace it has now to the infrastructure namespace
    $dbcContent = $dbcContent -replace 'namespace\s+Listenarr\.Api\.Models', 'namespace Listenarr.Infrastructure.Models'
    $dbcContent = $dbcContent -replace 'namespace\s+Listenarr\.Domain\.Models', 'namespace Listenarr.Infrastructure.Models'
    Set-Content -LiteralPath $dbcPath -Value $dbcContent -Encoding UTF8
    Write-Host "Updated DbContext namespace in $dbcPath"
} else {
    Write-Host "DbContext file not found at $dbcPath - skipping"
}

Write-Host "Updating migration namespaces in listenarr.infrastructure/Migrations..."
Get-ChildItem -Path .\listenarr.infrastructure\Migrations -Filter *.cs -File -Recurse | ForEach-Object {
    $p = $_.FullName
    $c = Get-Content -Raw -LiteralPath $p
    if ($c -match 'namespace\s+Listenarr\.Api\.Migrations') {
        $new = $c -replace 'namespace\s+Listenarr\.Api\.Migrations', 'namespace Listenarr.Infrastructure.Persistence.Migrations'
        Set-Content -LiteralPath $p -Value $new -Encoding UTF8
        Write-Host "Updated migration namespace in $p"
    }
}

Write-Host "Namespace updates complete. Please run 'git status' to review changes, then 'dotnet build' to verify."
