$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$output=Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $output | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'src\Emuera\bin\Release\Emuera.exe') -Destination (Join-Path $output 'Emuera.exe')
Get-ChildItem -LiteralPath (Join-Path $root 'native') -Filter '*.dll' | Copy-Item -Destination $output
$refs=Join-Path $root '.tools\net45\build\.NETFramework\v4.5'
$compiler=Join-Path $root '.tools\compiler\tasks\net472\csc.exe'
$argsList=@('/nologo','/target:exe','/langversion:7.3',('/out:'+(Join-Path $output 'EmueraAI.Tests.exe')),('/reference:'+(Join-Path $output 'Emuera.exe')))
foreach($name in @('mscorlib','System','System.Core','System.Drawing','System.Windows.Forms','System.Net.Http','System.Web.Extensions')) { $argsList+=('/reference:'+(Join-Path $refs ($name+'.dll'))) }
$argsList+=(Join-Path $PSScriptRoot 'CoreTests.cs')
& $compiler @argsList
if($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& (Join-Path $output 'EmueraAI.Tests.exe') $root
exit $LASTEXITCODE
