# Deletion and recovery

LumaSearch passes approved operations directly to its helper through inherited pipes. Operation history files cannot change the approved targets or deletion mode.

## Recycle and restore

Recycling first saves a recovery record and moves the verified object by its open handle into protected storage on the same local drive. Windows then recycles that staged object. A replacement at the original path is left alone.

Use **Restore deleted items…** in LumaSearch to return an item to its original location. Select the appropriate recorded date when several versions of the same path are listed. The original parent directory must exist. Restoration refuses to overwrite an existing file or folder.

Windows' own **Restore** returns these items to the staging location. They can then be restored to their original locations through LumaSearch. Items recycled before this recovery feature are still restored directly through Windows.

Emptying the Recycle Bin can remove recycled payloads permanently. Items that stopped in staging remain available through LumaSearch's recovery list. Do not remove protected recovery storage while it contains items you want to recover; uninstalling LumaSearch does not remove this storage.

Protected storage is located at `$LumaSearchRecovery` on each affected drive. It is hidden, restricted to administrators and SYSTEM, and partitioned by Windows user. Records are saved before objects move. The staged root receives a temporary integrity label; its original owner and access rules are preserved. Restoration restores its original label.

Protected recycling requires administrator execution and a local filesystem supporting identities and Windows security. Files with multiple hard links are left in place because changing their integrity label would also affect their other names. Permanent deletion remains available where supported.

## Incomplete operations

Cancellation stops at a safe boundary. An interrupted folder operation can be partial. Failed and cancelled recycling may leave items in protected recovery storage; use the recovery list to locate them.

Permanent deletion can remain pending while another application has a compatible open handle. Pending targets remain visible until their removal is confirmed. Startup checks all unresolved operation records, including older operations, and rechecks pending targets.

## Validation

`dotnet build LumaSearch.slnx --configuration Release` builds the app and test executable.

`dotnet Tests\bin\Release\net10.0-windows\LumaSearch.Tests.dll` runs the regression suite against disposable fixtures.

`dotnet Tests\bin\Release\net10.0-windows\LumaSearch.Tests.dll --recycle-smoke` additionally exercises native Windows recycling, original-location restoration, and replacement races. These tests use an isolated recovery directory with a test-user permission policy so they can run without UAC.

Run `Tests\Invoke-ElevatedRecoveryTest.ps1` from a normal, non-administrator PowerShell session to verify production protection. Windows requests administrator approval for the fixture worker. The script checks that the normal process cannot write the protected staged file, then verifies native recycling, restoration, and restored non-administrator write access. Only the newly created disposable fixture is processed.
