$ErrorActionPreference = 'Stop'
$probeRoot = Join-Path ([IO.Path]::GetTempPath()) ('elevated-recovery-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $probeRoot | Out-Null
[IO.File]::WriteAllText((Join-Path $probeRoot 'LumaSearch-recovery-security-fixture.txt'), 'Disposable LumaSearch security fixture.')
$testAssembly = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\LumaSearch.Tests.dll'
$testProcess = Start-Process -FilePath 'C:\Program Files\dotnet\dotnet.exe' -ArgumentList @(('"' + $testAssembly + '"'), '--elevated-recovery-smoke', ('"' + $probeRoot + '"')) -Verb RunAs -WindowStyle Hidden -PassThru
$readyFile = Join-Path $probeRoot 'ready.json'
$resultFile = Join-Path $probeRoot 'result.json'
$deadline = [DateTime]::UtcNow.AddSeconds(60)
while (-not (Test-Path -LiteralPath $readyFile) -and -not (Test-Path -LiteralPath $resultFile)) {
    if ([DateTime]::UtcNow -gt $deadline) { throw 'The administrator test did not start within 60 seconds.' }
    Start-Sleep -Milliseconds 100
}
if (Test-Path -LiteralPath $readyFile) {
    try {
        $stage = (Get-Content -LiteralPath $readyFile -Raw | ConvertFrom-Json).StagedPath
        $resolvedStage = [IO.Path]::GetFullPath($stage)
        $fixtureVolume = [IO.Path]::GetPathRoot($probeRoot)
        $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $allowedStorage = Join-Path $fixtureVolume ('$LumaSearchRecovery\' + $currentSid + '\')
        if (-not $resolvedStage.StartsWith($allowedStorage, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolvedStage) -ne 'LumaSearch-recovery-security-fixture.txt') { throw 'Unexpected staging fixture path.' }
        $writeDenied = $false
        try { [IO.File]::WriteAllText($resolvedStage, 'This disposable write must be refused.') }
        catch [UnauthorizedAccessException] { $writeDenied = $true }
        if (-not $writeDenied) { throw 'Production staging allowed a non-elevated write.' }
        Write-Output 'PASS: A non-elevated process cannot modify the protected staged fixture.'
    }
    finally { [IO.File]::WriteAllText((Join-Path $probeRoot 'continue.signal'), 'continue') }
}
$deadline = [DateTime]::UtcNow.AddSeconds(60)
while (-not (Test-Path -LiteralPath $resultFile)) {
    if ([DateTime]::UtcNow -gt $deadline) { throw 'The administrator test did not finish within 60 seconds.' }
    Start-Sleep -Milliseconds 100
}
$result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
if (-not $result.Passed) { throw $result.Message }
[IO.File]::WriteAllText((Join-Path $probeRoot 'LumaSearch-recovery-security-fixture.txt'), 'Disposable LumaSearch security fixture.')
Write-Output 'PASS: Original non-elevated write access is restored.'
Write-Output ('PASS: ' + $result.Message)
Write-Output ('Report: ' + $resultFile)
