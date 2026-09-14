param([ValidateSet('Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build LumaSearch.slnx --configuration $Configuration --target:Rebuild
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet Tests\bin\Release\net10.0-windows\LumaSearch.Tests.dll
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet publish LumaSearch.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts\publish\Release\win-x64 -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    dotnet Tests\bin\Release\net10.0-windows\LumaSearch.Tests.dll --verify-release artifacts\publish\Release\win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Published Release verification failed.' }
    dotnet artifacts\publish\Release\win-x64\LumaSearch.dll --verify-release
    if ($LASTEXITCODE -ne 0) { throw 'Administrator manifest or Release payload verification failed.' }
    $payload = Join-Path $PSScriptRoot 'artifacts\publish\Release\win-x64'
    $legacyPublish = Join-Path $PSScriptRoot 'artifacts\publish'
    foreach ($file in @(Get-ChildItem -LiteralPath $payload -File -Recurse)) {
        $target = Join-Path $legacyPublish $file.FullName.Substring($payload.Length).TrimStart('\')
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
    $compiler = Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { throw 'Inno Setup 7 is required.' }
    & $compiler /Q Installer\LumaSearch.iss
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
}
finally { Pop-Location }
