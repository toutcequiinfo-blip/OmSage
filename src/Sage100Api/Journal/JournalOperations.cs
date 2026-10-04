using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Sage100Api.Journal;

public enum StatutOperation { EnCours, Ok, Erreur }

public sealed record Operation(string Cle, string Type, string Application, StatutOperation Statut, string? Piece, string? Resultat, DateTime CreeLe, DateTime MajLe);

/// <summary>
/// Journal des opérations d'écriture, propre à l'API (SQLite, jamais dans la base Sage).
/// Première barrière anti-doublon : une clé (type + identifiant externe) ne peut être exécutée qu'une fois avec succès.
/// </summary>
public sealed class JournalOperations
{
    readonly string _cnx;
    readonly object _verrou = new();

    public JournalOperations(IOptions<SageOptions> options)
    {
        _cnx = new SqliteConnectionStringBuilder { DataSource = options.Value.CheminJournal }.ToString();
        using var c = Ouvrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS operations (
              cle TEXT PRIMARY KEY,
              type TEXT NOT NULL,
              application TEXT NOT NULL,
              statut TEXT NOT NULL,
              piece TEXT NULL,
              resultat TEXT NULL,
              cree_le TEXT NOT NULL,
              maj_le TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public static string Cle(string type, string idExterne) => $"{type}:{idExterne}";

    /// <summary>Au-delà, une opération restée « en cours » est considérée comme interrompue.</summary>
    public static readonly TimeSpan DelaiAbandon = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Réserve la clé. Renvoie null si l'opération peut démarrer ; sinon l'opération existante
    /// (déjà réussie, ou en cours dans une autre requête).
    /// </summary>
    public Operation? Reserver(string cle, string type, string application)
    {
        lock (_verrou)
        {
            using var c = Ouvrir();
            var existante = Lire(c, cle);
            // Une opération « en cours » depuis plus de DelaiAbandon a été interrompue (arrêt de l'API, erreur avant le
            // worker) : elle peut repartir, le worker retrouvant dans Sage une pièce déjà créée (DO_RefExterne, empreinte).
            if (existante is { Statut: StatutOperation.Ok }
                || existante is { Statut: StatutOperation.EnCours } && DateTime.UtcNow - existante.MajLe.ToUniversalTime() < DelaiAbandon)
                return existante;

            var maintenant = DateTime.UtcNow.ToString("O");
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO operations (cle, type, application, statut, cree_le, maj_le)
                VALUES ($cle, $type, $app, 'EnCours', $t, $t)
                ON CONFLICT(cle) DO UPDATE SET statut = 'EnCours', application = $app, maj_le = $t;
                """;
            cmd.Parameters.AddWithValue("$cle", cle);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$app", application);
            cmd.Parameters.AddWithValue("$t", maintenant);
            cmd.ExecuteNonQuery();
            return null;
        }
    }

    public void Terminer(string cle, StatutOperation statut, string? piece, string? resultat)
    {
        lock (_verrou)
        {
            using var c = Ouvrir();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE operations SET statut = $s, piece = $p, resultat = $r, maj_le = $t WHERE cle = $cle";
            cmd.Parameters.AddWithValue("$s", statut.ToString());
            cmd.Parameters.AddWithValue("$p", (object?)piece ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$r", (object?)resultat ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$cle", cle);
            cmd.ExecuteNonQuery();
        }
    }

    public Operation? Lire(string cle)
    {
        using var c = Ouvrir();
        return Lire(c, cle);
    }

    static Operation? Lire(SqliteConnection c, string cle)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT cle, type, application, statut, piece, resultat, cree_le, maj_le FROM operations WHERE cle = $cle";
        cmd.Parameters.AddWithValue("$cle", cle);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Operation(r.GetString(0), r.GetString(1), r.GetString(2), Enum.Parse<StatutOperation>(r.GetString(3)),
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
            DateTime.Parse(r.GetString(6)).ToUniversalTime(), DateTime.Parse(r.GetString(7)).ToUniversalTime());
    }

    SqliteConnection Ouvrir()
    {
        var c = new SqliteConnection(_cnx);
        c.Open();
        return c;
    }
}
