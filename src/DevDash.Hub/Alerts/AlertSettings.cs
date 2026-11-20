using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using DevDash.Hub.Data;
using DevDash.Hub.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DevDash.Hub.Alerts;

/// <summary>Whether a secret has a value, and where it comes from. Never the value.</summary>
/// <param name="SavedHere">Set on the Alerts page (kept in the database), not in configuration.</param>
public sealed record SecretState(bool Set, bool SavedHere)
{
    public string Text => !Set ? "not set" : SavedHere ? "set" : "set in configuration";
}

/// <summary>
/// The alert settings in force: <c>Hub:Alerts</c> from configuration (appsettings, environment), with
/// whatever was changed on the Alerts page laid over it. Those changes are rows in <c>AlertSettings</c>,
/// one per setting that differs from configuration, so a setting nobody touched keeps following
/// configuration. Secrets are stored encrypted with the hub's data-protection key and are write-only:
/// nothing here hands one to a page, and nothing logs one.
/// </summary>
public sealed class AlertSettings(
    IDbContextFactory<DevDashDbContext> dbFactory,
    IConfiguration configuration,
    IDataProtectionProvider protection,
    DashboardNotifier notifier,
    TimeProvider time,
    ILogger<AlertSettings> logger)
{
    public const string SectionName = HubOptions.SectionName + ":Alerts";

    public const string WebhookUrl = "Channels:Webhook:Url";
    public const string WebhookToken = "Channels:Webhook:Token";
    public const string NtfyTopic = "Channels:Ntfy:Topic";
    public const string NtfyToken = "Channels:Ntfy:Token";
    public const string HomeAssistantWebhookId = "Channels:HomeAssistant:WebhookId";

    /// <summary>The settings that are secrets. Everything else may be shown.</summary>
    public static readonly IReadOnlySet<string> SecretKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { WebhookUrl, WebhookToken, NtfyTopic, NtfyToken, HomeAssistantWebhookId };

    private readonly IDataProtector _protector = protection.CreateProtector("DevDash.Alerts.Secrets.v1");

    /// <summary>What configuration alone says, before anything saved on the page.</summary>
    public AlertOptions Configured() => (configuration.GetSection(SectionName).Get<AlertOptions>() ?? new AlertOptions()).Normalize();

    /// <summary>The settings in force.</summary>
    public async Task<AlertOptions> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return Effective(await db.AlertSettings.AsNoTracking().ToListAsync(ct));
    }

    /// <summary>For each secret: whether it has a value and where from.</summary>
    public async Task<IReadOnlyDictionary<string, SecretState>> SecretsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AlertSettings.AsNoTracking().ToListAsync(ct);
        var effective = Flatten(Effective(rows), secrets: true);
        var saved = rows.Where(r => r.IsSecret).Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return SecretKeys.ToDictionary(
            key => key,
            key => new SecretState(effective.GetValueOrDefault(key) is { Length: > 0 }, saved.Contains(key)),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>How many settings differ from configuration.</summary>
    public async Task<int> OverrideCountAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AlertSettings.CountAsync(ct);
    }

    /// <summary>
    /// Saves what the page holds. Only what differs from configuration is stored. <paramref name="secrets"/>
    /// has an entry for each secret the user typed (stored) or asked to remove (null or empty: the saved
    /// value goes, and configuration's shows through again); a secret with no entry is left as it is.
    /// </summary>
    public async Task SaveAsync(AlertOptions edited, IReadOnlyDictionary<string, string?> secrets, string by, CancellationToken ct = default)
    {
        var secretEdits = new Dictionary<string, string?>(secrets, StringComparer.OrdinalIgnoreCase);

        // A whole Home Assistant webhook URL typed into the address box: the id in it is a secret.
        var (haUrl, haId) = HomeAssistantChannelOptions.Split(edited.Channels.HomeAssistant.Url);
        edited.Channels.HomeAssistant.Url = haUrl;
        if (haId is not null && !secretEdits.ContainsKey(HomeAssistantWebhookId))
        {
            secretEdits[HomeAssistantWebhookId] = haId;
        }

        var wanted = Flatten(edited.Normalize(), secrets: false);
        var baseline = Flatten(Configured(), secrets: false);
        var now = time.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = (await db.AlertSettings.ToListAsync(ct)).ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

        void Set(string key, string value, bool secret)
        {
            if (!rows.TryGetValue(key, out var row))
            {
                row = new AlertSettingEntity { Key = key };
                rows[key] = row;
                db.AlertSettings.Add(row);
            }
            else if (!secret && row.Value == value)
            {
                return;
            }

            row.Value = secret ? _protector.Protect(value) : value;
            row.IsSecret = secret;
            row.UpdatedAt = now;
            row.UpdatedBy = by;
        }

        void Remove(string key)
        {
            if (rows.Remove(key, out var row))
            {
                db.AlertSettings.Remove(row);
            }
        }

        foreach (var (key, value) in wanted)
        {
            if (baseline.GetValueOrDefault(key) == value)
            {
                Remove(key);
            }
            else
            {
                Set(key, value, secret: false);
            }
        }

        foreach (var (key, value) in secretEdits.Where(e => SecretKeys.Contains(e.Key)))
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Remove(key);
            }
            else
            {
                Set(key, value.Trim(), secret: true);
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Alert settings saved by {User}: {Count} differ from configuration", by, rows.Count);
        notifier.Notify(DashboardChange.Alerts);
    }

    /// <summary>Forgets everything saved on the page, secrets included: configuration alone applies again.</summary>
    public async Task ResetAsync(string by, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var removed = await db.AlertSettings.ExecuteDeleteAsync(ct);
        logger.LogInformation("Alert settings reset to configuration by {User} ({Count} removed)", by, removed);
        notifier.Notify(DashboardChange.Alerts);
    }

    private AlertOptions Effective(IReadOnlyList<AlertSettingEntity> rows)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!row.IsSecret)
            {
                overrides[row.Key] = row.Value;
                continue;
            }

            try
            {
                overrides[row.Key] = _protector.Unprotect(row.Value);
            }
            catch (CryptographicException)
            {
                // The data-protection keys changed (a new volume, say). The value is lost, not leaked: set it again.
                logger.LogWarning("The saved alert secret {Key} could not be read with this hub's keys. Enter it again on the Alerts page.", row.Key);
            }
        }

        var merged = new ConfigurationBuilder()
            .AddConfiguration(configuration.GetSection(SectionName))
            .AddInMemoryCollection(overrides)
            .Build();
        return (merged.Get<AlertOptions>() ?? new AlertOptions()).Normalize();
    }

    /// <summary>
    /// Every setting as configuration would spell it: <c>Triggers:AtRisk:Days</c> → <c>3</c>. With
    /// <paramref name="secrets"/> false the secret settings are left out, so they can never be compared,
    /// stored or shown by accident.
    /// </summary>
    internal static Dictionary<string, string> Flatten(AlertOptions options, bool secrets)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Walk(options, "");
        return result;

        void Walk(object node, string prefix)
        {
            foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite))
            {
                var key = prefix + property.Name;
                var value = property.GetValue(node);
                if (property.PropertyType == typeof(string) || property.PropertyType == typeof(bool) || property.PropertyType == typeof(int))
                {
                    if (secrets || !SecretKeys.Contains(key))
                    {
                        result[key] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                    }
                }
                else if (value is not null)
                {
                    Walk(value, key + ":");
                }
            }
        }
    }
}
