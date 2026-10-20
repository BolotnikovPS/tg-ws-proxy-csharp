$dcs = @{
  'DC1' = '149.154.175.50'
  'DC2' = '149.154.167.51'
  'DC3' = '149.154.175.100'
  'DC4' = '149.154.167.91'
  'DC5' = '91.108.56.130'
}
foreach ($k in $dcs.Keys) {
  $r = Test-NetConnection -ComputerName $dcs[$k] -Port 443 -WarningAction SilentlyContinue
  "{0,-4} {1,-18} TCP443={2}" -f $k, $dcs[$k], $r.TcpTestSucceeded
}

# WS-эндпоинты (TLS-хендшейк; HTTP 4xx/5xx всё равно значит "доступен")
foreach ($dc in 1..5) {
  try {
    $r = Invoke-WebRequest -Uri "https://kws$dc.web.telegram.org/apiws" -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
    "DC$dc kws$dc.web.telegram.org: HTTP $($r.StatusCode)"
  } catch {
    "DC$dc kws$dc.web.telegram.org: FAIL"
  }
}