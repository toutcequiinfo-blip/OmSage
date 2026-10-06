<#
  Configure l'API Sage 100 sur un serveur de production, après la copie des fichiers par Setup.exe.
  Aucun outil de développement n'est nécessaire : l'API et le worker arrivent déjà compilés.

  Lancé par l'installateur (jamais à la main en temps normal) :
      configurer.ps1 -Parametres <fichier.json>   première installation ou reconfiguration
      configurer.ps1 -MiseAJour                   mise à jour : garde la configuration et redémarre les services
      configurer.ps1 -Arreter                     arrête les services (avant la copie des nouveaux fichiers)
      configurer.ps1 -Desinstaller                supprime les services et la règle de pare-feu (données gardées)

  Le fichier de paramètres (écrit par l'installateur dans son dossier temporaire, effacé ensuite) contient :
  serveurSql, baseCial, baseCpta, utilisateurSage, motDePasseSage, compteService, motDePasseService,
  portHttp, portHttps, https (bool), compteLecture (bool).
  Tout est consigné dans <dossier>\installation.log, sans les mots de passe.
#>
param(
    [string]$Dossier = (Split-Path -Parent $PSScriptRoot),
    [string]$Parametres,
    [switch]$MiseAJour,
    [switch]$Arreter,
    [switch]$Desinstaller
)

$ErrorActionPreference = "Stop"
$journal = Join-Path $Dossier "installation.log"
$services = @(
    @{ Nom = "Sage100Api.Worker"; Affichage = "Sage 100 API - Worker Objets Métiers"; Exe = "$Dossier\worker\Sage100Api.Worker.exe" },
    @{ Nom = "Sage100Api";        Affichage = "Sage 100 API";                         Exe = "$Dossier\api\Sage100Api.exe" }
)
$utf8 = New-Object System.Text.UTF8Encoding $false

function Ecrire($texte, $couleur = "Gray") {
    Write-Host $texte -ForegroundColor $couleur
    Add-Content -Path $journal -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $texte" -Encoding UTF8
}
function EcrireJson($chemin, $objet) { [IO.File]::WriteAllText($chemin, ($objet | ConvertTo-Json -Depth 20), $utf8) }
function LireJson($chemin) { if (Test-Path $chemin) { Get-Content $chemin -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null } }
function Propriete($objet, $nom, $valeur) { $objet | Add-Member -NotePropertyName $nom -NotePropertyValue $valeur -Force }
function Aleatoire($longueur) {
    $car = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789"
    $octets = New-Object byte[] $longueur
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($octets)
    -join ($octets | ForEach-Object { $car[$_ % $car.Length] })
}

function ArreterServices {
    foreach ($s in $services) {
        $existant = Get-Service -Name $s.Nom -ErrorAction SilentlyContinue
        if ($existant -and $existant.Status -ne "Stopped") { Stop-Service -Name $s.Nom -Force; Ecrire "$($s.Nom) arrêté" }
    }
}

function DemarrerServices {
    foreach ($s in $services) {
        try { Start-Service -Name $s.Nom; Ecrire "$($s.Nom) démarré" Green }
        catch {
            Ecrire "$($s.Nom) n'a pas démarré : $($_.Exception.Message)" Red
            Ecrire "Mot de passe du compte ? services.msc > $($s.Nom) > Connexion : ressaisissez-le, puis démarrez le service." Yellow
        }
    }
}

# Droit « Ouvrir une session en tant que service » : sans lui, un service sous un compte utilisateur refuse de démarrer (erreur 1069).
function AccorderDroitService($compte) {
    Add-Type @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

public static class DroitServiceInstallation
{
    [StructLayout(LayoutKind.Sequential)]
    struct LSA_UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    struct LSA_OBJECT_ATTRIBUTES { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }

    [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr systeme, ref LSA_OBJECT_ATTRIBUTES attributs, uint acces, out IntPtr politique);
    [DllImport("advapi32.dll")] static extern uint LsaAddAccountRights(IntPtr politique, byte[] sid, LSA_UNICODE_STRING[] droits, uint nombre);
    [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr politique);
    [DllImport("advapi32.dll")] static extern int LsaNtStatusToWinError(uint statut);

    public static void Accorder(string compte)
    {
        var sid = (SecurityIdentifier)new NTAccount(compte).Translate(typeof(SecurityIdentifier));
        var octets = new byte[sid.BinaryLength];
        sid.GetBinaryForm(octets, 0);
        var attributs = new LSA_OBJECT_ATTRIBUTES();
        attributs.Length = Marshal.SizeOf(attributs);
        IntPtr politique;
        uint statut = LsaOpenPolicy(IntPtr.Zero, ref attributs, 0x810, out politique);
        if (statut != 0) throw new Win32Exception(LsaNtStatusToWinError(statut));
        const string droit = "SeServiceLogonRight";
        var u = new LSA_UNICODE_STRING { Buffer = Marshal.StringToHGlobalUni(droit), Length = (ushort)(droit.Length * 2), MaximumLength = (ushort)((droit.Length + 1) * 2) };
        try
        {
            statut = LsaAddAccountRights(politique, octets, new[] { u }, 1);
            if (statut != 0) throw new Win32Exception(LsaNtStatusToWinError(statut));
        }
        finally
        {
            Marshal.FreeHGlobal(u.Buffer);
            LsaClose(politique);
        }
    }
}
'@
    [DroitServiceInstallation]::Accorder($compte)
}

try {
    if ($Arreter) { ArreterServices; return }

    if ($Desinstaller) {
        ArreterServices
        foreach ($s in $services) {
            if (Get-Service -Name $s.Nom -ErrorAction SilentlyContinue) { sc.exe delete $s.Nom | Out-Null; Ecrire "$($s.Nom) supprimé" }
        }
        Remove-NetFirewallRule -Name "Sage100Api" -ErrorAction SilentlyContinue
        Ecrire "Désinstallation terminée. Gardés dans $Dossier : configuration, journaux, base des extensions, certificats."
        return
    }

    if ($MiseAJour) {
        Ecrire "Mise à jour : configuration gardée."
        DemarrerServices
        return
    }

    if (-not $Parametres -or -not (Test-Path $Parametres)) { throw "Paramètres d'installation introuvables." }
    $p = LireJson $Parametres
    Ecrire "==> Installation dans $Dossier (serveur SQL $($p.serveurSql), base $($p.baseCial))" Cyan
    ArreterServices

    # 1. Worker : connexion Objets Métiers (journaux par mode gardés s'ils existent déjà).
    $cheminWorker = "$Dossier\worker\worker.json"
    $w = LireJson $cheminWorker
    if (-not $w) { $w = [pscustomobject]@{} }
    Propriete $w "serveur" $p.serveurSql
    Propriete $w "baseCial" $p.baseCial
    Propriete $w "baseCpta" $(if ($p.baseCpta) { $p.baseCpta } else { $p.baseCial })
    Propriete $w "utilisateur" $p.utilisateurSage
    Propriete $w "motDePasse" $p.motDePasseSage
    if (-not $w.PSObject.Properties["chaineSql"]) { Propriete $w "chaineSql" "" }
    if (-not $w.PSObject.Properties["journauxParMode"]) { Propriete $w "journauxParMode" ([pscustomobject]@{}) }
    EcrireJson $cheminWorker $w
    Ecrire "worker.json écrit (journaux par mode : à compléter si besoin)"

    # 2. API : chaîne SQL (sécurité intégrée = compte des services) et clé d'API de la borne, créée une seule fois.
    $cheminApi = "$Dossier\api\appsettings.Local.json"
    $a = LireJson $cheminApi
    if (-not $a) { $a = [pscustomobject]@{} }
    if (-not $a.Sage) { Propriete $a "Sage" ([pscustomobject]@{}) }
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $b.PSBase.DataSource = $p.serveurSql
    $b.PSBase.InitialCatalog = $p.baseCial
    $b.PSBase.IntegratedSecurity = $true
    $b.PSBase.TrustServerCertificate = $true
    $b.PSBase.ApplicationIntent = [System.Data.SqlClient.ApplicationIntent]::ReadOnly
    Propriete $a.Sage "ChaineSql" $b.PSBase.ConnectionString
    if (-not $a.Sage.ClesApi) { Propriete $a.Sage "ClesApi" ([pscustomobject]@{ borne = (Aleatoire 32) }) }
    if (-not $a.PSObject.Properties["Urls"]) { Propriete $a "Urls" "http://0.0.0.0:$($p.portHttp)" }
    EcrireJson $cheminApi $a
    Ecrire "appsettings.Local.json écrit"

    # 3. Services Windows sous le compte donné (il doit accéder à SQL Server et au dossier Sage).
    $mdp = ConvertTo-SecureString $p.motDePasseService -AsPlainText -Force
    $compte = New-Object System.Management.Automation.PSCredential ($p.compteService, $mdp)
    try { AccorderDroitService $p.compteService; Ecrire "Droit « ouvrir une session en tant que service » accordé à $($p.compteService)" }
    catch { Ecrire "Droit de service non accordé automatiquement : $($_.Exception.Message)" Yellow }
    foreach ($s in $services) {
        if (Get-Service -Name $s.Nom -ErrorAction SilentlyContinue) {
            # Par WMI plutôt que sc.exe : un mot de passe avec espaces ou guillemets passe sans souci d'échappement.
            $r = Get-CimInstance Win32_Service -Filter "Name='$($s.Nom)'" | Invoke-CimMethod -MethodName Change -Arguments @{
                PathName = "`"$($s.Exe)`""; StartMode = "Automatic"; StartName = $p.compteService; StartPassword = $p.motDePasseService }
            if ($r.ReturnValue -ne 0) { throw "Reconfiguration de $($s.Nom) refusée par Windows (code $($r.ReturnValue))." }
            Ecrire "$($s.Nom) reconfiguré"
        }
        else {
            New-Service -Name $s.Nom -DisplayName $s.Affichage -BinaryPathName "`"$($s.Exe)`"" -StartupType Automatic -Credential $compte | Out-Null
            Ecrire "$($s.Nom) créé"
        }
        # Redémarrage automatique en cas d'arrêt inattendu (5 s, 5 s, puis 30 s ; compteur remis à zéro chaque jour).
        sc.exe failure $s.Nom reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
    }

    # 4. Pare-feu.
    Remove-NetFirewallRule -Name "Sage100Api" -ErrorAction SilentlyContinue
    New-NetFirewallRule -Name "Sage100Api" -DisplayName "Sage 100 API" -Direction Inbound -Protocol TCP -LocalPort $p.portHttp, $p.portHttps -Action Allow -Profile Any | Out-Null
    Ecrire "Pare-feu ouvert sur les ports $($p.portHttp) et $($p.portHttps)"

    # 5. HTTPS : certificat créé une seule fois (le réinstaller obligerait à réinstaller l'autorité sur chaque tablette).
    if ($p.https) {
        if (Test-Path "$Dossier\api\appsettings.Https.json") { Ecrire "HTTPS déjà configuré : certificat gardé" }
        else {
            & "$PSScriptRoot\creer-certificats.ps1" -Dossier $Dossier -PortHttp $p.portHttp -PortHttps $p.portHttps *>&1 | ForEach-Object { Ecrire "  $_" }
        }
    }

    # 6. Compte SQL en lecture seule pour les tableaux de bord (facultatif).
    if ($p.compteLecture) {
        try { & "$PSScriptRoot\compte-lecture-seule.ps1" -Dossier $Dossier *>&1 | ForEach-Object { Ecrire "  $_" } }
        catch { Ecrire "Compte lecture seule non créé : $($_.Exception.Message). Les tableaux de bord liront avec le compte des services." Yellow }
    }

    DemarrerServices

    # 7. Vérification : l'API répond et le worker a ouvert Sage.
    Start-Sleep -Seconds 5
    try {
        $sante = Invoke-RestMethod -Uri "http://localhost:$($p.portHttp)/api/v1/sante" -TimeoutSec 20
        Ecrire "Santé : API $($sante.api), worker $($sante.worker | ConvertTo-Json -Compress)" Green
    }
    catch { Ecrire "L'API ne répond pas encore ($($_.Exception.Message)) : voir $Dossier\worker\logs et l'Observateur d'événements." Yellow }

    # 8. Récapitulatif pour l'installateur et pour l'administrateur.
    $https = Test-Path "$Dossier\api\appsettings.Https.json"
    $base = if ($https) { "https://$($env:COMPUTERNAME):$($p.portHttps)" } else { "http://$($env:COMPUTERNAME):$($p.portHttp)" }
    $recap = @"
Sage 100 API installée le $(Get-Date -Format 'dd/MM/yyyy HH:mm') sur $env:COMPUTERNAME

Borne de caisse     : $base/borne/
CRM commerciaux     : $base/crm/
Livraison           : $base/livraison/
Tableaux de bord    : $base/tableau-de-bord/
Documentation API   : http://localhost:$($p.portHttp)/swagger

Clé d'API de la borne : $($a.Sage.ClesApi.borne)
(à saisir au premier lancement de la borne et des applications ; gardée dans $cheminApi)

$(if ($https) { "Tablettes : installez une fois $Dossier\certificats\autorite-sage100api.cer comme « Certificat CA »." } else { "HTTPS non configuré : la borne hors ligne exige le HTTPS sur les tablettes." })
Journaux par mode de règlement : $cheminWorker (journauxParMode), puis Restart-Service Sage100Api.Worker
Journal de l'installation : $journal
"@
    [IO.File]::WriteAllText("$Dossier\LISEZMOI-installation.txt", $recap, $utf8)
    Ecrire "Terminé." Green
}
catch {
    Ecrire "ERREUR : $($_.Exception.Message)" Red
    exit 1
}
