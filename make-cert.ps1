# ============================================================
#  make-cert.ps1 —— 给本站签一张本地证书
#
#  做的事：
#    1. 建一个本地根证书（CN=NumberBomb Local CA），只管这一台机器
#    2. 用这个根签一张服务器证书，SAN 里写 localhost、127.0.0.1、网卡上的局域网 IP
#    3. 导出 certs/server.pfx（Kestrel 用）和 certs/root-ca.cer / .pem（发给手机装）
#
#  用法：
#    powershell -ExecutionPolicy Bypass -File make-cert.ps1
#    powershell -ExecutionPolicy Bypass -File make-cert.ps1 -Trust    # 顺便装到本机受信任根，自己电脑就不报警了
#    powershell -ExecutionPolicy Bypass -File make-cert.ps1 -Force    # 换了 WiFi / IP 变了，重新签一张
#
#  提示：根证书不变的话，重新签服务器证书不需要朋友重装。
# ============================================================

[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$Trust,
    [int]$Days = 825
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$certDir = Join-Path $root 'certs'
$pfxPath = Join-Path $certDir 'server.pfx'
$cerPath = Join-Path $certDir 'root-ca.cer'
$pemPath = Join-Path $certDir 'root-ca.pem'

New-Item -ItemType Directory -Force -Path $certDir | Out-Null

# ---------- 1. 根证书 ----------
$caSubject = 'CN=NumberBomb Local CA'
$ca = Get-ChildItem Cert:\CurrentUser\My |
      Where-Object { $_.Subject -eq $caSubject -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
      Sort-Object NotAfter -Descending |
      Select-Object -First 1

if (-not $ca) {
    Write-Host '建一个本地根证书…'
    $ca = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $caSubject `
        -KeyUsage CertSign, CRLSign, DigitalSignature `
        -KeyExportPolicy Exportable `
        -KeyLength 2048 `
        -KeyAlgorithm RSA `
        -HashAlgorithm SHA256 `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(10)
    Write-Host ("  根证书指纹 " + $ca.Thumbprint)
} else {
    Write-Host ("用已有的根证书 " + $ca.Thumbprint)
}

# ---------- 2. 这台机器的地址 ----------
$ips = Get-NetIPAddress -AddressFamily IPv4 |
       Where-Object {
           $_.IPAddress -notlike '127.*' -and
           $_.IPAddress -notlike '169.254.*' -and
           $_.PrefixOrigin -ne 'WellKnown'
       } |
       Select-Object -ExpandProperty IPAddress -Unique

$hostName = [System.Net.Dns]::GetHostName()

$sanParts = @('DNS=localhost', "DNS=$hostName", "DNS=$hostName.local", 'IPAddress=127.0.0.1')
foreach ($ip in $ips) { $sanParts += "IPAddress=$ip" }
$san = '2.5.29.17={text}' + ($sanParts -join '&')

# ---------- 3. 服务器证书 ----------
if ((Test-Path $pfxPath) -and -not $Force) {
    Write-Host ''
    Write-Host 'certs/server.pfx 已经在了。要重新签就加 -Force。'
} else {
    Write-Host ''
    Write-Host '签服务器证书，覆盖这些地址：'
    foreach ($p in $sanParts) { Write-Host ("  " + $p) }

    $leaf = New-SelfSignedCertificate `
        -Type Custom `
        -Subject ("CN=" + ($ips | Select-Object -First 1)) `
        -Signer $ca `
        -KeyUsage DigitalSignature, KeyEncipherment `
        -KeyExportPolicy Exportable `
        -KeyLength 2048 `
        -KeyAlgorithm RSA `
        -HashAlgorithm SHA256 `
        -TextExtension @($san, '2.5.29.37={text}1.3.6.1.5.5.7.3.1') `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddDays($Days)

    $empty = New-Object System.Security.SecureString
    Export-PfxCertificate -Cert $leaf -FilePath $pfxPath -Password $empty | Out-Null
    Write-Host ("  server.pfx 已导出（有效期到 " + $leaf.NotAfter.ToString('yyyy-MM-dd') + "）")

    # 签完把临时服务器证书从个人store 里拿掉，只留 pfx 文件
    Remove-Item ("Cert:\CurrentUser\My\" + $leaf.Thumbprint) -Force -ErrorAction SilentlyContinue
}

# ---------- 4. 导出根证书（发给手机装的那张） ----------
Export-Certificate -Cert $ca -FilePath $cerPath -Type CERT | Out-Null

$b64 = [Convert]::ToBase64String($ca.RawData, 'InsertLineBreaks')
$pem = "-----BEGIN CERTIFICATE-----`r`n$b64`r`n-----END CERTIFICATE-----`r`n"
[System.IO.File]::WriteAllText($pemPath, $pem, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ("  根证书已导出：certs/root-ca.cer（手机用）、certs/root-ca.pem")

# ---------- 5. 要不要顺手装到自己电脑 ----------
if ($Trust) {
    Write-Host ''
    Write-Host '把根证书装进「当前用户 → 受信任的根证书颁发机构」…'
    try {
        Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
        Write-Host '  装好了。这台电脑的浏览器以后不会再问。'
    } catch {
        Write-Host ('  没装上：' + $_.Exception.Message)
        Write-Host '  手动双击 certs/root-ca.cer → 安装证书 → 本地计算机/当前用户 → 受信任的根证书颁发机构'
    }
}

Write-Host ''
Write-Host '搞定。启动网站后：'
Write-Host ('  电脑  https://localhost:5443')
foreach ($ip in $ips) { Write-Host ("  手机  https://" + $ip + ":5443") }
Write-Host ''
Write-Host '手机第一次打开会提示证书不可信，需要先装 certs/root-ca.cer（步骤见 README）。'
