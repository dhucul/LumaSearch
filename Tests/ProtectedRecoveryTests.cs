using System.ComponentModel;
using System.IO;
using System.Security.AccessControl;
using LumaSearch;

internal static class ProtectedRecoveryTests
{
    internal static Task RunAsync(string root, Action<bool, string> check)
    {
        string fixtures = Path.Combine(root, "protected-recovery-tests");
        Directory.CreateDirectory(fixtures);
        var store = new RecoveryStorage(Path.Combine(fixtures, "store"));
        string file = Path.Combine(fixtures, "restore.txt");
        File.WriteAllText(file, "Original contents");
        var original = SearchResult.Capture(file);
        RecoveryEntry entry;
        using (var lease = store.Stage(original, CancellationToken.None))
        {
            entry = lease.Entry;
            check(!File.Exists(file) && SearchResult.Capture(entry.Record.StagedPath).Identity == original.Identity,
                "staging renames the verified object by handle and preserves identity");
            check(RecoveryStorage.ReadRecord(entry.RecordPath).Record.State == RecoveryState.Staged,
                "staging records the original path and recovery state");
        }
        File.WriteAllText(file, "Replacement must survive");
        bool collisionRefused = false;
        try { store.Restore(entry.RecordPath, CancellationToken.None); }
        catch (Exception ex) when (ex is Win32Exception or IOException) { collisionRefused = true; }
        check(collisionRefused && File.ReadAllText(file) == "Replacement must survive" && File.Exists(entry.Record.StagedPath),
            "restoration atomically refuses to overwrite a replacement");
        File.Delete(file);
        var restored = store.Restore(entry.RecordPath, CancellationToken.None);
        check(restored.Status == DeletionStatus.Restored && File.ReadAllText(file) == "Original contents" &&
            SearchResult.Capture(file).Identity == original.Identity && store.List().Length == 0,
            "restoration returns the original object and marks its record complete");

        using (var lease = store.Stage(SearchResult.Capture(file), CancellationToken.None))
        {
            entry = lease.Entry;
            RecoveryStorage.WriteRecord(entry with { Record = entry.Record with { State = RecoveryState.Prepared } });
        }
        check(store.Restore(entry.RecordPath, CancellationToken.None).Status == DeletionStatus.Restored && File.Exists(file),
            "an interrupted terminal staging write is recoverable from the prepared record");

        string folder = Path.Combine(fixtures, "folder");
        Directory.CreateDirectory(folder);
        string child = Path.Combine(folder, "child.txt");
        File.WriteAllText(child, "Child contents");
        string permissions = FileSystemAclExtensions.GetAccessControl(new FileInfo(child)).GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
        using (var lease = store.Stage(SearchResult.Capture(folder), CancellationToken.None)) entry = lease.Entry;
        store.Restore(entry.RecordPath, CancellationToken.None);
        check(File.ReadAllText(child) == "Child contents" &&
            FileSystemAclExtensions.GetAccessControl(new FileInfo(child)).GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner) == permissions,
            "folder staging and restoration preserve child permissions and contents");

        var descriptor = new RawSecurityDescriptor(RecoverySecurity.ProtectedDescriptor(testOnly: false, directory: false), 0);
        check(descriptor.Owner!.Value == "S-1-5-32-544" && descriptor.DiscretionaryAcl!.OfType<CommonAce>()
            .All(ace => ace.SecurityIdentifier.Value is "S-1-5-32-544" or "S-1-5-18") && descriptor.SystemAcl is { Count: > 0 },
            "production staging requires administrator ownership and a mandatory integrity label");
        bool untrustedRefused = false;
        string untrusted = Path.Combine(fixtures, "unprotected-store");
        Directory.CreateDirectory(untrusted);
        try { using var guard = RecoverySecurity.OpenDirectory(untrusted, create: false, testOnly: true); }
        catch (UnauthorizedAccessException) { untrustedRefused = true; }
        check(untrustedRefused, "pre-existing recovery storage with inherited permissions is rejected");
        return Task.CompletedTask;
    }
}
