<#
.SYNOPSIS
    Gera dist/SoulstoneCoop-<versao>.zip: BepInEx 6 IL2CPP + o mod, pronto pra extrair na pasta do jogo.

.DESCRIPTION
    Monta o pacote numa pasta de staging (dist/staging) e compacta. Nao compila nada e nao toca
    na pasta do jogo (so le). Veja docs/distribuicao.md.

    - Loader: copiado do BepInEx instalado em -GameDir (winhttp.dll, doorstop_config.ini,
      .doorstop_version, dotnet/, BepInEx/core/).
    - Mod: as DLLs de -PluginSource (padrao: <jogo>\BepInEx\plugins\SoulstoneCoop, o ultimo build
      que o DeployPlugin copiou). Pra empacotar um build feito em outra pasta, passe -PluginSource.
    - Configs: BepInEx.cfg e com.kolber.soulstonecoop.cfg do jogo, com todos os valores voltando pro
      "Default value" de cada entrada (tira o LogLevels = All da depuracao, o LastAddress etc.).
    - LEIA-ME.txt (texto abaixo, neste script).

    Confere as referencias das DLLs do mod (tudo tem que vir do dotnet/, do BepInEx/core, do
    interop gerado ou da propria pasta do plugin) e, no fim, se o zip so tem entradas permitidas.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\package.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\package.ps1 -PluginSource C:\temp\coop-build
#>
[CmdletBinding()]
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Soulstone Survivors',
    [string]$PluginSource = '',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PluginSource) { $PluginSource = Join-Path $GameDir 'BepInEx\plugins\SoulstoneCoop' }
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'dist' }

$PluginGuid = 'com.kolber.soulstonecoop'
$ExpectedGameVersion = '1.5.3.2'
# DLLs do plugin que vao no pacote. Se a fase 7 (Steam) trouxer mais arquivos, inclua aqui.
$PluginFiles = @('SoulstoneCoop.dll', 'LiteNetLib.dll')

