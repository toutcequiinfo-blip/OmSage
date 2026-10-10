using System.Security.Cryptography;
using System.Text;

namespace Sage100Api;

/// <summary>
/// Secrets des fichiers de configuration (mots de passe, chaînes SQL avec mot de passe, clés d'API, clé des jetons),
/// chiffrés par Windows (DPAPI, portée machine) : « dpapi:… ». Seul ce serveur sait les relire : copiés sur un autre poste,
/// ils sont illisibles. Chiffrés par outils\securite.ps1 à l'installation ; une valeur en clair reste acceptée.
/// Même chiffrement dans le worker (WorkerConfig) et dans securite.ps1 : préfixe, entropie et portée doivent rester identiques.
/// </summary>
public static class Secrets
{
    public const string Prefixe = "dpapi:";
    static readonly byte[] Entropie = Encoding.UTF8.GetBytes("Sage100Api.secrets.v1");

    public static bool EstProtege(string? valeur) => valeur?.StartsWith(Prefixe, StringComparison.Ordinal) == true;

    public static string Devoiler(string valeur)
    {
        if (!EstProtege(valeur)) return valeur;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Secret chiffré par Windows : illisible hors de Windows.");
        try
        {
            var octets = ProtectedData.Unprotect(Convert.FromBase64String(valeur[Prefixe.Length..]), Entropie, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(octets);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            throw new InvalidOperationException("Secret de configuration illisible : il a été chiffré sur un autre serveur. Ressaisissez-le (relancez l'installation).", e);
        }
    }

    public static string Proteger(string valeur)
    {
        if (EstProtege(valeur) || !OperatingSystem.IsWindows()) return valeur;
        return Prefixe + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(valeur), Entropie, DataProtectionScope.LocalMachine));
    }

    /// <summary>
    /// Ajoute par-dessus la configuration les secrets déchiffrés. Lu au démarrage : après un changement de mot de passe,
    /// les scripts redémarrent les services.
    /// </summary>
    public static void DevoilerConfiguration(ConfigurationManager configuration)
    {
        var devoiles = configuration.AsEnumerable().Where(x => EstProtege(x.Value)).ToDictionary(x => x.Key, x => (string?)Devoiler(x.Value!));
        if (devoiles.Count > 0) configuration.AddInMemoryCollection(devoiles);
    }
}
