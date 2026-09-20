using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace TlsPoc.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        // BenchmarkDotNet 0.15.x cannot generate a child project for the net11.0 moniker yet,
        // so run in-process rather than spawning an out-of-process benchmark host.
        var config = DefaultConfig.Instance
            .AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithWarmupCount(3)
                .WithIterationCount(10));

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}
