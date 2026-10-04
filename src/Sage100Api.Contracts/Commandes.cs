using System.Collections.Generic;

namespace Sage100Api.Contracts
{
    /// <summary>Commande saisie sur la borne. <see cref="IdExterne"/> est généré par la borne et rend l'envoi idempotent.</summary>
    public sealed class CommandeRequest
    {
        /// <summary>Identifiant unique côté borne (stocké dans DO_RefExterne, 51 caractères max).</summary>
        public string IdExterne { get; set; } = "";
        /// <summary>Code client Sage (CT_Num, 17 caractères max).</summary>
        public string Client { get; set; } = "";
        /// <summary>Référence visible dans Sage (DO_Ref, 17 caractères max). Facultatif.</summary>
        public string? Reference { get; set; }
        /// <summary>
        /// Pièce à créer : « commande » (bon de commande, par défaut), « livraison » (bon de livraison) ou « facture ».
        /// Bon de livraison et facture font bouger le stock dès leur création.
        /// </summary>
        public string? TypeDocument { get; set; }
        /// <summary>Souche de numérotation (DO_Souche : 0 = première souche de Paramètres société / Documents). Vide : souche par défaut.</summary>
        public int? Souche { get; set; }
        /// <summary>Dépôt de stockage (DE_No). Vide : dépôt du client ou dépôt principal, comme dans Sage.</summary>
        public int? Depot { get; set; }
        public List<LigneCommande> Lignes { get; set; } = new List<LigneCommande>();
    }

    /// <summary>Types de pièce que la borne peut créer, avec leur DO_Type Sage.</summary>
    public static class TypesPiece
    {
        public const string Commande = "commande";
        public const string Livraison = "livraison";
        public const string Facture = "facture";

        /// <summary>Type normalisé (minuscules), « commande » si vide, null si inconnu.</summary>
        public static string? Normaliser(string? type)
        {
            var t = (type ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0) return Commande;
            return t == Commande || t == Livraison || t == Facture ? t : null;
        }

        /// <summary>DO_Type de F_DOCENTETE : 1 bon de commande, 3 bon de livraison, 6 facture (7 une fois comptabilisée).</summary>
        public static int DoType(string type) => type == Livraison ? 3 : type == Facture ? 6 : 1;

        public static string Libelle(string type) => type == Livraison ? "bon de livraison" : type == Facture ? "facture" : "bon de commande";
    }

    public sealed class LigneCommande
    {
        /// <summary>Référence article (AR_Ref, 18 caractères max).</summary>
        public string Article { get; set; } = "";
        public double Quantite { get; set; }
        /// <summary>Énuméré de gamme 1 (EG_Enumere, par exemple « 52 »), obligatoire si l'article est à gamme.</summary>
        public string? Gamme1 { get; set; }
        /// <summary>Énuméré de gamme 2, pour un article à double gamme.</summary>
        public string? Gamme2 { get; set; }
        /// <summary>
        /// Conditionnement vendu (F_CONDITION.EC_Enumere, par exemple « Carton de 12 ») : la quantité est alors un nombre de
        /// conditionnements. Vide : quantité dans l'unité de vente de l'article.
        /// </summary>
        public string? Conditionnement { get; set; }
        /// <summary>Quantité contenue dans le conditionnement (EC_Quantite, par exemple 12). Obligatoire avec un conditionnement.</summary>
        public double? QuantiteConditionnement { get; set; }
        /// <summary>
        /// Rempli par l'API, ignoré s'il vient de l'application : prix unitaire par unité de vente selon le tarif du client
        /// (tarif client, sinon catégorie tarifaire, sinon prix de l'article), HT ou TTC selon <see cref="PrixTTC"/>.
        /// </summary>
        public double? PrixUnitaire { get; set; }
        /// <summary>Rempli par l'API : le prix unitaire est TTC.</summary>
        public bool PrixTTC { get; set; }
        /// <summary>Rempli par l'API : remises de la ligne (jusqu'à 3, appliquées en cascade comme dans Sage).</summary>
        public List<RemiseLigne>? Remises { get; set; }
    }

    /// <summary>Remise de ligne Sage : Type 0 = montant par unité, 1 = pourcentage, 2 = quantité offerte.</summary>
    public sealed class RemiseLigne
    {
        public int Type { get; set; }
        public double Valeur { get; set; }
    }

    public sealed class CommandeResult
    {
        public string IdExterne { get; set; } = "";
        /// <summary>Numéro de pièce Sage, par exemple BC00033 (ou BL00012, FA00045).</summary>
        public string Piece { get; set; } = "";
        /// <summary>Type de la pièce créée : commande, livraison ou facture.</summary>
        public string TypeDocument { get; set; } = TypesPiece.Commande;
        public double NetAPayer { get; set; }
        /// <summary>Vrai si la commande existait déjà (renvoi après coupure réseau).</summary>
        public bool DejaExistante { get; set; }
    }

    /// <summary>Encaissement enregistré comme acompte sur le bon de commande (fonctionne loi anti-fraude activée).</summary>
    public sealed class EncaissementRequest
    {
        /// <summary>Identifiant unique de l'encaissement côté borne.</summary>
        public string IdExterne { get; set; } = "";
        /// <summary>Intitulé exact du mode de règlement Sage (P_REGLEMENT.R_Intitule), par exemple "Espèces".</summary>
        public string Mode { get; set; } = "";
        public double Montant { get; set; }
        /// <summary>Référence de paiement (numéro de chèque, transaction mobile money, autorisation CB). Facultatif.</summary>
        public string? ReferencePaiement { get; set; }
    }

    public sealed class EncaissementResult
    {
        public string IdExterne { get; set; } = "";
        public string PieceCommande { get; set; } = "";
        public double Montant { get; set; }
        public bool DejaExistant { get; set; }
        /// <summary>« acompte » sur un bon de commande ou de livraison, « reglement » (règlement client) sur une facture.</summary>
        public string Nature { get; set; } = "acompte";
        /// <summary>Règlement imputé sur l'échéance de la facture ; faux s'il reste à lettrer dans Sage (facture pas encore validée).</summary>
        public bool Impute { get; set; }
    }

    /// <summary>Login de la borne : nom et mot de passe de l'utilisateur Sage (Fichier > Autorisations d'accès).</summary>
    public sealed class ConnexionRequest
    {
        public string Utilisateur { get; set; } = "";
        public string MotDePasse { get; set; } = "";
    }
}
