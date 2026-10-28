// Oodle decompression worker for PoEDataExtractor.
// Protocol (little-endian, all reads/writes synchronous on fd 0/1):
//   request : u32 rawSize | u32 compressedLen | compressedBytes
//   response: u32 status (0 = ok, 1 = error) | u32 outLen | bytes
//                   status 0 -> decompressed bytes
//                   status 1 -> UTF-8 error message
// Loop ends when stdin closes.
import { createRequire } from "node:module";
import path from "node:path";
import { pathToFileURL } from "node:url";
import fs from "fs";

// Resolve installed packages in the runtime folder while keeping this worker's source in the checkout.
const packages = process.env.Game_RUNTIME_DIR
  ? path.join(process.env.Game_RUNTIME_DIR, "Tools", "ooz", "package.json") : import.meta.url;
const { decompress } = await import(pathToFileURL(createRequire(packages).resolve("ooz-wasm")).href);

function readExact(n) {
  const buf = Buffer.allocUnsafe(n);
  let off = 0;
  while (off < n) {
    let r;
    try {
      r = fs.readSync(0, buf, off, n - off, null);
    } catch (e) {
      if (e && e.code === "EAGAIN") continue;
      if (e && e.code === "EOF") return null;
      throw e;
    }
    if (r === 0) return null;
    off += r;
  }
  return buf;
}

function writeAll(buf) {
  let off = 0;
  while (off < buf.length) {
    off += fs.writeSync(1, buf, off, buf.length - off);
  }
}

while (true) {
  const header = readExact(8);
  if (!header) break;
  const rawSize = header.readUInt32LE(0);
  const compressedLen = header.readUInt32LE(4);
  const compressed = readExact(compressedLen);
  if (!compressed) break;

  let payload;
  let status;
  try {
    const out = decompress(compressed, rawSize);
    payload = Buffer.from(out.buffer, out.byteOffset, out.byteLength);
    status = 0;
  } catch (e) {
    payload = Buffer.from(String((e && e.stack) || e), "utf8");
    status = 1;
  }

  const respHeader = Buffer.allocUnsafe(8);
  respHeader.writeUInt32LE(status >>> 0, 0);
  respHeader.writeUInt32LE(payload.length >>> 0, 4);
  writeAll(respHeader);
  writeAll(payload);
}
