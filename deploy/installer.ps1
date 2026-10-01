<#
  Installe (ou met à jour) l'API Sage 100 et son worker Objets Métiers en services Windows.

  À lancer sur le serveur Sage, dans PowerShell ouvert « en tant qu'administrateur », depuis le dossier du dépôt :
      powershell -ExecutionPolicy Bypass -File deploy\installer.ps1

  Prérequis : Visual Studio 2022 (ou ses Build Tools) avec le SDK .NET 8, et les Objets Métiers Sage 100 V12.
  La référence COM « Objets métiers Sage 100c » doit être présente dans le projet du worker (voir README).

  Les deux services tournent sous le même compte Windows : il doit avoir accès à SQL Server (sécurité intégrée)
  et à Sage. Ce compte partagé permet aussi à l'API de se connecter au canal nommé du worker.
#>
param(
    [string]$Dossier = "C:\Sage100Api",
    [int]$PortHttp = 5080,
    [int]$PortHttps = 5443
)

$ErrorActionPreference = "Stop"
$depot = Split-Path -Parent $PSScriptRoot
$services = @(
    @{ Nom = "Sage100Api.Worker"; Affichage = "Sage 100 API - Worker Objets Métiers"; Exe = "$Dossier\worker\Sage100Api.Worker.exe" },
    @{ Nom = "Sage100Api";        Affichage = "Sage 100 API";                         Exe = "$Dossier\api\Sage100Api.exe" }
)

function Etape($texte) { Write-Host "`n==> $texte" -ForegroundColor Cyan }

# Droit « Ouvrir une session en tant que service » : sans lui, un service sous un compte utilisateur refuse de démarrer (erreur 1069).
Add-Type @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

public static class DroitService
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
        uint statut = LsaOpenPolicy(IntPtr.Zero, ref attributs, 0x810, out politique); // POLICY_LOOKUP_NAMES | POLICY_CREATE_ACCOUNT
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

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Ouvrez PowerShell « en tant qu'administrateur », puis relancez ce script."
}

Write-Host "Arrêtez d'abord l'exécution dans Visual Studio : l'API et le worker lancés par F5 occupent le port $PortHttp et le canal du worker." -ForegroundColor Yellow

Etape "Arrêt des services existants"
foreach ($s in $services) {
    $existant = Get-Service -Name $s.Nom -ErrorAction SilentlyContinue
    if ($existant -and $existant.Status -ne "Stopped") { Stop-Service -Name $s.Nom -Force; Write-Host "  $($s.Nom) arrêté" }
}

Etape "Publication de l'API dans $Dossier\api"
dotnet publish "$depot\src\Sage100Api\Sage100Api.csproj" -c Release -o "$Dossier\api" --nologo
if ($LASTEXITCODE -ne 0) { throw "La publication de l'API a échoué." }
if (-not (Test-Path "$Dossier\api\appsettings.Local.json") -and (Test-Path "$depot\src\Sage100Api\appsettings.Local.json")) {
    Copy-Item "$depot\src\Sage100Api\appsettings.Local.json" "$Dossier\api\"
    Write-Host "  appsettings.Local.json copié depuis le dépôt (clés d'API, chaîne SQL)"
}

Etape "Compilation du worker dans $Dossier\worker"
# La référence COM exige le MSBuild de Visual Studio : « dotnet build » ne sait pas la résoudre.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "Visual Studio 2022 introuvable (vswhere.exe absent)." }
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild de Visual Studio introuvable." }
$sortie = Join-Path $env:TEMP "Sage100Api.Worker.build\"
if (Test-Path $sortie) { Remove-Item $sortie -Recurse -Force }
& $msbuild "$depot\src\Sage100Api.Worker\Sage100Api.Worker.csproj" /restore /p:Configuration=Release "/p:OutDir=$sortie" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "La compilation du worker a échoué." }
New-Item -ItemType Directory -Force "$Dossier\worker" | Out-Null
# worker.json n'est copié qu'à la première installation : on garde les réglages du serveur.
Get-ChildItem $sortie | Where-Object { -not ($_.Name -eq "worker.json" -and (Test-Path "$Dossier\worker\worker.json")) } |
    Copy-Item -Destination "$Dossier\worker" -Recurse -Force

Etape "Création des services"
$creer = $services | Where-Object { -not (Get-Service -Name $_.Nom -ErrorAction SilentlyContinue) }
if ($creer) {
    Write-Host "  Compte Windows des services : celui qui a accès à SQL Server et à Sage, par exemple $env:COMPUTERNAME\$env:USERNAME"
    Write-Host "  Le compte doit avoir un mot de passe : Windows refuse de lancer un service sous un compte sans mot de passe."
    $compte = Get-Credential -Message "Compte Windows des services Sage 100 API" -UserName "$env:COMPUTERNAME\$env:USERNAME"
    try { [DroitService]::Accorder($compte.UserName); Write-Host "  Droit « ouvrir une session en tant que service » accordé à $($compte.UserName)" }
    catch { Write-Host "  Droit de service non accordé automatiquement ($($_.Exception.Message)) : voir le message en cas d'échec au démarrage." -ForegroundColor Yellow }
    foreach ($s in $creer) {
        New-Service -Name $s.Nom -DisplayName $s.Affichage -BinaryPathName "`"$($s.Exe)`"" -StartupType Automatic -Credential $compte | Out-Null
        Write-Host "  $($s.Nom) créé"
    }
}
foreach ($s in $services) {
    # Redémarrage automatique en cas d'arrêt inattendu (5 s, 5 s, puis 30 s ; compteur remis à zéro chaque jour).
    sc.exe failure $s.Nom reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
}

Etape "Pare-feu : ports $PortHttp (HTTP) et $PortHttps (HTTPS)"
if (-not (Get-NetFirewallRule -Name "Sage100Api" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name "Sage100Api" -DisplayName "Sage 100 API" -Direction Inbound -Protocol TCP -LocalPort $PortHttp, $PortHttps -Action Allow -Profile Any | Out-Null
}

Etape "Démarrage"
foreach ($s in $services) {
    try { Start-Service -Name $s.Nom; Write-Host "  $($s.Nom) démarré" -ForegroundColor Green }
    catch {
        Write-Host "  $($s.Nom) n'a pas démarré : $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "  Erreur de connexion du compte ? Ouvrez services.msc > $($s.Nom) > Connexion, ressaisissez le mot de passe, puis démarrez le service."
    }
}

Write-Host "`nTerminé." -ForegroundColor Green
Write-Host "  Borne (HTTP)  : http://$($env:COMPUTERNAME):$PortHttp/borne/"
if (Test-Path "$Dossier\api\appsettings.Https.json") { Write-Host "  Borne (HTTPS) : https://$($env:COMPUTERNAME):$PortHttps/borne/" }
else { Write-Host "  Pour le HTTPS : lancez deploy\creer-certificats.ps1, puis relancez ce script." }
Write-Host "  Journal du worker : $Dossier\worker\logs"
