$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$privateTools=Join-Path $root '.tools'
New-Item -ItemType Directory -Force -Path $privateTools,(Join-Path $privateTools 'temp'),(Join-Path $privateTools 'nuget-feed') | Out-Null
$versions=Get-Content (Join-Path $root 'portable\toolchain.lock.json') -Raw | ConvertFrom-Json
function Get-VerifiedArchive($Url,$Path,$Algorithm,$Hash) {
    if (!(Test-Path -LiteralPath $Path) -or (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -ne $Hash) {
        & curl.exe -fL --retry 3 --connect-timeout 30 --output $Path $Url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $Url" }
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -ne $Hash) { throw "Publisher checksum mismatch: $Path" }
}
function Expand-VerifiedArchive($Archive,$Destination,[switch]$StripRoot) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $base=[IO.Path]::GetFullPath($Destination).TrimEnd('\')+'\'
    $zip=[IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            if (!$entry.Name) { continue }
            $relative=$entry.FullName
            if ($StripRoot) { $relative=$relative.Substring($relative.IndexOf('/')+1) }
            $target=[IO.Path]::GetFullPath((Join-Path $base $relative))
            if (!$target.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid archive path.' }
            New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$true)
        }
    } finally { $zip.Dispose() }
}
if (!(Test-Path (Join-Path $privateTools 'dotnet\sdk\10.0.404'))) {
    $archive=Join-Path $privateTools 'dotnet-sdk.zip'
    Get-VerifiedArchive $versions.dotnet.url $archive SHA512 $versions.dotnet.sha512
    Expand-VerifiedArchive $archive (Join-Path $privateTools 'dotnet')
}
if (!(Test-Path (Join-Path $privateTools 'jdk\bin\java.exe'))) {
    $archive=Join-Path $privateTools 'jdk-verified.zip'
    Get-VerifiedArchive $versions.jdk.url $archive SHA256 $versions.jdk.sha256
    Expand-VerifiedArchive $archive (Join-Path $privateTools 'jdk') -StripRoot
}
. (Join-Path $root 'portable\env.ps1')
& dotnet workload install maui-android --skip-manifest-update --configfile (Join-Path $root 'portable\NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'MAUI Android workload installation failed.' }
$sdkFiles=@('platforms\android-36\android.jar','build-tools\36.0.0\aapt2.exe','build-tools\36.0.0\apksigner.bat','platform-tools\adb.exe','cmdline-tools\19.0\bin\sdkmanager.bat')
$sdkMissing=@($sdkFiles | Where-Object { !(Test-Path (Join-Path $env:ANDROID_HOME $_)) })
if ($sdkMissing.Count -gt 0) {
    & dotnet build (Join-Path $root 'portable\era-core\EraCore.Maui\EraCore.Maui.csproj') -t:InstallAndroidDependencies -f net10.0-android -p:EraAiAndroidOnly=true -m:1 --disable-build-servers "-p:AndroidSdkDirectory=$env:ANDROID_HOME" "-p:JavaSdkDirectory=$env:JAVA_HOME" -p:AndroidInstallJavaDependencies=false -p:AcceptAndroidSDKLicenses=True -p:SkipVueBuild=true -p:AndroidDependencyInstallationTimeout=60
    if ($LASTEXITCODE -ne 0) { throw 'Android SDK dependency installation failed.' }
}
Write-Host 'Android build tools are ready. Run build-android.ps1.'
