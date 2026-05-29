const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});

    const encoder_mod = b.createModule(.{
        .root_source_file = b.path("src/encoder.zig"),
        .target = target,
        .optimize = optimize,
    });

    const decoder_mod = b.createModule(.{
        .root_source_file = b.path("src/decoder.zig"),
        .target = target,
        .optimize = optimize,
    });

    const utils_mod = b.createModule(.{
        .root_source_file = b.path("src/utils.zig"),
        .target = target,
        .optimize = optimize,
    });

    const lib = b.addLibrary(.{
        .name = "deltazor",
        .root_module = b.createModule(.{
 			.root_source_file = b.path("src/deltazor.zig"),
 			.target = target,
 			.optimize = optimize,
 			.imports = &.{
 				.{ .name = "encoder", .module = encoder_mod },
 				.{ .name = "decoder", .module = decoder_mod },
 				.{ .name = "utils", .module = utils_mod },
 			},
 		}),
 		.version = .{.major = 1, .minor = 0, .patch = 0},
    });

    b.installArtifact(lib);

    // Cross-compile + WASM (TASK-0370). The static lib above already cross-compiles to every
    // -Dtarget the matrix needs (x86_64/aarch64 linux/macos/windows) because the source is
    // pure-Zig/allocator-based — no OS syscalls. For WASM, a `.a` archive is NOT a usable
    // module, so when the resolved target is a wasm arch we ALSO emit an executable reactor
    // (`deltazor.wasm`) whose exports come from src/wasm.zig. `zig build -Dtarget=wasm32-*`
    // then produces zig-out/bin/deltazor.wasm; native targets are unaffected.
    if (target.result.cpu.arch.isWasm()) {
        const wasm = b.addExecutable(.{
            .name = "deltazor",
            .root_module = b.createModule(.{
                .root_source_file = b.path("src/wasm.zig"),
                .target = target,
                .optimize = optimize,
            }),
        });
        // Reactor module (no _start entry): exports stay callable, no main() required.
        wasm.entry = .disabled;
        // Surface the explicit `export fn` ABI symbols to the host.
        wasm.rdynamic = true;
        b.installArtifact(wasm);
    }

    // Step to generate and copy test data if needed (single chained command)
    const generate_testdata = b.addSystemCommand(&[_][]const u8{
        "cmd",
        "/c",
        // Always regenerate the corpus from the CURRENT C# encoder. Skipping when a
        // manifest exists let the Zig corpus go stale relative to a changed C# encoder,
        // which masks create-delta byte-parity regressions (EPIC-0044 / TASK-0429).
        // --no-restore is MANDATORY: restoring hangs on the Astn feed (see TASK-0363 exec log),
        // and without it the build silently used a STALE TestGen assembly, regenerating an
        // out-of-date corpus that masked C#<->Zig divergence. Build DeltaZor.csproj first so the
        // TestGen reference resolves to the CURRENT encoder, then run TestGen, then copy.
        "echo Regenerating test data from current C# encoder... && cd ..\\..\\src\\csharp && dotnet build DeltaZor\\DeltaZor.csproj --no-restore -m:1 && dotnet build DeltaZor.TestGen\\DeltaZor.TestGen.csproj --no-restore -m:1 && dotnet run --project DeltaZor.TestGen\\DeltaZor.TestGen.csproj --no-restore -m:1 && if not exist ..\\..\\src\\zig\\testdata mkdir ..\\..\\src\\zig\\testdata && xcopy /E /I DeltaZor.TestGen\\bin\\Debug\\net10.0\\testdata\\* ..\\..\\src\\zig\\testdata\\ /Y && cd ..\\..\\src\\zig",
    });
    generate_testdata.has_side_effects = true;
    generate_testdata.setName("generate-testdata");

    const tests = b.addTest(.{
 		.root_module = b.createModule(.{
         	.root_source_file = b.path("src/tests.zig"),
         	.optimize = optimize,
 			.target = target,
 			.imports = &.{
 				.{ .name = "encoder", .module = encoder_mod },
 				.{ .name = "decoder", .module = decoder_mod },
 				.{ .name = "utils", .module = utils_mod },
 			},
 		}),
    });

    // Depend on testdata generation
    tests.step.dependOn(&generate_testdata.step);

    const run_tests = b.addRunArtifact(tests);
    const test_step = b.step("test", "Run Zig tests (auto-generates testdata if needed)");
    test_step.dependOn(&run_tests.step);
}