<#
.SYNOPSIS
  Constrói as imagens dos motores da RFB a partir de infra/rfb/versions.json e confere que
  os dois motores do bundle usam o mesmo banco (db_sha256).
.EXAMPLE
  ./infra/rfb/build.ps1            # regular + simples
  ./infra/rfb/build.ps1 -Engine regular
#>
param(
    [ValidateSet('all', 'regular', 'simples')]
    [string]$Engine = 'all'
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$v = Get-Content (Join-Path $here 'versions.json') -Raw | ConvertFrom-Json
$env:DOCKER_BUILDKIT = '1'

# Banco SQLite gerado uma vez (stage db): o sha256 vira a label rtc.db_sha256 das imagens (D-009).
$dbImage = docker build $here -q --file (Join-Path $here 'Dockerfile') --target db `
    --build-arg "REGULAR_TAG=$($v.regular.tag)" --build-arg "REGULAR_SHA=$($v.regular.commit)"
if ($LASTEXITCODE -ne 0) { throw 'Falha no build do banco (stage db)' }
$dbSha = (docker run --rm --entrypoint cat $dbImage /db.sha256).Trim()
Write-Host "db_sha256 = $dbSha"

$engines = if ($Engine -eq 'all') { @('regular', 'simples') } else { @($Engine) }
foreach ($name in $engines) {
    $e = $v.$name
    $image = "taxgateway/rtc-${name}:$($e.tag)"
    Write-Host "==> Construindo $image (bundle $($v.bundle))"
    docker build $here `
        --file (Join-Path $here 'Dockerfile') `
        --build-arg "REGULAR_TAG=$($v.regular.tag)" `
        --build-arg "REGULAR_SHA=$($v.regular.commit)" `
        --build-arg "ENGINE_REPO=$($e.repo)" `
        --build-arg "ENGINE_TAG=$($e.tag)" `
        --build-arg "ENGINE_SHA=$($e.commit)" `
        --build-arg "ENGINE_JAR=$($e.jar)" `
        --build-arg "BUNDLE=$($v.bundle)" `
        --build-arg "DB_SHA256=$dbSha" `
        --tag $image `
        --tag "taxgateway/rtc-${name}:bundle-$($v.bundle)"
    if ($LASTEXITCODE -ne 0) { throw "Falha no build de $image" }
}

if ($Engine -eq 'all') {
    $shas = $engines | ForEach-Object {
        docker run --rm --entrypoint cat "taxgateway/rtc-${_}:$($v.$_.tag)" /opt/rtc/db.sha256
    }
    if (($shas | Select-Object -Unique).Count -ne 1) { throw "db_sha256 diverge entre os motores: $shas" }
    Write-Host "OK: db_sha256 = $($shas[0])"
}
