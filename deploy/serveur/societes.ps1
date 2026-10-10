<#
  Déclare la ou les sociétés Sage (bases) servies par l'API installée, sans réinstaller.

  À lancer dans PowerShell ouvert « en tant qu'administrateur » :
      powershell -ExecutionPolicy Bypass -File C:\Dev\OmSage\deploy\serveur\societes.ps1 -Bases "BIJOU,MODE"
  Sur un serveur installé par Setup.exe, le script est dans le dossier outils :
      powershell -ExecutionPolicy Bypass -File C:\Sage100Api\outils\societes.ps1 -Bases "BIJOU,MODE"

  -Bases   : bases Gestion commerciale, séparées par des virgules ; la première est la société principale
             (elle garde les tournées, activités CRM et le journal déjà enregistrés).
  -Comptas : bases Comptabilité dans le même ordre, si leur nom diffère (vide : les mêmes noms).
  Une seule base : retour au fonctionnement à une société.

  Le script met à jour worker\worker.json (dossiers) et api\appsettings.Local.json (Sage:Dossiers), garde les réglages
  déjà faits pour une société (journaux par mode, intitulé...), puis redémarre les services.
  Toutes les bases doivent être sur le même serveur SQL.

  Utilisateur Sage propre à une société (celui avec lequel le worker ouvre la base par les Objets Métiers) :
      powershell -ExecutionPolicy Bypass -File C:\Dev\OmSage\deploy\serveur\societes.ps1 -Societe VITA2026 -Utilisateur "<Administrateur>"
  Le mot de passe est demandé à l'écran (Entrée seule : pas de mot de passe). Sans ce réglage, une société s'ouvre avec
  l'utilisateur du haut de worker.json (celui de la société principale).
#>
param(
    [string]$Bases = "",
    [string]$Comptas = "",
    [string]$Societe = "",
    [string]$Utilisateur = "",
    [string]$MotDePasse,
    [string]$Dossier = "",
    [switch]$SansRedemarrer
)

$ErrorActionPreference = "Stop"
$utf8 = New-Object System.Text.UTF8Encoding $false
# Chiffrement des secrets et verrou du dossier (securite.ps1 : à côté de ce script une fois installé, deploy\serveur dans le dépôt).
$securite = @("$PSScriptRoot\securite.ps1", "$PSScriptRoot\serveur\securite.ps1") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($securite) { . $securite -Dossier $Dossier }
function LireJson($chemin) { if (Test-Path $chemin) { Get-Content $chemin -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null } }
function EcrireJson($chemin, $objet) { [IO.File]::WriteAllText($chemin, ($objet | ConvertTo-Json -Depth 20), $utf8) }
function Propriete($objet, $nom, $valeur) { $objet | Add-Member -NotePropertyName $nom -NotePropertyValue $valeur -Force }

# Dossier d'installation : celui des outils (Setup.exe), sinon C:\Sage100Api (installation de développement).
if (-not $Dossier) {
    $parent = Split-Path -Parent $PSScriptRoot
    $Dossier = if (Test-Path "$parent\worker\worker.json") { $parent } else { "C:\Sage100Api" }
}
$cheminWorker = "$Dossier\worker\worker.json"
$cheminApi = "$Dossier\api\appsettings.Local.json"
if (-not (Test-Path $cheminWorker) -or -not (Test-Path $cheminApi)) { throw "Installation introuvable dans $Dossier (worker\worker.json et api\appsettings.Local.json)." }

# ---------- Utilisateur Sage d'une société ----------
if ($Societe) {
    if (-not $Utilisateur) { throw "Indiquez l'utilisateur Sage de $Societe : -Utilisateur ""<Administrateur>""." }
    if (-not $PSBoundParameters.ContainsKey("MotDePasse")) {
        $saisi = Read-Host "Mot de passe Sage de $Utilisateur dans $Societe (Entrée seule : aucun)" -AsSecureString
        $MotDePasse = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($saisi))
    }
    $w = LireJson $cheminWorker
    $d = @($w.dossiers | Where-Object { $_ -and "$($_.baseCial)" -eq $Societe }) | Select-Object -First 1
    if (-not $d) {
        if ("$($w.baseCial)" -ne $Societe -or @($w.dossiers | Where-Object { $_ }).Count -gt 0) {
            throw "Société $Societe absente de $cheminWorker. Déclarez-la d'abord : societes.ps1 -Bases ""$($w.baseCial),$Societe""."
        }
        # Une seule société : c'est l'utilisateur du haut du fichier.
        Propriete $w "utilisateur" $Utilisateur
        Propriete $w "motDePasse" $MotDePasse
    }
    else {
        Propriete $d "utilisateur" $Utilisateur
        Propriete $d "motDePasse" $MotDePasse
    }
    EcrireJson $cheminWorker $w
    if ($securite) { ProtegerSecrets $Dossier }
    Write-Host "Utilisateur Sage de $Societe : $Utilisateur (enregistré, mot de passe chiffré, dans $cheminWorker)." -ForegroundColor Green
    if (-not $SansRedemarrer -and (Get-Service "Sage100Api.Worker" -ErrorAction SilentlyContinue)) {
        Restart-Service "Sage100Api.Worker"
        Write-Host "  Service Sage100Api.Worker redémarré"
    }
    return
}
if (-not $Bases) { throw "Indiquez les bases (-Bases ""BIJOU,MODE"") ou l'utilisateur d'une société (-Societe MODE -Utilisateur ...)." }

