<#
  Crée un compte SQL Server en LECTURE SEULE pour les tableaux de bord, et le déclare à l'API.

  À lancer sur le serveur Sage, dans PowerShell ouvert « en tant qu'administrateur », avec un compte Windows
  administrateur de SQL Server (celui qui a installé Sage, en général) :
      powershell -ExecutionPolicy Bypass -File C:\Dev\OmSage\deploy\compte-lecture-seule.ps1

  Ce que fait le script :
    - lit le serveur et la base Sage dans appsettings.Local.json (chaîne Sage:ChaineSql, et Sage:Dossiers s'il y a plusieurs sociétés) ;
    - crée la connexion SQL « sage100api_lecture » avec un mot de passe aléatoire, membre de db_datareader seulement
      (elle ne peut ni écrire, ni modifier, ni supprimer quoi que ce soit dans Sage) ;
    - écrit sa chaîne dans TableauDeBord:ChaineSql, dans appsettings.Local.json de l'API installée et du dépôt.
  Relancer le script change le mot de passe ; l'API relit la configuration toute seule.

  Si SQL Server n'accepte que l'authentification Windows, le script n'ajoute rien : les tableaux de bord lisent alors
  avec le compte des services, sans verrou et sans jamais écrire (le code ne fait que des SELECT).
#>
param(
    [string]$Dossier = "C:\Sage100Api",
    [string]$Login = "sage100api_lecture"
)

$ErrorActionPreference = "Stop"
$depot = Split-Path -Parent $PSScriptRoot
$fichiers = @("$Dossier\api\appsettings.Local.json", "$depot\src\Sage100Api\appsettings.Local.json") | Where-Object { Test-Path $_ }
if (-not $fichiers) { throw "appsettings.Local.json introuvable dans $Dossier\api ni dans le dépôt : lancez d'abord deploy\installer.ps1." }

# Chaîne Sage actuelle : serveur et base ; avec plusieurs sociétés, toutes leurs bases (Sage:Dossiers).
$chaine = $null
$bases = @()
foreach ($f in $fichiers + "$Dossier\api\appsettings.json") {
    if (-not (Test-Path $f)) { continue }
    $json = Get-Content $f -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($json.Sage -and $json.Sage.ChaineSql) {
        $chaine = $json.Sage.ChaineSql
        $bases = @($json.Sage.Dossiers | Where-Object { $_ -and $_.Base } | ForEach-Object { "$($_.Base)".Trim() })
        break
    }
}
if (-not $chaine) { throw "Chaîne Sage:ChaineSql introuvable dans la configuration de l'API." }
$b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $chaine
$serveur = $b.PSBase.DataSource
if (-not $bases) { $bases = @($b.PSBase.InitialCatalog) }
$base = $bases[0]
Write-Host "Serveur SQL : $serveur   Base(s) Sage : $($bases -join ', ')" -ForegroundColor Cyan

# Connexion d'administration avec le compte Windows qui lance le script.
# PSBase : sans lui, PowerShell prend « $x.DataSource = ... » pour une clé du dictionnaire et SqlClient la refuse.
$admin = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$admin.PSBase.DataSource = $serveur
$admin.PSBase.InitialCatalog = "master"
$admin.PSBase.IntegratedSecurity = $true
$admin.PSBase.TrustServerCertificate = $true
$cnx = New-Object System.Data.SqlClient.SqlConnection $admin.PSBase.ConnectionString
$cnx.Open()
function Executer($sql, $params = @{}) {
    $cmd = $cnx.CreateCommand()
    $cmd.CommandText = $sql
    foreach ($k in $params.Keys) { [void]$cmd.Parameters.AddWithValue($k, $params[$k]) }
    return $cmd.ExecuteScalar()
}

try {
    if ((Executer "SELECT CAST(SERVERPROPERTY('IsIntegratedSecurityOnly') AS int)") -eq 1) {
        Write-Host "SQL Server n'accepte que l'authentification Windows : aucun compte créé." -ForegroundColor Yellow
        Write-Host "Les tableaux de bord liront avec le compte des services (lecture seule par conception, sans verrou)."
        Write-Host "Pour un compte dédié : SQL Server Management Studio > serveur > Propriétés > Sécurité > « SQL Server et Windows »,"
        Write-Host "redémarrez le service SQL Server, puis relancez ce script."
        return
    }

    # Mot de passe aléatoire conforme à la stratégie de mots de passe Windows (majuscule, minuscule, chiffre, symbole).
    $car = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789"
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $octets = New-Object byte[] 24
    $rng.GetBytes($octets)
    $motDePasse = "Tb-" + (-join ($octets | ForEach-Object { $car[$_ % $car.Length] })) + "9a"

    # Noms passés par QUOTENAME : aucune concaténation de texte libre dans le SQL.
    Executer @"
DECLARE @l sysname = @login, @p nvarchar(128) = @mdp, @sql nvarchar(max);
IF SUSER_ID(@l) IS NULL
    SET @sql = N'CREATE LOGIN ' + QUOTENAME(@l) + N' WITH PASSWORD = ' + QUOTENAME(@p, '''') + N', CHECK_POLICY = ON, DEFAULT_DATABASE = ' + QUOTENAME(@base);
ELSE
    SET @sql = N'ALTER LOGIN ' + QUOTENAME(@l) + N' WITH PASSWORD = ' + QUOTENAME(@p, '''') + N'; ALTER LOGIN ' + QUOTENAME(@l) + N' ENABLE';
EXEC (@sql);
"@ @{ "@login" = $Login; "@mdp" = $motDePasse; "@base" = $base } | Out-Null
    # Utilisateur en lecture seule dans chaque base servie.
    foreach ($bd in $bases) {
        Executer @"
DECLARE @l sysname = @login, @sql nvarchar(max);
SET @sql = N'USE ' + QUOTENAME(@base) + N';
IF DATABASE_PRINCIPAL_ID(' + QUOTENAME(@l, '''') + N') IS NULL CREATE USER ' + QUOTENAME(@l) + N' FOR LOGIN ' + QUOTENAME(@l) + N';
ALTER ROLE db_datareader ADD MEMBER ' + QUOTENAME(@l) + N';
DENY INSERT, UPDATE, DELETE, EXECUTE, ALTER TO ' + QUOTENAME(@l) + N';';
EXEC (@sql);
"@ @{ "@login" = $Login; "@base" = $bd } | Out-Null
    }
    Write-Host "Compte SQL $Login prêt : lecture seule sur $($bases -join ', ')." -ForegroundColor Green
}
finally { $cnx.Close() }

# Vérification : le compte lit, et ne peut pas écrire.
$lecture = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$lecture.PSBase.DataSource = $serveur
$lecture.PSBase.InitialCatalog = $base
$lecture.PSBase.UserID = $Login
$lecture.PSBase.Password = $motDePasse
$lecture.PSBase.TrustServerCertificate = $true
$lecture.PSBase.ApplicationName = "Sage100Api tableaux de bord"
foreach ($bd in $bases) {
    $lecture.PSBase.InitialCatalog = $bd
    $test = New-Object System.Data.SqlClient.SqlConnection $lecture.PSBase.ConnectionString
    $test.Open()
    $cmd = $test.CreateCommand()
    $cmd.CommandText = "SELECT COUNT(*) FROM F_COMPTEG"
    Write-Host "  Test de lecture ($bd) : $($cmd.ExecuteScalar()) comptes généraux lus."
    $test.Close()
}
# La chaîne vise la base principale ; l'API la fait pointer vers la base de chaque société.
$lecture.PSBase.InitialCatalog = $base

# Déclaration à l'API (fichiers non versionnés). Rechargés à chaud : pas de redémarrage nécessaire.
foreach ($f in $fichiers) {
    $json = Get-Content $f -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $json.TableauDeBord) { $json | Add-Member -NotePropertyName TableauDeBord -NotePropertyValue ([pscustomobject]@{}) }
    $json.TableauDeBord | Add-Member -NotePropertyName ChaineSql -NotePropertyValue $lecture.PSBase.ConnectionString -Force
    [System.IO.File]::WriteAllText($f, ($json | ConvertTo-Json -Depth 20), (New-Object System.Text.UTF8Encoding $false))
    Write-Host "  TableauDeBord:ChaineSql écrite dans $f"
}
Write-Host "`nTerminé. Les tableaux de bord liront Sage avec $Login à la prochaine actualisation (bouton ↻)." -ForegroundColor Green
