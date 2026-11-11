# Checks that Framejet.exe works without its build folder. Copied alone into an empty folder, it must
# load every dll that the build put next to it from inside itself (its deps.<name>.dll resources).
# Run it in Windows PowerShell, which runs on .NET Framework like the app.
param([Parameter(Mandatory = $true)][string]$BuildFolder)
$ErrorActionPreference = 'Stop'

$dlls = @(Get-ChildItem (Join-Path $BuildFolder '*.dll'))
foreach ($own in 'Framejet.Core.dll', 'Framejet.Platform.dll') {
    if (-not ($dlls | Where-Object Name -eq $own)) { throw "$own is missing from $BuildFolder." }
}

$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$alone = Join-Path $tempRoot ('Framejet-alone-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $alone | Out-Null
Copy-Item (Join-Path $BuildFolder 'Framejet.exe') $alone

# Program's static constructor hooks the resolver that serves the embedded dlls. Running it starts nothing else.
$exe = [Reflection.Assembly]::LoadFrom((Join-Path $alone 'Framejet.exe'))
$program = $exe.GetType('Framejet.Program', $true)
[Runtime.CompilerServices.RuntimeHelpers]::RunClassConstructor($program.TypeHandle)

foreach ($dll in $dlls) {
    $name = [Reflection.AssemblyName]::GetAssemblyName($dll.FullName)
    # Load by name only: the AssemblyName read from the file carries its path, which would load the file itself.
    try { $loaded = [Reflection.Assembly]::Load($name.FullName) }
    catch { throw "$($dll.Name) is next to Framejet.exe but not inside it." }
    if ($loaded.GlobalAssemblyCache) { "OK  $($dll.Name) (Windows provides it)"; continue }
    if ($loaded.Location) { throw "$($dll.Name) loaded from $($loaded.Location), not from inside Framejet.exe." }
    "OK  $($dll.Name) $($name.Version)"
}
"Framejet.exe carries all $($dlls.Count) dlls."
