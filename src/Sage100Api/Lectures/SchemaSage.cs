using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Sage100Api.Lectures;

/// <summary>
/// Différences de structure entre versions de Sage 100, vues dans la base elle-même.
/// V10 et plus : l'identifiant de la borne est dans DO_RefExterne, et les requêtes restent telles quelles.
/// V9 : DO_RefExterne n'existe pas ; l'identifiant est dans l'information libre « IdBorne » de l'entête des documents.
/// Sans l'une ni l'autre, il est lu comme vide.
/// </summary>
public static partial class SchemaSage
{
    public const string InfoLibreIdBorne = "IdBorne";

    // Par chaîne de connexion (une par société) : la structure d'une base ne change pas tant que l'API tourne.
    static readonly ConcurrentDictionary<string, string?> Colonnes = new();

    [GeneratedRegex(@"\b(\w+\.)?DO_RefExterne\b")]
    private static partial Regex RefExterne();

    /// <summary>
    /// Colonne de F_DOCENTETE qui porte l'identifiant de la borne : DO_RefExterne, [IdBorne] ou null.
    /// Si la structure ne peut pas être lue, DO_RefExterne (fonctionnement V12 d'avant), sans retenir ce résultat.
    /// </summary>
    public static string? ColonneIdExterne(string chaineSql)
    {
        if (Colonnes.TryGetValue(chaineSql, out var connue)) return connue;
        try
        {
            using var c = new SqlConnection(chaineSql);
            var (refExterne, idBorne) = c.QueryFirst<(int?, int?)>(
                "SELECT COL_LENGTH('F_DOCENTETE', 'DO_RefExterne'), COL_LENGTH('F_DOCENTETE', @info)", new { info = InfoLibreIdBorne });
            return Colonnes[chaineSql] = refExterne != null ? "DO_RefExterne" : idBorne != null ? $"[{InfoLibreIdBorne}]" : null;
        }
        catch (Exception)
        {
            return "DO_RefExterne";
        }
    }

    /// <summary>La requête adaptée à la base : inchangée en V10 et plus (DO_RefExterne présente).</summary>
    public static string Adapter(string sql, string chaineSql)
    {
        var colonne = ColonneIdExterne(chaineSql);
        if (colonne == "DO_RefExterne") return sql;
        return RefExterne().Replace(sql, m => colonne == null ? "CAST(NULL AS varchar(69))" : m.Groups[1].Value + colonne);
    }

    /// <summary>Pour les tests : oublie la structure lue.</summary>
    internal static void Oublier() => Colonnes.Clear();

    /// <summary>Pour les tests : impose la colonne d'une base sans la lire.</summary>
    internal static void Imposer(string chaineSql, string? colonne) => Colonnes[chaineSql] = colonne;
}
