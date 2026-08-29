using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Privon.Storage.Tests;

// PRIVON v0.2.1 Gate 3B correction -- MasterKeyStore/PrivonLocalStore fail-safe regression for an
// EXISTING master.key that becomes unreadable (ACL deny, or a locked-file-style IOException),
// as distinct from a genuinely MISSING master.key (legitimate first-run). Same proven real
// Windows ACL Deny technique as CAT-013/EXC-012 -- current-user-only, own temp directory, no
// admin rights, ACL always restored in `finally`. Synthetic data only.
//
// CRITICAL_INVARIANT this whole file exists to lock: an existing, unreadable master.key must
// NEVER be silently regenerated/overwritten -- doing so would permanently destroy the ability to
// decrypt every store already encrypted under the original key, the instant write access allows
// the overwrite to succeed. Every test that asserts "unchanged" compares real bytes/hashes taken
// BEFORE the ACL denial against bytes/hashes taken AFTER the denied operation, never merely "did
// not throw".
public class MasterKeyFailSafeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PrivonMasterKeyFailSafeTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static byte[] Hash(byte[] bytes) => SHA256.HashData(bytes);

    // Applies a real, current-user-only Read Deny ACL rule to `path` and returns an IDisposable
    // that removes it again -- guarantees restoration via `using`/`finally` at every call site,
    // never left modified even if the test body throws.
    private static IDisposable DenyRead(string path)
    {
        var fileInfo = new FileInfo(path);
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(currentUser, FileSystemRights.Read | FileSystemRights.ReadData, AccessControlType.Deny);
        var acl = fileInfo.GetAccessControl();
        acl.AddAccessRule(rule);
        fileInfo.SetAccessControl(acl);

        // Sanity check: confirm this specific file state genuinely reproduces
        // UnauthorizedAccessException from the real File.ReadAllBytes BEFORE any test trusts the
        // production code's own handling of it.
        var directReadException = Record.Exception(() => File.ReadAllBytes(path));
        if (directReadException is not UnauthorizedAccessException)
        {
            var restoredAcl = fileInfo.GetAccessControl();
            restoredAcl.RemoveAccessRule(rule);
            fileInfo.SetAccessControl(restoredAcl);
            throw new InvalidOperationException(
                $"ACL deny rule did not reproduce UnauthorizedAccessException on {path} -- harness is wrong, not the fix under test. " +
                $"Actual: {directReadException?.GetType().Name ?? "no exception"}");
        }

        return new AclRestorer(fileInfo, rule);
    }

    private sealed class AclRestorer(FileInfo fileInfo, FileSystemAccessRule rule) : IDisposable
    {
        public void Dispose()
        {
            var acl = fileInfo.GetAccessControl();
            acl.RemoveAccessRule(rule);
            fileInfo.SetAccessControl(acl);
        }
    }

    // ==================================================================
    // MK-001 -- master.key genuinely absent -> first-run key generation still works.
    // ==================================================================
    [Fact]
    public void Mk001_MasterKeyGenuinelyAbsent_FirstRunKeyGenerationStillWorks()
    {
        var keyPath = Path.Combine(_root, "master.key");
        Directory.CreateDirectory(_root);
        Assert.False(File.Exists(keyPath));

        var store = PrivonLocalStore.OpenOrCreate(_root);

        Assert.True(File.Exists(keyPath));
        // A real, usable key was created -- prove it round-trips real (synthetic) data, not just
        // that a file now exists.
        var settings = new PrivonSettings(ProtectionEnabled: false, Categories: new ProtectionCategorySettings(true, false, true, false, true));
        store.SaveSettings(settings);
        Assert.Equal(settings, store.LoadSettings());
    }

    // ==================================================================
    // MK-002 -- existing valid master.key -> normal load unchanged (same key bytes reused, not
    // regenerated, across a fresh PrivonLocalStore.OpenOrCreate call).
    // ==================================================================
    [Fact]
    public void Mk002_ExistingValidMasterKey_NormalLoadUnchanged_SameKeyBytesReused()
    {
        var keyPath = Path.Combine(_root, "master.key");
        var first = PrivonLocalStore.OpenOrCreate(_root);
        var settings = new PrivonSettings(ProtectionEnabled: false, Categories: ProtectionCategorySettings.AllOn);
        first.SaveSettings(settings);
        var keyBytesAfterFirstOpen = File.ReadAllBytes(keyPath);

        var second = PrivonLocalStore.OpenOrCreate(_root); // fresh instance, same root

        Assert.Equal(keyBytesAfterFirstOpen, File.ReadAllBytes(keyPath)); // never regenerated
        Assert.Equal(settings, second.LoadSettings()); // decrypts real data written under the same key
    }

    // ==================================================================
    // MK-003 -- existing master.key + real current-user ACL READ deny -> no new master key
    // generated, existing master.key bytes unchanged, Open/store path reaches protective
    // behavior (does not throw, safe defaults are returned).
    // ==================================================================
    [Fact]
    public void Mk003_ExistingMasterKey_AclReadDeny_NoRegeneration_KeyBytesUnchanged_ProtectiveBehavior()
    {
        var keyPath = Path.Combine(_root, "master.key");
        PrivonLocalStore.OpenOrCreate(_root); // creates the key
        var keyBytesBeforeDenial = File.ReadAllBytes(keyPath);
        var keyHashBeforeDenial = Hash(keyBytesBeforeDenial);

        using (DenyRead(keyPath))
        {
            var exception = Record.Exception(() => PrivonLocalStore.OpenOrCreate(_root));
            Assert.Null(exception); // MK-010 companion: OpenOrCreate itself never throws/aborts composition
        }

        // MK-010: no master.key replacement/regeneration occurred while denied -- exact bytes
        // (compared via hash) unchanged, checked here once read access is restored.
        var keyBytesAfterRestoration = File.ReadAllBytes(keyPath);
        Assert.Equal(keyHashBeforeDenial, Hash(keyBytesAfterRestoration));
    }

    // ==================================================================
    // MK-004 -- Settings read resolves to ProtectionEnabled=true, Categories=AllOn while the
    // master key is unavailable.
    // ==================================================================
    [Fact]
    public void Mk004_MasterKeyAclDenial_SettingsReadResolvesToSafeDefault()
    {
        var keyPath = Path.Combine(_root, "master.key");
        var setup = PrivonLocalStore.OpenOrCreate(_root);
        setup.SaveSettings(new PrivonSettings(ProtectionEnabled: false, Categories: new ProtectionCategorySettings(false, false, false, false, false)));

        using (DenyRead(keyPath))
        {
            var store = PrivonLocalStore.OpenOrCreate(_root);
            var loaded = store.LoadSettings();

            Assert.True(loaded.ProtectionEnabled);
            Assert.Equal(ProtectionCategorySettings.AllOn, loaded.Categories);
        }
    }

    // ==================================================================
    // MK-005 -- TrustedPublicInfo read = empty while the master key is unavailable.
    // ==================================================================
    [Fact]
    public void Mk005_MasterKeyAclDenial_TrustedPublicInfoReadIsEmpty()
    {
        var keyPath = Path.Combine(_root, "master.key");
        var setup = PrivonLocalStore.OpenOrCreate(_root);
        setup.SaveTrustedPublicInfo([new TrustedPublicInfoEntry("Phone", "01012345678")]);

        using (DenyRead(keyPath))
        {
            var store = PrivonLocalStore.OpenOrCreate(_root);
            Assert.Empty(store.LoadTrustedPublicInfo());
        }
    }

    // ==================================================================
    // MK-006 -- existing Exceptions (Level1) read = empty while the master key is unavailable.
    // ==================================================================
    [Fact]
    public void Mk006_MasterKeyAclDenial_ExceptionsReadIsEmpty()
    {
        var keyPath = Path.Combine(_root, "master.key");
        var setup = PrivonLocalStore.OpenOrCreate(_root);
        setup.SaveExceptions([new ExceptionEntry("GpsCoordinate", "37.5,127.0")]);

        using (DenyRead(keyPath))
        {
            var store = PrivonLocalStore.OpenOrCreate(_root);
            Assert.Empty(store.LoadExceptions());
        }
    }

    // ==================================================================
    // MK-007 -- UserExceptions read = empty while the master key is unavailable.
    // ==================================================================
    [Fact]
    public void Mk007_MasterKeyAclDenial_UserExceptionsReadIsEmpty()
    {
        var keyPath = Path.Combine(_root, "master.key");
        var setup = PrivonLocalStore.OpenOrCreate(_root);
        setup.SaveUserExceptions([new UserExceptionEntry("Phone", "01012345678")]);

        using (DenyRead(keyPath))
        {
            var store = PrivonLocalStore.OpenOrCreate(_root);
            Assert.Empty(store.LoadUserExceptions());
        }
    }

    // ==================================================================
    // MK-008 -- SaveSettings fails explicitly while the master key is unavailable; existing
    // settings.bin bytes remain byte-for-byte unchanged (never silently succeeds, never persists
    // under a replacement key).
    // ==================================================================
    [Fact]
    public void Mk008_MasterKeyUnavailable_SaveSettingsFails_ExistingBytesUnchanged()
    {
        var keyPath = Path.Combine(_root, "master.key");
        var settingsPath = Path.Combine(_root, "settings.bin");
        var setup = PrivonLocalStore.OpenOrCreate(_root);
        setup.SaveSettings(new PrivonSettings(ProtectionEnabled: false, Categories: ProtectionCategorySettings.AllOn));
        var settingsBytesBefore = File.ReadAllBytes(settingsPath);

        using (DenyRead(keyPath))
        {
            var store = PrivonLocalStore.OpenOrCreate(_root);
            var exception = Record.Exception(() =>
                store.SaveSettings(new PrivonSettings(ProtectionEnabled: true, Categories: new ProtectionCategorySettings(true, true, true, true, true))));

            Assert.NotNull(exception);
            Assert.Equal(settingsBytesBefore, File.ReadAllBytes(settingsPath));
        }
    }

    // ==================================================================
    // MK-009 -- SaveUserExceptions fails explicitly while the master key is unavailable; existing
    // user-exceptions.bin bytes remain byte-for-byte unchanged.
    // ==================================================================
    [Fact]
    public void Mk009_MasterKeyUnavailable_SaveUserExceptionsFails_ExistingBytesUnchanged()
    {
        var keyPath = Path.Combine(_root, "master.key");
        var userExceptionsPath = Path.Combine(_root, "user-exceptions.bin");
        var setup = PrivonLocalStore.OpenOrCreate(_root);
        setup.SaveUserExceptions([new UserExceptionEntry("Phone", "01012345678")]);
        var bytesBefore = File.ReadAllBytes(userExceptionsPath);

        using (DenyRead(keyPath))
        {
            var store = PrivonLocalStore.OpenOrCreate(_root);
            var exception = Record.Exception(() => store.SaveUserExceptions([new UserExceptionEntry("Email", "user@example.test")]));

            Assert.NotNull(exception);
            Assert.Equal(bytesBefore, File.ReadAllBytes(userExceptionsPath));
        }
    }

    // ==================================================================
    // MK-011 -- removing the ACL denial: the SAME original master key loads again, and
    // previously encrypted store data (written BEFORE the denial) is readable again -- proves no
    // destructive fallback (regeneration/overwrite) ever occurred during the denial window.
    // ==================================================================
    [Fact]
    public void Mk011_AclDenialRemoved_OriginalKeyLoads_PreviouslyEncryptedDataReadableAgain()
    {
        var keyPath = Path.Combine(_root, "master.key");
        var originalSettings = new PrivonSettings(
            ProtectionEnabled: false,
            Categories: new ProtectionCategorySettings(NameEnabled: false, PhoneEnabled: true, EmailEnabled: false, AddressEnabled: true, CompanyEnabled: false));
        var setup = PrivonLocalStore.OpenOrCreate(_root);
        setup.SaveSettings(originalSettings);
        var keyHashBefore = Hash(File.ReadAllBytes(keyPath));

        using (DenyRead(keyPath))
        {
            var duringDenial = PrivonLocalStore.OpenOrCreate(_root);
            Assert.True(duringDenial.LoadSettings().ProtectionEnabled); // safe default while denied
        }
        // ACL restored here (DenyRead's IDisposable already ran).

        Assert.Equal(keyHashBefore, Hash(File.ReadAllBytes(keyPath))); // same key, never regenerated

        var afterRestoration = PrivonLocalStore.OpenOrCreate(_root);
        Assert.Equal(originalSettings, afterRestoration.LoadSettings()); // original data, still decryptable
    }

    // ==================================================================
    // MK-012 -- ACL cleanup genuinely restores the test environment: a plain File.ReadAllBytes
    // succeeds again after the deny/restore cycle, not merely "RemoveAccessRule was called".
    // ==================================================================
    [Fact]
    public void Mk012_AclCleanup_GenuinelyRestoresReadAccess()
    {
        var keyPath = Path.Combine(_root, "master.key");
        PrivonLocalStore.OpenOrCreate(_root);

        using (DenyRead(keyPath))
        {
            Assert.IsType<UnauthorizedAccessException>(Record.Exception(() => File.ReadAllBytes(keyPath)));
        }

        var exception = Record.Exception(() => File.ReadAllBytes(keyPath));
        Assert.Null(exception);
    }

    // ==================================================================
    // MK-018 -- generic IOException on an EXISTING key -> MasterKeyUnavailable, NEVER classified
    // as missing (no regeneration). Simulated via an exclusive file lock, the deterministic, real
    // way to reproduce a genuine IOException from File.ReadAllBytes without relying on ACLs.
    // ==================================================================
    [Fact]
    public void Mk018_ExistingMasterKey_LockedFile_IOException_NoRegeneration_SafeDefaultBehavior()
    {
        var keyPath = Path.Combine(_root, "master.key");
        PrivonLocalStore.OpenOrCreate(_root);
        var keyHashBefore = Hash(File.ReadAllBytes(keyPath));

        using var lockingHandle = new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var directReadException = Record.Exception(() => File.ReadAllBytes(keyPath));
        Assert.IsType<IOException>(directReadException);
        Assert.IsNotType<FileNotFoundException>(directReadException); // a genuine, non-missing IOException

        var store = PrivonLocalStore.OpenOrCreate(_root);
        Assert.True(store.LoadSettings().ProtectionEnabled); // safe default, no throw

        lockingHandle.Dispose();
        Assert.Equal(keyHashBefore, Hash(File.ReadAllBytes(keyPath))); // never regenerated
    }

    // ==================================================================
    // MK-019 -- UnauthorizedAccessException on an EXISTING key -> MasterKeyUnavailable, NEVER
    // classified as missing (no regeneration). Explicit companion to MK-003/MK-004, focused
    // specifically on the missing-vs-unavailable classification itself.
    // ==================================================================
    [Fact]
    public void Mk019_ExistingMasterKey_UnauthorizedAccess_NeverClassifiedAsMissing_NoRegeneration()
    {
        var keyPath = Path.Combine(_root, "master.key");
        PrivonLocalStore.OpenOrCreate(_root);
        var keyBytesBefore = File.ReadAllBytes(keyPath);

        using (DenyRead(keyPath))
        {
            PrivonLocalStore.OpenOrCreate(_root); // must not call CreateAndSave
        }

        // If the implementation had ever misclassified this as "missing" and regenerated, the
        // file would now hold a DIFFERENT 32-byte DPAPI blob (astronomically unlikely to
        // coincide with the original by chance) -- exact equality is the proof.
        Assert.Equal(keyBytesBefore, File.ReadAllBytes(keyPath));
    }

    // ==================================================================
    // MK-013 -- existing master.key whose DPAPI payload causes CryptographicException (garbage,
    // non-DPAPI-blob bytes) -> MasterKeyUnavailable: no replacement key, exact master.key bytes
    // unchanged, safe-default reads across every store, writes fail explicitly.
    // ==================================================================
    [Fact]
    public void Mk013_ExistingMasterKey_CryptographicException_Unavailable_NoRegeneration_SafeDefaults_WritesFail()
    {
        var keyPath = Path.Combine(_root, "master.key");
        var settingsPath = Path.Combine(_root, "settings.bin");
        var setup = PrivonLocalStore.OpenOrCreate(_root);
        setup.SaveSettings(new PrivonSettings(ProtectionEnabled: false, Categories: new ProtectionCategorySettings(false, false, false, false, false)));
        setup.SaveTrustedPublicInfo([new TrustedPublicInfoEntry("Phone", "01012345678")]);
        setup.SaveExceptions([new ExceptionEntry("GpsCoordinate", "37.5,127.0")]);
        setup.SaveUserExceptions([new UserExceptionEntry("Email", "user@example.test")]);
        var settingsBytesBefore = File.ReadAllBytes(settingsPath);

        // Corrupt master.key with bytes that are structurally NOT a valid DPAPI-protected blob --
        // ProtectedData.Unprotect throws CryptographicException for this, never IOException/
        // UnauthorizedAccessException, so this specifically exercises the DPAPI-failure branch.
        var garbageBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        File.WriteAllBytes(keyPath, garbageBytes);
        Assert.Throws<CryptographicException>(() =>
            ProtectedData.Unprotect(garbageBytes, optionalEntropy: null, DataProtectionScope.CurrentUser));
        var corruptedKeyBytes = File.ReadAllBytes(keyPath);

        var store = PrivonLocalStore.OpenOrCreate(_root);

        // Safe-default reads across every store.
        var settings = store.LoadSettings();
        Assert.True(settings.ProtectionEnabled);
        Assert.Equal(ProtectionCategorySettings.AllOn, settings.Categories);
        Assert.Empty(store.LoadTrustedPublicInfo());
        Assert.Empty(store.LoadExceptions());
        Assert.Empty(store.LoadUserExceptions());

        // Writes fail explicitly.
        Assert.NotNull(Record.Exception(() => store.SaveSettings(PrivonSettings.SafeDefault)));

        // Non-destructive: master.key was never touched further, and the pre-corruption settings
        // ciphertext (still on disk, just no longer decryptable without the lost original key) is
        // also untouched.
        Assert.Equal(corruptedKeyBytes, File.ReadAllBytes(keyPath));
        Assert.Equal(settingsBytesBefore, File.ReadAllBytes(settingsPath));
    }

    // ==================================================================
    // MK-014 -- restoring the ORIGINAL valid master.key after the MK-013 CryptographicException
    // condition -> the original encrypted stores become readable again, proving no destructive
    // regeneration occurred during the corrupted window (a regenerated key would have permanently
    // orphaned this data instead).
    // ==================================================================
    [Fact]
    public void Mk014_RestoreOriginalKeyAfterCryptographicFailure_OriginalDataReadableAgain()
    {
        var keyPath = Path.Combine(_root, "master.key");
        var originalSettings = new PrivonSettings(
            ProtectionEnabled: false,
            Categories: new ProtectionCategorySettings(NameEnabled: true, PhoneEnabled: false, EmailEnabled: true, AddressEnabled: false, CompanyEnabled: true));
        var setup = PrivonLocalStore.OpenOrCreate(_root);
        setup.SaveSettings(originalSettings);
        var originalKeyBytes = File.ReadAllBytes(keyPath);

        File.WriteAllBytes(keyPath, [9, 9, 9, 9, 9, 9, 9, 9]); // garbage -- CryptographicException
        var duringCorruption = PrivonLocalStore.OpenOrCreate(_root);
        Assert.True(duringCorruption.LoadSettings().ProtectionEnabled); // degraded safe default

        File.WriteAllBytes(keyPath, originalKeyBytes); // restore the ORIGINAL key exactly

        var afterRestoration = PrivonLocalStore.OpenOrCreate(_root);
        Assert.Equal(originalSettings, afterRestoration.LoadSettings());
    }

    // ==================================================================
    // MK-015 -- existing master.key containing a VALID DPAPI-protected plaintext of the WRONG
    // length (unprotect succeeds, but the key is not 32 bytes) -> MasterKeyUnavailable: no
    // replacement, exact master.key bytes unchanged, degraded safe-default reads, writes fail.
    // ==================================================================
    [Fact]
    public void Mk015_ExistingMasterKey_ValidDpapiWrongLength_Unavailable_NoRegeneration_SafeDefaults_WritesFail()
    {
        var keyPath = Path.Combine(_root, "master.key");
        PrivonLocalStore.OpenOrCreate(_root); // establishes a real key/directory first

        // A genuinely valid DPAPI-protected blob (unprotects without throwing) whose PLAINTEXT is
        // the wrong length (16 bytes, not the required 32) -- exercises the length-validation
        // branch specifically, never CryptographicException.
        var wrongLengthPlaintext = new byte[16];
        RandomNumberGenerator.Fill(wrongLengthPlaintext);
        var wrongLengthProtected = ProtectedData.Protect(wrongLengthPlaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(keyPath, wrongLengthProtected);
        var unprotected = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), optionalEntropy: null, DataProtectionScope.CurrentUser);
        Assert.NotEqual(MasterKeyStore.KeySizeBytes, unprotected.Length); // sanity: harness constructed the right condition
        var keyBytesBefore = File.ReadAllBytes(keyPath);

        var store = PrivonLocalStore.OpenOrCreate(_root);

        Assert.True(store.LoadSettings().ProtectionEnabled);
        Assert.Equal(ProtectionCategorySettings.AllOn, store.LoadSettings().Categories);
        Assert.Empty(store.LoadTrustedPublicInfo());
        Assert.NotNull(Record.Exception(() => store.SaveSettings(PrivonSettings.SafeDefault)));

        Assert.Equal(keyBytesBefore, File.ReadAllBytes(keyPath)); // never regenerated
    }

    // ==================================================================
    // MK-016 -- restoring the ORIGINAL valid master.key after the MK-015 wrong-length condition ->
    // original encrypted data readable again.
    // ==================================================================
    [Fact]
    public void Mk016_RestoreOriginalKeyAfterWrongLengthCondition_OriginalDataReadableAgain()
    {
        var keyPath = Path.Combine(_root, "master.key");
        var originalSettings = new PrivonSettings(ProtectionEnabled: false, Categories: ProtectionCategorySettings.AllOn with { EmailEnabled = false });
        var setup = PrivonLocalStore.OpenOrCreate(_root);
        setup.SaveSettings(originalSettings);
        var originalKeyBytes = File.ReadAllBytes(keyPath);

        var wrongLengthPlaintext = new byte[8];
        RandomNumberGenerator.Fill(wrongLengthPlaintext);
        File.WriteAllBytes(keyPath, ProtectedData.Protect(wrongLengthPlaintext, optionalEntropy: null, DataProtectionScope.CurrentUser));
        var duringCondition = PrivonLocalStore.OpenOrCreate(_root);
        Assert.True(duringCondition.LoadSettings().ProtectionEnabled);

        File.WriteAllBytes(keyPath, originalKeyBytes); // restore the ORIGINAL key exactly

        var afterRestoration = PrivonLocalStore.OpenOrCreate(_root);
        Assert.Equal(originalSettings, afterRestoration.LoadSettings());
    }

    // ==================================================================
    // MK-017 -- definite, genuinely missing master.key -> creation occurs exactly once (a second,
    // immediately-following OpenOrCreate call reuses the SAME just-created key, never creates a
    // second one).
    // ==================================================================
    [Fact]
    public void Mk017_GenuinelyMissingMasterKey_CreationOccursExactlyOnce()
    {
        var keyPath = Path.Combine(_root, "master.key");
        Directory.CreateDirectory(_root);
        Assert.False(File.Exists(keyPath));

        PrivonLocalStore.OpenOrCreate(_root); // first call -- must create
        Assert.True(File.Exists(keyPath));
        var keyBytesAfterFirstCreation = File.ReadAllBytes(keyPath);

        PrivonLocalStore.OpenOrCreate(_root); // second call -- must reuse, never recreate
        Assert.Equal(keyBytesAfterFirstCreation, File.ReadAllBytes(keyPath));
    }

    // ==================================================================
    // MK-020 -- missing-detection correctness does not depend on a File.Exists-then-read TOCTOU
    // window. The production implementation (see MasterKeyStore.LoadOrCreateOrUnavailable) no
    // longer calls File.Exists at all -- it classifies "missing" SOLELY from whichever exception
    // (if any) the actual File.ReadAllBytes attempt itself throws (FileNotFoundException/
    // DirectoryNotFoundException = missing; anything else caught = unavailable). This is proven
    // here the smallest deterministic way available: an EXISTING key under a real ACL read-deny
    // condition (the same real-world condition File.Exists is documented to sometimes silently
    // report as "does not exist" for, since it swallows all internal errors and returns false)
    // must still never be misclassified as missing. If missing-detection depended on
    // File.Exists's own (potentially wrong) verdict instead of the real read attempt's own
    // outcome, this specific case is exactly where that bug would surface as a regenerated key.
    // ==================================================================
    [Fact]
    public void Mk020_MissingDetection_DoesNotDependOnFileExists_ExistingAclDeniedKeyNeverMisclassifiedAsMissing()
    {
        var keyPath = Path.Combine(_root, "master.key");
        PrivonLocalStore.OpenOrCreate(_root);
        var keyBytesBefore = File.ReadAllBytes(keyPath);

        using (DenyRead(keyPath))
        {
            // Whatever File.Exists happens to report for an ACL-denied file in this environment is
            // irrelevant to the production code's own correctness -- it must never call
            // CreateAndSave here regardless.
            PrivonLocalStore.OpenOrCreate(_root);
            PrivonLocalStore.OpenOrCreate(_root); // repeated calls -- never eventually "gives up" and creates
        }

        Assert.Equal(keyBytesBefore, File.ReadAllBytes(keyPath));
    }

    // ==================================================================
    // MK-021 -- LEGACY_LOAD_OR_CREATE_DESTRUCTIVE_FALLBACK_HARDENING: the legacy public
    // MasterKeyStore.LoadOrCreate (never PrivonLocalStore.OpenOrCreate / LoadOrCreateOrUnavailable
    // above -- this test targets the legacy compatibility API directly) must never silently
    // regenerate/overwrite an EXISTING master.key it cannot use. Same deterministic
    // crypto-invalid-blob technique as MK-013 (preferred over ACL manipulation -- no timing, no
    // admin rights, no restoration step needed): garbage bytes that are structurally not a valid
    // DPAPI-protected blob, so ProtectedData.Unprotect throws CryptographicException, never
    // IOException/UnauthorizedAccessException. Pre-fix, LoadOrCreate's TryLoad-based collapse
    // treats this identically to "missing" and overwrites master.key with a brand-new key --
    // exactly the destructive fallback this test exists to close.
    // ==================================================================
    [Fact]
    public void Mk021_LegacyLoadOrCreate_ExistingCorruptMasterKey_ThrowsAndPreservesOriginalBytes()
    {
        var keyPath = Path.Combine(_root, "master.key");
        Directory.CreateDirectory(_root);

        var corruptBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        File.WriteAllBytes(keyPath, corruptBytes);
        Assert.Throws<CryptographicException>(() =>
            ProtectedData.Unprotect(corruptBytes, optionalEntropy: null, DataProtectionScope.CurrentUser));
        var bytesBefore = File.ReadAllBytes(keyPath);

        var exception = Record.Exception(() => MasterKeyStore.LoadOrCreate(keyPath));

        Assert.IsType<InvalidOperationException>(exception);
        Assert.True(File.Exists(keyPath));
        Assert.Equal(bytesBefore, File.ReadAllBytes(keyPath));
    }
}
