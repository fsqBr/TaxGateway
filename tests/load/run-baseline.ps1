<#
.SYNOPSIS
  Mede latência e vazão do motor regular isolado para vários tamanhos de documento (SDD §16.4).
  Requer: docker compose up -d rtc-regular
#>
param(
    [int[]]$Items = @(1, 20, 100, 500),
    [int]$Vus = 8,
    [string]$Duration = '30s'
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
foreach ($n in $Items) {
    Write-Host "==> ITEMS=$n VUS=$Vus"
    docker run --rm --network taxgateway_engines -v "${here}:/scripts:ro" `
        -e ITEMS=$n -e VUS=$Vus -e DURATION=$Duration `
        grafana/k6:latest run --quiet /scripts/rtc-baseline.js 2>&1 |
        Select-String -Pattern 'http_req_duration\{phase:measure\}|http_reqs\.' -Context 0,1
}
