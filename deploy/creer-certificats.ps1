<#
  Crée le certificat HTTPS de l'API pour les tablettes, signé par une autorité locale propre à ce serveur.

  À lancer sur le serveur Sage, dans PowerShell ouvert « en tant qu'administrateur » :
      powershell -ExecutionPolicy Bypass -File deploy\creer-certificats.ps1

  Produit dans $Dossier\certificats :
    - autorite-sage100api.cer : à installer UNE FOIS sur chaque tablette (Paramètres > Sécurité >
      Chiffrement et identifiants > Installer un certificat > Certificat CA) ;
    - serveur.pfx : utilisé par l'API (fichier appsettings.Https.json écrit dans $Dossier\api).

  Le certificat couvre le nom du serveur et ses adresses IP actuelles : réservez l'adresse IP du serveur dans la box
  ou le routeur, sinon relancez ce script (et réinstallez l'autorité sur les tablettes) si elle change.
#>
param(
    [string]$Dossier = "C:\Sage100Api",
    [string[]]$Noms = @($env:COMPUTERNAME),
    [int]$PortHttp = 5080,
    [int]$PortHttps = 5443
)

$ErrorActionPreference = "Stop"
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Ouvrez PowerShell « en tant qu'administrateur », puis relancez ce script."
}

$dossierCert = Join-Path $Dossier "certificats"
New-Item -ItemType Directory -Force $dossierCert, (Join-Path $Dossier "api") | Out-Null

$ips = @(Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -ne "127.0.0.1" -and $_.IPAddress -notlike "169.254.*" } |
    Select-Object -ExpandProperty IPAddress)
$nomsCouverts = @(@($Noms) + "localhost" | Select-Object -Unique)
$ipsCouvertes = @($ips) + "127.0.0.1"
$san = "2.5.29.17={text}" + ((@($nomsCouverts | ForEach-Object { "DNS=$_" }) + @($ipsCouvertes | ForEach-Object { "IPAddress=$_" })) -join "&")
Write-Host "Noms couverts : $($nomsCouverts -join ', ')"
Write-Host "Adresses IP couvertes : $($ipsCouvertes -join ', ')"

# 1. Autorité locale (sert uniquement à signer le certificat du serveur).
$autorite = New-SelfSignedCertificate -Type Custom -Subject "CN=Sage100Api Autorite locale $env:COMPUTERNAME" `
    -KeyUsage CertSign, CRLSign, DigitalSignature -KeyExportPolicy NonExportable -KeyLength 2048 -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears(10) -CertStoreLocation Cert:\LocalMachine\My `
    -TextExtension @("2.5.29.19={critical}{text}ca=1&pathlength=0")

# 2. Certificat du serveur (2 ans : la limite acceptée par tous les navigateurs).
$serveur = New-SelfSignedCertificate -Type Custom -Subject "CN=$($Noms[0])" -Signer $autorite `
    -KeyUsage DigitalSignature, KeyEncipherment -KeyExportPolicy Exportable -KeyLength 2048 -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddDays(730) -CertStoreLocation Cert:\LocalMachine\My `
    -TextExtension @($san, "2.5.29.37={text}1.3.6.1.5.5.7.3.1")

# 3. Fichiers : autorité (publique) pour les tablettes, PFX protégé par un mot de passe aléatoire pour l'API.
$cer = Join-Path $dossierCert "autorite-sage100api.cer"
Export-Certificate -Cert $autorite -FilePath $cer -Type CERT | Out-Null
$motDePasse = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 32 | ForEach-Object { [char]$_ })
$pfx = Join-Path $dossierCert "serveur.pfx"
Export-PfxCertificate -Cert $serveur -FilePath $pfx -Password (ConvertTo-SecureString $motDePasse -AsPlainText -Force) | Out-Null

# 4. Ce PC fait confiance à l'autorité ; sa clé privée est supprimée : personne ne pourra signer d'autre certificat avec.
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Remove-Item -Path "Cert:\LocalMachine\My\$($autorite.Thumbprint)" -DeleteKey
Remove-Item -Path "Cert:\LocalMachine\My\$($serveur.Thumbprint)" -DeleteKey

# 5. Points d'écoute de l'API.
$config = @{
    Kestrel = @{
        Endpoints = @{
            Http  = @{ Url = "http://0.0.0.0:$PortHttp" }
            Https = @{ Url = "https://0.0.0.0:$PortHttps"; Certificate = @{ Path = $pfx; Password = $motDePasse } }
        }
    }
}
$json = Join-Path $Dossier "api\appsettings.Https.json"
[IO.File]::WriteAllText($json, ($config | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
# Chiffrement des secrets et verrou du dossier (securite.ps1 : à côté de ce script une fois installé, deploy\serveur dans le dépôt).
$securite = @("$PSScriptRoot\securite.ps1", "$PSScriptRoot\serveur\securite.ps1") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($securite) { . $securite -Dossier $Dossier }
if ($securite) { ProtegerSecrets $Dossier }

Write-Host "`nCertificats créés dans $dossierCert" -ForegroundColor Green
Write-Host "  1. Copiez autorite-sage100api.cer sur chaque tablette et installez-le comme « Certificat CA »."
Write-Host "  2. Redémarrez l'API : Restart-Service Sage100Api (ou relancez deploy\installer.ps1)."
Write-Host "  3. Sur la tablette : https://$($Noms[0]):$PortHttps/borne/ $(if ($ips.Count -gt 0) { "ou https://$($ips[0]):$PortHttps/borne/" })"
