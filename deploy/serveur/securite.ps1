<#
  Protège une installation de l'API Sage 100 :
    1. chiffre les secrets des fichiers de configuration avec Windows (DPAPI, portée machine) : mots de passe Sage et SQL,
       clés d'API, mot de passe du certificat HTTPS. Copiés sur un autre poste, ils sont illisibles ;
    2. verrouille le dossier d'installation : seuls les administrateurs, le système et le compte des services y ont accès.
  Affiche aussi l'identifiant de ce serveur, à donner à l'éditeur pour obtenir la licence.

  Lancé par configurer.ps1 (installation et mise à jour), societes.ps1, creer-certificats.ps1 et compte-lecture-seule.ps1.
  À la main, dans PowerShell « en tant qu'administrateur » :
      powershell -ExecutionPolicy Bypass -File C:\Sage100Api\outils\securite.ps1
  Chargé par « . securite.ps1 », il ne fait rien d'autre que définir ses fonctions.

  Chiffrement identique à l'API (Secrets.cs) et au worker : préfixe « dpapi: », entropie « Sage100Api.secrets.v1 », portée machine.
#>
param(
    [string]$Dossier = "C:\Sage100Api",
    [switch]$SansVerrou
)

Add-Type -AssemblyName System.Security
$script:EntropieSecrets = [Text.Encoding]::UTF8.GetBytes("Sage100Api.secrets.v1")
$script:PrefixeSecret = "dpapi:"

function ProtegerTexte([string]$texte) {
    if ([string]::IsNullOrEmpty($texte) -or $texte.StartsWith($script:PrefixeSecret)) { return $texte }
    $octets = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($texte), $script:EntropieSecrets,
        [Security.Cryptography.DataProtectionScope]::LocalMachine)
    return $script:PrefixeSecret + [Convert]::ToBase64String($octets)
}

function DevoilerTexte([string]$texte) {
    if ([string]::IsNullOrEmpty($texte) -or -not $texte.StartsWith($script:PrefixeSecret)) { return $texte }
    $octets = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($texte.Substring($script:PrefixeSecret.Length)),
        $script:EntropieSecrets, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    return [Text.Encoding]::UTF8.GetString($octets)
}

# Secret ? Mot de passe, clé de signature, clé d'API (sous ClesApi), chaîne SQL contenant un mot de passe.
function EstSecret([string]$nom, [string]$parent, $valeur) {
    if ($valeur -isnot [string] -or $valeur -eq "") { return $false }
    if ($nom -match '^(motDePasse|password|pwd|cleSignature)$') { return $true }
    if ($parent -match '^clesApi$') { return $true }
    if ($nom -match '^chaineSql$' -and $valeur -match '(?i)(password|pwd)\s*=') { return $true }
    return $false
}

function ProtegerObjet($objet, [string]$parent) {
    $change = $false
    if ($objet -is [System.Collections.IEnumerable] -and $objet -isnot [string]) {
        foreach ($e in $objet) { if (ProtegerObjet $e $parent) { $change = $true } }
        return $change
    }
    if ($objet -isnot [System.Management.Automation.PSCustomObject]) { return $false }
    foreach ($p in @($objet.PSObject.Properties)) {
        if (EstSecret $p.Name $parent $p.Value) {
            if (-not $p.Value.StartsWith($script:PrefixeSecret)) { $p.Value = ProtegerTexte $p.Value; $change = $true }
        }
        elseif ($null -ne $p.Value -and $p.Value -isnot [string] -and -not ($p.Value -is [ValueType])) {
            if (ProtegerObjet $p.Value $p.Name) { $change = $true }
        }
    }
    return $change
}

# Chiffre les secrets encore en clair dans worker.json, appsettings.Local.json, appsettings.Https.json et la clé des jetons.
function ProtegerSecrets([string]$dossier) {
    $utf8 = New-Object System.Text.UTF8Encoding $false
    foreach ($f in @("$dossier\worker\worker.json", "$dossier\api\appsettings.Local.json", "$dossier\api\appsettings.Https.json")) {
        if (-not (Test-Path $f)) { continue }
        $json = Get-Content $f -Raw -Encoding UTF8 | ConvertFrom-Json
        if (ProtegerObjet $json "") {
            [IO.File]::WriteAllText($f, ($json | ConvertTo-Json -Depth 20), $utf8)
            Write-Host "  Secrets chiffrés : $f"
        }
    }
    foreach ($cle in Get-ChildItem $dossier -Recurse -Filter "sage100api-jetons.cle" -ErrorAction SilentlyContinue) {
        $texte = (Get-Content $cle.FullName -Raw).Trim()
        if (-not $texte.StartsWith($script:PrefixeSecret)) {
            [IO.File]::WriteAllText($cle.FullName, (ProtegerTexte $texte), $utf8)
            Write-Host "  Clé des jetons chiffrée : $($cle.FullName)"
        }
    }
}

# Compte Windows des services (celui qui doit garder l'accès au dossier) ; « .\nom » devient « POSTE\nom ».
function CompteDesServices {
    $s = Get-CimInstance Win32_Service -Filter "Name='Sage100Api'" -ErrorAction SilentlyContinue
    if (-not $s -or -not $s.StartName) { return $null }
    $compte = $s.StartName
    if ($compte -match '^\.\\') { $compte = "$env:COMPUTERNAME\" + $compte.Substring(2) }
    if ($compte -match '^(LocalSystem|NT AUTHORITY\\SYSTEM)$') { return $null }
    return $compte
}

# Dossier lisible seulement par les administrateurs, le système et le compte des services (lecture et écriture : journaux, bases).
function VerrouillerDossier([string]$dossier) {
    if (-not (Test-Path $dossier)) { return }
    $droits = @("*S-1-5-32-544:(OI)(CI)F", "*S-1-5-18:(OI)(CI)F")
    $compte = CompteDesServices
    if ($compte) { $droits += "${compte}:(OI)(CI)M" }
    & icacls $dossier /inheritance:r /grant:r @droits /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Verrouillage de $dossier refusé (icacls, code $LASTEXITCODE)." }
    # Les sous-dossiers et fichiers reprennent ces seuls droits (d'éventuels droits « Utilisateurs » ajoutés à la main disparaissent).
    & icacls "$dossier\*" /reset /T /C /Q | Out-Null
    Write-Host "  Dossier $dossier réservé aux administrateurs$(if ($compte) { " et à $compte" })"
}

# Identifiant de ce serveur (MachineGuid de Windows, vue 64 bits du registre) : c'est lui que la licence désigne.
function IdentifiantServeur {
    $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try { return "$($hklm.OpenSubKey('SOFTWARE\Microsoft\Cryptography').GetValue('MachineGuid'))".Trim().ToLowerInvariant() }
    finally { $hklm.Dispose() }
}

if ($MyInvocation.InvocationName -ne ".") {
    $ErrorActionPreference = "Stop"
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Ouvrez PowerShell « en tant qu'administrateur », puis relancez ce script."
    }
    ProtegerSecrets $Dossier
    if (-not $SansVerrou) { VerrouillerDossier $Dossier }
    Write-Host "Identifiant de ce serveur pour la licence : $(IdentifiantServeur) ($env:COMPUTERNAME)" -ForegroundColor Cyan
}
