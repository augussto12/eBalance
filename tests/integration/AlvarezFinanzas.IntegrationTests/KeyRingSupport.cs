using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AlvarezFinanzas.Infrastructure;

namespace AlvarezFinanzas.IntegrationTests;

/// <summary>
/// Key ring como en producción: directorio de claves más un certificado PFX con contraseña en archivo, ambos temporales y
/// generados en el momento (nada fijo en el repositorio). Permite simular la pérdida o el reemplazo del volumen de claves.
/// </summary>
public sealed class TestKeyRing : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alvarez-ring-" + Guid.NewGuid().ToString("N"));

    public TestKeyRing()
    {
        Directory.CreateDirectory(KeysPath);
        Directory.CreateDirectory(Path.GetDirectoryName(CertificatePath)!);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=alvarez-finanzas-keyring-tests", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        File.WriteAllBytes(CertificatePath, cert.Export(X509ContentType.Pfx, password));
        File.WriteAllText(PasswordPath, password);
    }

    public string KeysPath => Path.Combine(_root, "keys");
    public string CertificatePath => Path.Combine(_root, "keyring", "current.pfx");
    public string PasswordPath => Path.Combine(_root, "keyring", "current.password");

    public Dictionary<string, string> Settings(IDictionary<string, string>? extra = null)
    {
        var settings = new Dictionary<string, string>
        {
            ["App:KeyRingCertificatePath"] = CertificatePath,
            ["App:KeyRingCertificatePasswordFile"] = PasswordPath,
        };
        foreach (var (k, v) in extra ?? new Dictionary<string, string>()) settings[k] = v;
        return settings;
    }

    public string[] KeyFiles() => Directory.GetFiles(KeysPath, "key-*.xml").Order(StringComparer.Ordinal).ToArray();

    /// <summary>Copia de las claves (como el key ring de un backup).</summary>
    public Dictionary<string, byte[]> Snapshot() => KeyFiles().ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);

    public void Restore(Dictionary<string, byte[]> snapshot)
    {
        foreach (var (name, bytes) in snapshot) File.WriteAllBytes(Path.Combine(KeysPath, name), bytes);
    }

    /// <summary>Volumen de claves perdido: el directorio queda vacío, como cuando Docker lo recrea desde la imagen.</summary>
    public void Lose()
    {
        foreach (var f in KeyFiles()) File.Delete(f);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Ejecuta un comando de la CLI operativa en el mismo proceso, con la configuración por argumentos.</summary>
public static class CliRunner
{
    public static async Task<(int Code, string Output)> RunAsync(string command, string connectionString, string keyRingPath,
        TestKeyRing? ring = null, string environment = "Production", string? input = null, params string[] extra)
    {
        var args = new List<string>
        {
            command,
            $"--environment={environment}",
            $"--ConnectionStrings:App={connectionString}",
            $"--App:PublicOrigin={TestApp.Origin}",
            "--App:DataProtectionApplicationName=alvarez-finanzas-tests",
            $"--App:KeyRingPath={keyRingPath}",
        };
        if (ring is not null)
        {
            args.Add($"--App:KeyRingCertificatePath={ring.CertificatePath}");
            args.Add($"--App:KeyRingCertificatePasswordFile={ring.PasswordPath}");
        }
        args.AddRange(extra);
        var (stdout, stderr, stdin) = (Console.Out, Console.Error, Console.In);
        var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        Console.SetIn(new StringReader(input ?? ""));
        try
        {
            var code = await Cli.RunAsync([.. args]);
            return (code, output.ToString());
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Console.SetIn(stdin);
        }
    }
}
