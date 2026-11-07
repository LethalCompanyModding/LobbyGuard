using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace LobbyGuard.Tests;

// The copied game assemblies aren't in the deps file, so the runtime doesn't look for them next to the tests.
internal static class GameAssemblies
{
    [ModuleInitializer]
    internal static void Init() =>
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
}
