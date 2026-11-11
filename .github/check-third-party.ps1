# Checks that the DirectShowLib source Framejet ships matches the DirectShowLib it builds with.
# THIRD-PARTY-NOTICES.md names the version that third-party\DirectShowLib-source.zip holds. When
# Framejet.Platform.csproj moves to another version, replace the zip and update the notices together.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path (Join-Path $root 'third-party\DirectShowLib-source.zip'))) { throw 'third-party\DirectShowLib-source.zip is missing.' }

[xml]$project = Get-Content (Join-Path $root 'src\Framejet.Platform\Framejet.Platform.csproj')
$used = @($project.Project.ItemGroup.PackageReference | Where-Object { $_.Include -eq 'DirectShowLib' })[0].Version
$row = Select-String -Path (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Pattern '^\| DirectShowLib \| ([^ |]+) \|' | Select-Object -First 1
$noted = if ($row) { $row.Matches[0].Groups[1].Value } else { 'no version' }

if ($used -ne $noted) {
    throw "Framejet.Platform.csproj uses DirectShowLib $used, but THIRD-PARTY-NOTICES.md describes $noted. Put that version's source in third-party\DirectShowLib-source.zip, then update the notices."
}
"DirectShowLib $used matches THIRD-PARTY-NOTICES.md and its source zip."
