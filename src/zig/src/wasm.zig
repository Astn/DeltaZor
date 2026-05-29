// WASM C-ABI export surface for DeltaZor (TASK-0370).
//
// The native public API (DeltaZor.createDelta / applyDelta in deltazor.zig) takes a
// std.mem.Allocator and Zig slices, neither of which a wasm host (JS, wasmtime, etc.)
// can express across the module boundary. This file is the MINIMAL correct adapter: it
// pins a single global allocator and exposes flat pointer/length C-ABI functions plus
// alloc/free so the host owns the buffer lifecycle.
//
// Allocator decision: std.heap.wasm_allocator. It is the canonical freestanding-wasm
// allocator in Zig 0.15 (backed by the wasm `memory.grow` builtin) and needs no OS — so
// the same wrapper builds for BOTH wasm32-freestanding and wasm32-wasi. We deliberately do
// NOT use page_allocator (its wasm impl is the wasm_allocator anyway) or a fixed buffer
// (callers' input sizes are unbounded).
//
// This module is ONLY compiled into the wasm artifact; native lib/test builds never import
// it, so the host-facing ABI choices here can't perturb C#<->Zig byte-parity (the corpus
// still exercises deltazor.zig directly via tests.zig).

const std = @import("std");
const deltazor = @import("deltazor.zig").DeltaZor;

const allocator = std.heap.wasm_allocator;

/// Allocate `len` bytes in wasm linear memory and return the pointer (0 on OOM).
/// The host writes input here, then passes the pointer back to create/apply.
export fn dz_alloc(len: usize) ?[*]u8 {
    const buf = allocator.alloc(u8, len) catch return null;
    return buf.ptr;
}

/// Free a buffer previously returned by dz_alloc / dz_create_delta.
export fn dz_free(ptr: [*]u8, len: usize) void {
    allocator.free(ptr[0..len]);
}

/// Encode a delta. old/new are host-provided buffers. On success returns a pointer to a
/// freshly allocated delta buffer and writes its length to *out_len; the host must dz_free
/// it. Returns null (and sets *out_len = 0) on any error (incl. OOM).
export fn dz_create_delta(
    old_ptr: [*]const u8,
    old_len: usize,
    new_ptr: [*]const u8,
    new_len: usize,
    out_len: *usize,
) ?[*]u8 {
    const delta = deltazor.createDelta(
        old_ptr[0..old_len],
        new_ptr[0..new_len],
        allocator,
        .{},
    ) catch {
        out_len.* = 0;
        return null;
    };
    out_len.* = delta.len;
    return delta.ptr;
}

/// Apply a delta. The host must size `output` correctly (the new-data length, which it knows
/// out of band — same contract as the native applyDelta). Returns 0 on success, 1 on error.
export fn dz_apply_delta(
    old_ptr: [*]const u8,
    old_len: usize,
    delta_ptr: [*]const u8,
    delta_len: usize,
    out_ptr: [*]u8,
    out_len: usize,
) i32 {
    deltazor.applyDelta(
        old_ptr[0..old_len],
        delta_ptr[0..delta_len],
        out_ptr[0..out_len],
        allocator,
    ) catch return 1;
    return 0;
}
