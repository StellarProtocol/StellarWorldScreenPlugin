// PURE managed BouncyCastle — no System.Security.Cryptography.ECDsa (CNG-backed) anywhere in this
// file. Under Wine/Proton, ECDsa's SignData throws NTE_NOT_SUPPORTED (0x80090029); BouncyCastle is
// pure managed code and works identically on Windows and Wine. Copied from
// StellarCombatMeterPlugin/LogUpload/Es256.cs (proven under Wine/Proton) and renamespaced for
// StellarWorldScreen's SP-1c per-install identity — crypto semantics are unchanged byte-for-byte.
using System;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.X509;

namespace Stellar.WorldScreen.Identity;

/// <summary>
/// Managed ECDSA P-256 / SHA-256 primitives (BouncyCastle) shared by <see cref="InstallKey"/> (and,
/// later, the portal-write signer). Pure managed code: unlike
/// <c>System.Security.Cryptography.ECDsa</c> (OS/CNG-backed), its <c>SignData</c> equivalent works
/// under Wine/Proton (the CNG path throws NTE_NOT_SUPPORTED, 0x80090029). Output is byte-compatible
/// with the backend verifier: named-curve PKCS#8 private keys, SPKI (uncompressed-point) public keys,
/// and IEEE-P1363 raw r‖s 64-byte signatures over SHA-256 — see
/// services/stellar-portal/docs/api.md.
/// </summary>
internal static class Es256
{
    /// <summary>Generates a fresh P-256 key pair carrying the prime256v1 named-curve OID.</summary>
    internal static AsymmetricCipherKeyPair Generate()
    {
        var gen = new ECKeyPairGenerator("ECDSA");
        gen.Init(new ECKeyGenerationParameters(X9ObjectIdentifiers.Prime256v1, new SecureRandom()));
        return gen.GenerateKeyPair();
    }

    /// <summary>Imports a base64-less PKCS#8 DER private key.</summary>
    internal static ECPrivateKeyParameters ImportPkcs8(byte[] pkcs8)
        => (ECPrivateKeyParameters)PrivateKeyFactory.CreateKey(pkcs8);

    /// <summary>Named-curve PKCS#8 DER of the private key (re-importable by BouncyCastle and .NET).</summary>
    internal static byte[] ExportPkcs8(ECPrivateKeyParameters priv)
        => PrivateKeyInfoFactory.CreatePrivateKeyInfo(priv).GetDerEncoded();

    /// <summary>Derives the public key from the private scalar, preserving the named-curve OID.</summary>
    internal static ECPublicKeyParameters DerivePublic(ECPrivateKeyParameters priv)
    {
        var q = priv.Parameters.G.Multiply(priv.D).Normalize();
        return priv.PublicKeyParamSet != null
            ? new ECPublicKeyParameters("ECDSA", q, priv.PublicKeyParamSet)
            : new ECPublicKeyParameters("ECDSA", q, priv.Parameters);
    }

    /// <summary>SPKI (SubjectPublicKeyInfo) DER — what the backend's <c>importPublicKey("spki", …)</c> expects.</summary>
    internal static byte[] ExportSpki(ECPublicKeyParameters pub)
        => SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(pub).GetDerEncoded();

    /// <summary>
    /// Signs <paramref name="message"/> as ECDSA P-256 over SHA-256, returning the IEEE-P1363 raw
    /// signature (r||s, each left-padded to 32 bytes = 64 bytes total). Uses RFC-6979 deterministic k
    /// so no runtime RNG is required at sign time; the backend verifies with WebCrypto's raw
    /// P1363-format ECDSA verify.
    /// </summary>
    internal static byte[] SignP1363(ECPrivateKeyParameters priv, byte[] message)
    {
        var hash = new byte[32];
        var digest = new Sha256Digest();
        digest.BlockUpdate(message, 0, message.Length);
        digest.DoFinal(hash, 0);

        var signer = new ECDsaSigner(new HMacDsaKCalculator(new Sha256Digest()));
        signer.Init(true, priv);
        var rs = signer.GenerateSignature(hash);

        int size = (priv.Parameters.N.BitLength + 7) / 8; // 32 for P-256
        var sig = new byte[size * 2];
        Array.Copy(BigIntegers.AsUnsignedByteArray(size, rs[0]), 0, sig, 0, size);
        Array.Copy(BigIntegers.AsUnsignedByteArray(size, rs[1]), 0, sig, size, size);
        return sig;
    }
}
