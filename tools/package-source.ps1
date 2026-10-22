$ErrorActionPreference='Stop'
$workspacePath=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $workspacePath
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$appVersion=([xml](Get-Content src/HearthLink/HearthLink.csproj -Raw)).Project.PropertyGroup.Version
$sourcePath=Join-Path $workspacePath "artifacts/HearthLink-$appVersion-source.zip"
if(Test-Path -LiteralPath $sourcePath) { Remove-Item -LiteralPath $sourcePath -Force }
$archive=[IO.Compression.ZipFile]::Open($sourcePath,[IO.Compression.ZipArchiveMode]::Create)
try {
    $sourceFiles=@('.gitignore','Directory.Build.props','build.ps1','global.json','README.md') | ForEach-Object {Get-Item -LiteralPath $_}
    $sourceFiles+=Get-ChildItem -LiteralPath src,tests,docs,installer,third-party,assets,tools,website -Recurse -File | Where-Object {$_.FullName -notmatch '[\\/](bin|obj|\.vercel)[\\/]'}
    foreach($file in $sourceFiles) {
        $relative=$file.FullName.Substring($workspacePath.Length+1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {$archive.Dispose()}
Write-Output $sourcePath
