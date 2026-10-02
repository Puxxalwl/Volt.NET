#!/usr/bin/env python3
"""Emits a minimal but REAL WebAssembly module implementing the Volt island
contract (CONTRACT.md): memory + bump alloc + volt_dispatch + volt_render.

The module models the `Calc` island with state {"value":N}: dispatch returns the
new state JSON, render returns a CONTRACT-shaped form. Used by the node-based
hydrate.js protocol conformance test (tests/Volt.E2E/wasm-conformance.js).

Usage: python3 tools/gen-calc-wasm.py [output-path]
"""
import struct
import sys

STATE_JSON = b'{"value":8}\x00'
RENDER_HTML = b'<form method="post" action="/_volt/fallback" data-v-form><p class="wasm-result">8</p></form>\x00'
DATA_OFFSET = 1024  # strings live here; the heap starts after them


def u32leb(n):
    out = bytearray()
    while True:
        b = n & 0x7F
        n >>= 7
        if n:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def i32leb(n):
    # signed LEB128
    out = bytearray()
    while True:
        b = n & 0x7F
        sign = b & 0x40
        n >>= 7
        if (n == 0 and sign == 0) or (n == -1 and sign != 0):
            out.append(b)
            return bytes(out)
        out.append(b | 0x80)


def section(sid, payload):
    return bytes([sid]) + u32leb(len(payload)) + payload


def vec(items):
    return u32leb(len(items)) + b"".join(items)


def name(n):
    b = n.encode()
    return u32leb(len(b)) + b


# ---- type section: t0 (i32)->(i32) alloc, t1 (i32 x8)->(i32) dispatch, t2 (i32,i32)->(i32) render
I32 = b"\x7f"
types = vec([
    b"\x60" + vec([I32]) + vec([I32]),
    b"\x60" + vec([I32] * 8) + vec([I32]),
    b"\x60" + vec([I32, I32]) + vec([I32]),
])

# ---- function section: funcs 0,1,2 use types 0,1,2
funcs = vec([u32leb(0), u32leb(1), u32leb(2)])

# ---- memory section: 1 page, no max
memory = vec([b"\x00" + u32leb(1)])

# ---- global section: mutable i32 heap pointer initialized past the data
heap_start = DATA_OFFSET + len(STATE_JSON) + len(RENDER_HTML)
glob = vec([
    I32 + b"\x01" + b"\x41" + i32leb(heap_start) + b"\x0b",  # globaltype + init expr
])

# ---- export section
exports = vec([
    name("memory") + b"\x02" + u32leb(0),
    name("alloc") + b"\x00" + u32leb(0),
    name("volt_dispatch") + b"\x00" + u32leb(1),
    name("volt_render") + b"\x00" + u32leb(2),
])

# ---- code section
def body(code):
    b = b"\x00" + code  # 0 local declaration groups
    return u32leb(len(b)) + b

# alloc(n): bump allocator, returns the OLD top
alloc_body = body(
    b"\x20\x00"       # local.get 0 (n)
    b"\x23\x00"       # global.get 0 (heap)
    b"\x6a"           # i32.add  (n + heap)
    b"\x24\x00"       # global.set 0
    b"\x23\x00"       # global.get 0 (new top)
    b"\x20\x00"       # local.get 0
    b"\x6b"           # i32.sub  (new top - n = old top)
    b"\x0b"           # end
)
# volt_dispatch(...): returns the static new-state JSON pointer
dispatch_body = body(b"\x41" + i32leb(DATA_OFFSET) + b"\x0b")
# volt_render(statePtr, stateLen): returns the static html pointer
render_body = body(b"\x41" + i32leb(DATA_OFFSET + len(STATE_JSON)) + b"\x0b")

code = vec([alloc_body, dispatch_body, render_body])

# ---- data section: active segment in memory 0
data = vec([
    b"\x00" + b"\x41" + i32leb(DATA_OFFSET) + b"\x0b" + u32leb(len(STATE_JSON) + len(RENDER_HTML)) + STATE_JSON + RENDER_HTML,
])

module = (
    b"\x00asm" + struct.pack("<I", 1)
    + section(1, types)
    + section(3, funcs)
    + section(5, memory)
    + section(6, glob)
    + section(7, exports)
    + section(10, code)
    + section(11, data)
)

out = sys.argv[1] if len(sys.argv) > 1 else "tests/fixtures/calc.wasm"
with open(out, "wb") as f:
    f.write(module)
print(f"wrote {out} ({len(module)} bytes)")
