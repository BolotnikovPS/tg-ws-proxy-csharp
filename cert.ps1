# Генерация самоподписанного TLS-сертификата для WEB proxy bridge и экспорт в PFX.
# Работает БЕЗ прав администратора: сертификат кладётся в хранилище текущего пользователя,
# Kestrel читает только экспортированный PFX-файл с диска, поэтому LocalMachine не нужен.

param(
    [string]$DnsName = "bridge.example.com",
    [string]$Password = "MyPfxPassword",
    [string]$OutDir  = "certs"
)

$ErrorActionPreference = "Stop"

# 1. Папка для PFX (относительный путь — относительно папки, откуда запущен скрипт)
if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir | Out-Null
}
$pfxPath = Join-Path (Resolve-Path $OutDir) "bridge.pfx"

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

Write-Host ""
Write-Host "Готово:" -ForegroundColor Green
Write-Host "  PFX: $pfxPath"
Write-Host "  Thumbprint: $($cert.Thumbprint)"
Write-Host ""
Write-Host "В config.json укажи:"
Write-Host "  webProxyCertPath     = $pfxPath"
Write-Host "  webProxyCertPassword = $Password"
Write-Host ""
Write-Host "Для реального клиента (браузер) сертификат ещё нужно доверить:"
Write-Host "  импортируй его в 'Доверенные корневые центры сертификации', либо"
Write-Host "  используй CA-подписанный сертификат (Let's Encrypt). Для смоук-теста это"
Write-Host "  не нужно — запускай tools/web-proxy-smoke.mjs с флагом --insecure."
