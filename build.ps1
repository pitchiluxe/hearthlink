param([switch]$SelfContained)
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_HOME=Join-Path $PSScriptRoot '.build-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE='true'
dotnet run --project tests/HearthLink.Tests -c Release
if($LASTEXITCODE -ne 0) { throw 'Security checks failed' }
dotnet run --project tests/HearthLink.WindowsTests -c Release
if($LASTEXITCODE -ne 0) { throw 'Windows TLS checks failed' }
$standalone=if($SelfContained) {'true'} else {'false'}
$publishDir=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts/publish'))
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))+[IO.Path]::DirectorySeparatorChar
if(!$publishDir.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Publish output is outside the workspace artifacts folder' }
if(Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
dotnet publish src/HearthLink -c Release -r win-x64 --self-contained $standalone -o artifacts/publish
if($LASTEXITCODE -ne 0) { throw 'Publish failed' }
if($SelfContained) {
    $packages=if($env:NUGET_PACKAGES) {$env:NUGET_PACKAGES} else {Join-Path $env:DOTNET_CLI_HOME '.nuget/packages'}
    $runtimeConfig=Get-Content artifacts/publish/HearthLink.runtimeconfig.json -Raw | ConvertFrom-Json
    $runtimeVersion=($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
    $desktopVersion=($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App').version
    $licenses=New-Item -ItemType Directory -Path artifacts/publish/licenses -Force
    Copy-Item -LiteralPath (Join-Path $packages "microsoft.netcore.app.runtime.win-x64/$runtimeVersion/LICENSE.TXT") -Destination (Join-Path $licenses.FullName 'dotnet-runtime-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $packages "microsoft.netcore.app.runtime.win-x64/$runtimeVersion/THIRD-PARTY-NOTICES.TXT") -Destination (Join-Path $licenses.FullName 'dotnet-runtime-THIRD-PARTY-NOTICES.txt')
    Copy-Item -LiteralPath (Join-Path $packages "microsoft.windowsdesktop.app.runtime.win-x64/$desktopVersion/LICENSE") -Destination (Join-Path $licenses.FullName 'dotnet-desktop-LICENSE.txt')
    Copy-Item -LiteralPath third-party/winforms-10.0.13-NOTICES.txt -Destination (Join-Path $licenses.FullName 'winforms-THIRD-PARTY-NOTICES.txt')
}
Copy-Item -LiteralPath README.md -Destination artifacts/publish/README.md
Copy-Item -LiteralPath docs/ARCHITECTURE.md -Destination artifacts/publish/ARCHITECTURE.md
Copy-Item -LiteralPath docs/ACCEPTANCE.md -Destination artifacts/publish/ACCEPTANCE.md
Copy-Item -LiteralPath docs/VERIFICATION.md -Destination artifacts/publish/VERIFICATION.md
Copy-Item -LiteralPath docs/DESIGN.md -Destination artifacts/publish/DESIGN.md
Copy-Item -LiteralPath docs/UPDATES.md -Destination artifacts/publish/UPDATES.md
$compiler='C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
if(!(Test-Path -LiteralPath $compiler)) { throw 'Install Inno Setup 6, or update compiler path in build.ps1' }
& $compiler installer/HearthLink.iss
if($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
Get-FileHash artifacts/*setup.exe -Algorithm SHA256
Get-ChildItem artifacts/*setup.exe | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash,$_.Name } | Set-Content -LiteralPath artifacts/SHA256SUMS.txt -Encoding ascii
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$appVersion=([xml](Get-Content src/HearthLink/HearthLink.csproj -Raw)).Project.PropertyGroup.Version
$sourcePath=Join-Path $PSScriptRoot "artifacts/HearthLink-$appVersion-source.zip"
if(Test-Path -LiteralPath $sourcePath) { Remove-Item -LiteralPath $sourcePath -Force }
$archive=[IO.Compression.ZipFile]::Open($sourcePath,[IO.Compression.ZipArchiveMode]::Create)
try {
    $sourceFiles=@('.gitignore','Directory.Build.props','build.ps1','global.json','README.md') | ForEach-Object {Get-Item -LiteralPath $_}
    $sourceFiles+=Get-ChildItem -LiteralPath src,tests,docs,installer,third-party,assets,tools,website -Recurse -File | Where-Object {$_.FullName -notmatch '[\\/](bin|obj|\.vercel)[\\/]' -and $_.Name -notlike '.env*'}
    foreach($file in $sourceFiles) {
        $relative=$file.FullName.Substring($PSScriptRoot.Length+1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {$archive.Dispose()}
