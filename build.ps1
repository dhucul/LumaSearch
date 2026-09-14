param([ValidateSet('Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build LumaSearch.slnx --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet Tests\bin\Release\net10.0-windows\LumaSearch.Tests.dll
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet publish LumaSearch.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts\publish\Release\win-x64 -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    dotnet Tests\bin\Release\net10.0-windows\LumaSearch.Tests.dll --verify-release artifacts\publish\Release\win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Published Release verification failed.' }
    $compiler = Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { throw 'Inno Setup 7 is required.' }
    & $compiler /Q Installer\LumaSearch.iss
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
}
finally { Pop-Location }
