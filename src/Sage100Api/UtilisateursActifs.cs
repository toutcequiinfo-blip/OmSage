using System.Collections.Concurrent;
using System.Net;

namespace Sage100Api;

/// <summary>Une session : un utilisateur Sage, sur une société, depuis un poste.</summary>
public sealed class SessionActive
{
    public required string Login { get; init; }
    public string? Nom { get; set; }
    public required string Dossier { get; init; }
    public required string Adresse { get; init; }
    public string? Poste { get; set; }
    public string? SessionWindows { get; set; }
    public bool Administrateur { get; set; }
    public ConcurrentDictionary<string, DateTime> Applications { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime Debut { get; init; }
    public DateTime Derniere { get; set; }
    public int Requetes;
}

/// <summary>
/// Utilisateurs actifs : chaque requête portant un jeton de connexion met à jour la session (utilisateur, société, poste).
/// Le nom du poste vient du DNS (adresse IP -> nom), la session Windows de l'authentification Windows intégrée
/// (POST /api/v1/session-windows, envoyé par les pages au démarrage). Rien n'est écrit sur disque : la liste repart à vide
/// au redémarrage du service, les sessions sans activité depuis 24 heures sont oubliées.
/// </summary>
public sealed class UtilisateursActifs(ILogger<UtilisateursActifs> log)
{
    static readonly TimeSpan Oubli = TimeSpan.FromHours(24);
    readonly ConcurrentDictionary<string, SessionActive> _sessions = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, string> _windows = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, (string? Nom, DateTime Lu)> _postes = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, Utilisateur> _connus = new(StringComparer.OrdinalIgnoreCase);

    public static string Adresse(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        if (ip == null) return "?";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.ToString();
    }

    static string Cle(string login, string dossier, string adresse) => $"{login}|{dossier}|{adresse}";

    public void Noter(Utilisateur u, string dossier, string application, HttpContext http)
    {
        var adresse = Adresse(http);
        var maintenant = DateTime.UtcNow;
        _connus[$"{dossier}|{u.Login}"] = u;
        var s = _sessions.GetOrAdd(Cle(u.Login, dossier, adresse), _ => new SessionActive
        {
            Login = u.Login, Dossier = dossier, Adresse = adresse, Debut = maintenant,
            SessionWindows = _windows.GetValueOrDefault($"{u.Login}|{adresse}"),
        });
        s.Nom = string.Join(" ", new[] { u.Prenom, u.Nom }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } n ? n : null;
        s.Administrateur = u.Administrateur;
        s.Derniere = maintenant;
        s.Applications[application] = maintenant;
        Interlocked.Increment(ref s.Requetes);
        if (s.Poste == null) _ = NommerPoste(s);
        if (_sessions.Count > 500) Nettoyer();
    }

    /// <summary>Session Windows reconnue pour cet utilisateur sur ce poste (« DOMAINE\nom »).</summary>
    public void NoterWindows(Utilisateur u, HttpContext http, string nom)
    {
        var adresse = Adresse(http);
        _windows[$"{u.Login}|{adresse}"] = nom;
        foreach (var s in _sessions.Values.Where(s => s.Adresse == adresse && s.Login.Equals(u.Login, StringComparison.OrdinalIgnoreCase)))
            s.SessionWindows = nom;
        log.LogInformation("Session Windows de {Login} sur {Adresse} : {Windows}", u.Login, adresse, nom);
    }

    /// <summary>Utilisateurs venus sur cette société depuis le démarrage du service (dernier jeton de chacun).</summary>
    public IEnumerable<Utilisateur> Connus(string dossier) =>
        _connus.Where(x => x.Key.StartsWith(dossier + "|", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value);

    public IReadOnlyList<SessionActive> Sessions()
    {
        Nettoyer();
        return _sessions.Values.OrderByDescending(s => s.Derniere).ToList();
    }

    void Nettoyer()
    {
        var limite = DateTime.UtcNow - Oubli;
        foreach (var (cle, s) in _sessions)
            if (s.Derniere < limite) _sessions.TryRemove(cle, out _);
    }

    async Task NommerPoste(SessionActive s)
    {
        if (_postes.TryGetValue(s.Adresse, out var connu) && DateTime.UtcNow - connu.Lu < TimeSpan.FromHours(1))
        {
            s.Poste = connu.Nom ?? "";
            return;
        }
        string? nom = null;
        try
        {
            if (IPAddress.TryParse(s.Adresse, out var ip))
            {
                if (IPAddress.IsLoopback(ip)) nom = Environment.MachineName;
                else
                {
                    using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var h = await Dns.GetHostEntryAsync(ip.ToString(), delai.Token);
                    nom = h.HostName;
                }
            }
        }
        catch (Exception e)
        {
            log.LogDebug(e, "Nom du poste {Adresse} introuvable", s.Adresse);
        }
        // « PC-COMPTA.vita.local » -> « PC-COMPTA » ; une adresse renvoyée telle quelle n'est pas un nom.
        if (nom != null && IPAddress.TryParse(nom, out _)) nom = null;
        nom = nom?.Split('.')[0].ToUpperInvariant();
        _postes[s.Adresse] = (nom, DateTime.UtcNow);
        s.Poste = nom ?? "";
    }
}

/// <summary>Note l'activité de chaque requête /api portant un jeton valide (après le choix de la société).</summary>
public sealed class NoterActivite(RequestDelegate suivant, ServiceAuthentification auth, UtilisateursActifs activite, Dossiers dossiers)
{
    public Task InvokeAsync(HttpContext http)
    {
        if (http.Request.Path.StartsWithSegments("/api") && auth.Lire(http) is { } u)
            activite.Noter(u, string.IsNullOrEmpty(u.Dossier) ? dossiers.Liste[0].Code : u.Dossier, Application(http), http);
        return suivant(http);
    }

    static readonly Dictionary<string, string> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["borne"] = "Borne", ["crm"] = "CRM", ["livraison"] = "Livraison", ["tableau-de-bord"] = "Tableau de bord", ["ocr"] = "Documents (OCR)",
        ["swagger"] = "Swagger",
    };

    /// <summary>Application d'après la page qui appelle (/borne/, /crm/…), sinon le nom de la clé d'API.</summary>
    static string Application(HttpContext http)
    {
        if (Uri.TryCreate(http.Request.Headers.Referer.ToString(), UriKind.Absolute, out var page)
            && page.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } dossier)
            return Pages.GetValueOrDefault(dossier) ?? dossier;
        return CleApi.Application(http);
    }
}
