using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TizenAmbilight
{
    static partial class Ambilight
    {
        static HttpListener _webListener;
        static volatile bool _webRunning;

        static string HtmlPage()
        {
            return @"<!DOCTYPE html>
<html lang=""pt-BR"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1, user-scalable=no"">
<title>TizenAmbilight</title>
<style>
  :root{color-scheme:dark}
  *{box-sizing:border-box}
  body{font:14px/1.45 system-ui,sans-serif;background:#0f1216;color:#e6e9ee;margin:0;padding:20px}
  h1{font-size:18px;margin:0 0 4px}
  .sub{color:#8a93a3;margin-bottom:20px;font-size:12px}
  .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:14px}
  .card{background:#171b21;border:1px solid #262c35;border-radius:10px;padding:14px}
  .card h2{font-size:12px;text-transform:uppercase;letter-spacing:.06em;color:#8a93a3;margin:0 0 12px}
  label{display:flex;justify-content:space-between;align-items:center;gap:10px;margin:8px 0}
  label span{flex:1;color:#c3c9d4}
  input[type=range]{flex:1.2;height:28px}
  .val{width:56px;text-align:right;font-variant-numeric:tabular-nums;color:#7fd1ff}
  button{background:#2b6df5;color:#fff;border:0;border-radius:7px;padding:8px 13px;cursor:pointer;font-size:13px;min-height:36px}
  button:hover{background:#3d7dff}
  button.alt{background:#2a313b}
  button.alt:hover{background:#39424f}
  .row{display:flex;gap:8px;flex-wrap:wrap;margin-top:12px}
  .chips{display:flex;gap:6px;flex-wrap:wrap}
  .chip{padding:5px 11px;border-radius:20px;background:#222831;border:1px solid #333c48;cursor:pointer;font-size:12px;min-height:34px;display:flex;align-items:center}
  .chip.active{background:#2b6df5;border-color:#2b6df5}
  .kv{display:flex;justify-content:space-between;padding:4px 0;border-bottom:1px solid #262c35;font-size:12px}
  .kv .k{color:#8a93a3}
  .kv .v{color:#7fd1ff;font-variant-numeric:tabular-nums}
  #log{margin-top:18px;padding:11px 13px;background:#12161b;border:1px solid #262c35;border-radius:8px;font:12px/1.5 ui-monospace,monospace;color:#8a93a3;white-space:pre-wrap;min-height:44px;max-height:180px;overflow-y:auto;position:sticky;bottom:10px;z-index:10}
  @media(max-width:600px){
    body{padding:12px;padding-bottom:60px}
    .grid{grid-template-columns:1fr;gap:10px}
    .card{padding:12px}
    h1{font-size:16px}
    input[type=range]{height:32px}
    button{padding:10px 16px;min-height:42px;font-size:14px}
    .chip{padding:8px 14px;min-height:40px;font-size:13px}
    .val{width:50px}
    #log{font-size:11px;padding:9px 11px;margin-top:12px}
  }
</style>
</head>
<body>
<h1>TizenAmbilight</h1>
<div class=""sub"">Dashboard embutido · <a href=""#"" onclick=""loadStatus();return false"" style=""color:#7fd1ff"">status</a></div>
<div class=""grid"">
  <div class=""card"">
    <h2>Setup</h2>
    <label><span title=""IP do WLED (ex: 192.168.3.153)"">WLED IP</span><input type=""text"" id=""wledIp"" style=""flex:1.2;background:#222831;border:1px solid #333c48;border-radius:7px;padding:6px 10px;color:#e6e9ee;font-size:13px"" placeholder=""192.168.3.153""></label>
    <label><span title=""Total de LEDs na fita"">LEDs</span><input type=""range"" id=""ledCount"" min=""4"" max=""490"" step=""1"" value=""73""><b class=""val"" id=""ledCount_v"">73</b></label>
    <label><span title=""Largura da faixa de captura (% da borda)"">bandFrac</span><input type=""range"" id=""bandFrac"" min=""0.02"" max=""0.5"" step=""0.01"" value=""0.08""><b class=""val"" id=""bandFrac_v"">0.08</b></label>
    <label><span title=""Zonas de captura por frame (menos = mais rápido)"">zoneSample</span><input type=""range"" id=""zoneSample"" min=""1"" max=""8"" step=""1"" value=""3""><b class=""val"" id=""zoneSample_v"">3</b></label>
    <div class=""row"">
      <button onclick=""saveSetup()"">Aplicar setup</button>
      <button class=""alt"" onclick=""restartApp()"">Reiniciar app</button>
    </div>
  </div>
  <div class=""card"">
    <h2>Cor</h2>
    <label><span title=""Curva de luminosidade. Menor = mais claro nas sombras."">Gamma</span><input type=""range"" id=""gamma"" min=""1"" max=""4"" step=""0.05"" value=""1.5""><b class=""val"" id=""gamma_v"">1.50</b></label>
    <label><span title=""Multiplica brilho geral. Alto pode lavar cores."">Gain</span><input type=""range"" id=""gain"" min=""0.1"" max=""8"" step=""0.05"" value=""1""><b class=""val"" id=""gain_v"">1.00</b></label>
    <label><span title=""Intensidade das cores. 1.0 = neutro"">Sat</span><input type=""range"" id=""saturation"" min=""0"" max=""4"" step=""0.05"" value=""1""><b class=""val"" id=""saturation_v"">1.00</b></label>
    <label><span title=""Corta pretos profundos. Remove ruído em cenas escuras."">Black</span><input type=""range"" id=""black"" min=""0"" max=""200"" step=""1"" value=""12""><b class=""val"" id=""black_v"">12</b></label>
  </div>
  <div class=""card"">
    <h2>Movimento</h2>
    <label><span title=""Suavização temporal. MENOR = mais suave."">Alpha</span><input type=""range"" id=""alpha"" min=""0.02"" max=""1"" step=""0.01"" value=""0.25""><b class=""val"" id=""alpha_v"">0.25</b></label>
    <label><span title=""Mistura espacial entre zonas vizinhas."">Spatial</span><input type=""range"" id=""spatial"" min=""0"" max=""1"" step=""0.01"" value=""0.5""><b class=""val"" id=""spatial_v"">0.50</b></label>
    <label><span title=""Taxa de atualização dos LEDs."">FPS</span><input type=""range"" id=""targetFps"" min=""0"" max=""120"" step=""1"" value=""25""><b class=""val"" id=""targetFps_v"">25</b></label>
    <div class=""chips"" style=""margin-top:10px"">
      <div class=""chip"" data-cmd=""adaptiveGamma=0"">gama fixa</div>
      <div class=""chip"" data-cmd=""adaptiveGamma=1"">gama adapt.</div>
    </div>
  </div>
  <div class=""card"">
    <h2>Mapeamento</h2>
    <label><span title=""Rotaciona mapeamento LED."">mapOffset</span><input type=""range"" id=""mapOffset"" min=""-4096"" max=""4096"" step=""1"" value=""0""><b class=""val"" id=""mapOffset_v"">0</b></label>
    <div class=""chips"" style=""margin-top:10px"">
      <div class=""chip"" data-cmd=""mapReverse=0"">horário</div>
      <div class=""chip"" data-cmd=""mapReverse=1"">anti-horário</div>
    </div>
    <div class=""chips"" style=""margin-top:8px"">
      <div class=""chip"" data-cmd=""test=1"" title=""4 cores: topo=vermelho, direita=verde, baixo=azul, esquerda=amarelo"">test pattern ON</div>
      <div class=""chip"" data-cmd=""test=0"">test pattern OFF</div>
    </div>
  </div>
  <div class=""card"">
    <h2>Tuning</h2>
    <div class=""chips"">
      <div class=""chip"" data-cmd=""pause"" title=""Pausa captura (evita wedge no scaler)"">pausar</div>
      <div class=""chip"" data-cmd=""run"">resumir</div>
      <div class=""chip"" data-cmd=""zhex=1"" title=""Dump hex de todas as zonas no log"">dump zonas</div>
    </div>
    <div class=""chips"" style=""margin-top:8px"">
      <div class=""chip"" data-cmd=""backend=0"">backend auto</div>
      <div class=""chip"" data-cmd=""backend=2"" title=""Força PPI (pra Netflix/DRM)"">PPI travado</div>
    </div>
    <div class=""chips"" style=""margin-top:8px"">
      <div class=""chip"" data-preset=""cinema"" title=""Suave, escuro, cores quentes"">cinema</div>
      <div class=""chip"" data-preset=""gaming"" title=""Responsivo, vivo, brilhante"">gaming</div>
      <div class=""chip"" data-preset=""reset"" title=""Volta pros defaults"">reset</div>
    </div>
    <div class=""row"" style=""margin-top:10px"">
      <input type=""text"" id=""token"" placeholder=""token avançado (ex: gamma=2,gain=1.2)"" style=""flex:1;background:#222831;border:1px solid #333c48;border-radius:7px;padding:8px 11px;color:#e6e9ee;font-size:13px;min-height:36px"">
      <button onclick=""send(document.getElementById('token').value)"">enviar</button>
    </div>
  </div>
  <div class=""card"">
    <h2>Preview LED Strip</h2>
    <canvas id=""strip"" width=""500"" height=""320"" style=""width:100%;border-radius:8px""></canvas>
    <div class=""sub"" style=""margin:8px 0 0"">Cores ao vivo · fita 194 LEDs · teste pattern mostra cores de mapeamento</div>
  </div>
  <div class=""card"">
    <h2>Status</h2>
    <div id=""status"">carregando...</div>
  </div>
</div>
<div id=""log"">pronto</div>
<script>
const $ = id => document.getElementById(id);
const sliders = ['gamma','gain','saturation','black','alpha','spatial','targetFps','mapOffset','ledCount','bandFrac','zoneSample'];
sliders.forEach(v => {
  const el = $(v);
  el.addEventListener('input', () => $(v+'_v').textContent = el.value);
  el.addEventListener('change', () => save(v, el.value));
});
function save(key, val) {
  fetch('/api/config', {method:'POST', body:JSON.stringify({[key]:parseFloat(val)}), headers:{'Content-Type':'application/json'}})
    .then(r => r.text()).then(t => $('log').textContent = '> ' + key + '=' + val + '\n' + t)
    .catch(e => $('log').textContent = 'erro: ' + e);
}
function saveSetup() {
  const ip = $('wledIp').value.trim();
  const d = {};
  if (ip) d.wledIp = ip;
  d.ledCount = parseInt($('ledCount').value) || 73;
  d.bandFrac = parseFloat($('bandFrac').value) || 0.08;
  d.zoneSample = parseInt($('zoneSample').value) || 3;
  fetch('/api/config', {method:'POST', body:JSON.stringify(d), headers:{'Content-Type':'application/json'}})
    .then(r => r.text()).then(t => { $('log').textContent = '> setup salvo\n' + t; setTimeout(restartApp, 500); })
    .catch(e => $('log').textContent = 'erro: ' + e);
}
function restartApp() {
  fetch('/api/control', {method:'POST', body:'pause', headers:{'Content-Type':'text/plain'}})
    .then(() => { $('log').textContent = '> app reiniciando... (aguarde ~5s)'; })
    .catch(e => $('log').textContent = 'erro: ' + e);
}
function send(cmd) {
  fetch('/api/control', {method:'POST', body:cmd, headers:{'Content-Type':'text/plain'}})
    .then(r => r.text())
    .then(t => { $('log').textContent = '> ' + cmd + '\n' + t; $('log').scrollTop = $('log').scrollHeight; })
    .catch(e => $('log').textContent = 'erro: ' + e);
}
function loadStatus() {
  fetch('/api/status').then(r => r.json()).then(d => {
    let h = '';
    for (const [k,v] of Object.entries(d)) h += '<div class=""kv""><span class=""k"">'+k+'</span><span class=""v"">'+v+'</span></div>';
    $('status').innerHTML = h;
  }).catch(e => $('status').textContent = 'erro: ' + e);
}
function loadConfig() {
  fetch('/api/config').then(r => r.json()).then(d => {
    sliders.forEach(v => {
      if (d[v] !== undefined) { $(v).value = d[v]; $(v+'_v').textContent = d[v]; }
    });
    if (d.wledIp !== undefined) $('wledIp').value = d.wledIp;
    if (d.ledCount !== undefined) { $('ledCount').value = d.ledCount; $('ledCount_v').textContent = d.ledCount; }
    if (d.bandFrac !== undefined) { $('bandFrac').value = d.bandFrac; $('bandFrac_v').textContent = d.bandFrac; }
    if (d.zoneSample !== undefined) { $('zoneSample').value = d.zoneSample; $('zoneSample_v').textContent = d.zoneSample; }
  }).catch(e => $('log').textContent = 'erro cfg: ' + e);
}
document.querySelectorAll('.chip[data-cmd]').forEach(c => {
  c.addEventListener('click', () => {
    send(c.dataset.cmd);
    c.parentElement.querySelectorAll('.chip').forEach(x => x.classList.remove('active'));
    c.classList.add('active');
  });
});

const presets = {
  cinema: 'gamma=2.0,gain=0.7,saturation=1.1,black=20,alpha=0.06,spatial=0.8,targetFps=20,punch=0,floor=0,adaptiveGamma=0',
  gaming: 'gamma=1.3,gain=1.2,saturation=1.3,black=5,alpha=0.25,spatial=0.4,targetFps=45,punch=0.15,floor=0,adaptiveGamma=0',
  reset: 'gamma=1.5,gain=1.0,saturation=1.0,black=12,alpha=0.25,spatial=0.5,targetFps=25,punch=0.15,floor=0,adaptiveGamma=1,gammaMin=1.15,gammaMax=1.6'
};
document.querySelectorAll('.chip[data-preset]').forEach(c => {
  c.addEventListener('click', () => {
    const p = presets[c.dataset.preset];
    if (p) send(p);
    document.querySelectorAll('.chip[data-preset]').forEach(x => x.classList.remove('active'));
    c.classList.add('active');
  });
});
loadConfig();
loadStatus();
loadZones();
setInterval(loadStatus, 5000);
setInterval(loadZones, 200);

function loadZones() {
  fetch('/api/zones').then(r => r.json()).then(d => {
    if (d.error) return;
    drawStrip(d);
  }).catch(() => {});
}

function drawStrip(d) {
  const c = document.getElementById('strip');
  if (!c) return;
  const ctx = c.getContext('2d');
  const W = c.width, H = c.height;
  ctx.fillStyle = '#0a0d11';
  ctx.fillRect(0, 0, W, H);

  const tc = d.topCount, rc = d.rightCount, bc = d.bottomCount, lc = d.leftCount;
  const total = d.zoneCount;
  const zones = d.zones;
  const mapOffset = d.mapOffset || 0;
  const mapReverse = d.mapReverse || false;

  // ---- perimeter rectangle ----
  const pad = 24;
  const bw = 12;
  const innerW = W - pad * 2;
  const innerH = H - pad * 2 - 50;

  // top edge (zones 0..tc-1)
  const topW = innerW / tc;
  for (let i = 0; i < tc; i++) {
    const z = zones[i];
    ctx.fillStyle = `rgb(${z[0]},${z[1]},${z[2]})`;
    ctx.fillRect(pad + i * topW, pad, topW + 0.5, bw);
  }
  // right edge (zones tc..tc+rc-1)
  const rightH = innerH / rc;
  for (let i = 0; i < rc; i++) {
    const z = zones[tc + i];
    ctx.fillStyle = `rgb(${z[0]},${z[1]},${z[2]})`;
    ctx.fillRect(W - pad - bw, pad + i * rightH, bw, rightH + 0.5);
  }
  // bottom edge (zones tc+rc..tc+rc+bc-1), reversed
  const botW = innerW / bc;
  for (let i = 0; i < bc; i++) {
    const z = zones[tc + rc + i];
    ctx.fillStyle = `rgb(${z[0]},${z[1]},${z[2]})`;
    ctx.fillRect(W - pad - (i + 1) * botW, H - pad - 50 - bw, botW + 0.5, bw);
  }
  // left edge (zones tc+rc+bc..total-1), reversed
  const leftH = innerH / lc;
  for (let i = 0; i < lc; i++) {
    const z = zones[tc + rc + bc + i];
    ctx.fillStyle = `rgb(${z[0]},${z[1]},${z[2]})`;
    ctx.fillRect(pad, H - pad - 50 - (i + 1) * leftH, bw, leftH + 0.5);
  }

  // ---- start marker: bright ring at the FIRST LED position (mapOffset) ----
  // find which edge/slot the start zone falls on
  let sx = pad, sy = pad;
  if (mapOffset < tc) {
    sx = pad + mapOffset * topW + topW / 2; sy = pad + bw / 2;
  } else if (mapOffset < tc + rc) {
    sx = W - pad - bw / 2; sy = pad + (mapOffset - tc) * rightH + rightH / 2;
  } else if (mapOffset < tc + rc + bc) {
    sx = W - pad - (mapOffset - tc - rc + 0.5) * botW; sy = H - pad - 50 - bw / 2;
  } else {
    sx = pad + bw / 2; sy = H - pad - 50 - (mapOffset - tc - rc - bc + 0.5) * leftH;
  }
  // glowing start dot
  const grd = ctx.createRadialGradient(sx, sy, 0, sx, sy, 18);
  grd.addColorStop(0, 'rgba(255,255,255,1)');
  grd.addColorStop(0.5, 'rgba(255,255,255,0.6)');
  grd.addColorStop(1, 'rgba(255,255,255,0)');
  ctx.fillStyle = grd;
  ctx.beginPath(); ctx.arc(sx, sy, 18, 0, Math.PI * 2); ctx.fill();
  ctx.fillStyle = '#fff';
  ctx.beginPath(); ctx.arc(sx, sy, 6, 0, Math.PI * 2); ctx.fill();
  ctx.strokeStyle = '#000';
  ctx.lineWidth = 1.5;
  ctx.beginPath(); ctx.arc(sx, sy, 6, 0, Math.PI * 2); ctx.stroke();

  // ---- direction arrows along the perimeter ----
  const dir = mapReverse ? -1 : 1;
  ctx.strokeStyle = 'rgba(255,255,255,0.35)';
  ctx.lineWidth = 1.5;
  const arrowStep = Math.floor(total / 8);
  for (let a = 0; a < total; a += arrowStep) {
    const zi = ((mapOffset + dir * a) % total + total) % total;
    let ax, ay, angle;
    if (zi < tc) {
      ax = pad + zi * topW + topW / 2; ay = pad + bw + 8;
      angle = dir > 0 ? 0 : Math.PI;
    } else if (zi < tc + rc) {
      ax = W - pad - bw - 8; ay = pad + (zi - tc) * rightH + rightH / 2;
      angle = dir > 0 ? Math.PI / 2 : -Math.PI / 2;
    } else if (zi < tc + rc + bc) {
      ax = W - pad - (zi - tc - rc + 0.5) * botW; ay = H - pad - 50 - bw - 8;
      angle = dir > 0 ? Math.PI : 0;
    } else {
      ax = pad + bw + 8; ay = H - pad - 50 - (zi - tc - rc - bc + 0.5) * leftH;
      angle = dir > 0 ? -Math.PI / 2 : Math.PI / 2;
    }
    ctx.save();
    ctx.translate(ax, ay);
    ctx.rotate(angle);
    ctx.beginPath();
    ctx.moveTo(-5, -4); ctx.lineTo(5, 0); ctx.lineTo(-5, 4);
    ctx.stroke();
    ctx.restore();
  }

  // ---- linear zone strip (bottom) ----
  const stripY = H - 38;
  const stripW = W - pad * 2;
  const segW = stripW / total;
  for (let i = 0; i < total; i++) {
    const z = zones[i];
    ctx.fillStyle = `rgb(${z[0]},${z[1]},${z[2]})`;
    ctx.fillRect(pad + i * segW, stripY, segW + 0.5, 16);
  }
  // frame
  ctx.strokeStyle = '#333c48';
  ctx.lineWidth = 1;
  ctx.strokeRect(pad, stripY, stripW, 16);
  // start marker on linear strip
  ctx.fillStyle = '#fff';
  ctx.beginPath();
  ctx.moveTo(pad + mapOffset * segW + segW / 2, stripY - 4);
  ctx.lineTo(pad + mapOffset * segW + segW / 2 - 5, stripY - 11);
  ctx.lineTo(pad + mapOffset * segW + segW / 2 + 5, stripY - 11);
  ctx.closePath();
  ctx.fill();
  ctx.strokeStyle = '#fff';
  ctx.lineWidth = 2;
  ctx.beginPath();
  ctx.moveTo(pad + mapOffset * segW, stripY - 2);
  ctx.lineTo(pad + mapOffset * segW, stripY + 18);
  ctx.stroke();

  // labels
  ctx.fillStyle = '#8a93a3';
  ctx.font = '10px system-ui';
  ctx.fillText('0', pad, stripY + 30);
  ctx.fillText(String(total), W - pad - 12, stripY + 30);
  ctx.fillText(`map=${mapOffset} rev=${mapReverse ? 1 : 0} · ${total} zonas · inicio LED ${mapOffset}`, pad, 14);
}
</script>
</body>
</html>";
        }

        static void WebServerThread(int port, string bindAddr)
        {
            try
            {
                string prefix = "http://" + (string.IsNullOrEmpty(bindAddr) || bindAddr == "0.0.0.0" ? "+" : bindAddr) + ":" + port + "/";
                _webListener = new HttpListener();
                _webListener.Prefixes.Add(prefix);
                _webListener.Start();
                _webRunning = true;
                L("web: listening on " + prefix);
                while (_webRunning)
                {
                    var ctx = _webListener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleContext(ctx));
                }
            }
            catch (Exception e) { L("web: " + e.Message); _webRunning = false; }
        }

        static void HandleContext(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                var res = ctx.Response;
                string path = req.Url.AbsolutePath.ToLowerInvariant();
                string method = req.HttpMethod;
                string body = "";
                if (method == "POST" && req.HasEntityBody)
                    body = new StreamReader(req.InputStream, Encoding.UTF8).ReadToEnd();

                byte[] resp;
                string contentType = "application/json";

                if (path == "/" || path == "/index.html")
                {
                    resp = Encoding.UTF8.GetBytes(HtmlPage());
                    contentType = "text/html; charset=utf-8";
                }
                else if (path == "/api/config" && method == "GET")
                {
                    resp = Encoding.UTF8.GetBytes(ConfigToJson());
                }
                else if (path == "/api/config" && method == "POST")
                {
                    resp = Encoding.UTF8.GetBytes(ApplyConfigPatch(body));
                }
                else if (path == "/api/status")
                {
                    resp = Encoding.UTF8.GetBytes(StatusToJson());
                }
                else if (path == "/api/zones")
                {
                    resp = Encoding.UTF8.GetBytes(ZonesToJson());
                }
                else if (path == "/api/control" && method == "POST")
                {
                    var c = _lastConfig;
                    var s = _lastStats;
                    if (c != null && s != null) ApplyControl(body, c, s);
                    resp = Encoding.UTF8.GetBytes("{\"ok\":true}");
                }
                else
                {
                    resp = Encoding.UTF8.GetBytes("{\"error\":\"not found\"}");
                    res.StatusCode = 404;
                }

                res.ContentType = contentType;
                res.ContentLength64 = resp.Length;
                res.OutputStream.Write(resp, 0, resp.Length);
                res.OutputStream.Close();
            }
            catch { try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { } }
        }

        static string ConfigToJson()
        {
            var sb = new StringBuilder("{");
            sb.Append("\"configSource\":").Append(JsonQuote(ConfigFile.Source));
            foreach (var kv in ConfigFile.All())
            {
                sb.Append(',').Append(JsonQuote(kv.Key)).Append(':');
                if (kv.Value is bool) sb.Append((bool)kv.Value ? "true" : "false");
                else if (kv.Value is double) sb.Append(((double)kv.Value).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                else if (kv.Value is int) sb.Append((int)kv.Value);
                else if (kv.Value is long) sb.Append((long)kv.Value);
                else if (kv.Value != null) sb.Append(JsonQuote(kv.Value.ToString()));
                else sb.Append("null");
            }
            sb.Append('}');
            return sb.ToString();
        }

        static readonly Dictionary<string, string> _keyToToken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "saturation", "sat" }, { "targetFps", "fps" }, { "mapOffset", "map" },
            { "mapReverse", "rev" }, { "adaptiveGamma", "ag" }, { "staleMs", "stale" },
            { "staleLevel", "stalevel" }, { "ledCount", "leds" }, { "barRows", "brows" },
            { "barThresh", "bthr" }, { "alphaCut", "acut" }, { "alphaFast", "afast" },
            { "gammaMin", "gmin" }, { "gammaMax", "gmax" }, { "adaptTau", "atau" },
            { "ppiZones", "pz" }, { "ppiSettleMs", "psleep" },
        };

        static string ApplyConfigPatch(string json)
        {
            try
            {
                var d = ConfigFile.Parse(json);
                int saved = 0;
                var tokens = new List<string>();
                foreach (var kv in d)
                {
                    ConfigFile.SetKey(kv.Key, kv.Value);
                    string token;
                    tokens.Add((_keyToToken.TryGetValue(kv.Key, out token) ? token : kv.Key) + "=" + kv.Value);
                    saved++;
                }
                string cfgPath = ConfigFile.Source;
                if (cfgPath != null && !cfgPath.StartsWith("error") && cfgPath != "none" && System.IO.File.Exists(cfgPath))
                    ConfigFile.Save(cfgPath);
                // Apply to live config immediately
                var c = _lastConfig;
                var s = _lastStats;
                if (c != null && s != null && tokens.Count > 0)
                    ApplyControl(string.Join(",", tokens), c, s);
                return "{\"ok\":true,\"saved\":" + saved + "}";
            }
            catch (Exception e) { return "{\"error\":" + JsonQuote(e.Message) + "}"; }
        }

        static string StatusToJson()
        {
            var st = _lastStats;
            if (st == null) return "{\"status\":\"no data\"}";
            var sb = new StringBuilder("{");
            sb.Append("\"fps\":").Append(st.WindowFrames > 0 && st.Window.Elapsed.TotalSeconds > 0 ? (st.WindowFrames / st.Window.Elapsed.TotalSeconds).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "0");
            sb.Append(",\"frames\":").Append(st.Frames);
            sb.Append(",\"captureOk\":").Append(st.CaptureOk);
            sb.Append(",\"captureFail\":").Append(st.CaptureFail);
            sb.Append(",\"sendOk\":").Append(st.SendOk);
            sb.Append(",\"sendFail\":").Append(st.SendFail);
            sb.Append(",\"backend\":").Append(JsonQuote(st.BackendActive));
            sb.Append(",\"power\":").Append(JsonQuote(st.PowerState));
            sb.Append(",\"lastStatus\":").Append(JsonQuote(st.LastStatus));
            sb.Append(",\"lastMapping\":").Append(JsonQuote(st.LastMapping));
            sb.Append('}');
            return sb.ToString();
        }

        static string ZonesToJson()
        {
            var zc = _lastZoneColors;
            var ly = _lastLayout;
            var cfg = _lastConfig;
            if (zc == null || ly == null) return "{\"error\":\"no zone data\"}";
            var sb = new StringBuilder("{");
            sb.Append("\"zoneCount\":").Append(ly.Total);
            sb.Append(",\"topCount\":").Append(ly.TopCount);
            sb.Append(",\"rightCount\":").Append(ly.RightCount);
            sb.Append(",\"bottomCount\":").Append(ly.BottomCount);
            sb.Append(",\"leftCount\":").Append(ly.LeftCount);
            sb.Append(",\"bandPx\":").Append(ly.BandX.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (cfg != null)
            {
                sb.Append(",\"ledCount\":").Append(cfg.LedCount);
                sb.Append(",\"mapOffset\":").Append(cfg.MapOffset);
                sb.Append(",\"mapReverse\":").Append(cfg.MapReverse ? "true" : "false");
                sb.Append(",\"testPattern\":").Append(cfg.TestPattern ? "true" : "false");
            }
            sb.Append(",\"zones\":[");
            for (int i = 0; i < zc.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('[').Append(zc.R[i]).Append(',').Append(zc.G[i]).Append(',').Append(zc.B[i]).Append(']');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static string JsonQuote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
