// WASM island protocol conformance: exercises the wasm helpers of the SHIPPED
// hydrate.js against the real .wasm fixture module (tests/fixtures/calc.wasm).
//
// usage: node wasm-conformance.js <hydrate.js path> <calc.wasm path>

const assert = require("assert");
const fs = require("fs");

const [hydratePath, wasmPath] = process.argv.slice(2);
assert.ok(hydratePath && wasmPath, "usage: node wasm-conformance.js <hydrate.js> <calc.wasm>");

const { wasmWriteString, wasmReadString, wasmDispatchIsland } = require(hydratePath);
const bytes = fs.readFileSync(wasmPath);

(async () => {
  const { instance } = await WebAssembly.instantiate(bytes, {});

  // 1) string write/read round-trip through module memory
  const str = wasmWriteString(instance, '{"value":7}');
  const memory = new Uint8Array(instance.exports.memory.buffer);
  const decoded = new TextDecoder().decode(memory.subarray(str.offset, str.offset + str.length));
  assert.strictEqual(decoded, '{"value":7}', "writeString round-trip");

  // 2) readString: NUL-terminated read of the static dispatch result
  const statePtr = instance.exports.volt_dispatch(0, 0, 0, 0, 0, 0, 0, 0);
  const state = wasmReadString(instance, statePtr);
  assert.strictEqual(state, '{"value":8}', "volt_dispatch returns the new-state JSON");

  // 3) the full hydrate.js wasm dispatch pipeline (same path a browser action takes)
  const html = wasmDispatchIsland(instance, "Calc", "add", { value: 7 }, {});
  assert.ok(html && html.includes("data-v-form"), "volt_render returns CONTRACT form markup");
  assert.ok(html.includes("wasm-result"), "render output contains the component markup");

  console.log("wasm conformance: OK");
})().catch((err) => {
  console.error(err);
  process.exit(1);
});
