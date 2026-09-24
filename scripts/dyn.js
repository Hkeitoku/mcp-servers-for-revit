#!/usr/bin/env node
// dyn.js — drive the Dynamo MCP bridge directly over the socket (:8080),
// bypassing the MCP tool layer. Useful when Claude Code's tool index drops the
// dynamo_* tools after a mid-session reconnect.
//
// Usage:
//   node scripts/dyn.js '{"op":"get_graph"}'
//   node scripts/dyn.js '{"op":"build_graph","nodes":[{"id":"a","kind":"codeblock","code":"1..10;"}]}'
//   node scripts/dyn.js '{"op":"run","wait":true}'
//   echo '{"op":"clear"}' | node scripts/dyn.js
//
// Requires: Revit (or Civil 3D) open with a Dynamo Home graph and the MCP switch on.

const net = require("net");

function call(method, params, { host = "localhost", port = 8080, timeoutMs = 70000 } = {}) {
  return new Promise((resolve, reject) => {
    const sock = new net.Socket();
    let buf = "";
    const id = Date.now().toString() + Math.random().toString().slice(2, 8);
    const to = setTimeout(() => { sock.destroy(); reject(new Error("timeout: " + method)); }, timeoutMs);
    sock.connect(port, host, () =>
      sock.write(JSON.stringify({ jsonrpc: "2.0", method, params: params || {}, id })));
    sock.on("data", (d) => {
      buf += d.toString();
      try {
        const r = JSON.parse(buf);
        clearTimeout(to);
        sock.end();
        if (r.error) reject(new Error(r.error.message || JSON.stringify(r.error)));
        else resolve(r.result);
      } catch (_) { /* wait for the rest of the frame */ }
    });
    sock.on("error", (e) => { clearTimeout(to); reject(e); });
  });
}

async function readStdin() {
  if (process.stdin.isTTY) return "";
  const chunks = [];
  for await (const c of process.stdin) chunks.push(c);
  return Buffer.concat(chunks).toString().trim();
}

async function main() {
  const raw = process.argv[2] || (await readStdin());
  if (!raw) {
    console.error("usage: node scripts/dyn.js '<json>'   (json = the params for dynamo_op, must include \"op\")");
    process.exit(2);
  }
  let params;
  try { params = JSON.parse(raw); }
  catch (e) { console.error("bad JSON: " + e.message); process.exit(2); }

  const method = params.method || "dynamo_op";
  delete params.method;

  try {
    const result = await call(method, params);
    console.log(JSON.stringify(result, null, 2));
  } catch (e) {
    console.error("FAILED: " + e.message);
    process.exit(1);
  }
}

if (require.main === module) main();

module.exports = { call };
