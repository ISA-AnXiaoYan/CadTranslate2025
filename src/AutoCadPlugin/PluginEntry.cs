using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(CadTranslate2025.Commands))]

namespace CadTranslate2025;

public sealed class PluginEntry : IExtensionApplication
{
    public void Initialize() { }
    public void Terminate() { }
}

