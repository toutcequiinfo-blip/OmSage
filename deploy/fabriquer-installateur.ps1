<#
  Fabrique Setup.exe, l'installateur de production de l'API Sage 100, sur le PC de développement.

  À lancer depuis PowerShell (pas besoin d'être administrateur) :
      powershell -ExecutionPolicy Bypass -File C:\Dev\OmSage\deploy\fabriquer-installateur.ps1

  Prérequis sur ce PC (une seule fois) :
    - Visual Studio 2022 avec le SDK .NET 8 et la référence COM Objets Métiers du worker (déjà en place pour le développement) ;
    - Inno Setup 6 ou 7, gratuit : https://jrsoftware.org/isdl.php (ou : winget install JRSoftware.InnoSetup).

  Résultat : deploy\sortie\Sage100Api-Setup-<version>.exe, un seul fichier à copier sur le serveur du client.

  Pour un client en Sage 100 V9 (paquet à part, la V12 n'est pas touchée) :
      powershell -ExecutionPolicy Bypass -File C:\Dev\OmSage\deploy\fabriquer-installateur.ps1 -VersionSage V9
  Il faut la DLL Objets Métiers V9 (objets100c.dll copiée depuis un poste V9, sans l'installer) dans C:\Dev\OmSage\lib\sage-v9\.
  Le worker est alors compilé avec elle, et le fichier s'appelle Sage100Api-Setup-<version>-V9.exe.
  Le serveur n'a besoin ni de Visual Studio, ni de Git, ni du runtime .NET 8 (l'API est publiée autonome) ;
  il lui faut les Objets Métiers Sage 100 V12 et le .NET Framework 4.8 (présent d'office sur Windows récent).
  Aucun secret n'entre dans le paquet : appsettings.Local.json, certificats et bases restent sur ce PC.
#>
param(
    [string]$Version = "$(Get-Date -Format 'yyyy.M.d').$([int](Get-Date -Format 'HHmm'))",
    [ValidateSet("V12", "V9")] [string]$VersionSage = "V12",
    [string]$DllV9 = ""
)

$ErrorActionPreference = "Stop"
$depot = Split-Path -Parent $PSScriptRoot
$sortie = Join-Path $PSScriptRoot "sortie"
$paquet = Join-Path $sortie "paquet"
$utf8 = New-Object System.Text.UTF8Encoding $false
function Etape($texte) { Write-Host "`n==> $texte" -ForegroundColor Cyan }

# Inno Setup : cherché d'abord, pour ne pas compiler pour rien s'il manque.
# Inno Setup 6 ou 7, 32 ou 64 bits, installé pour tous ou pour l'utilisateur seul : la version la plus récente est prise.
$iscc = @("${env:ProgramFiles(x86)}", "$env:ProgramFiles", "$env:LOCALAPPDATA\Programs") | Where-Object { $_ -and (Test-Path $_) } |
    ForEach-Object { Get-ChildItem $_ -Directory -Filter "Inno Setup *" -ErrorAction SilentlyContinue } |
    ForEach-Object { Join-Path $_.FullName "ISCC.exe" } | Where-Object { Test-Path $_ } |
    Sort-Object { (Get-Item $_).VersionInfo.FileVersionRaw } -Descending | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup introuvable. Installez-le (https://jrsoftware.org/isdl.php ou « winget install JRSoftware.InnoSetup »), puis relancez ce script."
}

Etape "Préparation du paquet $Version"
if (Test-Path $paquet) { Remove-Item $paquet -Recurse -Force }
New-Item -ItemType Directory -Force "$paquet\api", "$paquet\worker", "$paquet\outils" | Out-Null

Etape "Publication de l'API (autonome, Windows 64 bits)"
dotnet publish "$depot\src\Sage100Api\Sage100Api.csproj" -c Release -r win-x64 --self-contained true -o "$paquet\api" --nologo
if ($LASTEXITCODE -ne 0) { throw "La publication de l'API a échoué." }
# Ni secrets ni données de ce PC dans le paquet.
Get-ChildItem "$paquet\api" -Include "appsettings.Local.json", "appsettings.Https.json", "*.db", "*.db-shm", "*.db-wal" -Recurse | Remove-Item -Force
# appsettings.json livré sans la base de développement ni la clé d'essai : configurer.ps1 écrit celles du serveur.
$appsettings = Get-Content "$paquet\api\appsettings.json" -Raw -Encoding UTF8 | ConvertFrom-Json
$appsettings.Sage.ChaineSql = ""
$appsettings.Sage.PSObject.Properties.Remove("ClesApi")
[IO.File]::WriteAllText("$paquet\api\appsettings.json", ($appsettings | ConvertTo-Json -Depth 20), $utf8)

Etape "Compilation du worker Objets Métiers (MSBuild de Visual Studio, à cause de la référence COM)"
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "Visual Studio 2022 introuvable (vswhere.exe absent)." }
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild de Visual Studio introuvable." }
$optionsV9 = @()
if ($VersionSage -eq "V9") {
    # Interop générée depuis la DLL V9 (tlbimp du SDK Windows, installé avec Visual Studio), sans enregistrer la DLL sur ce PC.
    if (-not $DllV9) { $DllV9 = Join-Path $depot "lib\sage-v9\objets100c.dll" }
    if (-not (Test-Path $DllV9)) { throw "DLL Objets Métiers V9 introuvable : $DllV9. Copiez objets100c.dll depuis un poste Sage V9 dans ce dossier." }
    $tlbimp = Get-ChildItem "${env:ProgramFiles(x86)}\Microsoft SDKs\Windows" -Recurse -Filter "TlbImp.exe" -ErrorAction SilentlyContinue |
        Sort-Object { $_.VersionInfo.FileVersionRaw } -Descending | Select-Object -First 1
    if (-not $tlbimp) { throw "TlbImp.exe introuvable (SDK Windows de Visual Studio)." }
    $interop = Join-Path $sortie "v9\Interop.Objets100cLib.dll"
    New-Item -ItemType Directory -Force (Split-Path $interop) | Out-Null
    & $tlbimp.FullName $DllV9 "/out:$interop" /namespace:Objets100cLib /machine:X86 /silent
    if ($LASTEXITCODE -ne 0) { throw "TlbImp n'a pas pu lire la DLL V9." }
    $optionsV9 = @("/p:SageVersion=V9", "/p:SageInterop=$interop")
    Write-Host "  Worker compilé pour Sage 100 V9 avec $DllV9"
}
& $msbuild "$depot\src\Sage100Api.Worker\Sage100Api.Worker.csproj" /restore /p:Configuration=Release "/p:OutDir=$paquet\worker\" @optionsV9 /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "La compilation du worker a échoué." }
if (Test-Path "$paquet\worker\logs") { Remove-Item "$paquet\worker\logs" -Recurse -Force }
# Modèle neutre : configurer.ps1 le remplit avec le serveur, la base et l'utilisateur saisis dans l'assistant.
$modele = [ordered]@{ serveur = ""; baseCial = ""; baseCpta = ""; utilisateur = "<Administrateur>"; motDePasse = ""; chaineSql = ""; journauxParMode = @{} }
[IO.File]::WriteAllText("$paquet\worker\worker.json", ($modele | ConvertTo-Json -Depth 5), $utf8)

Etape "Outils de configuration du serveur"
Copy-Item "$PSScriptRoot\serveur\configurer.ps1", "$PSScriptRoot\serveur\societes.ps1", "$PSScriptRoot\creer-certificats.ps1", "$PSScriptRoot\compte-lecture-seule.ps1" "$paquet\outils"

Etape "Fabrication de Setup.exe avec $iscc"
& $iscc "/DVersion=$Version" "/DPaquet=$paquet" "/DSortie=$sortie" "/DSageVersion=$VersionSage" "$PSScriptRoot\installateur\Sage100Api.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup a échoué (voir les messages ci-dessus)." }

$setup = Join-Path $sortie "Sage100Api-Setup-$Version$(if ($VersionSage -ne 'V12') { "-$VersionSage" }).exe"
Write-Host "`nTerminé : $setup" -ForegroundColor Green
Write-Host "  Copiez ce fichier sur le serveur du client et lancez-le (clic droit > Exécuter en tant qu'administrateur si Windows le demande)."
Write-Host "  Pour une mise à jour, relancez simplement un Setup plus récent : la configuration du serveur est gardée."
Start-Process explorer.exe "/select,`"$setup`""
