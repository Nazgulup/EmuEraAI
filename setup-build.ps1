$ErrorActionPreference = 'Stop'
$toolRoot = Join-Path $PSScriptRoot '.tools'
New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null
$packages = @(
    @{ Name='compiler'; Id='microsoft.net.compilers.toolset'; Version='4.14.0'; Hash='941a9cf3ea618d88d01a3dd6b1a45a06bcf07716a9f81ce4031caa3edd24a845' },
    @{ Name='net45'; Id='microsoft.netframework.referenceassemblies.net45'; Version='1.0.3'; Hash='23a9f94ea3e2cb88cd8341af75b811c6fb5cb82516fc696e95ed4620279128e3' }
)
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($package in $packages) {
    $archive = Join-Path $toolRoot ($package.Name + '.nupkg')
    if (!(Test-Path -LiteralPath $archive)) {
        $url = 'https://api.nuget.org/v3-flatcontainer/{0}/{1}/{0}.{1}.nupkg' -f $package.Id,$package.Version
        & curl.exe -fL --connect-timeout 20 --max-time 300 -o $archive $url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $($package.Id)" }
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $package.Hash) { throw "Hash mismatch: $archive" }
    $destination = Join-Path $toolRoot $package.Name
    if (!(Test-Path -LiteralPath $destination)) { [IO.Compression.ZipFile]::ExtractToDirectory($archive,$destination) }
}
Write-Host 'Project-local compiler and reference assemblies ready.'