$listeBases = @($Bases.Split(",") | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$listeComptas = @($Comptas.Split(",") | ForEach-Object { $_.Trim() })
if (-not $listeBases) { throw "Indiquez au moins une base Gestion commerciale." }
$societes = @(for ($i = 0; $i -lt $listeBases.Count; $i++) {
    [pscustomobject]@{ baseCial = $listeBases[$i]; baseCpta = $(if ($i -lt $listeComptas.Count -and $listeComptas[$i]) { $listeComptas[$i] } else { $listeBases[$i] }) }
})

# Worker : la principale en haut du fichier, une session Objets Métiers par société dans « dossiers ».
$w = LireJson $cheminWorker
Propriete $w "baseCial" $societes[0].baseCial
Propriete $w "baseCpta" $societes[0].baseCpta
if ($societes.Count -gt 1) {
    $anciennes = @($w.dossiers | Where-Object { $_ })
    $liste = foreach ($s in $societes) {
        $d = $anciennes | Where-Object { "$($_.baseCial)" -eq $s.baseCial } | Select-Object -First 1
        if (-not $d) { $d = [pscustomobject]@{} }
        Propriete $d "baseCial" $s.baseCial
        Propriete $d "baseCpta" $s.baseCpta
        $d
    }
    Propriete $w "dossiers" @($liste)
}
elseif ($w.PSObject.Properties["dossiers"]) { $w.PSObject.Properties.Remove("dossiers") }
EcrireJson $cheminWorker $w

# API : la chaîne SQL vise la principale ; Sage:Dossiers liste les sociétés proposées à la connexion.
# Le fichier du dépôt (Visual Studio) est mis à jour aussi s'il existe.
$depot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
foreach ($f in @($cheminApi, "$depot\src\Sage100Api\appsettings.Local.json") | Where-Object { Test-Path $_ } | Select-Object -Unique) {
    $a = LireJson $f
    if (-not $a.Sage) { Propriete $a "Sage" ([pscustomobject]@{}) }
    if ($a.Sage.ChaineSql) {
        # PSBase : sans lui, PowerShell prend « $b.InitialCatalog = ... » pour une clé du dictionnaire.
        $protegee = "$($a.Sage.ChaineSql)".StartsWith("dpapi:")
        $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder (DevoilerTexte $a.Sage.ChaineSql)
        $b.PSBase.InitialCatalog = $societes[0].baseCial
        Propriete $a.Sage "ChaineSql" $(if ($protegee) { ProtegerTexte $b.PSBase.ConnectionString } else { $b.PSBase.ConnectionString })
    }
    if ($societes.Count -gt 1) {
        $anciens = @($a.Sage.Dossiers | Where-Object { $_ })
        $liste = foreach ($s in $societes) {
            $d = $anciens | Where-Object { "$($_.Base)" -eq $s.baseCial } | Select-Object -First 1
            if (-not $d) { $d = [pscustomobject]@{} }
            Propriete $d "Base" $s.baseCial
            $d
        }
        Propriete $a.Sage "Dossiers" @($liste)
    }
    elseif ($a.Sage.PSObject.Properties["Dossiers"]) { $a.Sage.PSObject.Properties.Remove("Dossiers") }
    EcrireJson $f $a
    Write-Host "  $f mis à jour"
}

if ($societes.Count -gt 1) { Write-Host "Sociétés servies : $($listeBases -join ', ') (la société se choisit à la connexion des applications)." -ForegroundColor Green }
else { Write-Host "Une seule société : $($listeBases[0])." -ForegroundColor Green }

# Mot de passe saisi : chiffré dans worker.json avant le redémarrage.
if ($securite) { ProtegerSecrets $Dossier }

if (-not $SansRedemarrer) {
    foreach ($nom in "Sage100Api.Worker", "Sage100Api") {
        if (Get-Service $nom -ErrorAction SilentlyContinue) { Restart-Service $nom; Write-Host "  Service $nom redémarré" }
    }
}
