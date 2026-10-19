namespace Dropstone.Services;

internal enum PayloadMode
{
    Ask,
    Local,
    Url
}

internal sealed class DeploymentConfig
{
    public string ConfigPath { get; private init; } = string.Empty;
    public string InstallDir { get; private init; } = string.Empty;
    public string LauncherExe { get; private init; } = string.Empty;
    public string VersionsDir { get; private init; } = string.Empty;
    public PayloadMode Mode { get; private init; }
    public string PayloadPath { get; private init; } = string.Empty;
    public Uri? PayloadUri { get; private init; }
    public string? Warning { get; private init; }

    public bool Exists => !string.IsNullOrEmpty(ConfigPath);

    public static DeploymentConfig Load()
    {
        var baseDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = AppContext.BaseDirectory;
        }

        var configPath = Path.Combine(baseDirectory, "config.ini");
        if (!File.Exists(configPath))
        {
            return new DeploymentConfig();
        }

        Dictionary<string, string> values;
        try
        {
            values = IniConfig.Read(configPath);
        }
        catch (Exception exception)
        {
            return new DeploymentConfig
            {
                ConfigPath = configPath,
                Warning = $"Could not read config.ini: {exception.Message}"
            };
        }

        var directory = Path.GetDirectoryName(configPath) ?? baseDirectory;
        string ReadPath(string key)
        {
            var value = values.GetValueOrDefault(key, string.Empty).Trim();
            if (value.Length == 0)
            {
                return string.Empty;
            }

            value = Environment.ExpandEnvironmentVariables(value);
            try
            {
                if (!Path.IsPathRooted(value))
                {
                    value = Path.Combine(directory, value);
                }
                return Path.GetFullPath(value);
            }
            catch
            {
                return string.Empty;
            }
        }

        var installDir = ReadPath("installdir");
        var launcherExe = ReadPath("launcherexe");
        var versionsDir = ReadPath("versionsdir");
        var payloadPath = ReadPath("payloadpath");
        var payloadUrl = values.GetValueOrDefault("payloadurl", string.Empty).Trim();
        var modeText = values.GetValueOrDefault("payloadmode", string.Empty).Trim();

        var mode = modeText.ToLowerInvariant() switch
        {
            "local" or "file" => PayloadMode.Local,
            "url" or "http" or "https" => PayloadMode.Url,
            _ => PayloadMode.Ask
        };

        string? warning = null;
        Uri? payloadUri = null;
        if (mode == PayloadMode.Local && payloadPath.Length == 0)
        {
            warning = "payloadMode=local requires payloadPath in config.ini. Choose the payload manually.";
            mode = PayloadMode.Ask;
        }
        else if (mode == PayloadMode.Local && !File.Exists(payloadPath))
        {
            warning = $"The preset payload was not found: {payloadPath}";
        }
        else if (mode == PayloadMode.Url)
        {
            if (Uri.TryCreate(payloadUrl, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                payloadUri = uri;
            }
            else
            {
                warning = "payloadMode=url requires an absolute http/https payloadUrl in config.ini. Choose the payload manually.";
                mode = PayloadMode.Ask;
            }
        }

        return new DeploymentConfig
        {
            ConfigPath = configPath,
            InstallDir = installDir,
            LauncherExe = launcherExe,
            VersionsDir = versionsDir,
            Mode = mode,
            PayloadPath = payloadPath,
            PayloadUri = payloadUri,
            Warning = warning
        };
    }
}
