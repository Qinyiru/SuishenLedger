$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $compiler)) { $compiler = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $compiler)) { throw 'C# compiler not found.' }

New-Item -ItemType Directory -Force $dist | Out-Null
$exe = Join-Path $dist 'SuishenLedger.exe'
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 /out:$exe `
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll `
  /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll (Join-Path $root 'Program.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$test = Start-Process -FilePath $exe -ArgumentList '--self-test' -Wait -PassThru
if ($test.ExitCode -ne 0) { throw 'Self-test failed.' }

$version = [Reflection.AssemblyName]::GetAssemblyName($exe).Version.ToString(3)
$packageRoot = Join-Path $dist 'packages'
$staging = Join-Path $packageRoot ('SuishenLedger-v' + $version)
$zip = Join-Path $packageRoot ('SuishenLedger-v' + $version + '-trial.zip')
if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
New-Item -ItemType Directory -Force $staging | Out-Null
Copy-Item $exe (Join-Path $staging 'SuishenLedger.exe')
Copy-Item (Join-Path $root 'README.md') (Join-Path $staging 'README.md')
$hash = (Get-FileHash (Join-Path $staging 'SuishenLedger.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path (Join-Path $staging 'SuishenLedger.exe.sha256') -Value $hash -Encoding ASCII -NoNewline
Compress-Archive -Path (Get-ChildItem -LiteralPath $staging -File | Select-Object -ExpandProperty FullName) -DestinationPath $zip -CompressionLevel Optimal
Remove-Item -LiteralPath $staging -Recurse -Force
Write-Host "Package: $zip"
