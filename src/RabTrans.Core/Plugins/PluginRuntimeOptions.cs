using System.Diagnostics;

namespace RabTrans.Core.Plugins;

public static class PluginRuntimeOptions
{
    public static string NodeExecutablePath { get; set; } = string.Empty;

    public static string PythonExecutablePath { get; set; } = string.Empty;

    public static void ApplyCommonEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
    }

    public static string ResolveCommand(string command, string entryPath)
    {
        if (command.Equals("node", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(command) && Path.GetExtension(entryPath).Equals(".js", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(NodeExecutablePath)
                ? "node"
                : NodeExecutablePath;
        }

        if (command.Equals("python", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("python3", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(command) && Path.GetExtension(entryPath).Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(PythonExecutablePath)
                ? "python"
                : PythonExecutablePath;
        }

        return string.IsNullOrWhiteSpace(command) ? entryPath : command;
    }
}
