# Audit write sites that may persist JSON-backed audiobook fields
Param(
    [string]$Out = "scripts/write_audit_results.txt"
)

Write-Host "Scanning repository for write-site patterns..."

$patterns = @(
    'Isbn\s*=',
    'Authors\s*=',
    'AuthorAsins\s*=',
    'Narrators\s*=',
    'Genres\s*=',
    'Tags\s*='
)

Remove-Item -Path $Out -ErrorAction SilentlyContinue

foreach ($p in $patterns) {
    Write-Host "Pattern: $p"
    Select-String -Path "**/*.*" -Pattern $p -SimpleMatch -NotMatch:$false | ForEach-Object {
        $line = "${($_.Path)}:${($_.LineNumber)}: $($_.Line.Trim())"
        Add-Content -Path $Out -Value $line
    }
}

Write-Host "Audit complete. Results written to $Out"
