using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using MimeKit;

namespace WhalePod.Infrastructure.Email;

public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddWhalePodEmail(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment,
        bool includeTransport)
    {
        var section = configuration.GetSection("Email");
        var smtpSection = section.GetSection("Smtp");
        var options = new EmailOptions
        {
            Provider = section["Provider"] ?? string.Empty,
            PublicBaseUrl = section["PublicBaseUrl"] ?? string.Empty,
            SiteName = string.IsNullOrWhiteSpace(section["SiteName"]) ? "WhalePod（鲸群）" : section["SiteName"]!,
            SuppressionHashSecret = section["SuppressionHashSecret"],
            UnsubscribeSecret = section["UnsubscribeSecret"],
            UnsubscribeTokenLifetimeDays = ReadInt(section, "UnsubscribeTokenLifetimeDays", 30),
            PollIntervalSeconds = ReadInt(section, "PollIntervalSeconds", 30),
            BatchSize = ReadInt(section, "BatchSize", 20),
            LeaseSeconds = ReadInt(section, "LeaseSeconds", 300),
            MaxAttempts = ReadInt(section, "MaxAttempts", 5),
            BackoffBaseSeconds = ReadInt(section, "BackoffBaseSeconds", 60),
            BackoffMaxSeconds = ReadInt(section, "BackoffMaxSeconds", 3600),
            Smtp = new SmtpEmailOptions
            {
                Host = smtpSection["Host"],
                Port = ReadInt(smtpSection, "Port", 587),
                Security = smtpSection["Security"] ?? "StartTls",
                UserName = smtpSection["UserName"],
                Password = smtpSection["Password"],
                FromAddress = smtpSection["FromAddress"],
                FromName = smtpSection["FromName"] ?? "WhalePod",
                TimeoutSeconds = ReadInt(smtpSection, "TimeoutSeconds", 30)
            }
        };

        var provider = (options.Provider ?? string.Empty).Trim();
        if (provider.Length == 0)
        {
            if (!isDevelopment)
            {
                throw new InvalidOperationException("Email:Provider is required outside Development.");
            }

            provider = EmailProviders.Logging;
        }

        if (!string.Equals(provider, EmailProviders.Logging, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(provider, EmailProviders.Smtp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Email:Provider must be Smtp or Logging.");
        }

        if (!isDevelopment
            && !string.Equals(provider, EmailProviders.Smtp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Email:Provider must be Smtp outside Development.");
        }

        ValidatePublicBaseUrl(options.PublicBaseUrl, isDevelopment);
        ValidateSecret(options.SuppressionHashSecret, "Email:SuppressionHashSecret");
        ValidateSecret(options.UnsubscribeSecret, "Email:UnsubscribeSecret");
        if (string.Equals(provider, EmailProviders.Smtp, StringComparison.OrdinalIgnoreCase)
            && (includeTransport || !isDevelopment))
        {
            ValidateSmtp(options.Smtp, isDevelopment);
        }

        services.AddSingleton<IOptions<EmailOptions>>(Options.Create(options));
        services.AddSingleton<EmailAddressHasher>();
        services.AddSingleton<EmailUnsubscribeTokenService>();
        services.AddSingleton<EmailTemplateRenderer>();
        services.AddScoped<EmailNotificationQueue>();
        services.AddScoped<EmailUnsubscribeService>();

        if (includeTransport)
        {
            if (string.Equals(provider, EmailProviders.Smtp, StringComparison.OrdinalIgnoreCase))
            {
                services.AddSingleton<IEmailSender, SmtpEmailSender>();
            }
            else
            {
                services.AddSingleton<IEmailSender, LoggingEmailSender>();
            }

            services.AddScoped<EmailDispatchService>();
        }

        return services;
    }

    private static void ValidatePublicBaseUrl(string publicBaseUrl, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(publicBaseUrl)
            || !Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Email:PublicBaseUrl must be an absolute HTTP(S) URL.");
        }

        if (!isDevelopment && uri.Scheme != "https")
        {
            throw new InvalidOperationException("Email:PublicBaseUrl must use HTTPS outside Development.");
        }
    }

    private static void ValidateSecret(string? secret, string key)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
        {
            throw new InvalidOperationException($"{key} must be at least 32 characters.");
        }
    }

    private static void ValidateSmtp(SmtpEmailOptions smtp, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(smtp.Host)
            || smtp.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(smtp.FromAddress)
            || !MailboxAddress.TryParse(smtp.FromAddress, out _))
        {
            throw new InvalidOperationException(
                "Email:Smtp requires Host, Port, and a valid FromAddress when Provider=Smtp.");
        }

        if (!isDevelopment)
        {
            var security = (smtp.Security ?? string.Empty).Trim().ToLowerInvariant();
            if (security is "" or "none" or "auto" or "starttlswhenavailable")
            {
                throw new InvalidOperationException(
                    "Email:Smtp:Security must be StartTls or SSL outside Development.");
            }
        }
    }

    private static int ReadInt(IConfigurationSection section, string key, int fallback)
    {
        return int.TryParse(section[key], out var value) ? value : fallback;
    }
}