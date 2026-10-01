using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Sage100Api.Contracts;
using Sage100Api.Ecritures;

namespace Sage100Api;

/// <summary>Contrôle de l'en-tête X-Api-Key (une clé par application cliente). Santé et Swagger restent publics.</summary>
public sealed class CleApi(RequestDelegate suivant, IOptionsMonitor<SageOptions> options)
{
    public const string Entete = "X-Api-Key";
    const string CleApplication = "sage100api.application";

    public async Task InvokeAsync(HttpContext http)
    {
        var chemin = http.Request.Path;
        if (chemin.StartsWithSegments("/swagger") || chemin.StartsWithSegments("/api/v1/sante"))
        {
            await suivant(http);
            return;
        }

        var fournie = http.Request.Headers[Entete].ToString();
        var application = options.CurrentValue.ClesApi.FirstOrDefault(kv => Egales(kv.Value, fournie)).Key;
        if (string.IsNullOrEmpty(fournie) || application == null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await http.Response.WriteAsJsonAsync(new { erreur = "Clé d'API absente ou invalide (en-tête X-Api-Key)." });
            return;
        }
        http.Items[CleApplication] = application;
        await suivant(http);
    }

    public static string Application(HttpContext http) => http.Items[CleApplication] as string ?? "inconnue";

    static bool Egales(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

public static class Reponses
{
    public static IResult Invalide(IEnumerable<string> erreurs) =>
        Results.UnprocessableEntity(new { code = "VALIDATION", erreurs });

    /// <summary>
    /// 404 : client, article, mode ou commande inconnu ; 409 : même opération déjà en cours ;
    /// 422 : règle Sage refusée (stock, période clôturée...) ; 503 : worker ou Sage indisponible.
    /// </summary>
    public static IResult Depuis<T>(ResultatEcriture<T> r, Func<T, IResult> succes)
    {
        if (r.CodeErreur == null) return succes(r.Valeur!);
        var corps = new { code = r.CodeErreur, message = r.Message };
        return r.CodeErreur switch
        {
            CodesErreur.Introuvable or CodesApi.CommandeInconnue => Results.NotFound(corps),
            CodesApi.EnCours => Results.Conflict(corps),
            CodesErreur.SageMetier => Results.UnprocessableEntity(corps),
            _ => Results.Json(corps, statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }
}
