using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Sage100Api.Contracts;
using Sage100Api.Lectures;
using Sage100Api.Worker;

namespace Sage100Api;

/// <summary>
/// Utilisateur connecté, porté par le jeton. Collaborateur : fiche collaborateur Sage rattachée au login
/// (mise sur les bons de commande) ; Caissier : case Caissier de cette fiche.
/// </summary>
public sealed record Utilisateur(string Login, bool Administrateur, int? Collaborateur, string? Nom, string? Prenom, bool Vendeur, bool Caissier, DateTime Expiration,
    string? Dossier = null)
{
    public bool PeutEncaisser => Caissier || Administrateur;

    public Auteur Auteur() => new() { Utilisateur = Login, CollaborateurNom = Nom, CollaborateurPrenom = Prenom };
}

public sealed record ResultatConnexion(Utilisateur? Utilisateur, string? Jeton, string? CodeErreur, string? Message);

/// <summary>
/// Connexion des utilisateurs avec leur login Sage : le worker ouvre Sage avec ce login et ce mot de passe
/// (c'est Sage qui les vérifie), puis l'API remet un jeton signé, valable <see cref="AuthentificationOptions.DureeHeures"/>.
/// Aucun mot de passe n'est gardé par l'API.
/// </summary>
public sealed class ServiceAuthentification
{
    public const string CodeConnexionRequise = "CONNEXION_REQUISE";
    public const string CodeDroitRefuse = "DROIT_REFUSE";
    public const string CodeTropDEssais = "TROP_D_ESSAIS";
    public const string CodeDossierRequis = "DOSSIER_REQUIS";
    const int EssaisMax = 5;
    static readonly TimeSpan Blocage = TimeSpan.FromMinutes(2);

    readonly IOptionsMonitor<AuthentificationOptions> _options;
    readonly IWorkerClient _worker;
    readonly ILecturesSage _lectures;
    readonly Dossiers _dossiers;
    readonly ILogger<ServiceAuthentification> _log;
    readonly byte[] _cle;
    readonly ConcurrentDictionary<string, (int Echecs, DateTime Depuis)> _echecs = new(StringComparer.OrdinalIgnoreCase);

    public ServiceAuthentification(IOptionsMonitor<AuthentificationOptions> options, IOptions<SageOptions> sage, IWorkerClient worker,
        ILecturesSage lectures, Dossiers dossiers, ILogger<ServiceAuthentification> log)
    {
        _options = options;
        _dossiers = dossiers;
        _worker = worker;
        _lectures = lectures;
        _log = log;
        _cle = Cle(options.CurrentValue.CleSignature, sage.Value.CheminJournal);
    }

    public AuthentificationOptions Options => _options.CurrentValue;

    public async Task<ResultatConnexion> Connecter(ConnexionRequest demande, CancellationToken ct)
    {
        var login = demande.Utilisateur.Trim();
        if (login.Length == 0)
            return new(null, null, CodesErreur.AccesRefuse, "Saisissez votre nom d'utilisateur Sage.");
        if (_echecs.TryGetValue(login, out var e) && e.Echecs >= EssaisMax && DateTime.UtcNow - e.Depuis < Blocage)
            return new(null, null, CodeTropDEssais, $"Trop d'essais pour {login}. Réessayez dans {Blocage.TotalMinutes:0} minutes.");

        // Société choisie à la connexion : Sage vérifie le login sur cette base, et le jeton ne servira que pour elle.
        if (string.IsNullOrWhiteSpace(demande.Dossier) && _dossiers.Multiple)
            return new(null, null, CodeDossierRequis, "Choisissez la société.");
        var dossier = _dossiers.Trouver(demande.Dossier);
        if (dossier == null)
            return new(null, null, CodeDossierRequis, $"Société inconnue : {demande.Dossier}.");
        using var societe = Dossiers.Activer(dossier.Code);

        var reponse = await _worker.Envoyer(Operations.VerifierUtilisateur, new ConnexionRequest { Utilisateur = login, MotDePasse = demande.MotDePasse }, ct);
        if (!reponse.Ok || reponse.Resultat is null)
        {
            if (reponse.CodeErreur == CodesErreur.AccesRefuse)
            {
                _echecs.AddOrUpdate(login, _ => (1, DateTime.UtcNow), (_, v) => DateTime.UtcNow - v.Depuis < Blocage ? (v.Echecs + 1, v.Depuis) : (1, DateTime.UtcNow));
                _log.LogWarning("Connexion refusée pour {Login} : {Message}", login, reponse.MessageErreur);
            }
            return new(null, null, reponse.CodeErreur ?? CodesErreur.Technique, reponse.MessageErreur ?? "Erreur inconnue.");
        }
        _echecs.TryRemove(login, out _);

        var verifie = reponse.Resultat.Value.Deserialize<UtilisateurVerifie>(WorkerProtocol.Json)!;
        Collaborateur? co = null;
        try
        {
            co = await _lectures.CollaborateurUtilisateur(login);
        }
        catch (Exception ex)
        {
            // La connexion reste valable : seuls le collaborateur sur la commande et le droit de caissier manqueront.
            _log.LogError(ex, "Lecture du collaborateur de {Login} impossible", login);
        }

        var u = new Utilisateur(login, verifie.Administrateur, co?.Numero, co?.Nom?.Trim(), co?.Prenom?.Trim(), co?.Vendeur ?? false, co?.Caissier ?? false,
            DateTime.UtcNow.AddHours(Math.Max(1, Options.DureeHeures)), dossier.Code);
        _log.LogInformation("Connexion de {Login} sur {Dossier} (collaborateur {Collaborateur}, caissier {Caissier})", login, dossier.Code, co?.Numero, u.Caissier);
        return new(u, Signer(u), null, null);
    }

    /// <summary>Utilisateur du jeton de l'en-tête Authorization: Bearer, s'il est valide et non expiré.</summary>
    public Utilisateur? Lire(HttpContext http)
    {
        var entete = http.Request.Headers.Authorization.ToString();
        if (!entete.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var jeton = entete["Bearer ".Length..].Trim();
        var morceaux = jeton.Split('.');
        if (morceaux.Length != 2) return null;
        try
        {
            var attendue = HMACSHA256.HashData(_cle, Encoding.ASCII.GetBytes(morceaux[0]));
            if (!CryptographicOperations.FixedTimeEquals(attendue, Base64Url.Decode(morceaux[1]))) return null;
            var u = JsonSerializer.Deserialize<Utilisateur>(Base64Url.Decode(morceaux[0]), WorkerProtocol.Json);
            return u != null && u.Expiration > DateTime.UtcNow ? u : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    string Signer(Utilisateur u)
    {
        var charge = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(u, WorkerProtocol.Json));
        return charge + "." + Base64Url.Encode(HMACSHA256.HashData(_cle, Encoding.ASCII.GetBytes(charge)));
    }

    /// <summary>Clé fixée dans la configuration, sinon clé aléatoire créée une fois et gardée à côté du journal.</summary>
    static byte[] Cle(string configuree, string cheminJournal)
    {
        if (!string.IsNullOrWhiteSpace(configuree)) return Convert.FromBase64String(configuree);
        var fichier = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cheminJournal)) ?? ".", "sage100api-jetons.cle");
        if (File.Exists(fichier)) return Convert.FromBase64String(File.ReadAllText(fichier).Trim());
        var cle = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(fichier, Convert.ToBase64String(cle));
        return cle;
    }

    static class Base64Url
    {
        public static string Encode(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] Decode(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        }
    }
}
