param([string]$id)
$ws = New-Object System.Net.WebSockets.ClientWebSocket
$null = $ws.ConnectAsync([Uri]"ws://127.0.0.1:5173/ws", [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
$enc = [Text.Encoding]::UTF8
$b = $enc.GetBytes('{"type":"plugin.reload","payload":{"pluginId":"' + $id + '"}}')
$null = $ws.SendAsync([System.ArraySegment[byte]]$b, [Net.WebSockets.WebSocketMessageType]::Text, $true, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
$buf = New-Object byte[] 65536
$stop = ([DateTimeOffset]::Now).AddSeconds(30)
while ([DateTimeOffset]::Now -lt $stop -and $ws.State -eq [Net.WebSockets.WebSocketState]::Open) {
  $t = $ws.ReceiveAsync([System.ArraySegment[byte]]$buf, [System.Threading.CancellationToken]::None)
  if (-not $t.Wait(12000)) { Write-Output "recv timeout"; break }
  $msg = $enc.GetString($buf, 0, $t.Result.Count)
  if ($msg -match '"type":"plugin\.(reloaded|Failed)"') { Write-Output ("GOT " + $msg.Substring(0,[Math]::Min(140,$msg.Length))); break }
}
try { $ws.CloseAsync([Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "", [System.Threading.CancellationToken]::None).GetAwaiter().GetResult() } catch {}
