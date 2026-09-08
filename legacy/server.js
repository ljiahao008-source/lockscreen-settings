'use strict';
/*
 * 锁屏·屏幕·睡眠 全套设置  v2.0（便携单文件版）
 * 纯 Node 内置模块，零依赖。支持网页界面 + 命令行两种用法。
 */
const http = require('http');
const { execFileSync, spawn, fork } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const APP = 'lockscreen-delay';
const VERSION = '2.1.0';
const DEFAULT_PORT = 18890;
const PORT_TRY = 20; // 端口被占用时向后尝试的次数

let sea = null;
try { sea = require('node:sea'); } catch (e) { sea = null; }
const IS_SEA = !!(sea && sea.isSea && sea.isSea());
const APP_DIR = IS_SEA ? path.dirname(process.execPath) : __dirname;
const EXE_NAME = IS_SEA ? path.basename(process.execPath) : '锁屏延迟设置.exe';
// 运行时数据（浏览器配置 / 日志 / 文件锁）统一放 data 子目录，保持根目录只有程序本体
const DATA_DIR = path.join(APP_DIR, 'data');
try { fs.mkdirSync(DATA_DIR, { recursive: true }); } catch (e) { }
const CONFIG_FILE = path.join(DATA_DIR, 'browser.txt');
// 迁移旧版浏览器配置（v2.1 及以前存在 exe 同目录）
try {
  const oldCfg = path.join(APP_DIR, 'browser.txt');
  if (fs.existsSync(oldCfg) && !fs.existsSync(CONFIG_FILE)) fs.copyFileSync(oldCfg, CONFIG_FILE);
} catch (e) { }

/* ---------------- 基础工具 ---------------- */

function decode(buf) {
  if (buf == null) return '';
  if (typeof buf === 'string') return buf;
  try { return new TextDecoder('gbk').decode(buf); } catch (e) { return buf.toString('utf8'); }
}

const SPAWN_OPTS = { windowsHide: true, maxBuffer: 16 * 1024 * 1024, stdio: ['ignore', 'pipe', 'pipe'] };

function run(cmd, args) {
  return decode(execFileSync(cmd, args, SPAWN_OPTS));
}

function runQuiet(cmd, args) {
  try { return { ok: true, out: run(cmd, args) }; }
  catch (e) { return { ok: false, out: decode(e.stdout) + decode(e.stderr), err: e.message }; }
}

function pick(re, text) {
  const m = re.exec(text);
  return m ? m[1] : null;
}

function toNum(x) {
  if (x == null) return null;
  const v = /^0x/i.test(x) ? parseInt(x, 16) : parseInt(x, 10);
  if (!isFinite(v)) return null;
  return v >= 0xfffffffe ? 0 : v; // 0xffffffff 视为「永不」
}

function fmt(sec) {
  sec = Number(sec) || 0;
  if (sec <= 0) return '永不';
  if (sec < 60) return sec + ' 秒';
  if (sec < 3600) { const m = sec / 60; return (Number.isInteger(m) ? m : m.toFixed(1)) + ' 分钟'; }
  const h = sec / 3600;
  return (Number.isInteger(h) ? h : h.toFixed(1)) + ' 小时';
}

function parseVal(v) {
  let s = String(v == null ? '' : v).trim().toLowerCase();
  if (s === '') throw new Error('缺少时间值');
  if (/^(never|off|no|false|none|0|永不|从不|关闭|禁用)$/.test(s)) return 0;
  let m;
  if ((m = /^(\d+(?:\.\d+)?)\s*(秒|s|sec|secs|second|seconds)$/.exec(s))) return Math.round(parseFloat(m[1]));
  if ((m = /^(\d+(?:\.\d+)?)\s*(分钟|分|m|min|mins|minute|minutes)$/.exec(s))) return Math.round(parseFloat(m[1]) * 60);
  if ((m = /^(\d+(?:\.\d+)?)\s*(小时|时|h|hr|hrs|hour|hours)$/.exec(s))) return Math.round(parseFloat(m[1]) * 3600);
  if (/^\d+$/.test(s)) return parseInt(s, 10); // 纯数字按秒
  throw new Error('无法识别的时间值: ' + v);
}

/* ---------------- 电源设置 ---------------- */

const ITEMS = {
  lock: {
    name: '锁屏后黑屏', icon: '🔒', sub: 'SUB_VIDEO', setting: 'VIDEOCONLOCK',
    desc: '锁屏壁纸出现后，多久自动关闭显示器（黑屏）。设为「永不」= 锁屏后屏幕一直亮着。'
  },
  display: {
    name: '关闭显示器', icon: '🖥️', sub: 'SUB_VIDEO', setting: 'VIDEOIDLE',
    desc: '无任何操作闲置多久后关闭屏幕。'
  },
  sleep: {
    name: '睡眠', icon: '😴', sub: 'SUB_SLEEP', setting: 'STANDBYIDLE',
    desc: '闲置多久后进入睡眠。睡眠唤醒后仍需登录，也会看到锁屏界面。'
  },
  hibernate: {
    name: '休眠', icon: '💤', sub: 'SUB_SLEEP', setting: 'HIBERNATEIDLE',
    desc: '闲置多久后进入休眠。需系统已开启休眠功能，否则此项不会生效。'
  }
};
const KEYS = ['lock', 'display', 'sleep', 'hibernate'];
const ALIAS = {
  lock: 'lock', lockscreen: 'lock', screensaverlock: 'lock', 锁屏: 'lock', 锁屏后黑屏: 'lock',
  display: 'display', screen: 'display', monitor: 'display', off: 'display', 显示器: 'display', 屏幕: 'display', 关闭显示器: 'display',
  sleep: 'sleep', standby: 'sleep', 睡眠: 'sleep',
  hibernate: 'hibernate', hib: 'hibernate', 休眠: 'hibernate',
  saver: 'saver', screensaver: 'saver', 屏保: 'saver', 屏幕保护: 'saver'
};

function activeScheme() {
  const t = run('powercfg', ['/getactivescheme']);
  const guid = pick(/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/, t);
  let name = pick(/GUID:\s*[0-9a-fA-F-]{36}\s*\(([^)]*)\)/, t);
  if (!name) name = pick(/GUID:\s*[0-9a-fA-F-]{36}\s*\(?\s*(.+?)\s*$/m, t);
  return { guid: guid || 'SCHEME_CURRENT', name: (name || '未知').trim() };
}

function queryItem(sub, setting) {
  const t = run('powercfg', ['/q', 'SCHEME_CURRENT', sub, setting]);
  let ac = pick(/当前交流电源设置索引[^\n]*?:\s*(0x[0-9a-fA-F]+|\d+)/, t);
  let dc = pick(/当前直流电源设置索引[^\n]*?:\s*(0x[0-9a-fA-F]+|\d+)/, t);
  if (ac == null || dc == null) {
    ac = pick(/Current AC Power Setting Index[^\n]*?:\s*(0x[0-9a-fA-F]+|\d+)/i, t) || ac;
    dc = pick(/Current DC Power Setting Index[^\n]*?:\s*(0x[0-9a-fA-F]+|\d+)/i, t) || dc;
  }
  if (ac == null || dc == null) { // 兜底：该设置块最后两个 0x 值即 AC、DC
    const all = t.match(/0x[0-9a-fA-F]{8}/g) || [];
    if (all.length >= 6) { ac = all[all.length - 2]; dc = all[all.length - 1]; }
  }
  return { ac: toNum(ac), dc: toNum(dc) };
}

// 一次 powercfg /q 拿回全部设置，避免反复调用
function queryAll() {
  const t = run('powercfg', ['/q', 'SCHEME_CURRENT']);
  const map = {};
  let cur = null;
  const lines = t.split(/\r?\n/);
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    let m;
    if ((m = /GUID\s*(?:别名|Alias)\s*:\s*(\S+)/i.exec(line))) { cur = m[1]; continue; }
    if (/当前交流电源设置索引|Current AC Power Setting Index/i.test(line)) {
      const v = toNum(pick(/(0x[0-9a-fA-F]+|\d+)\s*$/, line));
      if (cur) { map[cur] = map[cur] || {}; map[cur].ac = v; }
      continue;
    }
    if (/当前直流电源设置索引|Current DC Power Setting Index/i.test(line)) {
      const v = toNum(pick(/(0x[0-9a-fA-F]+|\d+)\s*$/, line));
      if (cur) { map[cur] = map[cur] || {}; map[cur].dc = v; }
      continue;
    }
  }
  return map;
}

function applyScheme() {
  const g = activeScheme().guid;
  const r = runQuiet('powercfg', ['/setactive', g]);
  return r;
}

function setItem(key, sec, mode) {
  const it = ITEMS[key];
  if (!it) throw new Error('未知的设置项: ' + key);
  mode = (mode || 'both').toLowerCase();
  const errs = [];
  if (mode !== 'dc') {
    const r = runQuiet('powercfg', ['/setacvalueindex', 'SCHEME_CURRENT', it.sub, it.setting, String(sec)]);
    if (!r.ok) errs.push('接通电源: ' + (r.out || r.err).trim());
  }
  if (mode !== 'ac') {
    const r = runQuiet('powercfg', ['/setdcvalueindex', 'SCHEME_CURRENT', it.sub, it.setting, String(sec)]);
    if (!r.ok) errs.push('使用电池: ' + (r.out || r.err).trim());
  }
  const ap = applyScheme();
  if (!ap.ok) errs.push('使设置生效: ' + (ap.out || ap.err).trim());
  return { ok: errs.length === 0, errors: errs };
}

let _battery = null;
function hasBattery() {
  if (_battery !== null) return _battery;
  try {
    const out = execFileSync('powershell', ['-NoProfile', '-Command',
      '@(Get-CimInstance -ClassName Win32_Battery -ErrorAction SilentlyContinue).Count'],
      { windowsHide: true, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
    const n = parseInt(String(out).trim(), 10);
    _battery = isFinite(n) && n > 0;
  } catch (e) { _battery = false; }
  return _battery;
}

function hibernateAvailable() {
  const drive = process.env.SystemDrive || 'C:';
  let file = false;
  try { file = fs.existsSync(drive + '\\hiberfil.sys'); } catch (e) { file = false; }
  return file;
}

/* ---------------- 屏幕保护程序 ---------------- */

const SAVER_KEY = 'HKCU\\Control Panel\\Desktop';

function regGet(name) {
  const r = runQuiet('reg', ['query', SAVER_KEY, '/v', name]);
  if (!r.ok) return null;
  const m = new RegExp(name + '\\s+REG_\\w+\\s+(.*)', 'i').exec(r.out);
  return m ? m[1].trim() : null;
}

function regSet(name, value) {
  return runQuiet('reg', ['add', SAVER_KEY, '/v', name, '/t', 'REG_SZ', '/d', String(value), '/f']);
}

function getSaver() {
  const active = regGet('ScreenSaveActive');
  const timeout = regGet('ScreenSaveTimeOut');
  const secure = regGet('ScreenSaverIsSecure') || '0';
  const on = String(active) === '1';
  return {
    active: on,
    timeout: parseInt(timeout || '900', 10) || 0,
    text: on ? fmt(parseInt(timeout || '900', 10) || 0) : '已关闭',
    secure: String(secure) === '1'
  };
}

// 通知系统屏保设置已变更（否则可能要等下次登录才生效）
function notifySaverChange(on, timeout) {
  const ps = [
    '$sig = \'[DllImport("user32.dll")] public static extern bool SystemParametersInfo(int uAction, int uParam, int lpvParam, int fuWinIni);\'',
    'try {',
    '  Add-Type -MemberDefinition $sig -Name S -Namespace W -ErrorAction Stop | Out-Null',
    '  [W.S]::SystemParametersInfo(0x000F, ' + (parseInt(timeout, 10) || 0) + ', 0, 3) | Out-Null',
    '  [W.S]::SystemParametersInfo(0x0011, ' + (on ? 1 : 0) + ', 0, 3) | Out-Null',
    '} catch { exit 1 }'
  ].join('\r\n');
  const f = path.join(os.tmpdir(), 'lockdelay_saver_' + process.pid + '.ps1');
  try {
    fs.writeFileSync(f, ps, 'utf8');
    runQuiet('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', f]);
  } catch (e) { /* 忽略 */ }
  finally { try { fs.unlinkSync(f); } catch (e) { } }
}

function setSaver(on, timeout, secure) {
  const errors = [];
  const a = regSet('ScreenSaveActive', on ? '1' : '0');
  if (!a.ok) errors.push('写入 ScreenSaveActive 失败');
  if (timeout != null) {
    const t = regSet('ScreenSaveTimeOut', String(parseInt(timeout, 10) || 0));
    if (!t.ok) errors.push('写入 ScreenSaveTimeOut 失败');
  }
  if (secure != null) {
    const s = regSet('ScreenSaverIsSecure', secure ? '1' : '0');
    if (!s.ok) errors.push('写入 ScreenSaverIsSecure 失败');
  }
  notifySaverChange(!!on, timeout);
  return { ok: errors.length === 0, errors: errors };
}

/* ---------------- 动态锁（Dynamic Lock） ---------------- */

// 动态锁开关 / 信任设备都写在这个键（注意是「Windows NT」不是「Windows」）
const WINLOGON_KEY = 'HKCU\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon';

function regQuery(key, name) {
  const r = runQuiet('reg', ['query', key, '/v', name]);
  if (!r.ok) return null;
  const esc = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const m = new RegExp(esc + '\\s+REG_\\w+\\s+(.*)', 'i').exec(r.out);
  return m ? m[1].trim() : null;
}

// 检测蓝牙配对密钥键是否健康（被管家之类清空后动态锁会失效）
// Keys 键下：一级 = 蓝牙适配器 MAC，二级 = 已配对设备 MAC
function queryBluetoothState() {
  const ps = [
    '$keys = "HKLM:\\SYSTEM\\CurrentControlSet\\Services\\BTHPORT\\Parameters\\Keys"',
    'try {',
    '  $acl = Get-Acl $keys',
    '  $ace = $acl.Access.Count',
    '  if ($ace -ge 5) { Write-Output ("KEYSHEALTH=healthy") } else { Write-Output ("KEYSHEALTH=broken") }',
    '  Write-Output ("KEYSACE=" + $ace)',
    '} catch {',
    '  Write-Output "KEYSHEALTH=unknown"',
    '  Write-Output "KEYSACE=-1"',
    '}',
    'try {',
    '  $item = Get-Item $keys -ErrorAction Stop',
    '  $adapters = @($item.GetSubKeyNames())',
    '  Write-Output ("ADAPTER=" + ($adapters -join ","))',
    '  $macs = @()',
    '  foreach ($a in $adapters) {',
    '    $sub = Get-Item ($keys + "\\" + $a) -ErrorAction SilentlyContinue',
    '    if ($sub) { $macs += $sub.GetSubKeyNames() }',
    '  }',
    '  Write-Output ("PAIRED=" + ($macs -join ","))',
    '} catch {',
    '  Write-Output "PAIRED=error"',
    '}'
  ].join('\r\n');
  const f = path.join(os.tmpdir(), 'lockdelay_bt_' + process.pid + '.ps1');
  const res = { keysHealthy: 'unknown', keysAce: -1, adapterMac: '', pairedMacs: [] };
  try {
    fs.writeFileSync(f, ps, 'utf8');
    const r = runQuiet('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', f]);
    const out = r.ok ? (r.out || '') : '';
    let m;
    if ((m = /KEYSHEALTH=(\w+)/.exec(out))) res.keysHealthy = m[1];
    if ((m = /KEYSACE=(-?\d+)/.exec(out))) res.keysAce = parseInt(m[1], 10);
    if ((m = /ADAPTER=([0-9a-fA-F,]*)/.exec(out))) res.adapterMac = (m[1] || '').split(',')[0] || '';
    if ((m = /PAIRED=([0-9a-fA-F,]*)/.exec(out))) res.pairedMacs = m[1] ? m[1].split(',').filter(Boolean) : [];
  } catch (e) { /* 保持 unknown */ }
  finally { try { fs.unlinkSync(f); } catch (e) { } }
  return res;
}

