$ErrorActionPreference = 'Stop'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $compiler)) { $compiler = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $compiler)) { throw 'C# compiler not found.' }

$outputDir = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force $outputDir | Out-Null
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 /out:"$outputDir\SuishenLedger.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "$PSScriptRoot\Program.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$test = Start-Process -FilePath "$outputDir\SuishenLedger.exe" -ArgumentList '--self-test' -Wait -PassThru
if ($test.ExitCode -ne 0) { throw 'Self-test failed.' }
Write-Host "Built: $outputDir\SuishenLedger.exe"
