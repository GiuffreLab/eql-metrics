<#
.SYNOPSIS
  Scrapes the EverQuest Legends wiki (eqlwiki.com) for spell data and writes spells.json.

.DESCRIPTION
  Walks Category:Spells via the MediaWiki API, reads each page's {{Spellpage|...}} template,
  and extracts name, duration, target/spell type, mana, and the cast-on-you / cast-on-other /
  wears-off message strings. EQL Metrics loads the resulting spells.json to drive self-buff
  apply/fade detection with accurate durations. Re-run it whenever the game adds/updates spells.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scrape-spells.ps1
  # writes to %APPDATA%\EqlMetrics\spells.json (where the overlay looks for it)

.EXAMPLE
  .\scrape-spells.ps1 -OutFile .\spells.json      # write a local copy instead
#>
[CmdletBinding()]
param(
  [string]$Api     = "https://eqlwiki.com/api.php",
  [string]$Category= "Category:Spells",
  [string]$OutFile = "$env:APPDATA\EqlMetrics\spells.json",
  [int]$DelayMs    = 150
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$headers = @{ "User-Agent" = "eql-metrics-spell-scraper/1.0 (personal EQ overlay; contact: local user)" }

function Get-Field([string]$text, [string]$name) {
  # NB: use [ \t]* (not \s*) around the value so an EMPTY field doesn't let the
  # matcher cross the newline and capture the next "| param =" line.
  $m = [regex]::Match($text, "(?m)^[ \t]*\|[ \t]*$([regex]::Escape($name))[ \t]*=[ \t]*(.*?)[ \t]*$")
  if ($m.Success) {
    $v = $m.Groups[1].Value.Trim()
    if ($v.StartsWith('|')) { return "" }   # guard against any stray template artifact
    return $v
  }
  return ""
}

function Convert-Duration([string]$d) {
  if ([string]::IsNullOrWhiteSpace($d)) { return 0 }
  if ($d -match '(?i)instant|permanent|until|charge') { return 0 }
  # take the first value of a level-scaled range ("... to ...")
  $d = ($d -split '(?i)\bto\b')[0]
  # MM:SS form (e.g. "0:06", "30:00 minutes")
  if ($d -match '(\d+):(\d{2})') { return [int]$Matches[1]*60 + [int]$Matches[2] }
  $sec = 0.0; $hit = $false
  if ($d -match '(\d+(?:\.\d+)?)\s*(?:hours?|hrs?|hr)\b')     { $sec += [double]$Matches[1]*3600; $hit=$true }
  if ($d -match '(\d+(?:\.\d+)?)\s*(?:minutes?|mins?|min)\b') { $sec += [double]$Matches[1]*60;   $hit=$true }
  if ($d -match '(\d+(?:\.\d+)?)\s*(?:seconds?|secs?|sec)\b') { $sec += [double]$Matches[1];       $hit=$true }
  if ($d -match '(\d+)\s*ticks?\b')                          { $sec += [int]$Matches[1]*6;        $hit=$true }
  if (-not $hit) { return 0 }
  return [int][math]::Round($sec)
}

# ---- 1. enumerate spell pages (paginated) ----
Write-Host "Enumerating $Category ..."
$titles = New-Object System.Collections.Generic.List[string]
$cont = $null
do {
  $u = "$Api`?action=query&list=categorymembers&cmtitle=$([uri]::EscapeDataString($Category))&cmlimit=500&cmtype=page&format=json&formatversion=2"
  if ($cont) { $u += "&cmcontinue=$([uri]::EscapeDataString($cont))" }
  $r = Invoke-RestMethod -Uri $u -Headers $headers
  foreach ($m in $r.query.categorymembers) { $titles.Add($m.title) }
  $cont = $r.continue.cmcontinue
  Start-Sleep -Milliseconds $DelayMs
} while ($cont)
Write-Host "Found $($titles.Count) spell pages."

# ---- 2. fetch + parse each page ----
$spells = New-Object System.Collections.Generic.List[object]
$i = 0
foreach ($t in $titles) {
  $i++
  try {
    $u = "$Api`?action=parse&page=$([uri]::EscapeDataString($t))&prop=wikitext&format=json&formatversion=2"
    $r = Invoke-RestMethod -Uri $u -Headers $headers
    $wt = [string]$r.parse.wikitext
    if ($wt -notmatch 'Spellpage') { continue }
    $dur = Get-Field $wt 'duration'
    $name = Get-Field $wt 'spellname'; if (-not $name) { $name = $t }
    $spells.Add([pscustomobject][ordered]@{
      spell         = $name
      duration_text = $dur
      duration_sec  = (Convert-Duration $dur)
      target_type   = (Get-Field $wt 'target_type')
      spell_type    = (Get-Field $wt 'spell_type')
      mana          = (Get-Field $wt 'mana')
      cast_on_you   = (Get-Field $wt 'msg_cast_on_you')
      cast_on_other = (Get-Field $wt 'msg_cast_on_other')
      wears_off     = (Get-Field $wt 'msg_wears_off')
    })
  } catch {
    Write-Warning "skip '$t': $($_.Exception.Message)"
  }
  if ($i % 50 -eq 0) { Write-Host "  $i / $($titles.Count)" }
  Start-Sleep -Milliseconds $DelayMs
}

# ---- 3. write json ----
$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
# write UTF-8 without BOM so any JSON reader is happy
$json = ($spells | Sort-Object spell | ConvertTo-Json -Depth 4)
[System.IO.File]::WriteAllText($OutFile, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote $($spells.Count) spells to $OutFile"
