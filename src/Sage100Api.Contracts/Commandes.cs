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
        public List<LigneCommande> Lignes { get; set; } = new List<LigneCommande>();
    }

    public sealed class LigneCommande
    {
        /// <summary>Référence article (AR_Ref, 18 caractères max).</summary>
        public string Article { get; set; } = "";
        public double Quantite { get; set; }
    }

    public sealed class CommandeResult
    {
        public string IdExterne { get; set; } = "";
        /// <summary>Numéro de pièce Sage, par exemple BC00033.</summary>
        public string Piece { get; set; } = "";
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
    }
}
