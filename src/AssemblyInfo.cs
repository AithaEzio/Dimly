using System.Reflection;
using System.Runtime.InteropServices;

// The C# compiler turns these into the Win32 version resource, which is what Explorer reads
// for the file's tooltip and Properties > Details. It is also the single place the version
// lives: AppInfo reads it back out rather than repeating it.

[assembly: AssemblyTitle("Dimly")]
[assembly: AssemblyDescription("Dims your screens while you are away.")]
[assembly: AssemblyProduct("Dimly")]
[assembly: AssemblyCompany("Aitha")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Aitha. MIT licence.")]

[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1")]
[assembly: AssemblyInformationalVersion("1.1")]

[assembly: ComVisible(false)]
