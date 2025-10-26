using System.Reflection;
using BenchmarkDotNet.Running;
using DeltaZorTests.Benchmarks;
using DZ.Tests;


BenchmarkRunner.Run(Assembly.GetExecutingAssembly());
