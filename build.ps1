param([switch]$Test,[ValidatePattern('^[A-Za-z0-9._-]+\.exe$')][string]$BinaryName='EmueraAI.exe')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:TEMP = Join-Path $root '.tools\temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP,(Join-Path $root 'dist'),(Join-Path $root 'logs') | Out-Null
$compiler = Join-Path $root '.tools\compiler\tasks\net472'
$refs = Join-Path $root '.tools\net45\build\.NETFramework\v4.5'
if (!(Test-Path (Join-Path $compiler 'csc.exe')) -or !(Test-Path (Join-Path $refs 'mscorlib.dll'))) {
    throw 'Build dependencies missing. Run setup-build.ps1 first.'
}
$msbuild = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
& $msbuild (Join-Path $root 'src\Emuera\Emuera.csproj') /t:Build /p:Configuration=Release "/p:CscToolPath=$compiler" "/p:FrameworkPathOverride=$refs" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Emuera build failed.' }
Copy-Item -LiteralPath (Join-Path $root 'src\Emuera\bin\Release\Emuera.exe') -Destination (Join-Path $root "dist\$BinaryName")
Copy-Item -LiteralPath (Join-Path $root 'EmueraAI.exe.config') -Destination (Join-Path $root "dist\$BinaryName.config")
Copy-Item -LiteralPath (Join-Path $root 'LICENSE.Emuera.txt') -Destination (Join-Path $root 'dist\LICENSE.Emuera.txt')
foreach ($file in @('README.md','CHANGES.txt','EmueraAI.exe.config','Start-Demo.cmd','Start-Loader.cmd')) {
    Copy-Item -LiteralPath (Join-Path $root $file) -Destination (Join-Path $root 'dist')
}
foreach ($folder in @('demo','presets','native')) {
    Get-ChildItem -LiteralPath (Join-Path $root $folder) -File -Recurse | ForEach-Object {
        $relative = $_.FullName.Substring((Join-Path $root $folder).Length + 1)
        $destination = if ($folder -eq 'native') { Join-Path (Join-Path $root 'dist') $relative } else { Join-Path (Join-Path $root "dist\$folder") $relative }
        New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination
    }
}
if ($Test) { & (Join-Path $root 'tests\run.ps1'); if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' } }
Write-Host "Built dist\$BinaryName"
