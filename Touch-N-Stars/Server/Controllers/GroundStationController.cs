using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using TouchNStars.Server.Models;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// API Controller for Ground Station plugin integration.
/// Accesses the Ground Station plugin via reflection to avoid compile-time dependencies.
/// Provides endpoints to read and write configuration for all supported notification
/// services (Pushover, Telegram, Email, Discord, Slack, MQTT, IFTTT, ntfy.sh).
/// </summary>
public class GroundStationController : WebApiController
{
    private const string AssemblyName = "DaleGhent.NINA.GroundStation";
    private const string GroundStationTypeName = "DaleGhent.NINA.GroundStation.GroundStation";
    private const string ConfigPropertyName = "GroundStationConfig";

    private static Assembly _assembly;
    private static Type _groundStationType;
    private static readonly object _initLock = new();

    // ── Assembly / type resolution ─────────────────────────────────────────────

    private static Assembly GetAssembly()
    {
        if (_assembly != null) return _assembly;
        lock (_initLock)
        {
            if (_assembly != null) return _assembly;
            _assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == AssemblyName);
        }
        return _assembly;
    }

    private static Type GetGroundStationType()
    {
        if (_groundStationType != null) return _groundStationType;
        lock (_initLock)
        {
            if (_groundStationType != null) return _groundStationType;
            _groundStationType = GetAssembly()?.GetType(GroundStationTypeName);
        }
        return _groundStationType;
    }

    private static object GetConfig()
    {
        var type = GetGroundStationType();
        if (type == null) return null;
        var prop = type.GetProperty(ConfigPropertyName, BindingFlags.Public | BindingFlags.Static);
        return prop?.GetValue(null);
    }

    // ── Reflection helpers ─────────────────────────────────────────────────────

    private static T GetProp<T>(object obj, string name, T fallback = default)
    {
        if (obj == null) return fallback;
        try
        {
            var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null) return fallback;
            var val = prop.GetValue(obj);
            if (val is T t) return t;
            return fallback;
        }
        catch { return fallback; }
    }

    private static object GetPropRaw(object obj, string name)
    {
        if (obj == null) return null;
        try
        {
            var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return prop?.GetValue(obj);
        }
        catch { return null; }
    }

    private static (bool Success, string Error) SetProp(object obj, string name, object value)
    {
        if (obj == null) return (false, "Config object is null");
        try
        {
            var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null) return (false, $"Property '{name}' not found");
            if (!prop.CanWrite) return (false, $"Property '{name}' is read-only");
            var converted = ConvertValue(value, prop.PropertyType);
            prop.SetValue(obj, converted);
            return (true, null);
        }
        catch (TargetInvocationException tie)
        {
            var inner = tie.InnerException ?? tie;
            Logger.Error($"GroundStationController: failed to set '{name}': {inner.Message}");
            return (false, inner.Message);
        }
        catch (Exception ex)
        {
            Logger.Error($"GroundStationController: failed to set '{name}': {ex.Message}");
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Converts a value (possibly a Newtonsoft JValue or string) to the target CLR type.
    /// EmbedIO deserializes JSON bodies via Newtonsoft.Json, so dictionary values arrive
    /// as JValue instances whose ToString() yields a culture-neutral string representation.
    /// </summary>
    private static object ConvertValue(object value, Type targetType)
    {
        if (value == null) return null;
        if (targetType.IsInstanceOfType(value)) return value;

        var strVal = value.ToString();

        if (targetType.IsEnum)
        {
            // Accept either an integer string ("0") or a name ("Pushover")
            if (int.TryParse(strVal, out int intVal))
                return Enum.ToObject(targetType, intVal);
            return Enum.Parse(targetType, strVal, ignoreCase: true);
        }

        if (targetType == typeof(string)) return strVal;
        if (targetType == typeof(bool)) return bool.Parse(strVal);
        if (targetType == typeof(int)) return int.Parse(strVal, CultureInfo.InvariantCulture);
        if (targetType == typeof(ushort)) return ushort.Parse(strVal, CultureInfo.InvariantCulture);
        if (targetType == typeof(short)) return short.Parse(strVal, CultureInfo.InvariantCulture);
        if (targetType == typeof(byte)) return byte.Parse(strVal, CultureInfo.InvariantCulture);
        if (targetType == typeof(double)) return double.Parse(strVal, CultureInfo.InvariantCulture);
        if (targetType == typeof(float)) return float.Parse(strVal, CultureInfo.InvariantCulture);
        if (targetType == typeof(long)) return long.Parse(strVal, CultureInfo.InvariantCulture);

        return Convert.ChangeType(strVal, targetType, CultureInfo.InvariantCulture);
    }

    private static string EnumName(object enumVal) => enumVal?.ToString() ?? string.Empty;

    private static int EnumValue(object enumVal)
        => enumVal == null ? 0 : (int)Convert.ChangeType(enumVal, typeof(int), CultureInfo.InvariantCulture);

    // ── Test-command invocation ────────────────────────────────────────────────

    private static async Task<(bool Success, string Error)> InvokeTestCommand(object config, string commandPropName)
    {
        try
        {
            var configType = config.GetType();
            var cmdProp = configType.GetProperty(commandPropName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

            if (cmdProp == null)
                return (false, $"Command '{commandPropName}' not found on GroundStationConfig");

            bool isStatic = cmdProp.GetGetMethod()?.IsStatic ?? false;
            var cmd = isStatic ? cmdProp.GetValue(null) : cmdProp.GetValue(config);
            if (cmd == null)
                return (false, $"Command '{commandPropName}' is null");

            // Prefer ExecuteAsync(object) from IAsyncRelayCommand (CommunityToolkit.Mvvm).
            // Use GetMethods() + manual filter to avoid AmbiguousMatchException when
            // AsyncRelayCommand<T> exposes both ExecuteAsync(object) and ExecuteAsync(T).
            var executeAsync = cmd.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                    m.Name == "ExecuteAsync"
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(object));

            if (executeAsync != null)
            {
                var task = executeAsync.Invoke(cmd, new object[] { null }) as Task;
                if (task != null) await task;
                return (true, null);
            }

            // Fall back to synchronous Execute
            var execute = cmd.GetType().GetMethod("Execute",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(object) }, null);
            execute?.Invoke(cmd, new object[] { null });
            return (true, null);
        }
        catch (TargetInvocationException tie)
        {
            var inner = tie.InnerException ?? tie;
            Logger.Error($"GroundStationController: {commandPropName} threw: {inner.Message}");
            return (false, inner.Message);
        }
        catch (Exception ex)
        {
            Logger.Error($"GroundStationController: {commandPropName}: {ex.Message}");
            return (false, ex.Message);
        }
    }

    // ── Status ─────────────────────────────────────────────────────────────────

    /// <summary>GET /api/groundstation/status — returns whether the Ground Station plugin is loaded.</summary>
    [Route(HttpVerbs.Get, "/groundstation/status")]
    public object GetStatus()
    {
        var asm = GetAssembly();
        return new
        {
            Success = true,
            Response = new
            {
                Installed = asm != null,
                Version = asm?.GetName().Version?.ToString()
            },
            StatusCode = 200,
            Type = "GroundStationStatus"
        };
    }

    // ── Pushover ───────────────────────────────────────────────────────────────

    /// <summary>GET /api/groundstation/pushover — read Pushover settings.</summary>
    [Route(HttpVerbs.Get, "/groundstation/pushover")]
    public object GetPushover()
    {
        try
        {
            var cfg = GetConfig();
            if (cfg == null) return PluginNotInstalled();

            return new
            {
                Success = true,
                Response = new
                {
                    UserKey = GetProp<string>(cfg, "PushoverUserKey"),
                    AppKey = GetProp<string>(cfg, "PushoverAppKey"),
                    DefaultNotificationSound = EnumName(GetPropRaw(cfg, "PushoverDefaultNotificationSound")),
                    DefaultNotificationSoundValue = EnumValue(GetPropRaw(cfg, "PushoverDefaultNotificationSound")),
                    DefaultNotificationPriority = EnumName(GetPropRaw(cfg, "PushoverDefaultNotificationPriority")),
                    DefaultNotificationPriorityValue = EnumValue(GetPropRaw(cfg, "PushoverDefaultNotificationPriority")),
                    DefaultFailureSound = EnumName(GetPropRaw(cfg, "PushoverDefaultFailureSound")),
                    DefaultFailureSoundValue = EnumValue(GetPropRaw(cfg, "PushoverDefaultFailureSound")),
                    DefaultFailurePriority = EnumName(GetPropRaw(cfg, "PushoverDefaultFailurePriority")),
                    DefaultFailurePriorityValue = EnumValue(GetPropRaw(cfg, "PushoverDefaultFailurePriority")),
                    EmergRetryInterval = GetProp<int>(cfg, "PushoverEmergRetryInterval"),
                    EmergExpireAfter = GetProp<int>(cfg, "PushoverEmergExpireAfter"),
                    FailureTitleText = GetProp<string>(cfg, "PushoverFailureTitleText"),
                    FailureBodyText = GetProp<string>(cfg, "PushoverFailureBodyText"),
                },
                StatusCode = 200,
                Type = "PushoverSettings"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return InternalError(ex.Message);
        }
    }

    /// <summary>
    /// PUT /api/groundstation/pushover — update Pushover settings.
    /// Accepted keys: PushoverUserKey, PushoverAppKey, PushoverDefaultNotificationSound,
    /// PushoverDefaultNotificationPriority, PushoverDefaultFailureSound,
    /// PushoverDefaultFailurePriority, PushoverEmergRetryInterval, PushoverEmergExpireAfter,
    /// PushoverFailureTitleText, PushoverFailureBodyText.
    /// Enum values may be supplied as the integer value or the member name.
    /// </summary>
    [Route(HttpVerbs.Put, "/groundstation/pushover")]
    public async Task<object> SetPushover()
    {
        try
        {
            var cfg = GetConfig();
            if (cfg == null) return PluginNotInstalled();

            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (body == null) return BadRequest("Empty request body");

            ApplySettings(cfg, body, new[]
            {
                "PushoverUserKey", "PushoverAppKey",
                "PushoverDefaultNotificationSound", "PushoverDefaultNotificationPriority",
                "PushoverDefaultFailureSound", "PushoverDefaultFailurePriority",
                "PushoverEmergRetryInterval", "PushoverEmergExpireAfter",
                "PushoverFailureTitleText", "PushoverFailureBodyText"
            }, out int setCount, out var failures);

            return SettingsResult("PushoverSettings", setCount, failures);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return InternalError(ex.Message);
        }
    }

    /// <summary>POST /api/groundstation/pushover/test — send a Pushover test message.</summary>
    [Route(HttpVerbs.Post, "/groundstation/pushover/test")]
    public async Task<object> TestPushover()
    {
        var cfg = GetConfig();
        if (cfg == null) return PluginNotInstalled();

        var (success, error) = await InvokeTestCommand(cfg, "PushoverTestCommand");
        return TestResult(success, error);
    }

    // ── Telegram ───────────────────────────────────────────────────────────────

    /// <summary>GET /api/groundstation/telegram — read Telegram settings.</summary>
    [Route(HttpVerbs.Get, "/groundstation/telegram")]
    public object GetTelegram()
    {
        try
        {
            var cfg = GetConfig();
            if (cfg == null) return PluginNotInstalled();

            return new
            {
                Success = true,
                Response = new
                {
                    AccessToken = GetProp<string>(cfg, "TelegramAccessToken"),
                    ChatId = GetProp<string>(cfg, "TelegramChatId"),
                    FailureBodyText = GetProp<string>(cfg, "TelegramFailureBodyText"),
                },
                StatusCode = 200,
                Type = "TelegramSettings"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return InternalError(ex.Message);
        }
    }

    /// <summary>
    /// PUT /api/groundstation/telegram — update Telegram settings.
    /// Accepted keys: TelegramAccessToken, TelegramChatId, TelegramFailureBodyText.
    /// </summary>
    [Route(HttpVerbs.Put, "/groundstation/telegram")]
    public async Task<object> SetTelegram()
    {
        try
        {
            var cfg = GetConfig();
            if (cfg == null) return PluginNotInstalled();

            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (body == null) return BadRequest("Empty request body");

            ApplySettings(cfg, body, new[]
            {
                "TelegramAccessToken", "TelegramChatId", "TelegramFailureBodyText"
            }, out int setCount, out var failures);

            return SettingsResult("TelegramSettings", setCount, failures);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return InternalError(ex.Message);
        }
    }

    /// <summary>POST /api/groundstation/telegram/test — send a Telegram test message.</summary>
    [Route(HttpVerbs.Post, "/groundstation/telegram/test")]
    public async Task<object> TestTelegram()
    {
        var cfg = GetConfig();
        if (cfg == null) return PluginNotInstalled();

        var (success, error) = await InvokeTestCommand(cfg, "TelegramTestCommand");
        return TestResult(success, error);
    }

    // ── Email (SMTP) ───────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/groundstation/email — read SMTP/email settings.
    /// Note: SmtpPassword is intentionally excluded from the response.
    /// </summary>
    [Route(HttpVerbs.Get, "/groundstation/email")]
    public object GetEmail()
    {
        try
        {
            var cfg = GetConfig();
            if (cfg == null) return PluginNotInstalled();

            return new
            {
                Success = true,
                Response = new
                {
                    FromAddress = GetProp<string>(cfg, "SmtpFromAddress"),
                    DefaultRecipients = GetProp<string>(cfg, "SmtpDefaultRecipients"),
                    HostName = GetProp<string>(cfg, "SmtpHostName"),
                    HostPort = GetProp<ushort>(cfg, "SmtpHostPort"),
                    Username = GetProp<string>(cfg, "SmtpUsername"),
                    // SmtpPassword omitted — supply via PUT only
                    FailureSubjectText = GetProp<string>(cfg, "EmailFailureSubjectText"),
                    FailureBodyText = GetProp<string>(cfg, "EmailFailureBodyText"),
                },
                StatusCode = 200,
                Type = "EmailSettings"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return InternalError(ex.Message);
        }
    }

    /// <summary>
    /// PUT /api/groundstation/email — update SMTP/email settings.
    /// Accepted keys: SmtpFromAddress, SmtpDefaultRecipients, SmtpHostName, SmtpHostPort,
    /// SmtpUsername, SmtpPassword, EmailFailureSubjectText, EmailFailureBodyText.
    /// </summary>
    [Route(HttpVerbs.Put, "/groundstation/email")]
    public async Task<object> SetEmail()
    {
        try
        {
            var cfg = GetConfig();
            if (cfg == null) return PluginNotInstalled();

            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (body == null) return BadRequest("Empty request body");

            ApplySettings(cfg, body, new[]
            {
                "SmtpFromAddress", "SmtpDefaultRecipients", "SmtpHostName", "SmtpHostPort",
                "SmtpUsername", "SmtpPassword",
                "EmailFailureSubjectText", "EmailFailureBodyText"
            }, out int setCount, out var failures);

            return SettingsResult("EmailSettings", setCount, failures);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return InternalError(ex.Message);
        }
    }

    /// <summary>POST /api/groundstation/email/test — send an email test message.</summary>
    [Route(HttpVerbs.Post, "/groundstation/email/test")]
    public async Task<object> TestEmail()
    {
        var cfg = GetConfig();
        if (cfg == null) return PluginNotInstalled();

        var (success, error) = await InvokeTestCommand(cfg, "EmailTestCommand");
        return TestResult(success, error);
    }

    // ── Discord ────────────────────────────────────────────────────────────────

    /// <summary>GET /api/groundstation/discord — read Discord webhook settings.</summary>
    [Route(HttpVerbs.Get, "/groundstation/discord")]
    public object GetDiscord()
    {
        try
        {
            var cfg = GetConfig();
            if (cfg == null) return PluginNotInstalled();

            return new
            {
                Success = true,
                Response = new
                {
                    WebhookDefaultUrl = GetProp<string>(cfg, "DiscordWebhookDefaultUrl"),
                    WebhookDefaultBotName = GetProp<string>(cfg, "DiscordWebhookDefaultBotName"),
                    ImageWebhookUrl = GetProp<string>(cfg, "DiscordImageWebhookUrl"),
                    FailureWebhookUrl = GetProp<string>(cfg, "DiscordFailureWebhookUrl"),
                    FailureTitle = GetProp<string>(cfg, "DiscordWebhookFailureTitle"),
                    FailureMessage = GetProp<string>(cfg, "DiscordWebhookFailureMessage"),
                    ImageEventEnabled = GetProp<bool>(cfg, "DiscordImageEventEnabled"),
                    ImageTypesSelected = GetProp<string>(cfg, "DiscordImageTypesSelected"),
                    ImageInterval = GetProp<int>(cfg, "DiscordImageInterval"),
                    ImagePostTitle = GetProp<string>(cfg, "DiscordImagePostTitle"),
                },
                StatusCode = 200,
                Type = "DiscordSettings"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return InternalError(ex.Message);
        }
    }

    /// <summary>
    /// PUT /api/groundstation/discord — update Discord webhook settings.
    /// Accepted keys: DiscordWebhookDefaultUrl, DiscordWebhookDefaultBotName,
    /// DiscordImageWebhookUrl, DiscordFailureWebhookUrl, DiscordWebhookFailureTitle,
    /// DiscordWebhookFailureMessage, DiscordImageEventEnabled, DiscordImageTypesSelected,
    /// DiscordImageInterval, DiscordImagePostTitle.
    /// </summary>
    [Route(HttpVerbs.Put, "/groundstation/discord")]
    public async Task<object> SetDiscord()
    {
        try
        {
            var cfg = GetConfig();
            if (cfg == null) return PluginNotInstalled();

            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (body == null) return BadRequest("Empty request body");

            ApplySettings(cfg, body, new[]
            {
                "DiscordWebhookDefaultUrl", "DiscordWebhookDefaultBotName",
                "DiscordImageWebhookUrl", "DiscordFailureWebhookUrl",
                "DiscordWebhookFailureTitle", "DiscordWebhookFailureMessage",
                "DiscordImageEventEnabled", "DiscordImageTypesSelected",
                "DiscordImageInterval", "DiscordImagePostTitle"
            }, out int setCount, out var failures);

            return SettingsResult("DiscordSettings", setCount, failures);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return InternalError(ex.Message);
        }
    }

    /// <summary>POST /api/groundstation/discord/test — send a Discord webhook test message.</summary>
    [Route(HttpVerbs.Post, "/groundstation/discord/test")]
    public async Task<object> TestDiscord()
    {
        var cfg = GetConfig();
        if (cfg == null) return PluginNotInstalled();

        var (success, error) = await InvokeTestCommand(cfg, "DiscordWebhookTestCommand");
        return TestResult(success, error);
    }

    // ── Slack ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/groundstation/slack — read Slack settings.
    /// The Channels list reflects whatever was last fetched from the Slack API.
    /// Use POST /api/groundstation/slack/refresh-channels to update it.
    /// </summary>
    [Route(HttpVerbs.Get, "/groundstation/slack")]
    public object GetSlack()
    {
        try
        {
            var cfg = GetConfig();
            if (cfg == null) return PluginNotInstalled();

            return new
            {
                Success = true,
                Response = new
                {
                    OAuthToken = GetProp<string>(cfg, "SlackOAuthToken"),
                    WorkspaceName = GetProp<string>(cfg, "SlackWorkspaceName"),
                    BotName = GetProp<string>(cfg, "SlackBotName"),
                    BotDisplayName = GetProp<string>(cfg, "SlackBotDisplayName"),
                    FailureMessage = GetProp<string>(cfg, "SlackFailureMessage"),
                    ImageEventEnabled = GetProp<bool>(cfg, "SlackImageEventEnabled"),
                    ImageTypesSelected = GetProp<string>(cfg, "SlackImageTypesSelected"),
                    ImageInterval = GetProp<int>(cfg, "SlackImageInterval"),
                    Channels = GetPropRaw(cfg, "SlackChannels"),
                    ImageEventChannel = GetPropRaw(cfg, "SlackImageEventChannel"),
                },
                StatusCode = 200,
                Type = "SlackSettings"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return InternalError(ex.Message);
        }
    }

    /// <summary>
    /// PUT /api/groundstation/slack — update Slack settings.
    /// Accepted keys: SlackOAuthToken, SlackFailureMessage, SlackImageEventEnabled,
    /// SlackImageTypesSelected, SlackImageInterval.
    /// To update the channel list, use POST /api/groundstation/slack/refresh-channels.
    /// </summary>
    [Route(HttpVerbs.Put, "/groundstation/slack")]
    public async Task<object> SetSlack()
    {
        try
        {
            var cfg = GetConfig();
            if (cfg == null) return PluginNotInstalled();

            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (body == null) return BadRequest("Empty request body");

            ApplySettings(cfg, body, new[]
            {
                "SlackOAuthToken", "SlackFailureMessage",
                "SlackImageEventEnabled", "SlackImageTypesSelected", "SlackImageInterval"
            }, out int setCount, out var failures);

            return SettingsResult("SlackSettings", setCount, failures);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return InternalError(ex.Message);
        }
    }

    /// <summary>
    /// POST /api/groundstation/slack/refresh-channels — fetches the channel list from the
    /// Slack API using the stored OAuth token and updates the config (equivalent to clicking
    /// "Get Channel List" in the WPF options).
    /// </summary>
    [Route(HttpVerbs.Post, "/groundstation/slack/refresh-channels")]
    public async Task<object> RefreshSlackChannels()
    {
        var cfg = GetConfig();
        if (cfg == null) return PluginNotInstalled();

        var (success, error) = await InvokeTestCommand(cfg, "GetSlackChannelListCommand");
        if (!success)
            return TestResult(false, error);

        return new
        {
            Success = true,
            Response = new
            {
                WorkspaceName = GetProp<string>(cfg, "SlackWorkspaceName"),
                BotName = GetProp<string>(cfg, "SlackBotName"),
                BotDisplayName = GetProp<string>(cfg, "SlackBotDisplayName"),
                Channels = GetPropRaw(cfg, "SlackChannels"),
            },
            StatusCode = 200,
            Type = "SlackChannels"
        };
    }

    // ── MQTT ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/groundstation/mqtt — read MQTT settings.
    /// Note: MqttPassword is intentionally excluded from the response.
    /// </summary>
    [Route(HttpVerbs.Get, "/groundstation/mqtt")]
    public object GetMqtt()
    {
        try
        {
            var cfg = GetConfig();
            if (cfg == null) return PluginNotInstalled();

            return new
            {
                Success = true,
                Response = new
                {
                    BrokerHost = GetProp<string>(cfg, "MqttBrokerHost"),
                    BrokerPort = GetProp<ushort>(cfg, "MqttBrokerPort"),
                    BrokerUseTls = GetProp<bool>(cfg, "MqttBrokerUseTls"),
                    Username = GetProp<string>(cfg, "MqttUsername"),
                    // MqttPassword omitted — supply via PUT only
                    ClientId = GetProp<string>(cfg, "MqttClientId"),
                    DefaultTopic = GetProp<string>(cfg, "MqttDefaultTopic"),
                    DefaultQoSLevel = GetProp<int>(cfg, "MqttDefaultQoSLevel"),
                    DefaultFailureQoSLevel = GetProp<int>(cfg, "MqttDefaultFailureQoSLevel"),
                    DefaultRetain = GetProp<bool>(cfg, "MqttDefaultRetain"),
                    MaxReconnectAttempts = GetProp<int>(cfg, "MqttMaxReconnectAttempts"),
                    LwtEnabled = GetProp<bool>(cfg, "MqttLwtEnabled"),
                    LwtTopic = GetProp<string>(cfg, "MqttLwtTopic"),
                    LwtBirthPayload = GetProp<string>(cfg, "MqttLwtBirthPayload"),
                    LwtLastWillPayload = GetProp<string>(cfg, "MqttLwtLastWillPayload"),
                    LwtClosePayload = GetProp<string>(cfg, "MqttLwtClosePayload"),
                    ImagePublisherEnabled = GetProp<bool>(cfg, "MqttImagePubliserEnabled"),
                    ImagePublisherMetadataOnly = GetProp<bool>(cfg, "MqttImagePubliserMetadataOnly"),
                    ImagePublisherImageTopic = GetProp<string>(cfg, "MqttImagePublisherImageTopic"),
                    ImagePublisherMetadataTopic = GetProp<string>(cfg, "MqttImagePublisherMetdataTopic"),
                    ImagePublisherQoSLevel = GetProp<int>(cfg, "MqttImagePublisherQoSLevel"),
                    ImagePublisherRetain = GetProp<bool>(cfg, "MqttImagePublisherRetain"),
                    ImageTypesSelected = GetProp<string>(cfg, "MqttImageTypesSelected"),
                    ImagePubIsConfigured = GetProp<bool>(cfg, "MqttImagePubIsConfigured"),
                },
                StatusCode = 200,
                Type = "MqttSettings"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return InternalError(ex.Message);
        }
    }

    /// <summary>
    /// PUT /api/groundstation/mqtt — update MQTT settings.
    /// Accepted keys: MqttBrokerHost, MqttBrokerPort, MqttBrokerUseTls, MqttUsername,
    /// MqttPassword, MqttClientId, MqttDefaultTopic, MqttDefaultQoSLevel,
    /// MqttDefaultFailureQoSLevel, MqttDefaultRetain, MqttMaxReconnectAttempts,
    /// MqttLwtEnabled, MqttLwtTopic, MqttLwtBirthPayload, MqttLwtLastWillPayload,
    /// MqttLwtClosePayload, MqttImagePubliserEnabled, MqttImagePubliserMetadataOnly,
    /// MqttImagePublisherImageTopic, MqttImagePublisherMetdataTopic,
    /// MqttImagePublisherQoSLevel, MqttImagePublisherRetain, MqttImageTypesSelected.
    /// Note: setting MqttBrokerUseTls automatically adjusts MqttBrokerPort (8883/1883).
    /// </summary>
    [Route(HttpVerbs.Put, "/groundstation/mqtt")]
    public async Task<object> SetMqtt()
    {
        try
        {
            var cfg = GetConfig();
            if (cfg == null) return PluginNotInstalled();

            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (body == null) return BadRequest("Empty request body");

            ApplySettings(cfg, body, new[]
            {
                "MqttBrokerHost", "MqttBrokerPort", "MqttBrokerUseTls",
                "MqttUsername", "MqttPassword",
                "MqttClientId", "MqttDefaultTopic",
                "MqttDefaultQoSLevel", "MqttDefaultFailureQoSLevel", "MqttDefaultRetain",
                "MqttMaxReconnectAttempts",
                "MqttLwtEnabled", "MqttLwtTopic",
                "MqttLwtBirthPayload", "MqttLwtLastWillPayload", "MqttLwtClosePayload",
                "MqttImagePubliserEnabled", "MqttImagePubliserMetadataOnly",
                "MqttImagePublisherImageTopic", "MqttImagePublisherMetdataTopic",
                "MqttImagePublisherQoSLevel", "MqttImagePublisherRetain",
                "MqttImageTypesSelected"
            }, out int setCount, out var failures);

            return SettingsResult("MqttSettings", setCount, failures);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return InternalError(ex.Message);
        }
    }

    /// <summary>POST /api/groundstation/mqtt/test — publish an MQTT test message.</summary>
    [Route(HttpVerbs.Post, "/groundstation/mqtt/test")]
    public async Task<object> TestMqtt()
    {
        var cfg = GetConfig();
        if (cfg == null) return PluginNotInstalled();

        var (success, error) = await InvokeTestCommand(cfg, "MQTTTestCommand");
        return TestResult(success, error);
    }

    // ── IFTTT ──────────────────────────────────────────────────────────────────

    /// <summary>GET /api/groundstation/ifttt — read IFTTT webhook settings.</summary>
    [Route(HttpVerbs.Get, "/groundstation/ifttt")]
    public object GetIfttt()
    {
        try
        {
            var cfg = GetConfig();
            if (cfg == null) return PluginNotInstalled();

            return new
            {
                Success = true,
                Response = new
                {
                    WebhookKey = GetProp<string>(cfg, "IftttWebhookKey"),
                    FailureValue1 = GetProp<string>(cfg, "IftttFailureValue1"),
                    FailureValue2 = GetProp<string>(cfg, "IftttFailureValue2"),
                    FailureValue3 = GetProp<string>(cfg, "IftttFailureValue3"),
                },
                StatusCode = 200,
                Type = "IftttSettings"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return InternalError(ex.Message);
        }
    }

    /// <summary>
    /// PUT /api/groundstation/ifttt — update IFTTT webhook settings.
    /// Accepted keys: IftttWebhookKey, IftttFailureValue1, IftttFailureValue2,
    /// IftttFailureValue3.
    /// </summary>
    [Route(HttpVerbs.Put, "/groundstation/ifttt")]
    public async Task<object> SetIfttt()
    {
        try
        {
            var cfg = GetConfig();
            if (cfg == null) return PluginNotInstalled();

            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (body == null) return BadRequest("Empty request body");

            ApplySettings(cfg, body, new[]
            {
                "IftttWebhookKey", "IftttFailureValue1", "IftttFailureValue2", "IftttFailureValue3"
            }, out int setCount, out var failures);

            return SettingsResult("IftttSettings", setCount, failures);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return InternalError(ex.Message);
        }
    }

    /// <summary>POST /api/groundstation/ifttt/test — trigger an IFTTT webhook test event.</summary>
    [Route(HttpVerbs.Post, "/groundstation/ifttt/test")]
    public async Task<object> TestIfttt()
    {
        var cfg = GetConfig();
        if (cfg == null) return PluginNotInstalled();

        var (success, error) = await InvokeTestCommand(cfg, "IftttTestCommand");
        return TestResult(success, error);
    }

    // ── ntfy.sh ────────────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/groundstation/ntfysh — read ntfy.sh settings.
    /// Note: NtfyShPassword and NtfyShToken are intentionally excluded from the response.
    /// </summary>
    [Route(HttpVerbs.Get, "/groundstation/ntfysh")]
    public object GetNtfySh()
    {
        try
        {
            var cfg = GetConfig();
            if (cfg == null) return PluginNotInstalled();

            return new
            {
                Success = true,
                Response = new
                {
                    DefaultTopic = GetProp<string>(cfg, "NtfyShDefaultTopic"),
                    DefaultIcon = GetProp<string>(cfg, "NtfyShDefaultIcon"),
                    Url = GetProp<string>(cfg, "NtfyShUrl"),
                    User = GetProp<string>(cfg, "NtfyShUser"),
                    // NtfyShPassword and NtfyShToken omitted — supply via PUT only
                    FailureTitle = GetProp<string>(cfg, "NtfyShFailureTitle"),
                    FailureMessage = GetProp<string>(cfg, "NtfyShFailureMessage"),
                    FailureTags = GetProp<string>(cfg, "NtfyShFailureTags"),
                    FailurePriority = EnumName(GetPropRaw(cfg, "NtfyShFailurePriority")),
                    FailurePriorityValue = EnumValue(GetPropRaw(cfg, "NtfyShFailurePriority")),
                },
                StatusCode = 200,
                Type = "NtfyShSettings"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return InternalError(ex.Message);
        }
    }

    /// <summary>
    /// PUT /api/groundstation/ntfysh — update ntfy.sh settings.
    /// Accepted keys: NtfyShDefaultTopic, NtfyShDefaultIcon, NtfyShUrl, NtfyShUser,
    /// NtfyShPassword, NtfyShToken, NtfyShFailureTitle, NtfyShFailureMessage,
    /// NtfyShFailureTags, NtfyShFailurePriority.
    /// NtfyShFailurePriority may be supplied as the integer value or member name
    /// (Default, High, Low, Max, Min).
    /// </summary>
    [Route(HttpVerbs.Put, "/groundstation/ntfysh")]
    public async Task<object> SetNtfySh()
    {
        try
        {
            var cfg = GetConfig();
            if (cfg == null) return PluginNotInstalled();

            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (body == null) return BadRequest("Empty request body");

            ApplySettings(cfg, body, new[]
            {
                "NtfyShDefaultTopic", "NtfyShDefaultIcon", "NtfyShUrl",
                "NtfyShUser", "NtfyShPassword", "NtfyShToken",
                "NtfyShFailureTitle", "NtfyShFailureMessage",
                "NtfyShFailureTags", "NtfyShFailurePriority"
            }, out int setCount, out var failures);

            return SettingsResult("NtfyShSettings", setCount, failures);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return InternalError(ex.Message);
        }
    }

    // ── Shared helpers ─────────────────────────────────────────────────────────

    private static void ApplySettings(
        object cfg,
        Dictionary<string, object> body,
        string[] allowedProperties,
        out int setCount,
        out Dictionary<string, string> failures)
    {
        setCount = 0;
        failures = new Dictionary<string, string>();
        var allowedSet = new HashSet<string>(allowedProperties, StringComparer.Ordinal);

        foreach (var kvp in body)
        {
            if (!allowedSet.Contains(kvp.Key))
            {
                failures[kvp.Key] = "Property not allowed";
                continue;
            }

            var (ok, err) = SetProp(cfg, kvp.Key, kvp.Value);
            if (ok)
            {
                setCount++;
            }
            else
            {
                failures[kvp.Key] = err ?? "Failed to set property";
            }
        }
    }

    private static object SettingsResult(string type, int setCount, Dictionary<string, string> failures)
    {
        return new
        {
            Success = failures.Count == 0,
            Response = new { Updated = setCount },
            Failures = failures.Count > 0 ? (object)failures : null,
            StatusCode = 200,
            Type = type
        };
    }

    private static object TestResult(bool success, string error)
    {
        return new
        {
            Success = success,
            Error = error,
            StatusCode = success ? 200 : 500,
            Type = "TestResult"
        };
    }

    private static ApiResponse PluginNotInstalled()
    {
        return new ApiResponse
        {
            Success = false,
            Error = "Ground Station plugin is not installed or not loaded",
            StatusCode = 503,
            Type = "Error"
        };
    }

    private static ApiResponse BadRequest(string message)
    {
        return new ApiResponse
        {
            Success = false,
            Error = message,
            StatusCode = 400,
            Type = "Error"
        };
    }

    private static ApiResponse InternalError(string message)
    {
        return new ApiResponse
        {
            Success = false,
            Error = message,
            StatusCode = 500,
            Type = "Error"
        };
    }
}
