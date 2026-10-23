namespace AlvarezFinanzas.Infrastructure;

/// <summary>Configuración no secreta (appsettings / variables de entorno con prefijo ALVAREZ_).</summary>
public sealed class AppOptions
{
    /// <summary>Origen público esperado, p. ej. https://finanzas.ejemplo.com. Se usa para validar Origin/Referer.</summary>
    public string PublicOrigin { get; set; } = "";
    /// <summary>Nombre estable de aplicación para Data Protection. Distinto por entorno.</summary>
    public string DataProtectionApplicationName { get; set; } = "";
    public string KeyRingPath { get; set; } = "";
    /// <summary>Certificado PFX que cifra el key ring (archivo montado como secreto).</summary>
    public string? KeyRingCertificatePath { get; set; }
    public string? KeyRingCertificatePasswordFile { get; set; }
    /// <summary>
    /// Verificar al arrancar que el key ring descifra el centinela guardado en la base (si no, la API no arranca). Siempre
    /// activo en producción, aunque valga false; en otros entornos se activa con true y requiere KeyRingPath.
    /// </summary>
    public bool VerifyKeyRingAtStartup { get; set; }
    /// <summary>Proxies de confianza para X-Forwarded-For (IPs o CIDR separadas por coma).</summary>
    public string TrustedProxies { get; set; } = "";
    public int SessionIdleMinutes { get; set; } = 30;
    public int SessionAbsoluteHours { get; set; } = 12;
    public int PreAuthMinutes { get; set; } = 5;
    public int ElevationMinutes { get; set; } = 5;
    public int ImportMaxBytes { get; set; } = 5 * 1024 * 1024;
    public int ImportMaxRecords { get; set; } = 10_000;
    public int AuditRetentionDays { get; set; } = 90;
    public int LoginPerAccountPerMinute { get; set; } = 5;
    public int LoginPerIpPerMinute { get; set; } = 20;
    public int MfaPerMinute { get; set; } = 5;
    public int ImportExportPerHour { get; set; } = 5;
    /// <summary>Habilita el endpoint de datos ficticios. Solo se respeta en Development.</summary>
    public bool EnableDemoData { get; set; }
}

/// <summary>
/// Lector explícito de secretos desde archivos montados. ASP.NET no convierte automáticamente variables *_FILE.
/// </summary>
public static class SecretFiles
{
    public static string Read(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException($"Falta la ruta del secreto: {description}.");
        if (!File.Exists(path)) throw new InvalidOperationException($"No existe el archivo de secreto para {description}.");
        var value = File.ReadAllText(path).TrimEnd('\r', '\n');
        if (value.Length == 0) throw new InvalidOperationException($"El secreto {description} está vacío.");
        return value;
    }

    /// <summary>
    /// Arma la cadena de conexión: la contraseña sale de un archivo (*_FILE), nunca de la configuración en texto.
    /// </summary>
    public static string BuildConnectionString(IConfiguration config, string name)
    {
        var baseCs = config.GetConnectionString(name)
            ?? throw new InvalidOperationException($"Falta ConnectionStrings:{name}.");
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(baseCs);
        var passwordFile = config[$"ConnectionStrings:{name}PasswordFile"];
        if (!string.IsNullOrEmpty(passwordFile))
        {
            builder.Password = Read(passwordFile, $"contraseña de base ({name})");
        }
        return builder.ConnectionString;
    }
}
