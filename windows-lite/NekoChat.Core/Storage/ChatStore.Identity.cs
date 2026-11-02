namespace NekoChat.Core.Storage;

public sealed record LocalIdentitySnapshot(string? Id, string? PublicSpkiBase64, string? CredentialRef, long Revision);
public sealed record IdentityChangeDiscarded(string Reason) : ReplaceIdentityResult;

public sealed partial class ChatStore
{
    private const string LocalIdentitySql = "SELECT l.id,l.publicKey,l.credentialRef,s.revision FROM LocalSecurityRevision s LEFT JOIN LocalIdentity l ON l.singleton=1 WHERE s.singleton=1";
    public LocalIdentitySnapshot GetLocalIdentitySnapshot() => Read(c => {
        using var cmd = c.CreateCommand(); cmd.CommandText = LocalIdentitySql; using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new StoreException(StoreErrorKind.Corrupt, "Security revision records are missing.");
        return MapLocalIdentity(r);
    });
    private static LocalIdentitySnapshot MapLocalIdentity(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : Convert.ToBase64String((byte[])r.GetValue(1)), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3));
    private static bool LocalIdentityMatches(WriteContext ctx, LocalIdentitySnapshot expected) => ctx.Query(LocalIdentitySql, MapLocalIdentity).Single() == expected;

    public bool ProvisionLocalIdentityIfCurrent(LocalIdentitySnapshot expected, string id, byte[] publicKey, string credentialRef, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(publicKey); ArgumentNullException.ThrowIfNull(stillCurrent);
        if (expected.Id is not null || !StoreIds.IsValid(id) || publicKey.Length == 0 || string.IsNullOrEmpty(credentialRef)) throw new ArgumentException("Invalid identity provisioning.");
        return Write("provision-identity", ctx => {
            if (!stillCurrent() || !LocalIdentityMatches(ctx, expected)) return (false, false);
            ctx.Exec("INSERT INTO LocalIdentity(singleton,id,publicKey,credentialRef,createdUtc) VALUES(1,@id,@key,@ref,@now)",
                ("@id", id), ("@key", publicKey), ("@ref", credentialRef), ("@now", ctx.NowUtc)); return (true, true);
        }, guardRejected: () => false, commitGuard: stillCurrent);
    }

    public ReplaceIdentityResult ReplaceLocalIdentityIfCurrent(LocalIdentitySnapshot expected, string newId, byte[] newPublicKey, string newCredentialRef, Func<bool> stillCurrent)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(stillCurrent);
        ArgumentNullException.ThrowIfNull(newPublicKey); ArgumentException.ThrowIfNullOrEmpty(newCredentialRef);
        return ReplaceLocalIdentityCore(newId, newPublicKey, newCredentialRef, expected, stillCurrent);
    }
}
