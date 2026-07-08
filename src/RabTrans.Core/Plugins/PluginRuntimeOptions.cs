namespace RabTrans.Core.Plugins;

public static class PluginRuntimeOptions
{
    public static string NodeExecutablePath { get; set; } = string.Empty;

    public static string ResolveCommand(string command, string entryPath)
    {
        if (command.Equals("node", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(command) && Path.GetExtension(entryPath).Equals(".js", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(NodeExecutablePath)
                ? "node"
                : NodeExecutablePath;
        }

        return string.IsNullOrWhiteSpace(command) ? entryPath : command;
    }
}
