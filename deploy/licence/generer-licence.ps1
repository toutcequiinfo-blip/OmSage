<#
  Fabrique la licence d'un client : fichier licence.lic signé, valable pour UN serveur et les sociétés (bases Sage) indiquées.
  À lancer sur le poste de développement (celui qui garde la clé privée), jamais chez le client.

  1. Première fois seulement : création des clés de licence
         powershell -ExecutionPolicy Bypass -File C:\Dev\OmSage\deploy\licence\generer-licence.ps1 -CreerCles
     - lib\licence\cle-privee.xml : signe les licences. À SAUVEGARDER (clé USB, coffre) et à ne jamais donner :
       sans elle, plus aucune licence ne peut être faite pour les installations existantes.
     - lib\licence\cle-publique.xml : intégrée à l'API à la compilation (installer.ps1, fabriquer-installateur.ps1).
     Ni l'une ni l'autre ne vont dans Git.

  2. Pour chaque client : l'identifiant de son serveur est dans C:\Sage100Api\LISEZMOI-installation.txt
     (ou https://<serveur>:5443/api/v1/licence), puis :
         powershell -ExecutionPolicy Bypass -File C:\Dev\OmSage\deploy\licence\generer-licence.ps1 -Client "VITA" -Serveur 1a2b3c4d-... -NomServeur SRV-SAGE-DBA -Bases "VITA2026,VIGIE" -Expiration 2027-12-31
     -Bases "*" : toutes les sociétés du serveur. Sans -Expiration : licence sans fin.
     Le fichier va dans deploy\sortie\licences ; copiez-le dans C:\Sage100Api du serveur, sous le nom licence.lic.
#>
param(
    [switch]$CreerCles,
    [string]$Client,
    [string]$Serveur,
    [string]$NomServeur = "",
    [string]$Bases = "*",
    [string]$Expiration = "",
    [string]$Sortie = ""
)

$ErrorActionPreference = "Stop"
$depot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dossierCles = Join-Path $depot "lib\licence"
$privee = Join-Path $dossierCles "cle-privee.xml"
$publique = Join-Path $dossierCles "cle-publique.xml"
$utf8 = New-Object System.Text.UTF8Encoding $false

# Fournisseur « RSA et AES » : signatures SHA-256 (l'ancien fournisseur par défaut ne les connaît pas).
function NouvelleRsa {
    $csp = New-Object System.Security.Cryptography.CspParameters 24
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider 2048, $csp
    $rsa.PersistKeyInCsp = $false
    return $rsa
}

if ($CreerCles) {
    if (Test-Path $privee) { throw "Les clés existent déjà ($privee). Les remplacer rendrait invalides toutes les licences déjà livrées." }
    New-Item -ItemType Directory -Force $dossierCles | Out-Null
    $rsa = NouvelleRsa
    [IO.File]::WriteAllText($privee, $rsa.ToXmlString($true), $utf8)
    [IO.File]::WriteAllText($publique, $rsa.ToXmlString($false), $utf8)
    Write-Host "Clés créées dans $dossierCles" -ForegroundColor Green
    Write-Host "  SAUVEGARDEZ $privee hors de ce PC (clé USB, coffre) : elle seule permet de faire les licences." -ForegroundColor Yellow
    Write-Host "  Relancez ensuite deploy\installer.ps1 (ce PC) et fabriquer-installateur.ps1 (Setup des clients) : l'API contrôlera la licence."
    return
}

if (-not (Test-Path $privee)) { throw "Clé privée introuvable ($privee). Première fois : relancez ce script avec -CreerCles." }
if (-not $Client) { throw "Indiquez le client : -Client ""VITA""." }
if (-not $Serveur) { throw "Indiquez l'identifiant du serveur : -Serveur <identifiant de LISEZMOI-installation.txt>." }
$Serveur = $Serveur.Trim().ToLowerInvariant()
if ($Serveur -notmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$') {
    Write-Host "Attention : « $Serveur » ne ressemble pas à un identifiant Windows (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)." -ForegroundColor Yellow
}
$listeBases = @($Bases.Split(",") | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if (-not $listeBases) { $listeBases = @("*") }
$fin = $null
if ($Expiration) { $fin = ([datetime]::ParseExact($Expiration, "yyyy-MM-dd", [Globalization.CultureInfo]::InvariantCulture)).ToString("yyyy-MM-dd") }

# Contenu signé tel quel (JSON en base64) : l'API vérifie la signature sur ces octets exacts.
$contenu = [ordered]@{
    version = 1; client = $Client; serveur = $Serveur; nomServeur = $NomServeur; bases = $listeBases
    expiration = $fin; emise = (Get-Date).ToString("yyyy-MM-dd")
} | ConvertTo-Json -Compress
$octets = [Text.Encoding]::UTF8.GetBytes($contenu)
$rsa = NouvelleRsa
$rsa.FromXmlString((Get-Content $privee -Raw -Encoding UTF8))
$signature = $rsa.SignData($octets, "SHA256")

$fichier = [ordered]@{ client = $Client; nomServeur = $NomServeur; contenu = [Convert]::ToBase64String($octets); signature = [Convert]::ToBase64String($signature) } |
    ConvertTo-Json
if (-not $Sortie) {
    $dossierSortie = Join-Path $depot "deploy\sortie\licences"
    New-Item -ItemType Directory -Force $dossierSortie | Out-Null
    $nom = "$Client-$(if ($NomServeur) { $NomServeur } else { $Serveur.Substring(0, 8) })" -replace '[^\w\-]', '_'
    $Sortie = Join-Path $dossierSortie "$nom.lic"
}
[IO.File]::WriteAllText($Sortie, $fichier, $utf8)
Write-Host "Licence créée : $Sortie" -ForegroundColor Green
Write-Host "  Client $Client, serveur $Serveur$(if ($NomServeur) { " ($NomServeur)" }), sociétés $($listeBases -join ', ')$(if ($fin) { ", jusqu'au $fin" } else { ', sans fin' })"
Write-Host "  À copier dans le dossier de l'installation du serveur (C:\Sage100Api), sous le nom licence.lic."
