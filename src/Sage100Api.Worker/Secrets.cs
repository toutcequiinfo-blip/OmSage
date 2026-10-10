using System;
using System.Security.Cryptography;
using System.Text;

namespace Sage100Api.Worker
{
    /// <summary>
    /// Secrets de worker.json chiffrés par Windows (DPAPI, portée machine) : « dpapi:… ». Même préfixe, même entropie
    /// et même portée que l'API (Sage100Api.Secrets) et que outils\securite.ps1. Une valeur en clair reste acceptée.
    /// </summary>
    internal static class Secrets
    {
        const string Prefixe = "dpapi:";
        static readonly byte[] Entropie = Encoding.UTF8.GetBytes("Sage100Api.secrets.v1");

        public static string Devoiler(string valeur)
        {
            if (valeur == null || !valeur.StartsWith(Prefixe, StringComparison.Ordinal)) return valeur!;
            try
            {
                var octets = ProtectedData.Unprotect(Convert.FromBase64String(valeur.Substring(Prefixe.Length)), Entropie, DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(octets);
            }
            catch (Exception e) when (e is CryptographicException || e is FormatException)
            {
                throw new InvalidOperationException("Mot de passe de worker.json illisible : il a été chiffré sur un autre serveur. Ressaisissez-le (relancez l'installation).", e);
            }
        }
    }
}
