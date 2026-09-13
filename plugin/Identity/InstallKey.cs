using System;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;

namespace Stellar.WorldScreen.Identity;

/// <summary>
/// Per-install ECDSA P-256 identity. The private key is generated locally and stored in prefs
/// (never transmitted, never shared) so there is no shared secret to leak. Signs the canonical write
/// payloads the stellar-portal backend expects, in IEEE-P1363 base64 (its <c>verify.ts</c>-compatible
/// format — see services/stellar-portal/docs/api.md). Crypto goes through the managed
/// <see cref="Es256"/> so key-gen and signing work under Wine/Proton, where the OS/CNG-backed
/// <c>System.Security.Cryptography.ECDsa</c> throws NTE_NOT_SUPPORTED. Copied from
/// StellarCombatMeterPlugin/LogUpload/InstallKey.cs (proven under Wine/Proton) and renamespaced;
/// crypto semantics are unchanged, only the pref key is portal-specific.
/// </summary>
internal sealed class InstallKey : IDisposable
{
    private const string PrefInstallKey = "worldportal.installKey"; // base64 PKCS#8 private key
    private readonly ECPrivateKeyParameters _priv;
    private readonly ECPublicKeyParameters _pub;

    private InstallKey(ECPrivateKeyParameters priv)
    {
        _priv = priv;
        _pub = Es256.DerivePublic(priv);
    }

    /// <summary>Loads the stored per-install key, or generates + persists one on first use (or if the
    /// stored value is corrupt). Prefs access is injected so this is unit-testable off-game.</summary>
    internal static InstallKey LoadOrCreate(Func<string, string?> getPref, Action<string, string> setPref)
    {
        var stored = getPref(PrefInstallKey);
        if (!string.IsNullOrEmpty(stored))
        {
            try { return new InstallKey(Es256.ImportPkcs8(Convert.FromBase64String(stored!))); }
            catch { /* corrupt pref → regenerate below */ }
        }
        var priv = (ECPrivateKeyParameters)Es256.Generate().Private;
        setPref(PrefInstallKey, Convert.ToBase64String(Es256.ExportPkcs8(priv)));
        return new InstallKey(priv);
    }

    /// <summary>SPKI (SubjectPublicKeyInfo) base64 — what the backend's <c>importPublicKey("spki", …)</c> expects.</summary>
    internal string PubKeySpkiBase64 => Convert.ToBase64String(Es256.ExportSpki(_pub));

    /// <summary>Base64 IEEE-P1363 signature over <paramref name="payload"/> (P-256 / SHA-256).</summary>
    internal string SignInstall(string payload)
        => Convert.ToBase64String(Es256.SignP1363(_priv, Encoding.UTF8.GetBytes(payload)));

    public void Dispose() { /* BouncyCastle key parameters hold no unmanaged resources */ }
}
