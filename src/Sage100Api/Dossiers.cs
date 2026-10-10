using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Sage100Api;

/// <summary>Une société de Sage:Dossiers (configuration).</summary>
public sealed class DossierOptions
{
    /// <summary>Base Gestion commerciale (nom de la base SQL). C'est aussi le code de la société dans l'API.</summary>
    public string Base { get; set; } = "";
    /// <summary>Nom affiché à la connexion. Vide : la raison sociale lue dans Sage (P_DOSSIER).</summary>
    public string Intitule { get; set; } = "";
    /// <summary>Connexion SQL propre à cette société. Vide : Sage:ChaineSql avec cette base.</summary>
    public string ChaineSql { get; set; } = "";
}

/// <summary>Société servie par l'API. La principale garde les fichiers locaux d'avant le multi-société (journal, extensions).</summary>
public sealed record Dossier(string Code, string Intitule, string ChaineSql, bool Principal);

/// <summary>
/// Sociétés (bases Sage) servies par une même installation, et société de la requête en cours.
/// Sans Sage:Dossiers, une seule société : celle de Sage:ChaineSql, comme avant le multi-société.
/// La société en cours suit le fil d'exécution (AsyncLocal) : posée par <see cref="SocieteDeLaRequete"/> pour une requête HTTP,
/// par <see cref="Activer"/> pour un traitement de fond. Lectures SQL, worker et bases SQLite locales s'en servent.
/// </summary>
public sealed class Dossiers(IOptionsMonitor<SageOptions> options)
{
    public const string Entete = "X-Dossier";
    static readonly AsyncLocal<string?> Actif = new();

    public IReadOnlyList<Dossier> Liste
    {
        get
        {
            var o = options.CurrentValue;
            var configures = o.Dossiers.Where(d => !string.IsNullOrWhiteSpace(d.Base)).ToList();
            if (configures.Count == 0)
            {
                var b = new SqlConnectionStringBuilder(o.ChaineSql);
                var code = string.IsNullOrWhiteSpace(b.InitialCatalog) ? "Sage" : b.InitialCatalog;
                return [new Dossier(code, code, o.ChaineSql, true)];
            }
            return configures.Select((d, i) => new Dossier(d.Base.Trim(), string.IsNullOrWhiteSpace(d.Intitule) ? d.Base.Trim() : d.Intitule.Trim(),
                string.IsNullOrWhiteSpace(d.ChaineSql) ? AvecBase(o.ChaineSql, d.Base.Trim()) : d.ChaineSql, i == 0)).ToList();
        }
    }

    public bool Multiple => Liste.Count > 1;

