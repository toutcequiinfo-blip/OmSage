using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sage100Api;

/// <summary>
/// Clé publique qui vérifie les licences. Intégrée à la compilation depuis lib\licence\cle-publique.xml (créée sur le poste de
/// développement par deploy\licence\generer-licence.ps1, jamais dans Git). Sans elle, la licence n'est pas contrôlée :
/// c'est le cas des compilations de test ; fabriquer-installateur.ps1 refuse de fabriquer un Setup sans elle.
/// </summary>
public sealed record ClePubliqueLicence(string? Xml)
{
    public const string Ressource = "Sage100Api.cle-publique-licence.xml";

    public static ClePubliqueLicence Integree()
    {
        using var flux = Assembly.GetExecutingAssembly().GetManifestResourceStream(Ressource);
        if (flux == null) return new((string?)null);
        using var lecteur = new StreamReader(flux, Encoding.UTF8);
        return new(lecteur.ReadToEnd());
    }
}

/// <summary>Contenu signé d'une licence : client, serveur (identifiant Windows de la machine), bases Sage couvertes, échéance.</summary>
public sealed class ContenuLicence
{
    public int Version { get; set; } = 1;
    public string Client { get; set; } = "";
    /// <summary>Identifiant de la machine (MachineGuid de Windows), en minuscules.</summary>
    public string Serveur { get; set; } = "";
    public string? NomServeur { get; set; }
    /// <summary>Bases Gestion commerciale couvertes ; « * » = toutes.</summary>
    public List<string> Bases { get; set; } = [];
    public DateTime? Expiration { get; set; }
    public DateTime? Emise { get; set; }