function Fail([string]$msg) { Write-Host "ERRO: $msg" -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------- versao

$pluginCs = Join-Path $RepoRoot 'src\SoulstoneCoop\Plugin.cs'
$m = Select-String -Path $pluginCs -Pattern 'PluginVersion\s*=\s*"([^"]+)"' | Select-Object -First 1
if (-not $m) { Fail "PluginVersion nao encontrado em $pluginCs" }
$Version = $m.Matches[0].Groups[1].Value
Write-Host "Versao do mod (Plugin.cs): $Version"

# ---------------------------------------------------------------- conferencias de origem

foreach ($p in @($GameDir, $PluginSource)) { if (-not (Test-Path $p)) { Fail "pasta nao existe: $p" } }
foreach ($f in @('winhttp.dll', 'doorstop_config.ini', 'dotnet\coreclr.dll', 'BepInEx\core\BepInEx.Unity.IL2CPP.dll')) {
    if (-not (Test-Path (Join-Path $GameDir $f))) { Fail "BepInEx incompleto no jogo: falta $f" }
}
foreach ($f in $PluginFiles) {
    if (-not (Test-Path (Join-Path $PluginSource $f))) { Fail "falta $f em $PluginSource" }
}

$buildInfo = Join-Path $GameDir 'build_info.txt'
if ((Test-Path $buildInfo) -and -not ((Get-Content $buildInfo -Raw) -match [regex]::Escape($ExpectedGameVersion))) {
    Write-Warning "build_info.txt do jogo nao fala em $ExpectedGameVersion - o jogo atualizou? Revise o LEIA-ME."
}

# A DLL guarda o PluginVersion no atributo [BepInPlugin] (string serializada: tamanho + UTF-8).
# Se a versao do Plugin.cs nao aparece la, a DLL instalada e de outro build.
$dllBytes = [IO.File]::ReadAllBytes((Join-Path $PluginSource 'SoulstoneCoop.dll'))
$latin1 = [Text.Encoding]::GetEncoding(28591)
if (-not $latin1.GetString($dllBytes).Contains([string][char]$Version.Length + $Version)) {
    Fail "SoulstoneCoop.dll em $PluginSource nao e da versao $Version do Plugin.cs (build velho ou PluginVersion mudou sem rebuild)."
}
$info = (Get-Item (Join-Path $PluginSource 'SoulstoneCoop.dll')).VersionInfo
Write-Host "SoulstoneCoop.dll: $($info.ProductVersion)  ($((Get-Item (Join-Path $PluginSource 'SoulstoneCoop.dll')).LastWriteTime))"

# Referencias: tudo que as DLLs do plugin usam tem que existir no runtime do BepInEx, no core,
# no interop (gerado na 1a execucao a partir do jogo) ou na propria pasta do plugin.
$known = @{}
foreach ($dir in @('dotnet', 'BepInEx\core', 'BepInEx\interop')) {
    $full = Join-Path $GameDir $dir
    if (Test-Path $full) { Get-ChildItem $full -Filter *.dll | ForEach-Object { $known[$_.BaseName] = $dir } }
}
foreach ($f in $PluginFiles) { $known[[IO.Path]::GetFileNameWithoutExtension($f)] = 'plugin' }
$missing = @()
foreach ($f in $PluginFiles) {
    $asm = [Reflection.Assembly]::ReflectionOnlyLoad([IO.File]::ReadAllBytes((Join-Path $PluginSource $f)))
    foreach ($ref in $asm.GetReferencedAssemblies()) {
        if (-not $known.ContainsKey($ref.Name)) { $missing += "$f -> $($ref.Name)" }
    }
}
if ($missing) { Fail ("dependencias que nao vem do jogo/BepInEx e nao estao no pacote:`n  " + ($missing -join "`n  ")) }
Write-Host "Referencias das DLLs do mod: OK"

# ---------------------------------------------------------------- staging

$Stage = Join-Path $OutDir 'staging'
if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
New-Item -ItemType Directory -Force $Stage | Out-Null

foreach ($f in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')) {
    $src = Join-Path $GameDir $f
    if (Test-Path $src) { Copy-Item -LiteralPath $src -Destination $Stage }
}
Copy-Item -LiteralPath (Join-Path $GameDir 'dotnet') -Destination $Stage -Recurse
New-Item -ItemType Directory -Force (Join-Path $Stage 'BepInEx') | Out-Null
Copy-Item -LiteralPath (Join-Path $GameDir 'BepInEx\core') -Destination (Join-Path $Stage 'BepInEx') -Recurse

$plugDst = Join-Path $Stage 'BepInEx\plugins\SoulstoneCoop'
New-Item -ItemType Directory -Force $plugDst | Out-Null
foreach ($f in $PluginFiles) { Copy-Item -LiteralPath (Join-Path $PluginSource $f) -Destination $plugDst }

# Configs: cada "key = valor" volta pro "# Default value:" logo acima dele.
function Reset-CfgToDefaults([string[]]$lines) {
    $pending = $null
    foreach ($line in $lines) {
        if ($line -match '^\s*#\s*Default value:\s?(.*)$') { $pending = $Matches[1]; $line; continue }
        if ($null -ne $pending -and $line -match '^([^#\[][^=]*?)\s*=') {
            "$($Matches[1]) = $pending"; $pending = $null; continue
        }
        if ($line -match '^\s*\[') { $pending = $null }
        $line
    }
}
$cfgDst = Join-Path $Stage 'BepInEx\config'
New-Item -ItemType Directory -Force $cfgDst | Out-Null
$utf8NoBom = New-Object Text.UTF8Encoding($false)
$modCfgDefault = @"
## Settings file was created by plugin Soulstone Coop v$Version
## Plugin GUID: $PluginGuid

[Coop]

## Host address last typed in the Co-op panel (filled in automatically next time).
# Setting type: String
# Default value: 127.0.0.1
LastAddress = 127.0.0.1

[Debug]

## Start hosting automatically when a run begins (instead of pressing Insert).
# Setting type: Boolean
# Default value: false
AutoHost = false
"@
foreach ($name in @('BepInEx.cfg', "$PluginGuid.cfg")) {
    $src = Join-Path $GameDir "BepInEx\config\$name"
    if (Test-Path $src) {
        $lines = Reset-CfgToDefaults ([IO.File]::ReadAllLines($src))
    } elseif ($name -eq 'BepInEx.cfg') {
        Write-Warning "BepInEx.cfg nao existe no jogo; o pacote vai sem ele (o BepInEx cria um padrao)."
        continue
    } else {
        $lines = $modCfgDefault -split "`r?`n"
    }
    [IO.File]::WriteAllText((Join-Path $cfgDst $name), (($lines -join "`r`n") + "`r`n"), $utf8NoBom)
}
$modCfgText = [IO.File]::ReadAllText((Join-Path $cfgDst "$PluginGuid.cfg"))
if ($modCfgText -notmatch '(?m)^AutoHost = false\s*$') { Fail "AutoHost no cfg padrao nao ficou false" }
if ($modCfgText -notmatch '(?m)^LastAddress = (127\.0\.0\.1)?\s*$') { Fail "LastAddress no cfg padrao nao ficou vazio/127.0.0.1" }

# ---------------------------------------------------------------- LEIA-ME

$readme = @"
SOULSTONE COOP $Version - mod de co-op online pra Soulstone Survivors
=====================================================================

PRECISA DE
- Windows e Soulstone Survivors na versão $ExpectedGameVersion (a da Steam hoje).
- Os DOIS jogadores com esta MESMA versão do mod ($Version). Versões diferentes não conectam.
- Internet na primeira vez que abrir o jogo com o mod.

INSTALAR
1. Feche o jogo.
2. Na Steam: botão direito em Soulstone Survivors > Gerenciar > Procurar arquivos locais.
   Abre a pasta do jogo (onde fica o "Soulstone Survivors.exe").
3. Abra este zip (duplo clique), selecione tudo (Ctrl+A) e arraste pra dentro dessa pasta.
   Se perguntar, aceite substituir os arquivos.
   Confira: o winhttp.dll e a pasta BepInEx têm que ficar AO LADO do "Soulstone Survivors.exe",
   e não dentro de uma subpasta "SoulstoneCoop-$Version".
   (Se usar "Extrair tudo", apague o nome da subpasta do caminho que o Windows sugere.)

PRIMEIRA ABERTURA
- Abra o jogo pela Steam normalmente.
- Abre junto uma janela preta de texto (o console do BepInEx). É normal. NÃO feche essa janela:
  fechar ela fecha o jogo.
- Na primeira vez demora ALGUNS MINUTOS até o jogo aparecer (o BepInEx prepara tudo).
  Parece travado, mas não está. Espere. Das próximas vezes abre rápido.
- Se o antivírus reclamar do winhttp.dll: é o carregador do BepInEx, o mod precisa dele.

FUNCIONOU?
- No menu principal aparece uma aba "Co-op". Se apareceu, o mod está carregado.

COMO JOGAR (convite pela Steam, sem IP e sem abrir porta)
- Os dois abrem o jogo PELA STEAM e ficam no menu principal.
- Quem hospeda (o host): aba Co-op > Convidar amigo. Se abrir a janela da Steam, escolha o amigo.
  Se não abrir: na lista de amigos da Steam (fora do jogo), botão direito no amigo >
  "Convidar para o jogo".
- Quem entra: aceita o convite no chat da Steam. O painel Co-op mostra
  "Conectado com <nome> (Steam, x ms)". Se o jogo estiver fechado, aceitar o convite abre o jogo
  e ele entra sozinho ao chegar no menu.
- Se o convite não chegar: o host clica em Copiar código e manda o número no chat; quem entra
  cola no campo do painel Co-op (botão Colar) > Entrar.
- Depois de conectados: cada um escolhe o próprio personagem, e o HOST escolhe o mapa e aperta
  Lutar. A partida de quem entrou começa sozinha, no mesmo mapa.

ALTERNATIVA: POR IP
- Host: aba Co-op > Hospedar por IP (se o Windows perguntar do firewall, "Permitir acesso").
- Quem entra: põe o IP do host no campo > Entrar. Pela internet isso exige uma VPN de jogo
  (Tailscale, ZeroTier ou Radmin VPN, usando o IP da VPN) ou a porta 7777 UDP aberta no roteador
  do host.

DESINSTALAR
- Na pasta do jogo, apague: winhttp.dll, doorstop_config.ini, .doorstop_version, a pasta dotnet,
  a pasta BepInEx, este LEIA-ME.txt, o README.txt, o SoulstoneCoop-LICENSE.txt e o
  THIRD-PARTY-NOTICES.txt. Só isso: o jogo volta ao normal.
- Obs.: o "Verificar integridade dos arquivos" da Steam conserta arquivos do jogo, mas NÃO apaga
  os do mod. Pra tirar o mod, apague os arquivos acima.

DEU PROBLEMA?
- Mande o arquivo BepInEx\LogOutput.log (dentro da pasta do jogo) logo depois do problema,
  sem abrir o jogo de novo antes (ele é reescrito toda vez que o jogo abre).
"@
[IO.File]::WriteAllText((Join-Path $Stage 'LEIA-ME.txt'), ($readme -replace "`r?`n", "`r`n"), (New-Object Text.UTF8Encoding($true)))

$readmeEn = @"
SOULSTONE COOP $Version - online co-op mod for Soulstone Survivors
=====================================================================

REQUIREMENTS
- Windows and Soulstone Survivors $ExpectedGameVersion (the current Steam version).
- BOTH players on this SAME mod version ($Version). Different versions refuse to connect.
- Internet the first time you open the game with the mod.

INSTALL
1. Close the game.
2. In Steam: right-click Soulstone Survivors > Manage > Browse local files.
   This opens the game folder (the one with "Soulstone Survivors.exe").
3. Open this zip, select everything (Ctrl+A) and drag it into that folder. Accept replacing files.
   Check: winhttp.dll and the BepInEx folder must end up NEXT TO "Soulstone Survivors.exe",
   not inside a "SoulstoneCoop-$Version" subfolder.

FIRST LAUNCH
- Launch the game from Steam as usual.
- A black text window (the BepInEx console) opens too. That's normal. DON'T close it: closing it
  closes the game.
- The first time takes A FEW MINUTES before the game shows up (BepInEx prepares everything).
  It looks frozen, it isn't. Wait. Next launches are fast.
- If your antivirus complains about winhttp.dll: it's the BepInEx loader, the mod needs it.

DID IT WORK?
- A "Co-op" tab shows up in the main menu. If it's there, the mod is loaded.

HOW TO PLAY (Steam invite - no IP, no port forwarding)
- Both players launch the game FROM STEAM and stay in the main menu.
- Host: Co-op tab > Invite friend. If the Steam overlay opens, pick your friend.
  If it doesn't: in the Steam friends list (outside the game), right-click the friend >
  "Invite to game".
- Guest: accept the invite in Steam chat. The Co-op panel shows "Connected with <name> (Steam, x ms)".
  If the game is closed, accepting the invite launches it and joins once it reaches the menu.
- If the invite doesn't arrive: the host clicks Copy code and sends the number in chat; the guest
  pastes it into the Co-op panel field (Paste button) > Join.
- Once connected: each player picks their own character, and the HOST picks the map and presses
  Fight. The guest's run starts by itself on the same map.

ALTERNATIVE: BY IP
- Host: Co-op tab > Host by IP (allow it if Windows asks about the firewall).
- Guest: type the host's IP > Join. Over the internet this needs a gaming VPN (Tailscale, ZeroTier,
  Radmin VPN - use the VPN IP) or UDP port 7777 forwarded on the host's router.

