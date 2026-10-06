; Installateur de production de l'API Sage 100 (Inno Setup 6 ou 7).
; Ne pas compiler à la main : lancer deploy\fabriquer-installateur.ps1, qui prépare le paquet puis appelle ISCC.exe.
;
; Setup.exe copie l'API (autonome, sans runtime .NET à installer) et le worker déjà compilés, puis lance
; outils\configurer.ps1 : services Windows, configuration, pare-feu, certificat HTTPS, compte SQL lecture seule.
; Mise à jour : relancer un Setup.exe plus récent ; la configuration du serveur est gardée.

#ifndef Version
  #define Version "1.0.0"
#endif
#ifndef Paquet
  #define Paquet "..\sortie\paquet"
#endif
#ifndef Sortie
  #define Sortie "..\sortie"
#endif

[Setup]
AppId={{8C6F2E4A-5B1D-4E7A-9C3F-2A1B6D7E8F90}
AppName=Sage 100 API
AppVersion={#Version}
AppVerName=Sage 100 API {#Version}
DefaultDirName=C:\Sage100Api
UsePreviousAppDir=yes
DisableDirPage=auto
DisableProgramGroupPage=yes
DisableReadyPage=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#Sortie}
OutputBaseFilename=Sage100Api-Setup-{#Version}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Sage 100 API
SetupLogging=yes
CloseApplications=no

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[Files]
; Configuration du serveur jamais écrasée : elle est écrite par configurer.ps1, pas livrée.
Source: "{#Paquet}\api\*"; DestDir: "{app}\api"; Excludes: "appsettings.Local.json,appsettings.Https.json,*.db,*.db-shm,*.db-wal"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Paquet}\worker\*"; DestDir: "{app}\worker"; Excludes: "worker.json,logs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Paquet}\worker\worker.json"; DestDir: "{app}\worker"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#Paquet}\outils\*"; DestDir: "{app}\outils"; Flags: ignoreversion

[Run]
Filename: "{app}\LISEZMOI-installation.txt"; Description: "Afficher le récapitulatif (adresses des applications, clé d'API)"; Flags: postinstall shellexec skipifsilent nowait; Check: RecapitulatifPresent

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\outils\configurer.ps1"" -Dossier ""{app}"" -Desinstaller"; RunOnceId: "Desinstaller"; Flags: runhidden waituntilterminated

[Code]
var
  PageSage, PageCompte, PagePorts: TInputQueryWizardPage;
  PageOptions: TInputOptionWizardPage;

function PowerShell: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

{ Serveur déjà configuré (par une installation précédente ou par deploy\installer.ps1) : mise à jour, réglages gardés. }
function EstMiseAJour: Boolean;
begin
  Result := FileExists(AddBackslash(WizardDirValue) + 'api\appsettings.Local.json')
    and FileExists(AddBackslash(WizardDirValue) + 'worker\worker.json');
end;

function RecapitulatifPresent: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\LISEZMOI-installation.txt'));
end;

function InitializeSetup: Boolean;
var
  Release: Cardinal;
begin
  Result := True;
  { Le worker Objets Métiers exige le .NET Framework 4.8 (présent d'office sur Windows 10, 11 et Server 2019+). }
  Release := 0;
  RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release);
  if Release < 528040 then
  begin
    MsgBox('Le .NET Framework 4.8 est nécessaire au worker Objets Métiers. Installez-le (Windows Update ou site de Microsoft), puis relancez l''installation.', mbCriticalError, MB_OK);
    Result := False;
  end;
end;

procedure InitializeWizard;
begin
  PageSage := CreateInputQueryPage(wpSelectDir, 'Base Sage 100',
    'Connexion à la base Sage, pour les lectures SQL et pour les Objets Métiers',
    'Les Objets Métiers Sage 100 V12 doivent déjà être installés sur ce serveur. Mot de passe Sage : vide si l''utilisateur n''en a pas.');
  PageSage.Add('Serveur SQL (nom ou nom\instance) :', False);
  PageSage.Add('Base Gestion commerciale (plusieurs sociétés : séparées par des virgules) :', False);
  PageSage.Add('Base Comptabilité (vide = la même ; plusieurs : même ordre) :', False);
  PageSage.Add('Utilisateur Sage :', False);
  PageSage.Add('Mot de passe Sage :', True);
  PageSage.Values[0] := GetComputerNameString;
  PageSage.Values[3] := '<Administrateur>';

  PageCompte := CreateInputQueryPage(PageSage.ID, 'Compte des services',
    'Compte Windows sous lequel tournent l''API et le worker',
    'Ce compte doit avoir accès à SQL Server (sécurité intégrée) et à Sage. Il lui faut un mot de passe : Windows ne lance pas un service sous un compte sans mot de passe (un code PIN ne suffit pas).');
  PageCompte.Add('Compte (PC\nom ou DOMAINE\nom) :', False);
  PageCompte.Add('Mot de passe du compte :', True);
  PageCompte.Values[0] := GetComputerNameString + '\' + GetUserNameString;

  PagePorts := CreateInputQueryPage(PageCompte.ID, 'Réseau', 'Ports d''écoute de l''API',
    'Le pare-feu Windows sera ouvert sur ces deux ports.');
  PagePorts.Add('Port HTTP :', False);
  PagePorts.Add('Port HTTPS (tablettes) :', False);
  PagePorts.Values[0] := '5080';
  PagePorts.Values[1] := '5443';

  PageOptions := CreateInputOptionPage(PagePorts.ID, 'Options', 'Sécurité',
    'Cochez ce qui doit être mis en place.', False, False);
  PageOptions.Add('Créer le certificat HTTPS des tablettes (nécessaire à la borne hors ligne)');
  PageOptions.Add('Créer un compte SQL en lecture seule pour les tableaux de bord (si SQL Server l''autorise)');
  PageOptions.Values[0] := True;
  PageOptions.Values[1] := True;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := EstMiseAJour and ((PageID = PageSage.ID) or (PageID = PageCompte.ID) or (PageID = PagePorts.ID) or (PageID = PageOptions.ID));
end;

function PortValide(Texte: String): Boolean;
var
  N: Integer;
begin
  N := StrToIntDef(Trim(Texte), 0);
  Result := (N > 0) and (N < 65536);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = PageSage.ID) and ((Trim(PageSage.Values[0]) = '') or (Trim(PageSage.Values[1]) = '') or (Trim(PageSage.Values[3]) = '')) then
  begin
    MsgBox('Indiquez le serveur SQL, la base Gestion commerciale et l''utilisateur Sage.', mbError, MB_OK);
    Result := False;
  end
  else if (CurPageID = PageCompte.ID) and ((Pos('\', PageCompte.Values[0]) = 0) or (PageCompte.Values[1] = '')) then
  begin
    MsgBox('Indiquez le compte sous la forme PC\nom ou DOMAINE\nom, avec son mot de passe.', mbError, MB_OK);
    Result := False;
  end
  else if (CurPageID = PagePorts.ID) and (not PortValide(PagePorts.Values[0]) or not PortValide(PagePorts.Values[1])) then
  begin
    MsgBox('Les ports doivent être des nombres entre 1 et 65535.', mbError, MB_OK);
    Result := False;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine;
  if EstMiseAJour then
    Result := Result + 'Mise à jour : la configuration actuelle du serveur est gardée.' + NewLine + 'Les services sont arrêtés pendant la copie, puis redémarrés.'
  else
    Result := Result + 'Base(s) Sage : ' + PageSage.Values[1] + ' sur ' + PageSage.Values[0] + NewLine
      + 'Utilisateur Sage : ' + PageSage.Values[3] + NewLine
      + 'Compte des services : ' + PageCompte.Values[0] + NewLine
      + 'Ports : ' + Trim(PagePorts.Values[0]) + ' (HTTP), ' + Trim(PagePorts.Values[1]) + ' (HTTPS)';
end;

{ Les services tournent pendant une mise à jour : on les arrête avant de remplacer leurs fichiers. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  Exec(PowerShell, '-NoProfile -Command "Stop-Service Sage100Api, Sage100Api.Worker -Force -ErrorAction SilentlyContinue"', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function TexteJson(Texte: String): String;
begin
  Result := Texte;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
  Result := '"' + Result + '"';
end;

function Booleen(Valeur: Boolean): String;
begin
  if Valeur then Result := 'true' else Result := 'false';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Fichier, Parametres, Script: String;
  Lignes: TArrayOfString;
begin
  if CurStep <> ssPostInstall then Exit;
  Script := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\outils\configurer.ps1') + '" -Dossier "' + ExpandConstant('{app}') + '"';
  WizardForm.StatusLabel.Caption := 'Configuration du serveur (services, pare-feu, certificats)...';
  if EstMiseAJour then
    Exec(PowerShell, Script + ' -MiseAJour', '', SW_HIDE, ewWaitUntilTerminated, Code)
  else
  begin
    { Paramètres passés par un fichier temporaire (effacé tout de suite) : les mots de passe ne passent pas sur la ligne de commande. }
    Fichier := ExpandConstant('{tmp}\parametres.json');
    Parametres := '{' +
      '"serveurSql":' + TexteJson(Trim(PageSage.Values[0])) + ',' +
      '"baseCial":' + TexteJson(Trim(PageSage.Values[1])) + ',' +
      '"baseCpta":' + TexteJson(Trim(PageSage.Values[2])) + ',' +
      '"utilisateurSage":' + TexteJson(Trim(PageSage.Values[3])) + ',' +
      '"motDePasseSage":' + TexteJson(PageSage.Values[4]) + ',' +
      '"compteService":' + TexteJson(Trim(PageCompte.Values[0])) + ',' +
      '"motDePasseService":' + TexteJson(PageCompte.Values[1]) + ',' +
      '"portHttp":' + IntToStr(StrToIntDef(Trim(PagePorts.Values[0]), 5080)) + ',' +
      '"portHttps":' + IntToStr(StrToIntDef(Trim(PagePorts.Values[1]), 5443)) + ',' +
      '"https":' + Booleen(PageOptions.Values[0]) + ',' +
      '"compteLecture":' + Booleen(PageOptions.Values[1]) + '}';
    SetArrayLength(Lignes, 1);
    Lignes[0] := Parametres;
    SaveStringsToUTF8File(Fichier, Lignes, False);
    Exec(PowerShell, Script + ' -Parametres "' + Fichier + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
    DeleteFile(Fichier);
  end;
  if Code <> 0 then
    MsgBox('La configuration du serveur a rencontré une erreur. Détail dans ' + ExpandConstant('{app}\installation.log') + '.', mbError, MB_OK);
end;