    public bool CouvreBase(string? code) =>
        Bases.Any(b => b == "*" || b.Equals(code?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Fichier licence.lic : contenu (JSON en base64) et sa signature RSA SHA-256 ; client et serveur répétés en clair pour la lecture.</summary>
public sealed class FichierLicence
{
    public string? Client { get; set; }
    public string? NomServeur { get; set; }
    public string Contenu { get; set; } = "";
    public string Signature { get; set; } = "";
}

public sealed record EtatLicence(bool Valide, bool Controlee, string Message, ContenuLicence? Contenu, string IdentifiantServeur, string? Fichier);

/// <summary>
/// Licence de l'installation : sans licence valide pour ce serveur, l'API refuse de servir (sauf /sante et /licence),
/// et une société hors licence est refusée. Le fichier est relu dès qu'il change : déposer licence.lic suffit, sans redémarrer.
/// Emplacements : Licence:Fichier si renseigné, sinon licence.lic dans le dossier d'installation (C:\Sage100Api) ou à côté de l'API.
/// </summary>
public sealed class ServiceLicence(ClePubliqueLicence cle, IConfiguration configuration, IWebHostEnvironment environnement, ILogger<ServiceLicence> log)
{
    public const string CodeLicence = "LICENCE";
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    readonly object _verrou = new();
    (string? Fichier, DateTime Date, DateTime Lu, EtatLicence Etat)? _cache;

    public static string IdentifiantMachine()
    {
        if (OperatingSystem.IsWindows())
        {
            // Vue 64 bits du registre : la clé n'existe pas dans la vue 32 bits.
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var crypto = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (crypto?.GetValue("MachineGuid") is string guid && guid.Length > 0) return guid.Trim().ToLowerInvariant();
        }
        foreach (var f in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
            if (File.Exists(f)) return File.ReadAllText(f).Trim().ToLowerInvariant();
        return Environment.MachineName.ToLowerInvariant();
    }

    IEnumerable<string> Emplacements()
    {
        var configure = configuration["Licence:Fichier"];
        if (!string.IsNullOrWhiteSpace(configure)) yield return Path.GetFullPath(configure, environnement.ContentRootPath);
        var racine = Path.GetFullPath(environnement.ContentRootPath);
        if (Path.GetDirectoryName(racine.TrimEnd(Path.DirectorySeparatorChar)) is { } parent) yield return Path.Combine(parent, "licence.lic");
        yield return Path.Combine(racine, "licence.lic");
    }

    public EtatLicence Etat()
    {
        var id = IdentifiantMachine();
        if (string.IsNullOrWhiteSpace(cle.Xml)) return new(true, false, "Licence non contrôlée (compilation sans clé de licence).", null, id, null);
        var fichier = Emplacements().FirstOrDefault(File.Exists);
        var date = fichier == null ? DateTime.MinValue : File.GetLastWriteTimeUtc(fichier);
        lock (_verrou)
        {
            // Relu si le fichier change, et chaque heure pour l'échéance.
            if (_cache is { } c && c.Fichier == fichier && c.Date == date && DateTime.UtcNow - c.Lu < TimeSpan.FromHours(1)) return c.Etat;
            var etat = Lire(fichier, id);
            if (_cache?.Etat.Message != etat.Message)
            {
                if (etat.Valide) log.LogInformation("Licence : {Message}", etat.Message);
                else log.LogError("Licence : {Message}", etat.Message);
            }
            _cache = (fichier, date, DateTime.UtcNow, etat);
            return etat;
        }
    }

    EtatLicence Lire(string? fichier, string id)
    {
        if (fichier == null)
            return new(false, true, $"Aucune licence installée. Identifiant de ce serveur : {id}. Déposez le fichier licence.lic dans le dossier de l'installation.", null, id, null);
        try
        {
            var f = JsonSerializer.Deserialize<FichierLicence>(File.ReadAllText(fichier), Json) ?? throw new FormatException();
            var octets = Convert.FromBase64String(f.Contenu);
            using var rsa = RSA.Create();
            rsa.FromXmlString(cle.Xml!);
            if (!rsa.VerifyData(octets, Convert.FromBase64String(f.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return new(false, true, "Licence invalide : le fichier a été modifié ou ne vient pas de l'éditeur.", null, id, fichier);
            var contenu = JsonSerializer.Deserialize<ContenuLicence>(octets, Json) ?? throw new FormatException();
            if (!contenu.Serveur.Trim().Equals(id, StringComparison.OrdinalIgnoreCase))
                return new(false, true, $"Cette licence est faite pour un autre serveur ({contenu.NomServeur ?? contenu.Serveur}). Identifiant de ce serveur : {id}.", contenu, id, fichier);
            if (contenu.Expiration is { } fin && DateTime.Today > fin.Date)
                return new(false, true, $"Licence de {contenu.Client} expirée le {fin:dd/MM/yyyy}.", contenu, id, fichier);
            var bases = contenu.Bases.Contains("*") ? "toutes les sociétés" : string.Join(", ", contenu.Bases);
            return new(true, true, $"Licence de {contenu.Client} ({bases}){(contenu.Expiration is { } e ? $", valable jusqu'au {e:dd/MM/yyyy}" : "")}.", contenu, id, fichier);
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            return new(false, true, $"Fichier de licence illisible ({e.Message}).", null, id, fichier);
        }
    }

    /// <summary>Message de refus pour cette société, ou null si la licence la couvre.</summary>
    public string? Refus(string? societe)
    {
        var e = Etat();
        if (!e.Controlee) return null;
        if (!e.Valide) return e.Message;
        return e.Contenu!.CouvreBase(societe) ? null : $"La société {societe} n'est pas couverte par la licence de {e.Contenu.Client}.";
    }
}

/// <summary>Refuse les requêtes /api sans licence valide (après le choix de la société). /sante et /licence restent ouverts.</summary>
public sealed class ControleLicence(RequestDelegate suivant, ServiceLicence licence, Dossiers dossiers)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var chemin = http.Request.Path;
        if (chemin.StartsWithSegments("/api") && !chemin.StartsWithSegments("/api/v1/sante") && !chemin.StartsWithSegments("/api/v1/licence")
            && licence.Refus(dossiers.Courant.Code) is { } refus)
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { code = ServiceLicence.CodeLicence, message = refus });
            return;
        }
        await suivant(http);
    }
}