    /// <summary>Société d'après son code (sans tenir compte des majuscules) ; code vide : la principale.</summary>
    public Dossier? Trouver(string? code)
    {
        var liste = Liste;
        return string.IsNullOrWhiteSpace(code) ? liste[0] : liste.FirstOrDefault(d => d.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Société en cours (la principale si rien n'est posé : application à une seule société, ou traitement sans contexte).</summary>
    public Dossier Courant => Trouver(Actif.Value) ?? Liste[0];

    public string Code => Courant.Code;

    public string ChaineSql => Courant.ChaineSql;

    /// <summary>Pose la société en cours pour la suite du traitement (fil d'exécution asynchrone courant et ses enfants).</summary>
    public static void Poser(string? code) => Actif.Value = code;

    /// <summary>Pose la société le temps d'un bloc using, puis remet la précédente.</summary>
    public static IDisposable Activer(string code)
    {
        var avant = Actif.Value;
        Actif.Value = code;
        return new Retour(() => Actif.Value = avant);
    }

    /// <summary>Chaîne fournie pour une autre base (compte lecture seule des tableaux de bord...) adaptée à la société en cours.</summary>
    public string Adapter(string chaine) => string.IsNullOrWhiteSpace(chaine) ? ChaineSql : Multiple ? AvecBase(chaine, Code) : chaine;

    /// <summary>Dossier des fichiers locaux de la société : celui du journal pour la principale, sinon un sous-dossier dossiers\&lt;code&gt;.</summary>
    public string Repertoire(string racine)
    {
        var d = Courant;
        if (d.Principal) return racine;
        var nom = string.Concat(d.Code.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(racine, "dossiers", nom);
    }

    static string AvecBase(string chaine, string baseSql) => new SqlConnectionStringBuilder(chaine) { InitialCatalog = baseSql }.ConnectionString;

    sealed class Retour(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

/// <summary>
/// Base SQLite locale de l'API (journal, extensions), une par société : le schéma est créé à la première ouverture de chaque fichier.
/// </summary>
public sealed class BaseLocale(Dossiers dossiers, SageOptions options, string fichier, Action<SqliteConnection> preparer)
{
    readonly ConcurrentDictionary<string, bool> _pretes = new(StringComparer.OrdinalIgnoreCase);
    readonly string _racine = Path.GetDirectoryName(Path.GetFullPath(options.CheminJournal)) ?? ".";

    public SqliteConnection Ouvrir()
    {
        var dossier = dossiers.Repertoire(_racine);
        var chemin = Path.Combine(dossier, fichier);
        if (!_pretes.ContainsKey(chemin)) Directory.CreateDirectory(dossier);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = chemin }.ToString());
        c.Open();
        if (_pretes.TryAdd(chemin, true))
        {
            try
            {
                preparer(c);
            }
            catch
            {
                _pretes.TryRemove(chemin, out _);
                c.Dispose();
                throw;
            }
        }
        return c;
    }
}

/// <summary>
/// Pose la société de la requête : celle du jeton de connexion, sinon l'en-tête X-Dossier, sinon la seule société.
/// Avec plusieurs sociétés, une requête sans société est refusée (sauf santé, liste des sociétés et connexion).
/// Un jeton ne sert que pour sa société : pour en changer, il faut se reconnecter.
/// </summary>
public sealed class SocieteDeLaRequete(RequestDelegate suivant, Dossiers dossiers, ServiceAuthentification auth)
{
    public const string CodeRequis = "DOSSIER_REQUIS";
    public const string CodeInconnu = "DOSSIER_INCONNU";
    public const string CodeDifferent = "DOSSIER_DIFFERENT";

    public async Task InvokeAsync(HttpContext http)
    {
        var chemin = http.Request.Path;
        if (!chemin.StartsWithSegments("/api"))
        {
            await suivant(http);
            return;
        }

        var demande = http.Request.Headers[Dossiers.Entete].ToString().Trim();
        var u = auth.Lire(http);
        // Jeton d'avant le multi-société : il ne pouvait viser que la société principale.
        var duJeton = u == null ? null : string.IsNullOrEmpty(u.Dossier) ? dossiers.Liste[0].Code : u.Dossier;
        var code = duJeton ?? (demande.Length > 0 ? demande : null);

        if (duJeton != null && demande.Length > 0 && !demande.Equals(duJeton, StringComparison.OrdinalIgnoreCase))
        {
            await Refuser(http, StatusCodes.Status409Conflict, CodeDifferent, $"Vous êtes connecté sur la société {duJeton} : reconnectez-vous pour changer de société.");
            return;
        }
        if (code != null)
        {
            var d = dossiers.Trouver(code);
            if (d == null)
            {
                await Refuser(http, StatusCodes.Status400BadRequest, CodeInconnu, $"Société inconnue : {code}.");
                return;
            }
            code = d.Code;
        }
        else if (dossiers.Multiple && !Libre(chemin))
        {
            await Refuser(http, StatusCodes.Status400BadRequest, CodeRequis, $"Choisissez la société (en-tête {Dossiers.Entete} ou connexion).");
            return;
        }

        Dossiers.Poser(code);
        await suivant(http);
    }

    static bool Libre(PathString chemin) =>
        chemin.StartsWithSegments("/api/v1/sante") || chemin.StartsWithSegments("/api/v1/dossiers") || chemin.StartsWithSegments("/api/v1/connexion")
        || chemin.StartsWithSegments("/api/v1/session-windows") || chemin.StartsWithSegments("/api/v1/licence");

    static Task Refuser(HttpContext http, int statut, string code, string message)
    {
        http.Response.StatusCode = statut;
        return http.Response.WriteAsJsonAsync(new { code, message });
    }
}
