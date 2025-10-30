const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});

    const lib = b.addLibrary(.{
        .name = "deltazor",
        .root_module = b.createModule(.{
			.root_source_file = b.path("src/deltazor.zig"),
			.target = target,
			.optimize = optimize
		}),
		.version = .{.major = 1, .minor = 0, .patch = 0},
    });

    b.installArtifact(lib);

    // Step to generate and copy test data if needed (single chained command)
    const generate_testdata = b.addSystemCommand(&[_][]const u8{
        "cmd",
        "/c",
        "echo Checking for test data... && if exist testdata\\manifest.json (echo Test data exists, skipping generation.) else (echo Generating test data... && cd ..\\..\\src\\csharp && dotnet build DeltaZor.TestGen\\DeltaZor.TestGen.csproj && dotnet run --project DeltaZor.TestGen\\DeltaZor.TestGen.csproj && if not exist ..\\..\\src\\zig\\testdata mkdir ..\\..\\src\\zig\\testdata && xcopy /E /I DeltaZor.TestGen\\bin\\Debug\\net9.0\\testdata\\* ..\\..\\src\\zig\\testdata\\ /Y && cd ..\\..\\src\\zig && copy testdata\\manifest.json src\\manifest.json)",
    });
    generate_testdata.has_side_effects = true;
    generate_testdata.setName("generate-testdata");

    const tests = b.addTest(.{
		.root_module = b.createModule(.{
        	.root_source_file = b.path("src/tests.zig"),
        	.optimize = optimize,
			.target = target
		}),
    });

    // Depend on testdata generation
    tests.step.dependOn(&generate_testdata.step);

    const run_tests = b.addRunArtifact(tests);
    const test_step = b.step("test", "Run Zig tests (auto-generates testdata if needed)");
    test_step.dependOn(&run_tests.step);
}