// 获取已配对的蓝牙设备 MAC 列表（去重）
// 实时获取当前活跃的蓝牙设备（基于 PnP 状态 + 注册表活跃度）
// 实时获取当前活跃的蓝牙设备
// 基于 Get-PnpDevice 状态 + 注册表 LastSeen 活跃度
function getPairedDevices() {
  try {
    // 1. 用 PnP 获取所有 Status=OK 的蓝牙设备，提取 MAC
    const pnpOut = run('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
      'Get-PnpDevice -Class Bluetooth -Status OK -ErrorAction SilentlyContinue | ForEach-Object {',
        '$mac = $null;',
        'if ($_.InstanceId -match \'([0-9A-Fa-f]{12})\') { $mac = $matches[1].ToLower(); }',
        'if (-not $mac) { foreach ($h in $_.HardwareId) { if ($h -match \'([0-9A-Fa-f]{12})\') { $mac = $matches[1].ToLower(); break; } } }',
        'if ($mac) { Write-Host $mac; }',
      '}']);
    
    const macLines = pnpOut.trim().split(/\r?\n/).filter(function(l) { return l.trim(); });
    const uniqueMacs = [...new Set(macLines.map(function(m) { return m.trim().toLowerCase(); }))];
    
    // 2. 过滤：只保留 7 天内有 LastSeen 的设备
    const now = Date.now();
    const threshold = 7 * 24 * 60 * 60 * 1000;
    
    return uniqueMacs.filter(function(mac) {
      try {
        const lsOut = run('reg', ['query', 'HKLM\\SYSTEM\\CurrentControlSet\\Services\\BTHPORT\\Parameters\\Devices\\' + mac, '/v', 'LastSeen']);
        const m = /0x([0-9a-fA-F]+)/i.exec(lsOut);
        if (!m) return false;
        const lastSeenMs = (parseInt(m[1], 16) / 10000) - 11644473600000;
        return (now - lastSeenMs) < threshold;
      } catch(e) { return false; }
    });
  } catch(e) {
    return [];
  }
}

// 判断指定 MAC 是否为手机类设备（COD Major Device Class = 2）
// 判断设备是否为手机类（通过 COD 或名称包含关键字）
function isPhoneDevice(mac) {
  try {
    // 先检查 COD
    var codLine = run('reg', ['query', 'HKLM\\SYSTEM\\CurrentControlSet\\Services\\BTHPORT\\Parameters\\Devices\\' + mac, '/v', 'COD']);
    var codM = /0x([0-9a-fA-F]+)/i.exec(codLine);
    if (codM && (parseInt(codM[1], 16) >> 8 & 0x1f) === 2) return true;
    // 再检查名称是否包含手机关键字
    var nameLine = run('reg', ['query', 'HKLM\\SYSTEM\\CurrentControlSet\\Services\\BTHPORT\\Parameters\\Devices\\' + mac, '/v', 'Name']);
    var nm = /REG_BINARY\s+([0-9A-Fa-f\s]+)/i.exec(nameLine);
    if (nm) {
      var hex = nm[1].replace(/\s+/g, '').trim();
      var buf = Buffer.from(hex, 'hex');
      while (buf.length > 0 && buf[buf.length - 1] === 0) buf = buf.slice(0, -1);
      var name = buf.toString('utf8').toLowerCase();
      if (name.includes('phone') || name.includes('mobile') || name.includes('cell')) return true;
    }
    return false;
  } catch(e) { return false; }
}

// 读取蓝牙设备的显示名称（UTF-8 编码）
function getDeviceName(mac) {
  try {
    var r = run('reg', ['query', 'HKLM\\SYSTEM\\CurrentControlSet\\Services\\BTHPORT\\Parameters\\Devices\\' + mac, '/v', 'Name']);
    var m = /REG_BINARY\s+([0-9A-Fa-f\s]+)/i.exec(r);
    if (!m) return null;
    var hex = m[1].replace(/\s+/g, '').trim();
    if (!hex) return null;
    var buf = Buffer.from(hex, 'hex');
    while (buf.length > 0 && buf[buf.length - 1] === 0) buf = buf.slice(0, -1);
    return buf.toString('utf8') || null;
  } catch (e) { return null; }
}

function getDynamicLock() {
  const eg = regQuery(WINLOGON_KEY, 'EnableGoodbye');
  const enabled = eg === '0x1' || eg === '1';
  const dp = regQuery(WINLOGON_KEY, 'DevicePairing');
  const pairedMacs = getPairedDevices();
  const pairedCount = pairedMacs.length;
  const phoneCount = pairedMacs.filter(isPhoneDevice).length;
  const deviceSelected = !!dp && pairedMacs.indexOf(dp.toLowerCase()) >= 0;
  const bt = queryBluetoothState();
  return {
    enabled: enabled,
    deviceSelected: deviceSelected,
    pairedCount: pairedCount,
    phoneCount: phoneCount,
    pairedMacs: pairedMacs,
      selectedMac: dp || null,
      selectedName: (dp ? getDeviceName(dp) : null) || dp || null,
    adapterMac: null,
    keysHealthy: bt.keysHealthy,
    keysAce: bt.keysAce
  };
}
function setDynamicLock(on) {
  const r = runQuiet('reg', ['add', WINLOGON_KEY, '/v', 'EnableGoodbye', '/t', 'REG_DWORD', '/d', on ? '1' : '0', '/f']);
  return { ok: r.ok, error: r.ok ? null : (r.out || r.err).trim() };
}
function autoRepairDynamicLock() {
  const dp = regQuery(WINLOGON_KEY, 'DevicePairing');
  const pairedMacs = getPairedDevices();
  if (pairedMacs.length === 0) {
    if (dp) {
      runQuiet('reg', ['delete', WINLOGON_KEY, '/v', 'DevicePairing', '/f']);
      log('动态锁修复: 无配对设备，已清除 DevicePairing');
    }
    return { repaired: true, action: 'cleared' };
  }
  // 优先选择手机类设备
  const bestMac = pairedMacs.find(isPhoneDevice) || pairedMacs[0];
  if (!dp || pairedMacs.indexOf(dp.toLowerCase()) < 0 || dp.toLowerCase() !== bestMac.toLowerCase()) {
    var r = runQuiet('reg', ['add', WINLOGON_KEY, '/v', 'DevicePairing', '/t', 'REG_SZ', '/d', bestMac, '/f']);
    if (r.ok) log('动态锁修复: DevicePairing -> ' + bestMac);
  }
  return { repaired: true, mac: bestMac };
}


function openSettings(target) {
  const uris = {
    dynamiclock: 'ms-settings:signinoptions-dynamiclock',
    signin: 'ms-settings:signinoptions',
    bluetooth: 'ms-settings:bluetooth'
  };
  const uri = uris[target] || uris.dynamiclock;
  try {
    spawn('cmd.exe', ['/c', 'start', '', uri], { detached: true, stdio: 'ignore', windowsHide: true }).unref();
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e.message };
  }
}





const KNOWN_BROWSERS = {
  edge: 'msedge.exe', msedge: 'msedge.exe',
  chrome: 'chrome.exe', google: 'chrome.exe',
  firefox: 'firefox.exe', ff: 'firefox.exe',
  brave: 'brave.exe',
  opera: 'launcher.exe',
  vivaldi: 'vivaldi.exe',
  qq: 'QQBrowser.exe', qqbrowser: 'QQBrowser.exe',
  '360': '360chrome.exe', '360chrome': '360chrome.exe', '360se': '360se.exe',
  sogo: 'SogouExplorer.exe', sogou: 'SogouExplorer.exe'
};

