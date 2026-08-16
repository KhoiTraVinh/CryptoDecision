# Run this script as Administrator to open port 8080
netsh advfirewall firewall add rule name="CryptoDecision API 8080 In"  dir=in  action=allow protocol=TCP localport=8080
netsh advfirewall firewall add rule name="CryptoDecision API 8080 Out" dir=out action=allow protocol=TCP localport=8080
Write-Host "Port 8080 opened in Windows Firewall (inbound + outbound)" -ForegroundColor Green
