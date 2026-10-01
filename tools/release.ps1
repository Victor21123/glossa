<#
.SYNOPSIS
  Builds the portable archive for a GitHub release: Glossa-<version>-win-x64.zip.
.DESCRIPTION
  The archive holds the Release build and a GlossaData folder beside Glossa.exe with what the app cannot download
  by itself yet: the OCR models, UniDic and CC-CEDICT. Glossa finds that folder and keeps all its data there.
  Dictionaries, level lists, the AI model and the llama.cpp engine are downloaded from inside the app.
  Needs the .NET 8 Desktop Runtime on the target PC.
  The version lives in one place, <Version> in Directory.Build.props, and the archive is named after it. -Version is
  optional: omitted, the props value is used; given and different, the script stops, so the archive name and the
  version the app reports (and compares with GitHub's latest release) can never disagree.
#>
param(
    [string]$Version,
    [string]$Data = 'D:\GlossaData'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot

$props = Join-Path $root 'Directory.Build.props'
$node = Select-Xml -Path $props -XPath '/Project/PropertyGroup/Version' | Select-Object -First 1
if (-not $node -or -not $node.Node.InnerText.Trim()) { throw "No <Version> in $props" }
$declared = $node.Node.InnerText.Trim()
if (-not $Version) { $Version = $declared }
if ($Version.StartsWith('v')) { $Version = $Version.Substring(1) }
if ($Version -ne $declared) {
    throw "Version mismatch: -Version is '$Version' but Directory.Build.props says '$declared'. Change <Version> in Directory.Build.props (the one place), commit it, then run this script again."
}
Write-Host "Version $Version"
$out = Join-Path $root 'src\Glossa.App\bin\release'
$app = Join-Path $out 'Glossa'
$zip = Join-Path $out "Glossa-$Version-win-x64.zip"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
Write-Host 'Building...'
dotnet publish (Join-Path $root 'src\Glossa.App\Glossa.App.csproj') -c Release -o $app --nologo -v quiet
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }

function Copy-Data([string]$from, [string]$to) {
    $src = Join-Path $Data $from
    if (-not (Test-Path $src)) { throw "missing $src" }
    $dst = Join-Path $app "GlossaData\$to"
    New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
    Copy-Item $src $dst -Recurse
}
# OCR: PP-OCRv5 mobile (lines, Cyrillic, orientation) and the PP-OCRv6 medium reader, which uses the small list.
Copy-Data 'models\ocr\v5' 'models\ocr\v5'
Copy-Data 'models\ocr\v6\PP-OCRv6_rec_medium.onnx' 'models\ocr\v6\PP-OCRv6_rec_medium.onnx'
Copy-Data 'models\ocr\v6\ppocrv6_small_dict.txt' 'models\ocr\v6\ppocrv6_small_dict.txt'
Copy-Data 'dict\unidic-lite' 'dict\unidic-lite'
Copy-Data 'dict\cedict_ts.u8' 'dict\cedict_ts.u8'

Copy-Item (Join-Path $root 'LICENSE') (Join-Path $app 'LICENSE.txt')
@'
Data shipped in GlossaData:

- models\ocr: PaddleOCR PP-OCRv5 mobile and PP-OCRv6 medium (ONNX), Apache License 2.0,
  https://github.com/PaddlePaddle/PaddleOCR
- dict\unidic-lite: UniDic via unidic-lite, BSD license (dict\unidic-lite\BSD),
  https://github.com/polm/unidic-lite
- dict\cedict_ts.u8: CC-CEDICT, Creative Commons Attribution-ShareAlike 4.0,
  https://www.mdbg.net/chinese/dictionary?page=cedict
'@ | Set-Content (Join-Path $app 'GlossaData\NOTICE.txt') -Encoding ASCII

Write-Host 'Packing...'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($app, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
Write-Host ('{0} ({1:N0} MB)' -f $zip, ((Get-Item $zip).Length / 1MB))
