using System.CodeDom.Compiler;
using System.Text;
using Microsoft.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SingBoot.Tests;

internal static class StubSingBox
{
    public static void Compile(string outputPath, string version = "1.0.0", bool failOnRun = false)
    {
        var source = $$"""
            using System;
            using System.IO;
            using System.Text;
            using System.Threading;

            internal static class StubSingBox
            {
                private static int Main(string[] args)
                {
                    if (args.Length == 1 && args[0] == "version")
                    {
                        Console.WriteLine("sing-box version {{version}}");
                        return 0;
                    }
                    if (args.Length != 3 || args[0] != "run" || args[1] != "-c" || args[2] != "stdin")
                    {
                        Console.Error.WriteLine("FATAL unexpected arguments");
                        return 2;
                    }

                    File.AppendAllText("versions.log", "{{version}}" + Environment.NewLine);
                    if ({{(failOnRun ? "true" : "false")}})
                    {
                        Console.Error.WriteLine("FATAL incompatible config");
                        return 3;
                    }

                    var exitEvent = new ManualResetEvent(false);
                    Console.CancelKeyPress += (_, eventArgs) =>
                    {
                        eventArgs.Cancel = true;
                        exitEvent.Set();
                    };

                    var stdin = Console.In.ReadToEnd().Trim();
                    Console.WriteLine("stub ready");
                    Console.Error.WriteLine("\u001b[33mWARN network starting https://example.test/config?token=secret\u001b[0m");
                    File.AppendAllText(
                        Path.Combine(Environment.CurrentDirectory, "launches.log"),
                        stdin + Environment.NewLine,
                        new UTF8Encoding(false));

                    exitEvent.WaitOne(TimeSpan.FromSeconds(20));
                    return 0;
                }
            }
            """;

        using var provider = new CSharpCodeProvider();
        var parameters = new CompilerParameters
        {
            GenerateExecutable = true,
            OutputAssembly = outputPath,
            TreatWarningsAsErrors = false
        };
        parameters.ReferencedAssemblies.Add("System.dll");

        var results = provider.CompileAssemblyFromSource(parameters, source);
        if (results.Errors.HasErrors)
        {
            var errors = new StringBuilder();
            foreach (CompilerError error in results.Errors)
                errors.AppendLine(error.ToString());
            Assert.Fail($"Failed to compile stub sing-box.exe: {errors}");
        }
    }
}
