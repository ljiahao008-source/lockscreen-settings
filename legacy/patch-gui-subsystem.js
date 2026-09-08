'use strict';
/*
 * 将 SEA 打包后的 exe 从 Console 子系统改为 Windows GUI 子系统，
 * 消除双击启动时的黑色控制台窗口（桌面化改造的一环）。
 *
 * 用法: node patch-gui-subsystem.js <exe路径>
 *   Console(3) -> Windows GUI(2)，只改 PE 头里的 Subsystem 字段，不动其它任何字节。
 */
const fs = require('fs');

const file = process.argv[2];
if (!file) { console.error('用法: node patch-gui-subsystem.js <exe路径>'); process.exit(2); }
if (!fs.existsSync(file)) { console.error('文件不存在: ' + file); process.exit(2); }

const fd = fs.openSync(file, 'r+');
try {
  const head = Buffer.alloc(64);
  fs.readSync(fd, head, 0, 64, 0);
  const peOff = head.readUInt32LE(0x3c); // e_lfanew -> PE 头偏移

  const sig = Buffer.alloc(4);
  fs.readSync(fd, sig, 0, 4, peOff);
  if (sig.toString('latin1') !== 'PE\u0000\u0000') { console.error('[patch] 不是有效的 PE 文件'); process.exit(3); }

  const optOff = peOff + 24; // Optional header 起点（COFF 头 20 字节）
  // Subsystem 字段偏移：PE32 与 PE32+ 均为 0x44（ImageBase 的 8 字节与
  // BaseOfData+ImageBase 的 8 字节恰好对齐，后续字段完全一致），无需区分。
  const subOff = optOff + 0x44;

  const cur = Buffer.alloc(2);
  fs.readSync(fd, cur, 0, 2, subOff);
  const oldVal = cur.readUInt16LE(0);
  if (oldVal === 2) { console.log('[patch] 已是 GUI 子系统，无需修改: ' + file); process.exit(0); }
  if (oldVal !== 3) { console.error('[patch] 意外子系统值 ' + oldVal + '，中止以避免破坏文件'); process.exit(4); }

  cur.writeUInt16LE(2, 0); // 2 = WINDOWS_GUI
  fs.writeSync(fd, cur, 0, 2, subOff);
  console.log('[patch] Subsystem ' + oldVal + '(console) -> 2(windows gui)  @ 0x' + subOff.toString(16) + ' : ' + file);
} finally {
  fs.closeSync(fd);
}
