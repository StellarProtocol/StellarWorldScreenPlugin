// Off-game unit tests for the per-install ECDSA identity (SP-1c Task 2). Verification deliberately
// goes through BouncyCastle (not System.Security.Cryptography.ECDsa) so the whole sign->verify round
// trip stays on the SAME managed crypto stack InstallKey/Es256 use — the thing that must work under
// Wine/Proton (see Es256.cs header) — proving the SPKI pubkey + raw P1363 signature are self-consistent
// independent of any OS/CNG-backed provider. This is also what the backend's verify.ts (crypto.subtle
// importKey("spki", ...) + verify ECDSA/SHA-256/P1363) will accept — see services/stellar-portal/docs/api.md.
using System;
using System.Collections.Generic;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Stellar.WorldScreen.Identity;
using Xunit;

namespace Stellar.WorldScreen.Tests;

public class InstallKeyTests
{
    private static (Func<string, string?> get, Action<string, string> set) FakePrefs(Dictionary<string, string> store)
        => (k => store.TryGetValue(k, out var v) ? v : null, (k, v) => store[k] = v);

    /// <summary>Re-derives whether <paramref name="sig"/> (raw P1363 r‖s) verifies over
    /// <paramref name="message"/> under the SPKI-encoded public key — entirely via BouncyCastle,
    /// mirroring what a WebCrypto-based backend verifier does for an IEEE-P1363 signature.</summary>
    private static bool VerifyP1363(byte[] spki, byte[] message, byte[] sig)
    {
        var pub = (ECPublicKeyParameters)PublicKeyFactory.CreateKey(spki);

        var hash = new byte[32];
        var digest = new Sha256Digest();
        digest.BlockUpdate(message, 0, message.Length);
        digest.DoFinal(hash, 0);

        int size = (pub.Parameters.N.BitLength + 7) / 8; // 32 for P-256
        Assert.Equal(size * 2, sig.Length);
        var r = new BigInteger(1, sig, 0, size);
        var s = new BigInteger(1, sig, size, size);

        var verifier = new ECDsaSigner();
        verifier.Init(false, pub);
        return verifier.VerifySignature(hash, r, s);
    }

    [Fact]
    public void LoadOrCreate_GeneratesAndPersists_OnFirstCall()
    {
        var store = new Dictionary<string, string>();
        var (get, set) = FakePrefs(store);

        using var key = InstallKey.LoadOrCreate(get, set);

        Assert.False(string.IsNullOrEmpty(key.PubKeySpkiBase64));
        // The setPref closure must actually be invoked (the key is persisted, not just held in memory).
        var stored = Assert.Single(store);
        Assert.Equal("worldportal.installKey", stored.Key);
        Assert.False(string.IsNullOrEmpty(stored.Value));
    }

    [Fact]
    public void LoadOrCreate_SecondLoad_ReusesStoredKey_StableIdentityAcrossReload()
    {
        var store = new Dictionary<string, string>();
        var (get, set) = FakePrefs(store);

        using var a = InstallKey.LoadOrCreate(get, set);
        var storedAfterFirst = store["worldportal.installKey"];

        using var b = InstallKey.LoadOrCreate(get, set); // reads the SAME stored pref value

        Assert.Equal(a.PubKeySpkiBase64, b.PubKeySpkiBase64);
        Assert.Equal(storedAfterFirst, store["worldportal.installKey"]); // unchanged, not regenerated
    }

    [Fact]
    public void SignInstall_ReturnsNonEmptyBase64_DecodingToExactly64Bytes_NotDer()
    {
        var store = new Dictionary<string, string>();
        var (get, set) = FakePrefs(store);
        using var key = InstallKey.LoadOrCreate(get, set);

        var sigB64 = key.SignInstall("x");
        Assert.False(string.IsNullOrEmpty(sigB64));

        var sig = Convert.FromBase64String(sigB64);
        // P-256 IEEE-P1363 is r‖s, each 32 bytes = 64 total. A DER encoding (0x30 SEQUENCE header +
        // two variable-length INTEGERs) would instead run ~70-72 bytes and start with 0x30 — pin the
        // exact length so a regression to DER (e.g. from a plain System.Security.Cryptography.ECDsa
        // .SignData without an explicit P1363 signature format) fails loudly here.
        Assert.Equal(64, sig.Length);
    }