UNINSTALL
- In the game folder, delete: winhttp.dll, doorstop_config.ini, .doorstop_version, the dotnet folder,
  the BepInEx folder, LEIA-ME.txt, README.txt, SoulstoneCoop-LICENSE.txt and THIRD-PARTY-NOTICES.txt.
  That's it.
- Note: Steam's "Verify integrity of game files" does NOT remove the mod's files.

SOMETHING WENT WRONG?
- Send BepInEx\LogOutput.log (inside the game folder) right after the problem, before opening the
  game again (it's rewritten every launch).
"@
[IO.File]::WriteAllText((Join-Path $Stage 'README.txt'), ($readmeEn -replace "`r?`n", "`r`n"), (New-Object Text.UTF8Encoding($true)))

# The mod's MIT license and the notices of the bundled third-party software (BepInEx/Il2CppInterop are LGPL).
foreach ($pair in @(@('LICENSE', 'SoulstoneCoop-LICENSE.txt'), @('THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-NOTICES.txt'))) {
    $src = Join-Path $RepoRoot $pair[0]
    if (-not (Test-Path $src)) { Fail "falta $($pair[0]) na raiz do repo" }
    Copy-Item -LiteralPath $src -Destination (Join-Path $Stage $pair[1])
}

# ---------------------------------------------------------------- zip

$zipPath = Join-Path $OutDir "SoulstoneCoop-$Version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$stageFull = (Resolve-Path $Stage).Path.TrimEnd('\') + '\'
$zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    foreach ($file in Get-ChildItem $Stage -Recurse -File -Force) {
        $entry = $file.FullName.Substring($stageFull.Length).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry, 'Optimal') | Out-Null
    }
} finally { $zip.Dispose() }

