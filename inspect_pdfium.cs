using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(@"C:\Users\User\.nuget\packages\pdfiumcore\147.0.7699\lib\net8.0\PDFiumCore.dll");
var type = asm.GetType("PDFiumCore.fpdfview");
foreach (var m in type.GetMethods(BindingFlags.Public|BindingFlags.Static).Where(m => m.Name.Contains("Bitmap") || m.Name.Contains("PageSize") || m.Name.Contains("LoadDocument") || m.Name.Contains("LoadMem") || m.Name.Contains("Close")).OrderBy(m => m.Name))
{
    Console.WriteLine(m);
}
