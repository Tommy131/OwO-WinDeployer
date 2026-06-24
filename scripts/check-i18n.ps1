# Verifies the i18n translation dictionaries are consistent:
#  - en / zh / de have IDENTICAL key sets (no missing translations)
#  - no key is defined twice across an area's files within one language (last-writer-wins hazard)
#  - placeholder index sets ({0}{1}…) match across the three languages for each key
#  - catalog/i18n/{en,de}.json cover the same software ids
# Exit code 0 = OK, 1 = problems found. Run from the repo root: pwsh scripts/check-i18n.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$base = Join-Path $root 'src/WinDeploy.Core/I18n/Resources'
$langs = 'en', 'zh', 'de'
$problems = 0

function Read-Lang($lang) {
  $map = @{}
  Get-ChildItem (Join-Path $base $lang) -Filter *.json | ForEach-Object {
    $j = Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($p in $j.PSObject.Properties) {
      if ($map.ContainsKey($p.Name)) { Write-Host "DUP [$lang] '$($p.Name)' in $($_.Name)" -ForegroundColor Red; $script:problems++ }
      else { $map[$p.Name] = $p.Value }
    }
  }
  $map
}

$maps = @{}; foreach ($l in $langs) { $maps[$l] = Read-Lang $l }
$en = $maps['en']
Write-Host ("Key counts: " + (($langs | ForEach-Object { "$_=$($maps[$_].Count)" }) -join '  '))

# Parity: every language must have exactly en's key set.
foreach ($l in $langs) {
  foreach ($k in $en.Keys) { if (-not $maps[$l].ContainsKey($k)) { Write-Host "MISSING [$l] $k" -ForegroundColor Red; $problems++ } }
  foreach ($k in $maps[$l].Keys) { if (-not $en.ContainsKey($k)) { Write-Host "EXTRA [$l] $k (not in en)" -ForegroundColor Red; $problems++ } }
}

# Placeholder parity: {0}{1}… index set must match across languages.
function Ph($s) { ([regex]::Matches([string]$s, '\{(\d+)')) | ForEach-Object { [int]$_.Groups[1].Value } | Sort-Object -Unique }
foreach ($k in $en.Keys) {
  $ref = (Ph $en[$k]) -join ','
  foreach ($l in 'zh', 'de') {
    if ($maps[$l].ContainsKey($k)) {
      $cur = (Ph $maps[$l][$k]) -join ','
      if ($cur -ne $ref) { Write-Host "PLACEHOLDER MISMATCH '$k' en={$ref} $l={$cur}" -ForegroundColor Yellow; $problems++ }
    }
  }
}

# Catalog summary sidecars must cover the same ids.
$catEn = (Get-Content (Join-Path $root 'catalog/i18n/en.json') -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties.Name
$catDe = (Get-Content (Join-Path $root 'catalog/i18n/de.json') -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties.Name
$ceS = [System.Collections.Generic.HashSet[string]]::new([string[]]$catEn)
foreach ($id in $catDe) { if (-not $ceS.Contains($id)) { Write-Host "catalog de id not in en: $id" -ForegroundColor Red; $problems++ } }
foreach ($id in $catEn) { if ($catDe -notcontains $id) { Write-Host "catalog en id not in de: $id" -ForegroundColor Red; $problems++ } }
Write-Host ("catalog i18n: en=$($catEn.Count) de=$($catDe.Count)")

if ($problems -eq 0) { Write-Host "i18n OK: all checks passed." -ForegroundColor Green; exit 0 }
else { Write-Host "i18n: $problems problem(s) found." -ForegroundColor Red; exit 1 }