function resolveBrowser(spec) {
  if (!spec) return null;
  const s = String(spec).trim().replace(/^["']|["']$/g, '');
  if (!s) return null;
  if (/^(system|default|系统|默认)$/i.test(s)) return null;
  if (fs.existsSync(s)) return s;
  const exe = KNOWN_BROWSERS[s.toLowerCase()] || (/\.exe$/i.test(s) ? s : s + '.exe');
  // 1) 注册表 App Paths
  const r = runQuiet('reg', ['query', 'HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\' + exe, '/ve']);
  if (r.ok) {
    const m = /REG_\w+\s+(.+\.exe)/i.exec(r.out);
    if (m && fs.existsSync(m[1].trim())) return m[1].trim();
  }
  const r2 = runQuiet('reg', ['query', 'HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\' + exe, '/ve']);
  if (r2.ok) {
    const m = /REG_\w+\s+(.+\.exe)/i.exec(r2.out);
    if (m && fs.existsSync(m[1].trim())) return m[1].trim();
  }
  // 2) 常见安装位置
  const dirs = [
    process.env['ProgramFiles(x86)'], process.env.ProgramFiles,
    process.env.LOCALAPPDATA, process.env.ProgramW6432
  ].filter(Boolean);
  const subs = {
    'msedge.exe': ['Microsoft\\Edge\\Application'],
    'chrome.exe': ['Google\\Chrome\\Application'],
    'firefox.exe': ['Mozilla Firefox'],
    'brave.exe': ['BraveSoftware\\Brave-Browser\\Application'],
    'vivaldi.exe': ['Vivaldi\\Application'],
    'QQBrowser.exe': ['Tencent\\QQBrowser']
  }[exe] || [];
  for (const d of dirs) {
    for (const sub of subs) {
      const p = path.join(d, sub, exe);
      if (fs.existsSync(p)) return p;
    }
  }
  return null;
}

// 供界面展示的浏览器清单
const BROWSER_LIST = [
  { value: 'system', name: '系统默认浏览器' },
  { value: 'edge', name: 'Microsoft Edge' },
  { value: 'chrome', name: 'Google Chrome' },
  { value: 'firefox', name: 'Mozilla Firefox' },
  { value: 'brave', name: 'Brave' },
  { value: 'vivaldi', name: 'Vivaldi' },
  { value: 'opera', name: 'Opera' },
  { value: 'qq', name: 'QQ 浏览器' },
  { value: '360chrome', name: '360 极速浏览器' },
  { value: '360se', name: '360 安全浏览器' }
];

const CONFIG_TEMPLATE = [
  '# 锁屏·屏幕·睡眠 全套设置 - 浏览器偏好（可选）',
  '# 下面这一行写要用来打开设置页面的浏览器。',
  '# 可写关键字（edge / chrome / firefox / brave / opera / vivaldi / qq / 360）',
  '# 或写完整的 .exe 路径。',
  '# 留空或整行注释掉 = 用系统默认浏览器。',
  '# 也可以直接在网页界面里选，不必手动改这里。',
  ''
].join('\r\n');

function browserInfo() {
  const cur = readBrowserConfig();
  const list = BROWSER_LIST.map(b => {
    const p = b.value === 'system' ? null : resolveBrowser(b.value);
    return { value: b.value, name: b.name, path: p, installed: b.value === 'system' || !!p };
  });
  return { current: cur, resolved: cur ? resolveBrowser(cur) : null, list: list };
}

function saveBrowserPref(value) {
  const v = String(value == null ? '' : value).trim();
  if (!v || /^(system|default|系统|默认)$/i.test(v)) {
    fs.writeFileSync(CONFIG_FILE, CONFIG_TEMPLATE, 'utf8');
    return { ok: true, resolved: null };
  }
  const exe = resolveBrowser(v);
  fs.writeFileSync(CONFIG_FILE, CONFIG_TEMPLATE + (exe || v) + '\r\n', 'utf8');
  return { ok: true, resolved: exe, fallback: !exe };
}

function readBrowserConfig() {
  try {
    const lines = fs.readFileSync(CONFIG_FILE, 'utf8').split(/\r?\n/);
    for (const l of lines) {
      const t = l.trim();
      if (t && !t.startsWith('#')) return t;
    }
  } catch (e) { }
  return null;
}

function openUrl(url, spec) {
  const exe = resolveBrowser(spec);
  const openDefault = () => {
    try {
      spawn('cmd.exe', ['/c', 'start', '', url.replace(/[&^]/g, '^$&')], { detached: true, stdio: 'ignore', windowsHide: true }).unref();
      return { ok: true, via: '系统默认浏览器' };
    } catch (e) {
      return { ok: false, error: e.message };
    }
  };
  if (!exe) return openDefault();
  try {
    const isChromium = /chrome|msedge|chromium|brave|vivaldi|opera|360chrome|qqbrowser/i.test(exe);
    let args;
    if (isChromium) {
      args = ['--proxy-server=""', '--proxy-bypass-list=<-loopback>;localhost;127.0.0.1;*', '--disable-http-cache', '--new-window', '--window-size=1000,800', '--window-position=100,100', url];
    } else {
      args = ['--new-window', '--window-size=1000,800', '--window-position=100,100', url];
    }
    const child = spawn(exe, args, { detached: true, stdio: 'ignore', windowsHide: false });
    child.on('error', () => openDefault());
    child.unref();
    return { ok: true, via: exe };
  } catch (e) {
    return openDefault();
  }
}

/* ---------------- 状态 ---------------- */

function getState(port) {
  const scheme = activeScheme();
  const map = queryAll();
  const items = {};
  for (const k of KEYS) {
    const v = map[ITEMS[k].setting] || { ac: null, dc: null };
    items[k] = {
      key: k, name: ITEMS[k].name, icon: ITEMS[k].icon, desc: ITEMS[k].desc,
      ac: v.ac, dc: v.dc, acText: fmt(v.ac), dcText: fmt(v.dc)
    };
  }
  return {
    app: APP, version: VERSION, port: port, pid: process.pid,
    scheme: scheme,
    hasBattery: hasBattery(),
    hibernateAvailable: hibernateAvailable(),
    items: items,
    saver: getSaver(),
    exe: EXE_NAME
  };
}

/* ---------------- 配置导出 / 导入 ---------------- */

const EXPORT_DEFAULT = '锁屏设置.json';

function pad(s, n) {
  s = String(s == null ? '' : s);
  let w = 0;
  for (const ch of s) w += /[\u4e00-\u9fa5]/.test(ch) ? 2 : 1;
  return s + ' '.repeat(Math.max(0, n - w));
}

// 相对路径 / 纯文件名都落到 exe 同目录，方便整个文件夹拷走
function resolvePath(p, defaultName) {
  if (!p) return path.join(APP_DIR, defaultName);
  if (path.isAbsolute(p)) return p;
  if (/[\\/]/.test(p)) return path.resolve(process.cwd(), p);
  return path.join(APP_DIR, p);
}

function buildConfig() {
  const map = queryAll();
  const scheme = activeScheme();
  const items = {};
  for (const k of KEYS) {
    const v = map[ITEMS[k].setting] || {};
    items[k] = { ac: v.ac == null ? 0 : v.ac, dc: v.dc == null ? 0 : v.dc };
  }
  const sv = getSaver();
  return {
    app: APP,
    type: 'lockscreen-delay-config',
    version: VERSION,
    exportedAt: new Date().toISOString(),
    computer: os.hostname(),
    scheme: { name: scheme.name, guid: scheme.guid },
    items: items,
    saver: { active: sv.active, timeout: sv.timeout, secure: sv.secure }
  };
}

function exportConfig(file) {
  const target = resolvePath(file, EXPORT_DEFAULT);
  const cfg = buildConfig();
  fs.writeFileSync(target, JSON.stringify(cfg, null, 2), 'utf8');
  return { file: target, config: cfg };
}

function readConfig(file) {
  const p = resolvePath(file, EXPORT_DEFAULT);
  if (!fs.existsSync(p)) throw new Error('找不到配置文件：' + p + '\n  （不带文件名时默认找 exe 同目录的 ' + EXPORT_DEFAULT + '）');
  let j;
  try { j = JSON.parse(fs.readFileSync(p, 'utf8')); }
  catch (e) { throw new Error('配置文件不是有效的 JSON：' + e.message); }
  if (!j || typeof j !== 'object' || !j.items) throw new Error('这不像是本工具导出的配置文件（缺少 items 字段）');
  return { file: p, config: j };
}

function applyConfig(cfg) {
  const results = [];
  const errors = [];
  const items = cfg.items || {};
  let applied = 0;
  for (const k of KEYS) {
    const v = items[k];
    if (!v) continue;
    const ac = parseInt(v.ac, 10), dc = parseInt(v.dc, 10);
    if (!isFinite(ac) || !isFinite(dc)) continue;
    applied++;
    let r;
    if (ac === dc) {
      r = setItem(k, ac, 'both');
    } else {
      const r1 = setItem(k, ac, 'ac');
      const r2 = setItem(k, dc, 'dc');
      r = { ok: r1.ok && r2.ok, errors: [].concat(r1.errors || [], r2.errors || []) };
    }
    results.push('  ' + pad(ITEMS[k].name, 16) + '接通电源 ' + fmt(ac) + '  使用电池 ' + fmt(dc));
    if (!r.ok) errors.push(ITEMS[k].name + '：' + r.errors.join('；'));
  }
  const s = cfg.saver;
  if (s) {
    const on = s.active === true || s.active === '1' || s.active === 'true';
    const t = parseInt(s.timeout, 10);
    const sec = s.secure === true || s.secure === '1' || s.secure === 'true';
    const r = setSaver(on, isFinite(t) ? t : null, sec);
    results.push('  ' + pad('屏幕保护程序', 16) + (on ? fmt(isFinite(t) ? t : 0) : '已关闭'));
    if (!r.ok) errors.push('屏幕保护程序：' + r.errors.join('；'));
  }
  if (applied === 0 && !s) errors.push('配置里没有可用的设置项（items 为空）');
  return { ok: errors.length === 0, results: results, errors: errors };
}

function importConfig(file) {
  const r = readConfig(file);
  return { file: r.file, config: r.config, applied: applyConfig(r.config) };
}

/* ---------------- HTTP ---------------- */

function json(res, code, obj) {
  const body = JSON.stringify(obj);
  res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(body);
}

function readBody(req) {
  return new Promise((resolve) => {
    let d = '';
    let done = false;
    const finish = (obj) => { if (!done) { done = true; resolve(obj); } };
    // 注意：超过上限后 req.destroy() 不会再触发 'end'，必须在这里先 finish，
    // 否则 Promise 永不 resolve，请求处理挂起、连接泄漏。
    req.on('data', c => {
      if (done) return;
      d += c;
      if (d.length > 1e6) { d = d.slice(0, 1000000); finish({}); req.destroy(); }
    });
    req.on('end', () => {
      if (done) return;
      try { finish(d ? JSON.parse(d) : {}); } catch (e) { finish({}); }
    });
    req.on('error', () => finish({}));
    req.on('aborted', () => finish({}));
  });
}

/* ---------------- 自动退出（页面关闭后）---------------- */

let exitTimer = null;
let KEEP_ALIVE = false;     // --keep-alive 时禁用自动退出
const EXIT_DELAY = 10000;   // 断开后延时退出，避开刷新 / EventSource 自动重连
const sseClients = new Set(); // 当前存活的页面连接（EventSource 响应对象）
let pageOpened = false;       // 是否曾有页面打开（--no-open 且从未开页面时不自动退出）

// 无活动超时退出（页面关闭后约10秒自动退出）
function scheduleExit() {
  if (KEEP_ALIVE || !pageOpened) return; // 常驻模式 或 从未开过页面：不自动退出
  if (exitTimer) clearTimeout(exitTimer);
  exitTimer = setTimeout(() => {
    if (sseClients.size > 0) return;     // 退出前复查：仍有存活连接则放弃
    log('无活动连接，程序自动退出');
    try { process.exit(0); } catch (e) { }
  }, EXIT_DELAY);
}
function cancelExit() {
  if (exitTimer) { clearTimeout(exitTimer); exitTimer = null; }
}

function createServer(port) {
  return http.createServer(async (req, res) => {
    const u = new URL(req.url, 'http://0.0.0.0');  // allow any host header
    const p = u.pathname.replace(/\/+$/, '') || '/';
    log('收到请求 ' + req.method + ' ' + p + ' 来自 ' + (req.socket.remoteAddress || '?'));
    const q = Object.assign({}, Object.fromEntries(u.searchParams), await readBody(req));

    try {
      if (p === '/' || p === '/index.html') {
        res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
        res.end(htmlPage(port));
        return;
      }
      if (p === '/api/ping') return json(res, 200, { app: APP, version: VERSION, port: port, pid: process.pid });

      // 页面存活探测（SSE）：页面打开时建立长连接，关闭时断开，服务端据此自动退出
      if (p === '/api/events') {
        res.writeHead(200, {
          'Content-Type': 'text/event-stream; charset=utf-8',
          'Cache-Control': 'no-store',
          'Connection': 'keep-alive',
          'X-Accel-Buffering': 'no'
        });
        res.write('retry: 3000\n\n');
        pageOpened = true;
        sseClients.add(res);
        cancelExit();
        log('页面已连接（存活探测），当前连接数=' + sseClients.size);
        const hb = setInterval(() => {
          try { res.write(': ping\n\n'); } catch (e) { clearInterval(hb); }
        }, 15000);
        let closed = false;
        const onClose = (why) => {
          if (closed) return;
          closed = true;
          clearInterval(hb);
          sseClients.delete(res);
          log('页面已断开(' + (why || 'close') + ')，剩余连接数=' + sseClients.size);
          if (sseClients.size === 0) scheduleExit();
        };
        req.on('close', () => onClose('req.close'));
        req.on('aborted', () => onClose('req.aborted'));
        req.on('error', () => onClose('req.error'));
        res.on('close', () => onClose('res.close'));
        return;
      }

      // 页面关闭时连接断开，服务端据此自动退出
      if (p === '/api/state') return json(res, 200, getState(port));

      if (p === '/api/browser') {
        if (req.method !== 'POST') return json(res, 200, browserInfo());
        if (q.value === undefined) return json(res, 400, { ok: false, error: '缺少 value' });
        try {
          const r = saveBrowserPref(q.value);
          return json(res, 200, Object.assign({ info: browserInfo() }, r));
        } catch (e) { return json(res, 500, { ok: false, error: e.message }); }
      }

      if (p === '/api/set' || p === '/set') {
        const key = ALIAS[String(q.key || '').toLowerCase()] || String(q.key || '').toLowerCase();
        const mode = String(q.mode || 'both').toLowerCase();
        if (key === 'saver') {
          let on, timeout = null;
          const v = String(q.sec != null ? q.sec : q.s != null ? q.s : q.value);
          if (/^(on|1|true|开|开启)$/i.test(v)) on = true;
          else if (/^(off|0|false|关|关闭)$/i.test(v)) on = false;
          else { on = true; timeout = parseVal(v); }
          if (q.timeout != null) timeout = parseVal(q.timeout);
          const r = setSaver(on, timeout, null);
          return json(res, 200, Object.assign({ saver: getSaver(), name: '屏幕保护程序' }, r));
        }
        if (!ITEMS[key]) return json(res, 400, { ok: false, error: '未知设置项: ' + q.key });
        const sec = parseVal(q.sec != null ? q.sec : q.s != null ? q.s : q.value);
        const r = setItem(key, sec, mode);
        const v = queryAll()[ITEMS[key].setting] || { ac: null, dc: null };
        return json(res, 200, Object.assign({
          ok: r.ok, key: key, name: ITEMS[key].name, sec: sec, text: fmt(sec),
          ac: v.ac, dc: v.dc, acText: fmt(v.ac), dcText: fmt(v.dc)
        }, r.ok ? {} : { error: r.errors.join('；') }));
      }

      if (p === '/api/saver') {
        const r = setSaver(!!q.on && String(q.on) !== '0' && String(q.on) !== 'false',
          q.timeout != null ? parseVal(q.timeout) : null,
          q.secure != null ? !!q.secure && String(q.secure) !== '0' : null);
        return json(res, 200, Object.assign({ saver: getSaver() }, r.ok ? {} : { error: r.errors.join('；') }));
      }

      if (p === '/api/defaults') {
        const r = runQuiet('powercfg', ['-restoredefaultschemes']);
        setSaver(false, 900, false);
        return json(res, 200, { ok: r.ok, state: getState(port), error: r.ok ? null : (r.out || r.err) });
      }

      if (p === '/api/export') {
        const cfg = buildConfig();
        if (q.file) { // 同时存一份到磁盘（页面下载之外的备份）
          try {
            const t = resolvePath(q.file, EXPORT_DEFAULT);
            fs.writeFileSync(t, JSON.stringify(cfg, null, 2), 'utf8');
            cfg.savedAs = t;
          } catch (e) { /* 忽略 */ }
        }
        return json(res, 200, cfg);
      }

      if (p === '/api/import') {
        let cfg = q.config;
        if (cfg && typeof cfg === 'string') {
          try { cfg = JSON.parse(cfg); } catch (e) { return json(res, 400, { ok: false, error: '配置不是有效的 JSON' }); }
        }
        if (!cfg && q.file) {
          try { cfg = readConfig(q.file).config; }
          catch (e) { return json(res, 400, { ok: false, error: e.message }); }
        }
        if (!cfg || typeof cfg !== 'object') return json(res, 400, { ok: false, error: '缺少配置内容' });
        if (!cfg.items) return json(res, 400, { ok: false, error: '这不像是本工具导出的配置文件（缺少 items 字段）' });
        const a = applyConfig(cfg);
        return json(res, 200, Object.assign({
          ok: a.ok, applied: a.results, state: getState(port)
        }, a.ok ? {} : { error: a.errors.join('；') }));
      }

      if (p === '/api/dynlock') {
        if (req.method === 'POST' && q.on !== undefined) {
          const on = ['0', 'false', 'off', '', 'no', 'n', '关闭', '关'].indexOf(String(q.on)) < 0;
          const r = setDynamicLock(on);
          if (r.ok) runQuiet('reg', ['query', WINLOGON_KEY, '/v', 'EnableGoodbye']);
          const dl = getDynamicLock();
          return json(res, 200, Object.assign({ dynlock: dl }, r.ok ? {} : { error: r.error }));
        }
        if (req.method === 'POST' && q.open) {
          return json(res, 200, openSettings(q.open));
        }
        return json(res, 200, getDynamicLock());
      }
      if (p === '/api/quit') {
        // 先删文件锁再响应：用户关窗后立即重新启动时，新实例读锁文件已是"无锁"状态，
        // 会全新启动，而不是复用「正要退出」的旧后端（旧后端 process.exit 前也走
        // 'exit' 事件再 releaseLock 一次，幂等无害）。
        releaseLock();
        json(res, 200, { ok: true, message: 'bye' });
        setTimeout(() => { try { process.exit(0); } catch (e) { } }, 120);
        return;
      }

      // 兼容旧版接口
      if (p === '/get') {
        const key = ALIAS[String(q.key || '').toLowerCase()];
        if (key === 'saver') return json(res, 200, { text: getSaver().text });
        if (!ITEMS[key]) return json(res, 400, { text: '?' });
        const v = queryItem(ITEMS[key].sub, ITEMS[key].setting);
        return json(res, 200, { text: fmt(v.ac), ac: v.ac, dc: v.dc });
      }

      return json(res, 404, { error: 'not found' });
    } catch (e) {
      return json(res, 500, { ok: false, error: e.message });
    }
  });
}

/* ---------------- 实例探测 ---------------- */

function probe(port) {
  return new Promise((resolve) => {
    const req = http.request({ host: '127.0.0.1', port: port, path: '/api/ping', method: 'GET', timeout: 700 },
      (res) => {
        let d = '';
        res.on('data', c => d += c);
        res.on('end', () => {
          try {
            const j = JSON.parse(d);
            resolve(j && j.app === APP ? { port: port, info: j } : null);
          } catch (e) { resolve(null); }
        });
      });
    req.on('error', () => resolve(null));
    req.on('timeout', () => { req.destroy(); resolve(null); });
    req.end();
  });
}

async function findRunning() {
  for (let i = 0; i < PORT_TRY; i++) {
    const r = await probe(DEFAULT_PORT + i);
    if (r) return r;
  }
  return null;
}

/* ---------------- 文件锁互斥（不依赖网络探测，比 HTTP probe 更可靠） ---------------- */

const LOCK_FILE = path.join(DATA_DIR, '.lockscreen-delay.lock');

function isProcessAlive(pid) {
  if (!pid) return false;
  try { process.kill(pid, 0); return true; }
  catch (e) { return !!(e && e.code === 'EPERM'); }
}

function readLock() {
  try {
    const txt = fs.readFileSync(LOCK_FILE, 'utf8').trim();
    const parts = txt.split(/\r?\n/);
    return { pid: parseInt(parts[0], 10) || 0, port: parseInt(parts[1], 10) || 0 };
  } catch (e) { return { pid: 0, port: 0 }; }
}

function acquireLock() {
  const cur = readLock();
  if (cur.pid && isProcessAlive(cur.pid)) {
    return { ok: false, port: cur.port || DEFAULT_PORT };
  }
  try { fs.unlinkSync(LOCK_FILE); } catch (e) { }
  try {
    const fd = fs.openSync(LOCK_FILE, 'wx'); // 原子独占创建
    fs.writeSync(fd, String(process.pid) + '\n' + String(DEFAULT_PORT) + '\n');
    fs.closeSync(fd);
    return { ok: true };
  } catch (e) {
    const cur2 = readLock();
    if (cur2.pid && isProcessAlive(cur2.pid)) {
      return { ok: false, port: cur2.port || DEFAULT_PORT };
    }
    return { ok: true };
  }
}

function updateLockPort(port) {
  try { fs.writeFileSync(LOCK_FILE, String(process.pid) + '\n' + String(port) + '\n', 'utf8'); }
  catch (e) { }
}

function releaseLock() {
  try {
    const cur = readLock();
    if (cur.pid === process.pid) { try { fs.unlinkSync(LOCK_FILE); } catch (e) { } }
  } catch (e) { }
}

// 启动日志：写到 data 子目录，方便排查「打不开」时不用看控制台窗口
const LOG_FILE = path.join(DATA_DIR, '.lockscreen-delay.log');
function log(msg) {
  try {
    const line = '[' + new Date().toLocaleString('zh-CN', { hour12: false }) + '] ' + msg + '\r\n';
    fs.appendFileSync(LOG_FILE, line, 'utf8');
  } catch (e) { }
}

/* 控制台输出安全化（桌面化改造）：
 * 打包后的 exe 为 Windows GUI 子系统，双击时没有控制台句柄——直接 console.log
 * 在部分宿主环境下会异常。统一改走 out()：同时写入日志文件（.lockscreen-delay.log），
 * 并在终端可用时（命令行调用，如 --status / --stop）仍输出到终端。
 * 至此程序不再依赖、也不再弹出任何控制台窗口。 */
const _stdLog = console.log.bind(console);
const _stdErr = console.error.bind(console);
function toText(a) {
  if (a instanceof Error) return a.stack || a.message;
  if (typeof a === 'object' && a !== null) { try { return JSON.stringify(a); } catch (e) { return String(a); } }
  return String(a);
}
function out(...args) {
  try { log(args.map(toText).join(' ')); } catch (e) { }
  try { _stdLog(...args); } catch (e) { }
}
console.log = out;
console.error = (...args) => {
  try { log('[错误] ' + args.map(toText).join(' ')); } catch (e) { }
  try { _stdErr(...args); } catch (e) { }
};

function listen(server, port) {
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    // 默认只监听 127.0.0.1（本机回环），避免局域网内其他设备通过无鉴权的 HTTP API
    // 修改电源设置 / 停止服务。确需局域网访问时，可设置环境变量 LOCKSCREEN_HOST=0.0.0.0。
    const host = process.env.LOCKSCREEN_HOST || '127.0.0.1';
    server.listen(port, host, () => resolve(port));
  });
}

async function startServer(preferPort) {
  const start = preferPort || DEFAULT_PORT;
  let lastErr;
  for (let i = 0; i < PORT_TRY; i++) {
    const port = start + i;
    const server = createServer(port);
    try {
      await listen(server, port);
      return { server: server, port: port };
    } catch (e) {
      lastErr = e;
      try { server.close(); } catch (x) { }
      if (e.code !== 'EADDRINUSE') break;
    }
  }
  throw lastErr || new Error('无法启动服务');
}

async function stopRunning() {
  const r = await findRunning();
  if (r) {
    await new Promise((resolve) => {
      const req = http.request({ host: '127.0.0.1', port: r.port, path: '/api/quit', method: 'POST', timeout: 1200 },
        () => resolve());
      req.on('error', () => resolve());
      req.on('timeout', () => { req.destroy(); resolve(); });
      req.end();
    });
    return { ok: true, via: 'port ' + r.port };
  }
  if (/^node(\.exe)?$/i.test(path.basename(process.execPath))) return { ok: false, via: '未找到运行中的服务' };
  const t = runQuiet('taskkill', ['/F', '/IM', path.basename(process.execPath)]);
  return { ok: t.ok, via: 'taskkill' };
}

/* ---------------- 命令行 ---------------- */

const HELP = [
  '',
  '  锁屏·屏幕·睡眠 全套设置  v' + VERSION + '（便携版）',
  '',
  '  用法：',
  '    ' + EXE_NAME + '                        启动桌面图形界面（无桌面版时打开设置页面）',
  '    ' + EXE_NAME + ' display=10min          直接设置（命令行模式，设完即退）',
  '    ' + EXE_NAME + ' --status               查看当前所有设置',
  '    ' + EXE_NAME + ' --stop                 停止后台服务',
  '',
  '  设置项：',
  '    lock       锁屏后黑屏（锁屏壁纸出现后多久关屏）',
  '    display    关闭显示器（闲置多久关屏）',
  '    sleep      睡眠',
  '    hibernate  休眠',
  '    saver      屏幕保护程序（on / off / 时间）',
  '    dynlock    动态锁（离开自动锁屏，on / off / status）',
  '',
  '  时间写法：never(永不) / 30s / 10min / 2h / 600（纯数字=秒）',
  '  前缀 ac: / dc: 可只改「接通电源」或「使用电池」，例如 ac:sleep=30min',
  '  一次可写多项：' + EXE_NAME + ' display=10min ac:sleep=30min saver=off',
  '',
  '  其它参数：',
  '    --browser=X          用指定浏览器打开页面（edge / chrome / firefox / 完整路径）',
  '    --set-browser=X      把浏览器偏好写入 browser.txt，之后一直生效',
  '    --port=N             指定端口（默认 18890，被占用自动顺延）',
  '    --no-open            只启动服务，不打开浏览器',
  '    --open               强制打开页面（配合已运行实例）',
  '    --keep-alive         关掉页面也不退出（默认关掉页面约 10 秒后自动退出）',
  '    --status / --list    打印当前设置并退出',
  '    --dynlock           查看动态锁状态（开关 / 信任设备 / 配对 / 密钥）',
  '    --dynlock=on|off    开启 / 关闭动态锁',
  '    --defaults           恢复电源计划的系统默认值',
  '    --stop               停止后台服务',
  '    --help               显示本帮助',
  '',
  '  换电脑 / 重装系统时迁移设置（不需要管理员）：',
  '    ' + EXE_NAME + ' --export           把当前设置存成「锁屏设置.json」',
  '    ' + EXE_NAME + ' --import           在新电脑上还原（文件放 exe 同目录）',
  '    ' + EXE_NAME + ' --show             只看文件内容，不应用',
  '  不写文件名时默认用 exe 同目录的「' + EXPORT_DEFAULT + '」，整个文件夹拷走即可。',
  ''
].join('\n');

function printStatus() {
  const st = getState(0);
  const lines = [];
  lines.push('');
  lines.push('  当前电源计划：' + st.scheme.name + '   (' + st.scheme.guid + ')');
  lines.push('  休眠功能：' + (st.hibernateAvailable ? '已开启' : '未开启（休眠设置不会生效）'));
  lines.push('');
  if (st.hasBattery) {
    lines.push('  ' + pad('项目', 18) + pad('接通电源', 14) + '使用电池');
    for (const k of KEYS) lines.push('  ' + pad(ITEMS[k].name, 18) + pad(st.items[k].acText, 14) + st.items[k].dcText);
  } else {
    lines.push('  ' + pad('项目', 18) + '当前值');
    for (const k of KEYS) lines.push('  ' + pad(ITEMS[k].name, 18) + st.items[k].acText);
  }
  lines.push('  ' + pad('屏幕保护', 18) + (st.saver.active ? st.saver.timeout + ' 秒' : '已关闭'));
  const dl = getDynamicLock();
  lines.push('  ' + pad('动态锁', 18) + (dl.enabled ? '已开启' : '已关闭') + '  信任设备' + (dl.deviceSelected ? '已选择' : '未选择') + '  蓝牙密钥' + (dl.keysHealthy === 'healthy' ? '正常' : '异常'));
  lines.push('');
  return lines.join('\n');
}

function printDynlock() {
  const dl = getDynamicLock();
  const on = dl.enabled;
  const device = dl.selectedName || dl.selectedMac || '(未配置)';
  const valid = dl.deviceSelected;
  const lines = [
    '',
    '  === 动态锁 ===',
    '  状态：' + (on ? '已开启' : '已关闭'),
    '  信任设备：' + (valid ? device : '未选择'),
    ''
  ];
  return lines.join('\n');
}

function parseArgs(argv) {
  const opts = { sets: [], port: null, browser: null, setBrowser: null, noOpen: false, open: false, action: null, keepAlive: false, dynlockAction: null, exportFile: null, importFile: null, showFile: null, guiPid: null };
  for (const a of argv) {
    if (/^(--help|-h|\/|help)$/i.test(a)) { opts.action = 'help'; }
    else if (/^--export$/i.test(a)) { opts.exportFile = ''; }
    else if (/^--export[=:]/i.test(a)) { opts.exportFile = a.replace(/^--export[=:]/i, ''); }
    else if (/^--import$/i.test(a)) { opts.importFile = ''; }
    else if (/^--import[=:]/i.test(a)) { opts.importFile = a.replace(/^--import[=:]/i, ''); }
    else if (/^--show$/i.test(a)) { opts.showFile = ''; }
    else if (/^--show[=:]/i.test(a)) { opts.showFile = a.replace(/^--show[=:]/i, ''); }
    else if (/^(--status|--list|-l)$/i.test(a)) { opts.action = 'status'; }
    else if (/^(--stop|--quit|--exit|-q)$/i.test(a)) { opts.action = 'stop'; }
    else if (/^(--defaults|--reset)$/i.test(a)) { opts.action = 'defaults'; }
    else if (/^--port[=:]?/i.test(a)) { opts.port = parseInt(a.replace(/^--port[=:]?/i, ''), 10) || null; }
    else if (/^--browser[=:]/i.test(a)) { opts.browser = a.replace(/^--browser[=:]/i, ''); }
    else if (/^--set-browser[=:]/i.test(a)) { opts.setBrowser = a.replace(/^--set-browser[=:]/i, ''); }
    else if (/^--no-open$/i.test(a)) { opts.noOpen = true; }
    else if (/^--open$/i.test(a)) { opts.open = true; }
    else if (/^--keep-alive$/i.test(a)) { opts.keepAlive = true; }
    else if (/^--gui-pid[=:]/i.test(a)) { opts.guiPid = parseInt(a.replace(/^--gui-pid[=:]/i, ''), 10) || null; }
    else if (/^--dynlock$/i.test(a)) { opts.dynlockAction = 'status'; }
    else if (/^--dynlock[=:]/i.test(a)) {
      const v = a.replace(/^--dynlock[=:]/i, '').trim().toLowerCase();
      if (/^(on|1|true|开|开启)$/.test(v)) opts.dynlockAction = 'on';
      else if (/^(off|0|false|关|关闭)$/.test(v)) opts.dynlockAction = 'off';
      else if (/^(status|show|state|查|看)$/.test(v)) opts.dynlockAction = 'status';
      else throw new Error('无法识别的动态锁参数：' + v + '（可用 on / off / status）');
    }
    else {
      const m = /^(?:(ac|dc|both)[:：])?([A-Za-z\u4e00-\u9fa5]+)\s*[=:]\s*(.+)$/.exec(a);
      if (m) {
        const key = ALIAS[m[2].toLowerCase()] || m[2].toLowerCase();
        if (!key || (key !== 'saver' && !ITEMS[key])) throw new Error('未知的设置项：' + m[2]);
        opts.sets.push({ mode: (m[1] || 'both').toLowerCase(), key: key, raw: m[3].trim() });
      } else {
        throw new Error('无法识别的参数：' + a + '\n使用 ' + EXE_NAME + ' --help 查看用法');
      }
    }
  }
  return opts;
}

async function main() {
  let opts;
  try { opts = parseArgs(process.argv.slice(2)); }
  catch (e) { console.log('\n  [错误] ' + e.message + '\n'); process.exitCode = 1; return; }

  if (opts.action === 'help') { console.log(HELP); return; }

  if (opts.dynlockAction) {
    if (opts.dynlockAction === 'status') { console.log(printDynlock()); return; }
    const on = opts.dynlockAction === 'on';
    const r = setDynamicLock(on);
    console.log('\n  动态锁已' + (on ? '开启' : '关闭') + (r.ok ? '' : '（失败：' + r.error + '）'));
    console.log(printDynlock());
    if (!r.ok) process.exitCode = 1;
    return;
  }

  if (opts.setBrowser) {
    const exe = resolveBrowser(opts.setBrowser);
    fs.writeFileSync(CONFIG_FILE, '# 指定打开设置页面所用的浏览器（可用 edge / chrome / firefox，或写完整 exe 路径）\r\n' +
      (exe || opts.setBrowser) + '\r\n', 'utf8');
    console.log('\n  已保存浏览器设置：' + (exe || opts.setBrowser) +
      (exe ? '' : '\n  （注意：未能在系统中找到该浏览器，已原样保存，打开时可能失败）'));
    if (!opts.action && opts.sets.length === 0) return;
  }

  // 查看配置文件内容（不应用）
  if (opts.showFile !== null) {
    try {
      const r = readConfig(opts.showFile);
      const c = r.config;
      console.log('\n  配置文件：' + r.file);
      if (c.exportedAt) console.log('  导出时间：' + String(c.exportedAt).replace('T', ' ').slice(0, 19) + (c.computer ? '   来自电脑：' + c.computer : ''));
      if (c.scheme) console.log('  原电源计划：' + c.scheme.name);
      console.log('');
      for (const k of KEYS) {
        const v = c.items[k];
        if (!v) continue;
        console.log('  ' + pad(ITEMS[k].name, 16) + '接通电源 ' + fmt(v.ac) + '  使用电池 ' + fmt(v.dc));
      }
      const s = c.saver;
      if (s) console.log('  ' + pad('屏幕保护程序', 16) + (s.active ? fmt(s.timeout) : '已关闭'));
      console.log('');
    } catch (e) { console.log('\n  [错误] ' + e.message + '\n'); process.exitCode = 1; }
    return;
  }

  let didTask = false;

  // 导入配置（可与后面的设置项组合：先导入，再用命令行覆盖个别项）
  if (opts.importFile !== null) {
    try {
      const r = importConfig(opts.importFile);
      console.log('\n  已读取：' + r.file);
      console.log('  还原内容：');
      console.log(r.applied.results.join('\n'));
      if (!r.applied.ok) { console.log('\n  [部分失败] ' + r.applied.errors.join('；')); process.exitCode = 1; }
    } catch (e) { console.log('\n  [错误] ' + e.message + '\n'); process.exitCode = 1; }
    didTask = true;
  }

  // 命令行设置模式
  if (opts.sets.length) {
    const lines = [];
    for (const s of opts.sets) {
      try {
        if (s.key === 'saver') {
          let on, timeout = null;
          const v = s.raw.toLowerCase();
          if (/^(on|1|true|开|开启)$/.test(v)) on = true;
          else if (/^(off|0|false|关|关闭)$/.test(v)) on = false;
          else { on = true; timeout = parseVal(s.raw); }
          const r = setSaver(on, timeout, null);
          lines.push('  屏幕保护程序 -> ' + (on ? (timeout != null ? fmt(timeout) : '开启') : '关闭') + (r.ok ? '' : '   [失败] ' + r.errors.join('；')));
        } else {
          const sec = parseVal(s.raw);
          const r = setItem(s.key, sec, s.mode);
          const v = queryAll()[ITEMS[s.key].setting] || { ac: null, dc: null };
          const tag = s.mode === 'both' ? '' : '（' + (s.mode === 'ac' ? '仅接通电源' : '仅使用电池') + '）';
          lines.push('  ' + ITEMS[s.key].name + tag + ' -> ' + fmt(sec) +
            (s.mode === 'both' ? '' : '   当前交流=' + fmt(v.ac) + ' 直流=' + fmt(v.dc)) +
            (r.ok ? '' : '   [失败] ' + r.errors.join('；')));
        }
      } catch (e) {
        lines.push('  ' + s.key + ' -> [错误] ' + e.message);
        process.exitCode = 1;
      }
    }
    console.log('');
    console.log(lines.join('\n'));
    console.log('');
    didTask = true;
  }

  // 导出配置
  if (opts.exportFile !== null) {
    try {
      const r = exportConfig(opts.exportFile);
      console.log('\n  配置已导出到：' + r.file);
      console.log('  换电脑时把这个文件和 exe 一起拷过去，在新电脑上运行：');
      console.log('    ' + EXE_NAME + ' --import=' + path.basename(r.file));
      console.log('');
    } catch (e) { console.log('\n  [错误] ' + e.message + '\n'); process.exitCode = 1; }
    didTask = true;
  }

  if (didTask) return;

  if (opts.action === 'status') { console.log(printStatus()); return; }

  if (opts.action === 'defaults') {
    const r = runQuiet('powercfg', ['-restoredefaultschemes']);
    setSaver(false, 900, false);
    console.log('\n  电源计划已恢复为系统默认值：' + (r.ok ? '完成' : '失败 - ' + (r.out || r.err).trim()) + '\n');
    if (!r.ok) process.exitCode = 1;
    return;
  }

  if (opts.action === 'stop') {
    const r = await stopRunning();
    console.log('\n  后台服务已停止（' + r.via + '）\n');
    return;
  }

  // 服务模式
  KEEP_ALIVE = !!opts.keepAlive;
  log('启动服务模式 pid=' + process.pid + ' args=' + JSON.stringify(process.argv.slice(2)) + ' exeDir=' + APP_DIR);

  // 文件锁互斥：原子独占创建锁文件，比 HTTP 探测更可靠，杜绝两个实例同时启动的竞态
  const lock = acquireLock();
  log('文件锁: ' + (lock.ok ? '获取成功' : '已有实例, 端口=' + lock.port));
  if (!lock.ok) {
    const url = 'http://127.0.0.1:' + (lock.port || DEFAULT_PORT) + '/';
    if (!opts.noOpen) {
      const spec = opts.browser || readBrowserConfig();
      const r = openUrl(url, spec);
      log('打开浏览器(已有实例): ' + (r.ok ? '成功 via=' + r.via : '失败 err=' + r.error) + ' url=' + url);
      console.log('\n  服务已在运行（端口 ' + (lock.port || DEFAULT_PORT) + '），已打开页面' + (r.via ? '：' + r.via : '') + '\n');
    } else {
      console.log('\n  服务已在运行：' + url + '\n');
    }
    return;
  }

  const started = await startServer(opts.port || DEFAULT_PORT);
  updateLockPort(started.port); // 端口可能被占用顺延，回写实际端口到锁文件
  process.on('exit', releaseLock); // 进程退出时释放锁，避免残留锁文件
  log('服务已启动, 端口=' + started.port);

  // GUI 托管模式：--gui-pid 指明拉起本后端的桌面图形界面进程。
  // 若 GUI 被异常终止（崩溃 / 任务管理器结束），后端不再常驻，随 GUI 自动退出，
  // 避免「界面没了但后台进程还在」的残留（正常关窗仍走 /api/quit 立即退出）。
  if (opts.guiPid && opts.guiPid > 0) {
    const gpid = opts.guiPid;
    log('GUI 托管: 监听 GUI 进程 pid=' + gpid + '，进程消失后本服务自动退出');
    const gTimer = setInterval(() => {
      if (!isProcessAlive(gpid)) {
        clearInterval(gTimer);
        log('GUI 托管: GUI 进程 ' + gpid + ' 已退出，后端随之退出');
        try { process.exit(0); } catch (e) { }
      }
    }, 10000);
    if (gTimer.unref) gTimer.unref();
  }

  // 启动时自动修复动态锁配置
  autoRepairDynamicLock();
  const url = 'http://127.0.0.1:' + started.port + '/';
  console.log('');
  console.log('  锁屏·屏幕·睡眠 全套设置  v' + VERSION);
  console.log('  服务已启动：' + url);
  if (KEEP_ALIVE) {
    console.log('  常驻模式（--keep-alive）：关掉页面不会退出，需手动 --stop 或点页面右上角「退出」。');
  } else {
    console.log('  关掉设置页面约 10 秒后，程序会自动退出，不留后台进程。');
  }
  console.log('  服务进程无窗口运行；停止方式：' + EXE_NAME + ' --stop 或双击「停止后台.bat」。');
  console.log('');
  if (!opts.noOpen) {
    const spec = opts.browser || readBrowserConfig();
    const r = openUrl(url, spec);
    log('打开浏览器: ' + (r.ok ? '成功 via=' + r.via : '失败 err=' + r.error) + ' url=' + url);
    console.log('  已打开浏览器' + (r.via ? '：' + r.via : '') + (r.ok ? '' : '（打开失败：' + r.error + '）'));
    console.log('  若未自动打开，请手动访问 ' + url);
    console.log('');
  }
}

/* ---------------- 主从守护（看门狗） ----------------
 * 入口按 process.argv 中的 --worker 标志拆分主/从：
 *   - 主进程（无 --worker）：服务模式下只做守护，fork/spawn 子进程执行原有服务逻辑；
 *   - 子进程（有 --worker）：注册异常兜底后执行原有 main() 业务。
 * SEA 兼容：子进程统一通过 process.execPath 启动，不依赖磁盘源文件路径。
 */

const IS_WORKER = process.argv.includes('--worker');

// 判断是否"服务模式"（需要守护）。含一次性任务参数（命令行模式）则返回 false。
function isServiceMode(argv) {
  const ONCE_FLAG = /^(--help|-h|\/\?|help|--status|--list|-l|--stop|--quit|--exit|-q|--defaults|--reset|--dynlock)$/i;
  for (const a of argv) {
    const t = String(a == null ? '' : a).trim();
    if (!t) continue;
    if (ONCE_FLAG.test(t)) return false;
    if (/^--dynlock[=:]/i.test(t)) return false;
    if (/^--export([=:]|$)/i.test(t)) return false;
    if (/^--import([=:]|$)/i.test(t)) return false;
    if (/^--show([=:]|$)/i.test(t)) return false;
    if (/^--set-browser[=:]/i.test(t)) return false;
    if (/^(?:(ac|dc|both)[:：])?[A-Za-z\u4e00-\u9fa5]+\s*[=:]\s*.+$/.test(t)) return false;
  }
  return true;
}

// 启动一个业务子进程（主/从拆分）：
//   - 开发态：child_process.fork 以本文件为入口，自带 IPC 通道；
//   - SEA 态：fork 无法引用磁盘模块路径，统一经 process.execPath + --worker 重入入口，
//     由 argv 里的 --worker 标志切到子进程角色，保证与源码运行行为一致。
function spawnWorker(workerArgs) {
  if (IS_SEA) {
    // SEA：fork 无法引用磁盘模块路径，统一经 process.execPath + --worker 重入入口。
    // 子进程 stdout/stderr 用 pipe 转发而非 inherit，避免继承父进程（可能已失效的）控制台句柄，
    // 防止在部分宿主环境（如无控制台的守护/后台启动）下写输出导致子进程异常退出。
    const child = spawn(process.execPath, ['--worker'].concat(workerArgs), {
      stdio: ['ignore', 'pipe', 'pipe'],
      windowsHide: true
    });
    if (child.stdout) child.stdout.on('data', (d) => { try { process.stdout.write(d); } catch (e) { } });
    if (child.stderr) child.stderr.on('data', (d) => { try { process.stderr.write(d); } catch (e) { } });
    return child;
  }
  // 开发态：child_process.fork 拆分主/从，保留 IPC 通道（stdio 第 4 项 'ipc'）
  return fork(__filename, ['--worker'].concat(workerArgs), {
    stdio: ['ignore', 'inherit', 'inherit', 'ipc'],
    windowsHide: true
  });
}

// 主进程守护循环
function runSupervisor(workerArgs) {
  const CRASH_WINDOW_MS = 30000;   // 崩溃统计窗口
  const MAX_CRASH = 5;             // 窗口内超过该次数触发防重启风暴
  const RESTART_BASE_MS = 1000;    // 首次重启延迟
  const RESTART_MAX_MS = 3000;     // 重启延迟封顶（满足"3 秒内"）

  let child = null;
  const crashTimes = [];
  let shuttingDown = false;

  function start() {
    child = spawnWorker(workerArgs);
    log('守护: 启动业务子进程 pid=' + child.pid + '（经 process.execPath 启动）');
    child.on('exit', (code, signal) => {
      if (shuttingDown) return;
      const crashed = (code !== 0) || (signal != null);
      if (!crashed) {
        // 正常退出（exit 0，如关页面自动退出）：服务正常结束，主进程随之退出
        log('守护: 业务子进程正常退出 (code=0)，主进程退出');
        process.exit(0);
        return;
      }
      const now = Date.now();
      while (crashTimes.length && now - crashTimes[0] > CRASH_WINDOW_MS) crashTimes.shift();
      crashTimes.push(now);
      log('守护: 检测到业务子进程崩溃 code=' + code + ' signal=' + signal +
          '（' + (CRASH_WINDOW_MS / 1000) + ' 秒内第 ' + crashTimes.length + ' 次）');
      if (crashTimes.length >= MAX_CRASH) {
        log('守护: 连续崩溃达 ' + crashTimes.length + ' 次，触发防重启风暴保护，停止自动重启');
        process.exit(code || 1);
        return;
      }
      const delay = Math.min(RESTART_MAX_MS, RESTART_BASE_MS * Math.pow(2, crashTimes.length - 1));
      log('守护: ' + (delay / 1000) + ' 秒后重启业务子进程');
      setTimeout(start, delay);
    });
  }

  function shutdown() {
    if (shuttingDown) return;
    shuttingDown = true;
    log('守护: 收到退出信号，优雅停止业务子进程 pid=' + (child ? child.pid : '?'));
    if (child) {
      child.removeAllListeners('exit');
      try { child.kill('SIGTERM'); } catch (e) { }
    }
    setTimeout(() => { process.exit(0); }, 2000);
  }
  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);

  // 守护主进程自身的异常兜底：打印带时间/PID/完整堆栈的日志后保持存活，
  // 避免守护进程因偶发异常退出导致整个看门狗失效。
  function supervisorErrLog(kind, err) {
    const ts = new Date().toLocaleString('zh-CN', { hour12: false });
    const detail = err && err.stack ? err.stack : String(err);
    log('[守护 pid=' + process.pid + ' ' + ts + '] ' + kind + '（守护保持存活）:\n' + detail);
    console.error('[守护 pid=' + process.pid + '] ' + kind + '（保持存活）:', err);
  }
  process.on('uncaughtException', (err) => supervisorErrLog('uncaughtException', err));
  process.on('unhandledRejection', (reason) => supervisorErrLog('unhandledRejection', reason));

  start();
}

// 异常兜底：打印带时间/PID/完整堆栈的错误日志后保持进程存活
function installWorkerSafetyNets() {
  function errLog(kind, err) {
    const ts = new Date().toLocaleString('zh-CN', { hour12: false });
    const detail = err && err.stack ? err.stack : String(err);
    log('[WORKER pid=' + process.pid + ' ' + ts + '] ' + kind + '（进程保持存活）:\n' + detail);
    console.error('[WORKER pid=' + process.pid + '] ' + kind + '（保持存活）:', err);
  }
  process.on('uncaughtException', (err) => errLog('uncaughtException', err));
  process.on('unhandledRejection', (reason) => errLog('unhandledRejection', reason));
}

function runBusiness() {
  main().catch(e => {
    const msg = (e && e.message ? e.message : e);
    log('启动错误: ' + msg + (e && e.stack ? ' | ' + String(e.stack).split('\n')[1] : ''));
    console.log('\n  [错误] ' + msg + '\n');
    process.exitCode = 1;
    // worker（服务模式）下启动失败必须真正退出，让守护进程据此判定崩溃并重启；
    // 命令行一次性任务则交由自然退出（exitCode 已设为 1）。
    if (IS_WORKER) process.exit(1);
  });
}

/* 桌面化入口：无任何参数（典型双击场景，含「启动锁屏设置.vbs」）且同目录存在桌面版
 * LockscreenGui.exe 时，转交桌面图形界面并退出自身；桌面版缺失时回退原网页模式。 */
function launchGuiAndExit() {
  // 新布局：GUI 在 LockscreenGui 子目录；兼容旧布局：与主程序同目录
  const gui = [path.join(APP_DIR, 'LockscreenGui', 'LockscreenGui.exe'), path.join(APP_DIR, 'LockscreenGui.exe')]
    .find(p => fs.existsSync(p));
  if (!gui) return false;
  log('无参数启动: 检测到桌面版，转交 ' + gui);
  try {
    // 注意：不能带 windowsHide:true —— 那会把 STARTUPINFO.wShowWindow 设为 SW_HIDE，
    // 使桌面版主窗口首次显示即被隐藏（窗口句柄为 0）。桌面版有自身窗口，无需隐藏。
    const p = spawn(gui, [], { detached: true, stdio: 'ignore' });
    p.unref();
    log('桌面版已拉起 pid=' + p.pid + '，主进程随即退出');
    // 给 GUI 一点启动时间，随后退出自身，避免与 GUI 拉起的后端实例争抢文件锁/端口
    setTimeout(() => { try { process.exit(0); } catch (e) { } }, 1200);
    return true;
  } catch (e) {
    log('拉起桌面版失败: ' + e.message + '，回退网页模式');
    return false;
  }
}

/* 入口分派 */
if (IS_WORKER) {
  // 子进程：异常兜底 + 执行原有业务（去掉 --worker 标志，保证 main() 解析参数正确）
  installWorkerSafetyNets();
  process.argv = process.argv.filter(a => a !== '--worker');
  runBusiness();
} else {
  const rawArgs = process.argv.slice(2);
  if (rawArgs.length === 0 && launchGuiAndExit()) {
    // 已转交桌面图形界面，主进程稍后自行退出（不再进入服务模式 / 网页模式）
  } else if (isServiceMode(rawArgs)) {
    // 主进程 + 服务模式：只做守护
    log('守护: 主进程启动 pid=' + process.pid + '（服务模式，进入看门狗守护）');
    runSupervisor(rawArgs);
  } else {
    // 主进程 + 命令行模式：直接执行业务（一次性任务，不守护）
    runBusiness();
  }
}

/* ---------------- 页面 ---------------- */

function htmlPage(port) {
  return '<!DOCTYPE html>\n<html lang="zh-CN">\n<head>\n<meta charset="utf-8">\n' +
    '<meta name="viewport" content="width=device-width,initial-scale=1">\n' +
    '<title>锁屏·屏幕·睡眠 全套设置</title>\n<style>\n' + CSS + '\n</style>\n</head>\n<body>\n' +
    '<div class="wrap">\n' +
    '  <header>\n' +
    '    <div class="ttl"><span class="logo">🔒</span>\n' +
    '      <div><h1>锁屏·屏幕·睡眠 全套设置</h1>\n' +
    '      <p class="sub">全部项目都可设为「永不」，彻底取消自动黑屏 / 锁屏 / 睡眠。「接通电源」与「使用电池」是两套独立的值。</p></div>\n' +
    '    </div>\n' +
    '    <div class="tools">\n' +
    '      <button class="icon" id="btn-theme" title="切换主题">🌓</button>\n' +
    '      <button class="icon" id="btn-refresh" title="重新读取">⟳</button>\n' +
    '      <button class="icon danger" id="btn-quit" title="退出后台程序">⏻</button>\n' +
    '    </div>\n' +
    '  </header>\n' +
    '  <div class="bar" id="bar">\n' +
    '    <span class="chip">⚙️ <b id="scheme">读取中…</b></span>\n' +
    '    <span class="chip">🌐 端口 <b>' + port + '</b></span>\n' +
    '    <span class="chip" id="chip-hib">💤 休眠</span>\n' +
    '  </div>\n' +
    '  <div id="app"><div class="loading">正在读取当前系统设置…</div></div>\n' +
    '  <div class="card" id="card-browser">\n' +
    '    <div class="hd"><span>🌐 打开页面用的浏览器</span></div>\n' +
    '    <div class="ds">默认用系统默认浏览器。改完之后，下次启动程序就会用你选的浏览器打开设置页面。</div>\n' +
    '    <div class="cst">\n' +
    '      <select id="sel-browser"></select>\n' +
    '      <button id="btn-save-browser">保存</button>\n' +
    '      <span class="hint" id="browser-cur">读取中…</span>\n' +
    '    </div>\n' +
    '  </div>\n' +
    '  <div class="foot">\n' +
    '    <button class="big ok" id="btn-never">🔓 一键全部设为「永不」</button>\n' +
    '    <button class="big warn" id="btn-defaults">↩ 恢复系统默认</button>\n' +
    '  </div>\n' +
    '  <div class="foot">\n' +
    '    <button class="big ghost" id="btn-export">📤 导出配置（存成 JSON）</button>\n' +
    '    <button class="big ghost" id="btn-import">📥 导入配置（从 JSON 还原）</button>\n' +
    '  </div>\n' +
    '  <input type="file" id="file-import" accept=".json,application/json" style="display:none">\n' +
    '  <p class="tip">设置会立即写入当前电源计划并生效。屏保为当前用户注册表项，修改后已广播通知系统。<br>\n' +
    '  导出 / 导入配置不需要管理员权限，适合换电脑或重装系统后恢复。</p>\n' +
    '</div>\n<div class="toast" id="toast"></div>\n<script>\n' + CLIENT_JS + '\n</script>\n</body>\n</html>';
}

const CSS = [
  ':root{',
  '  --bg:#f1f5f9; --card:#ffffff; --text:#1f2937; --muted:#6b7280; --border:#e2e8f0;',
  '  --btn:#f1f5f9; --btn-h:#e2e8f0; --accent:#f59e0b; --accent-soft:#fef3c7; --accent-text:#78350f;',
  '  --ok:#16a34a; --danger:#dc2626; --shadow:0 1px 3px rgba(15,23,42,.08),0 8px 24px rgba(15,23,42,.05);',
  '}',
  '[data-theme="dark"]{',
  '  --bg:#0f172a; --card:#1e293b; --text:#e2e8f0; --muted:#94a3b8; --border:#334155;',
  '  --btn:#334155; --btn-h:#475569; --accent:#fbbf24; --accent-soft:#78350f; --accent-text:#fde68a;',
  '  --shadow:0 1px 3px rgba(0,0,0,.3);',
  '}',
  '*{box-sizing:border-box;margin:0;padding:0}',
  'body{font-family:"Segoe UI","Microsoft YaHei",system-ui,sans-serif;background:var(--bg);color:var(--text);min-height:100vh;padding:22px 16px 40px;transition:background .2s,color .2s}',
  '.wrap{max-width:760px;margin:0 auto}',
  'header{display:flex;align-items:flex-start;justify-content:space-between;gap:12px;margin-bottom:14px}',
  '.ttl{display:flex;gap:12px;align-items:flex-start}',
  '.logo{width:42px;height:42px;border-radius:12px;background:linear-gradient(135deg,#f59e0b,#fbbf24);display:inline-flex;align-items:center;justify-content:center;font-size:22px;flex:none}',
  'h1{font-size:20px;font-weight:700;line-height:1.3}',
  '.sub{color:var(--muted);font-size:12.5px;margin-top:5px;line-height:1.6;max-width:52em}',
  '.tools{display:flex;gap:6px;flex:none}',
  'button{font-family:inherit;cursor:pointer;border:1px solid var(--border);background:var(--btn);color:var(--text);border-radius:9px;transition:.15s;font-size:13px}',
  'button:hover{background:var(--btn-h)}',
  '.icon{width:34px;height:34px;font-size:15px;display:inline-flex;align-items:center;justify-content:center}',
  '.icon.danger:hover{background:var(--danger);color:#fff;border-color:var(--danger)}',
  '.bar{display:flex;flex-wrap:wrap;gap:8px;margin-bottom:14px}',
  '.chip{background:var(--card);border:1px solid var(--border);border-radius:999px;padding:6px 13px;font-size:12px;color:var(--muted);box-shadow:var(--shadow)}',
  '.chip b{color:var(--text);font-weight:600}',
  '.card{background:var(--card);border:1px solid var(--border);border-radius:14px;padding:16px;margin-bottom:12px;box-shadow:var(--shadow)}',
  '.card .hd{display:flex;align-items:center;gap:8px;font-size:15px;font-weight:600}',
  '.card .ds{color:var(--muted);font-size:12px;line-height:1.6;margin:6px 0 12px}',
  '.mode{border:1px solid var(--border);border-radius:11px;padding:11px;margin-bottom:8px;background:transparent}',
  '.mode:last-of-type{margin-bottom:0}',
  '.mhd{display:flex;justify-content:space-between;align-items:center;font-size:12.5px;margin-bottom:9px}',
  '.mhd .lb{color:var(--muted)}',
  '.mhd .cur{font-weight:700;color:var(--accent);font-variant-numeric:tabular-nums}',
  '.btns{display:flex;flex-wrap:wrap;gap:6px}',
  '.btns button{padding:6px 11px;border-radius:8px}',
  '.btns button.on{background:var(--accent);border-color:var(--accent);color:#1f2937;font-weight:700}',
  '.btns button.never{color:var(--accent);font-weight:600;border-color:var(--accent)}',
  '.cst{display:flex;align-items:center;gap:6px;margin-top:9px;flex-wrap:wrap}',
  '.cst input{width:96px;padding:6px 9px;border-radius:8px;border:1px solid var(--border);background:var(--bg);color:var(--text);font-family:inherit;font-size:13px}',
  '.cst input:focus{outline:2px solid var(--accent);outline-offset:-1px}',
  '.cst button{padding:6px 11px;border-radius:8px}',
  '.cst .hint{color:var(--muted);font-size:11.5px}',
  '.cst select{padding:6px 9px;border-radius:8px;border:1px solid var(--border);background:var(--bg);color:var(--text);font-family:inherit;font-size:13px;max-width:230px}',
  '.cst select:focus{outline:2px solid var(--accent);outline-offset:-1px}',
  '.saver{display:flex;align-items:center;gap:8px;flex-wrap:wrap}',
  '.warnbox{margin-top:10px;font-size:12px;color:var(--muted);background:var(--btn);border-radius:8px;padding:8px 11px;line-height:1.6}',
  '.dl-stats{margin-top:12px;display:flex;flex-direction:column;gap:7px}',
  '.dl-row{display:flex;align-items:center;gap:9px;font-size:12.5px}',
  '.dl-dot{width:8px;height:8px;border-radius:50%;flex:none;background:var(--muted)}',
  '.dl-dot.ok{background:#16a34a}',
  '.dl-dot.bad{background:#dc2626}',
  '.dl-row .dl-val{margin-left:auto;font-weight:600}',
  '.dl-val.ok{color:#16a34a}',
  '.dl-val.bad{color:#dc2626}',
  '.foot{display:flex;gap:10px;margin-top:6px;flex-wrap:wrap}',
  '.big{flex:1;min-width:210px;padding:14px;border-radius:12px;font-size:14.5px;font-weight:700;border:none;color:#fff}',
  '.big.ok{background:linear-gradient(135deg,#22c55e,#16a34a)}',
  '.big.warn{background:linear-gradient(135deg,#64748b,#475569)}',
  '.big.ghost{background:transparent;border:1px solid var(--border);color:var(--text);font-weight:600}',
  '.big.ghost:hover{background:var(--btn-h)}',
  '.big:hover{filter:brightness(1.07)}',
  '.tip{color:var(--muted);font-size:11.5px;margin-top:14px;line-height:1.7}',
  '.loading{color:var(--muted);font-size:13px;padding:30px;text-align:center}',
  '.toast{position:fixed;top:18px;left:50%;transform:translateX(-50%) translateY(-14px);background:#16a34a;color:#fff;padding:10px 20px;border-radius:10px;font-size:13.5px;opacity:0;transition:.25s;pointer-events:none;z-index:99;box-shadow:0 8px 22px rgba(0,0,0,.18)}',
  '.toast.show{opacity:1;transform:translateX(-50%) translateY(0)}',
  '.toast.err{background:#dc2626}',
  '@media (max-width:520px){ .cst input{width:78px} h1{font-size:18px} }'
].join('\n');

const CLIENT_JS = [
  "var PRESETS=[[0,'永不'],[60,'1分'],[180,'3分'],[300,'5分'],[600,'10分'],[900,'15分'],[1800,'30分'],[3600,'1小时'],[7200,'2小时']];",
  "var SAVER_PRESETS=[[60,'1分'],[300,'5分'],[600,'10分'],[900,'15分'],[1800,'30分'],[3600,'1小时']];",
  "var S=null;",
  "var DL=null;",
  "function $(id){return document.getElementById(id)}",
  "var tt;",
  "function show(m,err){var t=$('toast');t.textContent=m;t.className='toast show'+(err?' err':'');clearTimeout(tt);tt=setTimeout(function(){t.className='toast'+(err?' err':'')},2400)}",
  "function api(path,data){",
  "  return fetch(path,{method:data?'POST':'GET',headers:data?{'Content-Type':'application/json'}:{},body:data?JSON.stringify(data):undefined})",
  "    .then(function(r){return r.json()})",
  "    .then(function(j){ if(j&&j.ok===false&&j.error) show('操作失败：'+j.error,true); return j; });",
  "}",
  "function esc(s){return String(s==null?'':s).replace(/[&<>]/g,function(c){return {'&':'&amp;','<':'&lt;','>':'&gt;'}[c]})}",
  "function modeRow(k,it,mode,label,val){",
  "  var on=val===0?' never':'';",
  "  var h='<div class=\"mode\"><div class=\"mhd\"><span class=\"lb\">'+label+'</span>'+",
  "    '<span class=\"cur\" id=\"cur-'+k+'-'+mode+'\">'+esc(it[mode+'Text'])+'</span></div><div class=\"btns\" id=\"btns-'+k+'-'+mode+'\">';",
  "  for(var i=0;i<PRESETS.length;i++){",
  "    var p=PRESETS[i];",
  "    h+='<button class=\"'+(p[0]===val?'on':'')+(p[0]===0?' never':'')+'\" data-act=\"preset\" data-k=\"'+k+'\" data-m=\"'+mode+'\" data-sec=\"'+p[0]+'\">'+p[1]+'</button>';",
  "  }",
  "  h+='</div><div class=\"cst\"><input type=\"number\" min=\"0\" step=\"1\" id=\"inp-'+k+'-'+mode+'\" placeholder=\"自定义/分钟\">'+",
  "    '<button data-act=\"custom\" data-k=\"'+k+'\" data-m=\"'+mode+'\">应用</button>'+",
  "    '<button data-act=\"sync\" data-k=\"'+k+'\" data-m=\"'+mode+'\" title=\"把这个值同时用到另一项\">↕ 同步两项</button>'+",
  "    '<span class=\"hint\">填 0 = 永不</span></div></div>';",
  "  return h;",
  "}",
  "function cardHtml(k,it){",
  "  var h='<div class=\"card\"><div class=\"hd\"><span>'+it.icon+' '+esc(it.name)+'</span></div><div class=\"ds\">'+esc(it.desc)+'</div>';",
  "  h+=modeRow(k,it,'ac','🔌 接通电源',it.ac);",
  "  if(S.hasBattery) h+=modeRow(k,it,'dc','🔋 使用电池',it.dc);",
  "  if(k==='hibernate'&&!S.hibernateAvailable) h+='<div class=\"warnbox\">⚠️ 本机未开启休眠功能（未找到 hiberfil.sys），此项设置不会生效。如需使用，请以管理员身份运行 <b>powercfg /h on</b> 开启休眠。</div>';",
  "  return h+'</div>';",
  "}",
  "function saverHtml(sv){",
  "  var h='<div class=\"card\"><div class=\"hd\"><span>🖼️ 屏幕保护程序</span></div>'+",
  "    '<div class=\"ds\">屏保恢复时若勾选「显示登录屏幕」也会导致锁屏，不需要就直接关掉。</div>'+",
  "    '<div class=\"saver\"><button class=\"'+(sv.active?'':'on')+'\" data-act=\"saver\" data-on=\"0\">关闭</button>'+",
  "    '<button class=\"'+(sv.active?'on':'')+'\" data-act=\"saver\" data-on=\"1\">开启</button>'+",
  "    '<span class=\"hint\" style=\"color:var(--muted);font-size:12px;margin-left:6px\">当前：'+(sv.active?esc(sv.timeout)+' 秒后启动':'已关闭')+'</span></div>';",
  "  if(sv.active){",
  "    h+='<div class=\"btns\" style=\"margin-top:10px\">';",
  "    for(var i=0;i<SAVER_PRESETS.length;i++){var p=SAVER_PRESETS[i];",
  "      h+='<button class=\"'+(p[0]===sv.timeout?'on':'')+'\" data-act=\"saverTime\" data-sec=\"'+p[0]+'\">'+p[1]+'</button>';}",
  "    h+='</div>';",
  "  }",
  "  h+='<div class=\"warnbox\">屏保开关写在当前用户注册表，改完已通知系统刷新；个别情况下需注销后完全生效。</div>';",
  "  return h+'</div>';",
  "}",
  "function statRow(label,ok,okText,badText){",
  "  return '<div class=\"dl-row\"><span class=\"dl-dot '+(ok?'ok':'bad')+'\"></span><span>'+esc(label)+'</span><span class=\"dl-val '+(ok?'ok':'bad')+'\">'+esc(ok?okText:badText)+'</span></div>';",
  "}",
  "function dynlockHtml(dl){",
  "  if(!dl){ return '<div class=\"card\"><div class=\"hd\"><span>📱 动态锁（离开自动锁屏）</span></div><div class=\"ds\">正在检测…</div></div>'; }",
  "  var h='<div class=\"card\"><div class=\"hd\"><span>📱 动态锁（离开自动锁屏）</span></div>'+",
  "    '<div class=\"ds\">利用已配对的手机蓝牙信号：你带着手机离开电脑后自动锁屏。前提是蓝牙已配对手机，并在系统设置里勾选该手机为信任设备。</div>'+",
  "    '<div class=\"saver\"><button class=\"'+(dl.enabled?'on':'')+'\" data-act=\"dynlock\" data-on=\"0\">关闭</button>'+",
  "    '<button class=\"'+(dl.enabled?'on':'')+'\" data-act=\"dynlock\" data-on=\"1\">开启</button>'+",
  "    '<span class=\"hint\" style=\"color:var(--muted);font-size:12px;margin-left:6px\">当前：'+(dl.enabled?'已开启':'已关闭')+'</span></div>';",
  "  var rs='';",
  "  rs+=statRow('动态锁开关', dl.enabled, '已开启', '已关闭');",
  "  rs+=statRow('信任设备', dl.deviceSelected, dl.selectedName || dl.selectedMac || '已选择', '未选择');",
  "  rs+=statRow('蓝牙密钥权限', dl.keysHealthy==='healthy', '正常', (dl.keysHealthy==='unknown'?'无法检测':'异常'));",
  "  h+='<div class=\"dl-stats\">'+rs+'</div>';",
  "  h+='<div class=\"btns\" style=\"margin-top:12px\">'+",
  "    '<button data-act=\"dynlock-open\" data-target=\"bluetooth\">🔗 打开蓝牙设置</button>'+",
  "    '<button data-act=\"dynlock-open\" data-target=\"dynamiclock\">🔗 打开动态锁设置</button></div>';",
  "  if(dl.pairedCount===0) h+='<div class=\"warnbox\">⚠️ 未检测到已配对的手机。请点「打开蓝牙设置」先配对手机，再回来勾选信任设备。</div>';",
  "  else if(!dl.deviceSelected) h+='<div class=\"warnbox\">⚠️ 手机已配对，但还没勾选信任设备。请点「打开动态锁设置」勾选你的手机。</div>';",
  "  if(dl.keysHealthy==='broken') h+='<div class=\"warnbox\">⚠️ 蓝牙密钥权限异常（可能被某些清理软件删掉），会导致动态锁失效。</div>';",
  "  return h+'</div>';",
  "}",
  "function render(st){",
  "  S=st;",
  "  $('scheme').textContent=st.scheme.name;",
  "  var ch=$('chip-hib');",
  "  ch.innerHTML=(st.hibernateAvailable?'💤 休眠已开启':'💤 休眠未开启');",
  "  ch.style.opacity=st.hibernateAvailable?'1':'.75';",
  "  var h='';",
  "  var ks=['lock','display','sleep','hibernate'];",
  "  for(var i=0;i<ks.length;i++) h+=cardHtml(ks[i],st.items[ks[i]]);",
  "  h+=saverHtml(st.saver);",
  "  h+='<div id=\"card-dynlock\">'+dynlockHtml(DL)+'</div>';",
  "  $('app').innerHTML=h;",
  "}",
  "function refresh(){ return fetch('/api/state').then(function(r){return r.json()}).then(render); }",
  "function refreshDynlock(){ return fetch('/api/dynlock').then(function(r){return r.json()}).then(function(dl){ DL=dl; var el=$('card-dynlock'); if(el) el.innerHTML=dynlockHtml(dl); }).catch(function(){}); }",
  "function updateRow(k,mode,sec,text){",
  "  var cur=$('cur-'+k+'-'+mode); if(cur) cur.textContent=text;",
  "  var box=$('btns-'+k+'-'+mode); if(!box) return;",
  "  var bs=box.getElementsByTagName('button');",
  "  for(var i=0;i<bs.length;i++){ var v=parseInt(bs[i].getAttribute('data-sec'),10);",
  "    bs[i].className=(v===sec?'on':'')+(v===0?' never':''); }",
  "  if(S&&S.items[k]){ S.items[k][mode]=sec; S.items[k][mode+'Text']=text; }",
  "}",
  "function setVal(k,mode,sec){",
  "  return api('/api/set',{key:k,sec:sec,mode:mode}).then(function(j){",
  "    if(!j||j.error) return;",
  "    if(j.ac!=null) updateRow(k,'ac',j.ac,j.acText);",
  "    if(j.dc!=null&&S.hasBattery) updateRow(k,'dc',j.dc,j.dcText);",
  "    show('✅ '+j.name+'（'+(mode==='ac'?'接通电源':mode==='dc'?'使用电池':'两项')+'）已设为 '+j.text);",
  "  });",
  "}",
  "document.addEventListener('click',function(e){",
  "  var b=e.target.closest?e.target.closest('button'):null; if(!b) return;",
  "  var act=b.getAttribute('data-act'); if(!act) return;",
  "  var k=b.getAttribute('data-k'), m=b.getAttribute('data-m');",
  "  if(act==='preset'){ setVal(k,m,parseInt(b.getAttribute('data-sec'),10)); }",
  "  else if(act==='custom'||act==='sync'){",
  "    var inp=$('inp-'+k+'-'+m); var v=parseInt(inp.value,10);",
  "    if(isNaN(v)||v<0){ show('请输入 0 或正整数（分钟）',true); return; }",
  "    var sec=v*60;",
  "    if(act==='sync'){ setVal(k,'both',sec).then(function(){ inp.value=''; }); }",
  "    else { setVal(k,m,sec).then(function(){ inp.value=''; }); }",
  "  }",
  "  else if(act==='saver'){",
  "    api('/api/saver',{on:b.getAttribute('data-on')==='1'}).then(function(j){",
  "      if(j&&j.saver){ S.saver=j.saver; render(S); show('✅ 屏保已'+(j.saver.active?'开启':'关闭')); }",
  "    });",
  "  }",
  "  else if(act==='saverTime'){",
  "    api('/api/saver',{on:true,timeout:parseInt(b.getAttribute('data-sec'),10)}).then(function(j){",
  "      if(j&&j.saver){ S.saver=j.saver; render(S); show('✅ 屏保时间已设为 '+j.saver.timeout+' 秒'); }",
  "    });",
  "  }",
  "  else if(act==='dynlock'){",
  "    api('/api/dynlock',{on:b.getAttribute('data-on')==='1'}).then(function(j){",
  "      if(j&&j.dynlock){ DL=j.dynlock; refreshDynlock(); show('✅ 动态锁已'+(j.dynlock.enabled?'开启':'关闭')); }",
  "    });",
  "  }",
  "  else if(act==='dynlock-open'){",
  "    api('/api/dynlock',{open:b.getAttribute('data-target')}).then(function(j){",
  "      if(j&&j.ok){ show('已打开系统设置，请在其中操作'); } else { show('打开失败，请手动到 设置→账户→登录选项', true); }",
  "    });",
  "  }",
  "});",
  "$('btn-refresh').onclick=function(){ refresh().then(function(){show('已重新读取')}); refreshDynlock(); };",
  "$('btn-export').onclick=function(){",
  "  fetch('/api/export').then(function(r){return r.json()}).then(function(cfg){",
  "    var blob=new Blob([JSON.stringify(cfg,null,2)],{type:'application/json;charset=utf-8'});",
  "    var a=document.createElement('a');",
  "    a.href=URL.createObjectURL(blob); a.download='锁屏设置.json';",
  "    document.body.appendChild(a); a.click();",
  "    setTimeout(function(){ URL.revokeObjectURL(a.href); a.remove(); },800);",
  "    show('✅ 配置已导出为「锁屏设置.json」');",
  "  });",
  "};",
  "$('btn-import').onclick=function(){ $('file-import').click(); };",
  "$('file-import').onchange=function(e){",
  "  var f=e.target.files&&e.target.files[0]; if(!f) return;",
  "  var rd=new FileReader();",
  "  rd.onload=function(){",
  "    var cfg;",
  "    try{ cfg=JSON.parse(rd.result); }catch(err){ show('这个文件不是有效的 JSON',true); return; }",
  "    if(!cfg||!cfg.items){ show('这不像是本工具导出的配置文件（缺少 items）',true); return; }",
  "    api('/api/import',{config:cfg}).then(function(j){",
  "      if(j&&j.state){ render(j.state); show('✅ 配置已导入并应用'); }",
  "    });",
  "  };",
  "  rd.readAsText(f,'utf-8'); e.target.value='';",
  "};",
  "$('btn-never').onclick=function(){",
  "  var ks=['lock','display','sleep','hibernate'],i=0;",
  "  (function next(){",
  "    if(i>=ks.length){ api('/api/saver',{on:false}).then(function(j){ if(j&&j.saver) S.saver=j.saver; refresh().then(function(){show('✅ 已全部设为「永不」')}); }); return; }",
  "    var k=ks[i++]; api('/api/set',{key:k,sec:0,mode:'both'}).then(next);",
  "  })();",
  "};",
  "$('btn-defaults').onclick=function(){",
  "  if(!confirm('将把电源计划恢复为系统默认值（当前所有自定义时间都会丢失），继续？')) return;",
  "  api('/api/defaults').then(function(j){ if(j&&j.state) render(j.state); show('✅ 已恢复系统默认'); });",
  "};",
  "$('btn-quit').onclick=function(){",
  "  if(!confirm('退出后台程序？退出后本页将失效，重新双击 exe 即可再次启动。')) return;",
  "  fetch('/api/quit',{method:'POST'}).then(function(){ document.body.innerHTML='<div class=\"loading\">程序已退出，可以关闭此页。<br><br>重新双击「'+S.exe+'」即可再次启动。</div>'; });",
  "};",
  "$('btn-theme').onclick=function(){",
  "  var cur=document.documentElement.getAttribute('data-theme');",
  "  if(!cur) cur=(window.matchMedia&&window.matchMedia('(prefers-color-scheme: dark)').matches)?'dark':'light';",
  "  var next=cur==='dark'?'light':'dark';",
  "  document.documentElement.setAttribute('data-theme',next);",
  "  try{ localStorage.setItem('lockdelay-theme',next); }catch(e){}",
  "};",
  "(function(){ try{ var t=localStorage.getItem('lockdelay-theme'); if(t) document.documentElement.setAttribute('data-theme',t); }catch(e){} })();",
  "function loadBrowsers(){",
  "  return fetch('/api/browser').then(function(r){return r.json()}).then(function(info){",
  "    var sel=$('sel-browser'); if(!sel) return;",
  "    sel.innerHTML='';",
  "    info.list.forEach(function(b){",
  "      if(!b.installed) return;",
  "      var o=document.createElement('option');",
  "      o.value=b.value; o.setAttribute('data-path', b.path||'');",
  "      o.textContent=b.name;",
  "      sel.appendChild(o);",
  "    });",
  "    var cur=info.current, res=info.resolved, found=false;",
  "    for(var i=0;i<sel.options.length;i++){",
  "      var o=sel.options[i];",
  "      if(o.value===cur || (res && o.getAttribute('data-path')===res)){ sel.selectedIndex=i; found=true; break; }",
  "    }",
  "    if(!found && cur){",
  "      var o2=document.createElement('option'); o2.value=cur;",
  "      o2.textContent='自定义：'+cur;",
  "      sel.appendChild(o2); sel.selectedIndex=sel.options.length-1;",
  "    }",
  "    $('browser-cur').textContent = res ? ('当前：'+res) : '当前：系统默认浏览器';",
  "  });",
  "}",
  "$('btn-save-browser').onclick=function(){",
  "  api('/api/browser',{value:$('sel-browser').value}).then(function(j){",
  "    if(j&&j.ok){ loadBrowsers().then(function(){",
  "      show(j.fallback ? '已保存，但没找到这个浏览器，将退回系统默认' : '✅ 已保存，下次启动生效');",
  "    }); }",
  "  });",
  "};",
  "loadBrowsers();",
  "if(window.EventSource){ try{ new EventSource('/api/events'); }catch(e){} }",
  "refresh().catch(function(e){ $('app').innerHTML='<div class=\"loading\">读取失败：'+e.message+'</div>'; });",
  "refreshDynlock();"
].join('\n');