    [Fact]
    public void SignInstall_Verifies_ViaBouncyCastle_AgainstItsOwnSpkiPubKey()
    {
        var store = new Dictionary<string, string>();
        var (get, set) = FakePrefs(store);
        using var key = InstallKey.LoadOrCreate(get, set);

        const string payload = "claim|world-portal|K7-42QX|nonce-abc";
        var spki = Convert.FromBase64String(key.PubKeySpkiBase64);
        var sig = Convert.FromBase64String(key.SignInstall(payload));

        Assert.True(VerifyP1363(spki, Encoding.UTF8.GetBytes(payload), sig));
    }

    [Fact]
    public void SignInstall_TamperedPayload_FailsVerification()
    {
        var store = new Dictionary<string, string>();
        var (get, set) = FakePrefs(store);
        using var key = InstallKey.LoadOrCreate(get, set);

        var spki = Convert.FromBase64String(key.PubKeySpkiBase64);
        var sig = Convert.FromBase64String(key.SignInstall("original-payload"));

        Assert.False(VerifyP1363(spki, Encoding.UTF8.GetBytes("tampered-payload"), sig));
    }

    [Fact]
    public void SignInstall_DifferentPayloads_ProduceDifferentSignatures_BothVerify()
    {
        var store = new Dictionary<string, string>();
        var (get, set) = FakePrefs(store);
        using var key = InstallKey.LoadOrCreate(get, set);
        var spki = Convert.FromBase64String(key.PubKeySpkiBase64);

        var sigA = Convert.FromBase64String(key.SignInstall("payload-a"));
        var sigB = Convert.FromBase64String(key.SignInstall("payload-b"));

        Assert.NotEqual(Convert.ToBase64String(sigA), Convert.ToBase64String(sigB));
        Assert.True(VerifyP1363(spki, Encoding.UTF8.GetBytes("payload-a"), sigA));
        Assert.True(VerifyP1363(spki, Encoding.UTF8.GetBytes("payload-b"), sigB));
    }

    [Fact]
    public void SignInstall_SamePayloadTwice_IsDeterministic_RFC6979_AndBothVerify()
    {
        // Es256.SignP1363 uses HMacDsaKCalculator (RFC-6979 deterministic k), by design (see Es256.cs
        // header: "no runtime RNG required at sign time"), so signing the SAME payload twice yields a
        // byte-identical signature — not a fresh random one. This pins that deliberate determinism
        // rather than asserting randomized-ECDSA behavior this implementation does not have.
        var store = new Dictionary<string, string>();
        var (get, set) = FakePrefs(store);
        using var key = InstallKey.LoadOrCreate(get, set);
        var spki = Convert.FromBase64String(key.PubKeySpkiBase64);

        var sig1 = key.SignInstall("same-payload");
        var sig2 = key.SignInstall("same-payload");

        Assert.Equal(sig1, sig2);
        Assert.True(VerifyP1363(spki, Encoding.UTF8.GetBytes("same-payload"), Convert.FromBase64String(sig1)));
    }

    [Fact]
    public void LoadOrCreate_CorruptStoredValue_RegeneratesInstead_OfThrowing()
    {
        var store = new Dictionary<string, string> { ["worldportal.installKey"] = "not-valid-base64-pkcs8!!" };
        var (get, set) = FakePrefs(store);

        using var key = InstallKey.LoadOrCreate(get, set);

        Assert.False(string.IsNullOrEmpty(key.PubKeySpkiBase64));
        Assert.NotEqual("not-valid-base64-pkcs8!!", store["worldportal.installKey"]);
    }
}
