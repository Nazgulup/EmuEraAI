param([string]$Runtime='android-arm64')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'portable\env.ps1')
$project=Join-Path $PSScriptRoot 'portable\era-core\EraCore.Maui\EraCore.Maui.csproj'
$web=Join-Path $PSScriptRoot 'portable\era-core\EraCore.Web'
$assets=Join-Path $PSScriptRoot 'portable\era-core\EraCore.Maui\wwwroot'
$publish=Join-Path $PSScriptRoot "android-dist\$Runtime"
$previewKey=Join-Path $env:ANDROID_USER_HOME 'eraai-preview.keystore'
New-Item -ItemType Directory -Force -Path $env:ANDROID_USER_HOME | Out-Null
Push-Location $web
try {
    if (!(Test-Path 'node_modules\vue-tsc')) {
        & npm.cmd ci --ignore-scripts --no-audit --no-fund --registry=https://registry.npmjs.org
        if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency restore failed.' }
    }
    & npm.cmd run build -- --base=./ --outDir=dist-maui
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
    New-Item -ItemType Directory -Force -Path $assets | Out-Null
    Copy-Item -Path '.\dist-maui\*' -Destination $assets -Recurse -Force
    $licenses=Join-Path $assets 'licenses'
    New-Item -ItemType Directory -Force -Path $licenses | Out-Null
    Copy-Item -Path '..\Readme\License\*' -Destination $licenses -Recurse -Force
    Copy-Item -Path '.\src\assets\fonts\*.txt' -Destination $licenses -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'portable\UPSTREAM.txt') -Destination $licenses
} finally { Pop-Location }
& dotnet publish $project -f net10.0-android -c Release -r $Runtime -m:1 --disable-build-servers -p:EraAiAndroidOnly=true -p:RunAOTCompilation=false -p:PublishTrimmed=false -p:AndroidPackageFormats=apk -p:EmbedAssembliesIntoApk=true -p:SkipVueBuild=true "-p:_ApkDebugKeyStore=$previewKey" "-p:AndroidSdkDirectory=$env:ANDROID_HOME" "-p:JavaSdkDirectory=$env:JAVA_HOME" "-p:PublishDir=$publish\"
if ($LASTEXITCODE -ne 0) { throw 'Android publish failed.' }
$apk=Get-ChildItem -LiteralPath $publish -Filter '*-Signed.apk' | Select-Object -First 1
if (!$apk) { throw 'Signed APK was not produced.' }
Copy-Item -LiteralPath $apk.FullName -Destination (Join-Path $PSScriptRoot 'android-dist\ERA-AI-Loader.apk')
Write-Host 'Built android-dist\ERA-AI-Loader.apk'
