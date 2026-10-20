# Генерация самоподписанного TLS-сертификата для WEB proxy bridge и экспорт в PFX.
# Работает БЕЗ прав администратора: сертификат кладётся в хранилище ТЕКУЩЕГО пользователя,
# Kestrel читает только экспортированный PFX-файл с диска, поэтому LocalMachine не нужен.
#
# Примеры:
#   # Обычный домен
#   .\tools\scripts\cert.ps1 -DnsName bridge.example.com -Password MyPfxPassword
#
#   # Локальная проверка без домена: SAN lvh.me (резолвится в 127.0.0.1)
#   #   + доверие сертификату в «Доверенных корневых ЦС» текущего пользователя,
#   #     чтобы браузер (Chrome/Edge) открывал https://lvh.me/ без предупреждений.
#   .\tools\scripts\cert.ps1 -Local
#
#   # Убрать локальное доверие
#   .\tools\scripts\cert.ps1 -Remove <thumbprint>
#
# Для смоук-теста доверие НЕ требуется: tools/web-proxy-smoke.mjs запускается с --insecure
# или с --ca <файл>.pem (проверка TLS остаётся включённой; .pem пишется вместе с PFX).

param(
    [string[]]$DnsName = @("bridge.example.com"),
    [string]$Password = "MyPfxPassword",
    [string]$OutDir  = "certs",
    [string]$PfxName = "bridge.pfx",

    # Локальный режим: SAN lvh.me + доверие в Cert:\CurrentUser\Root.
    [switch]$Local,

    # Доверить выпущенный сертификат в хранилище корневых ЦС текущего пользователя.
    [switch]$Trust,

    # Удалить сертификат с указанным отпечатком из My и Root текущего пользователя.
    [string]$Remove
)

$ErrorActionPreference = "Stop"

function Remove-LocalCert([string]$Thumbprint) {
    $clean = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $removed = 0
    foreach ($store in @('Cert:\CurrentUser\My', 'Cert:\CurrentUser\Root')) {
        Get-ChildItem $store | Where-Object { $_.Thumbprint -eq $clean } | ForEach-Object {
            Remove-Item -Path $_.PSPath -Force
            $removed++
            Write-Host "Удалён из $store : $clean" -ForegroundColor Yellow
        }
    }
    if ($removed -eq 0) { Write-Host "Сертификат $clean не найден в хранилищах текущего пользователя." }
}

if ($Remove) {
    Remove-LocalCert $Remove
    return
}

if ($Local) {
    $DnsName = @('lvh.me', '*.lvh.me')
    $Trust = $true
}

# 1. Папка для PFX (относительный путь — относительно папки, откуда запущен скрипт)
if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir | Out-Null
}
$outFull = (Resolve-Path $OutDir).Path
$pfxPath = Join-Path $outFull $PfxName
$baseName = [System.IO.Path]::GetFileNameWithoutExtension($PfxName)
$cerPath = Join-Path $outFull ($baseName + ".cer")
$pemPath = Join-Path $outFull ($baseName + ".pem")

# 2. Самоподписанный сертификат в хранилище текущего пользователя (не требует админа)
$cert = New-SelfSignedCertificate `
    -DnsName $DnsName `
    -CertStoreLocation Cert:\CurrentUser\My `
    -KeyExportPolicy Exportable `
    -Type SSLServerAuthentication `
    -NotAfter (Get-Date).AddYears(1)

# 3. Экспорт в PFX с паролем
$pass = ConvertTo-SecureString $Password -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pass | Out-Null

# 4. Экспорт публичной части в PEM (для смоука с --ca: проверка TLS остаётся включённой).
#    ВАЖНО: .pem всегда пишется ЗАНОВО вместе с PFX, чтобы не рассинхронизироваться
#    с ключом (иначе --ca не совпадёт с предъявляемым сертификатом → «self-signed certificate»).
$der = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
$b64 = [Convert]::ToBase64String($der, [System.Base64FormattingOptions]::InsertLineBreaks)
$pem = "-----BEGIN CERTIFICATE-----`n" + $b64 + "`n-----END CERTIFICATE-----`n"
[System.IO.File]::WriteAllText($pemPath, $pem, [System.Text.UTF8Encoding]::new($false))

# 5. Опционально: доверие (публичная часть) в корневые ЦС текущего пользователя
if ($Trust) {
    Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null
    Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
}

Write-Host ""
Write-Host "Готово:" -ForegroundColor Green
Write-Host "  PFX: $pfxPath"
Write-Host "  PEM: $pemPath"
Write-Host "  SAN: $($DnsName -join ', ')"
Write-Host "  Thumbprint: $($cert.Thumbprint)"
if ($Trust) {
    Write-Host "  Доверие: установлено в Cert:\CurrentUser\Root (только для текущего пользователя)"
    Write-Host "  Снять доверие: .\tools\scripts\cert.ps1 -Remove $($cert.Thumbprint)"
}
Write-Host ""
Write-Host "В config.json укажи:"
Write-Host "  webProxyCertPath     = $pfxPath"
Write-Host "  webProxyCertPassword = $Password"
if ($Local) {
    Write-Host "  webProxyPublicHost   = lvh.me      (домен, а не IP: Telegram не принимает IP)"
    Write-Host ""
    Write-Host "Локальная проверка (сервер и клиент на одной машине):" -ForegroundColor Cyan
    Write-Host "  dotnet run --project src/TgWsProxy -- --config config.json"
    Write-Host "  node tools/web-proxy-smoke.mjs --host lvh.me --secret <32hex> --carrier websocket --ca $pemPath"
    Write-Host "  браузер: https://lvh.me/?bridge=<capability>"
} else {
    Write-Host ""
    Write-Host "Для реального клиента (браузер) сертификат нужно доверить:"
    Write-Host "  перезапусти скрипт с -Trust, либо используй сертификат от доверенного ЦС."
    Write-Host "  Для смоук-теста доверие не нужно: tools/web-proxy-smoke.mjs --insecure"
}
