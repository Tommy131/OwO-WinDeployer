using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WinDeploy.App.Services.Ftp;

/// <summary>Loads (or generates) the X.509 certificate the FTPS listener authenticates with.</summary>
public static class FtpCertificate
{
    private static readonly string AutoCertPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinDeploy", "ftp-autocert.pfx");

    /// <summary>Resolve the server certificate: a configured .pfx, a PEM cert+key pair, or — when none is
    /// configured — a cached self-signed cert (created on first use). The result is always re-imported via
    /// PKCS#12 so its private key is usable by SslStream/SChannel on Windows (the well-known ephemeral-key
    /// workaround).</summary>
    public static X509Certificate2 Resolve(FtpServerConfig cfg)
    {
        var path = cfg.CertPath?.Trim();
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".pfx" or ".p12")
                return Reimport(X509CertificateLoader.LoadPkcs12FromFile(path, cfg.CertPassword));

            // PEM certificate (+ optional separate PEM key)
            var keyPath = cfg.KeyPath?.Trim();
            var loaded = !string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath)
                ? X509Certificate2.CreateFromPemFile(path, keyPath)
                : X509Certificate2.CreateFromPemFile(path);
            using (loaded) return Reimport(loaded);
        }

        return LoadOrCreateSelfSigned();
    }

    private static X509Certificate2 LoadOrCreateSelfSigned()
    {
        try
        {
            if (File.Exists(AutoCertPath))
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(AutoCertPath, null);
                if (existing.NotAfter > DateTime.Now.AddDays(1)) return Reimport(existing);
                existing.Dispose();   // expired — regenerate below
            }
        }
        catch { /* regenerate */ }

        using var cert = GenerateSelfSigned($"{Environment.MachineName} WinDeploy FTP");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AutoCertPath)!);
            File.WriteAllBytes(AutoCertPath, cert.Export(X509ContentType.Pfx));
        }
        catch { /* cache is best-effort */ }
        return Reimport(cert);
    }

    private static X509Certificate2 GenerateSelfSigned(string cn)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName);
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // server auth
        var now = DateTimeOffset.UtcNow;
        return req.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));
    }

    /// <summary>Round-trip through PKCS#12 so the private key is associated in a way SslStream can use as a
    /// server credential (an ephemeral CreateFromPem key otherwise fails on Windows).</summary>
    private static X509Certificate2 Reimport(X509Certificate2 cert)
    {
        var pfx = cert.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }
}
