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
  Toutes les bases doivent être sur le même serveur SQL ; l'utilisateur Sage est le même partout, sauf si on en donne un
  propre à une société dans worker.json (dossiers : utilisateur, motDePasse).
#>
param(
    [Parameter(Mandatory = $true)][string]$Bases,
    [string]$Comptas = "",
    [string]$Dossier = "",
    [switch]$SansRedemarrer
)

$ErrorActionPreference = "Stop"
$utf8 = New-Object System.Text.UTF8Encoding $false
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
        $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $a.Sage.ChaineSql
        $b.PSBase.InitialCatalog = $societes[0].baseCial
        Propriete $a.Sage "ChaineSql" $b.PSBase.ConnectionString
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

if (-not $SansRedemarrer) {
    foreach ($nom in "Sage100Api.Worker", "Sage100Api") {
        if (Get-Service $nom -ErrorAction SilentlyContinue) { Restart-Service $nom; Write-Host "  Service $nom redémarré" }
    }
}