# ---------------------------------------------------------------- conferencia do zip

# Lista branca: qualquer coisa fora disso no zip e erro (interop, unity-libs, cache, logs,
# steam_appid.txt, arquivos do jogo, outros plugins como o UnityExplorer...).
$allowed = @(
    '^winhttp\.dll$', '^doorstop_config\.ini$', '^\.doorstop_version$', '^LEIA-ME\.txt$', '^README\.txt$', '^SoulstoneCoop-LICENSE\.txt$', '^THIRD-PARTY-NOTICES\.txt$',
    '^dotnet/', '^BepInEx/core/',
    ('^BepInEx/plugins/SoulstoneCoop/(' + (($PluginFiles | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')$'),
    '^BepInEx/config/BepInEx\.cfg$', ('^BepInEx/config/' + [regex]::Escape("$PluginGuid.cfg") + '$')
)
$forbidden = '(?i)(^BepInEx/(interop|unity-libs|cache|patchers)/|\.log$|steam_appid\.txt$|GameAssembly\.dll$|_Data/|\.exe$)'
$zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entries = @($zip.Entries | ForEach-Object { $_.FullName })
    $bad = @($entries | Where-Object { $e = $_; ($e -match $forbidden) -or -not ($allowed | Where-Object { $e -match $_ }) })
    $total = ($zip.Entries | Measure-Object Length -Sum).Sum
} finally { $zip.Dispose() }
if ($bad) { Remove-Item $zipPath -Force; Fail ("entradas proibidas no zip (zip apagado):`n  " + ($bad -join "`n  ")) }
foreach ($need in @('winhttp.dll', 'doorstop_config.ini', 'BepInEx/core/BepInEx.Unity.IL2CPP.dll', 'dotnet/coreclr.dll', 'LEIA-ME.txt', 'README.txt', 'SoulstoneCoop-LICENSE.txt', 'THIRD-PARTY-NOTICES.txt') +
                  @($PluginFiles | ForEach-Object { "BepInEx/plugins/SoulstoneCoop/$_" })) {
    if ($entries -notcontains $need) { Fail "falta $need no zip" }
}

Remove-Item $Stage -Recurse -Force
$size = (Get-Item $zipPath).Length
Write-Host ""
Write-Host ("OK: {0}" -f $zipPath) -ForegroundColor Green
Write-Host ("    {0} arquivos, {1:N1} MB compactado ({2:N1} MB extraido)" -f $entries.Count, ($size / 1MB), ($total / 1MB))
$entries | Where-Object { $_ -notmatch '^(dotnet|BepInEx/core)/' } | ForEach-Object { "    $_" }
"    dotnet/        {0} arquivos" -f @($entries | Where-Object { $_ -like 'dotnet/*' }).Count
"    BepInEx/core/  {0} arquivos" -f @($entries | Where-Object { $_ -like 'BepInEx/core/*' }).Count
