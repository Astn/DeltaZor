# DeltaZor

High-performance, zero-allocation, SIMD-accelerated adaptive binary deltas with RLE+XOR.

## Core Features (Implemented)
- Opcodes 0x00-0x03: ZeroRun (COPY), NonZeroRun (XOR), Extension (Extend), Truncation (Trim).

## High-Priority (Partial)
- MOTIF Repeats (0x04 Uniform, 0x05 Varying): Implemented with chunk-less mask-based contiguous packing for repeating patterns, featuring lazy, single-accumulator detection for variable UnitSizes 2-8 in a single-pass, allocation-free manner.

## Pending Features
- ChannelRun (TBD) for structured data (integratable with MOTIF).
- Float/Half Runs (TBD).
- Arithmetic/Planar (TBD).

Dual-language: C# (.NET) and Zig (native/WASM).

## Revision History
- October 28, 2025: Refined MOTIF to chunk-less mask-based for performance and allocation-free gains.