using System.Collections.Generic;

namespace Sage100Api.Contracts
{
    /// <summary>
    /// Contrôles faits avant d'appeler Sage, pour renvoyer des erreurs claires (HTTP 422) plutôt qu'une exception COM.
    /// Longueurs : « Structure des bases Sage 100 » (CT_Num 17, AR_Ref 18, DO_Ref 17, DO_RefExterne 51).
    /// </summary>
    public static class Validation
    {
        public const int LongueurIdExterne = 51;
        /// <summary>Numéro de pièce Sage (DO_Piece), par exemple BC00042.</summary>
        public const int LongueurPiece = 13;
        public const int LongueurClient = 17;
        public const int LongueurArticle = 18;
        public const int LongueurReference = 17;
        public const int LongueurMode = 35;
        public const int LongueurEnumere = 35;

        public static List<string> Verifier(CommandeRequest c)
        {
            var e = new List<string>();
            Texte(e, "idExterne", c.IdExterne, LongueurIdExterne);
            Texte(e, "client", c.Client, LongueurClient);
            if (c.Reference != null && c.Reference.Length > LongueurReference)
                e.Add($"reference : {LongueurReference} caractères maximum.");
            if (TypesPiece.Normaliser(c.TypeDocument) == null)
                e.Add("typeDocument : commande, livraison ou facture.");
            if (c.Souche.HasValue && (c.Souche < 0 || c.Souche > 49)) e.Add("souche : de 0 à 49.");
            if (c.Depot.HasValue && c.Depot <= 0) e.Add("depot : numéro de dépôt Sage (DE_No).");
            if (c.Lignes == null || c.Lignes.Count == 0)
            {
                e.Add("lignes : la commande doit contenir au moins une ligne.");
                return e;
            }
            for (int i = 0; i < c.Lignes.Count; i++)
            {
                var l = c.Lignes[i];
                Texte(e, $"lignes[{i}].article", l.Article, LongueurArticle);
                if (!(l.Quantite > 0)) e.Add($"lignes[{i}].quantite : doit être supérieure à 0.");
                if (l.Gamme1 != null && l.Gamme1.Length > LongueurEnumere) e.Add($"lignes[{i}].gamme1 : {LongueurEnumere} caractères maximum.");
                if (l.Gamme2 != null && l.Gamme2.Length > LongueurEnumere) e.Add($"lignes[{i}].gamme2 : {LongueurEnumere} caractères maximum.");
                if (!string.IsNullOrEmpty(l.Gamme2) && string.IsNullOrEmpty(l.Gamme1)) e.Add($"lignes[{i}].gamme2 : renseigner aussi gamme1.");
                if (!string.IsNullOrEmpty(l.Conditionnement))
                {
                    if (l.Conditionnement!.Length > LongueurEnumere) e.Add($"lignes[{i}].conditionnement : {LongueurEnumere} caractères maximum.");
                    if (!(l.QuantiteConditionnement > 0)) e.Add($"lignes[{i}].quantiteConditionnement : quantité du conditionnement obligatoire.");
                    if (!string.IsNullOrEmpty(l.Gamme1)) e.Add($"lignes[{i}].conditionnement : un article à gamme n'a pas de conditionnement dans Sage.");
                }
            }
            return e;
        }

        public static List<string> Verifier(EncaissementRequest p)
        {
            var e = new List<string>();
            Texte(e, "idExterne", p.IdExterne, LongueurIdExterne);
            Texte(e, "mode", p.Mode, LongueurMode);
            if (!(p.Montant > 0)) e.Add("montant : doit être supérieur à 0.");
            return e;
        }

        static void Texte(List<string> e, string champ, string? valeur, int max)
        {
            if (string.IsNullOrWhiteSpace(valeur)) e.Add($"{champ} : obligatoire.");
            else if (valeur!.Length > max) e.Add($"{champ} : {max} caractères maximum.");
        }
    }
}
