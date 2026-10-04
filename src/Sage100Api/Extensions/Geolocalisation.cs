using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Sage100Api.Extensions;

/// <summary>Position GPS (WGS 84) d'un client, d'une adresse de livraison ou d'un dépôt.</summary>
public sealed record Position(double Latitude, double Longitude, double? Precision, string? Source, DateTime MajLe, string? Utilisateur);

public sealed record PositionRequest(double Latitude, double Longitude, double? Precision, string? Source);

/// <summary>
/// Positions GPS enregistrées par les extensions (application de livraison, CRM terrain...). Sage n'a pas de champ pour elles et
/// l'API n'écrit jamais en SQL dans Sage : elles sont gardées dans un fichier SQLite à côté du journal (sage100api-extensions.db).
/// </summary>
public sealed class Geolocalisation
{
    public static readonly string[] Cibles = ["client", "adresse-livraison", "depot"];
    readonly string _cnx;

    public Geolocalisation(IOptions<SageOptions> options)
    {
        _cnx = ChaineExtensions(options.Value);
        using var c = Ouvrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS positions (
              cible TEXT NOT NULL,
              cle TEXT NOT NULL,
              latitude REAL NOT NULL,
              longitude REAL NOT NULL,
              precision_m REAL NULL,
              source TEXT NULL,
              utilisateur TEXT NULL,
              maj_le TEXT NOT NULL,
              PRIMARY KEY (cible, cle)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Base SQLite des extensions (positions GPS, activités CRM), à côté du journal.</summary>
    public static string ChaineExtensions(SageOptions o)
    {
        var dossier = Path.GetDirectoryName(Path.GetFullPath(o.CheminJournal)) ?? ".";
        return new SqliteConnectionStringBuilder { DataSource = Path.Combine(dossier, "sage100api-extensions.db") }.ToString();
    }

    SqliteConnection Ouvrir()
    {
        var c = new SqliteConnection(_cnx);
        c.Open();
        return c;
    }

    public static IEnumerable<string> Verifier(string cible, PositionRequest p)
    {
        if (!Cibles.Contains(cible)) yield return $"Cible inconnue « {cible} » : {string.Join(", ", Cibles)}.";
        if (p.Latitude is < -90 or > 90 || double.IsNaN(p.Latitude)) yield return "latitude doit être entre -90 et 90.";
        if (p.Longitude is < -180 or > 180 || double.IsNaN(p.Longitude)) yield return "longitude doit être entre -180 et 180.";
        if (p.Precision is < 0) yield return "precision (en mètres) ne peut pas être négative.";
        if (p.Source is { Length: > 50 }) yield return "source : 50 caractères au plus.";
    }

    public Position Enregistrer(string cible, string cle, PositionRequest p, string? utilisateur)
    {
        var maj = DateTime.UtcNow;
        using var c = Ouvrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO positions (cible, cle, latitude, longitude, precision_m, source, utilisateur, maj_le)
            VALUES ($cible, $cle, $lat, $lon, $prec, $source, $utilisateur, $maj)
            ON CONFLICT (cible, cle) DO UPDATE SET latitude = $lat, longitude = $lon, precision_m = $prec, source = $source,
              utilisateur = $utilisateur, maj_le = $maj;
            """;
        cmd.Parameters.AddWithValue("$cible", cible);
        cmd.Parameters.AddWithValue("$cle", cle);
        cmd.Parameters.AddWithValue("$lat", p.Latitude);
        cmd.Parameters.AddWithValue("$lon", p.Longitude);
        cmd.Parameters.AddWithValue("$prec", (object?)p.Precision ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", (object?)p.Source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$utilisateur", (object?)utilisateur ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$maj", maj.ToString("O"));
        cmd.ExecuteNonQuery();
        return new Position(p.Latitude, p.Longitude, p.Precision, p.Source, maj, utilisateur);
    }

    public bool Supprimer(string cible, string cle)
    {
        using var c = Ouvrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM positions WHERE cible = $cible AND cle = $cle";
        cmd.Parameters.AddWithValue("$cible", cible);
        cmd.Parameters.AddWithValue("$cle", cle);
        return cmd.ExecuteNonQuery() > 0;
    }

    public Position? Lire(string cible, string cle) => Toutes(cible).GetValueOrDefault(cle);

    /// <summary>Toutes les positions d'une cible, par clé (code client, numéro d'adresse, numéro de dépôt).</summary>
    public IReadOnlyDictionary<string, Position> Toutes(string cible)
    {
        var r = new Dictionary<string, Position>(StringComparer.OrdinalIgnoreCase);
        using var c = Ouvrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT cle, latitude, longitude, precision_m, source, maj_le, utilisateur FROM positions WHERE cible = $cible";
        cmd.Parameters.AddWithValue("$cible", cible);
        using var l = cmd.ExecuteReader();
        while (l.Read())
            r[l.GetString(0)] = new Position(l.GetDouble(1), l.GetDouble(2), l.IsDBNull(3) ? null : l.GetDouble(3), l.IsDBNull(4) ? null : l.GetString(4),
                DateTime.Parse(l.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), l.IsDBNull(6) ? null : l.GetString(6));
        return r;
    }
}
